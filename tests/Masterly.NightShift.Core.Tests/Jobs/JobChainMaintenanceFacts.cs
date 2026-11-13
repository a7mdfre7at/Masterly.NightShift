using Masterly.NightShift.Client;

namespace Masterly.NightShift.Core.Tests;

public sealed class JobChainMaintenanceFacts : IDisposable
{
    private const string CleanupSet = "chains:cleanup";
    private const string DueSet = "chains:due";
    private const string ExpirySet = "chains:expiring";
    private const string IndexHash = "chains:index";

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<IBackgroundJobClient> _client = new();
    private readonly Dictionary<string, Dictionary<string, string>> _hashes = [];
    private readonly Dictionary<string, TimeSpan> _timesToLive = [];
    private readonly Dictionary<string, List<(double Score, string Value)>> _sets = [];
    private readonly Dictionary<string, StateData> _states = [];
    private readonly HashSet<string> _busyLocks = [];
    private readonly TimeSpan _interval = JobChainMaintenanceProcess.Interval;
    private readonly TimeSpan _busyInterval = JobChainMaintenanceProcess.BusyInterval;
    private readonly TimeSpan _retention = JobChain.RecordRetention;

    // Whether the process knows that every listed chain has its place among the chains to look at
    private bool _indexed = true;

    public JobChainMaintenanceFacts()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(true);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetSetContains)).Returns(true);
        _storage.Setup(x => x.GetConnection()).Returns(() => _connection.Object);

        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns((string resource, TimeSpan _) => _busyLocks.Contains(resource) ? throw new DistributedLockTimeoutException(resource) : Mock.Of<IDisposable>());
        _connection.Setup(x => x.GetRangeFromSet(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((string key, int from, int to) => [.. Ordered(key).Skip(from).Take(to - from + 1).Select(static x => x.Value)]);
        _connection.Setup(x => x.GetFirstByLowestScoreFromSet(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns((string key, double from, double to, int count) => [.. Ordered(key).Where(x => x.Score >= from && x.Score <= to).Take(count).Select(static x => x.Value)]);
        _connection.Setup(x => x.GetFirstByLowestScoreFromSet(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>()))
            .Returns((string key, double from, double to) => Ordered(key).Where(x => x.Score >= from && x.Score <= to).Select(static x => x.Value).FirstOrDefault());
        _connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>()))
            .Returns((string key) => [.. Set(key).Select(static x => x.Value)]);
        _connection.Setup(x => x.GetSetContains(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string value) => Set(key).Exists(x => x.Value == value));
        _connection.Setup(x => x.GetSetCount(It.IsAny<string>()))
            .Returns((string key) => Set(key).Count);
        _connection.Setup(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string field) => _hashes.TryGetValue(key, out Dictionary<string, string> hash) ? hash.GetValueOrDefault(field) : null);
        _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>()))
            .Returns((string key) => _hashes.TryGetValue(key, out Dictionary<string, string> hash) ? new Dictionary<string, string>(hash) : null);
        _connection.Setup(x => x.GetHashCount(It.IsAny<string>()))
            .Returns((string key) => _hashes.TryGetValue(key, out Dictionary<string, string> hash) ? hash.Count : 0);
        _connection.Setup(x => x.GetHashTtl(It.IsAny<string>()))
            .Returns((string key) => _timesToLive.TryGetValue(key, out TimeSpan timeToLive) ? timeToLive : TimeSpan.FromSeconds(-1));
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
        JobChain.RecordRetention = _retention;
    }

    // What interrupted retries and replacements left to do

    [Fact]
    public void Maintain_FinishesInterruptedChanges_AndLooksAtChains_OnAStorageWithoutTheExtendedApi()
    {
        WithoutTheExtendedApi();
        PendingCleanup("first", "{\"reason\":\"r\",\"delete\":[\"old-1\"]}");
        PendingCleanup("second", "{\"reason\":\"r\",\"delete\":[\"old-2\"]}");
        _states["old-1"] = State(AwaitingState.StateName);
        _states["old-2"] = State(AwaitingState.StateName);
        Line("waiting", TimeSpan.FromMinutes(20), ("a", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))), ("b", Awaiting("a")));

        Assert.True(Maintain().Ran);

        Assert.Equal(DeletedState.StateName, _states["old-1"].Name);
        Assert.Equal(DeletedState.StateName, _states["old-2"].Name);
        Assert.Empty(Set(CleanupSet));
        Assert.Equal(EnqueuedState.StateName, _states["b"].Name);
        _connection.Verify(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.GetHashTtl(It.IsAny<string>()), Times.Never);
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

        JobChains.MaintenanceRun first = Maintain();

        Assert.True(first.MoreToDo);
        Assert.Equal(AwaitingState.StateName, _states["old"].Name);

        Maintain();

        Assert.Equal(DeletedState.StateName, _states["old"].Name);
        Assert.Equal(JobChains.MaxCleanupsPerRun, Set(CleanupSet).Count);
    }

    [Fact]
    public void Maintain_FinishesWhatAnInterruptedChangeLeftToDo()
    {
        PendingCleanup("c", "{\"reason\":\"Replaced by a retry of the chain\",\"delete\":[\"deep\",\"done\",\"gone\"],\"release\":\"held\"}");
        _states["deep"] = State(AwaitingState.StateName);
        _states["done"] = State(SucceededState.StateName);
        _states["held"] = State(ScheduledState.StateName, "Chain retried from this step");

        Maintain();

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

        Maintain();

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

        Assert.True(Maintain().Ran);

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

        Maintain();

        Assert.Empty(Set(CleanupSet));
    }

    [Fact]
    public void Maintain_LeavesAChainThatIsChangingRightNow()
    {
        PendingCleanup("c", "{\"reason\":\"r\",\"delete\":[\"job\"]}");
        _states["job"] = State(AwaitingState.StateName);
        _busyLocks.Add("lock:chain:c");

        Assert.True(Maintain().Ran);

        Assert.Single(Set(CleanupSet));
        Assert.Equal(AwaitingState.StateName, _states["job"].Name);
    }

    // Steps that were never told to start

    [Fact]
    public void Maintain_StartsAStepThatWaitsForSeveralSteps_AndWasNeverToldToStart()
    {
        // Both branches succeeded long ago and the step after them still waits: the process that was to start it
        // stopped after the last branch was committed
        WatchedChain("c", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);

        JobChains.MaintenanceRun run = Maintain();

        Assert.True(run.Ran);
        _client.Verify(x => x.ChangeState("done", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
        _client.Verify(x => x.ChangeState("alert", It.Is<DeletedState>(state => state.Reason == "Continuation condition was not met"), AwaitingState.StateName), Times.Once);

        // Looked at again a few minutes later: it runs once more
        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromMinutes(9)), After(TimeSpan.FromMinutes(16)));

        // Then no step waits any more: nothing is started twice, and the chain is looked at only for its record
        Set(DueSet)[0] = (0, "c");
        Maintain();

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Exactly(2));
        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromDays(22)), After(TimeSpan.FromDays(23)));
    }

    [Fact]
    public void Maintain_StartsAStepAfterASingleStep_ThatWasNeverToldToStart_InAChainWithoutAnyGroup()
    {
        // A chain of single steps is watched like any other: the process that finished the first step stopped
        // before it started the second
        Line("c", TimeSpan.FromMinutes(30),
            ("a", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))),
            ("b", Awaiting("a")),
            ("c", Awaiting("b")));

        Maintain();

        _client.Verify(x => x.ChangeState("b", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
        _client.Verify(x => x.ChangeState("c", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromMinutes(9)), After(TimeSpan.FromMinutes(16)));
    }

    [Fact]
    public void Maintain_LeavesAStepAlone_ThatTheStepsBeforeItDecidedAMomentAgo()
    {
        WatchedChain("c", TimeSpan.FromSeconds(30), SucceededState.StateName, SucceededState.StateName);

        Maintain();

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromMinutes(9)), After(TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void Maintain_LeavesAStepWaiting_BehindABranchThatFailed_AndLooksAgainLater()
    {
        WatchedChain("c", TimeSpan.FromMinutes(20), FailedState.StateName, SucceededState.StateName);

        Maintain();

        // The step for every success keeps waiting; the one for a failure is started
        _client.Verify(x => x.ChangeState("done", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        _client.Verify(x => x.ChangeState("alert", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
        Assert.Single(Set(DueSet));
    }

    [Fact]
    public void Maintain_LooksAgainSoon_AtAChainThatStillRuns_HoweverOldItIs()
    {
        // A branch still runs in a chain created three hours ago: a step that waits for the branches and is never
        // told to start is found within minutes, not hours
        WatchedChain("c", TimeSpan.FromHours(3), ProcessingState.StateName, SucceededState.StateName);

        Maintain();

        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromMinutes(9)), After(TimeSpan.FromMinutes(16)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(72)]
    [InlineData(24 * 20)]
    public void Maintain_LooksLessAndLessOften_AtAChainWhereNothingRuns_AndStepsWaitBehindAFailure(int failedHoursAgo)
    {
        // As long from now as the chain has stood still: a requeue of the failed step brings the look forward
        WatchedChain("c", TimeSpan.FromHours(failedHoursAgo), FailedState.StateName, SucceededState.StateName);
        _states["alert"] = Finished(SucceededState.StateName, TimeSpan.FromHours(failedHoursAgo));

        Maintain();

        Assert.InRange(
            Assert.Single(Set(DueSet)).Score,
            After(TimeSpan.FromHours(failedHoursAgo) - TimeSpan.FromMinutes(5)),
            After(TimeSpan.FromHours(failedHoursAgo) + TimeSpan.FromMinutes(5)));
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Maintain_LooksAtAChainWhoseStepIsScheduled_AMomentAfterTheStepIsDue_AtTheLatest()
    {
        // Created ten days ago and scheduled for in three days: nothing of it moves before, and its second step
        // must not wait ten days for a server should it never be told to start
        Line("c", TimeSpan.FromDays(10), ("a", Scheduled(TimeSpan.FromDays(3))), ("b", Awaiting("a")));

        Maintain();

        Assert.InRange(
            Assert.Single(Set(DueSet)).Score,
            After(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(9)),
            After(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void Maintain_LooksSoon_AtAChainWhoseScheduledStepIsDue()
    {
        Line("c", TimeSpan.FromDays(10), ("a", Scheduled(TimeSpan.FromSeconds(-30))), ("b", Awaiting("a")));

        Maintain();

        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromMinutes(9)), After(TimeSpan.FromMinutes(16)));
    }

    [Fact]
    public void Maintain_StartsAStep_WhoseBranchesFinishedAtATimeNobodyCanRead()
    {
        // A time that can't be read counts as long ago: the step isn't about to be told for good
        WatchedChain("c", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);
        _states["b"] = new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string> { ["SucceededAt"] = "not a time" } };
        _states["c"] = new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() };

        Maintain();

        _client.Verify(x => x.ChangeState("done", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
    }

    [Fact]
    public void Maintain_ForgetsAChainThatWasDeleted_AndLooksLaterAtOneThatIsChanging()
    {
        Set(DueSet).Add((1, "deleted"));
        WatchedChain("busy", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);
        _busyLocks.Add("lock:chain:busy");

        Maintain();

        Assert.Equal(["busy"], Set(DueSet).Select(static x => x.Value));
        Assert.InRange(Set(DueSet)[0].Score, After(TimeSpan.FromMinutes(4)), After(TimeSpan.FromMinutes(6)));
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Maintain_LooksLater_AtAChainItCanNotRead_AndGoesOnWithTheOthers()
    {
        Set(DueSet).Add((1, "broken"));
        Set(JobChains.SetKey).Add((1, "broken"));
        _hashes[JobChains.GetKey("broken")] = new Dictionary<string, string> { ["Definition"] = "{ not json", ["Jobs"] = "{\"0.0\":\"x\"}" };
        WatchedChain("c", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);

        Assert.True(Maintain().Ran);

        Assert.InRange(Set(DueSet).Single(static x => x.Value == "broken").Score, After(TimeSpan.FromMinutes(14)), After(TimeSpan.FromMinutes(16)));
        Assert.Contains("broken", Listed());
        _client.Verify(x => x.ChangeState("done", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
    }

    // The record of a chain

    [Fact]
    public void Maintain_KeepsTheRecord_OfAChainWithAStepThatFailedForGood_AndLooksAgainBeforeItExpires()
    {
        // Nobody touched the chain for 27 days: its record was to expire in three, with the failed job still there
        WatchedChain("c", TimeSpan.FromDays(27), FailedState.StateName, SucceededState.StateName);
        _states["alert"] = Finished(SucceededState.StateName, TimeSpan.FromDays(27));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromDays(3);

        Maintain();

        Assert.InRange(_timesToLive[JobChains.GetKey("c")], TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));
        Assert.Contains("c", Listed());

        // A quarter of the retention before the record expires, however long the chain has stood still
        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromDays(22.4)), After(TimeSpan.FromDays(22.6)));
        Assert.Empty(Set(ExpirySet));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Enqueued")]
    [InlineData("Processing")]
    [InlineData("Scheduled")]
    public void Maintain_KeepsTheRecord_WhileTheJobOfAStepExists_InAStateOtherThanSucceededOrDeleted(string state)
    {
        Line("c", TimeSpan.FromDays(40), ("a", Finished(SucceededState.StateName, TimeSpan.FromDays(40))), ("b", Finished(state, TimeSpan.FromDays(29))));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromHours(2);

        Maintain();

        Assert.InRange(_timesToLive[JobChains.GetKey("c")], TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));
        Assert.Single(Set(DueSet));
        Assert.Contains("c", Listed());
    }

    [Fact]
    public void Maintain_KeepsTheRecord_ForTheTimeThisProcessIsSetTo()
    {
        JobChain.RecordRetention = TimeSpan.FromDays(4);
        Line("c", TimeSpan.FromDays(40), ("a", Finished(FailedState.StateName, TimeSpan.FromDays(40))));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromHours(2);

        Maintain();

        Assert.InRange(_timesToLive[JobChains.GetKey("c")], TimeSpan.FromDays(3.9), TimeSpan.FromDays(4));
        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromDays(2.9)), After(TimeSpan.FromDays(3.1)));
    }

    [Fact]
    public void Maintain_NeverShortensARecord_ThatAProcessSetToKeepRecordsLongerWrote()
    {
        JobChain.RecordRetention = TimeSpan.FromDays(4);
        Line("c", TimeSpan.FromDays(40), ("a", Finished(FailedState.StateName, TimeSpan.FromDays(40))));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromDays(60);

        Maintain();

        Assert.Equal(TimeSpan.FromDays(60), _timesToLive[JobChains.GetKey("c")]);
        Assert.InRange(Assert.Single(Set(DueSet)).Score, After(TimeSpan.FromDays(58.9)), After(TimeSpan.FromDays(59.1)));
    }

    [Fact]
    public void Maintain_KeepsTheRecord_UntilAfterAStepScheduledFarAheadIsDue()
    {
        Line("c", TimeSpan.FromDays(40), ("a", Scheduled(TimeSpan.FromDays(60))), ("b", Awaiting("a")));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromDays(10);

        Maintain();

        Assert.InRange(_timesToLive[JobChains.GetKey("c")], TimeSpan.FromDays(89.9), TimeSpan.FromDays(90));
    }

    [Fact]
    public void Maintain_LeavesAChainToExpire_OnceNothingOfItIsLeftToRun_AndUnlistsItWhenItsRecordExpired()
    {
        Line("c", TimeSpan.FromMinutes(30),
            ("a", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))),
            ("b", Finished(DeletedState.StateName, TimeSpan.FromMinutes(20))),
            ("gone", null));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromDays(29);

        Maintain();

        // Its record isn't kept longer, and no server looks at it before the record expires
        Assert.Equal(TimeSpan.FromDays(29), _timesToLive[JobChains.GetKey("c")]);
        Assert.Empty(Set(DueSet));
        Assert.InRange(Assert.Single(Set(ExpirySet)).Score, After(TimeSpan.FromDays(29)), After(TimeSpan.FromDays(29) + TimeSpan.FromMinutes(2)));
        Assert.Contains("c", Listed());

        // The record expired
        _hashes.Remove(JobChains.GetKey("c"));
        Set(ExpirySet)[0] = (0, "c");
        Maintain();

        Assert.Empty(Listed());
        Assert.Empty(Set(ExpirySet));
        Assert.Empty(Set(DueSet));
    }

    [Fact]
    public void Maintain_UnlistsAChainLeftToExpire_WhoseRecordTheStorageStillHoldsAfterItsTime()
    {
        // SQL Server removes expired records now and then: one past its time is as good as gone
        Line("c", TimeSpan.FromDays(31), ("a", Finished(SucceededState.StateName, TimeSpan.FromDays(31))));
        Set(DueSet).Clear();
        Set(ExpirySet).Add((1, "c"));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromSeconds(-90);

        Maintain();

        Assert.Empty(Listed());
        Assert.Empty(Set(ExpirySet));
    }

    [Fact]
    public void Maintain_GivesARecordThatDoesNotExpire_ATimeToLive_OnceNothingOfItsChainIsLeftToRun()
    {
        // A record written without a time to live would stay for good, listed or not
        Line("c", TimeSpan.FromDays(2), ("a", Finished(SucceededState.StateName, TimeSpan.FromDays(2))));

        Maintain();

        Assert.InRange(_timesToLive[JobChains.GetKey("c")], TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));
        Assert.InRange(Assert.Single(Set(ExpirySet)).Score, After(TimeSpan.FromDays(30)), After(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(2)));
        Assert.Empty(Set(DueSet));
        Assert.Contains("c", Listed());
    }

    [Fact]
    public void Maintain_UnlistsAChain_WhoseRecordExpiredWhileItWasLookedAt()
    {
        // The storage removes the record between the read of the chain and the read of its time to live, and then
        // answers as for a record that doesn't expire: the chain isn't kept for it
        Line("c", TimeSpan.FromDays(31), ("a", Finished(SucceededState.StateName, TimeSpan.FromDays(31))));
        Set(DueSet).Clear();
        Set(ExpirySet).Add((1, "c"));
        _connection.Setup(x => x.GetHashTtl(JobChains.GetKey("c")))
            .Returns(() =>
            {
                _hashes.Remove(JobChains.GetKey("c"));
                return TimeSpan.FromSeconds(-1);
            });

        Maintain();

        Assert.Empty(Listed());
        Assert.Empty(Set(ExpirySet));
        Assert.Empty(Set(DueSet));
    }

    [Fact]
    public void Maintain_LooksAgainAtAChainLeftToExpire_WhenAStepOfItRunsAgain()
    {
        // Somebody requeued a step after the chain had finished, and it failed for good: the record is kept
        Line("c", TimeSpan.FromDays(31), ("a", Finished(FailedState.StateName, TimeSpan.FromHours(1))));
        Set(DueSet).Clear();
        Set(ExpirySet).Add((1, "c"));
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromMinutes(5);

        Maintain();

        Assert.Contains("c", Listed());
        Assert.Empty(Set(ExpirySet));
        Assert.Single(Set(DueSet));
        Assert.InRange(_timesToLive[JobChains.GetKey("c")], TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));
    }

    [Fact]
    public void Maintain_PutsAChainBackInTheList_WhoseRecordItKeeps_AfterTheRecordHadNoTimeLeft()
    {
        // Left to expire, and taken out of the list while the storage still held its record; then a step of it
        // was requeued and failed for good
        Line("c", TimeSpan.FromDays(31), ("a", Finished(FailedState.StateName, TimeSpan.FromMinutes(10))));
        Set(JobChains.SetKey).Clear();
        _timesToLive[JobChains.GetKey("c")] = TimeSpan.FromSeconds(-90);

        Maintain();

        Assert.Equal(["c"], Listed());
        Assert.InRange(Set(JobChains.SetKey)[0].Score, After(TimeSpan.FromDays(-31) - TimeSpan.FromMinutes(1)), After(TimeSpan.FromDays(-31) + TimeSpan.FromMinutes(1)));
        Assert.InRange(_timesToLive[JobChains.GetKey("c")], TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));
        Assert.Single(Set(DueSet));
    }

    [Fact]
    public void Maintain_KeepsAChainListed_WhoseRecordTheStorageLost_ForAsLongAsARecordIsKept()
    {
        // Its steps still run because it is listed (JobChainStepFilter)
        Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(DateTime.UtcNow.AddDays(-3)), "lost-early"));
        Set(DueSet).Add((1, "lost-early"));

        Maintain();

        Assert.Equal(["lost-early"], Listed());
        Assert.Empty(Set(DueSet));
        Assert.InRange(Assert.Single(Set(ExpirySet)).Score, After(TimeSpan.FromDays(29.9)), After(TimeSpan.FromDays(30.1)));

        Set(ExpirySet)[0] = (0, "lost-early");
        Maintain();

        Assert.Empty(Listed());
        Assert.Empty(Set(ExpirySet));
    }

    [Fact]
    public void Maintain_StopsLookingAtAChain_OnceNoStepWaits_OnAStorageWhoseRecordsNeverExpire()
    {
        WithoutTheExtendedApi();
        Line("failed", TimeSpan.FromHours(3), ("a", Finished(SucceededState.StateName, TimeSpan.FromHours(3))), ("b", Finished(FailedState.StateName, TimeSpan.FromHours(3))));
        Line("waiting", TimeSpan.FromHours(3), ("c", Finished(FailedState.StateName, TimeSpan.FromHours(3))), ("d", Awaiting("c")));
        Set(DueSet).Add((2, "deleted"));

        Maintain();

        // The chain with a step still waiting is looked at again; the others are listed for as long as they exist
        Assert.Equal(["waiting"], Set(DueSet).Select(static x => x.Value));
        Assert.Equal(["failed", "waiting"], Listed().Order(StringComparer.Ordinal));
        Assert.Empty(Set(ExpirySet));
    }

    // What a run costs

    [Fact]
    public void Maintain_ReadsOnlyTheChainsThatAreDue_HoweverManyAreKept()
    {
        // Thousands of chains with a failed step, kept for months: none of them is read before its turn
        for (int i = 0; i < 3000; i++)
        {
            string chainId = $"kept-{i:0000}";
            Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(DateTime.UtcNow.AddDays(-200)), chainId));
            Set(DueSet).Add((After(TimeSpan.FromDays(1 + (i % 20))), chainId));
            _hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string> { ["Jobs"] = "{}" };
        }

        Line("due", TimeSpan.FromDays(200), ("a", Finished(FailedState.StateName, TimeSpan.FromDays(200))));

        JobChains.MaintenanceRun run = Maintain();

        Assert.True(run.Ran);
        Assert.False(run.MoreToDo);
        Assert.Equal(3001, Listed().Count);
        _connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Once);
        _connection.Verify(x => x.GetAllEntriesFromHash(JobChains.GetKey("due")), Times.Once);
        _connection.Verify(x => x.GetHashTtl(It.IsAny<string>()), Times.Once);
        _connection.Verify(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.GetRangeFromSet(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _connection.Verify(x => x.GetAllItemsFromSet(It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.GetSetCount(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Maintain_LooksAtABoundedNumberOfChains_AndGoesOnTheNextTime()
    {
        for (int i = 0; i < JobChains.MaxLooksPerRun + 50; i++)
        {
            Set(DueSet).Add((1 + i, $"deleted-{i:0000}"));
        }

        JobChains.MaintenanceRun first = Maintain();

        Assert.True(first.MoreToDo);
        Assert.Equal(50, Set(DueSet).Count);
        _connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Exactly(JobChains.MaxLooksPerRun));

        JobChains.MaintenanceRun second = Maintain();

        Assert.False(second.MoreToDo);
        Assert.Empty(Set(DueSet));
    }

    [Fact]
    public void Maintain_LooksAtTheChains_OneAtATime_OnAStorageWithoutBatches()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(false);
        Line("first", TimeSpan.FromMinutes(30), ("a", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))), ("b", Awaiting("a")));
        Line("second", TimeSpan.FromMinutes(30), ("c", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))), ("d", Awaiting("c")));
        Set(DueSet).Add((3, "deleted"));

        Maintain();

        Assert.Equal(EnqueuedState.StateName, _states["b"].Name);
        Assert.Equal(EnqueuedState.StateName, _states["d"].Name);
        Assert.Equal(["first", "second"], Set(DueSet).Select(static x => x.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Maintain_StopsLooking_WhenTheServerStops()
    {
        Line("c", TimeSpan.FromMinutes(30), ("a", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))), ("b", Awaiting("a")));
        using CancellationTokenSource stopping = new();
        stopping.Cancel();

        JobChains.MaintenanceRun run = JobChains.Maintain(_storage.Object, _client.Object, ref _indexed, stopping.Token);

        Assert.True(run.Ran);
        Assert.False(run.MoreToDo);
        Assert.Equal(AwaitingState.StateName, _states["b"].Name);
        Assert.Equal(1, Assert.Single(Set(DueSet)).Score);
    }

    [Fact]
    public void Maintain_LeavesTheWorkToTheServerThatHoldsTheLock()
    {
        _busyLocks.Add(JobChains.MaintenanceLockResource);
        Line("c", TimeSpan.FromMinutes(30), ("a", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))), ("b", Awaiting("a")));

        Assert.False(Maintain().Ran);

        Assert.Equal(AwaitingState.StateName, _states["b"].Name);
        Assert.Equal(1, Assert.Single(Set(DueSet)).Score);
    }

    // The chains listed by a build that kept no index of them

    [Fact]
    public void Maintain_GivesTheChainsListedByAnEarlierBuild_ALook_AndOnlyOnce()
    {
        _indexed = false;
        Line("failed", TimeSpan.FromDays(3), ("a", Finished(FailedState.StateName, TimeSpan.FromDays(3))), ("b", Awaiting("a")));
        Line("finished", TimeSpan.FromDays(2), ("c", Finished(SucceededState.StateName, TimeSpan.FromDays(2))));
        Set(DueSet).Clear();
        _timesToLive[JobChains.GetKey("failed")] = TimeSpan.FromDays(27);
        _timesToLive[JobChains.GetKey("finished")] = TimeSpan.FromDays(28);

        // One the earlier build watched for its steps that wait for several, and one this build recorded
        Set("chains:joins").Add((1, "failed"));
        Line("recorded", TimeSpan.FromMinutes(1), ("d", State(EnqueuedState.StateName)), ("e", Awaiting("d")));
        double recordedAt = After(TimeSpan.FromMinutes(4));
        Set(DueSet)[0] = (recordedAt, "recorded");

        JobChains.MaintenanceRun run = Maintain();

        Assert.True(_indexed);
        Assert.False(run.MoreToDo);
        Assert.Equal("done", _hashes[IndexHash]["Listed"]);
        Assert.Empty(Set("chains:joins"));

        // Each was looked at in the same run, and has its place; the one that had it kept it
        Assert.Equal(["failed", "recorded"], Set(DueSet).Select(static x => x.Value).Order(StringComparer.Ordinal));
        Assert.Equal(["finished"], Set(ExpirySet).Select(static x => x.Value));
        Assert.Equal(recordedAt, Set(DueSet).Single(static x => x.Value == "recorded").Score);
        _connection.Verify(x => x.GetRangeFromSet(JobChains.SetKey, It.IsAny<int>(), It.IsAny<int>()), Times.Once);

        // A server that starts later reads that it is done, and the list no more
        _indexed = false;
        Maintain();

        Assert.True(_indexed);
        _connection.Verify(x => x.GetRangeFromSet(JobChains.SetKey, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
        _connection.Verify(x => x.GetSetCount(JobChains.SetKey), Times.Once);
    }

    [Fact]
    public void Maintain_GoesThroughTheListOfAnEarlierBuild_ABoundedNumberOfChainsARun()
    {
        _indexed = false;
        for (int i = 0; i < JobChains.MaxIndexedPerRun + 20; i++)
        {
            Set(JobChains.SetKey).Add((1000 + i, $"old-{i:0000}"));
        }

        JobChains.MaintenanceRun first = Maintain();

        // The newest first; how far it came is in the storage, for whichever server runs next
        Assert.True(first.MoreToDo);
        Assert.False(_indexed);
        Assert.Equal("20", _hashes[IndexHash]["Listed"]);
        Assert.Equal(JobChains.MaxIndexedPerRun, Set(DueSet).Count + Set(ExpirySet).Count);
        Assert.DoesNotContain(Set(DueSet).Concat(Set(ExpirySet)), static x => x.Value == "old-0000");

        bool otherServer = false;
        JobChains.Maintain(_storage.Object, _client.Object, ref otherServer, TestContext.Current.CancellationToken);

        Assert.True(otherServer);
        Assert.Equal("done", _hashes[IndexHash]["Listed"]);
        Assert.Equal(JobChains.MaxIndexedPerRun + 20, Set(DueSet).Count + Set(ExpirySet).Count);
    }

    [Fact]
    public void Maintain_GivesTheChainsListedByAnEarlierBuild_ALook_OnAStorageWithoutRanges()
    {
        WithoutTheExtendedApi();
        _indexed = false;
        Line("old", TimeSpan.FromDays(3), ("a", Finished(FailedState.StateName, TimeSpan.FromDays(3))), ("b", Awaiting("a")));
        Set(DueSet).Clear();
        Line("recorded", TimeSpan.FromMinutes(1), ("d", State(EnqueuedState.StateName)), ("e", Awaiting("d")));
        double recordedAt = After(TimeSpan.FromMinutes(4));
        Set(DueSet)[0] = (recordedAt, "recorded");

        Maintain();

        Assert.True(_indexed);
        Assert.Equal("done", _hashes[IndexHash]["Listed"]);
        Assert.Equal(["old", "recorded"], Set(DueSet).Select(static x => x.Value).Order(StringComparer.Ordinal));
        Assert.Equal(recordedAt, Set(DueSet).Single(static x => x.Value == "recorded").Score);
    }

    [Fact]
    public void Maintain_GoesThroughTheListAgain_WhenItCouldNotBeRead_AndStillLooksAtTheChainsThatAreDue()
    {
        _indexed = false;
        Line("c", TimeSpan.FromMinutes(30), ("a", Finished(SucceededState.StateName, TimeSpan.FromMinutes(20))), ("b", Awaiting("a")));
        _connection.Setup(x => x.GetSetCount(JobChains.SetKey)).Throws(new InvalidOperationException("Storage is down"));

        JobChains.MaintenanceRun run = Maintain();

        Assert.True(run.Ran);
        Assert.False(_indexed);
        Assert.False(_hashes.ContainsKey(IndexHash));
        Assert.Equal(EnqueuedState.StateName, _states["b"].Name);
    }

    // Steps decided again

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
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void RedriveLostDecisions_HandsTheParentsResultToAStepAfterASingleStep_WhenResultsArePassedToContinuations()
    {
        // The filter that starts a continuation hands it its parent's result: so does the server that starts it later
        JobChainInfo info = LineOfTwo(new StateData
        {
            Name = SucceededState.StateName,
            Data = new Dictionary<string, string> { ["SucceededAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-20)), ["Result"] = "42" },
        });
        List<string> calls = [];
        _connection.Setup(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string jobId, string name, string value) => calls.Add($"{jobId}.{name}={value}"));
        _client.Setup(x => x.ChangeState("second", It.IsAny<IState>(), It.IsAny<string>()))
            .Callback((string jobId, IState state, string _) => calls.Add($"{jobId}:{state.Name}"))
            .Returns(true);

        Assert.Equal(1, PassingResults(() => JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info)));

        // Before the step is started, so it never runs without
        Assert.Equal(["second.AntecedentResult=42", "second:Enqueued"], calls);
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void RedriveLostDecisions_StartsAStepWhoseParentReturnedNothing_WithoutAResult()
    {
        JobChainInfo info = LineOfTwo(Finished(SucceededState.StateName, TimeSpan.FromMinutes(20)));

        Assert.Equal(1, PassingResults(() => JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info)));

        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _client.Verify(x => x.ChangeState("second", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void RedriveLostDecisions_HandsTheParentsExceptionToAStepThatWaitsForItsFailure()
    {
        StateData failed = Finished(FailedState.StateName, TimeSpan.FromMinutes(20));
        failed.Data["ExceptionType"] = "System.InvalidOperationException";
        failed.Data["ExceptionMessage"] = "Broken";
        JobChainInfo info = LineOfTwo(failed, JobContinuationOptions.OnlyOnFailedState);

        Assert.Equal(1, PassingResults(() => JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info)));

        _connection.Verify(x => x.SetJobParameter("second", "AntecedentException", It.Is<string>(value => value.Contains("Broken", StringComparison.Ordinal))), Times.Once);
        _client.Verify(x => x.ChangeState("second", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void RedriveLostDecisions_HandsTheReasonOfADeletedParentToAStepThatWaitsForIt()
    {
        JobChainInfo info = LineOfTwo(Finished(DeletedState.StateName, TimeSpan.FromMinutes(20)), JobContinuationOptions.OnlyOnDeletedState);

        Assert.Equal(1, PassingResults(() => JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info)));

        _connection.Verify(x => x.SetJobParameter("second", "AntecedentException", JobParameterInjectionFilter.DefaultException), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void RedriveLostDecisions_LeavesAStepWaiting_WhenResultsArePassedToContinuations_AndTheParentsCanNotBeReadAnyMore()
    {
        // Started without its input it would run wrong: it waits for a requeue
        JobChainInfo info = LineOfTwo(new StateData { Name = SucceededState.StateName, Data = null });

        Assert.Equal(0, PassingResults(() => JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info)));

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void RedriveLostDecisions_HandsNothingToAStepThatWaitsForSeveralSteps_AsTheFilterThatStartsItDoesNot()
    {
        WatchedChain("c", TimeSpan.FromMinutes(20), SucceededState.StateName, SucceededState.StateName);
        _states["b"].Data["Result"] = "1";
        _states["c"].Data["Result"] = "2";
        JobChainInfo info = JobChains.ReadSummary(_connection.Object, "c");

        Assert.Equal(2, PassingResults(() => JobChains.RedriveLostDecisions(_client.Object, _connection.Object, info)));

        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _client.Verify(x => x.ChangeState("done", It.IsAny<EnqueuedState>(), AwaitingState.StateName), Times.Once);
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

    // The process

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
    public void Process_MaintainsTheChains_OfItsStorage_AndReadsTheListOnlyUntilItKnowsEveryChainHasItsPlace()
    {
        JobChainMaintenanceProcess.Interval = TimeSpan.FromMilliseconds(1);
        BackgroundProcessContextMock context = new() { Storage = _storage };
        Set(JobChains.SetKey).Add((1, "expired"));
        Set(ExpirySet).Add((1, "expired"));
        JobChainMaintenanceProcess process = new(new Mock<IBackgroundJobFactory>().Object, new Mock<IBackgroundJobStateChanger>().Object);

        process.Execute(context.Object);

        Assert.Empty(Listed());
        Assert.Equal("done", _hashes[IndexHash]["Listed"]);

        process.Execute(context.Object);

        _connection.Verify(x => x.GetValueFromHash(IndexHash, "Listed"), Times.Once);
    }

    private JobChains.MaintenanceRun Maintain() => JobChains.Maintain(_storage.Object, _client.Object, ref _indexed, TestContext.Current.CancellationToken);

    private void WithoutTheExtendedApi()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(false);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetSetContains)).Returns(false);
    }

    // The result of a call made while results are passed to continuations (UseResultsInContinuations)
    private static T PassingResults<T>(Func<T> call)
    {
        ContinuationsSupportAttribute pushing = new(pushResults: true);
        GlobalJobFilters.Filters.Add(pushing);

        try
        {
            return call();
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(pushing);
        }
    }

    private static double After(TimeSpan time) => JobHelper.ToTimestamp(DateTime.UtcNow + time);

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

    // A step "second" that waits for the step "first", which finished twenty minutes ago in the state given
    private JobChainInfo LineOfTwo(StateData first, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
    {
        DateTime longAgo = DateTime.UtcNow.AddMinutes(-20);
        _states["first"] = first;
        _states["second"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingState("first", new EnqueuedState(), options).SerializeData() };

        return new JobChainInfo("c", 1, longAgo, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "first", first.Name, 0, -1) { FinishedAt = longAgo },
            new JobChainStepInfo("1.0", "second", AwaitingState.StateName, 1, 0) { Link = ChainLink.Then, Options = options },
        ]);
    }

    // A recorded and listed chain of single steps, each after the one before, created a while ago and due for a
    // look; a step without state is one whose job expired
    private void Line(string chainId, TimeSpan createdAgo, params (string JobId, StateData State)[] steps)
    {
        JobChain chain = JobChain.StartWith(() => JobChainFacts.Step(steps[0].JobId));
        for (int i = 1; i < steps.Length; i++)
        {
            string name = steps[i].JobId;
            chain.Then(() => JobChainFacts.Step(name));
        }

        DateTime createdAt = DateTime.UtcNow - createdAgo;
        _hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(chain),
            ["Jobs"] = "{" + string.Join(",", steps.Select(static (step, level) => $"\"{level}.0\":\"{step.JobId}\"")) + "}",
            ["CreatedAt"] = JobHelper.SerializeDateTime(createdAt),
        };
        Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(createdAt), chainId));
        Set(DueSet).Add((1, chainId));

        foreach ((string jobId, StateData state) in steps)
        {
            if (state != null)
            {
                _states[jobId] = state;
            }
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
        Set(JobChains.SetKey).Add((JobHelper.ToTimestamp(finishedAt.AddMinutes(-1)), chainId));
        Set(DueSet).Add((JobHelper.ToTimestamp(finishedAt), chainId));

        _states["a"] = Finished(SucceededState.StateName, finishedAgo);
        _states["b"] = Finished(firstBranch, finishedAgo);
        _states["c"] = Finished(secondBranch, finishedAgo);
        _states["done"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingJobsState(["b", "c"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.All).SerializeData() };
        _states["alert"] = new StateData { Name = AwaitingState.StateName, Data = new AwaitingJobsState(["b", "c"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.AnyFailed).SerializeData() };
    }

    private static StateData Finished(string name, TimeSpan ago)
    {
        return new StateData { Name = name, Data = new Dictionary<string, string> { [$"{name}At"] = JobHelper.SerializeDateTime(DateTime.UtcNow - ago) } };
    }

    private static StateData Awaiting(string parentId)
    {
        return new StateData { Name = AwaitingState.StateName, Data = new AwaitingState(parentId).SerializeData() };
    }

    // A step scheduled for a time from now
    private static StateData Scheduled(TimeSpan dueIn)
    {
        return new StateData { Name = ScheduledState.StateName, Data = new ScheduledState(DateTime.UtcNow + dueIn).SerializeData() };
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
        transaction.Setup(x => x.RemoveSet(It.IsAny<string>()))
            .Callback((string key) => pending.Add(() => Set(key).Clear()));
        transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Callback((string key, string value, double score) => pending.Add(() =>
            {
                Set(key).RemoveAll(x => x.Value == value);
                Set(key).Add((score, value));
            }));
        transaction.Setup(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Callback((string key, TimeSpan expireIn) => pending.Add(() => _timesToLive[key] = expireIn));
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
