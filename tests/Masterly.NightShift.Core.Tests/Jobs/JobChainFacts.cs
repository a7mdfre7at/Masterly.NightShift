namespace Masterly.NightShift.Core.Tests;

public class JobChainFacts : IDisposable
{
    private readonly PolicyStorage _storage = new();
    private readonly Mock<IBackgroundJobClientV2> _client = new();
    private readonly List<(Job Job, IState State, IDictionary<string, object> Parameters)> _created = [];
    private readonly TimeSpan _leaseRenewal = JobChains.LeaseRenewal;
    private readonly TimeSpan _leaseTime = JobChains.LeaseTime;

    public JobChainFacts()
    {
        // A job just created is in the state it was created in
        _storage.Connection.Setup(x => x.GetStateData(It.IsAny<string>()))
            .Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string>() });

        _client.Setup(x => x.Storage).Returns(_storage.Object);
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job job, IState state, IDictionary<string, object> parameters) =>
            {
                _created.Add((job, state, parameters));
                return $"job-{_created.Count}";
            });
    }

    public void Dispose()
    {
        JobChains.LeaseRenewal = _leaseRenewal;
        JobChains.LeaseTime = _leaseTime;
        GC.SuppressFinalize(this);
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
        for (int i = 1; i < JobChain.MaxLevels; i++)
        {
            chain.Then(() => Step("b"));
        }

        Assert.Throws<InvalidOperationException>(() => chain.Then(() => Step("c")));
    }

    [Fact]
    public void Builder_RefusesMoreStepsThanTheLimit()
    {
        JobChain chain = JobChain.StartWith(() => Step("a"))
            .ThenMany(branches =>
            {
                for (int i = 0; i < JobChain.MaxSteps - 1; i++)
                {
                    branches.Branch(() => Step("b"));
                }
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
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>())).Returns(default(string));

        Assert.Null(JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()), Times.Once);
    }

    [Fact]
    public void Enqueue_DeletesTheCreatedSteps_WhenALaterStepIsCanceled()
    {
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingState>(), It.IsAny<IDictionary<string, object>>())).Returns(default(string));
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = EnqueuedState.StateName });
        _client.Setup(x => x.ChangeState("job-1", It.IsAny<DeletedState>(), EnqueuedState.StateName)).Returns(true);

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _client.Verify(x => x.ChangeState("job-1", It.IsAny<DeletedState>(), EnqueuedState.StateName));
    }

    [Fact]
    public void Enqueue_LeavesTheFirstStep_WhenAWorkerTookItBeforeALaterStepFailed()
    {
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingState>(), It.IsAny<IDictionary<string, object>>())).Returns(default(string));
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = ProcessingState.StateName });

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Enqueue_CreatesNothing_WhenTheRecordCanNotBeStarted()
    {
        _storage.Connection.Setup(x => x.CreateWriteTransaction()).Throws(new InvalidOperationException("Storage is down"));

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()), Times.Never);
    }

    [Fact]
    public void Enqueue_DeletesTheCreatedSteps_WhenTheRecordCanNotBeWritten()
    {
        // The record is started, with the client's lease; the write that lists the steps fails
        FailWritesAfterTheFirst(new Mock<IWriteOnlyTransaction>());
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = EnqueuedState.StateName });
        _storage.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = AwaitingState.StateName });

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _client.Verify(x => x.ChangeState("job-2", It.IsAny<DeletedState>(), AwaitingState.StateName));
        _client.Verify(x => x.ChangeState("job-1", It.IsAny<DeletedState>(), EnqueuedState.StateName));
    }

    [Fact]
    public void Enqueue_TakesTheChainAsCreated_WhenTheWriteOfTheRecordThrew_ButWentThrough()
    {
        // A lost reply: the commit throws, and the record is there
        string chainId = null;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                chainId = (string)parameters[JobChains.ChainIdParameter];
                _created.Add((null, null, parameters));
                return $"job-{_created.Count}";
            });
        JobChain chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b"));
        Mock<IWriteOnlyTransaction> lostReply = new();
        lostReply.Setup(x => x.Commit()).Callback(() => _storage.Hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(chain),
            ["Jobs"] = "{\"0.0\":\"job-1\",\"1.0\":\"job-2\"}",
            ["Generation"] = "1",
        }).Throws(new TimeoutException("The reply was lost"));
        FailWritesAfterTheFirst(lostReply);

        JobChainResult result = chain.Enqueue(_client.Object);

        Assert.Equal("job-1", result?.RootId);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.True(_storage.Hashes.ContainsKey(JobChains.GetKey(chainId)));
    }

    [Fact]
    public void Enqueue_StartsTheRecordWithItsLease_BeforeTheFirstJob_AndListsTheStepsOnlyAtTheEnd()
    {
        List<string> jobsWhileCreating = [];
        List<bool> listedWhileCreating = [];
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                jobsWhileCreating.Add(_storage.Hashes[JobChains.GetKey((string)parameters[JobChains.ChainIdParameter])]["Jobs"]);
                listedWhileCreating.Add(_storage.Sets.ContainsKey(JobChains.SetKey));
                _created.Add((null, null, parameters));
                return $"job-{_created.Count}";
            });

        JobChainResult result = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object);

        // While the jobs are created: a lease and no step, in a record that isn't listed
        Assert.All(jobsWhileCreating, jobs => Assert.StartsWith("{\"changing\":\"", jobs, StringComparison.Ordinal));
        Assert.All(listedWhileCreating, Assert.False);

        string recorded = _storage.Hashes[JobChains.GetKey(result!.ChainId)]["Jobs"];
        Assert.DoesNotContain("changing", recorded, StringComparison.Ordinal);
        Assert.Contains("\"1.0\":\"job-2\"", recorded, StringComparison.Ordinal);
        Assert.Contains(result.ChainId, _storage.Sets[JobChains.SetKey]);
    }

    [Fact]
    public void Enqueue_RenewsItsLease_WhileItCreatesTheSteps_OnlyWhenItHasWorkedForAWhile()
    {
        JobChain chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")).Then(() => Step("d"));

        chain.Enqueue(_client.Object);
        int quick = _storage.Commits;

        JobChains.LeaseRenewal = TimeSpan.Zero;
        chain.Enqueue(_client.Object);
        int slow = _storage.Commits - quick;

        // The record started and the record written; and, for a client that takes long, the lease before each step
        Assert.Equal(2, quick);
        Assert.Equal(5, slow);
    }

    [Fact]
    public void Enqueue_RemovesTheRecordItStarted_WhenTheFirstStepIsCanceled()
    {
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>())).Returns(default(string));

        Assert.Null(JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        Assert.Empty(_storage.Hashes);
    }

    [Fact]
    public void Enqueue_GivesEveryChainWithStepsThatWait_ALookByAServer_WhateverItsShape()
    {
        // A step after a single step is started by the process that finishes that step, which may stop first, as
        // a step after a group is
        JobChainResult line = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object);
        JobChainResult joined = JobChain.StartWith(() => Step("a"))
            .ThenMany(branches => branches.Branch(() => Step("b")).Branch(() => Step("c")))
            .ThenAfterAll(() => Step("d"))
            .Enqueue(_client.Object);

        // A single step has nothing waiting, and its record never expires on this storage: nothing to look at
        JobChain.StartWith(() => Step("a")).Enqueue(_client.Object);

        Assert.Equal(new[] { line!.ChainId, joined!.ChainId }.Order(StringComparer.Ordinal), _storage.Sets["chains:due"]);
        Assert.Equal(3, _storage.Sets[JobChains.SetKey].Count);
        Assert.False(_storage.Sets.ContainsKey("chains:joins"));
    }

    // The record is started by a write that works; the writes after it go to the transaction given
    private void FailWritesAfterTheFirst(Mock<IWriteOnlyTransaction> later)
    {
        int writes = 0;
        IWriteOnlyTransaction first = _storage.Connection.Object.CreateWriteTransaction();
        if (later.Setups.Count == 0)
        {
            later.Setup(x => x.Commit()).Throws(new InvalidOperationException("Storage is down"));
        }

        _storage.Connection.Setup(x => x.CreateWriteTransaction()).Returns(() => writes++ == 0 ? first : later.Object);
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
    public void JobChainInfo_IsSettled_WhenAStepNothingKeepsFromStarting_WasNeverTold()
    {
        // Every branch succeeded and the step after them still waits, long after: the process that was to start
        // it stopped after the last branch was committed. The chain isn't unsettled for good.
        DateTime longAgo = DateTime.UtcNow - JobChains.DecisionWindow - TimeSpan.FromMinutes(1);
        JobChainInfo lost = Chain(
            Step("0.0", "Succeeded", ChainLink.Root, finishedAt: longAgo),
            Step("1.0", "Succeeded", ChainLink.Then, finishedAt: longAgo),
            Step("1.1", "Succeeded", ChainLink.Then, finishedAt: longAgo),
            Step("2.0", "Awaiting", ChainLink.AfterAll),
            Step("3.0", "Awaiting", ChainLink.Then));
        JobChainInfo starting = Chain(
            Step("0.0", "Succeeded", ChainLink.Root, finishedAt: longAgo),
            Step("1.0", "Succeeded", ChainLink.Then, finishedAt: longAgo),
            Step("1.1", "Succeeded", ChainLink.Then),
            Step("2.0", "Awaiting", ChainLink.AfterAll),
            Step("3.0", "Awaiting", ChainLink.Then));
        JobChainInfo line = Chain(
            Step("0.0", "Succeeded", ChainLink.Root, finishedAt: longAgo),
            Step("1.0", "Awaiting", ChainLink.Then));

        Assert.False(starting.IsSettled);
        Assert.Empty(starting.NeverToldSteps());

        // The step after the one never told waits behind it, like a step behind a failure
        Assert.Equal(["2.0"], lost.NeverToldSteps().Select(static x => x.Key));
        Assert.True(lost.IsSettled);

        Assert.True(line.IsSettled);
        Assert.Equal(["1.0"], line.NeverToldSteps().Select(static x => x.Key));
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
    public void Enqueue_TakesTheChainBack_WhenItsFirstStepWasDeletedBeforeTheChainWasRecorded()
    {
        // Creating the chain took longer than a step waits for its record: a worker took the first step and
        // deleted it for lack of one
        string chainId = null;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                chainId = (string)parameters[JobChains.ChainIdParameter];
                _created.Add((null, null, parameters));
                return $"job-{_created.Count}";
            });
        _storage.Connection.Setup(x => x.GetStateData("job-1"))
            .Returns(() => new StateData { Name = DeletedState.StateName, Reason = $"Chain {chainId} isn't recorded: it was deleted or never completely created", Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = AwaitingState.StateName });
        _client.Setup(x => x.ChangeState("job-2", It.IsAny<DeletedState>(), AwaitingState.StateName)).Returns(true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        // Decided before the chain is recorded: it never was, so none of its steps could run
        Assert.Contains("wasn't created", exception.Message, StringComparison.Ordinal);
        _client.Verify(x => x.ChangeState("job-2", It.IsAny<DeletedState>(), AwaitingState.StateName));
        Assert.False(_storage.Hashes.ContainsKey(JobChains.GetKey(chainId)));
        Assert.False(_storage.Sets.ContainsKey(JobChains.SetKey));
        Assert.Contains("job:job-1:state-lock", _storage.Locks);
    }

    [Fact]
    public void Enqueue_KeepsTheRecordItStarted_WithoutAnyStep_WhenAJobItCreatedCanNotBeDeleted()
    {
        // The job that stays finds a record that doesn't list it when it comes due, and is deleted then; without
        // a record it could run for being old enough for its record to have expired
        string chainId = null;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingState>(), It.IsAny<IDictionary<string, object>>())).Returns(default(string));
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                chainId = (string)parameters[JobChains.ChainIdParameter];
                return "job-1";
            });
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = ScheduledState.StateName });
        _client.Setup(x => x.ChangeState("job-1", It.IsAny<DeletedState>(), ScheduledState.StateName)).Throws(new InvalidOperationException("Storage is down"));

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Schedule(_client.Object, TimeSpan.FromDays(45)));

        Assert.Equal("{}", _storage.Hashes[JobChains.GetKey(chainId)]["Jobs"]);
        Assert.False(_storage.Sets.TryGetValue(JobChains.SetKey, out SortedSet<string> listed) && listed.Contains(chainId));
    }

    [Fact]
    public void Enqueue_UnlistsTheChain_WhenTheWriteOfTheRecordThrew_WentThrough_AndCanNotBeReadAgain()
    {
        // The write is taken for failed and the steps are deleted: the chain mustn't stay listed without a record,
        // or a job that couldn't be deleted would run for it
        string chainId = null;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                chainId = (string)parameters[JobChains.ChainIdParameter];
                _created.Add((null, null, parameters));
                return $"job-{_created.Count}";
            });
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<DeletedState>(), It.IsAny<string>())).Returns(true);

        IWriteOnlyTransaction started = _storage.Connection.Object.CreateWriteTransaction();
        IWriteOnlyTransaction takenBack = _storage.Connection.Object.CreateWriteTransaction();
        Mock<IWriteOnlyTransaction> lostReply = new();
        lostReply.Setup(x => x.Commit()).Callback(() =>
        {
            _storage.Hashes[JobChains.GetKey(chainId)] = new Dictionary<string, string> { ["Definition"] = "{}", ["Jobs"] = "{\"0.0\":\"job-1\",\"1.0\":\"job-2\"}", ["Generation"] = "1" };
            _storage.Sets[JobChains.SetKey] = new SortedSet<string>(StringComparer.Ordinal) { chainId };
            _storage.Connection.Setup(x => x.GetAllEntriesFromHash(JobChains.GetKey(chainId))).Throws(new TimeoutException("Storage is down"));
        }).Throws(new TimeoutException("The reply was lost"));
        int writes = 0;
        _storage.Connection.Setup(x => x.CreateWriteTransaction()).Returns(() => writes++ switch { 0 => started, 1 => lostReply.Object, _ => takenBack });

        Assert.Throws<TimeoutException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        Assert.False(_storage.Hashes.ContainsKey(JobChains.GetKey(chainId)));
        Assert.DoesNotContain(chainId, _storage.Sets[JobChains.SetKey]);
    }

    [Fact]
    public void Enqueue_MarksEveryStep_AsAStepOfARecordedChain()
    {
        JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object);

        // The mark says how long the record is kept, in seconds: 30 days
        _storage.Connection.Verify(x => x.SetJobParameter("job-1", JobChains.ChainRecordedParameter, "2592000"), Times.Once);
        _storage.Connection.Verify(x => x.SetJobParameter("job-2", JobChains.ChainRecordedParameter, "2592000"), Times.Once);
    }

    [Fact]
    public void Enqueue_MarksTheSteps_WithTheTimeThisProcessKeepsRecordsFor()
    {
        TimeSpan retention = JobChain.RecordRetention;
        JobChain.RecordRetention = TimeSpan.FromDays(2);

        try
        {
            JobChain.StartWith(() => Step("a")).Enqueue(_client.Object);
        }
        finally
        {
            JobChain.RecordRetention = retention;
        }

        _storage.Connection.Verify(x => x.SetJobParameter("job-1", JobChains.ChainRecordedParameter, "172800"), Times.Once);
    }

    [Fact]
    public void Enqueue_WritesTheTimeThisProcessKeepsRecordsFor_AmongTheTimesAnyProcessDid()
    {
        TimeSpan retention = JobChain.RecordRetention;

        try
        {
            JobChain.StartWith(() => Step("a")).Enqueue(_client.Object);
            JobChain.StartWith(() => Step("b")).Enqueue(_client.Object);
            JobChain.RecordRetention = TimeSpan.FromDays(2);
            JobChain.StartWith(() => Step("c")).Enqueue(_client.Object);
        }
        finally
        {
            JobChain.RecordRetention = retention;
        }

        Assert.Equal(["172800", "2592000"], _storage.Hashes["chains:retentions"].Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("172800", 2)]
    [InlineData("true", 30)]
    [InlineData("0", 30)]
    [InlineData("-5", 30)]
    [InlineData("99999999999999", 30)]
    public void MarkedRetention_IsWhatTheMarkSays_OrWhatThisProcessIsSetTo_ForAMarkThatDoesNotSay(string mark, int days)
    {
        Assert.Equal(TimeSpan.FromDays(days), JobChains.MarkedRetention(mark));
        Assert.Null(JobChains.MarkedRetention(null));
        Assert.Null(JobChains.MarkedRetention(string.Empty));
    }

    [Fact]
    public void Enqueue_DoesNotMarkAnyStep_WhenTheChainIsNotRecorded()
    {
        FailWritesAfterTheFirst(new Mock<IWriteOnlyTransaction>());

        Assert.Throws<InvalidOperationException>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), JobChains.ChainRecordedParameter, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Enqueue_LooksAtEveryStep_WhenItsLeaseLapsedBeforeTheChainIsRecorded_AndTakesTheChainBackForOneAServerDeleted()
    {
        // The client was frozen, or one step took longer to create than the lease lasts: a server may have deleted
        // a step that was due meanwhile, which the first step alone doesn't tell
        JobChains.LeaseTime = TimeSpan.Zero;
        string chainId = null;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                chainId = (string)parameters[JobChains.ChainIdParameter];
                _created.Add((null, null, parameters));
                return $"job-{_created.Count}";
            });
        _storage.Connection.Setup(x => x.GetStateData("job-3"))
            .Returns(() => new StateData { Name = DeletedState.StateName, Reason = $"Chain {chainId} isn't recorded: it was deleted or never completely created", Data = new Dictionary<string, string>() });
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<DeletedState>(), It.IsAny<string>())).Returns(true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")).Enqueue(_client.Object));

        Assert.Contains("'job-3' was deleted before the chain was recorded", exception.Message, StringComparison.Ordinal);
        Assert.Contains("job:job-3:state-lock", _storage.Locks);
        Assert.False(_storage.Hashes.ContainsKey(JobChains.GetKey(chainId)));
        _client.Verify(x => x.ChangeState("job-2", It.IsAny<DeletedState>(), It.IsAny<string>()));
    }

    [Fact]
    public void Enqueue_ReadsOnlyTheFirstStep_WhenItsLeaseNeverLapsed()
    {
        JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")).Enqueue(_client.Object);

        _storage.Connection.Verify(x => x.GetStateData("job-2"), Times.Never);
        _storage.Connection.Verify(x => x.GetStateData("job-3"), Times.Never);
        Assert.DoesNotContain("job:job-2:state-lock", _storage.Locks);
    }

    [Fact]
    public void Enqueue_TakesTheChainBack_WhenAServerDeletedItsFirstStep_WhileThisProcessWasFrozenWritingTheRecord()
    {
        // Its lock and its lease are gone by the time it wakes up: the first step is read once more after the write
        string chainId = null;
        int reads = 0;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Returns((Job _, IState _, IDictionary<string, object> parameters) =>
            {
                chainId = (string)parameters[JobChains.ChainIdParameter];
                _created.Add((null, null, parameters));
                return $"job-{_created.Count}";
            });
        _storage.Connection.Setup(x => x.GetStateData("job-1")).Returns(() => reads++ == 0
            ? new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string>() }
            : new StateData { Name = DeletedState.StateName, Reason = $"Chain {chainId} isn't recorded: it was deleted or never completely created", Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = AwaitingState.StateName });
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<DeletedState>(), It.IsAny<string>())).Returns(true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client.Object));

        Assert.Contains("wasn't created", exception.Message, StringComparison.Ordinal);
        _client.Verify(x => x.ChangeState("job-2", It.IsAny<DeletedState>(), AwaitingState.StateName));
        Assert.False(_storage.Hashes.ContainsKey(JobChains.GetKey(chainId)));
        Assert.DoesNotContain(chainId, _storage.Sets[JobChains.SetKey]);
    }

    [Fact]
    public void GetChain_ReadsAChain_WhoseStepsMethodsWereRenamedOrRemoved()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        ForgetTheMethods("c");

        JobChainInfo info = _storage.Object.GetChain("c");

        Assert.Equal(["root", "next"], info.Steps.Select(static x => x.JobId));
        Assert.All(info.Steps, static step => Assert.Null(step.Job));
        Assert.False(info.IsSettled);
    }

    [Fact]
    public void DeleteChain_DeletesAChain_WhoseStepsMethodsWereRenamedOrRemoved()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        ForgetTheMethods("c");
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
    }

    [Fact]
    public void RetryChainFrom_RefusesClearly_WhenAStepItMustCreateAgainHasNoMethodAnyMore_AndChangesNothing()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        ForgetTheMethods("c");
        _storage.Connection.Setup(x => x.GetJobParameter("root", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = FailedState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("next")).Returns(new StateData { Name = AwaitingState.StateName, Data = new Dictionary<string, string>() });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Retry(_storage.Object, _client.Object, "root"));

        Assert.Contains("Step '1.0' of chain 'c' can't be created again", exception.Message, StringComparison.Ordinal);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.DoesNotContain("changing", _storage.Hashes[JobChains.GetKey("c")]["Jobs"], StringComparison.Ordinal);
    }

    [Fact]
    public void RetryChainFrom_RetriesTheLastStep_WithoutLoadingTheMethodsItDoesNotNeed()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        ForgetTheMethods("c");
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        HoldsWhenAsked("next", FailedState.StateName);

        JobChainResult retried = JobChains.Retry(_storage.Object, _client.Object, "next");

        Assert.Equal("next", retried.Steps["1.0"]);
        Assert.Equal("2", _storage.Hashes[JobChains.GetKey("c")]["Generation"]);
        _client.Verify(x => x.ChangeState("next", It.IsAny<EnqueuedState>(), ScheduledState.StateName));
    }

    [Fact]
    public void RetryChainFrom_NotesWhatItStartsFrom_OnTheStep_BeforeItHoldsIt_WhereTheStorageCanNotDoBothAtOnce()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        List<string> order = HoldsWhenAsked("next", FailedState.StateName, new Dictionary<string, string> { ["FailedAt"] = "2026-01-01T00:00:00.0000000Z" }, "It broke");

        JobChains.Retry(_storage.Object, _client.Object, "next");

        // The note first, under the job's lock, then the hold: no step is ever held without it
        Assert.Equal(["lock", "note", "unlock", "hold"], order.Take(4));
        JobChains.RetryMarker marker = JobChains.RetryMarker.Parse(_notes["next"]);
        Assert.Equal(1, marker.Generation);
        Assert.Equal(FailedState.StateName, marker.State);
        Assert.Equal("It broke", marker.Reason);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", marker.Data["FailedAt"]);
        Assert.Equal(32, marker.Id.Length);
    }

    [Fact]
    public void RetryChainFrom_HoldsItsStep_ForAsLongAsItsLeaseLasts_AndALittleMore()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        HoldsWhenAsked("next", FailedState.StateName);

        JobChains.Retry(_storage.Object, _client.Object, "next");

        ScheduledState hold = _client.Invocations.Select(static x => x.Arguments.ElementAtOrDefault(1)).OfType<ScheduledState>().Single();
        Assert.Equal("Chain retried from this step", hold.Reason);
        Assert.InRange(hold.EnqueueAt - DateTime.UtcNow, JobChains.LeaseTime, JobChains.LeaseTime + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void RetryChainFrom_DoesNotNoteAnything_OnAStepThatChangedSinceItReadIt()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });

        // Failed when the retry reads it; requeued by somebody else by the time the retry has its lock
        Queue<string> states = new([FailedState.StateName, FailedState.StateName]);
        _storage.Connection.Setup(x => x.GetStateData("next"))
            .Returns(() => new StateData { Name = states.Count > 0 ? states.Dequeue() : EnqueuedState.StateName, Data = new Dictionary<string, string>() });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        Assert.Contains("changed meanwhile", exception.Message, StringComparison.Ordinal);
        _storage.Connection.Verify(x => x.SetJobParameter("next", JobChains.ChainRetryParameter, It.IsAny<string>()), Times.Never);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.Equal("1", _storage.Hashes[JobChains.GetKey("c")]["Generation"]);
        Assert.DoesNotContain("changing", _storage.Hashes[JobChains.GetKey("c")]["Jobs"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Scheduled", true)]
    [InlineData("Enqueued", true)]
    [InlineData("Failed", false)]
    [InlineData("Processing", false)]
    public void RetryChainFrom_RecordsTheRetry_OnlyWhileItsStepIsStillOnItsWayFromTheHold_WithItsNote(string stateAtTheEnd, bool recorded)
    {
        // Enqueued: the hold ended and no worker took the step yet. It still can't run before the retry is
        // recorded, so the retry goes on; a step that is anywhere else was put back, or changed by somebody.
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        HoldsWhenAsked("next", FailedState.StateName, stateOnceHeld: stateAtTheEnd);

        if (recorded)
        {
            JobChains.Retry(_storage.Object, _client.Object, "next");
        }
        else
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));
            Assert.Contains("wasn't retried", exception.Message, StringComparison.Ordinal);
            Assert.Contains("The chain is as it was", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(recorded ? "2" : "1", _storage.Hashes[JobChains.GetKey("c")]["Generation"]);
        Assert.DoesNotContain("changing", _storage.Hashes[JobChains.GetKey("c")]["Jobs"], StringComparison.Ordinal);
    }

    [Fact]
    public void RetryChainFrom_IsNotRecorded_WhenTheNoteOnItsStepIsNotItsOwnAnyMore()
    {
        // A server put the step back and took the note off; or another note is there
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        HoldsWhenAsked("next", FailedState.StateName);
        _client.Setup(x => x.ChangeState("next", It.IsAny<ScheduledState>(), FailedState.StateName))
            .Callback(() => _notes["next"] = "{\"id\":\"another\",\"generation\":1,\"state\":\"Failed\",\"data\":{}}")
            .Returns(true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        Assert.Contains("wasn't retried", exception.Message, StringComparison.Ordinal);
        Assert.Equal("1", _storage.Hashes[JobChains.GetKey("c")]["Generation"]);
    }

    [Theory]
    [InlineData("Failed", "The retry of chain c was abandoned", "put background job 'next' back")]
    [InlineData("Scheduled", "Chain retried from this step", null)]
    public void RetryChainFrom_TakesTheRetryBack_OnlyWhenAServerPutItsStepBack_WhileThisProcessWasFrozenRecordingIt(string stateAfter, string reasonAfter, string said)
    {
        // Frozen for longer than a lock is safe: its lock and its lease are gone by the time it wakes up, so the
        // step is read once more after the write
        TimeSpan safeLockTime = JobChains.SafeLockTime;
        JobChains.SafeLockTime = TimeSpan.Zero;

        try
        {
            RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")), ["root", "next"]);
            _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
            _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
            List<string> order = HoldsWhenAsked("next", FailedState.StateName);
            bool recorded = false;
            _storage.Connection.Setup(x => x.GetStateData("next")).Returns(() =>
            {
                if (!order.Contains("hold"))
                {
                    return new StateData { Name = FailedState.StateName, Data = new Dictionary<string, string>() };
                }

                // Held until the record is written; what a server made of it meanwhile shows from then on
                recorded |= _storage.Hashes[JobChains.GetKey("c")].GetValueOrDefault("Generation") == "2";
                // A server that put the step back says for which retry
                Dictionary<string, string> data = stateAfter == FailedState.StateName
                    ? new Dictionary<string, string> { ["AbandonedRetry"] = JobChains.RetryMarker.Parse(_heldWith["next"])!.Id }
                    : [];

                return recorded
                    ? new StateData { Name = stateAfter, Reason = reasonAfter, Data = data }
                    : new StateData { Name = ScheduledState.StateName, Reason = "Chain retried from this step", Data = new Dictionary<string, string>() };
            });

            if (said == null)
            {
                // Still held: the retry is recorded, and the step released
                JobChains.Retry(_storage.Object, _client.Object, "next");

                Assert.Equal("2", _storage.Hashes[JobChains.GetKey("c")]["Generation"]);
                _client.Verify(x => x.ChangeState("next", It.IsAny<EnqueuedState>(), ScheduledState.StateName), Times.Once);
                return;
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

            // The record goes back to what it was, and the step is left as the server put it
            Assert.Contains("wasn't retried: this process was frozen while it recorded the retry", exception.Message, StringComparison.Ordinal);
            Assert.Contains(said, exception.Message, StringComparison.Ordinal);
            Assert.Contains("the chain is as it was", exception.Message, StringComparison.Ordinal);
            Assert.Equal("1", _storage.Hashes[JobChains.GetKey("c")]["Generation"]);
            Assert.DoesNotContain("changing", _storage.Hashes[JobChains.GetKey("c")]["Jobs"], StringComparison.Ordinal);
            _client.Verify(x => x.ChangeState("next", It.IsAny<EnqueuedState>(), It.IsAny<string>()), Times.Never);
            _client.Verify(x => x.ChangeState("next", It.IsAny<JobChains.RestoredState>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            JobChains.SafeLockTime = safeLockTime;
        }
    }

    [Fact]
    public void RetryChainFrom_ThatFails_LeavesItsStepToTheWorkerThatTakesIt_WhenItsHoldEndedMeanwhile()
    {
        // The hold ended and no worker took the step yet: it waits in its queue with the note of the retry.
        // Moved from here, it would leave its place in the queue behind; the worker that takes it puts it back.
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")), ["root", "next", "last"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("last")).Returns(new StateData { Name = AwaitingState.StateName, Data = new Dictionary<string, string>() });
        List<string> order = HoldsWhenAsked("next", FailedState.StateName, stateOnceHeld: EnqueuedState.StateName);
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>())).Throws(new InvalidOperationException("The storage is down."));

        Assert.ThrowsAny<Exception>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        _client.Verify(x => x.ChangeState("next", It.IsAny<JobChains.RestoredState>(), It.IsAny<string>()), Times.Never);
        Assert.DoesNotContain("forget", order);
        Assert.NotNull(_notes["next"]);
        Assert.Equal("1", _storage.Hashes[JobChains.GetKey("c")].GetValueOrDefault("Generation", "1"));
        Assert.DoesNotContain("changing", _storage.Hashes[JobChains.GetKey("c")]["Jobs"], StringComparison.Ordinal);
    }

    [Fact]
    public void RetryChainFrom_Refuses_AStepRequeuedBetweenTheCheckThatTheChainIsSettled_AndTheReadOfItsState()
    {
        // The chain is read as settled; then somebody requeues the step, before the retry reads its state to hold
        // it. Held from there, the step would keep its place in the queue, and be enqueued once more later.
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")), ["root", "next", "last"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("last")).Returns(new StateData { Name = AwaitingState.StateName, Data = new Dictionary<string, string>() });
        int reads = 0;
        _storage.Connection.Setup(x => x.GetStateData("next")).Returns(() => reads++ == 0
            ? new StateData { Name = FailedState.StateName, Data = new Dictionary<string, string>() }
            : new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string> { ["Queue"] = "default" } });
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        Assert.Contains("hasn't finished", exception.Message, StringComparison.Ordinal);
        _client.Verify(x => x.ChangeState("next", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.Null(_storage.Hashes[JobChains.GetKey("c")].GetValueOrDefault("Generation"));
        Assert.DoesNotContain("changing", _storage.Hashes[JobChains.GetKey("c")]["Jobs"], StringComparison.Ordinal);
    }

    [Fact]
    public void RetryChainFrom_Refuses_AStepWhoseMethodCanNotBeLoadedHere_BeforeAnythingIsWritten()
    {
        // A host without the step's assembly (an app with only the dashboard) can't hold the step: its state
        // change would fail it instead, so the retry is refused first, clearly
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")), ["root", "next", "last"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("next")).Returns(new StateData { Name = FailedState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("last")).Returns(new StateData { Name = AwaitingState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetJobData("next")).Returns(new JobData
        {
            State = FailedState.StateName,
            LoadException = new JobLoadException("The type wasn't found.", new TypeLoadException("Gone")),
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        Assert.Contains("can't be loaded", exception.Message, StringComparison.Ordinal);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()), Times.Never);
        Assert.DoesNotContain("changing", _storage.Hashes[JobChains.GetKey("c")]["Jobs"], StringComparison.Ordinal);
        _storage.Connection.Verify(x => x.SetJobParameter("next", JobChains.ChainRetryParameter, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RetryChainFrom_ThatFails_LeavesItsStep_AndItsNote_ToAnotherRetryThatHoldsItNow()
    {
        // On a storage whose chain lock is a lease, a client frozen for longer than the lease loses it: a server
        // puts its step back, and another client retries the chain and holds the step with its own note
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")), ["root", "next", "last"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("last")).Returns(new StateData { Name = AwaitingState.StateName, Data = new Dictionary<string, string>() });
        HoldsWhenAsked("next", FailedState.StateName);
        string another = new JobChains.RetryMarker { Id = "another", Generation = 1, State = FailedState.StateName }.Serialize();
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Callback(() => _notes["next"] = another)
            .Throws(new InvalidOperationException("The storage is down."));

        Assert.ThrowsAny<Exception>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        // The step is held, but not for this retry: it isn't moved, and the other retry's note stays
        _client.Verify(x => x.ChangeState("next", It.IsAny<JobChains.RestoredState>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(another, _notes["next"]);
    }

    [Fact]
    public void RetryChainFrom_ThatFails_AsksToPutItsStepBack_OnlyWhileTheStepIsHeldForIt()
    {
        // Between the client's read and its state change another retry may hold the step: the state it asks for
        // names its retry, so the state change, under the job's lock, refuses it then
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")), ["root", "next", "last"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("last")).Returns(new StateData { Name = AwaitingState.StateName, Data = new Dictionary<string, string>() });
        HoldsWhenAsked("next", FailedState.StateName);
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>())).Throws(new InvalidOperationException("The storage is down."));

        Assert.ThrowsAny<Exception>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        string retryId = JobChains.RetryMarker.Parse(_notes.GetValueOrDefault("next") ?? _heldWith["next"])?.Id;
        Assert.NotNull(retryId);
        _client.Verify(x => x.ChangeState("next", It.Is<JobChains.RestoredState>(state => state.HeldFor == retryId), ScheduledState.StateName), Times.Once);
    }

    [Fact]
    public void RetryChainFrom_ThatFails_PutsItsStepBack_FromItsHold_AndTakesItsNoteOff()
    {
        RecordChain("c", JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")), ["root", "next", "last"]);
        _storage.Connection.Setup(x => x.GetJobParameter("next", JobChains.ChainIdParameter)).Returns("\"c\"");
        _storage.Connection.Setup(x => x.GetStateData("root")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        _storage.Connection.Setup(x => x.GetStateData("last")).Returns(new StateData { Name = AwaitingState.StateName, Data = new Dictionary<string, string>() });
        List<string> order = HoldsWhenAsked("next", FailedState.StateName, new Dictionary<string, string> { ["FailedAt"] = "2026-01-01T00:00:00.0000000Z" }, "It broke");
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>())).Throws(new InvalidOperationException("The storage is down."));

        Assert.ThrowsAny<Exception>(() => JobChains.Retry(_storage.Object, _client.Object, "next"));

        // The state it was in, with its reason and its data, while the lease still keeps a worker from it
        _client.Verify(
            x => x.ChangeState(
                "next",
                It.Is<JobChains.RestoredState>(state => state.Name == FailedState.StateName && state.Reason == "It broke" && state.SerializeData()["FailedAt"] == "2026-01-01T00:00:00.0000000Z"),
                ScheduledState.StateName),
            Times.Once);
        // Its own note, taken off under the job's lock
        Assert.Equal(["lock", "forget", "unlock"], order[^3..]);
        Assert.Null(_notes["next"]);
    }

    // The notes of retries on jobs, as the storage keeps them
    private readonly Dictionary<string, string> _notes = [];

    // The last note written on each job, also once it was taken off
    private readonly Dictionary<string, string> _heldWith = [];

    // A step in a state, which the client's state changes move to the Scheduled state (or another, for a test)
    // once it is held; says in which order the retry locked the job, wrote its note and held the step
    private List<string> HoldsWhenAsked(string jobId, string state, Dictionary<string, string> data = null, string reason = null, string stateOnceHeld = null)
    {
        List<string> order = [];
        bool held = false;

        _storage.Connection.Setup(x => x.GetStateData(jobId))
            .Returns(() => held
                ? new StateData { Name = stateOnceHeld ?? ScheduledState.StateName, Reason = "Chain retried from this step", Data = new Dictionary<string, string>() }
                : new StateData { Name = state, Reason = reason, Data = data ?? [] });
        _storage.Connection.Setup(x => x.SetJobParameter(jobId, JobChains.ChainRetryParameter, It.IsAny<string>()))
            .Callback<string, string, string>((_, _, value) =>
            {
                order.Add(value == null ? "forget" : "note");
                _notes[jobId] = value;
                if (value != null)
                {
                    _heldWith[jobId] = value;
                }
            });
        _storage.Connection.Setup(x => x.GetJobParameter(jobId, JobChains.ChainRetryParameter)).Returns(() => _notes.GetValueOrDefault(jobId));
        _storage.Connection.Setup(x => x.AcquireDistributedLock($"job:{jobId}:state-lock", It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                order.Add("lock");
                Mock<IDisposable> handle = new();
                handle.Setup(x => x.Dispose()).Callback(() => order.Add("unlock"));
                return handle.Object;
            });
        _client.Setup(x => x.ChangeState(jobId, It.IsAny<IState>(), It.IsAny<string>()))
            .Callback<string, IState, string>((_, newState, _) =>
            {
                if (newState is ScheduledState)
                {
                    order.Add("hold");
                    held = true;
                }
            })
            .Returns(true);

        return order;
    }

    [Fact]
    public void IdForJob_IsTheSameForTheSameJob_AndLooksLikeAnyChainId()
    {
        string id = JobChains.IdForJob("12345");

        Assert.Equal(id, JobChains.IdForJob("12345"));
        Assert.NotEqual(id, JobChains.IdForJob("12346"));
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public void CreateOnce_ReturnsTheChainRecordedUnderTheIdentifier_InsteadOfCreatingASecondOne()
    {
        JobChain chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b"));

        JobChainResult first = JobChains.CreateOnce(chain, _client.Object, "occurrence");
        JobChainResult again = JobChains.CreateOnce(chain, _client.Object, "occurrence");

        Assert.Equal("occurrence", first!.ChainId);
        Assert.Equal(first.Steps, again!.Steps);
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()), Times.Exactly(2));
        Assert.Contains("lock:chain:occurrence", _storage.Locks);
    }

    // What a deploy does that renames or removes the method of the steps: the stored definition names a method
    // that can't be loaded
    private void ForgetTheMethods(string chainId)
    {
        Dictionary<string, string> record = _storage.Hashes[JobChains.GetKey(chainId)];
        Assert.Contains("Step", record["Definition"], StringComparison.Ordinal);
        record["Definition"] = record["Definition"].Replace("Step", "Gone", StringComparison.Ordinal);
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
        // Read once more since a step that an abandoned retry holds is put back before the steps are deleted
        _storage.Connection.SetupSequence(x => x.GetStateData("root"))
            .Returns(new StateData { Name = EnqueuedState.StateName })
            .Returns(new StateData { Name = ProcessingState.StateName })
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
            for (int i = 0; i < chain.Levels[level].Steps.Count; i++)
            {
                keys.Add($"{level}.{i}");
            }
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
