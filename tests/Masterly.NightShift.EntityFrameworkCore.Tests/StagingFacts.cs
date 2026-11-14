using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Conformance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>Jobs staged in an application's context commit or roll back with it.</summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class StagingFacts : IDisposable
{
    private readonly SqliteDatabase _database = new();
    private readonly EntityFrameworkCoreStorage _storage;

    public StagingFacts()
    {
        using (ShopContext context = CreateContext())
        {
            context.Database.EnsureCreated();
        }

        _storage = new EntityFrameworkCoreStorage(CreateContext);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void StagedJob_IsEnqueued_WhenTheContextSaves()
    {
        string? id;
        using (ShopContext context = CreateContext())
        {
            context.Orders.Add(new Order { Name = "Book" });
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("staged"));

            Assert.NotNull(id);
            Assert.Null(State(id));
            Assert.Equal(0, _storage.GetMonitoringApi().EnqueuedCount("default"));

            context.SaveChanges();
        }

        Assert.Equal(EnqueuedState.StateName, State(id));
        Assert.Equal(id, Assert.Single(_storage.GetMonitoringApi().EnqueuedJobs("default", 0, 10)).Key);
    }

    [Fact]
    public void StagedJob_IsNeverEnqueued_WhenTheContextIsNotSaved()
    {
        string? id;
        using (ShopContext context = CreateContext())
        {
            context.Orders.Add(new Order { Name = "Book" });
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("discarded"));
        }

        Assert.Null(State(id!));
        Assert.Equal(0, _storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.NotNull(_storage.GetMonitoringApi().JobDetails(id!)?.ExpireAt);
    }

    [Fact]
    public void StagedJob_RollsBack_WithTheApplicationsTransaction()
    {
        // On SQL Server: SQLite has one writer at a time, so the id can't be reserved while the application's
        // transaction holds the write lock
        string connectionString = SqlServerDatabase.ConnectionString("Staging");
        using (ShopContext setup = CreateSqlServerContext(connectionString))
        {
            setup.Database.EnsureDeleted();
            setup.Database.EnsureCreated();
        }

        EntityFrameworkCoreStorage storage = new(() => CreateSqlServerContext(connectionString));
        string? id;
        using (ShopContext context = CreateSqlServerContext(connectionString))
        {
            using IDbContextTransaction transaction = context.Database.BeginTransaction();
            context.Orders.Add(new Order { Name = "Book" });
            id = context.NightShift(storage).Enqueue(() => ConformanceJobs.Record("rolled-back"));
            context.SaveChanges();
            Assert.Equal(EnqueuedState.StateName, context.Set<JobRow>().Single(x => x.Id == long.Parse(id!)).StateName);
            transaction.Rollback();
        }

        using ShopContext reader = CreateSqlServerContext(connectionString);
        Assert.Empty(reader.Orders);
        using IStorageConnection connection = storage.GetConnection();
        Assert.Null(connection.GetStateData(id!));
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("default"));
    }

    [Fact]
    public void StagedSchedule_IsScheduled_WhenTheContextSaves()
    {
        using ShopContext context = CreateContext();

        string? id = context.NightShift(_storage).Schedule(() => ConformanceJobs.Record("later"), TimeSpan.FromHours(1));
        context.SaveChanges();

        Assert.Equal(ScheduledState.StateName, State(id!));
        Assert.Equal(1, _storage.GetMonitoringApi().ScheduledCount());
    }

    [Fact]
    public async Task StagedJob_RunsOnAServer_OnceSaved()
    {
        string key = Guid.NewGuid().ToString("N");
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, _storage);

        string? id;
        using (ShopContext context = CreateContext())
        {
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record(key));
            context.SaveChanges();
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (State(id!) != SucceededState.StateName)
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Job {id} didn't succeed.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, ConformanceJobs.Runs(key));
    }

    [Fact]
    public void Staging_OnSqlite_InsideTheContextsTransaction_FailsAtOnce()
    {
        using ShopContext context = CreateContext();
        using IDbContextTransaction transaction = context.Database.BeginTransaction();
        context.Orders.Add(new Order { Name = "Book" });
        context.SaveChanges();
        Stopwatch stopwatch = Stopwatch.StartNew();

        BackgroundJobClientException exception = Assert.Throws<BackgroundJobClientException>(() => context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("blocked")));

        Assert.Contains("BeginTransaction", Assert.IsType<InvalidOperationException>(exception.InnerException).Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Staging waited for SQLite's busy timeout.");
    }

    [Fact]
    public void Staging_OnSqlite_BeforeTheContextsTransaction_CommitsWithIt()
    {
        string? id;
        using (ShopContext context = CreateContext())
        {
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("before"));
            using IDbContextTransaction transaction = context.Database.BeginTransaction();
            context.Orders.Add(new Order { Name = "Book" });
            context.SaveChanges();
            transaction.Commit();
        }

        Assert.Equal(EnqueuedState.StateName, State(id!));
    }

    [Fact]
    public void StagedJob_NotSaved_KeepsItsReservedIdForAtLeastADay()
    {
        string? id;
        using (ShopContext context = CreateContext())
        {
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("unsaved"));
        }

        Assert.True(_storage.GetMonitoringApi().JobDetails(id!)?.ExpireAt > JobClock.UtcNow.AddHours(23));
    }

    [Fact]
    public void SaveChanges_WakesTheWorkers_OnlyWhenItSavesStagedQueueEntries()
    {
        using ShopContext context = CreateContext();
        IBackgroundJobClient client = context.NightShift(_storage);

        Task beforeOrder = _storage.Enqueued;
        context.Orders.Add(new Order { Name = "Book" });
        context.SaveChanges();
        Assert.False(beforeOrder.IsCompleted);

        client.Schedule(() => ConformanceJobs.Record("scheduled"), TimeSpan.FromHours(1));
        context.SaveChanges();
        Assert.False(beforeOrder.IsCompleted);

        client.Enqueue(() => ConformanceJobs.Record("enqueued"));
        context.SaveChanges();
        Assert.True(beforeOrder.IsCompleted);
    }

    [Fact]
    public void NightShift_Refuses_AContextWithoutTheStoragesTables()
    {
        using EmptyContext context = new(new DbContextOptionsBuilder<EmptyContext>().UseSqlite(_database.ConnectionString).Options);

        Assert.Throws<InvalidOperationException>(() => context.NightShift(_storage));
    }

    [Fact]
    public void Storage_Refuses_AContextFactoryWhoseModelLacksTheTables()
    {
        Assert.Throws<InvalidOperationException>(() => new EntityFrameworkCoreStorage(() => new EmptyContext(new DbContextOptionsBuilder<EmptyContext>().UseSqlite(_database.ConnectionString).Options)));
    }

    [AutomaticRetry(Attempts = 0)]
    [PreventDuplicates]
    public static void Once(string name) => _ = name;

    [Fact]
    public void Requeue_StagedInTheContextThatStagedTheJob_AfterItSucceeded_EnqueuesIt()
    {
        // The context still tracks the job's row from the save before; a worker ran the job since
        using ShopContext context = CreateContext();
        string id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("a"))!;
        context.SaveChanges();
        Assert.True(new BackgroundJobClient(_storage).ChangeState(id, new SucceededState(null, 1, 1), EnqueuedState.StateName));

        Assert.True(context.NightShift(_storage).Requeue(id));
        context.SaveChanges();

        Assert.Equal(EnqueuedState.StateName, State(id));
        Assert.Null(_storage.GetMonitoringApi().JobDetails(id)?.ExpireAt);
    }

    [Fact]
    public void SetValue_StagedInTheContextThatStagedIt_AfterAnotherProcessRemovedIt_IsAddedAgain()
    {
        using ShopContext context = CreateContext();
        using (IStorageConnection connection = context.NightShift(_storage) is IBackgroundJobClientV2 client ? client.Storage.GetConnection() : throw new InvalidOperationException())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet("set", "a");
            transaction.SetRangeInHash("hash", [new("field", "1")]);
            transaction.Commit();
        }

        context.SaveChanges();
        using (IStorageConnection other = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = other.CreateWriteTransaction())
        {
            transaction.RemoveFromSet("set", "a");
            transaction.RemoveHash("hash");
            transaction.Commit();
        }

        using (IStorageConnection connection = ((IBackgroundJobClientV2)context.NightShift(_storage)).Storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet("set", "a", 2);
            transaction.SetRangeInHash("hash", [new("field", "2")]);
            transaction.Commit();
        }

        context.SaveChanges();

        using IStorageConnection reader = _storage.GetConnection();
        Assert.Equal(["a"], reader.GetAllItemsFromSet("set"));
        Assert.Equal("2", ((JobStorageConnection)reader).GetValueFromHash("hash", "field"));
    }

    [Fact]
    public void Duplicates_StagedBeforeTheSameSaveChanges_OnlyOneIsCreated()
    {
        string name = Guid.NewGuid().ToString("N");
        using ShopContext context = CreateContext();
        IBackgroundJobClient client = context.NightShift(_storage);

        string? first = client.Enqueue(() => Once(name));
        string? second = client.Enqueue(() => Once(name));
        context.SaveChanges();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, _storage.GetMonitoringApi().EnqueuedCount("default"));
    }

    [Fact]
    public void Duplicate_OfAJobStagedButNeverSaved_IsCreated_OnceTheReservationTimedOut()
    {
        string name = Guid.NewGuid().ToString("N");
        TimeProvider original = JobClock.TimeProvider;
        ShiftedClock clock = new();
        JobClock.TimeProvider = clock;

        try
        {
            using (ShopContext context = CreateContext())
            {
                Assert.NotNull(context.NightShift(_storage).Enqueue(() => Once(name)));

                // Never saved
            }

            Assert.Null(new BackgroundJobClient(_storage).Enqueue(() => Once(name)));

            clock.Offset = TimeSpan.FromMinutes(3);
            Assert.NotNull(new BackgroundJobClient(_storage).Enqueue(() => Once(name)));
        }
        finally
        {
            JobClock.TimeProvider = original;
        }
    }

    [Fact]
    public void Continuation_OfACommittedJob_SavedAfterTheParentFinished_StartsWhenSaved()
    {
        TimeProvider original = JobClock.TimeProvider;
        ShiftedClock clock = new();
        JobClock.TimeProvider = clock;

        try
        {
            BackgroundJobClient plain = new(_storage);
            string parent = plain.Enqueue(() => ConformanceJobs.Record("parent"))!;

            using ShopContext context = CreateContext();
            string continuation = context.NightShift(_storage).ContinueJobWith(parent, () => ConformanceJobs.Record("continuation"))!;

            // The application saves 16 minutes later (the reservation lasts at least a day); meanwhile the parent ran
            clock.Offset = TimeSpan.FromMinutes(16);
            Stopwatch watch = Stopwatch.StartNew();
            Assert.True(plain.ChangeState(parent, new SucceededState(null, 1, 1), EnqueuedState.StateName));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"The parent's state change took {watch.Elapsed}.");
            context.SaveChanges();

            Assert.Equal(SucceededState.StateName, State(parent));
            Assert.Equal(EnqueuedState.StateName, State(continuation));
        }
        finally
        {
            JobClock.TimeProvider = original;
        }
    }

    [Fact]
    public void Continuation_OfACommittedJob_IsRegisteredWhenTheContextSaves()
    {
        string parent = new BackgroundJobClient(_storage).Enqueue(() => ConformanceJobs.Record("parent"))!;
        using ShopContext context = CreateContext();

        string continuation = context.NightShift(_storage).ContinueJobWith(parent, () => ConformanceJobs.Record("continuation"))!;
        using (IStorageConnection connection = _storage.GetConnection())
        {
            Assert.Null(connection.GetJobParameter(parent, "Continuations"));
        }

        context.SaveChanges();

        Assert.Equal(AwaitingState.StateName, State(continuation));
        using IStorageConnection reader = _storage.GetConnection();
        Assert.Contains(continuation, reader.GetJobParameter(parent, "Continuations"), StringComparison.Ordinal);
    }

    [Fact]
    public void Parent_OfAStagedContinuationNeverSaved_FinishesWithoutWaiting()
    {
        BackgroundJobClient plain = new(_storage);
        string parent = plain.Enqueue(() => ConformanceJobs.Record("parent"))!;
        using (ShopContext context = CreateContext())
        {
            context.NightShift(_storage).ContinueJobWith(parent, () => ConformanceJobs.Record("continuation"));
        }

        Stopwatch watch = Stopwatch.StartNew();
        Assert.True(plain.ChangeState(parent, new SucceededState(null, 1, 1), EnqueuedState.StateName));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"The parent's state change took {watch.Elapsed}.");
    }

    [Fact]
    public void Continuation_CommittedButNotRegistered_WhenItsProcessStopped_IsRegisteredByAServer()
    {
        // What a process that stopped between the commit and the registration leaves: the continuation waits,
        // its parent doesn't know of it, and the set says it is to be registered
        BackgroundJobClient plain = new(_storage);
        string parent = plain.Enqueue(() => ConformanceJobs.Record("parent"))!;
        string continuation = plain.ContinueJobWith(parent, () => ConformanceJobs.Record("continuation"))!;
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            connection.SetJobParameter(parent, "Continuations", null);
            transaction.AddToSet(StagedContinuations.Key, continuation, JobHelper.ToTimestamp(JobClock.UtcNow.AddMinutes(-5)));
            transaction.Commit();
        }

        Assert.True(plain.ChangeState(parent, new SucceededState(null, 1, 1), EnqueuedState.StateName));
        Assert.Equal(AwaitingState.StateName, State(continuation));

        using CancellationTokenSource stopped = new();
        stopped.Cancel();
        BackgroundProcessContext context = new("server", _storage, new Dictionary<string, object>(), Guid.NewGuid(), stopped.Token, CancellationToken.None, CancellationToken.None);

        // It does its work, then its wait ends at once: the server is stopping
        Assert.ThrowsAny<OperationCanceledException>(() => new StagedContinuationsProcess(_storage).Execute(context));

        Assert.Equal(EnqueuedState.StateName, State(continuation));
        using IStorageConnection reader = _storage.GetConnection();
        Assert.Empty(reader.GetAllItemsFromSet(StagedContinuations.Key));
    }

    [Fact]
    public void Join_OfCommittedJobs_IsRegisteredWhenTheContextSaves_AndStartsIfTheyFinished()
    {
        BackgroundJobClient plain = new(_storage);
        string first = plain.Enqueue(() => ConformanceJobs.Record("first"))!;
        string second = plain.Enqueue(() => ConformanceJobs.Record("second"))!;

        using ShopContext context = CreateContext();
        string join = context.NightShift(_storage).ContinueJobWithJoin([first, second], FanInMode.All, () => ConformanceJobs.Record("join"))!;

        Assert.True(plain.ChangeState(first, new SucceededState(null, 1, 1), EnqueuedState.StateName));
        Assert.True(plain.ChangeState(second, new SucceededState(null, 1, 1), EnqueuedState.StateName));
        context.SaveChanges();

        Assert.Equal(EnqueuedState.StateName, State(join));
    }

    private string? State(string id)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetStateData(id)?.Name;
    }

    private ShopContext CreateContext() => new(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_database.ConnectionString).Options);

    private static ShopContext CreateSqlServerContext(string connectionString)
    {
        return new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlServer(connectionString).Options, NightShiftModelBuilderExtensions.DefaultSchema);
    }

    private sealed class ShopContext(DbContextOptions<ShopContext> options, string? schema = null) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyNightShiftModel(schema);
        }
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);

    /// <summary>The system clock, moved by a time the test sets.</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
