using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Storage.Conformance;

/// <summary>
/// What every storage must do, as SQL Server does it: jobs, states and parameters, sets, lists, hashes and
/// counters, queues, locks, servers, the monitoring API, purging, and jobs run end to end by a real server.
/// A storage's test project derives a class from this one and returns an empty storage from
/// <see cref="CreateStorage"/>; xUnit creates the class again for every test.
/// </summary>
public abstract class StorageConformanceFacts : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private JobStorage? _storage;

    protected JobStorage Storage => _storage ??= CreateStorage();

    /// <summary>An empty storage, created once per test.</summary>
    protected abstract JobStorage CreateStorage();

    /// <summary>
    /// Whether the dashboard's daily and hourly graphs read counters as soon as they are written. SQL Server reads
    /// them only once its aggregator has moved them.
    /// </summary>
    protected virtual bool TimelineReadsCountersAtOnce => true;

    /// <summary>
    /// Whether a range read that ends before the first position (both ends negative) reads nothing, as on SQL
    /// Server, where positions are numbered from 0 and a negative one comes before the first item.
    /// </summary>
    protected virtual bool RangesEndingBeforeTheFirstItemReadNothing => true;

    /// <summary>
    /// An empty storage whose workers don't keep their fetched jobs alive, so a fetched job goes back to its queue
    /// once <paramref name="invisibilityTimeout"/> passes, and whose waiting fetchers look at their queues at
    /// least that often. <c>null</c> (the default) when the storage can't be configured so: the tests that need
    /// it are skipped.
    /// </summary>
    protected virtual JobStorage? CreateStorageWithoutKeepAlive(TimeSpan invisibilityTimeout) => null;

    /// <summary>
    /// An empty storage whose distributed locks are leases of <paramref name="lifetime"/> that their holders renew,
    /// or <c>null</c> (the default) when its locks aren't leases: the tests that need it are skipped.
    /// </summary>
    protected virtual JobStorage? CreateStorageWithLockLifetime(TimeSpan lifetime) => null;

    /// <summary>
    /// Prepares <see cref="Storage"/> so that the returned write makes a transaction's commit fail, or returns
    /// <c>null</c> (the default) when no write fails a commit deterministically: the test that needs it is skipped.
    /// </summary>
    protected virtual Action<IWriteOnlyTransaction>? PrepareWriteThatFailsTheCommit() => null;

    public virtual void Dispose()
    {
        (_storage as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Jobs, parameters and states

    [Fact]
    public void CreateExpiredJob_StoresTheJob_ItsArguments_AndItsParameters()
    {
        DateTime createdAt = JobClock.UtcNow;
        string id = CreateJob("payload", createdAt);

        JobData data = Connection(x => x.GetJobData(id));

        Assert.Equal(typeof(ConformanceJobs), data.Job?.Type);
        Assert.Equal(nameof(ConformanceJobs.Record), data.Job?.Method.Name);
        Assert.Equal("payload", data.Job?.Args[0]);
        Assert.Null(data.State);
        Assert.InRange(data.CreatedAt, createdAt.AddSeconds(-1), createdAt.AddSeconds(1));
        Assert.Equal("en-US", data.ParametersSnapshot?["Culture"]);
        Assert.Equal("en-US", Connection(x => x.GetJobParameter(id, "Culture")));
    }

    [Fact]
    public void CreateExpiredJob_GivesEveryJobItsOwnId()
    {
        string[] ids = [CreateJob("a"), CreateJob("b"), CreateJob("c")];

        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Reads_AnswerNull_ForAJobThatDoesNotExist()
    {
        Assert.Null(Connection(x => x.GetJobData("987654321")));
        Assert.Null(Connection(x => x.GetJobData("not-a-number")));
        Assert.Null(Connection(x => x.GetStateData("987654321")));
        Assert.Null(Connection(x => x.GetJobParameter("987654321", "Culture")));
        Assert.Null(Storage.GetMonitoringApi().JobDetails("987654321"));
    }

    [Fact]
    public void SetJobParameter_AddsAndOverwrites_AndNullReadsBackAsNull()
    {
        string id = CreateJob("x");

        Connection(x => x.SetJobParameter(id, "Retries", "1"));
        Connection(x => x.SetJobParameter(id, "Retries", "2"));
        Connection(x => x.SetJobParameter(id, "Empty", null));

        Assert.Equal("2", Connection(x => x.GetJobParameter(id, "Retries")));
        Assert.Null(Connection(x => x.GetJobParameter(id, "Empty")));
    }

    [Fact]
    public void SetJobParameter_InATransaction_IsWrittenOnCommit()
    {
        Assert.SkipUnless(Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter), "The storage doesn't set parameters in transactions.");
        string id = CreateJob("x");

        Write(x => ((JobStorageTransaction)x).SetJobParameter(id, "Owner", "tests"));

        Assert.Equal("tests", Connection(x => x.GetJobParameter(id, "Owner")));
    }

    [Fact]
    public void SetJobState_MakesTheStateCurrent_WithItsReasonAndData()
    {
        string id = CreateJob("x");

        Write(x => x.SetJobState(id, new ScheduledState(TimeSpan.FromHours(1)) { Reason = "First" }));
        Write(x => x.SetJobState(id, new EnqueuedState("critical") { Reason = "Due" }));

        StateData? state = Connection(x => x.GetStateData(id));
        Assert.Equal(EnqueuedState.StateName, state?.Name);
        Assert.Equal("Due", state?.Reason);
        Assert.Equal("critical", state?.Data["Queue"]);
        Assert.Equal("critical", state?.Data["queue"]);
        Assert.Equal(EnqueuedState.StateName, Connection(x => x.GetJobData(id))?.State);
    }

    [Fact]
    public void AddJobState_AddsToTheHistory_WithoutChangingTheCurrentState()
    {
        string id = CreateJob("x");

        Write(x => x.SetJobState(id, new EnqueuedState()));
        Write(x => x.AddJobState(id, new DeletedState { Reason = "Only history" }));

        Assert.Equal(EnqueuedState.StateName, Connection(x => x.GetStateData(id))?.Name);
        JobDetailsDto details = Storage.GetMonitoringApi().JobDetails(id);
        Assert.Equal([DeletedState.StateName, EnqueuedState.StateName], details?.History.Select(static x => x.StateName));
        Assert.Equal("Only history", details?.History[0].Reason);
    }

    [Fact]
    public void Transaction_WritesNothing_UntilCommitted()
    {
        string id = CreateJob("x");

        using (IStorageConnection connection = Storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetJobState(id, new EnqueuedState());
            transaction.AddToSet("uncommitted", "value");
            transaction.IncrementCounter("uncommitted");
        }

        Assert.Null(Connection(x => x.GetStateData(id)));
        Assert.Empty(Connection(x => x.GetAllItemsFromSet("uncommitted")));
        Assert.Equal(0, ExtendedConnection(x => x.GetCounter("uncommitted")));
    }

    [Fact]
    public void Transaction_WhoseCommitFails_WritesNothing()
    {
        Action<IWriteOnlyTransaction>? failing = PrepareWriteThatFailsTheCommit();
        Assert.SkipWhen(failing == null, "No write fails the storage's commits.");
        string id = CreateJob("x");

        using (IStorageConnection connection = Storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetJobState(id, new EnqueuedState());
            transaction.AddToSet("before", "a");
            transaction.IncrementCounter("before");
            transaction.InsertToList("before", "a");
            transaction.SetRangeInHash("before", [new("field", "value")]);
            failing(transaction);
            transaction.AddToSet("after", "b");

            Assert.ThrowsAny<Exception>(transaction.Commit);
        }

        Assert.Null(Connection(x => x.GetStateData(id)));
        Assert.Empty(Connection(x => x.GetAllItemsFromSet("before")));
        Assert.Equal(0, ExtendedConnection(x => x.GetCounter("before")));
        Assert.Empty(ExtendedConnection(x => x.GetAllItemsFromList("before")));
        Assert.Null(Connection(x => x.GetAllEntriesFromHash("before")));
        Assert.Empty(Connection(x => x.GetAllItemsFromSet("after")));
    }

    [Fact]
    public void ExpireJob_AndPersistJob_SetAndClearWhenTheJobExpires()
    {
        string id = CreateJob("x");
        IMonitoringApi monitoring = Storage.GetMonitoringApi();

        Assert.NotNull(monitoring.JobDetails(id)?.ExpireAt);

        Write(x => x.PersistJob(id));
        Assert.Null(monitoring.JobDetails(id)?.ExpireAt);

        Write(x => x.ExpireJob(id, TimeSpan.FromDays(1)));
        Assert.InRange(monitoring.JobDetails(id)?.ExpireAt ?? DateTime.MinValue, JobClock.UtcNow.AddHours(23), JobClock.UtcNow.AddHours(25));
    }

    // Sets

    [Fact]
    public void Sets_KeepEachValueOnce_OrderedByScoreThenValue()
    {
        Write(x =>
        {
            x.AddToSet("set", "b", 2);
            x.AddToSet("set", "a", 1);
            x.AddToSet("set", "c", 0);
            x.AddToSet("set", "a2", 1);
            x.AddToSet("other", "z");
        });

        Assert.Equal(["a", "a2", "b", "c"], Connection(x => x.GetAllItemsFromSet("set")).Order(StringComparer.Ordinal));
        Assert.Equal(4, ExtendedConnection(x => x.GetSetCount("set")));
        Assert.Equal(["c", "a", "a2", "b"], ExtendedConnection(x => x.GetRangeFromSet("set", 0, 3)));
        Assert.Equal(["a2", "b"], ExtendedConnection(x => x.GetRangeFromSet("set", 2, 10)));
        Assert.True(ExtendedConnection(x => x.GetSetContains("set", "b")));
        Assert.False(ExtendedConnection(x => x.GetSetContains("set", "z")));
    }

    [Fact]
    public void AddToSet_ChangesTheScore_OfAValueAlreadyThere()
    {
        Write(x => x.AddToSet("set", "a", 5));
        Write(x => x.AddToSet("set", "b", 3));
        Write(x => x.AddToSet("set", "a", 1));

        Assert.Equal(2, ExtendedConnection(x => x.GetSetCount("set")));
        Assert.Equal(["a", "b"], ExtendedConnection(x => x.GetRangeFromSet("set", 0, 1)));
    }

    [Fact]
    public void GetFirstByLowestScoreFromSet_ReturnsTheLowestScoresInTheRange()
    {
        Write(x =>
        {
            x.AddToSet("due", "late", 10);
            x.AddToSet("due", "first", 1);
            x.AddToSet("due", "second", 2);
            x.AddToSet("due", "too-early", -5);
        });

        Assert.Equal("first", Connection(x => x.GetFirstByLowestScoreFromSet("due", 0, 5)));
        Assert.Null(Connection(x => x.GetFirstByLowestScoreFromSet("due", 20, 30)));
        Assert.Equal(["first", "second"], ExtendedConnection(x => x.GetFirstByLowestScoreFromSet("due", 0, 100, 2)));
        Assert.Throws<ArgumentException>(() => ExtendedConnection(x => x.GetFirstByLowestScoreFromSet("due", 5, 0, 1)));
    }

    [Fact]
    public void RemoveFromSet_RemoveSet_AndAddRangeToSet()
    {
        Write(x =>
        {
            x.AddToSet("set", "a");
            x.AddToSet("set", "b");
            ((JobStorageTransaction)x).AddRangeToSet("range", ["1", "2", "3"]);
        });

        Write(x => x.RemoveFromSet("set", "a"));
        Assert.Equal(["b"], Connection(x => x.GetAllItemsFromSet("set")));

        Write(x => ((JobStorageTransaction)x).RemoveSet("set"));
        Assert.Empty(Connection(x => x.GetAllItemsFromSet("set")));
        Assert.Equal(["1", "2", "3"], Connection(x => x.GetAllItemsFromSet("range")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RemovingAndAddingAgain_InOneTransaction_KeepsTheLastWrite()
    {
        Write(x =>
        {
            x.AddToSet("set", "a", 1);
            x.SetRangeInHash("hash", [new("field", "old")]);
        });

        Write(x =>
        {
            x.RemoveFromSet("set", "a");
            x.AddToSet("set", "a", 7);
            x.RemoveHash("hash");
            x.SetRangeInHash("hash", [new("field", "new")]);
            x.AddToSet("added-then-removed", "b");
            x.RemoveFromSet("added-then-removed", "b");
        });

        Assert.Equal(["a"], ExtendedConnection(x => x.GetFirstByLowestScoreFromSet("set", 7, 7, 10)));
        Assert.Equal("new", ExtendedConnection(x => x.GetValueFromHash("hash", "field")));
        Assert.Empty(Connection(x => x.GetAllItemsFromSet("added-then-removed")));
    }

    [Fact]
    public void GetSetCount_OfSeveralKeys_StopsAtTheLimit()
    {
        Write(x =>
        {
            x.AddToSet("k1", "a");
            x.AddToSet("k1", "b");
            x.AddToSet("k2", "c");
        });

        Assert.Equal(3, ExtendedConnection(x => x.GetSetCount(["k1", "k2", "k3"], 10)));
        Assert.Equal(2, ExtendedConnection(x => x.GetSetCount(["k1", "k2"], 2)));
    }

    [Fact]
    public void ExpireSet_AndPersistSet_SetAndClearItsTimeToLive()
    {
        Write(x => x.AddToSet("set", "a"));
        Assert.True(ExtendedConnection(x => x.GetSetTtl("set")) < TimeSpan.Zero);

        Write(x => ((JobStorageTransaction)x).ExpireSet("set", TimeSpan.FromHours(1)));
        Assert.InRange(ExtendedConnection(x => x.GetSetTtl("set")), TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(65));

        Write(x => ((JobStorageTransaction)x).PersistSet("set"));
        Assert.True(ExtendedConnection(x => x.GetSetTtl("set")) < TimeSpan.Zero);
    }

    // Lists

    [Fact]
    public void Lists_ReadNewestFirst()
    {
        Write(x =>
        {
            x.InsertToList("list", "1");
            x.InsertToList("list", "2");
            x.InsertToList("list", "3");
        });

        Assert.Equal(["3", "2", "1"], ExtendedConnection(x => x.GetAllItemsFromList("list")));
        Assert.Equal(["2", "1"], ExtendedConnection(x => x.GetRangeFromList("list", 1, 5)));
        Assert.Equal(3, ExtendedConnection(x => x.GetListCount("list")));
        Assert.Empty(ExtendedConnection(x => x.GetAllItemsFromList("missing")));
    }

    [Fact]
    public void RemoveFromList_RemovesEveryOccurrence_AndTrimListKeepsARange()
    {
        Write(x =>
        {
            foreach (string value in new[] { "1", "x", "2", "x", "3", "4", "5" }) x.InsertToList("list", value);
        });

        Write(x => x.RemoveFromList("list", "x"));
        Assert.Equal(["5", "4", "3", "2", "1"], ExtendedConnection(x => x.GetAllItemsFromList("list")));

        Write(x => x.TrimList("list", 1, 2));
        Assert.Equal(["4", "3"], ExtendedConnection(x => x.GetAllItemsFromList("list")));
    }

    [Fact]
    public void Ranges_NumberPositionsFromZero_SoNegativeOnesComeBeforeTheFirstItem()
    {
        Write(x =>
        {
            x.AddToSet("set", "a", 1);
            x.AddToSet("set", "b", 2);
            x.AddToSet("set", "c", 3);
            foreach (string key in new[] { "list", "trim-negative-start", "trim-negative", "trim-inverted" })
            {
                x.InsertToList(key, "1");
                x.InsertToList(key, "2");
                x.InsertToList(key, "3");
            }
        });

        Write(x =>
        {
            x.TrimList("trim-negative-start", -1, 1);
            x.TrimList("trim-negative", -3, -1);
            x.TrimList("trim-inverted", 2, 1);
        });

        Assert.Equal(["a"], ExtendedConnection(x => x.GetRangeFromSet("set", -2, 0)));
        Assert.Equal(["b", "c"], ExtendedConnection(x => x.GetRangeFromSet("set", 1, 100)));
        Assert.Empty(ExtendedConnection(x => x.GetRangeFromSet("set", 2, 1)));
        Assert.Equal(["3", "2"], ExtendedConnection(x => x.GetRangeFromList("list", -1, 1)));
        Assert.Empty(ExtendedConnection(x => x.GetRangeFromList("list", 1, 0)));
        Assert.Equal(["3", "2"], ExtendedConnection(x => x.GetAllItemsFromList("trim-negative-start")));
        Assert.Empty(ExtendedConnection(x => x.GetAllItemsFromList("trim-negative")));
        Assert.Empty(ExtendedConnection(x => x.GetAllItemsFromList("trim-inverted")));
    }

    [Fact]
    public void Ranges_EndingBeforeTheFirstItem_ReadNothing()
    {
        Assert.SkipUnless(RangesEndingBeforeTheFirstItemReadNothing, "The storage reads something for ranges of negative positions.");
        Write(x =>
        {
            x.AddToSet("set", "a", 1);
            x.AddToSet("set", "b", 2);
            x.InsertToList("list", "1");
            x.InsertToList("list", "2");
        });

        Assert.Empty(ExtendedConnection(x => x.GetRangeFromSet("set", -3, -1)));
        Assert.Empty(ExtendedConnection(x => x.GetRangeFromList("list", -3, -1)));
    }

    [Fact]
    public void ExpireList_AndPersistList_SetAndClearItsTimeToLive()
    {
        Write(x => x.InsertToList("list", "a"));
        Assert.True(ExtendedConnection(x => x.GetListTtl("list")) < TimeSpan.Zero);

        Write(x => ((JobStorageTransaction)x).ExpireList("list", TimeSpan.FromHours(1)));
        Assert.InRange(ExtendedConnection(x => x.GetListTtl("list")), TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(65));

        Write(x => ((JobStorageTransaction)x).PersistList("list"));
        Assert.True(ExtendedConnection(x => x.GetListTtl("list")) < TimeSpan.Zero);
    }

    // Hashes

    [Fact]
    public void Hashes_MergeFields_FromConnectionsAndTransactions()
    {
        Connection(x => x.SetRangeInHash("hash", [new("a", "1"), new("b", "2")]));
        Write(x => x.SetRangeInHash("hash", [new("b", "3"), new("c", "4")]));

        Dictionary<string, string>? hash = Connection(x => x.GetAllEntriesFromHash("hash"));
        Assert.Equal(3, hash?.Count);
        Assert.Equal("3", hash?["b"]);
        Assert.Equal("4", ExtendedConnection(x => x.GetValueFromHash("hash", "c")));
        Assert.Null(ExtendedConnection(x => x.GetValueFromHash("hash", "missing")));
        Assert.Equal(3, ExtendedConnection(x => x.GetHashCount("hash")));
        Assert.Null(Connection(x => x.GetAllEntriesFromHash("missing")));
    }

    [Fact]
    public void RemoveHash_RemovesEveryField()
    {
        Write(x => x.SetRangeInHash("hash", [new("a", "1")]));

        Write(x => x.RemoveHash("hash"));

        Assert.Null(Connection(x => x.GetAllEntriesFromHash("hash")));
    }

    [Fact]
    public void ExpireHash_AndPersistHash_SetAndClearItsTimeToLive()
    {
        Write(x => x.SetRangeInHash("hash", [new("a", "1")]));
        Assert.True(ExtendedConnection(x => x.GetHashTtl("hash")) < TimeSpan.Zero);

        Write(x => ((JobStorageTransaction)x).ExpireHash("hash", TimeSpan.FromHours(1)));
        Assert.InRange(ExtendedConnection(x => x.GetHashTtl("hash")), TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(65));

        Write(x => ((JobStorageTransaction)x).PersistHash("hash"));
        Assert.True(ExtendedConnection(x => x.GetHashTtl("hash")) < TimeSpan.Zero);
    }

    // Counters

    [Fact]
    public void Counters_AddUpIncrementsAndDecrements()
    {
        Write(x =>
        {
            x.IncrementCounter("counter");
            x.IncrementCounter("counter", TimeSpan.FromDays(1));
            x.IncrementCounter("counter");
            x.DecrementCounter("counter");
            x.DecrementCounter("other", TimeSpan.FromDays(1));
        });

        Assert.Equal(2, ExtendedConnection(x => x.GetCounter("counter")));
        Assert.Equal(-1, ExtendedConnection(x => x.GetCounter("other")));
        Assert.Equal(0, ExtendedConnection(x => x.GetCounter("missing")));
    }

    // Servers

    [Fact]
    public void Servers_AnnounceThemselves_Heartbeat_AndGoAway()
    {
        Connection(x => x.AnnounceServer("server-1", new ServerContext { WorkerCount = 4, Queues = ["default", "critical"] }));
        Connection(x => x.Heartbeat("server-1"));

        ServerDto server = Assert.Single(Storage.GetMonitoringApi().Servers());
        Assert.Equal("server-1", server.Name);
        Assert.Equal(4, server.WorkersCount);
        Assert.Equal(["default", "critical"], server.Queues);
        Assert.Equal(0, Connection(x => x.RemoveTimedOutServers(TimeSpan.FromHours(1))));

        Connection(x => x.RemoveServer("server-1"));

        Assert.Empty(Storage.GetMonitoringApi().Servers());
        Assert.Throws<BackgroundServerGoneException>(() => Connection(x => x.Heartbeat("server-1")));
        Assert.Throws<ArgumentException>(() => Connection(x => x.RemoveTimedOutServers(TimeSpan.FromSeconds(-1))));
    }

    [Fact]
    public void RemoveTimedOutServers_RemovesServersThatStoppedBeating()
    {
        Connection(x => x.AnnounceServer("gone", new ServerContext { WorkerCount = 1, Queues = ["default"] }));
        Thread.Sleep(1500);

        Assert.Equal(1, Connection(x => x.RemoveTimedOutServers(TimeSpan.FromMilliseconds(500))));
        Assert.Empty(Storage.GetMonitoringApi().Servers());
    }

    // Queues

    [Fact]
    public void FetchNextJob_TakesAnEnqueuedJob_AndRemoveFromQueueAcknowledgesIt()
    {
        string id = Enqueue("default");
        IMonitoringApi monitoring = Storage.GetMonitoringApi();
        Assert.Equal(1, monitoring.EnqueuedCount("default"));
        Assert.Equal(id, Assert.Single(monitoring.EnqueuedJobs("default", 0, 10)).Key);
        Assert.Equal(1, Assert.Single(monitoring.Queues(), static x => x.Name == "default").Length);

        using (IStorageConnection connection = Storage.GetConnection())
        using (IFetchedJob fetched = connection.FetchNextJob(["default"], CancellationToken.None))
        {
            Assert.Equal(id, fetched.JobId);
            Assert.Equal(0, monitoring.EnqueuedCount("default"));
            Assert.Equal(1, monitoring.FetchedCount("default"));
            fetched.RemoveFromQueue();
        }

        Assert.Equal(0, monitoring.EnqueuedCount("default"));
        Assert.Equal(0, monitoring.FetchedCount("default"));
        Assert.Null(TryFetch(["default"]));
    }

    [Fact]
    public void FetchNextJob_TakesFromAnyOfTheGivenQueues()
    {
        string id = Enqueue("reports");

        using IStorageConnection connection = Storage.GetConnection();
        using IFetchedJob fetched = connection.FetchNextJob(["critical", "reports"], CancellationToken.None);

        Assert.Equal(id, fetched.JobId);
        fetched.RemoveFromQueue();
    }

    [Fact]
    public void Requeue_AndDisposingWithoutAcknowledging_PutTheJobBack()
    {
        string id = Enqueue("default");
        using IStorageConnection connection = Storage.GetConnection();

        using (IFetchedJob fetched = connection.FetchNextJob(["default"], CancellationToken.None))
        {
            fetched.Requeue();
        }

        using (IFetchedJob fetched = connection.FetchNextJob(["default"], CancellationToken.None))
        {
            Assert.Equal(id, fetched.JobId);
        }

        using IFetchedJob again = connection.FetchNextJob(["default"], CancellationToken.None);
        Assert.Equal(id, again.JobId);
        again.RemoveFromQueue();
    }

    [Fact]
    public void FetchNextJob_WaitsForAJob_UntilCanceled()
    {
        using IStorageConnection connection = Storage.GetConnection();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        Assert.ThrowsAny<OperationCanceledException>(() => connection.FetchNextJob(["empty"], cancellation.Token));
    }

    [Fact]
    public async Task FetchNextJob_WakesUp_WhenAJobIsEnqueued()
    {
        Task<string> fetching = Task.Run(() =>
        {
            using IStorageConnection connection = Storage.GetConnection();
            using CancellationTokenSource cancellation = new(Timeout);
            using IFetchedJob fetched = connection.FetchNextJob(["wake"], cancellation.Token);
            fetched.RemoveFromQueue();
            return fetched.JobId;
        }, TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        string id = Enqueue("wake");

        Assert.Equal(id, await fetching.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ConcurrentFetchers_NeverTakeTheSameJob()
    {
        HashSet<string> enqueued = [.. Enumerable.Range(0, 20).Select(_ => Enqueue("parallel"))];
        ConcurrentBag<string> taken = [];

        Parallel.For(0, 4, _ =>
        {
            using IStorageConnection connection = Storage.GetConnection();
            while (true)
            {
                using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));
                IFetchedJob fetched;
                try
                {
                    fetched = connection.FetchNextJob(["parallel"], cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                using (fetched)
                {
                    taken.Add(fetched.JobId);
                    fetched.RemoveFromQueue();
                }
            }
        });

        Assert.Equal(20, taken.Count);
        Assert.Equal(enqueued.Order(StringComparer.Ordinal), taken.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RemoveFromQueue_InACommittedTransaction_AcknowledgesTheJob()
    {
        Enqueue("default");

        using (IStorageConnection connection = Storage.GetConnection())
        using (IFetchedJob fetched = connection.FetchNextJob(["default"], CancellationToken.None))
        {
            Assert.SkipUnless(Storage.HasFeature(JobStorageFeatures.Transaction.RemoveFromQueue(fetched.GetType())), "The storage doesn't acknowledge in transactions.");

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            ((JobStorageTransaction)transaction).RemoveFromQueue(fetched);
            transaction.SetJobState(fetched.JobId, new SucceededState(null, 1, 1));
            transaction.Commit();
        }

        Assert.Null(TryFetch(["default"]));
        Assert.Equal(0, Storage.GetMonitoringApi().FetchedCount("default"));
    }

    [Fact]
    public void AStaleFetch_CanNeitherRemoveNorRequeue_TheJobOnceAnotherWorkerTookIt()
    {
        Assert.SkipUnless(UseStorage(CreateStorageWithoutKeepAlive(TimeSpan.FromSeconds(1))), "The storage can't let a fetch go stale.");
        string requeued = Enqueue("stale");
        string removed = Enqueue("stale");
        IMonitoringApi monitoring = Storage.GetMonitoringApi();
        using IStorageConnection dead = Storage.GetConnection();
        using IStorageConnection live = Storage.GetConnection();
        using CancellationTokenSource cancellation = new(Timeout);

        // A worker that stopped keeping its jobs alive: past the invisibility timeout, another worker takes them
        IFetchedJob[] stale = [dead.FetchNextJob(["stale"], cancellation.Token), dead.FetchNextJob(["stale"], cancellation.Token)];
        IFetchedJob[] fresh = [live.FetchNextJob(["stale"], cancellation.Token), live.FetchNextJob(["stale"], cancellation.Token)];
        Assert.Equal(new[] { requeued, removed }.Order(StringComparer.Ordinal), fresh.Select(static x => x.JobId).Order(StringComparer.Ordinal));

        stale.Single(x => x.JobId == requeued).Requeue();
        stale.Single(x => x.JobId == removed).RemoveFromQueue();
        foreach (IFetchedJob job in stale) job.Dispose();

        Assert.Equal(0, monitoring.EnqueuedCount("stale"));
        Assert.Equal(2, monitoring.FetchedCount("stale"));

        foreach (IFetchedJob job in fresh)
        {
            job.RemoveFromQueue();
            job.Dispose();
        }

        Assert.Equal(0, monitoring.EnqueuedCount("stale"));
        Assert.Equal(0, monitoring.FetchedCount("stale"));
    }

    // Locks

    [Fact]
    public void DistributedLock_HeldLongerThanItsLifetime_StaysExclusive_WhileItsHolderRenewsIt()
    {
        TimeSpan lifetime = TimeSpan.FromSeconds(1);
        Assert.SkipUnless(UseStorage(CreateStorageWithLockLifetime(lifetime)), "The storage's locks aren't leases.");
        using IStorageConnection first = Storage.GetConnection();
        using IStorageConnection second = Storage.GetConnection();

        using (first.AcquireDistributedLock("renewed", TimeSpan.FromSeconds(5)))
        {
            Thread.Sleep(lifetime * 2.5);

            Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock("renewed", TimeSpan.FromMilliseconds(300)));
        }

        using (second.AcquireDistributedLock("renewed", TimeSpan.FromSeconds(5)))
        {
        }
    }

    [Fact]
    public void DistributedLocks_AreExclusive_AcrossConnections_AndReentrant_OnOne()
    {
        using IStorageConnection first = Storage.GetConnection();
        using IStorageConnection second = Storage.GetConnection();

        using (first.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)))
        {
            using (first.AcquireDistributedLock("resource", TimeSpan.FromSeconds(1)))
            {
            }

            Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock("resource", TimeSpan.FromMilliseconds(300)));
        }

        using (second.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)))
        {
        }
    }

    [Fact]
    public void DistributedLock_TakenInATransaction_IsHeldWhileTheTransactionIsOpen()
    {
        Assert.SkipUnless(Storage.HasFeature(JobStorageFeatures.Transaction.AcquireDistributedLock), "The storage doesn't lock in transactions.");
        using IStorageConnection first = Storage.GetConnection();
        using IStorageConnection second = Storage.GetConnection();

        using (IWriteOnlyTransaction transaction = first.CreateWriteTransaction())
        {
            ((JobStorageTransaction)transaction).AcquireDistributedLock("resource", TimeSpan.FromSeconds(5));
            transaction.AddToSet("locked", "a");

            Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock("resource", TimeSpan.FromMilliseconds(300)));
            transaction.Commit();
        }

        using (second.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)))
        {
        }
    }

    // Monitoring

    [Fact]
    public void Monitoring_CountsAndListsJobsByState()
    {
        string succeeded1 = CreateJob("s1");
        string succeeded2 = CreateJob("s2");
        string failed = CreateJob("f");
        string scheduled = CreateJob("sc");
        DateTime enqueueAt = JobClock.UtcNow.AddHours(1);
        Write(x =>
        {
            x.SetJobState(succeeded1, new SucceededState("result-1", 10, 20));
            x.SetJobState(succeeded2, new SucceededState(null, 1, 2));
            x.SetJobState(failed, new FailedState(new InvalidOperationException("Broken")) { Reason = "Failed hard" });
            x.SetJobState(scheduled, new ScheduledState(enqueueAt));
            x.IncrementCounter("stats:succeeded");
            x.IncrementCounter("stats:succeeded");
            x.AddToSet("recurring-jobs", "nightly");
        });

        IMonitoringApi monitoring = Storage.GetMonitoringApi();

        Assert.Equal(2, monitoring.SucceededListCount());
        Assert.Equal(1, monitoring.FailedCount());
        Assert.Equal(1, monitoring.ScheduledCount());
        Assert.Equal(0, monitoring.ProcessingCount());
        Assert.Equal([succeeded2, succeeded1], monitoring.SucceededJobs(0, 10).Select(static x => x.Key));
        Assert.Equal(30, monitoring.SucceededJobs(0, 10).Single(x => x.Key == succeeded1).Value?.TotalDuration);
        Assert.True(monitoring.SucceededJobs(0, 10)[1].Value?.InSucceededState);

        FailedJobDto? failure = Assert.Single(monitoring.FailedJobs(0, 10)).Value;
        Assert.Equal("Broken", failure?.ExceptionMessage);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure?.ExceptionType);
        Assert.Equal("Failed hard", failure?.Reason);

        ScheduledJobDto? delayed = Assert.Single(monitoring.ScheduledJobs(0, 10)).Value;
        Assert.InRange(delayed?.EnqueueAt ?? DateTime.MinValue, enqueueAt.AddSeconds(-1), enqueueAt.AddSeconds(1));

        StatisticsDto statistics = monitoring.GetStatistics();
        Assert.Equal(1, statistics.Failed);
        Assert.Equal(1, statistics.Scheduled);
        Assert.Equal(2, statistics.Succeeded);
        Assert.Equal(1, statistics.Recurring);
    }

    [Fact]
    public void Monitoring_JobDetails_ShowTheHistoryNewestFirst_AndTheParameters()
    {
        string id = CreateJob("x");
        Write(x => x.SetJobState(id, new ScheduledState(TimeSpan.FromHours(1))));
        Write(x => x.SetJobState(id, new EnqueuedState("critical")));

        JobDetailsDto details = Storage.GetMonitoringApi().JobDetails(id);

        Assert.Equal([EnqueuedState.StateName, ScheduledState.StateName], details?.History.Select(static x => x.StateName));
        Assert.Equal("critical", details?.History[0].Data["Queue"]);
        Assert.Equal("en-US", details?.Properties["Culture"]);
        Assert.Equal(nameof(ConformanceJobs.Record), details?.Job?.Method.Name);
    }

    [Fact]
    public void Monitoring_AwaitingJobs_ShowTheirParentsState()
    {
        Assert.SkipUnless(Storage.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs), "The storage doesn't list awaiting jobs.");
        string parent = CreateJob("parent");
        string child = CreateJob("child");
        Write(x =>
        {
            x.SetJobState(parent, new ScheduledState(TimeSpan.FromHours(1)));
            x.SetJobState(child, new AwaitingState(parent));
        });

        JobStorageMonitor monitoring = (JobStorageMonitor)Storage.GetMonitoringApi();
        AwaitingJobDto? awaiting = Assert.Single(monitoring.AwaitingJobs(0, 10)).Value;

        Assert.Equal(ScheduledState.StateName, awaiting?.ParentStateName);
        Assert.Equal(1, monitoring.AwaitingCount());
    }

    [Fact]
    public void Monitoring_AwaitingJobs_ListContinuationsOfTheSameParent_AndPendingJobsCanBeDeleted()
    {
        Assert.SkipUnless(Storage.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs), "The storage doesn't list awaiting jobs.");
        string parent = CreateJob("parent");
        string[] children = [CreateJob("first"), CreateJob("second"), CreateJob("third")];
        Write(x =>
        {
            x.SetJobState(parent, new ScheduledState(TimeSpan.FromHours(1)));
            foreach (string child in children) x.SetJobState(child, new AwaitingState(parent));
        });

        JobStorageMonitor monitoring = (JobStorageMonitor)Storage.GetMonitoringApi();
        JobList<AwaitingJobDto?> awaiting = monitoring.AwaitingJobs(0, 10);

        Assert.Equal(children.OrderBy(static x => x), awaiting.Select(static x => x.Key).OrderBy(static x => x));
        Assert.All(awaiting, static x => Assert.Equal(ScheduledState.StateName, x.Value?.ParentStateName));
        Assert.Equal(4, Storage.GetPendingJobs().Count);
    }

    [Fact]
    public void Monitoring_Timelines_ReadTheDailyAndHourlyCounters()
    {
        Assert.SkipUnless(TimelineReadsCountersAtOnce, "The storage aggregates counters before the graphs read them.");
        DateTime now = JobClock.UtcNow;
        Write(x =>
        {
            x.IncrementCounter($"stats:succeeded:{now:yyyy-MM-dd}", TimeSpan.FromDays(1));
            x.IncrementCounter($"stats:failed:{now:yyyy-MM-dd-HH}", TimeSpan.FromDays(1));
        });

        IMonitoringApi monitoring = Storage.GetMonitoringApi();
        IDictionary<DateTime, long> daily = monitoring.SucceededByDatesCount();
        IDictionary<DateTime, long> hourly = monitoring.HourlyFailedJobs();

        Assert.Equal(7, daily.Count);
        Assert.Equal(1, daily[now.Date]);
        Assert.Equal(24, hourly.Count);
        Assert.Equal(1, hourly.OrderByDescending(static x => x.Key).First().Value);
    }

    [Fact]
    public void GetUtcDateTime_IsCloseToNow()
    {
        Assert.SkipUnless(Storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime), "The storage has no clock.");

        DateTime now = ExtendedConnection(x => x.GetUtcDateTime());

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public void Purge_RemovesJobs_ButKeepsRecurringJobs_TheAuditLog_AndDrainMode()
    {
        Assert.SkipUnless(Storage.HasFeature(JobStorageFeatures.Purge), "The storage can't be purged.");
        string id = Enqueue("default");
        Write(x =>
        {
            x.AddToSet("recurring-jobs", "nightly");
            x.SetRangeInHash("recurring-job:nightly", [new("Cron", "0 0 * * *")]);
            x.SetRangeInHash(DrainMode.HashKey, [new("Paused", "1")]);
            x.InsertToList(AuditLog.ListKey, "entry");
            x.InsertToList("other-list", "entry");
            x.AddToSet("other-set", "value");
            x.IncrementCounter("stats:succeeded");
        });

        Storage.Purge(new StoragePurgeOptions());

        Assert.Null(Connection(x => x.GetJobData(id)));
        Assert.Equal(0, Storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Equal(["nightly"], Connection(x => x.GetAllItemsFromSet("recurring-jobs")));
        Assert.NotNull(Connection(x => x.GetAllEntriesFromHash("recurring-job:nightly")));
        Assert.NotNull(Connection(x => x.GetAllEntriesFromHash(DrainMode.HashKey)));
        Assert.Equal(["entry"], ExtendedConnection(x => x.GetAllItemsFromList(AuditLog.ListKey)));
        Assert.Empty(ExtendedConnection(x => x.GetAllItemsFromList("other-list")));
        Assert.Empty(Connection(x => x.GetAllItemsFromSet("other-set")));
        Assert.Equal(0, ExtendedConnection(x => x.GetCounter("stats:succeeded")));
    }

    // End to end

    [Fact]
    public async Task Server_RunsAnEnqueuedJob_ToSucceeded()
    {
        string key = NewKey();
        BackgroundJobClient client = new(Storage);

        string id = client.Enqueue(() => ConformanceJobs.Record(key))!;
        using (CreateServer())
        {
            await WaitForStateAsync(id, SucceededState.StateName);
        }

        Assert.Equal(1, ConformanceJobs.Runs(key));
        Assert.Equal(1, Storage.GetMonitoringApi().SucceededListCount());
    }

    [Fact]
    public async Task Server_RunsADelayedJob_WhenItIsDue()
    {
        string key = NewKey();
        BackgroundJobClient client = new(Storage);

        string id = client.Schedule(() => ConformanceJobs.Record(key), TimeSpan.FromSeconds(1))!;
        Assert.Equal(ScheduledState.StateName, Connection(x => x.GetStateData(id))?.Name);
        using (CreateServer())
        {
            await WaitForStateAsync(id, SucceededState.StateName);
        }

        Assert.Equal(1, ConformanceJobs.Runs(key));
    }

    [Fact]
    public async Task Server_RunsAContinuation_AfterItsParent()
    {
        string key = NewKey();
        BackgroundJobClient client = new(Storage);

        string parent = client.Enqueue(() => ConformanceJobs.Record(key))!;
        string child = client.ContinueJobWith(parent, () => ConformanceJobs.Record(key + ":child"))!;
        Assert.Equal(AwaitingState.StateName, Connection(x => x.GetStateData(child))?.Name);
        using (CreateServer())
        {
            await WaitForStateAsync(child, SucceededState.StateName);
        }

        Assert.Equal(1, ConformanceJobs.Runs(key + ":child"));
    }

    [Fact]
    public async Task Server_MovesAJobThatThrows_ToFailed()
    {
        BackgroundJobClient client = new(Storage);

        string id = client.Enqueue(() => ConformanceJobs.Fail("Broken on purpose"))!;
        using (CreateServer())
        {
            await WaitForStateAsync(id, FailedState.StateName);
        }

        FailedJobDto? failure = Assert.Single(Storage.GetMonitoringApi().FailedJobs(0, 10)).Value;
        Assert.Equal("Broken on purpose", failure?.ExceptionMessage);
    }

    [Fact]
    public async Task Server_RunsATriggeredRecurringJob()
    {
        string key = NewKey();
        RecurringJobManager manager = new(Storage);
        manager.AddOrUpdate("conformance", () => ConformanceJobs.Record(key), Cron.Yearly());

        string? id = manager.TriggerJob("conformance");
        using (CreateServer())
        {
            await WaitForStateAsync(id!, SucceededState.StateName);
        }

        Assert.Equal(1, ConformanceJobs.Runs(key));
        Assert.Equal(1, Storage.GetMonitoringApi().GetStatistics().Recurring);
    }

    [Fact]
    public async Task Server_RunsAChain_ToTheEnd()
    {
        string key = NewKey();

        JobChainResult? chain = JobChain.StartWith(() => ConformanceJobs.Record(key + ":1"))
            .Then(() => ConformanceJobs.Record(key + ":2"))
            .ThenMany(branches => branches.Branch(() => ConformanceJobs.Record(key + ":3a")).Branch(() => ConformanceJobs.Record(key + ":3b")))
            .ThenAfterAll(() => ConformanceJobs.Record(key + ":4"))
            .Enqueue(new BackgroundJobClient(Storage));

        using (CreateServer(workers: 2))
        {
            await WaitUntilAsync(() => Storage.GetChain(chain!.ChainId) is { IsSettled: true }, $"Chain {chain?.ChainId} didn't settle.");
        }

        Assert.Equal(1, ConformanceJobs.Runs(key + ":4"));
        Assert.Equal(1, ConformanceJobs.Runs(key + ":3b"));
    }

    // Helpers

    protected string CreateJob(string argument, DateTime? createdAt = null)
    {
        return Connection(x => x.CreateExpiredJob(
            Job.FromExpression(() => ConformanceJobs.Record(argument)),
            new Dictionary<string, string?> { ["Culture"] = "en-US" },
            createdAt ?? JobClock.UtcNow,
            TimeSpan.FromHours(1)))!; // Storages always create the job
    }

    protected string Enqueue(string queue)
    {
        string id = CreateJob(queue);
        Write(x =>
        {
            x.SetJobState(id, new EnqueuedState(queue));
            x.AddToQueue(queue, id);
        });
        return id;
    }

    protected void Write(Action<IWriteOnlyTransaction> write)
    {
        using IStorageConnection connection = Storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    protected T Connection<T>(Func<IStorageConnection, T> read)
    {
        using IStorageConnection connection = Storage.GetConnection();
        return read(connection);
    }

    protected void Connection(Action<IStorageConnection> act)
    {
        using IStorageConnection connection = Storage.GetConnection();
        act(connection);
    }

    protected T ExtendedConnection<T>(Func<JobStorageConnection, T> read)
    {
        using IStorageConnection connection = Storage.GetConnection();
        return read((JobStorageConnection)connection);
    }

    /// <summary>Makes <paramref name="storage"/> the test's storage; <c>false</c> when there is none.</summary>
    private bool UseStorage(JobStorage? storage)
    {
        if (storage == null) return false;
        _storage = storage;
        return true;
    }

    private string? TryFetch(string[] queues)
    {
        using IStorageConnection connection = Storage.GetConnection();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));
        try
        {
            using IFetchedJob fetched = connection.FetchNextJob(queues, cancellation.Token);
            fetched.Requeue();
            return fetched.JobId;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private BackgroundJobServer CreateServer(int workers = 1)
    {
        return new BackgroundJobServer(
            new BackgroundJobServerOptions { WorkerCount = workers, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) },
            Storage);
    }

    private Task WaitForStateAsync(string id, string state)
    {
        return WaitUntilAsync(() => state.Equals(Connection(x => x.GetStateData(id))?.Name, StringComparison.OrdinalIgnoreCase), $"Job {id} didn't reach {state}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > Timeout) throw new TimeoutException(message);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");
}

/// <summary>Jobs the conformance tests run.</summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public static class ConformanceJobs
{
    private static readonly ConcurrentDictionary<string, int> RunCounts = new(StringComparer.Ordinal);

    public static void Record(string key) => RunCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);

    [AutomaticRetry(Attempts = 0)]
    public static void Fail(string message) => throw new InvalidOperationException(message);

    public static int Runs(string key) => RunCounts.GetValueOrDefault(key);
}
