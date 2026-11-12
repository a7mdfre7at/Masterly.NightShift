using System.Globalization;
using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
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
    }

    [Fact]
    public void CountersAggregator_MergesIncrements_WithoutChangingTheTotals()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            for (int i = 0; i < 5; i++) transaction.IncrementCounter("stats:succeeded", TimeSpan.FromDays(1));
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
            for (int i = 0; i < 3; i++) x.IncrementCounter("stats:succeeded");
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
            for (int i = 0; i < count; i++) x.InsertToList(key, i.ToString(CultureInfo.InvariantCulture));
        });
    }
}
