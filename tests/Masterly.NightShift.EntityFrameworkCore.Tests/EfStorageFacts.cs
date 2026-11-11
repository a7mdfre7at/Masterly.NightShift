using Masterly.NightShift.Storage.Conformance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
}
