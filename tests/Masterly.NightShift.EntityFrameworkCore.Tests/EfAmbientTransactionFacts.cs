using System.Diagnostics.CodeAnalysis;
using System.Transactions;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>
/// The storage writes through contexts and connections of its own, which never join a caller's ambient
/// transaction (<see cref="TransactionScope"/>): a job created inside one exists whether the caller's transaction
/// commits or rolls back, and a failing write of the storage never dooms the caller's transaction. Jobs staged
/// in the application's context are the exception, by design: they commit or roll back with that context.
/// </summary>
/// <remarks>
/// Business rows are written on SQL Server only: SQLite doesn't take part in a <see cref="TransactionScope"/>.
/// </remarks>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class EfAmbientTransactionFacts : IDisposable
{
    private readonly SqliteDatabase _database = new();

    public enum Mode
    {
        /// <summary>A context of the storage's own, configured for a provider.</summary>
        Dedicated,

        /// <summary>The application's contexts, whose model includes the storage's tables.</summary>
        Application,
    }

    public void Dispose() => _database.Dispose();

    public static void Run()
    {
    }

    [Theory]
    [InlineData(Mode.Dedicated)]
    [InlineData(Mode.Application)]
    public void Job_EnqueuedInsideATransactionScopeThatRollsBack_StillExists_OnSqlite(Mode mode)
    {
        EntityFrameworkCoreStorage storage = CreateSqliteStorage(mode);
        string? id;

        using (new TransactionScope())
        {
            id = new BackgroundJobClient(storage).Enqueue(() => Run());

            // No Complete()
        }

        AssertEnqueued(storage, id);
    }

    [Theory]
    [InlineData(Mode.Dedicated)]
    [InlineData(Mode.Application)]
    public void Job_EnqueuedInsideATransactionScopeThatRollsBack_StillExists_OnSqlServer(Mode mode)
    {
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(mode);
        string? id;

        using (new TransactionScope())
        {
            AddOrder("rolled-back");
            id = new BackgroundJobClient(storage).Enqueue(() => Run());

            // No Complete(): the business transaction rolls back
        }

        Assert.Equal(0, Orders("rolled-back"));
        AssertEnqueued(storage, id);
    }

    [Theory]
    [InlineData(Mode.Dedicated)]
    [InlineData(Mode.Application)]
    public async Task Job_EnqueuedInsideATransactionScopeThatFlowsWithAsyncCalls_StillExists_WhenItRollsBack_OnSqlServer(Mode mode)
    {
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(mode);
        BackgroundJobClient client = new(storage);
        string? id;
        string? onAnotherThread;

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Task.Yield();
            AddOrder("rolled-back");
            id = client.Enqueue(() => Run());

            // The ambient transaction flows to the thread pool too
            onAnotherThread = await Task.Run(
                () =>
                {
                    Assert.NotNull(Transaction.Current);
                    return client.Enqueue(() => Run());
                },
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, Orders("rolled-back"));
        AssertEnqueued(storage, id);
        AssertEnqueued(storage, onAnotherThread);
    }

    [Theory]
    [InlineData(Mode.Dedicated)]
    [InlineData(Mode.Application)]
    public void Writes_InsideATransactionScope_AreCommittedAtOnce_AndKeptWhenItRollsBack_OnSqlServer(Mode mode)
    {
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(mode);
        string id;

        using (new TransactionScope())
        {
            using (IStorageConnection connection = storage.GetConnection())
            {
                id = connection.CreateExpiredJob(Job.FromExpression(() => Run()), new Dictionary<string, string?> { ["A"] = "1" }, JobClock.UtcNow, TimeSpan.FromHours(1))!;

                // On a connection that holds a distributed lock, as the state changer writes
                using (connection.AcquireDistributedLock($"job:{id}:state-lock", TimeSpan.FromSeconds(5)))
                using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
                {
                    transaction.SetJobState(id, new EnqueuedState());
                    transaction.AddToQueue("default", id);
                    transaction.IncrementCounter("in-scope");
                    transaction.Commit();
                }

                connection.SetJobParameter(id, "B", "2");
                connection.SetRangeInHash("hash", [new("field", "value")]);
            }

            // Committed already, while the scope is still open: a reader outside it sees them, without waiting
            using (new TransactionScope(TransactionScopeOption.Suppress))
            using (IStorageConnection reader = storage.GetConnection())
            {
                Assert.Equal(EnqueuedState.StateName, reader.GetStateData(id)?.Name);
            }

            // No Complete()
        }

        using IStorageConnection after = storage.GetConnection();
        Assert.Equal(EnqueuedState.StateName, after.GetStateData(id)?.Name);
        Assert.Equal("2", after.GetJobParameter(id, "B"));
        Assert.Equal("value", ((JobStorageConnection)after).GetValueFromHash("hash", "field"));
        Assert.Equal(1, ((JobStorageConnection)after).GetCounter("in-scope"));
        Assert.Equal(1, storage.GetMonitoringApi().EnqueuedCount("default"));

        // No lock was left behind by the rolled-back scope, and none is held by it
        using DbContext context = storage.CreateContext();
        Assert.Empty(context.Set<LockRow>().AsNoTracking());
    }

    [Theory]
    [InlineData(Mode.Dedicated)]
    [InlineData(Mode.Application)]
    public void BusinessData_WrittenInATransactionScope_IsCommitted_WhenAWriteOfTheStorageFailsInsideTheScope_OnSqlServer(Mode mode)
    {
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(mode);

        using (TransactionScope scope = new())
        {
            AddOrder("business-data");

            // A list item can't be null: the storage's transaction fails, and is rolled back by the storage
            Assert.Throws<DbUpdateException>(() => EfTransaction.Apply(storage, [new InsertToListOperation("list", null!)]));

            AddOrder("after-the-failure");
            scope.Complete();
        }

        Assert.Equal(1, Orders("business-data"));
        Assert.Equal(1, Orders("after-the-failure"));
    }

    [Fact]
    public void ExpirationAndCounters_RunInsideATransactionScope_CommitWhateverItDoes_OnSqlServer()
    {
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(Mode.Dedicated);
        Write(storage, static x =>
        {
            x.IncrementCounter("counter");
            x.AddToSet("old", "a");
            ((JobStorageTransaction)x).ExpireSet("old", TimeSpan.FromSeconds(-1));
        });

        using (new TransactionScope())
        {
            new EfExpirationManager(storage).RemoveExpired(CancellationToken.None);
            Assert.Equal(1, new EfCountersAggregator(storage).AggregateBatch());
        }

        using DbContext context = storage.CreateContext();
        Assert.Empty(context.Set<SetRow>().AsNoTracking());
        Assert.Empty(context.Set<CounterRow>().AsNoTracking());
        Assert.Equal(1, context.Set<AggregatedCounterRow>().AsNoTracking().Single().Value);
    }

    [Fact]
    public void StagedJob_RollsBack_WithTheApplicationsTransactionScope_OnSqlServer()
    {
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(Mode.Application);
        string? id;

        using (new TransactionScope())
        {
            using ShopContext context = CreateShopContext();
            context.Orders.Add(new Order { Name = "staged" });
            id = context.NightShift(storage).Enqueue(() => Run());
            context.SaveChanges();

            // No Complete()
        }

        Assert.Equal(0, Orders("staged"));
        Assert.NotNull(id);
        using IStorageConnection connection = storage.GetConnection();
        Assert.Null(connection.GetStateData(id)?.Name);
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("default"));

        // The id was reserved outside the scope, and expires by itself
        Assert.NotNull(storage.GetMonitoringApi().JobDetails(id)?.ExpireAt);
        using DbContext reader = storage.CreateContext();
        Assert.Empty(reader.Set<LockRow>().AsNoTracking());
    }

    [Fact]
    public void StagedJob_CommitsWithTheApplicationsTransactionScope_OnSqlServer()
    {
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(Mode.Application);
        string? id;

        using (TransactionScope scope = new())
        {
            using ShopContext context = CreateShopContext();
            context.Orders.Add(new Order { Name = "staged" });
            id = context.NightShift(storage).Enqueue(() => Run());
            context.SaveChanges();
            scope.Complete();
        }

        Assert.Equal(1, Orders("staged"));
        AssertEnqueued(storage, id);
    }

    [Fact]
    public void StagedJob_InsideATransactionScope_IsNotPartOfItBeforeTheContextSaves_OnSqlServer()
    {
        // The staging writes go to the context, so a scope that completes without SaveChanges stores no job
        EntityFrameworkCoreStorage storage = CreateSqlServerStorage(Mode.Application);
        string? id;

        using (TransactionScope scope = new())
        {
            using ShopContext context = CreateShopContext();
            id = context.NightShift(storage).Enqueue(() => Run());
            scope.Complete();
        }

        using IStorageConnection connection = storage.GetConnection();
        Assert.Null(connection.GetStateData(id!)?.Name);
    }

    private static void AssertEnqueued(EntityFrameworkCoreStorage storage, string? id)
    {
        Assert.NotNull(id);
        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(EnqueuedState.StateName, connection.GetStateData(id)?.Name);
        Assert.Contains(id, storage.GetMonitoringApi().EnqueuedJobs("default", 0, 100).Select(static x => x.Key));
    }

    private static void Write(EntityFrameworkCoreStorage storage, Action<IWriteOnlyTransaction> write)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    private EntityFrameworkCoreStorage CreateSqliteStorage(Mode mode)
    {
        EntityFrameworkCoreStorage dedicated = _database.CreateStorage();
        if (mode == Mode.Dedicated)
        {
            return dedicated;
        }

        string connectionString = _database.ConnectionString;
        return new EntityFrameworkCoreStorage(() => new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(connectionString).Options, null));
    }

    private static EntityFrameworkCoreStorage CreateSqlServerStorage(Mode mode)
    {
        // A database of its own, where the application's table sits next to the storage's
        string connectionString = SqlServerDatabase.ConnectionString("Ambient");
        using (ShopContext setup = CreateShopContext())
        {
            setup.Database.EnsureCreated();
            setup.Orders.ExecuteDelete();
        }

        EntityFrameworkCoreStorage storage = mode == Mode.Dedicated
            ? new EntityFrameworkCoreStorage(o => o.UseSqlServer(connectionString), new EntityFrameworkCoreStorageOptions { PrepareSchemaIfNecessary = false })
            : new EntityFrameworkCoreStorage(CreateShopContext);

        // Starts empty: every row of every table, locks included
        storage.Purge(new StoragePurgeOptions { KeepAuditLog = false, KeepDrainMode = false, KeepRecurringJobs = false, KeepServers = false });
        using DbContext context = storage.CreateContext();
        context.Set<LockRow>().ExecuteDelete();
        return storage;
    }

    private static ShopContext CreateShopContext()
    {
        return new ShopContext(
            new DbContextOptionsBuilder<ShopContext>().UseSqlServer(SqlServerDatabase.ConnectionString("Ambient")).Options,
            NightShiftModelBuilderExtensions.DefaultSchema);
    }

    // The application's own write, on its own context, inside the caller's scope
    private static void AddOrder(string name)
    {
        using ShopContext context = CreateShopContext();
        context.Orders.Add(new Order { Name = name });
        context.SaveChanges();
    }

    private static int Orders(string name)
    {
        using (new TransactionScope(TransactionScopeOption.Suppress))
        {
            using ShopContext context = CreateShopContext();
            return context.Orders.Count(x => x.Name == name);
        }
    }

    /// <summary>An application's context: its own table, and the storage's tables in its model.</summary>
    private sealed class ShopContext(DbContextOptions<ShopContext> options, string? schema) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyNightShiftModel(schema);
        }
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
