using System.Globalization;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Monitoring;
using Masterly.NightShift.Storage.Shared;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Masterly.NightShift.MongoDB.Tests;

public sealed class MongoStorageFacts : IDisposable
{
    private MongoStorage? _storage;

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        if (_storage != null)
        {
            MongoTestServer.Drop(_storage);
        }
    }

    [Fact]
    public void Heartbeats_AreWrittenWithTheServersClock_WhateverThisProcesssClockSays()
    {
        _storage = MongoTestServer.CreateStorage();
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(10));
        using IStorageConnection connection = _storage.GetConnection();

        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
        DateTime announced = Heartbeat(_storage);
        connection.Heartbeat("server");
        DateTime beaten = Heartbeat(_storage);

        DateTime serverNow = ((JobStorageConnection)connection).GetUtcDateTime();
        Assert.InRange(announced, serverNow.AddSeconds(-30), serverNow);
        Assert.InRange(beaten, announced, serverNow);
    }

    [Fact]
    public void RemoveTimedOutServers_KeepsALiveServer_WhenThisProcesssClockIsAhead()
    {
        // The watchdog of a server whose clock is ten minutes ahead of the others'
        _storage = MongoTestServer.CreateStorage();
        using IStorageConnection connection = _storage.GetConnection();
        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(10));

        Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromMinutes(1)));
        Assert.Single(_storage.GetMonitoringApi().Servers());
    }

    [Fact]
    public void RemoveTimedOutServers_RemovesAStoppedServerOnTime_WhenThisProcesssClockIsBehind()
    {
        _storage = MongoTestServer.CreateStorage();
        using IStorageConnection connection = _storage.GetConnection();
        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });

        // Silent for five minutes by the server's clock; this process's clock is ten minutes behind it
        DateTime serverNow = ((JobStorageConnection)connection).GetUtcDateTime();
        _storage.Collections.Servers.UpdateOne(
            Builders<BsonDocument>.Filter.Eq("_id", "server"),
            Builders<BsonDocument>.Update.Set("LastHeartbeat", serverNow.AddMinutes(-5)),
            cancellationToken: TestContext.Current.CancellationToken);
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(-10));

        Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromMinutes(10)));
        Assert.Equal(1, connection.RemoveTimedOutServers(TimeSpan.FromMinutes(1)));
        Assert.Empty(_storage.GetMonitoringApi().Servers());
    }

    [Fact]
    public void Heartbeat_OfAServerThatWasRemoved_ThrowsAnException_AndDoesNotBringItBack()
    {
        _storage = MongoTestServer.CreateStorage();
        using IStorageConnection connection = _storage.GetConnection();

        Assert.Throws<BackgroundServerGoneException>(() => connection.Heartbeat("gone"));
        Assert.Empty(_storage.GetMonitoringApi().Servers());
    }

    [Fact]
    public void AnnounceServer_Again_ReplacesItsData_AndKeepsOneServer()
    {
        _storage = MongoTestServer.CreateStorage();
        using IStorageConnection connection = _storage.GetConnection();

        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
        connection.AnnounceServer("server", new ServerContext { Queues = ["critical", "$default"], WorkerCount = 7 });

        ServerDto server = Assert.Single(_storage.GetMonitoringApi().Servers());
        Assert.Equal(7, server.WorkersCount);
        Assert.Equal(["critical", "$default"], server.Queues);
    }

    private static DateTime Heartbeat(MongoStorage storage)
    {
        ServerDto server = Assert.Single(storage.GetMonitoringApi().Servers());
        return Assert.NotNull(server.Heartbeat);
    }

    /// <summary>The system clock, moved by a fixed time.</summary>
    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    [Fact]
    public void Ctor_RefusesAStandaloneServer()
    {
        Mock<IMongoDatabase> database = new();
        database
            .Setup(x => x.RunCommand(It.IsAny<Command<BsonDocument>>(), It.IsAny<ReadPreference>(), It.IsAny<CancellationToken>()))
            .Returns(new BsonDocument { ["isWritablePrimary"] = true });

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
    public void Writes_WaitForAMajority_AndReadsGoToThePrimary_WhateverTheDatabasesSettings()
    {
        // Writes a primary may lose in a failover, and reads from a secondary that may lag
        IMongoDatabase careless = MongoTestServer.Database
            .WithWriteConcern(WriteConcern.Unacknowledged)
            .WithReadPreference(ReadPreference.SecondaryPreferred)
            .WithReadConcern(ReadConcern.Local);
        _storage = new MongoStorage(careless, new MongoStorageOptions { CollectionPrefix = $"t{Guid.NewGuid():N}." });

        using IStorageConnection connection = _storage.GetConnection();
        using (connection.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)))
        {
        }

        string id = CreateJob(_storage);
        connection.SetJobParameter(id, "Name", "value");
        Assert.Equal("value", connection.GetJobParameter(id, "Name"));
        Assert.All(
            [_storage.Collections.Jobs, _storage.Collections.JobQueue, _storage.Collections.Locks, _storage.Collections.Sequences, _storage.Collections.Servers],
            static x =>
            {
                Assert.Equal(WriteConcern.WMajority, x.Settings.WriteConcern);
                Assert.Equal(ReadPreference.Primary, x.Settings.ReadPreference);
                Assert.Equal(ReadConcern.Majority, x.Settings.ReadConcern);
            });
    }

    [Fact]
    public void Options_AreCopied_SoLaterChangesChangeNothing()
    {
        MongoStorageOptions options = new() { QueuePollInterval = TimeSpan.FromSeconds(2), CollectionPrefix = $"t{Guid.NewGuid():N}." };
        _storage = new MongoStorage(MongoTestServer.Database, options);

        options.QueuePollInterval = TimeSpan.Zero;
        options.InvisibilityTimeout = TimeSpan.Zero;
        _storage.Options.QueuePollInterval = TimeSpan.FromMinutes(2);

        Assert.Equal(TimeSpan.FromSeconds(2), _storage.Options.QueuePollInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), _storage.Settings.QueuePollInterval);
        Assert.Equal(TimeSpan.FromMinutes(30), _storage.Settings.InvisibilityTimeout);
    }

    [Fact]
    public void Lock_WhoseHandleWasNotDisposed_IsReleased_WithItsConnection()
    {
        _storage = MongoTestServer.CreateStorage(new MongoStorageOptions { DistributedLockLifetime = TimeSpan.FromSeconds(1) });
        IStorageConnection first = _storage.GetConnection();
        IDisposable forgotten = first.AcquireDistributedLock("resource", TimeSpan.Zero);

        first.Dispose();

        using IStorageConnection second = _storage.GetConnection();
        IDisposable taken = second.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5));

        // The forgotten handle no longer holds anything, so it releases nothing
        forgotten.Dispose();
        using IStorageConnection third = _storage.GetConnection();
        Assert.Throws<DistributedLockTimeoutException>(() => third.AcquireDistributedLock("resource", TimeSpan.Zero));
        taken.Dispose();
    }

    [Fact]
    public async Task Fetch_CanceledWhileItsClaimRuns_StillTakesTheJob_SoItIsNotHiddenForTheInvisibilityTimeout()
    {
        _storage = MongoTestServer.CreateStorage();
        string id = CreateJob(_storage);
        Commit(_storage, x => x.AddToQueue("default", id));

        // An open transaction that wrote the entry makes the claim wait on the server until it ends
        using IClientSessionHandle blocker = await _storage.Database.Client.StartSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        blocker.StartTransaction();
        await _storage.Collections.JobQueue.UpdateOneAsync(
            blocker,
            Builders<BsonDocument>.Filter.Eq("JobId", long.Parse(id, CultureInfo.InvariantCulture)),
            Builders<BsonDocument>.Update.Set("Blocked", true),
            cancellationToken: TestContext.Current.CancellationToken);
        using CancellationTokenSource stopping = new();
        using IStorageConnection worker = _storage.GetConnection();
        Task<IFetchedJob> fetch = Task.Factory.StartNew(
            () => worker.FetchNextJob(["default"], stopping.Token),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        await stopping.CancelAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await blocker.CommitTransactionAsync(TestContext.Current.CancellationToken);

        using IFetchedJob fetched = await fetch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(id, fetched.JobId);
        fetched.RemoveFromQueue();
    }

    [Fact]
    public async Task Enqueue_AndRequeue_WakeTheWorkerWaitingOnThatQueue_AtOnce()
    {
        _storage = MongoTestServer.CreateStorage(new MongoStorageOptions { QueuePollInterval = TimeSpan.FromMinutes(1) });
        using IStorageConnection worker = _storage.GetConnection();
        string id = CreateJob(_storage);

        Task<IFetchedJob> first = Fetch(worker);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Commit(_storage, x => x.AddToQueue("wake", id));
        IFetchedJob fetched = await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(id, fetched.JobId);

        using IStorageConnection other = _storage.GetConnection();
        Task<IFetchedJob> second = Fetch(other);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        fetched.Requeue();
        fetched.Dispose();
        using IFetchedJob again = await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(id, again.JobId);
        again.RemoveFromQueue();

        static Task<IFetchedJob> Fetch(IStorageConnection connection)
        {
            return Task.Factory.StartNew(
                () => connection.FetchNextJob(["other", "wake"], TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    [Fact]
    public void Fetch_OfAStoppingWorker_ClaimsNothing()
    {
        _storage = MongoTestServer.CreateStorage();
        string id = CreateJob(_storage);
        Commit(_storage, x => x.AddToQueue("default", id));
        using IStorageConnection worker = _storage.GetConnection();

        Assert.ThrowsAny<OperationCanceledException>(() => worker.FetchNextJob(["default"], new CancellationToken(canceled: true)));

        Assert.Equal(1, _storage.GetMonitoringApi().EnqueuedCount("default"));
    }

    [Fact]
    public void UseMongoStorage_MakesItTheCurrentStorage()
    {
        Mock<IGlobalConfiguration> configuration = new();

        IGlobalConfiguration<MongoStorage> result = configuration.Object.UseMongoStorage(MongoTestServer.Database, new MongoStorageOptions { CollectionPrefix = $"t{Guid.NewGuid():N}." });
        _storage = result.Entry;

        Assert.Same(result.Entry, JobStorage.Current);
    }

    [Theory]
    [InlineData("setName", "rs0", 13)]
    [InlineData("setName", "rs0", 25)]
    [InlineData("msg", "isdbgrid", 17)]
    public void EnsureSupported_AcceptsAReplicaSetOrAShardedCluster_OfMongoDB5OrLater(string field, string value, int wireVersion)
    {
        MongoStorage.EnsureSupported(new BsonDocument { [field] = value, ["maxWireVersion"] = wireVersion });
    }

    [Fact]
    public void EnsureSupported_RefusesAServerOlderThanMongoDB5()
    {
        NotSupportedException old = Assert.Throws<NotSupportedException>(() => MongoStorage.EnsureSupported(new BsonDocument { ["setName"] = "rs0", ["maxWireVersion"] = 9 }));
        Assert.Throws<NotSupportedException>(() => MongoStorage.EnsureSupported(new BsonDocument { ["msg"] = "isdbgrid" }));

        Assert.Contains("5.0", old.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_AcceptsAReplicaSet_AndCreatesTheIndexes()
    {
        Mock<IMongoDatabase> database = new() { DefaultValue = DefaultValue.Mock };
        database
            .Setup(x => x.RunCommand(It.IsAny<Command<BsonDocument>>(), It.IsAny<ReadPreference>(), It.IsAny<CancellationToken>()))
            .Returns(new BsonDocument { ["setName"] = "rs0", ["maxWireVersion"] = 21 });

        MongoStorage storage = new(database.Object);

        Mock.Get(storage.Collections.Jobs.Indexes).Verify(
            x => x.CreateMany(It.IsAny<IEnumerable<CreateIndexModel<BsonDocument>>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(0, 9, 1499, 1490)]
    [InlineData(5, 9, 1494, 1490)]
    [InlineData(1490, 3000, 9, 0)]
    [InlineData(-3, 2, 1499, 1497)]
    public void TrimList_OnALongList_KeepsOnlyTheRange(int keepFrom, int keepTo, int newestKept, int oldestKept)
    {
        _storage = MongoTestServer.CreateStorage();
        FillList(_storage, "long", 1500);

        Commit(_storage, x => x.TrimList("long", keepFrom, keepTo));

        using IStorageConnection reader = _storage.GetConnection();
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
        _storage = MongoTestServer.CreateStorage();
        FillList(_storage, "long", 1500);
        FillList(_storage, "other", 3);

        Commit(_storage, x => x.TrimList("long", keepFrom, keepTo));

        using IStorageConnection reader = _storage.GetConnection();
        Assert.Equal(0, ((JobStorageConnection)reader).GetListCount("long"));
        Assert.Equal(3, ((JobStorageConnection)reader).GetListCount("other"));
    }

    [Fact]
    public void GetCounter_AddsIncrementsNotMergedYet_ToTheMergedValue()
    {
        _storage = MongoTestServer.CreateStorage();
        Increment(_storage, "stats:succeeded", 3);
        new MongoCountersAggregator(_storage).AggregateBatch();
        Increment(_storage, "stats:succeeded", 2);

        using IStorageConnection connection = _storage.GetConnection();
        Assert.Equal(5, ((JobStorageConnection)connection).GetCounter("stats:succeeded"));
        Assert.Equal(0, ((JobStorageConnection)connection).GetCounter("stats:deleted"));
        Assert.Equal(5, _storage.GetMonitoringApi().GetStatistics().Succeeded);
    }

    [Fact]
    public void Fetch_TakesVisibleEntriesFirst_ThenStaleOnes_EachOnce()
    {
        _storage = MongoTestServer.CreateStorage(new MongoStorageOptions { InvisibilityTimeout = TimeSpan.FromSeconds(30) });
        string stale = CreateJob(_storage);
        string visible = CreateJob(_storage);
        Commit(_storage, x => x.AddToQueue("default", stale));
        _storage.Collections.JobQueue.UpdateOne(
            FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Set("FetchedAt", JobClock.UtcNow.AddMinutes(-1)).Set("Token", "dead"),
            cancellationToken: TestContext.Current.CancellationToken);
        Commit(_storage, x => x.AddToQueue("default", visible));

        using IStorageConnection connection = _storage.GetConnection();
        using IFetchedJob first = connection.FetchNextJob(["default"], CancellationToken.None);
        using IFetchedJob second = connection.FetchNextJob(["default"], CancellationToken.None);
        using CancellationTokenSource nothingLeft = new(TimeSpan.FromMilliseconds(300));

        Assert.Equal(visible, first.JobId);
        Assert.Equal(stale, second.JobId);
        Assert.ThrowsAny<OperationCanceledException>(() => connection.FetchNextJob(["default"], nothingLeft.Token));
        first.RemoveFromQueue();
        second.RemoveFromQueue();
    }

    [Fact]
    public async Task Fetch_ByWorkersAtOnce_ClaimsEveryEntryOnce()
    {
        _storage = MongoTestServer.CreateStorage();
        string[] ids = [.. Enumerable.Range(0, 20).Select(_ => CreateJob(_storage))];
        Commit(_storage, x =>
        {
            foreach (string id in ids)
            {
                x.AddToQueue("default", id);
            }
        });
        MongoStorage storage = _storage;

        string[][] fetched = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                using IStorageConnection connection = storage.GetConnection();
                List<string> taken = [];
                for (int i = 0; i < 5; i++)
                {
                    using IFetchedJob job = connection.FetchNextJob(["default"], CancellationToken.None);
                    taken.Add(job.JobId);
                    job.RemoveFromQueue();
                }

                return taken.ToArray();
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(ids.Order(StringComparer.Ordinal), fetched.SelectMany(static x => x).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Lease_RunsOutByTheServersClock_AndIsRenewedByItsOwnerOnly()
    {
        _storage = MongoTestServer.CreateStorage();
        using IStorageConnection connection = _storage.GetConnection();
        ILeaseStore leases = (ILeaseStore)connection;

        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow.AddMinutes(1)));
        Assert.True(leases.TryRenew("resource", "owner", JobClock.UtcNow.AddMinutes(1)));
        Assert.False(leases.TryRenew("resource", "other", JobClock.UtcNow.AddMinutes(1)));
        Assert.False(leases.TryTake("resource", "other", JobClock.UtcNow.AddMinutes(1)));

        BsonDocument lease = _storage.Collections.Locks.Find(FilterDefinition<BsonDocument>.Empty).Single(TestContext.Current.CancellationToken);
        Assert.Equal("owner", lease["Owner"].AsString);
        Assert.InRange(lease["ExpireAt"].ToUniversalTime(), JobClock.UtcNow.AddSeconds(50), JobClock.UtcNow.AddSeconds(70));
    }

    [Theory]
    [InlineData(-3, -1)]
    [InlineData(-5, -3)]
    [InlineData(2, 1)]
    [InlineData(5, 9)]
    public void Ranges_EndingBeforeTheFirstItem_OrEmpty_ReadNothing(int startingFrom, int endingAt)
    {
        _storage = MongoTestServer.CreateStorage();
        Commit(_storage, x =>
        {
            for (int i = 0; i < 3; i++)
            {
                x.InsertToList("list", i.ToString(CultureInfo.InvariantCulture));
                x.AddToSet("set", i.ToString(CultureInfo.InvariantCulture), i);
            }
        });

        using IStorageConnection reader = _storage.GetConnection();
        Assert.Empty(((JobStorageConnection)reader).GetRangeFromList("list", startingFrom, endingAt));
        Assert.Empty(((JobStorageConnection)reader).GetRangeFromSet("set", startingFrom, endingAt));
    }

    [Fact]
    public void Ranges_StartingBeforeTheFirstItem_ReadFromIt_AndEndingPastTheLast_ReadToIt()
    {
        _storage = MongoTestServer.CreateStorage();
        Commit(_storage, x =>
        {
            for (int i = 0; i < 3; i++)
            {
                x.InsertToList("list", i.ToString(CultureInfo.InvariantCulture));
                x.AddToSet("set", i.ToString(CultureInfo.InvariantCulture), i);
            }
        });

        using IStorageConnection reader = _storage.GetConnection();
        Assert.Equal(["2", "1"], ((JobStorageConnection)reader).GetRangeFromList("list", -3, 1));
        Assert.Equal(["0", "1"], ((JobStorageConnection)reader).GetRangeFromSet("set", -3, 1));
        Assert.Equal(["1", "0"], ((JobStorageConnection)reader).GetRangeFromList("list", 1, int.MaxValue));
        Assert.Equal(["1", "2"], ((JobStorageConnection)reader).GetRangeFromSet("set", 1, int.MaxValue));
    }

    private static void Commit(MongoStorage storage, Action<IWriteOnlyTransaction> write)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    // Items "0" (oldest) to "count - 1" (newest)
    private static void FillList(MongoStorage storage, string key, int count)
    {
        Commit(storage, x =>
        {
            for (int i = 0; i < count; i++)
            {
                x.InsertToList(key, i.ToString(CultureInfo.InvariantCulture));
            }
        });
    }

    private static void Increment(MongoStorage storage, string key, int times)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        for (int i = 0; i < times; i++)
        {
            transaction.IncrementCounter(key);
        }

        transaction.Commit();
    }

    private static string CreateJob(MongoStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?> { ["Culture"] = "en" }, JobClock.UtcNow, TimeSpan.FromHours(1))!;
    }
}
