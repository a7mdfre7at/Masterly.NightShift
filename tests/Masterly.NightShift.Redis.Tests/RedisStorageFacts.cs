using System.Diagnostics;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;
using StackExchange.Redis;

namespace Masterly.NightShift.Redis.Tests;

public sealed class RedisStorageFacts : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly List<RedisStorage> _storages = [];

    public void Dispose()
    {
        foreach (RedisStorage storage in _storages) RedisTestServer.Delete(storage);
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

    [Theory]
    [InlineData("nightshift:")]
    [InlineData("{}:nightshift:")]
    [InlineData("nightshift}{:")]
    public void Ctor_RefusesAPrefixWithoutAHashTag_UnlessAllowed(string prefix)
    {
        IConnectionMultiplexer connection = RedisTestServer.Connection;

        Assert.Throws<ArgumentException>(() => new RedisStorage(connection, new RedisStorageOptions { Prefix = prefix }));

        using RedisStorage allowed = new(connection, new RedisStorageOptions { Prefix = prefix + Guid.NewGuid().ToString("N") + ":", AllowPrefixWithoutHashTag = true });
        Assert.StartsWith(prefix, allowed.Options.Prefix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{nightshift}:")]
    [InlineData("app:{nightshift}:")]
    [InlineData("{a}")]
    public void HasHashTag_FindsANonEmptyNameInBraces(string prefix)
    {
        Assert.True(RedisStorageOptions.HasHashTag(prefix));
    }

    [Fact]
    public void Ctor_WithAConfigurationString_ChecksTheOptions_BeforeConnecting()
    {
        // Nothing listens there: connecting first would throw a connection error, and leave a connection behind
        const string Nowhere = "127.0.0.1:1,abortConnect=true,connectTimeout=200";

        Assert.Throws<ArgumentNullException>(() => new RedisStorage((string)null!));
        Assert.Throws<ArgumentException>(() => new RedisStorage(Nowhere, new RedisStorageOptions { InvisibilityTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new RedisStorage(Nowhere, new RedisStorageOptions { Prefix = "no-hash-tag:" }));
    }

    [Fact]
    public void Options_AreCopied_SoLaterChangesChangeNothing()
    {
        RedisStorageOptions options = new() { InvisibilityTimeout = TimeSpan.FromMinutes(5) };
        RedisStorage storage = Track(RedisTestServer.CreateStorage(options));

        options.InvisibilityTimeout = TimeSpan.FromMinutes(1);
        storage.Options.InvisibilityTimeout = TimeSpan.FromMinutes(2);

        Assert.Equal(TimeSpan.FromMinutes(5), storage.Options.InvisibilityTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), storage.Settings.InvisibilityTimeout);
    }

    [Fact]
    public void Dispose_LeavesAConnectionTheApplicationOwns_Open_AndCanBeRepeated()
    {
        RedisStorage storage = new(RedisTestServer.Connection, new RedisStorageOptions { Prefix = "{nightshift-tests-dispose}:" });

        storage.Dispose();
        storage.Dispose();

        Assert.True(RedisTestServer.Connection.IsConnected);
    }

    [Fact]
    public async Task Dispose_KeepsOtherStorages_OnTheSameConnection_Listening()
    {
        string prefix = $"{{nightshift-tests-{Guid.NewGuid():N}}}:";
        RedisStorage disposed = new(RedisTestServer.Connection, new RedisStorageOptions { Prefix = prefix });
        RedisStorage kept = Track(new RedisStorage(RedisTestServer.Connection, new RedisStorageOptions { Prefix = prefix }));

        disposed.Dispose();
        Task woken = kept.Enqueued;
        await RedisTestServer.Connection.GetSubscriber().PublishAsync(RedisChannel.Literal(prefix + "enqueued"), "default");

        await woken.WaitAsync(Deadline, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Jobs_ExpireInRedis_AndTheExpirationManager_RemovesThemFromTheStateLists()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        string id = CreateJob(storage);
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetJobState(id, new SucceededState(null, 1, 1));
            transaction.ExpireJob(id, TimeSpan.FromMilliseconds(50));
            transaction.Commit();
        }

        // Null once the key is gone, which a slow machine can reach before this line
        TimeSpan? ttl = storage.Database.KeyTimeToLive(storage.Key($"job:{id}"));
        Assert.True(ttl is null || ttl <= TimeSpan.FromMilliseconds(50), $"The job's time to live is {ttl}.");
        WaitUntil(() => (long)storage.Evaluate(RedisScripts.RemoveExpiredJobs, 100) > 0 || storage.GetMonitoringApi().SucceededListCount() == 0);

        Assert.Equal(0, storage.GetMonitoringApi().SucceededListCount());
        using IStorageConnection reader = storage.GetConnection();
        Assert.Null(reader.GetJobData(id));
    }

    [Fact]
    public void PersistJob_KeepsTheJob_AndItsHistory()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        string id = CreateJob(storage);
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetJobState(id, new EnqueuedState());
            transaction.PersistJob(id);
            transaction.Commit();
        }

        Assert.Null(storage.Database.KeyTimeToLive(storage.Key($"job:{id}")));
        Assert.Null(storage.Database.KeyTimeToLive(storage.Key($"job:{id}:history")));
        Assert.Null(storage.Database.KeyTimeToLive(storage.Key($"job:{id}:params")));
    }

    [Fact]
    public void Fetch_PutsBack_AnEntryWhoseFetcherStopped_AfterTheInvisibilityTimeout()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage(new RedisStorageOptions
        {
            InvisibilityTimeout = TimeSpan.FromMilliseconds(200),
            FetchPollInterval = TimeSpan.FromMilliseconds(50),
        }));
        string id = Enqueue(storage, "default");

        // Taken straight with the script, as by a worker whose process stopped: nothing keeps it alive
        RedisResult first = storage.Evaluate(RedisScripts.Fetch, 200, "dead", "default");
        Assert.False(first.IsNull);

        using IStorageConnection reader = storage.GetConnection();
        using CancellationTokenSource cancellation = new(Deadline);
        using IFetchedJob again = reader.FetchNextJob(["default"], cancellation.Token);

        Assert.Equal(id, again.JobId);
        again.RemoveFromQueue();
    }

    [Fact]
    public void Scripts_ReadTheServersClock_SoAClientWhoseClockIsAhead_NeitherTakesFetchedJobs_NorRemovesJobsEarly()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        string queued = Enqueue(storage, "default");
        string kept = CreateJob(storage);
        using IStorageConnection worker = storage.GetConnection();
        using IFetchedJob fetched = worker.FetchNextJob(["default"], CancellationToken.None);
        TimeProvider clock = JobClock.TimeProvider;

        try
        {
            JobClock.TimeProvider = new ShiftedTimeProvider(TimeSpan.FromHours(2));
            using IStorageConnection skewed = storage.GetConnection();
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

            Assert.ThrowsAny<OperationCanceledException>(() => skewed.FetchNextJob(["default"], cancellation.Token));
            Assert.Equal(0, (long)storage.Evaluate(RedisScripts.RemoveExpiredJobs, 100));
        }
        finally
        {
            JobClock.TimeProvider = clock;
        }

        Assert.Equal(queued, fetched.JobId);
        using IStorageConnection reader = storage.GetConnection();
        Assert.NotNull(reader.GetJobData(kept));
        fetched.RemoveFromQueue();
    }

    [Fact]
    public void Apply_RefusesAWrongTransaction_BeforeWritingAnything()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        storage.Database.StringSet(storage.Key("list:not-a-list"), "text");

        Assert.Throws<RedisServerException>(() => storage.Evaluate(RedisScripts.Apply, "zadd", "set", "a", 1, "bogus", "x"));
        Assert.Throws<RedisServerException>(() => storage.Evaluate(RedisScripts.Apply, "zadd", "set", "a", 1, "zadd", "set", "b"));
        Assert.Throws<RedisServerException>(() => storage.Evaluate(RedisScripts.Apply, "zadd", "set", "a", 1, "zadd", "set", "b", "not-a-score"));
        Assert.Throws<RedisServerException>(() => storage.Evaluate(RedisScripts.Apply, "zadd", "set", "a", 1, "hset", "hash", 3, "f", "v"));
        Assert.Throws<RedisServerException>(() => storage.Evaluate(RedisScripts.Apply, "zadd", "set", "a", 1, "counter", "c", "1.5", string.Empty));
        Assert.Throws<RedisServerException>(() => storage.Evaluate(RedisScripts.Apply, "zadd", "set", "a", 1, "lpush", "not-a-list", "x"));
        Assert.Throws<RedisServerException>(() => storage.Evaluate(RedisScripts.Apply, "zadd", "set", "a", 1, "expire", "string", "k", 10));

        Assert.Equal(0, storage.Database.SortedSetLength(storage.Key("set:set")));
    }

    [Fact]
    public void LargeHashes_Sets_AndJobParameters_AreWrittenInChunks()
    {
        const int Count = 5000;
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        Dictionary<string, string?> parameters = Enumerable.Range(0, Count).ToDictionary(static x => $"p{x}", static x => (string?)$"{x}", StringComparer.Ordinal);
        using IStorageConnection connection = storage.GetConnection();

        string id = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), parameters, JobClock.UtcNow, TimeSpan.FromHours(1))!;
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetRangeInHash("big", Enumerable.Range(0, Count).Select(static x => new KeyValuePair<string, string>($"f{x}", $"{x}")));
            ((JobStorageTransaction)transaction).AddRangeToSet("big", [.. Enumerable.Range(0, Count).Select(static x => $"v{x}")]);
            transaction.Commit();
        }

        JobStorageConnection reader = (JobStorageConnection)connection;
        Assert.Equal($"{Count - 1}", reader.GetJobParameter(id, $"p{Count - 1}"));
        Assert.Equal(Count, reader.GetHashCount("big"));
        Assert.Equal(Count, reader.GetSetCount("big"));
    }

    [Fact]
    public void Heartbeat_OfARemovedServer_Fails_WithoutBringingItBack()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        using IStorageConnection connection = storage.GetConnection();
        connection.AnnounceServer("server", new ServerContext { WorkerCount = 1, Queues = ["default"] });
        connection.RemoveServer("server");

        Assert.Throws<BackgroundServerGoneException>(() => connection.Heartbeat("server"));

        Assert.Null(storage.Database.SortedSetScore(storage.Key("servers:heartbeat"), "server"));
        Assert.Empty(storage.GetMonitoringApi().Servers());
    }

    [Fact]
    public void Lock_HeldByAProcessThatDied_IsFreed_WhenItsLeaseRunsOut()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        using IStorageConnection connection = storage.GetConnection();

        // A lease nobody renews, as left by a process that died
        Assert.True(((ILeaseStore)connection).TryTake("resource", "dead-owner", JobClock.UtcNow.AddMilliseconds(500)));

        using IStorageConnection other = storage.GetConnection();
        Assert.Throws<DistributedLockTimeoutException>(() => other.AcquireDistributedLock("resource", TimeSpan.FromMilliseconds(100)));
        using (other.AcquireDistributedLock("resource", Deadline))
        {
        }
    }

    [Fact]
    public void JobParameters_ExpireWithTheirJob()
    {
        RedisStorage storage = Track(RedisTestServer.CreateStorage());
        string id = CreateJob(storage);

        using IStorageConnection connection = storage.GetConnection();
        connection.SetJobParameter(id, "Added", "later");

        Assert.NotNull(storage.Database.KeyTimeToLive(storage.Key($"job:{id}:params")));
    }

    [Fact]
    public void UseRedisStorage_MakesItTheCurrentStorage()
    {
        Mock<IGlobalConfiguration> configuration = new();

        IGlobalConfiguration<RedisStorage> result = configuration.Object.UseRedisStorage(RedisTestServer.Connection, new RedisStorageOptions { Prefix = "{nightshift-tests-config}:" });

        Assert.Same(result.Entry, JobStorage.Current);
        result.Entry.Dispose();
    }

    private RedisStorage Track(RedisStorage storage)
    {
        _storages.Add(storage);
        return storage;
    }

    private static string CreateJob(RedisStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?> { ["Culture"] = "en" }, JobClock.UtcNow, TimeSpan.FromHours(1))!;
    }

    private static string Enqueue(RedisStorage storage, string queue)
    {
        string id = CreateJob(storage);
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToQueue(queue, id);
        transaction.Commit();
        return id;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < Deadline, "The condition didn't hold before the deadline.");
            Thread.Sleep(20);
        }
    }

    private sealed class ShiftedTimeProvider(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + shift;
    }
}
