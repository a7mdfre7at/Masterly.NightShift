using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

public sealed class EfStorageFacts : IDisposable
{
    private readonly SqliteDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Ctor_RefusesBadOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new EntityFrameworkCoreStorage((Action<DbContextOptionsBuilder>)null!));
        Assert.Throws<ArgumentException>(() => _database.CreateStorage(new EntityFrameworkCoreStorageOptions { QueuePollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => _database.CreateStorage(new EntityFrameworkCoreStorageOptions { InvisibilityTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DistributedLockLifetime = TimeSpan.FromMilliseconds(1) }));
    }

    [Fact]
    public void Lock_WhoseHandleWasNotDisposed_IsReleased_WithItsConnection()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DistributedLockLifetime = TimeSpan.FromSeconds(1) });
        IStorageConnection first = storage.GetConnection();
        IDisposable forgotten = first.AcquireDistributedLock("resource", TimeSpan.Zero);

        first.Dispose();

        using IStorageConnection second = storage.GetConnection();
        IDisposable taken = second.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5));

        // The forgotten handle no longer holds anything, so it releases nothing
        forgotten.Dispose();
        using IStorageConnection third = storage.GetConnection();
        Assert.Throws<DistributedLockTimeoutException>(() => third.AcquireDistributedLock("resource", TimeSpan.Zero));
        taken.Dispose();
    }

    [Fact]
    public void PrepareSchema_CreatesTheTables_InADatabaseThatHasOthers()
    {
        using (SqliteConnection connection = new(_database.ConnectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "create table Existing (Id integer primary key)";
            command.ExecuteNonQuery();
        }

        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        using IStorageConnection storageConnection = storage.GetConnection();
        Assert.NotNull(storageConnection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromHours(1)));
        Assert.NotNull(_database.CreateStorage());
    }

    [Fact]
    public void ExpirationManager_DeletesExpiredJobs_WithTheirStatesParametersAndQueueEntries_AndExpiredKeys()
    {
        ExpirationManager_DeletesExpiredJobs_WithTheirStatesParametersAndQueueEntries(foreignKeys: true);
    }

    [Fact]
    public void ExpirationManager_DeletesTheStatesAndParametersOfExpiredJobs_WhereTheDatabaseDoesNotEnforceForeignKeys()
    {
        ExpirationManager_DeletesExpiredJobs_WithTheirStatesParametersAndQueueEntries(foreignKeys: false);
    }

    private void ExpirationManager_DeletesExpiredJobs_WithTheirStatesParametersAndQueueEntries(bool foreignKeys)
    {
        _ = _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(
            o => o.UseSqlite($"{_database.ConnectionString};Foreign Keys={foreignKeys}"),
            new EntityFrameworkCoreStorageOptions { Schema = null });
        string expired;
        string kept;
        using (IStorageConnection connection = storage.GetConnection())
        {
            expired = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?> { ["Culture"] = "en" }, JobClock.UtcNow, TimeSpan.FromHours(1))!;
            kept = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("y")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromHours(1))!;
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetJobState(expired, new EnqueuedState());
            transaction.AddToQueue("default", expired);
            transaction.ExpireJob(expired, TimeSpan.FromSeconds(-1));
            transaction.AddToSet("old", "a");
            ((JobStorageTransaction)transaction).ExpireSet("old", TimeSpan.FromSeconds(-1));
            transaction.Commit();
        }

        new EfExpirationManager(storage).RemoveExpired(CancellationToken.None);

        using IStorageConnection reader = storage.GetConnection();
        Assert.Null(reader.GetJobData(expired));
        Assert.NotNull(reader.GetJobData(kept));
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Empty(reader.GetAllItemsFromSet("old"));
        using DbContext context = storage.CreateContext();
        Assert.Empty(context.Set<StateRow>());
        Assert.Empty(context.Set<JobParameterRow>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpirationManager_KeepsAJob_RequeuedAfterItWasFoundExpired(bool sqlServer)
    {
        EntityFrameworkCoreStorage storage = sqlServer ? SqlServerDatabase.CreateStorage() : _database.CreateStorage();
        string id = CreateJob(storage);
        Commit(storage, x =>
        {
            x.SetJobState(id, new SucceededState(null, 1, 1));
            x.ExpireJob(id, TimeSpan.FromSeconds(-1));
        });

        // A requeue from the dashboard, between the read of the expired ids and the deletes
        BeforeFirstDelete requeue = new(() => Commit(storage, x =>
        {
            x.SetJobState(id, new EnqueuedState());
            x.PersistJob(id);
            x.AddToQueue("default", id);
        }));
        EntityFrameworkCoreStorage expiring = sqlServer
            ? new(o => o.UseSqlServer(SqlServerDatabase.ConnectionString("Storage")).AddInterceptors(requeue))
            : new(o => o.UseSqlite(_database.ConnectionString).AddInterceptors(requeue), new EntityFrameworkCoreStorageOptions { Schema = null });

        new EfExpirationManager(expiring).RemoveExpired(CancellationToken.None);

        Assert.True(requeue.Ran);
        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(EnqueuedState.StateName, reader.GetStateData(id)?.Name);
        Assert.Equal(1, storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Equal(2, storage.GetMonitoringApi().JobDetails(id)?.History.Count);
    }

    [Fact]
    public void CountersAggregator_MergesIncrements_WithoutChangingTheTotals()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                transaction.IncrementCounter("stats:succeeded", TimeSpan.FromDays(1));
            }

            transaction.DecrementCounter("stats:succeeded");
            transaction.Commit();
        }

        int merged = new EfCountersAggregator(storage).AggregateBatch();

        Assert.Equal(6, merged);
        using DbContext context = storage.CreateContext();
        Assert.Empty(context.Set<CounterRow>());
        AggregatedCounterRow counter = Assert.Single(context.Set<AggregatedCounterRow>());
        Assert.Equal(4, counter.Value);
        Assert.NotNull(counter.ExpireAt);
        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(4, ((JobStorageConnection)reader).GetCounter("stats:succeeded"));
    }

    [Fact]
    public void ConcurrentTransactions_AddingTheSameSetValue_AllCommit()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        Parallel.For(0, 8, i =>
        {
            using IStorageConnection connection = storage.GetConnection();
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.AddToSet("recurring-jobs", "nightly", i);
            transaction.Commit();
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(["nightly"], reader.GetAllItemsFromSet("recurring-jobs"));
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.NoTracking, true)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution, true)]
    [InlineData(QueryTrackingBehavior.TrackAll, false)]
    public void Writes_ThroughApplicationContexts_ThatDoNotTrackQueriesOrDetectChanges_UpdateExistingRows(QueryTrackingBehavior tracking, bool detectChanges)
    {
        _ = _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(() => new ApplicationContext(
            new DbContextOptionsBuilder<ApplicationContext>().UseSqlite(_database.ConnectionString).UseQueryTrackingBehavior(tracking).Options,
            detectChanges));
        string id;
        using (IStorageConnection connection = storage.GetConnection())
        {
            id = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromHours(1))!;
            connection.SetRangeInHash("hash", [new("field", "1")]);
            connection.SetRangeInHash("hash", [new("field", "2")]);
            connection.SetJobParameter(id, "RetryCount", "1");
            connection.SetJobParameter(id, "RetryCount", "2");
            connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
            connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 2 });
        }

        Commit(storage, x => x.AddToSet("schedule", id, 100));
        Commit(storage, x => x.AddToSet("schedule", id, 200));
        EfCountersAggregator aggregator = new(storage);
        Commit(storage, static x => x.IncrementCounter("counter"));
        aggregator.AggregateBatch();
        Commit(storage, static x => x.IncrementCounter("counter"));
        aggregator.AggregateBatch();

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal("2", ((JobStorageConnection)reader).GetValueFromHash("hash", "field"));
        Assert.Equal("2", reader.GetJobParameter(id, "RetryCount"));
        Assert.Equal(id, reader.GetFirstByLowestScoreFromSet("schedule", 150, 250));
        Assert.Equal(2, ((JobStorageConnection)reader).GetCounter("counter"));
        Assert.Equal(2, Assert.Single(storage.GetMonitoringApi().Servers()).WorkersCount);
    }

    [Fact]
    public void Fetch_WhoseClaimSucceeded_ButWhoseReplyWasLost_ClaimsTheEntryWhenRetried()
    {
        LostReplies lostReplies = new(static x => x.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && x.CommandText.Contains("FetchToken", StringComparison.Ordinal));
        EntityFrameworkCoreStorage storage = CreateRetryingStorage(lostReplies);
        string id = CreateJob(storage);
        Commit(storage, x => x.AddToQueue("default", id));
        lostReplies.Armed = true;

        using IStorageConnection connection = storage.GetConnection();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using IFetchedJob fetched = connection.FetchNextJob(["default"], timeout.Token);

        Assert.Equal(id, fetched.JobId);
        Assert.Equal(1, lostReplies.Lost);
        fetched.RemoveFromQueue();
        Assert.Equal(0, storage.GetMonitoringApi().FetchedCount("default"));
    }

    [Fact]
    public void Transaction_WhoseCommitSucceeded_ButReportedAnError_IsNotAppliedTwice()
    {
        LostCommits lostCommits = new();
        EntityFrameworkCoreStorage storage = CreateRetryingStorage(lostCommits);
        string id = CreateJob(storage);
        lostCommits.Armed = true;

        Commit(storage, x =>
        {
            x.AddToQueue("default", id);
            x.InsertToList("list", "item");
            x.IncrementCounter("counter");
            x.SetJobState(id, new EnqueuedState());
        });

        Assert.Equal(1, lostCommits.Lost);
        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(1, storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Equal(1, ((JobStorageConnection)reader).GetListCount("list"));
        Assert.Equal(1, ((JobStorageConnection)reader).GetCounter("counter"));
        Assert.Equal(1, storage.GetMonitoringApi().JobDetails(id)?.History.Count);
    }

    [Fact]
    public void Transaction_AddingASetValueAgain_AfterRemovingTheSet_KeepsIt()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        Commit(storage, static x =>
        {
            x.AddToSet("set", "value");
            ((JobStorageTransaction)x).RemoveSet("set");
            x.AddToSet("set", "value");
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(["value"], reader.GetAllItemsFromSet("set"));
    }

    [Fact]
    public void Transaction_SettingAHashFieldAgain_AfterRemovingTheHash_KeepsTheLastValue()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        Commit(storage, static x =>
        {
            x.SetRangeInHash("hash", [new("field", "1")]);
            x.RemoveHash("hash");
            x.SetRangeInHash("hash", [new("field", "2")]);
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal("2", ((JobStorageConnection)reader).GetValueFromHash("hash", "field"));
    }

    [Fact]
    public void Transaction_AddingAHashField_AfterPersistingTheHash_DoesNotGiveItTheOldExpiry()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        Commit(storage, static x =>
        {
            x.SetRangeInHash("hash", [new("field", "1")]);
            ((JobStorageTransaction)x).ExpireHash("hash", TimeSpan.FromHours(1));
        });

        Commit(storage, static x =>
        {
            x.SetRangeInHash("hash", [new("field", "2")]);
            ((JobStorageTransaction)x).PersistHash("hash");
            x.SetRangeInHash("hash", [new("other", "3")]);
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(TimeSpan.FromSeconds(-1), ((JobStorageConnection)reader).GetHashTtl("hash"));
    }

    [Fact]
    public void StagedWrites_RemovingAndAddingAgain_KeepTheLastWrite()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet("set", "a", 1);
            transaction.SetRangeInHash("hash", [new("field", "old")]);
            transaction.InsertToList("list", "1");
            transaction.Commit();
        }

        using (DbContext context = storage.CreateContext())
        {
            EfOperationWriter writer = new(context, immediate: false);
            writer.Write(new Storage.Shared.RemoveFromSetOperation("set", "a"));
            writer.Write(new Storage.Shared.AddToSetOperation("set", "a", 7));
            writer.Write(new Storage.Shared.RemoveHashOperation("hash"));
            writer.Write(new Storage.Shared.SetRangeInHashOperation("hash", [new("field", "new")]));
            writer.Write(new Storage.Shared.InsertToListOperation("list", "2"));
            writer.Write(new Storage.Shared.InsertToListOperation("list", "3"));
            writer.Write(new Storage.Shared.TrimListOperation("list", 0, 1));
            context.SaveChanges();
        }

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(["a"], ((JobStorageConnection)reader).GetFirstByLowestScoreFromSet("set", 7, 7, 10));
        Assert.Equal("new", ((JobStorageConnection)reader).GetValueFromHash("hash", "field"));
        Assert.Equal(["3", "2"], ((JobStorageConnection)reader).GetAllItemsFromList("list"));
    }

    [Fact]
    public void UseEntityFrameworkCoreStorage_MakesItTheCurrentStorage()
    {
        Mock<IGlobalConfiguration> configuration = new();

        IGlobalConfiguration<EntityFrameworkCoreStorage> result = configuration.Object.UseEntityFrameworkCoreStorage(
            o => o.UseSqlite(_database.ConnectionString),
            new EntityFrameworkCoreStorageOptions { Schema = null, QueuePollInterval = TimeSpan.FromSeconds(3) });

        Assert.Equal(TimeSpan.FromSeconds(3), result.Entry.Options.QueuePollInterval);
        Assert.Same(result.Entry, JobStorage.Current);
    }

    [Theory]
    [InlineData(0, 9, 1499, 1490)]
    [InlineData(5, 9, 1494, 1490)]
    [InlineData(1490, 3000, 9, 0)]
    [InlineData(-3, 2, 1499, 1497)]
    public void TrimList_OnALongList_KeepsOnlyTheRange(int keepFrom, int keepTo, int newestKept, int oldestKept)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        FillList(storage, "long", 1500);

        Commit(storage, x => x.TrimList("long", keepFrom, keepTo));

        using IStorageConnection reader = storage.GetConnection();
        List<string> items = ((JobStorageConnection)reader).GetAllItemsFromList("long");
        Assert.Equal(newestKept - oldestKept + 1, items.Count);
        Assert.Equal(newestKept.ToString(CultureInfo.InvariantCulture), items[0]);
        Assert.Equal(oldestKept.ToString(CultureInfo.InvariantCulture), items[^1]);
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(1500, 2000)]
    [InlineData(-3, -1)]
    public void TrimList_ToAnEmptyRange_RemovesEveryItem(int keepFrom, int keepTo)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        FillList(storage, "long", 1500);
        FillList(storage, "other", 3);

        Commit(storage, x => x.TrimList("long", keepFrom, keepTo));

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(0, ((JobStorageConnection)reader).GetListCount("long"));
        Assert.Equal(3, ((JobStorageConnection)reader).GetListCount("other"));
    }

    [Fact]
    public void StagedTrimList_OnALongList_RemovesSavedItems_WithoutLoadingTheirValues()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        FillList(storage, "long", 1500);

        using (DbContext context = storage.CreateContext())
        {
            EfOperationWriter writer = new(context, immediate: false);
            writer.Write(new Storage.Shared.InsertToListOperation("long", "staged"));
            writer.Write(new Storage.Shared.TrimListOperation("long", 0, 9));

            Assert.All(
                context.ChangeTracker.Entries<ListRow>().Where(static x => x.State != EntityState.Added),
                static x => Assert.Null(x.Entity.Value));
            context.SaveChanges();
        }

        using IStorageConnection reader = storage.GetConnection();
        List<string> items = ((JobStorageConnection)reader).GetAllItemsFromList("long");
        Assert.Equal(10, items.Count);
        Assert.Equal("staged", items[0]);
        Assert.Equal("1491", items[^1]);
    }

    [Fact]
    public void GetCounter_AddsIncrementsNotMergedYet_ToTheMergedValue()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        Commit(storage, static x =>
        {
            for (int i = 0; i < 3; i++)
            {
                x.IncrementCounter("stats:succeeded");
            }
        });
        new EfCountersAggregator(storage).AggregateBatch();
        Commit(storage, static x =>
        {
            x.IncrementCounter("stats:succeeded");
            x.IncrementCounter("stats:succeeded");
            x.IncrementCounter("stats:failed");
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(5, ((JobStorageConnection)reader).GetCounter("stats:succeeded"));
        Assert.Equal(1, ((JobStorageConnection)reader).GetCounter("stats:failed"));
        Assert.Equal(0, ((JobStorageConnection)reader).GetCounter("stats:deleted"));
        Assert.Equal(5, storage.GetMonitoringApi().GetStatistics().Succeeded);
    }

    [Fact]
    public void CountersAggregator_MergesIntoExistingCounters_AndAddsNewOnes()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        EfCountersAggregator aggregator = new(storage);
        Commit(storage, static x => x.IncrementCounter("a"));
        aggregator.AggregateBatch();
        Commit(storage, static x =>
        {
            x.IncrementCounter("a");
            x.IncrementCounter("b");
            x.DecrementCounter("c");
        });

        Assert.Equal(3, aggregator.AggregateBatch());

        using DbContext context = storage.CreateContext();
        Assert.Equal(
            [("a", 2L), ("b", 1L), ("c", -1L)],
            context.Set<AggregatedCounterRow>().AsNoTracking().OrderBy(static x => x.Key).AsEnumerable().Select(static x => (x.Key, x.Value)));
    }

    [Fact]
    public void ExpirationManager_DeletesMoreExpiredKeysThanABatch()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        Commit(storage, static x =>
        {
            ((JobStorageTransaction)x).AddRangeToSet("old", [.. Enumerable.Range(0, 2500).Select(static i => i.ToString(CultureInfo.InvariantCulture))]);
            ((JobStorageTransaction)x).ExpireSet("old", TimeSpan.FromSeconds(-1));
            x.AddToSet("kept", "a");
        });

        new EfExpirationManager(storage).RemoveExpired(CancellationToken.None);

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(0, ((JobStorageConnection)reader).GetSetCount("old"));
        Assert.Equal(1, ((JobStorageConnection)reader).GetSetCount("kept"));
    }

    [Fact]
    public void SetJobState_OfAJobThatDoesNotExist_WritesNothing()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        Commit(storage, static x =>
        {
            x.SetJobState("424242", new EnqueuedState());
            x.ExpireJob("424242", TimeSpan.FromHours(1));
        });

        using DbContext context = storage.CreateContext();
        Assert.Empty(context.Set<StateRow>());
    }

    [Fact]
    public void Keys_DifferingInCase_AreDistinct_OnSqlite()
    {
        // The database's collation decides: SQLite compares as written, SQL Server and MySQL ignore case by default
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        Commit(storage, static x =>
        {
            x.AddToSet("Key", "a");
            x.AddToSet("key", "b");
            x.SetRangeInHash("Hash", [new("Field", "1")]);
            x.SetRangeInHash("Hash", [new("field", "2")]);
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(["a"], reader.GetAllItemsFromSet("Key"));
        Assert.Equal(["b"], reader.GetAllItemsFromSet("key"));
        Assert.Empty(reader.GetAllItemsFromSet("KEY"));
        Assert.Equal(2, reader.GetAllEntriesFromHash("Hash")?.Count);
    }

    [Fact]
    public void TryTake_ReturnsFalse_WhenTheLeaseIsHeld_AndSurfacesOtherFailures()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        using IStorageConnection connection = storage.GetConnection();
        ILeaseStore leases = (ILeaseStore)connection;

        Assert.True(leases.TryTake("resource", "first", JobClock.UtcNow.AddMinutes(1)));
        Assert.False(leases.TryTake("resource", "second", JobClock.UtcNow.AddMinutes(1)));

        // The owner can't be null: the insert fails for another reason than a held lease
        Assert.Throws<DbUpdateException>(() => leases.TryTake("other", null!, JobClock.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void Apply_GivesUp_AfterItsAttempts_WithTheLastError()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        // A list item can't be null: every attempt fails the same way
        Assert.Throws<DbUpdateException>(() => EfTransaction.Apply(storage, [new Storage.Shared.InsertToListOperation("list", null!)]));

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(0, ((JobStorageConnection)reader).GetListCount("list"));
    }

    [Fact]
    public async Task PrepareSchema_ByStoragesStartingAtOnce_CreatesTheTablesOnce()
    {
        using Barrier start = new(4);
        Task<EntityFrameworkCoreStorage>[] starting = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return _database.CreateStorage();
            },
            TestContext.Current.CancellationToken))];

        EntityFrameworkCoreStorage[] storages = await Task.WhenAll(starting);

        Assert.Equal(4, storages.Length);
        using DbContext context = storages[0].CreateContext();
        Assert.Equal([SchemaVersionRow.Current], context.Set<SchemaVersionRow>().Select(static x => x.Version));
    }

    [Theory]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", null)]
    [InlineData("MySql.EntityFrameworkCore", null)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", NightShiftModelBuilderExtensions.DefaultSchema)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", NightShiftModelBuilderExtensions.DefaultSchema)]
    [InlineData("Oracle.EntityFrameworkCore", NightShiftModelBuilderExtensions.DefaultSchema)]
    public void DefaultSchema_IsNone_OnMySqlAndMariaDb(string provider, string? schema)
    {
        Assert.Equal(schema, EntityFrameworkCoreStorage.DefaultSchema(provider));
    }

    [Fact]
    public void Schema_IsTheDefault_UntilSet()
    {
        EntityFrameworkCoreStorageOptions options = new();
        Assert.Equal(NightShiftModelBuilderExtensions.DefaultSchema, options.Schema);
        Assert.False(options.SchemaSet);

        // Set to the default, it is kept on every provider
        options.Schema = NightShiftModelBuilderExtensions.DefaultSchema;
        Assert.True(options.SchemaSet);
    }

    [Fact]
    public void Schema_OfADedicatedContext_IsTheOptionsOne_WhenSet()
    {
        Assert.Null(_database.CreateStorage().Schema);
    }

    [Fact]
    public void Schema_OfADedicatedContext_OnSqlServer_IsNightShift_ByDefault()
    {
        Assert.Equal(NightShiftModelBuilderExtensions.DefaultSchema, SqlServerDatabase.CreateStorage().Schema);
    }

    [Fact]
    public void PrepareSchema_CreatesTheVersionsTableFirst()
    {
        CreatedTables created = new();

        _ = new EntityFrameworkCoreStorage(o => o.UseSqlite(_database.ConnectionString).AddInterceptors(created), new EntityFrameworkCoreStorageOptions { Schema = null });

        Assert.Equal(12, created.Tables.Count);
        Assert.Contains("\"SchemaVersion\"", created.Tables[0], StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareSchema_CompletesTheTables_OfACreatorThatStoppedHalfWay()
    {
        // As on a database whose DDL isn't transactional, when the process creating the tables stopped
        LeaveTablesHalfCreated();

        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { SchemaCompletionTimeout = TimeSpan.FromMilliseconds(200) });

        using (DbContext context = storage.CreateContext())
        {
            Assert.Equal([SchemaVersionRow.Current], context.Set<SchemaVersionRow>().Select(static x => x.Version));
        }

        string id = CreateJob(storage);
        Commit(storage, x =>
        {
            x.SetJobState(id, new EnqueuedState());
            x.AddToQueue("default", id);
            ((JobStorageTransaction)x).SetJobParameter(id, "name", "value");
            x.AddToSet("set", "value");
            x.InsertToList("list", "value");
            x.SetRangeInHash("hash", [new("field", "value")]);
            x.IncrementCounter("counter");
        });
        Assert.Equal(1, new EfCountersAggregator(storage).AggregateBatch());
        using IStorageConnection connection = storage.GetConnection();
        connection.AnnounceServer("server", new ServerContext());
        using (connection.AcquireDistributedLock("lock", TimeSpan.Zero))
        {
            Assert.Equal(1, storage.GetMonitoringApi().EnqueuedCount("default"));
        }
    }

    [Fact]
    public async Task PrepareSchema_WaitsForTheTablesAnotherProcessIsCreating()
    {
        LeaveTablesHalfCreated();
        Stopwatch stopwatch = Stopwatch.StartNew();
        Task<EntityFrameworkCoreStorage> waiting = Task.Run(() => _database.CreateStorage(), TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        // The other process completes the tables and records their version
        _ = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { SchemaCompletionTimeout = TimeSpan.Zero });

        EntityFrameworkCoreStorage storage = await waiting;
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"The storage waited {stopwatch.Elapsed}.");
        using DbContext context = storage.CreateContext();
        Assert.Equal([SchemaVersionRow.Current], context.Set<SchemaVersionRow>().Select(static x => x.Version));
    }

    [Fact]
    public void PrepareSchema_RefusesTables_WithoutTheVersionsTable()
    {
        _ = _database.CreateStorage();
        ExecuteSql("drop table \"SchemaVersion\"");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _database.CreateStorage());

        Assert.Contains("Couldn't read the schema version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareSchema_SurfacesTheDatabasesError_WhenTheTablesCanNotBeCreated()
    {
        using (SqliteConnection connection = new(_database.ConnectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "create table Existing (Id integer primary key)";
            command.ExecuteNonQuery();
        }

        SqliteException exception = Assert.Throws<SqliteException>(() => new EntityFrameworkCoreStorage(
            o => o.UseSqlite($"{_database.ConnectionString};Mode=ReadOnly"),
            new EntityFrameworkCoreStorageOptions { Schema = null }));

        Assert.Equal(8, exception.SqliteErrorCode); // SQLITE_READONLY, not the missing table the probe saw
    }

    [Fact]
    public void PrepareSchema_RefusesTablesOfAnotherSchemaVersion()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        using (DbContext context = storage.CreateContext())
        {
            context.Set<SchemaVersionRow>().ExecuteUpdate(static s => s.SetProperty(static x => x.Version, SchemaVersionRow.Current + 1));
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _database.CreateStorage());

        Assert.Contains("newer version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyStorages_ShareTheInternalServiceProvider()
    {
        // EF Core warns after its 20th internal service provider; turned into an error, it would fail a storage
        for (int i = 0; i < 25; i++)
        {
            Assert.NotNull(new EntityFrameworkCoreStorage(
                o => o.UseSqlite(_database.ConnectionString).ConfigureWarnings(static w => w.Throw(CoreEventId.ManyServiceProvidersCreatedWarning)),
                new EntityFrameworkCoreStorageOptions { Schema = $"s{i}", PrepareSchemaIfNecessary = i == 0 }));
        }
    }

    [Theory]
    [InlineData(-3, -1)]
    [InlineData(-5, -3)]
    [InlineData(2, 1)]
    [InlineData(5, 9)]
    public void Ranges_EndingBeforeTheFirstItem_OrEmpty_ReadNothing(int startingFrom, int endingAt)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        Commit(storage, x =>
        {
            for (int i = 0; i < 3; i++)
            {
                x.InsertToList("list", i.ToString(CultureInfo.InvariantCulture));
                x.AddToSet("set", i.ToString(CultureInfo.InvariantCulture), i);
            }
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Empty(((JobStorageConnection)reader).GetRangeFromList("list", startingFrom, endingAt));
        Assert.Empty(((JobStorageConnection)reader).GetRangeFromSet("set", startingFrom, endingAt));
    }

    [Fact]
    public void Ranges_StartingBeforeTheFirstItem_ReadFromIt_AndEndingPastTheLast_ReadToIt()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        Commit(storage, x =>
        {
            for (int i = 0; i < 3; i++)
            {
                x.InsertToList("list", i.ToString(CultureInfo.InvariantCulture));
                x.AddToSet("set", i.ToString(CultureInfo.InvariantCulture), i);
            }
        });

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(["2", "1"], ((JobStorageConnection)reader).GetRangeFromList("list", -3, 1));
        Assert.Equal(["0", "1"], ((JobStorageConnection)reader).GetRangeFromSet("set", -3, 1));
        Assert.Equal(["1", "0"], ((JobStorageConnection)reader).GetRangeFromList("list", 1, int.MaxValue));
        Assert.Equal(["1", "2"], ((JobStorageConnection)reader).GetRangeFromSet("set", 1, int.MaxValue));
    }

    private static void Commit(EntityFrameworkCoreStorage storage, Action<IWriteOnlyTransaction> write)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    // Items "0" (oldest) to "count - 1" (newest)
    private static void FillList(EntityFrameworkCoreStorage storage, string key, int count)
    {
        Commit(storage, x =>
        {
            for (int i = 0; i < count; i++)
            {
                x.InsertToList(key, i.ToString(CultureInfo.InvariantCulture));
            }
        });
    }

    // The version's table, empty, and the job table: the others are gone
    private void LeaveTablesHalfCreated()
    {
        _ = _database.CreateStorage();
        ExecuteSql("delete from \"SchemaVersion\"");
        foreach (string table in new[] { "State", "JobParameter", "JobQueue", "Set", "List", "Hash", "Counter", "AggregatedCounter", "Server", "Lock" })
        {
            ExecuteSql($"drop table \"{table}\"");
        }
    }

    private void ExecuteSql(string sql)
    {
        using SqliteConnection connection = new(_database.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateJob(EntityFrameworkCoreStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromHours(1))!; // Never canceled: no filter runs here
    }

    // A storage whose contexts retry what fails with a TransientException, as EnableRetryOnFailure does
    private EntityFrameworkCoreStorage CreateRetryingStorage(IInterceptor interceptor)
    {
        return new EntityFrameworkCoreStorage(
            o => o.UseSqlite(_database.ConnectionString, static x => x.ExecutionStrategy(static d => new RetryingStrategy(d))).AddInterceptors(interceptor),
            new EntityFrameworkCoreStorageOptions { Schema = null });
    }

    private sealed class TransientException() : Exception("The reply was lost.");

    private sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies) : ExecutionStrategy(dependencies, 3, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientException;
    }

    /// <summary>Once armed, the first command that matches runs, then reports a failure, as a lost reply does.</summary>
    private sealed class LostReplies(Func<DbCommand, bool> matches) : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public int Lost { get; private set; }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            if (Armed && Lost == 0 && matches(command))
            {
                Lost++;
                throw new TransientException();
            }

            return result;
        }
    }

    /// <summary>The CREATE TABLE statements, in the order they run.</summary>
    private sealed class CreatedTables : DbCommandInterceptor
    {
        public List<string> Tables { get; } = [];

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (command.CommandText.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                Tables.Add(command.CommandText);
            }

            return result;
        }
    }

    /// <summary>Runs an action before the first DELETE statement, once.</summary>
    private sealed class BeforeFirstDelete(Action action) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (!Ran && command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Ran = true;
                action();
            }

            return result;
        }
    }

    /// <summary>Once armed, the first commit commits, then reports a failure, as a lost reply does.</summary>
    private sealed class LostCommits : DbTransactionInterceptor
    {
        public bool Armed { get; set; }

        public int Lost { get; private set; }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            if (Armed && Lost == 0)
            {
                Lost++;
                throw new TransientException();
            }
        }
    }

    /// <summary>An application's context, configured as the application likes: the storage must work with it.</summary>
    private sealed class ApplicationContext : DbContext
    {
        public ApplicationContext(DbContextOptions<ApplicationContext> options, bool detectChanges)
            : base(options)
        {
            ChangeTracker.AutoDetectChangesEnabled = detectChanges;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyNightShiftModel(schema: null);
        }
    }
}
