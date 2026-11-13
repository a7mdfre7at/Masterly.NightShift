using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>
/// Staging a job means taking part in the application's transaction. While that transaction is open, what the
/// client filters read or write about rows the context has staged runs in it, and never waits for it; workers
/// fetch the jobs other callers committed without waiting for it either. On SQL Server, with
/// <c>READ_COMMITTED_SNAPSHOT</c> off (locking reads, as in databases made by scripts) and on (as in databases
/// EF Core creates).
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class EfStagingTransactionFacts : IDisposable
{
    // Short, so a test that waits for the application's transaction fails in seconds instead of minutes
    private const int CommandTimeoutSeconds = 5;
    private static readonly TimeSpan NoWait = TimeSpan.FromSeconds(4);

    private readonly List<(string Database, bool Original)> _restore = [];

    public void Dispose()
    {
        foreach ((string database, bool original) in _restore)
        {
            SetReadCommittedSnapshot(database, original);
        }
    }

    public static void Run(string name) => _ = name;

    [PreventDuplicates]
    public static void Once(string name) => _ = name;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Worker_FetchesACommittedJob_WhileAnApplicationTransactionHoldsAStagedJob(bool snapshot, bool committedFirst)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        BackgroundJobClient client = new(storage);
        string? committed = committedFirst ? client.Enqueue(() => Run("committed")) : null;

        using AppContext app = CreateContext(snapshot);
        using IDbContextTransaction transaction = app.Database.BeginTransaction();
        app.NightShift(storage).Enqueue(() => Run("held"));
        app.SaveChanges();

        committed ??= client.Enqueue(() => Run("committed"));

        string? fetched = null;
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                using IStorageConnection connection = storage.GetConnection();
                using CancellationTokenSource giveUp = new(TimeSpan.FromSeconds(20));
                using IFetchedJob job = connection.FetchNextJob(["default"], giveUp.Token);
                fetched = job.JobId;
                job.RemoveFromQueue();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        Stopwatch watch = Stopwatch.StartNew();
        worker.Start();

        bool finishedWhileHeld = worker.Join(NoWait);
        TimeSpan elapsed = watch.Elapsed;
        transaction.Rollback();
        worker.Join();

        Assert.True(finishedWhileHeld, $"The worker waited {elapsed} for the application's transaction.");
        Assert.Null(failure);
        Assert.Equal(committed, fetched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Continuation_OfAJobStagedAndSavedInTheSameOpenTransaction_IsStagedAtOnce_AndCommitsWithIt(bool snapshot)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string? parent;
        string? continuation;

        using (AppContext app = CreateContext(snapshot))
        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            parent = app.NightShift(storage).Enqueue(() => Run("parent"));
            app.SaveChanges();

            Stopwatch watch = Stopwatch.StartNew();
            continuation = app.NightShift(storage).ContinueJobWith(parent!, () => Run("continuation"));
            app.SaveChanges();
            Assert.True(watch.Elapsed < NoWait, $"Staging the continuation took {watch.Elapsed}.");

            transaction.Commit();
        }

        Assert.Equal(EnqueuedState.StateName, State(storage, parent));
        Assert.Equal(AwaitingState.StateName, State(storage, continuation));
        Assert.Contains(continuation!, Parameter(storage, parent!, "Continuations"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Continuation_OfAJobStagedAndSavedInTheSameOpenTransaction_RollsBackWithIt(bool snapshot)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string? parent;
        string? continuation;

        using (AppContext app = CreateContext(snapshot))
        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            parent = app.NightShift(storage).Enqueue(() => Run("parent"));
            app.SaveChanges();
            continuation = app.NightShift(storage).ContinueJobWith(parent!, () => Run("continuation"));
            app.SaveChanges();

            transaction.Rollback();
        }

        Assert.Null(State(storage, parent));
        Assert.Null(State(storage, continuation));
        Assert.Null(Parameter(storage, parent!, "Continuations"));
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("default"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Continuation_OfAJobStagedAndSavedInTheSameTransactionScope_IsStagedAtOnce_AndCommitsWithIt(bool snapshot)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string? parent;
        string? continuation;

        using (TransactionScope scope = new(TransactionScopeOption.Required, TimeSpan.FromSeconds(20)))
        {
            using AppContext app = CreateContext(snapshot);
            parent = app.NightShift(storage).Enqueue(() => Run("parent"));
            app.SaveChanges();

            Stopwatch watch = Stopwatch.StartNew();
            continuation = app.NightShift(storage).ContinueJobWith(parent!, () => Run("continuation"));
            app.SaveChanges();
            Assert.True(watch.Elapsed < NoWait, $"Staging the continuation took {watch.Elapsed}.");

            scope.Complete();
        }

        Assert.Equal(EnqueuedState.StateName, State(storage, parent));
        Assert.Equal(AwaitingState.StateName, State(storage, continuation));
        Assert.Contains(continuation!, Parameter(storage, parent!, "Continuations"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Continuation_OfAJobStagedAndSavedInATransactionScopeThatRollsBack_IsGoneWithIt(bool snapshot)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string? parent;
        string? continuation;

        using (new TransactionScope(TransactionScopeOption.Required, TimeSpan.FromSeconds(20)))
        {
            using AppContext app = CreateContext(snapshot);
            parent = app.NightShift(storage).Enqueue(() => Run("parent"));
            app.SaveChanges();
            continuation = app.NightShift(storage).ContinueJobWith(parent!, () => Run("continuation"));
            app.SaveChanges();

            // No Complete()
        }

        Assert.Null(State(storage, parent));
        Assert.Null(State(storage, continuation));
        Assert.Null(Parameter(storage, parent!, "Continuations"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoContinuations_OfAJobStagedAndSavedInTheSameOpenTransaction_AreBothRegistered(bool snapshot)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string? parent;
        string? first;
        string? second;

        using (AppContext app = CreateContext(snapshot))
        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            parent = app.NightShift(storage).Enqueue(() => Run("parent"));
            app.SaveChanges();
            first = app.NightShift(storage).ContinueJobWith(parent!, () => Run("first"));
            second = app.NightShift(storage).ContinueJobWith(parent!, () => Run("second"));
            app.SaveChanges();
            transaction.Commit();
        }

        string continuations = Parameter(storage, parent!, "Continuations")!;
        Assert.Contains(first!, continuations, StringComparison.Ordinal);
        Assert.Contains(second!, continuations, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Continuation_OfACommittedJob_InsideAnOpenTransaction_IsRegisteredOnItsParentAtOnce(bool snapshot)
    {
        // As before: the parent is everybody's, so the registration is committed at once, under the parent's lock
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string? parent = new BackgroundJobClient(storage).Enqueue(() => Run("parent"));
        string? continuation;

        using (AppContext app = CreateContext(snapshot))
        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            continuation = app.NightShift(storage).ContinueJobWith(parent!, () => Run("continuation"));
            app.SaveChanges();

            Assert.Contains(continuation!, Parameter(storage, parent!, "Continuations"), StringComparison.Ordinal);
            transaction.Rollback();
        }

        Assert.Null(State(storage, continuation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Join_OfJobsStagedAndSavedInTheSameOpenTransaction_IsStagedAtOnce_AndCommitsWithThem(bool snapshot)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string? first;
        string? second;
        string? join;

        using (AppContext app = CreateContext(snapshot))
        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            first = app.NightShift(storage).Enqueue(() => Run("first"));
            second = app.NightShift(storage).Enqueue(() => Run("second"));
            app.SaveChanges();

            Stopwatch watch = Stopwatch.StartNew();
            join = app.NightShift(storage).ContinueJobWithJoin([first!, second!], FanInMode.All, () => Run("join"));
            app.SaveChanges();
            Assert.True(watch.Elapsed < NoWait, $"Staging the join took {watch.Elapsed}.");

            transaction.Commit();
        }

        Assert.Equal(AwaitingState.StateName, State(storage, join));
        using IStorageConnection connection = storage.GetConnection();
        Assert.Contains(join!, FanInContinuationsSupportAttribute.ReadChildren(connection, first!));
        Assert.Contains(join!, FanInContinuationsSupportAttribute.ReadChildren(connection, second!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Duplicate_OfAJobStagedAndSavedInTheSameOpenTransaction_IsRefusedAtOnce(bool snapshot)
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);
        string name = Guid.NewGuid().ToString("N");
        string? first;
        string? second;

        using (AppContext app = CreateContext(snapshot))
        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            first = app.NightShift(storage).Enqueue(() => Once(name));
            app.SaveChanges();

            Stopwatch watch = Stopwatch.StartNew();
            second = app.NightShift(storage).Enqueue(() => Once(name));
            app.SaveChanges();
            Assert.True(watch.Elapsed < NoWait, $"Staging the duplicate took {watch.Elapsed}.");

            transaction.Commit();
        }

        Assert.Equal(EnqueuedState.StateName, State(storage, first));
        Assert.Null(second);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Chain_StagedInAContext_IsRefusedAtOnce_BeforeAnythingOfItIsCreated(bool snapshot, bool inTransaction)
    {
        // The chain's client reads back the steps it created, which a context only writes when it is saved
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot);

        using AppContext app = CreateContext(snapshot);
        using IDbContextTransaction? transaction = inTransaction ? app.Database.BeginTransaction() : null;
        Stopwatch watch = Stopwatch.StartNew();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(app.NightShift(storage)));

        Assert.True(watch.Elapsed < NoWait, $"Refusing the chain took {watch.Elapsed}.");
        Assert.Contains("BackgroundJobClient(storage)", exception.Message, StringComparison.Ordinal);
        Assert.Empty(app.ChangeTracker.Entries());
        transaction?.Commit();
        using DbContext reader = storage.CreateContext();
        Assert.Empty(reader.Set<JobRow>().AsNoTracking());
    }

    [Fact]
    public void SaveChanges_InAnOpenTransaction_WakesTheWorkers_WhenTheTransactionCommits()
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot: true);

        using AppContext app = CreateContext(snapshot: true);
        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            Task beforeSave = storage.Enqueued;
            app.NightShift(storage).Enqueue(() => Run("held"));
            app.SaveChanges();
            Assert.False(beforeSave.IsCompleted);

            transaction.Commit();
            Assert.True(beforeSave.IsCompleted);
        }

        using (IDbContextTransaction transaction = app.Database.BeginTransaction())
        {
            Task beforeSave = storage.Enqueued;
            app.NightShift(storage).Enqueue(() => Run("rolled-back"));
            app.SaveChanges();
            transaction.Rollback();
            Assert.False(beforeSave.IsCompleted);
        }
    }

    [Fact]
    public void SaveChanges_InATransactionScope_WakesTheWorkers_WhenTheScopeCommits()
    {
        EntityFrameworkCoreStorage storage = CreateStorage(snapshot: true);
        Task beforeSave = storage.Enqueued;

        using (TransactionScope scope = new())
        {
            using AppContext app = CreateContext(snapshot: true);
            app.NightShift(storage).Enqueue(() => Run("held"));
            app.SaveChanges();
            Assert.False(beforeSave.IsCompleted);
            scope.Complete();
        }

        Assert.True(beforeSave.IsCompleted);

        Task beforeRollback = storage.Enqueued;
        using (new TransactionScope())
        {
            using AppContext app = CreateContext(snapshot: true);
            app.NightShift(storage).Enqueue(() => Run("rolled-back"));
            app.SaveChanges();
        }

        Assert.False(beforeRollback.IsCompleted);
    }

    private static string? State(EntityFrameworkCoreStorage storage, string? jobId)
    {
        Assert.NotNull(jobId);
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetStateData(jobId)?.Name;
    }

    private static string? Parameter(EntityFrameworkCoreStorage storage, string jobId, string name)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetJobParameter(jobId, name);
    }

    private EntityFrameworkCoreStorage CreateStorage(bool snapshot)
    {
        string database = SqlServerDatabase.DatabaseName(Name(snapshot));
        using (AppContext setup = CreateContext(snapshot))
        {
            setup.Database.EnsureCreated();
        }

        bool original = SetReadCommittedSnapshot(database, snapshot);
        if (original != snapshot && !_restore.Exists(x => x.Database == database))
        {
            _restore.Add((database, original));
        }

        EntityFrameworkCoreStorage storage = new(() => CreateContext(snapshot));
        storage.Purge(new StoragePurgeOptions { KeepAuditLog = false, KeepDrainMode = false, KeepRecurringJobs = false, KeepServers = false });
        using DbContext context = storage.CreateContext();
        context.Set<LockRow>().ExecuteDelete();
        return storage;
    }

    private static string Name(bool snapshot) => snapshot ? "StagingSnapshot" : "StagingLocking";

    private static AppContext CreateContext(bool snapshot)
    {
        return new AppContext(new DbContextOptionsBuilder<AppContext>()
            .UseSqlServer(SqlServerDatabase.ConnectionString(Name(snapshot)), static o => o.CommandTimeout(CommandTimeoutSeconds))
            .Options);
    }

    /// <summary>Sets <c>READ_COMMITTED_SNAPSHOT</c> of a test database, and returns what it was.</summary>
    private static bool SetReadCommittedSnapshot(string database, bool on)
    {
        using SqlConnection connection = new(new SqlConnectionStringBuilder(SqlServerDatabase.ConnectionString(Name(on))) { InitialCatalog = "master" }.ConnectionString);
        connection.Open();

        bool current;
        using (SqlCommand read = new("select is_read_committed_snapshot_on from sys.databases where name = @name", connection))
        {
            read.Parameters.AddWithValue("@name", database);
            current = (bool)read.ExecuteScalar();
        }

        if (current != on)
        {
            SqlConnection.ClearAllPools();
            using SqlCommand alter = new($"alter database [{database}] set read_committed_snapshot {(on ? "on" : "off")} with rollback immediate", connection);
            alter.ExecuteNonQuery();
            SqlConnection.ClearAllPools();
        }

        return current;
    }

    /// <summary>An application's context, whose model includes the storage's tables.</summary>
    private sealed class AppContext(DbContextOptions<AppContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyNightShiftModel(NightShiftModelBuilderExtensions.DefaultSchema);
        }
    }
}
