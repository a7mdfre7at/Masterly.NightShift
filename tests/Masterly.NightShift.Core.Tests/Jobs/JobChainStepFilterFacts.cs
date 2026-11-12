namespace Masterly.NightShift.Core.Tests;

public sealed class JobChainStepFilterFacts : IDisposable
{
    private const string ChainId = "0123456789abcdef0123456789abcdef";
    private const string JobId = "42";

    private readonly ElectStateContextMock _context = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<JobStorageTransaction> _expiring = new();
    private readonly TimeSpan _grace = JobChainStepFilter.CreationGrace;
    private readonly TimeSpan _slack = JobChains.RecordRefreshSlack;
    private readonly JobChainStepFilter _filter = new();

    public JobChainStepFilterFacts()
    {
        _context.ApplyContext.BackgroundJob.Id = JobId;
        _context.ApplyContext.NewStateObject = new ProcessingState("server", "worker");
        _context.ApplyContext.OldStateName = EnqueuedState.StateName;
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_expiring.Object);
    }

    public void Dispose()
    {
        JobChainStepFilter.CreationGrace = _grace;
        JobChains.RecordRefreshSlack = _slack;
    }

    [Fact]
    public void OnStateElection_ReadsNothing_ForAJobOutsideAChain_WhoseParametersAreLoaded()
    {
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { ["RetryCount"] = "1" };

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        Assert.Empty(_context.ApplyContext.Connection.Invocations);
    }

    [Fact]
    public void OnStateElection_ReadsOneParameter_ForAJobOutsideAChain_WhenTheStorageLoadsNoParameters()
    {
        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        _context.ApplyContext.Connection.Verify(x => x.GetJobParameter(JobId, JobChains.ChainIdParameter), Times.Once);
        Assert.Single(_context.ApplyContext.Connection.Invocations);
    }

    [Fact]
    public void OnStateElection_ReadsTheParameters_OfAStep_WhenTheStorageLoadsNone()
    {
        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainIdParameter)).Returns($"\"{ChainId}\"");
        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainStepParameter)).Returns("\"1.0\"");
        Record(("1.0", "7"));
        Created(TimeSpan.FromHours(1));

        _filter.OnStateElection(_context.Object);

        Assert.Equal(DeletedState.StateName, _context.Object.CandidateState.Name);
    }

    [Fact]
    public void OnStateElection_LetsTheStepRun_WhenTheRecordListsIt_WithOneReadOfTheRecordsJobs()
    {
        Step("1.0");
        Record(("0.0", "41"), ("1.0", JobId));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        _connection.Verify(x => x.GetValueFromHash(JobChains.GetKey(ChainId), "Jobs"), Times.Once);
        _connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ReadsTheWholeRecord_OnAStorageWithoutTheExtendedApi()
    {
        Step("0.0");
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        _connection.Setup(x => x.GetAllEntriesFromHash(JobChains.GetKey(ChainId)))
            .Returns(new Dictionary<string, string> { ["Jobs"] = $"{{\"0.0\":\"{JobId}\"}}" });

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DeletesTheStep_WhenTheRecordListsAnotherJobForIt()
    {
        Step("1.0");
        Record(("0.0", "41"), ("1.0", "77"));
        Created(TimeSpan.FromHours(1));

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Not a current step of chain {ChainId}", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_DeletesTheStep_WhenTheRecordDoesNotHaveItsKey()
    {
        Step("5.0");
        Record(("0.0", "41"));
        Created(TimeSpan.FromHours(1));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<DeletedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_HoldsAYoungStep_WhoseChainIsNotRecordedYet()
    {
        Step("0.0");
        Created(TimeSpan.FromSeconds(10));
        DateTime before = DateTime.UtcNow;

        _filter.OnStateElection(_context.Object);

        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.Equal($"Waiting for chain {ChainId} to be recorded", held.Reason);
        Assert.InRange(held.EnqueueAt, before.AddSeconds(9), DateTime.UtcNow.AddSeconds(11));
    }

    [Fact]
    public void OnStateElection_HoldsAYoungStep_ThatTheRecordDoesNotListYet()
    {
        // A retry or a replacement creates its jobs before it writes the record that lists them
        Step("1.0");
        Record(("1.0", "old"));
        Created(TimeSpan.FromSeconds(30));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(20, 20)]
    [InlineData(200, 60)]
    [InlineData(280, 21)]
    public void OnStateElection_HoldsLongerEachTime_ButNotPastTheGrace(int ageInSeconds, int expectedDelayInSeconds)
    {
        Step("0.0");
        Created(TimeSpan.FromSeconds(ageInSeconds));

        _filter.OnStateElection(_context.Object);

        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.InRange((held.EnqueueAt - DateTime.UtcNow).TotalSeconds, expectedDelayInSeconds - 2, expectedDelayInSeconds + 1);
    }

    [Fact]
    public void OnStateElection_EnqueuesTheStepItHeld_WhenTheRecordWasWrittenMeanwhile()
    {
        // The record is written after the filter read it, and the client looked for a held step before the hold
        // was stored: nobody else would enqueue the step
        Step("0.0");
        _context.ApplyContext.Object.AfterCommit = new PostCommitActions();
        _connection.SetupSequence(x => x.GetValueFromHash(JobChains.GetKey(ChainId), "Jobs"))
            .Returns((string)null)
            .Returns($"{{\"0.0\":\"{JobId}\"}}");
        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(Mock.Of<IDisposable>());
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { Job = _context.ApplyContext.BackgroundJob.Job, State = ScheduledState.StateName });

        _filter.OnStateElection(_context.Object);

        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        _expiring.Verify(x => x.SetJobState(JobId, It.IsAny<IState>()), Times.Never);

        _connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData { Name = ScheduledState.StateName, Reason = held.Reason, Data = held.SerializeData() });
        _context.Object.AfterCommit.Run();

        _expiring.Verify(x => x.SetJobState(JobId, It.Is<IState>(state => state.Name == EnqueuedState.StateName && state.Reason == "The chain is recorded")));
        _expiring.Verify(x => x.Commit());
    }

    [Fact]
    public void OnStateElection_LeavesTheStepItHeld_WhenTheRecordStillDoesNotListIt()
    {
        Step("0.0");
        _context.ApplyContext.Object.AfterCommit = new PostCommitActions();

        _filter.OnStateElection(_context.Object);
        _context.Object.AfterCommit.Run();

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        _connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Once);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void OnStateElection_DeletesTheStep_WhenItsChainWasNeverRecorded_OrWasDeleted()
    {
        Step("0.0");
        Created(TimeSpan.FromMinutes(6));

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Chain {ChainId} isn't recorded: it was deleted or never completely created", deleted.Reason);
        Assert.True(deleted.Reason.Length < 100);
    }

    [Fact]
    public void OnStateElection_LetsTheStepRun_WhenItsChainIsListed_ButTheStorageLostItsRecord()
    {
        // A storage that caps expiration times, or evicts keys, drops a record before its time: the chain is still
        // in the list of chains, which it enters with its record and leaves when it is deleted
        Step("0.0");
        Created(TimeSpan.FromHours(2));
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetSetContains)).Returns(true);
        _connection.Setup(x => x.GetSetContains(JobChains.SetKey, ChainId)).Returns(true);

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ReadsTheListOfChains_OnAStorageThatCanNotLookOneUp()
    {
        Step("0.0");
        Created(TimeSpan.FromHours(2));
        _connection.Setup(x => x.GetAllItemsFromSet(JobChains.SetKey)).Returns([ChainId]);

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_LetsTheStepRun_WhenItsRecordExpired()
    {
        Step("0.0");
        Created(JobChain.RecordRetention + TimeSpan.FromDays(1));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DeletesAnOldStepWithoutRecord_OnAStorageWhoseRecordsNeverExpire()
    {
        Step("0.0");
        Created(JobChain.RecordRetention + TimeSpan.FromDays(1));
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);

        _filter.OnStateElection(_context.Object);

        Assert.IsType<DeletedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_SchedulesTheStepAgain_WhenTheRecordCanNotBeRead()
    {
        Step("0.0");
        _connection.Setup(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));

        _filter.OnStateElection(_context.Object);

        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.Equal($"Waiting: the record of chain {ChainId} couldn't be read", held.Reason);
    }

    [Fact]
    public void OnStateElection_RemembersTheQueue_OfAStepItHolds()
    {
        Step("0.0");
        Created(TimeSpan.FromSeconds(10));
        _connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData
        {
            Name = EnqueuedState.StateName,
            Data = new Dictionary<string, string> { ["Queue"] = "critical" },
        });

        _filter.OnStateElection(_context.Object);

        _connection.Verify(x => x.SetJobParameter(JobId, "HeldQueue", "\"critical\""));
    }

    [Fact]
    public void OnStateElection_ChecksOnlyAJobAboutToRun()
    {
        Step("0.0");
        Created(TimeSpan.FromMinutes(6));
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _filter.OnStateElection(_context.Object);

        Assert.IsType<SucceededState>(_context.Object.CandidateState);
        Assert.Empty(_connection.Invocations);
    }

    [Fact]
    public void OnStateElection_IgnoresChainParameters_ThatAreNotText()
    {
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [JobChains.ChainIdParameter] = "{\"mine\":1}",
            [JobChains.ChainStepParameter] = "5",
        };

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateApplied_KeepsTheRecordLonger_OnceTheStateIsCommitted()
    {
        ApplyStateContext context = Applied(new SucceededState(null, 0, 0));
        _connection.Setup(x => x.GetHashTtl(JobChains.GetKey(ChainId))).Returns(TimeSpan.FromDays(3));

        _filter.OnStateApplied(context, context.Transaction);

        _expiring.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);

        context.AfterCommit.Run();

        _expiring.Verify(x => x.ExpireHash(JobChains.GetKey(ChainId), It.IsInRange(TimeSpan.FromDays(29.9), TimeSpan.FromDays(30), Moq.Range.Inclusive)));
        _expiring.Verify(x => x.Commit());
    }

    [Fact]
    public void OnStateApplied_KeepsTheRecord_UntilAfterAScheduledStepIsDue()
    {
        ApplyStateContext context = Applied(new ScheduledState(TimeSpan.FromDays(60)));
        _connection.Setup(x => x.GetHashTtl(JobChains.GetKey(ChainId))).Returns(TimeSpan.FromDays(30));

        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();

        _expiring.Verify(x => x.ExpireHash(JobChains.GetKey(ChainId), It.IsInRange(TimeSpan.FromDays(89.9), TimeSpan.FromDays(90), Moq.Range.Inclusive)));
    }

    [Fact]
    public void OnStateApplied_NeverShortensTheRecordsLife_AndReadsItOnceInAWhile()
    {
        ApplyStateContext context = Applied(new SucceededState(null, 0, 0));
        _connection.Setup(x => x.GetHashTtl(JobChains.GetKey(ChainId))).Returns(TimeSpan.FromDays(90));

        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();
        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();

        _expiring.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        _connection.Verify(x => x.GetHashTtl(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void OnStateApplied_DoesNothing_ForAStepCreatedLately_WhoseRecordIsNew()
    {
        ApplyStateContextMock mock = ApplyMock(new SucceededState(null, 0, 0));
        mock.BackgroundJob.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        mock.Object.AfterCommit = new PostCommitActions();

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);
        mock.Object.AfterCommit.Run();

        Assert.Empty(_connection.Invocations);
    }

    [Fact]
    public void OnStateApplied_KeepsTheRecord_OfAStepCreatedLately_ThatIsScheduledFarAhead()
    {
        ApplyStateContextMock mock = ApplyMock(new ScheduledState(TimeSpan.FromDays(10)));
        mock.BackgroundJob.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        mock.Object.AfterCommit = new PostCommitActions();
        _connection.Setup(x => x.GetHashTtl(It.IsAny<string>())).Returns(TimeSpan.FromDays(30));

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);
        mock.Object.AfterCommit.Run();

        _expiring.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsInRange(TimeSpan.FromDays(39.9), TimeSpan.FromDays(40), Moq.Range.Inclusive)));
    }

    [Fact]
    public void OnStateApplied_DoesNothing_ForAChainThatIsNotRecorded()
    {
        ApplyStateContext context = Applied(new SucceededState(null, 0, 0));
        _connection.Setup(x => x.GetHashTtl(It.IsAny<string>())).Returns(TimeSpan.FromSeconds(-1));

        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();

        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void OnStateApplied_NeverThrows_WhenTheStorageFails()
    {
        ApplyStateContext context = Applied(new SucceededState(null, 0, 0));
        _connection.Setup(x => x.GetHashTtl(It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));

        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();
    }

    [Fact]
    public void OnStateApplied_DoesNothing_ForAJobBeingCreated_OrOutsideAChain_OrWithoutExpiringRecords()
    {
        ApplyStateContextMock created = ApplyMock(new EnqueuedState());
        created.OldStateName = null;
        created.Object.AfterCommit = new PostCommitActions();
        _filter.OnStateApplied(created.Object, created.Object.Transaction);
        created.Object.AfterCommit.Run();

        ApplyStateContextMock plain = ApplyMock(new SucceededState(null, 0, 0));
        plain.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();
        plain.Object.AfterCommit = new PostCommitActions();
        _filter.OnStateApplied(plain.Object, plain.Object.Transaction);
        plain.Object.AfterCommit.Run();

        ApplyStateContextMock persistent = ApplyMock(new SucceededState(null, 0, 0));
        persistent.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        persistent.Object.AfterCommit = new PostCommitActions();
        _filter.OnStateApplied(persistent.Object, persistent.Object.Transaction);
        persistent.Object.AfterCommit.Run();

        Assert.Empty(_connection.Invocations);
    }

    private void Step(string key)
    {
        _connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
        _context.ApplyContext.Connection = _connection.As<IStorageConnection>();
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [JobChains.ChainIdParameter] = $"\"{ChainId}\"",
            [JobChains.ChainStepParameter] = $"\"{key}\"",
        };
    }

    private void Created(TimeSpan ago) => _context.ApplyContext.BackgroundJob.CreatedAt = DateTime.UtcNow - ago;

    private void Record(params (string Key, string JobId)[] jobs)
    {
        string serialized = "{" + string.Join(",", jobs.Select(static x => $"\"{x.Key}\":\"{x.JobId}\"")) + "}";
        _connection.Setup(x => x.GetValueFromHash(JobChains.GetKey(ChainId), "Jobs")).Returns(serialized);
        _context.ApplyContext.Connection.Setup(x => x.GetAllEntriesFromHash(JobChains.GetKey(ChainId)))
            .Returns(new Dictionary<string, string> { ["Jobs"] = serialized });
    }

    private ApplyStateContextMock ApplyMock(IState newState)
    {
        // Each test's chain is its own, so what one test learned about a record's expiry doesn't reach another
        string chainId = Guid.NewGuid().ToString("N");

        ApplyStateContextMock mock = new()
        {
            Connection = _connection.As<IStorageConnection>(),
            NewStateObject = newState,
            OldStateName = ProcessingState.StateName,
        };
        mock.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        mock.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { [JobChains.ChainIdParameter] = $"\"{chainId}\"" };
        mock.BackgroundJob.CreatedAt = DateTime.UtcNow.AddDays(-2);
        return mock;
    }

    private ApplyStateContext Applied(IState newState)
    {
        ApplyStateContextMock mock = ApplyMock(newState);
        mock.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { [JobChains.ChainIdParameter] = $"\"{ChainId}\"" };
        JobChains.ForgetExpiry(ChainId);

        ApplyStateContext context = mock.Object;
        context.AfterCommit = new PostCommitActions();
        return context;
    }
}
