using Masterly.NightShift.Client;

namespace Masterly.NightShift.Core.Tests;

public sealed class JobChainMaintenanceFacts : IDisposable
{
    private const string CleanupSet = "chains:cleanup";
    private const string WatchSet = "chains:joins";

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<IBackgroundJobClient> _client = new();
    private readonly Dictionary<string, Dictionary<string, string>> _hashes = [];
    private readonly Dictionary<string, List<(double Score, string Value)>> _sets = [];
    private readonly Dictionary<string, StateData> _states = [];
    private readonly HashSet<string> _busyLocks = [];
    private readonly TimeSpan _interval = JobChainMaintenanceProcess.Interval;
    private readonly TimeSpan _busyInterval = JobChainMaintenanceProcess.BusyInterval;
    private double _position;

    public JobChainMaintenanceFacts()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(true);
        _storage.Setup(x => x.GetConnection()).Returns(() => _connection.Object);

        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns((string resource, TimeSpan _) => _busyLocks.Contains(resource) ? throw new DistributedLockTimeoutException(resource) : Mock.Of<IDisposable>());
        _connection.Setup(x => x.GetRangeFromSet(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((string key, int from, int to) => [.. Ordered(key).Skip(from).Take(to - from + 1).Select(static x => x.Value)]);
        _connection.Setup(x => x.GetFirstByLowestScoreFromSet(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns((string key, double from, double to, int count) => [.. Ordered(key).Where(x => x.Score >= from && x.Score <= to).Take(count).Select(static x => x.Value)]);
        _connection.Setup(x => x.GetFirstByLowestScoreFromSet(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>()))
            .Returns((string key, double from, double to) => Ordered(key).Where(x => x.Score >= from && x.Score <= to).Select(static x => x.Value).FirstOrDefault());
        _connection.Setup(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string field) => _hashes.TryGetValue(key, out Dictionary<string, string> hash) ? hash.GetValueOrDefault(field) : null);
        _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>()))
            .Returns((string key) => _hashes.TryGetValue(key, out Dictionary<string, string> hash) ? new Dictionary<string, string>(hash) : null);
        _connection.Setup(x => x.GetHashCount(It.IsAny<string>()))
            .Returns((string key) => _hashes.TryGetValue(key, out Dictionary<string, string> hash) ? hash.Count : 0);
        _connection.Setup(x => x.GetStateData(It.IsAny<string>()))
            .Returns((string jobId) => _states.GetValueOrDefault(jobId));
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(CreateTransaction);

        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
            .Returns((string jobId, IState state, string _) =>
            {
                _states[jobId] = new StateData { Name = state.Name, Reason = state.Reason, Data = new Dictionary<string, string>() };
                return true;
            });
    }

    public void Dispose()
    {
        JobChainMaintenanceProcess.Interval = _interval;
        JobChainMaintenanceProcess.BusyInterval = _busyInterval;
    }

