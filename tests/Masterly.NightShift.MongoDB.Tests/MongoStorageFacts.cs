using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Masterly.NightShift.MongoDB.Tests;

public sealed class MongoStorageFacts : IDisposable
{
    private MongoStorage? _storage;

    public void Dispose()
    {
        if (_storage != null) MongoTestServer.Drop(_storage);
    }

    [Fact]
    public void Ctor_RefusesAStandaloneServer()
    {
        Mock<IMongoDatabase> database = new();
        database
            .Setup(x => x.RunCommand(It.IsAny<Command<BsonDocument>>(), It.IsAny<ReadPreference>(), It.IsAny<CancellationToken>()))
            .Returns(new BsonDocument { ["isWritablePrimary"] = true });
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => new MongoStorage(database.Object));

        Assert.Contains("replica set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_RefusesBadOptions()
    {
        IMongoDatabase database = MongoTestServer.Database;

        Assert.Throws<ArgumentNullException>(() => new MongoStorage((IMongoDatabase)null!));
        Assert.Throws<ArgumentException>(() => new MongoStorage(database, new MongoStorageOptions { CollectionPrefix = string.Empty }));
        Assert.Throws<ArgumentException>(() => new MongoStorage(database, new MongoStorageOptions { InvisibilityTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new MongoStorage(database, new MongoStorageOptions { QueuePollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new MongoStorage(database, new MongoStorageOptions { DistributedLockLifetime = TimeSpan.FromMilliseconds(10) }));
    }

    [Fact]
    public void CountersAggregator_MergesIncrements_WithoutChangingTheTotals()
    {
        _storage = MongoTestServer.CreateStorage();
        Increment(_storage, "stats:succeeded", 5);
        Increment(_storage, "stats:deleted", 2);

        int merged = new MongoCountersAggregator(_storage).AggregateBatch();

        Assert.Equal(7, merged);
        Assert.Equal(0, _storage.Collections.Counters.CountDocuments(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken));
        using IStorageConnection connection = _storage.GetConnection();
        Assert.Equal(5, ((JobStorageConnection)connection).GetCounter("stats:succeeded"));
        Assert.Equal(2, ((JobStorageConnection)connection).GetCounter("stats:deleted"));
    }

    [Fact]
    public async Task CountersAggregators_RunningAtOnce_CountEachIncrementOnce()
    {
        _storage = MongoTestServer.CreateStorage();
        Increment(_storage, "stats:succeeded", 50);
        MongoCountersAggregator first = new(_storage);
        MongoCountersAggregator second = new(_storage);

        await Task.WhenAll(Task.Run(first.AggregateBatch, TestContext.Current.CancellationToken), Task.Run(second.AggregateBatch, TestContext.Current.CancellationToken));
        first.AggregateBatch();

        using IStorageConnection connection = _storage.GetConnection();
        Assert.Equal(50, ((JobStorageConnection)connection).GetCounter("stats:succeeded"));
    }

    [Fact]
    public void Fetch_TakesAgain_AnEntryWhoseFetcherStopped_AfterTheInvisibilityTimeout()
    {
        _storage = MongoTestServer.CreateStorage(new MongoStorageOptions { InvisibilityTimeout = TimeSpan.FromSeconds(30) });
        string id = CreateJob(_storage);
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToQueue("default", id);
            transaction.Commit();
        }

        // Marked as fetched a minute ago by a worker whose process stopped: nothing keeps it alive
        _storage.Collections.JobQueue.UpdateOne(
            FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Set("FetchedAt", JobClock.UtcNow.AddMinutes(-1)).Set("Token", "dead"),
            cancellationToken: TestContext.Current.CancellationToken);

        using IStorageConnection reader = _storage.GetConnection();
        using IFetchedJob again = reader.FetchNextJob(["default"], CancellationToken.None);

        Assert.Equal(id, again.JobId);
        again.RemoveFromQueue();
    }

    [Fact]
    public void Lock_HeldByAProcessThatDied_IsFreed_WhenItsLeaseRunsOut()
    {
        _storage = MongoTestServer.CreateStorage();
        using IStorageConnection connection = _storage.GetConnection();

        // A lease nobody renews, as left by a process that died
        Assert.True(((ILeaseStore)connection).TryTake("resource", "dead-owner", JobClock.UtcNow.AddMilliseconds(500)));
        Assert.False(((ILeaseStore)connection).TryTake("resource", "other-owner", JobClock.UtcNow.AddSeconds(5)));

        using IStorageConnection other = _storage.GetConnection();
        using (other.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)))
        {
        }
    }

    [Fact]
    public void JobParameters_MayHaveDotsAndDollarsInTheirNames()
    {
        _storage = MongoTestServer.CreateStorage();
        string id = CreateJob(_storage);

        using IStorageConnection connection = _storage.GetConnection();
        connection.SetJobParameter(id, "a.b", "dotted");
        connection.SetJobParameter(id, "$c", "dollar");
        connection.SetJobParameter(id, "a.b", "changed");

        Assert.Equal("changed", connection.GetJobParameter(id, "a.b"));
        Assert.Equal("dollar", connection.GetJobParameter(id, "$c"));
        Assert.Equal("en", connection.GetJobParameter(id, "Culture"));
    }

    [Fact]
    public void Indexes_AreCreated_WithTtlOnExpireAt()
    {
        _storage = MongoTestServer.CreateStorage();

        List<BsonDocument> indexes = _storage.Collections.Jobs.Indexes.List(TestContext.Current.CancellationToken).ToList(TestContext.Current.CancellationToken);

        Assert.Contains(indexes, static x => x["key"].AsBsonDocument.Contains("ExpireAt") && x.Contains("expireAfterSeconds"));
        Assert.Contains(indexes, static x => x["key"].AsBsonDocument.Contains("StateName"));
    }

    [Fact]
    public void UseMongoStorage_MakesItTheCurrentStorage()
    {
        Mock<IGlobalConfiguration> configuration = new();

        IGlobalConfiguration<MongoStorage> result = configuration.Object.UseMongoStorage(MongoTestServer.Database, new MongoStorageOptions { CollectionPrefix = $"t{Guid.NewGuid():N}." });
        _storage = result.Entry;

        Assert.Same(result.Entry, JobStorage.Current);
    }

    private static void Increment(MongoStorage storage, string key, int times)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        for (int i = 0; i < times; i++) transaction.IncrementCounter(key);
        transaction.Commit();
    }

    private static string CreateJob(MongoStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?> { ["Culture"] = "en" }, JobClock.UtcNow, TimeSpan.FromHours(1))!;
    }
}
