using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;
using StackExchange.Redis;

namespace Masterly.NightShift.Redis.Tests;

public sealed class RedisStorageFacts : IDisposable
{
    private RedisStorage? _storage;

    public void Dispose()
    {
        if (_storage != null) RedisTestServer.Delete(_storage);
    }

    [Fact]
    public void Ctor_RefusesBadOptions()
    {
        IConnectionMultiplexer connection = RedisTestServer.Connection;

        Assert.Throws<ArgumentNullException>(() => new RedisStorage((IConnectionMultiplexer)null!));
        Assert.Throws<ArgumentException>(() => new RedisStorage(connection, new RedisStorageOptions { Prefix = string.Empty }));
        Assert.Throws<ArgumentException>(() => new RedisStorage(connection, new RedisStorageOptions { InvisibilityTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new RedisStorage(connection, new RedisStorageOptions { FetchPollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new RedisStorage(connection, new RedisStorageOptions { DistributedLockLifetime = TimeSpan.FromMilliseconds(10) }));
    }

    [Fact]
    public void Dispose_LeavesAConnectionTheApplicationOwns_Open()
    {
        RedisStorage storage = new(RedisTestServer.Connection, new RedisStorageOptions { Prefix = "{nightshift-tests-dispose}:" });

        storage.Dispose();

        Assert.True(RedisTestServer.Connection.IsConnected);
    }

    [Fact]
    public void Jobs_ExpireInRedis_AndTheExpirationManager_RemovesThemFromTheStateLists()
    {
        _storage = RedisTestServer.CreateStorage();
        string id = CreateJob(_storage);
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetJobState(id, new SucceededState(null, 1, 1));
            transaction.ExpireJob(id, TimeSpan.FromMilliseconds(50));
            transaction.Commit();
        }

        Assert.True(_storage.Database.KeyTimeToLive(_storage.Key($"job:{id}")) <= TimeSpan.FromMilliseconds(50));
        Thread.Sleep(200);
        _storage.Evaluate(RedisScripts.RemoveExpiredJobs, RedisValues.UnixMilliseconds(JobClock.UtcNow), 100);

        Assert.Equal(0, _storage.GetMonitoringApi().SucceededListCount());
        using IStorageConnection reader = _storage.GetConnection();
        Assert.Null(reader.GetJobData(id));
    }

    [Fact]
    public void PersistJob_KeepsTheJob_AndItsHistory()
    {
        _storage = RedisTestServer.CreateStorage();
        string id = CreateJob(_storage);
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetJobState(id, new EnqueuedState());
            transaction.PersistJob(id);
            transaction.Commit();
        }

        Assert.Null(_storage.Database.KeyTimeToLive(_storage.Key($"job:{id}")));
        Assert.Null(_storage.Database.KeyTimeToLive(_storage.Key($"job:{id}:history")));
        Assert.Null(_storage.Database.KeyTimeToLive(_storage.Key($"job:{id}:params")));
    }

    [Fact]
    public void Fetch_PutsBack_AnEntryWhoseFetcherStopped_AfterTheInvisibilityTimeout()
    {
        _storage = RedisTestServer.CreateStorage(new RedisStorageOptions { InvisibilityTimeout = TimeSpan.FromMilliseconds(200) });
        string id = CreateJob(_storage);
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToQueue("default", id);
            transaction.Commit();
        }

        // Taken straight with the script, as by a worker whose process stopped: nothing keeps it alive
        DateTime now = JobClock.UtcNow;
        RedisResult first = _storage.Evaluate(RedisScripts.Fetch, RedisValues.UnixMilliseconds(now), RedisValues.UnixMilliseconds(now.AddMilliseconds(-200)), "dead", "default");
        Assert.False(first.IsNull);
        Thread.Sleep(400);

        using IStorageConnection reader = _storage.GetConnection();
        using IFetchedJob again = reader.FetchNextJob(["default"], CancellationToken.None);

        Assert.Equal(id, again.JobId);
        again.RemoveFromQueue();
    }

    [Fact]
    public void Lock_HeldByAProcessThatDied_IsFreed_WhenItsLeaseRunsOut()
    {
        _storage = RedisTestServer.CreateStorage();
        using IStorageConnection connection = _storage.GetConnection();

        // A lease nobody renews, as left by a process that died
        Assert.True(((ILeaseStore)connection).TryTake("resource", "dead-owner", JobClock.UtcNow.AddMilliseconds(500)));

        using IStorageConnection other = _storage.GetConnection();
        Assert.Throws<DistributedLockTimeoutException>(() => other.AcquireDistributedLock("resource", TimeSpan.FromMilliseconds(100)));
        using (other.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)))
        {
        }
    }

    [Fact]
    public void JobParameters_ExpireWithTheirJob()
    {
        _storage = RedisTestServer.CreateStorage();
        string id = CreateJob(_storage);

        using IStorageConnection connection = _storage.GetConnection();
        connection.SetJobParameter(id, "Added", "later");

        Assert.NotNull(_storage.Database.KeyTimeToLive(_storage.Key($"job:{id}:params")));
    }

    [Fact]
    public void UseRedisStorage_MakesItTheCurrentStorage()
    {
        Mock<IGlobalConfiguration> configuration = new();

        IGlobalConfiguration<RedisStorage> result = configuration.Object.UseRedisStorage(RedisTestServer.Connection, new RedisStorageOptions { Prefix = "{nightshift-tests-config}:" });

        Assert.Same(result.Entry, JobStorage.Current);
        result.Entry.Dispose();
    }

    private static string CreateJob(RedisStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?> { ["Culture"] = "en" }, JobClock.UtcNow, TimeSpan.FromHours(1))!;
    }
}