    [Fact]
    public void Maintain_LeavesTheListAlone_ButFinishesInterruptedChanges_OnAStorageWithoutTheExtendedApi()
    {
        // Nothing expires there, so no chain leaves the list; what a retry left to delete is still deleted
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(false);
        List("expired", daysAgo: 40);
        PendingCleanup("first", "{\"reason\":\"r\",\"delete\":[\"old-1\"]}");
        PendingCleanup("second", "{\"reason\":\"r\",\"delete\":[\"old-2\"]}");
        _states["old-1"] = State(AwaitingState.StateName);
        _states["old-2"] = State(AwaitingState.StateName);

        Assert.True(JobChains.Maintain(_storage.Object, _client.Object, ref _position).Ran);

        Assert.Contains("expired", Listed());
        Assert.Equal(DeletedState.StateName, _states["old-1"].Name);
        Assert.Equal(DeletedState.StateName, _states["old-2"].Name);
        Assert.Empty(Set(CleanupSet));
        _connection.Verify(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Maintain_UnlistsChainsBehindAFullPageOfChainsKept_ThatWereCreatedInTheSameSecond()
    {
        DateTime sameSecond = DateTime.UtcNow.AddDays(-90);
        for (int i = 0; i < 120; i++)
        {
            Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(sameSecond), $"kept-{i:000}"));
            _hashes[JobChains.GetKey($"kept-{i:000}")] = new Dictionary<string, string> { ["CreatedAt"] = JobHelper.SerializeDateTime(sameSecond), ["Jobs"] = "{}" };
        }

        // Ordered after the chains kept, in the same second: a page of the list never reaches them
        Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(sameSecond), "z-expired-1"));
        Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(sameSecond), "z-expired-2"));

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Equal(120, Listed().Count);
        Assert.DoesNotContain(Listed(), static x => x.StartsWith("z-expired", StringComparison.Ordinal));
    }

    [Fact]
    public void Maintain_PushesBackAChangeItCanNotFinish_SoTheOthersGetTheirTurn()
    {
        // As many broken entries as one run takes: without a new score they would come up first every time
        for (int i = 0; i < JobChains.MaxCleanupsPerRun; i++)
        {
            PendingCleanup($"broken-{i:00}", "{ not json");
        }

        PendingCleanup("fine", "{\"reason\":\"r\",\"delete\":[\"old\"]}");
        _states["old"] = State(AwaitingState.StateName);

        JobChains.MaintenanceRun first = JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.True(first.MoreToDo);
        Assert.Equal(AwaitingState.StateName, _states["old"].Name);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Equal(DeletedState.StateName, _states["old"].Name);
        Assert.Equal(JobChains.MaxCleanupsPerRun, Set(CleanupSet).Count);
    }

    [Fact]
    public void Maintain_StartsAStepThatWaitsForSeveralSteps_AndWasNeverToldToStart()
    {
        // Both branches succeeded long ago and the step after them still waits: the process that was to start it
        // stopped after the last branch was committed
        WatchedChain("c", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);

        JobChains.MaintenanceRun run = JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.True(run.Ran);
        _client.Verify(x => x.ChangeState("done", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
        _client.Verify(x => x.ChangeState("alert", It.Is<DeletedState>(state => state.Reason == "Continuation condition was not met"), AwaitingState.StateName), Times.Once);

        // Looked at once more later, and then no more
        Assert.Single(Set(WatchSet));
        Assert.True(Set(WatchSet)[0].Score > JobHelper.ToTimestamp(DateTime.UtcNow));

        Set(WatchSet)[0] = (0, "c");
        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Empty(Set(WatchSet));
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public void Maintain_LeavesAStepAlone_ThatTheStepsBeforeItDecidedAMomentAgo()
    {
        WatchedChain("c", TimeSpan.FromSeconds(30), SucceededState.StateName, SucceededState.StateName);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.Single(Set(WatchSet));
    }

    [Fact]
    public void Maintain_LeavesAStepWaiting_BehindABranchThatFailed_AndLooksAgainLater()
    {
        WatchedChain("c", TimeSpan.FromMinutes(20), FailedState.StateName, SucceededState.StateName);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        // The step for every success keeps waiting; the one for a failure is started
        _client.Verify(x => x.ChangeState("done", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        _client.Verify(x => x.ChangeState("alert", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
        Assert.Single(Set(WatchSet));
    }

    [Fact]
    public void Maintain_LooksAgainSoon_AtAChainThatStillRuns_HoweverOldItIs()
    {
        // A branch still runs in a chain created three hours ago: a step that waits for the branches and is never
        // told to start is found within minutes, not hours
        WatchedChain("c", TimeSpan.FromHours(3), ProcessingState.StateName, SucceededState.StateName);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        double next = Assert.Single(Set(WatchSet)).Score;
        Assert.InRange(next, JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(9)), JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(16)));
    }

    [Fact]
    public void Maintain_LooksAgainLater_AtAChainWhereNothingRuns_AndStepsWaitBehindAFailure()
    {
        WatchedChain("c", TimeSpan.FromHours(3), FailedState.StateName, SucceededState.StateName);
        _states["alert"] = Finished(SucceededState.StateName, DateTime.UtcNow.AddHours(-3));

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        double next = Assert.Single(Set(WatchSet)).Score;
        Assert.InRange(next, JobHelper.ToTimestamp(DateTime.UtcNow.AddHours(2.9)), JobHelper.ToTimestamp(DateTime.UtcNow.AddHours(4.1)));
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Maintain_StartsAStep_WhoseBranchesFinishedAtATimeNobodyCanRead()
    {
        // A time that can't be read counts as long ago: the step isn't about to be told for good
        WatchedChain("c", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);
        _states["b"] = new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string> { ["SucceededAt"] = "not a time" } };
        _states["c"] = new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() };

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        _client.Verify(x => x.ChangeState("done", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
    }

    [Fact]
    public void Maintain_StopsWatchingAChain_ThatWasDeleted_AndLooksLaterAtOneThatIsChanging()
    {
        Set(WatchSet).Add((1, "deleted"));
        WatchedChain("busy", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);
        _busyLocks.Add("lock:chain:busy");

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Equal(["busy"], Set(WatchSet).Select(static x => x.Value));
        Assert.True(Set(WatchSet)[0].Score > JobHelper.ToTimestamp(DateTime.UtcNow));
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RedriveLostDecisions_StartsAStepAfterASingleStep_ThatWasNeverToldToStart()
    {
        DateTime longAgo = DateTime.UtcNow.AddMinutes(-20);
        _states["first"] = new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string> { ["SucceededAt"] = JobHelper.SerializeDateTime(longAgo) } };
        _states["second"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingState("first", new EnqueuedState("critical")).SerializeData() };
        _states["third"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingState("second").SerializeData() };
        JobChainInfo info = new("c", 1, longAgo, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "first", SucceededState.StateName, 0, -1) { FinishedAt = longAgo },
            new JobChainStepInfo("1.0", "second", AwaitingState.StateName, 1, 0) { Link = ChainLink.Then, Options = JobContinuationOptions.OnlyOnSucceededState },
            new JobChainStepInfo("2.0", "third", AwaitingState.StateName, 2, 1) { Link = ChainLink.Then, Options = JobContinuationOptions.OnlyOnSucceededState },
        ]);

        Assert.Equal(1, JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info));

        _client.Verify(x => x.ChangeState("second", It.Is<EnqueuedState>(state => state.Queue == "critical"), AwaitingState.StateName), Times.Once);
        _client.Verify(x => x.ChangeState("third", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void RedriveLostDecisions_LeavesAStepAfterASingleStepWaiting_WhenResultsArePassedToContinuations()
    {
        // The filter that starts a continuation hands it its parent's result; started here, it would run without
        DateTime longAgo = DateTime.UtcNow.AddMinutes(-20);
        _states["first"] = new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string> { ["SucceededAt"] = JobHelper.SerializeDateTime(longAgo) } };
        _states["second"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingState("first").SerializeData() };
        JobChainInfo info = new("c", 1, longAgo, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "first", SucceededState.StateName, 0, -1) { FinishedAt = longAgo },
            new JobChainStepInfo("1.0", "second", AwaitingState.StateName, 1, 0) { Link = ChainLink.Then, Options = JobContinuationOptions.OnlyOnSucceededState },
        ]);
        ContinuationsSupportAttribute pushing = new(pushResults: true);
        GlobalJobFilters.Filters.Add(pushing);

        try
        {
            Assert.Equal(0, JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info));
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(pushing);
        }

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RedriveLostDecisions_DecidesFromTheStoredStates_NotFromTheOnesReadBefore()
    {
        // The step left the Awaiting state, or a step it waits for was requeued, since the chain was read
        DateTime longAgo = DateTime.UtcNow.AddMinutes(-20);
        _states["first"] = State(EnqueuedState.StateName);
        _states["second"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingState("first").SerializeData() };
        _states["gone"] = State(EnqueuedState.StateName);
        JobChainInfo info = new("c", 1, longAgo, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "first", SucceededState.StateName, 0, -1) { FinishedAt = longAgo },
            new JobChainStepInfo("1.0", "second", AwaitingState.StateName, 1, 0) { Link = ChainLink.Then, Options = JobContinuationOptions.OnlyOnSucceededState },
            new JobChainStepInfo("1.1", "gone", AwaitingState.StateName, 1, 0) { Link = ChainLink.Then, Options = JobContinuationOptions.OnlyOnSucceededState },
        ]);

        Assert.Equal(0, JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info));

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Process_RunsAgainSooner_WhenARunStoppedAtALimitWithWorkLeft()
    {
        JobChainMaintenanceProcess.Interval = TimeSpan.FromSeconds(30);
        JobChainMaintenanceProcess.BusyInterval = TimeSpan.FromMilliseconds(1);
        for (int i = 0; i < JobChains.MaxCleanupsPerRun; i++)
        {
            PendingCleanup($"chain-{i:00}", "{\"reason\":\"r\",\"delete\":[]}");
        }

        BackgroundProcessContextMock context = new() { Storage = _storage };
        JobChainMaintenanceProcess process = new(new Mock<IBackgroundJobFactory>().Object, new Mock<IBackgroundJobStateChanger>().Object);

        // Returns at once instead of waiting for the next regular run
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        process.Execute(context.Object);

        Assert.Empty(Set(CleanupSet));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Maintain_LeavesTheWorkToTheServerThatHoldsTheLock()
    {
        _busyLocks.Add(JobChains.MaintenanceLockResource);
        List("expired", daysAgo: 40);

        Assert.False(JobChains.Maintain(_storage.Object, _client.Object, ref _position).Ran);

        Assert.Contains("expired", Listed());
    }

    [Fact]
    public void Maintain_UnlistsTheChainsWhoseRecordExpired_AndKeepsTheOnesStillRecorded()
    {
        List("expired-1", daysAgo: 50);
        List("scheduled-far-ahead", daysAgo: 45, recorded: true);
        List("expired-2", daysAgo: 40);
        List("recent", daysAgo: 1, recorded: true);

        Assert.True(JobChains.Maintain(_storage.Object, _client.Object, ref _position).Ran);

        Assert.Equal(["scheduled-far-ahead", "recent"], Listed());
        Assert.Equal(0, _position);
    }

    [Fact]
    public void Maintain_KeepsAYoungChainListed_WhoseRecordTheStorageLost()
    {
        // Its steps still run because it is listed (JobChainStepFilter)
        List("expired", daysAgo: 40);
        List("lost-early", daysAgo: 3);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Equal(["lost-early"], Listed());
    }

    [Fact]
    public void Maintain_WalksTheList_OneChainAtATime_OnAStorageWithoutBatches()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(false);
        List("expired-1", daysAgo: 50);
        List("kept", daysAgo: 45, recorded: true);
        List("expired-2", daysAgo: 40);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Equal(["kept"], Listed());
    }

    [Fact]
    public void Maintain_IsNotStalled_ByAPageOfOldChainsCreatedInTheSameSecond()
    {
        DateTime sameSecond = DateTime.UtcNow.AddDays(-90);
        for (int i = 0; i < 130; i++)
        {
            Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(sameSecond), $"kept-{i:000}"));
            _hashes[JobChains.GetKey($"kept-{i:000}")] = new Dictionary<string, string> { ["CreatedAt"] = JobHelper.SerializeDateTime(sameSecond), ["Jobs"] = "{}" };
        }

        List("expired", daysAgo: 60);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.DoesNotContain("expired", Listed());
        Assert.Equal(130, Listed().Count);
    }

    [Fact]
    public void Maintain_IsNotStalled_ByManyOldChainsStillRecorded()
    {
        for (int i = 0; i < 150; i++)
        {
            List($"kept-{i:000}", daysAgo: 90, recorded: true);
        }

        for (int i = 0; i < 30; i++)
        {
            List($"expired-{i:00}", daysAgo: 60);
        }

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Equal(150, Listed().Count);
        Assert.DoesNotContain(Listed(), static x => x.StartsWith("expired", StringComparison.Ordinal));
    }

    [Fact]
    public void Maintain_ReadsOnlyTheChainsOldEnoughToHaveExpired()
    {
        List("expired", daysAgo: 40);
        List("recent", daysAgo: 2, recorded: true);
        List("newer", daysAgo: 1, recorded: true);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Equal(["recent", "newer"], Listed());
        _connection.Verify(x => x.GetValueFromHash(JobChains.GetKey("recent"), It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.GetValueFromHash(JobChains.GetKey("newer"), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Maintain_LooksAtABoundedNumberOfChains_AndGoesOnFromThereTheNextTime()
    {
        for (int i = 0; i < JobChains.MaxListedPerRun + 200; i++)
        {
            List($"kept-{i:0000}", daysAgo: 90, recorded: true);
        }

        List("expired", daysAgo: 60);

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.True(_position > 0);
        Assert.Contains("expired", Listed());
        _connection.Verify(x => x.GetValueFromHash(It.IsAny<string>(), "CreatedAt"), Times.Exactly(JobChains.MaxListedPerRun));

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.DoesNotContain("expired", Listed());
        Assert.Equal(0, _position);
    }

    [Fact]
    public void Maintain_FinishesWhatAnInterruptedChangeLeftToDo()
    {
        PendingCleanup("c", "{\"reason\":\"Replaced by a retry of the chain\",\"delete\":[\"deep\",\"done\",\"gone\"],\"release\":\"held\"}");
        _states["deep"] = State(AwaitingState.StateName);
        _states["done"] = State(SucceededState.StateName);
        _states["held"] = State(ScheduledState.StateName, "Chain retried from this step");

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        _client.Verify(x => x.ChangeState("deep", It.Is<DeletedState>(state => state.Reason == "Replaced by a retry of the chain"), AwaitingState.StateName), Times.Once);
        _client.Verify(x => x.ChangeState("held", It.IsAny<EnqueuedState>(), ScheduledState.StateName), Times.Once);
        _client.Verify(x => x.ChangeState("done", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(string.Empty, _hashes[JobChains.GetKey("c")]["Cleanup"]);
        Assert.Empty(Set(CleanupSet));
    }

    [Fact]
    public void Maintain_LeavesAStepThatLeftItsHoldByItself()
    {
        PendingCleanup("c", "{\"reason\":\"r\",\"delete\":[],\"release\":\"held\"}");
        _states["held"] = State(ScheduledState.StateName, "Retry attempt 1 of 10");

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(Set(CleanupSet));
    }

    [Fact]
    public void Maintain_KeepsWhatCouldNotBeDone_ForTheNextRun_AndGoesOnWithTheOtherChains()
    {
        PendingCleanup("failing", "{\"reason\":\"r\",\"delete\":[\"stuck\",\"fine\"]}");
        PendingCleanup("other", "{\"reason\":\"r\",\"delete\":[\"other-job\"]}");
        _states["stuck"] = State(AwaitingState.StateName);
        _states["fine"] = State(AwaitingState.StateName);
        _states["other-job"] = State(EnqueuedState.StateName);
        _client.Setup(x => x.ChangeState("stuck", It.IsAny<IState>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));

        Assert.True(JobChains.Maintain(_storage.Object, _client.Object, ref _position).Ran);

        Assert.Contains("\"stuck\"", _hashes[JobChains.GetKey("failing")]["Cleanup"], StringComparison.Ordinal);
        Assert.DoesNotContain("\"fine\"", _hashes[JobChains.GetKey("failing")]["Cleanup"], StringComparison.Ordinal);
        Assert.Equal(["failing"], Set(CleanupSet).Select(static x => x.Value));
        Assert.Equal(DeletedState.StateName, _states["other-job"].Name);
    }

    [Fact]
    public void Maintain_UnlistsAChainWithNothingLeftToDo_OrWithoutRecord()
    {
        PendingCleanup("done", string.Empty);
        Set(CleanupSet).Add((2, "deleted"));

        JobChains.Maintain(_storage.Object, _client.Object, ref _position);

        Assert.Empty(Set(CleanupSet));
    }

    [Fact]
    public void Maintain_LeavesAChainThatIsChangingRightNow()
    {
        PendingCleanup("c", "{\"reason\":\"r\",\"delete\":[\"job\"]}");
        _states["job"] = State(AwaitingState.StateName);
        _busyLocks.Add("lock:chain:c");

        Assert.True(JobChains.Maintain(_storage.Object, _client.Object, ref _position).Ran);

        Assert.Single(Set(CleanupSet));
        Assert.Equal(AwaitingState.StateName, _states["job"].Name);
    }

    [Fact]
    public void Process_NeverThrows_WhenTheStorageFails_AndWaitsForTheNextRun()
    {
        JobChainMaintenanceProcess.Interval = TimeSpan.FromMilliseconds(1);
        BackgroundProcessContextMock context = new();
        context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        context.Storage.Setup(x => x.GetConnection()).Throws(new InvalidOperationException("Storage is down"));
        JobChainMaintenanceProcess process = new(new Mock<IBackgroundJobFactory>().Object, new Mock<IBackgroundJobStateChanger>().Object);

        process.Execute(context.Object);

        context.Storage.Verify(x => x.GetConnection(), Times.Once);
    }

    [Fact]
    public void Process_MaintainsTheChains_OfItsStorage()
    {
        JobChainMaintenanceProcess.Interval = TimeSpan.FromMilliseconds(1);
        BackgroundProcessContextMock context = new() { Storage = _storage };
        List("expired", daysAgo: 40);
        JobChainMaintenanceProcess process = new(new Mock<IBackgroundJobFactory>().Object, new Mock<IBackgroundJobStateChanger>().Object);

        process.Execute(context.Object);

        Assert.Empty(Listed());
    }

    private List<(double Score, string Value)> Set(string key)
    {
        if (!_sets.TryGetValue(key, out List<(double Score, string Value)> set))
        {
            _sets[key] = set = [];
        }

        return set;
    }

    private IEnumerable<(double Score, string Value)> Ordered(string key) => Set(key).OrderBy(static x => x.Score).ThenBy(static x => x.Value, StringComparer.Ordinal);

    private List<string> Listed() => [.. Ordered(JobChains.SetKey).Select(static x => x.Value)];

    private void List(string chainId, int daysAgo, bool recorded = false)
    {
        // Each chain a second after the one before, as the list orders them
        DateTime createdAt = DateTime.UtcNow.AddDays(-daysAgo).AddSeconds(Set(JobChains.SetKey).Count);
        Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(createdAt), chainId));

        if (recorded)
        {
            _hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string> { ["CreatedAt"] = JobHelper.SerializeDateTime(createdAt), ["Jobs"] = "{}" };
        }
    }

    // A recorded chain a → [b, c] → { done after all, alert after any failure } whose branches finished a while
    // ago in the states given, with both steps after them still waiting
    private void WatchedChain(string chainId, TimeSpan finishedAgo, string firstBranch, string secondBranch)
    {
        JobChain chain = JobChain.StartWith(() => JobChainFacts.Step("a"))
            .ThenMany(branches => branches.Branch(() => JobChainFacts.Step("b")).Branch(() => JobChainFacts.Step("c")))
            .ThenAfterAll(() => JobChainFacts.Step("done"))
            .ThenAfterAnyFailed(() => JobChainFacts.Step("alert"));
        DateTime finishedAt = DateTime.UtcNow - finishedAgo;

        _hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(chain),
            ["Jobs"] = "{\"0.0\":\"a\",\"1.0\":\"b\",\"1.1\":\"c\",\"2.0\":\"done\",\"2.1\":\"alert\"}",
            ["CreatedAt"] = JobHelper.SerializeDateTime(finishedAt.AddMinutes(-1)),
        };
        Set(WatchSet).Add((JobHelper.ToTimestamp(finishedAt), chainId));

        _states["a"] = Finished(SucceededState.StateName, finishedAt);
        _states["b"] = Finished(firstBranch, finishedAt);
        _states["c"] = Finished(secondBranch, finishedAt);
        _states["done"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingJobsState(["b", "c"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.All).SerializeData() };
        _states["alert"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingJobsState(["b", "c"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.AnyFailed).SerializeData() };
    }

    private static StateData Finished(string name, DateTime at)
    {
        return new StateData { Name = name, Data = new Dictionary<string, string> { [$"{name}At"] = JobHelper.SerializeDateTime(at) } };
    }

    private void PendingCleanup(string chainId, string cleanup)
    {
        _hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string> { ["Jobs"] = "{}", ["Cleanup"] = cleanup };
        Set(CleanupSet).Add((Set(CleanupSet).Count, chainId));
    }

    private static StateData State(string name, string reason = null)
    {
        return new StateData { Name = name, Reason = reason, Data = new Dictionary<string, string>() };
    }

    private IWriteOnlyTransaction CreateTransaction()
    {
        List<Action> pending = [];
        Mock<JobStorageTransaction> transaction = new();

        transaction.Setup(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string key, string value) => pending.Add(() => Set(key).RemoveAll(x => x.Value == value)));
        transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Callback((string key, string value, double score) => pending.Add(() =>
            {
                Set(key).RemoveAll(x => x.Value == value);
                Set(key).Add((score, value));
            }));
        transaction.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback((string key, IEnumerable<KeyValuePair<string, string>> fields) =>
            {
                List<KeyValuePair<string, string>> copy = [.. fields];
                pending.Add(() =>
                {
                    if (!_hashes.TryGetValue(key, out Dictionary<string, string> hash))
                    {
                        _hashes[key] = hash = [];
                    }

                    foreach (KeyValuePair<string, string> field in copy)
                    {
                        hash[field.Key] = field.Value;
                    }
                });
            });
        transaction.Setup(x => x.Commit()).Callback(() =>
        {
            foreach (Action action in pending)
            {
                action();
            }

            pending.Clear();
        });

        return transaction.Object;
    }
}
