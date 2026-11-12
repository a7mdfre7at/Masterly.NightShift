namespace Masterly.NightShift.Core.Tests;

public class JobChainFacts
{
    private readonly PolicyStorage _storage = new();
    private readonly Mock<IBackgroundJobClientV2> _client = new();
    private readonly List<(Job Job, IState State, IDictionary<string, object> Parameters)> _created = [];

    public JobChainFacts()
    {
        _client.Setup(x => x.Storage).Returns(_storage.Object);
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job job, IState state, IDictionary<string, object> parameters) =>
            {
                _created.Add((job, state, parameters));
                return $"job-{_created.Count}";
            });
    }

    [Fact]
    public void Builder_CountsTheSteps_AndGroupsConsecutiveJoins()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"))
            .Then(() => Step("b"))
            .ThenMany(branches => branches.Branch(() => Step("c")).Branch<Service>(x => x.Work()))
            .ThenAfterAll(() => Step("e"))
            .ThenAfterAnyFailed<Service>(x => x.WorkAsync());

        Assert.Equal(6, chain.StepCount);
        Assert.Equal(4, chain.Levels.Count);
        Assert.Equal(2, chain.Levels[3].Steps.Count);
        Assert.Equal(2, chain.Levels[3].Parent);
    }

    [Fact]
    public void Then_AfterJoins_ContinuesTheJoins()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"))
            .ThenAfterAll(() => Step("b"))
            .ThenAfterAny(() => Step("c"))
            .Then(() => Step("d"), JobContinuationOptions.OnAnyFinishedState);

        Assert.Equal(3, chain.Levels.Count);
        Assert.Equal(1, chain.Levels[2].Parent);
    }

    [Fact]
    public void ThenMany_ThrowsAnException_ForAnEmptyGroup_OrNull()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"));

        Assert.Throws<ArgumentException>("branches", () => chain.ThenMany(_ => { }));
        Assert.Throws<ArgumentNullException>("branches", () => chain.ThenMany(null));
    }

    [Fact]
    public void Builder_RefusesUndefinedOptions_AndBehaviors()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"));

        Assert.Throws<ArgumentOutOfRangeException>("options", () => chain.Then(() => Step("b"), (JobContinuationOptions)64));
        Assert.Throws<ArgumentOutOfRangeException>("options", () => chain.ThenMany(b => b.Branch(() => Step("b"), (JobContinuationOptions)64)));
        Assert.Throws<ArgumentOutOfRangeException>("behavior", () => chain.OnDelete((ChainDeleteBehavior)5));
        Assert.Equal(ChainDeleteBehavior.Restrict, chain.OnDelete(ChainDeleteBehavior.Restrict).DeleteBehavior);
    }

    [Fact]
    public void Builder_RefusesMoreLevelsThanTheLimit()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"));
        for (int i = 1; i < JobChain.MaxLevels; i++) chain.Then(() => Step("b"));

        Assert.Throws<InvalidOperationException>(() => chain.Then(() => Step("c")));
    }

    [Fact]
    public void Builder_RefusesMoreStepsThanTheLimit()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"))
            .ThenMany(branches =>
            {
                for (int i = 0; i < JobChain.MaxSteps - 1; i++) branches.Branch(() => Step("b"));
            });

        Assert.Throws<InvalidOperationException>(() => chain.ThenAfterAll(() => Step("c")));
    }

    [Fact]
    public void Enqueue_CreatesTheFirstStepEnqueued_AndTheOthersWaitingForTheLevelBefore()
    {
        JobChainResult result = JobChain.StartWith(() => Step("a"))
            .Then(() => Step("b"))
            .ThenMany(branches => branches.Branch(() => Step("c")).Branch(() => Step("d"), JobContinuationOptions.OnAnyFinishedState))
            .ThenAfterAll(() => Step("e"))
            .ThenAfterAnyFailed(() => Step("f"))
            .Enqueue(_client.Object);

        Assert.NotNull(result);
        Assert.Equal("job-1", result.RootId);
        Assert.Equal(["0.0", "1.0", "2.0", "2.1", "3.0", "3.1"], result.Steps.Keys.Order(StringComparer.Ordinal));

        Assert.IsType<EnqueuedState>(_created[0].State);

        AwaitingState then = Assert.IsType<AwaitingState>(_created[1].State);
        Assert.Equal("job-1", then.ParentId);
        Assert.Equal(JobContinuationOptions.OnlyOnSucceededState, then.Options);

        AwaitingState branch = Assert.IsType<AwaitingState>(_created[3].State);
        Assert.Equal("job-2", branch.ParentId);
        Assert.Equal(JobContinuationOptions.OnAnyFinishedState, branch.Options);

        AwaitingJobsState afterAll = Assert.IsType<AwaitingJobsState>(_created[4].State);
        Assert.Equal(["job-3", "job-4"], afterAll.ParentIds);
        Assert.Equal(FanInMode.All, afterAll.Mode);

        AwaitingJobsState afterFailure = Assert.IsType<AwaitingJobsState>(_created[5].State);
        Assert.Equal(["job-3", "job-4"], afterFailure.ParentIds);
        Assert.Equal(FanInMode.AnyFailed, afterFailure.Mode);

        Assert.All(_created, x => Assert.Equal(result.ChainId, x.Parameters[JobChains.ChainIdParameter]));
        Assert.Equal("3.1", _created[5].Parameters[JobChains.ChainStepParameter]);
    }

    [Fact]
    public void Enqueue_RecordsTheChain()
    {
        JobChainResult result = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object);

        Dictionary<string, string> hash = _storage.Hashes[JobChains.GetKey(result!.ChainId)];
        Assert.Equal("1", hash["Generation"]);
        Assert.Equal("Cascade", hash["DeleteBehavior"]);
        Assert.Contains("\"0.0\":\"job-1\"", hash["Jobs"], StringComparison.Ordinal);
        Assert.Contains(result.ChainId, _storage.Sets[JobChains.SetKey]);
    }

    [Fact]
    public void Schedule_CreatesTheFirstStepScheduled_WithTheOptions()
    {
        DateTimeOffset at = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

        JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Schedule(_client.Object, at, new ScheduleOptions { Policy = "weekdays" });

        Assert.Equal(at.UtcDateTime, Assert.IsType<ScheduledState>(_created[0].State).EnqueueAt);
        Assert.Equal("weekdays", _created[0].Parameters[ScheduleOptions.PolicyParameter]);
        Assert.False(_created[1].Parameters.ContainsKey(ScheduleOptions.PolicyParameter));
    }

    [Fact]
    public void Enqueue_ReturnsNull_AndCreatesNothingMore_WhenTheFirstStepIsCanceled()
    {
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>())).Returns((string)null);

        Assert.Null(JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()), Times.Once);
    }

    [Fact]
    public void Enqueue_DeletesTheCreatedSteps_WhenALaterStepIsCanceled()
    {
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingState>(), It.IsAny<IDictionary<string, object>>())).Returns((string)null);
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = EnqueuedState.StateName });
        _client.Setup(x => x.ChangeState("job-1", It.IsAny<DeletedState>(), EnqueuedState.StateName)).Returns(true);

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _client.Verify(x => x.ChangeState("job-1", It.IsAny<DeletedState>(), EnqueuedState.StateName));
    }

    [Fact]
    public void Enqueue_LeavesTheFirstStep_WhenAWorkerTookItBeforeALaterStepFailed()
    {
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingState>(), It.IsAny<IDictionary<string, object>>())).Returns((string)null);
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = ProcessingState.StateName });

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Enqueue_DeletesTheCreatedSteps_WhenTheRecordCanNotBeWritten()
    {
        _storage.Connection.Setup(x => x.CreateWriteTransaction()).Throws(new InvalidOperationException("Storage is down"));
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = EnqueuedState.StateName });
        _storage.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = AwaitingState.StateName });

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _client.Verify(x => x.ChangeState("job-2", It.IsAny<DeletedState>(), AwaitingState.StateName));
        _client.Verify(x => x.ChangeState("job-1", It.IsAny<DeletedState>(), EnqueuedState.StateName));
    }

    [Fact]
    public void Enqueue_ThrowsAnException_ForAClientThatCannotStoreParameters()
    {
        Assert.Throws<NotSupportedException>(() => JobChain.StartWith(() => Step("a")).Enqueue(new Mock<IBackgroundJobClient>().Object));
        Assert.Throws<ArgumentNullException>("client", () => JobChain.StartWith(() => Step("a")).Enqueue(null, null));
    }

    [Fact]
    public void Definition_RoundTripsTheChain()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"))
            .ThenMany(branches => branches.Branch<Service>(x => x.Work()).Branch(() => Step("c"), JobContinuationOptions.OnlyOnFailedState))
            .ThenAfterAllCompleted(() => Step("d"))
            .OnDelete(ChainDeleteBehavior.Restrict);

        JobChain read = JobChains.FromDefinition(JobChains.ToDefinition(chain));

        Assert.Equal(ChainDeleteBehavior.Restrict, read.DeleteBehavior);
        Assert.Equal(chain.StepCount, read.StepCount);
        Assert.Equal(typeof(Service), read.Levels[1].Steps[0].Job.Type);
        Assert.Equal(JobContinuationOptions.OnlyOnFailedState, read.Levels[1].Steps[1].Options);
        Assert.Equal(ChainLink.AfterAllCompleted, read.Levels[2].Steps[0].Link);
        Assert.Equal(["a"], read.Levels[0].Steps[0].Job.Args);
    }

    [Theory]
    [InlineData((int)ChainLink.AfterAnyFailed, JobContinuationOptions.OnlyOnFailedState)]
    [InlineData((int)ChainLink.AfterAnyFailedOrCanceled, JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnDeletedState)]
    [InlineData((int)ChainLink.AfterAllCompleted, JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnDeletedState | JobContinuationOptions.OnlyOnSucceededState)]
    [InlineData((int)ChainLink.AfterAny, JobContinuationOptions.OnlyOnSucceededState)]
    public void StateFor_AJoinAfterASingleStep_IsAContinuationWithTheMatchingOptions(int link, JobContinuationOptions expected)
    {
        IState state = JobChains.StateFor(new ChainStep(Job.FromExpression(() => Step("x")), (ChainLink)link, JobContinuationOptions.OnlyOnSucceededState), ["p"]);

        Assert.Equal(expected, Assert.IsType<AwaitingState>(state).Options);
    }

    [Theory]
    [InlineData(JobContinuationOptions.OnlyOnSucceededState, FanInMode.All, JobContinuationOptions.OnlyOnSucceededState)]
    [InlineData(JobContinuationOptions.OnlyOnDeletedState, FanInMode.All, JobContinuationOptions.OnlyOnDeletedState)]
    [InlineData(JobContinuationOptions.OnlyOnSucceededState | JobContinuationOptions.OnlyOnDeletedState, FanInMode.All, JobContinuationOptions.OnlyOnSucceededState | JobContinuationOptions.OnlyOnDeletedState)]
    [InlineData(JobContinuationOptions.OnlyOnFailedState, FanInMode.AnyFailed, JobContinuationOptions.OnlyOnSucceededState)]
    [InlineData(JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnDeletedState, FanInMode.AnyFailedOrCanceled, JobContinuationOptions.OnlyOnSucceededState)]
    [InlineData(JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnSucceededState, FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState)]
    [InlineData(JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnSucceededState | JobContinuationOptions.OnlyOnDeletedState, FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState)]
    [InlineData(JobContinuationOptions.OnAnyFinishedState, FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState)]
    public void StateFor_AThenAfterAGroup_WaitsAsItsOptionsSay(JobContinuationOptions options, FanInMode mode, JobContinuationOptions fanInOptions)
    {
        IState state = JobChains.StateFor(new ChainStep(Job.FromExpression(() => Step("x")), ChainLink.Then, options), ["p", "q"]);

        AwaitingJobsState awaiting = Assert.IsType<AwaitingJobsState>(state);
        Assert.Equal(mode, awaiting.Mode);
        Assert.Equal(fanInOptions, awaiting.Options);
    }

    [Fact]
    public void Builder_RefusesToWaitForAGroupToStart_ButNotForASingleStep()
    {
        JobChain chain = JobChain.StartWith(() => Step("a")).ThenMany(branches => branches.Branch(() => Step("b")).Branch(() => Step("c")));

        Assert.Throws<ArgumentException>("options", () => chain.Then(() => Step("d"), JobContinuationOptions.OnlyOnProcessingState));
        Assert.Throws<ArgumentException>("branches", () => chain.ThenMany(branches => branches.Branch(() => Step("d"), JobContinuationOptions.OnlyOnProcessingState)));
        Assert.Equal(2, JobChain.StartWith(() => Step("a")).Then(() => Step("b"), JobContinuationOptions.OnlyOnProcessingState).StepCount);
    }

    [Fact]
    public void GetKey_NamesTheRecord()
    {
        Assert.Equal("chain:abc", JobChains.GetKey("abc"));
        Assert.Throws<ArgumentException>("chainId", () => JobChains.GetKey(" "));
    }

    [Fact]
    public void JobChainInfo_IsSettled_OnlyWhenEveryStepFinished()
    {
        JobChainInfo running = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
            [new JobChainStepInfo("0.0", "1", "Succeeded", 0, -1), new JobChainStepInfo("1.0", "2", "Awaiting", 1, 0)]);
        JobChainInfo settled = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
            [new JobChainStepInfo("0.0", "1", "Failed", 0, -1), new JobChainStepInfo("1.0", "2", "Deleted", 1, 0), new JobChainStepInfo("1.1", "3", null, 1, 0)]);

        Assert.False(running.IsSettled);
        Assert.True(settled.IsSettled);
        Assert.Equal(1, settled.FailedCount);
    }

    [Fact]
    public void JobChainInfo_IsSettled_WhenTheOnlyWaitingStepsFollowAFailure()
    {
        JobChainInfo stuck = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
            [new JobChainStepInfo("0.0", "1", "Failed", 0, -1), new JobChainStepInfo("1.0", "2", "Awaiting", 1, 0)]);
        JobChainInfo deciding = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
            [new JobChainStepInfo("0.0", "1", "Succeeded", 0, -1), new JobChainStepInfo("1.0", "2", "Awaiting", 1, 0)]);

        Assert.True(stuck.IsSettled);
        Assert.False(deciding.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsSettled_WhenStepsWaitBehindStepsThatWaitBehindAFailure()
    {
        JobChainInfo line = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "1", "Failed", 0, -1),
            new JobChainStepInfo("1.0", "2", "Awaiting", 1, 0),
            new JobChainStepInfo("2.0", "3", "Awaiting", 2, 1),
            new JobChainStepInfo("3.0", "4", "Awaiting", 3, 2),
        ]);
        JobChainInfo group = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "1", "Succeeded", 0, -1),
            new JobChainStepInfo("1.0", "2", "Failed", 1, 0),
            new JobChainStepInfo("1.1", "3", "Succeeded", 1, 0),
            new JobChainStepInfo("2.0", "4", "Awaiting", 2, 1),
            new JobChainStepInfo("2.1", "5", "Succeeded", 2, 1),
            new JobChainStepInfo("3.0", "6", "Awaiting", 3, 2),
        ]);

        Assert.True(line.IsSettled);
        Assert.True(group.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsSettled_WhenStepsWaitBehindAStepWhoseJobExpired()
    {
        // A failed first step kept for a while (UseFailedJobRetention) and then expired: nothing starts the rest
        JobChainInfo line = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "1", null, 0, -1),
            new JobChainStepInfo("1.0", "2", "Awaiting", 1, 0),
            new JobChainStepInfo("2.0", "3", "Awaiting", 2, 1),
        ]);

        // A sibling still running keeps the chain going
        JobChainInfo running = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "1", "Succeeded", 0, -1),
            new JobChainStepInfo("1.0", "2", null, 1, 0),
            new JobChainStepInfo("1.1", "3", "Processing", 1, 0),
            new JobChainStepInfo("2.0", "4", "Awaiting", 2, 1),
        ]);

        Assert.True(line.IsSettled);
        Assert.False(running.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsNotSettled_WhenTheStepAfterAFailurePathIsAboutToStart()
    {
        // The failure path ran: the step after it waits only until its continuation is enqueued
        JobChainInfo chain = new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "1", "Failed", 0, -1),
            new JobChainStepInfo("1.0", "2", "Succeeded", 1, 0),
            new JobChainStepInfo("2.0", "3", "Awaiting", 2, 1),
        ]);

        Assert.False(chain.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsNotSettled_UntilAFailureJoinRan_AfterABranchFailed()
    {
        // The branch's failure is stored, and the join that waits for a failure is told a moment later
        JobChainInfo starting = Chain(
            Step("0.0", "Succeeded", ChainLink.Root),
            Step("1.0", "Failed", ChainLink.Then),
            Step("1.1", "Succeeded", ChainLink.Then),
            Step("2.0", "Awaiting", ChainLink.AfterAll),
            Step("2.1", "Awaiting", ChainLink.AfterAnyFailed));
        JobChainInfo ran = Chain(
            Step("0.0", "Succeeded", ChainLink.Root),
            Step("1.0", "Failed", ChainLink.Then),
            Step("1.1", "Succeeded", ChainLink.Then),
            Step("2.0", "Awaiting", ChainLink.AfterAll),
            Step("2.1", "Succeeded", ChainLink.AfterAnyFailed));

        Assert.False(starting.IsSettled);
        Assert.True(ran.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsSettled_WhenAJoinForEverySuccessWaitsBehindAFailure()
    {
        JobChainInfo chain = Chain(
            Step("0.0", "Succeeded", ChainLink.Root),
            Step("1.0", "Failed", ChainLink.Then),
            Step("1.1", "Succeeded", ChainLink.Then),
            Step("2.0", "Awaiting", ChainLink.AfterAll));

        Assert.True(chain.IsSettled);
    }

    [Theory]
    [InlineData((int)ChainLink.AfterAnyFailed, "Failed", "Processing", false)]
    [InlineData((int)ChainLink.AfterAnyFailedOrCanceled, "Failed", "Succeeded", false)]
    [InlineData((int)ChainLink.AfterAllCompleted, "Failed", "Succeeded", false)]
    [InlineData((int)ChainLink.AfterAllCompleted, "Failed", "Deleted", false)]
    [InlineData((int)ChainLink.AfterAny, "Failed", "Succeeded", false)]
    [InlineData((int)ChainLink.AfterAny, "Failed", "Failed", true)]
    [InlineData((int)ChainLink.AfterAny, "Failed", "Deleted", true)]
    [InlineData((int)ChainLink.AfterAll, "Failed", "Failed", true)]
    public void JobChainInfo_IsSettled_ForAJoinBehindAFailure_UnlessTheBranchesDecideIt(int link, string first, string second, bool settled)
    {
        JobChainInfo chain = Chain(
            Step("0.0", "Succeeded", ChainLink.Root),
            Step("1.0", first, ChainLink.Then),
            Step("1.1", second, ChainLink.Then),
            Step("2.0", "Awaiting", (ChainLink)link));

        Assert.Equal(settled, chain.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsNotSettled_WhileAJoinBehindAFailureIsAboutToBeSkipped()
    {
        // A deleted branch decides the join for every success: it is skipped, which starts the step after it
        JobChainInfo chain = Chain(
            Step("0.0", "Succeeded", ChainLink.Root),
            Step("1.0", "Failed", ChainLink.Then),
            Step("1.1", "Deleted", ChainLink.Then),
            Step("2.0", "Awaiting", ChainLink.AfterAll),
            Step("3.0", "Awaiting", ChainLink.Then, JobContinuationOptions.OnAnyFinishedState));

        Assert.False(chain.IsSettled);
    }

    [Theory]
    [InlineData((int)ChainLink.Then, JobContinuationOptions.OnlyOnFailedState, false)]
    [InlineData((int)ChainLink.Then, JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnSucceededState, false)]
    [InlineData((int)ChainLink.Then, JobContinuationOptions.OnlyOnSucceededState, true)]
    [InlineData((int)ChainLink.Then, JobContinuationOptions.OnlyOnDeletedState, true)]
    [InlineData((int)ChainLink.Then, JobContinuationOptions.OnAnyFinishedState, true)]
    [InlineData((int)ChainLink.AfterAnyFailed, JobContinuationOptions.OnlyOnSucceededState, false)]
    [InlineData((int)ChainLink.AfterAnyFailedOrCanceled, JobContinuationOptions.OnlyOnSucceededState, false)]
    [InlineData((int)ChainLink.AfterAllCompleted, JobContinuationOptions.OnlyOnSucceededState, false)]
    [InlineData((int)ChainLink.AfterAll, JobContinuationOptions.OnlyOnSucceededState, true)]
    [InlineData((int)ChainLink.AfterAny, JobContinuationOptions.OnlyOnSucceededState, true)]
    public void JobChainInfo_IsSettled_ForAStepAfterASingleFailedStep_UnlessItWaitsForTheFailure(int link, JobContinuationOptions options, bool settled)
    {
        JobChainInfo chain = Chain(
            Step("0.0", "Failed", ChainLink.Root),
            Step("1.0", "Awaiting", (ChainLink)link, options));

        Assert.Equal(settled, chain.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsNotSettled_WhileAThenAfterAGroup_ThatWaitsForAFailure_IsAboutToStart()
    {
        JobChainInfo chain = Chain(
            Step("0.0", "Succeeded", ChainLink.Root),
            Step("1.0", "Failed", ChainLink.Then),
            Step("1.1", "Succeeded", ChainLink.Then),
            Step("2.0", "Awaiting", ChainLink.Then, JobContinuationOptions.OnlyOnFailedState));

        Assert.False(chain.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsSettled_WhenTheStepThatWasToStartWasNeverTold()
    {
        // The branch failed long ago and the join still waits: the process that was to start it stopped. It waits
        // for a requeue or a retry, like the steps behind a failure.
        DateTime longAgo = DateTime.UtcNow - JobChains.DecisionWindow - TimeSpan.FromMinutes(1);
        JobChainInfo lost = Chain(
            Step("0.0", "Succeeded", ChainLink.Root, finishedAt: longAgo),
            Step("1.0", "Failed", ChainLink.Then, finishedAt: longAgo),
            Step("1.1", "Succeeded", ChainLink.Then, finishedAt: longAgo),
            Step("2.0", "Awaiting", ChainLink.AfterAnyFailed));
        JobChainInfo recent = Chain(
            Step("0.0", "Succeeded", ChainLink.Root, finishedAt: longAgo),
            Step("1.0", "Failed", ChainLink.Then, finishedAt: longAgo),
            Step("1.1", "Succeeded", ChainLink.Then, finishedAt: DateTime.UtcNow),
            Step("2.0", "Awaiting", ChainLink.AfterAnyFailed));

        Assert.True(lost.IsSettled);
        Assert.False(recent.IsSettled);
    }

    [Fact]
    public void JobChainInfo_IsSettled_WhenAJoinWaitsForAnExpiredStep_AndTheOthersDoNotDecideIt()
    {
        JobChainInfo chain = Chain(
            Step("0.0", "Succeeded", ChainLink.Root),
            Step("1.0", null, ChainLink.Then),
            Step("1.1", "Succeeded", ChainLink.Then),
            Step("2.0", "Awaiting", ChainLink.AfterAllCompleted));

        Assert.True(chain.IsSettled);
    }

    [Fact]
    public void StatisticsHistory_DoesNotCountAStep_PutBackIntoTheStateItWasIn()
    {
        StatisticsHistoryAttribute filter = new();
        ElectStateContextMock restored = new();
        restored.ApplyContext.NewStateObject = new JobChains.RestoredState(new StateData { Name = FailedState.StateName, Data = new Dictionary<string, string>() });
        ElectStateContextMock failed = new();
        failed.ApplyContext.NewStateObject = new FailedState(new InvalidOperationException("failed"));

        filter.OnStateElection(restored.Object);
        filter.OnStateElection(failed.Object);

        restored.ApplyContext.Transaction.Verify(x => x.IncrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        failed.ApplyContext.Transaction.Verify(x => x.IncrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Exactly(2));
    }

    [Fact]
    public void GlobalFilters_CheckTheStepsOfChains_BeforeAnyOtherFilter()
    {
        Assert.IsType<JobChainStepFilter>(GlobalJobFilters.Filters.First().Instance);
        Assert.Equal(JobFilter.DefaultOrder, GlobalJobFilters.Filters.First().Order);
    }

    [Fact]
    public void Enqueue_EnqueuesTheFirstStep_ThatAWorkerHeldUntilTheChainIsRecorded()
    {
        string chainId = null;
        _storage.Connection.Setup(x => x.GetStateData("job-1"))
            .Returns(() => new StateData { Name = ScheduledState.StateName, Reason = $"Waiting for chain {chainId} to be recorded", Data = new Dictionary<string, string>() });
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                chainId = (string)parameters[JobChains.ChainIdParameter];
                _created.Add((null, null, parameters));
                return $"job-{_created.Count}";
            });

        JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object);

        _client.Verify(x => x.ChangeState("job-1", It.Is<EnqueuedState>(state => state.Reason == "The chain is recorded"), ScheduledState.StateName), Times.Once);
    }

    [Fact]
    public void Schedule_LeavesAScheduledFirstStepAlone_OnceTheChainIsRecorded()
    {
        _storage.Connection.Setup(x => x.GetStateData("job-1"))
            .Returns(new StateData { Name = ScheduledState.StateName, Data = new Dictionary<string, string> { ["EnqueueAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow.AddHours(1)) } });

        JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Schedule(_client.Object, TimeSpan.FromHours(1));

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    private static JobChainInfo Chain(params JobChainStepInfo[] steps) => new("c", 1, DateTime.UtcNow, ChainDeleteBehavior.Cascade, steps);

    private static JobChainStepInfo Step(
        string key,
        string state,
        ChainLink link,
        JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState,
        DateTime? finishedAt = null)
    {
        int level = int.Parse(key.AsSpan(0, key.IndexOf('.', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture);
        bool finished = state is "Succeeded" or "Failed" or "Deleted";

        return new JobChainStepInfo(key, $"job-{key}", state, level, level - 1)
        {
            Link = link,
            Options = options,
            FinishedAt = finished ? finishedAt ?? DateTime.UtcNow : null,
        };
    }

    [Fact]
    public void DeleteChain_KeepsTheRecord_WhenAStepStartedWhileItWasDeleted()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        _storage.Connection.SetupSequence(x => x.GetStateData("root"))
            .Returns(new StateData { Name = EnqueuedState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName });
        _storage.Connection.Setup(x => x.GetStateData("next")).Returns(new StateData { Name = DeletedState.StateName });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Delete(_storage.Object, _client.Object, "c"));

        Assert.Contains("started meanwhile", exception.Message, StringComparison.Ordinal);
        Assert.True(_storage.Hashes.ContainsKey(JobChains.GetKey("c")));
    }

    [Fact]
    public void DeleteChain_OfARestrictChain_KeepsAStepThatStartedMeanwhile_AndTheRecord()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")).OnDelete(ChainDeleteBehavior.Restrict), ["root", "next"]);
        _storage.Connection.SetupSequence(x => x.GetStateData("root"))
            .Returns(new StateData { Name = EnqueuedState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName });
        _storage.Connection.Setup(x => x.GetStateData("next")).Returns(new StateData { Name = DeletedState.StateName });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Delete(_storage.Object, _client.Object, "c"));

        Assert.Contains("Restrict", exception.Message, StringComparison.Ordinal);
        _client.Verify(x => x.ChangeState("root", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.True(_storage.Hashes.ContainsKey(JobChains.GetKey("c")));
    }

    [Fact]
    public void DeleteChain_RemovesTheRecord_WhenEveryStepIsDeleted()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        Dictionary<string, string> states = new() { ["root"] = EnqueuedState.StateName, ["next"] = AwaitingState.StateName };
        _storage.Connection.Setup(x => x.GetStateData(It.IsAny<string>())).Returns((string id) => new StateData { Name = states[id] });
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<DeletedState>(), It.IsAny<string>()))
            .Returns((string id, IState _, string _) =>
            {
                states[id] = DeletedState.StateName;
                return true;
            });

        Assert.Equal(2, JobChains.Delete(_storage.Object, _client.Object, "c"));
        Assert.False(_storage.Hashes.ContainsKey(JobChains.GetKey("c")));
        Assert.Equal(-1, JobChains.Delete(_storage.Object, _client.Object, "c"));
    }

    private void RecordChain(string chainId, JobChain chain, string[] jobIds)
    {
        List<string> keys = [];
        for (int level = 0; level < chain.Levels.Count; level++)
        {
            for (int i = 0; i < chain.Levels[level].Steps.Count; i++) keys.Add($"{level}.{i}");
        }

        _storage.Hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(chain),
            ["Jobs"] = "{" + string.Join(",", keys.Zip(jobIds, static (key, id) => $"\"{key}\":\"{id}\"")) + "}",
        };
    }

    [Fact]
    public void AddOrUpdateChain_RegistersTheRunner_WithTheChainsDefinition()
    {
        Mock<IRecurringJobManager> manager = new();
        JobChain chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b"));

        manager.Object.AddOrUpdateChain("etl", chain, "0 2 * * *", skipWhileRunning: true);

        manager.Verify(x => x.AddOrUpdate("etl", It.Is<Job>(job =>
            job.Type == typeof(JobChainRunner) &&
            (string)job.Args[0] == JobChains.ToDefinition(chain) &&
            (bool)job.Args[1]), "0 2 * * *", It.IsAny<RecurringJobOptions>()));
        Assert.Throws<ArgumentNullException>("chain", () => manager.Object.AddOrUpdateChain("etl", null, "0 2 * * *"));
    }

    [Fact]
    public void JobChainRunner_SkipsTheOccurrence_WhileThePreviousChainRuns()
    {
        _storage.Hashes[RecurringJobChains.GetKey("etl")] = new Dictionary<string, string> { ["LastChainId"] = "previous" };
        _storage.Hashes[JobChains.GetKey("previous")] = new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(JobChain.StartWith(() => Step("a"))),
            ["Jobs"] = "{\"0.0\":\"root\"}",
        };
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = EnqueuedState.StateName });
        PerformContextMock context = new() { Storage = _storage.Storage, Connection = _storage.Connection };
        context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { ["RecurringJobId"] = "\"etl\"" };

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => JobChainRunner.Run(JobChains.ToDefinition(JobChain.StartWith(() => Step("b"))), true, context.Object));

        Assert.Contains("'previous'", exception.Message, StringComparison.Ordinal);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Step(string name)
    {
        _ = name;
    }

    public sealed class Service
    {
        public void Work()
        {
        }

        public Task WorkAsync() => Task.CompletedTask;
    }
}
