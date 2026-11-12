namespace Masterly.NightShift.Core.Tests;

public sealed class JobChainStepFilterFacts : IDisposable
{
    private const string ChainId = "0123456789abcdef0123456789abcdef";
    private const string JobId = "42";

    private readonly ElectStateContextMock _context = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<JobStorageTransaction> _expiring = new();
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
    public void OnStateElection_DeletesTheStep_WhenTheRecordListsAnotherJobForIt_HoweverYoungTheJobIs()
    {
        Step("1.0");
        Record(("0.0", "41"), ("1.0", "77"));

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Not a current step of chain {ChainId}", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_DeletesTheStep_WhenTheRecordDoesNotHaveItsKey()
    {
        Step("5.0");
        Record(("0.0", "41"));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<DeletedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_HoldsTheStep_WhileItsChainIsBeingCreated()
    {
        // The client wrote the record first, with its lease and no step yet
        Step("0.0");
        Record(Lease(since: TimeSpan.FromSeconds(10), until: TimeSpan.FromSeconds(110)));
        DateTime before = DateTime.UtcNow;

        _filter.OnStateElection(_context.Object);

        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.Equal($"Waiting for chain {ChainId} to be recorded", held.Reason);
        Assert.InRange(held.EnqueueAt, before.AddSeconds(9), DateTime.UtcNow.AddSeconds(11));
    }

    [Fact]
    public void OnStateElection_HoldsAStepTheRecordLists_WhileItsChainIsBeingChanged()
    {
        // A replacement or a retry in progress: nothing of the chain starts, not even what the record lists
        Step("0.0");
        Record(("0.0", JobId), Lease(since: TimeSpan.FromSeconds(1), until: TimeSpan.FromSeconds(119)));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_HoldsTheStep_HoweverOldTheJobIs_AndWhateverTheClockOfTheClientSaid()
    {
        // The job's creation time plays no part: a chain may take as long as it needs to be created
        Step("0.0");
        Created(TimeSpan.FromDays(3));
        Record(Lease(since: TimeSpan.FromHours(2), until: TimeSpan.FromSeconds(60)));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Theory]
    [InlineData(0, 110, 2)]
    [InlineData(20, 100, 20)]
    [InlineData(600, 110, 60)]
    [InlineData(600, 20, 21)]
    public void OnStateElection_HoldsLongerTheLongerTheChangeLasts_ButNotMuchPastItsLease(int lastedSeconds, int remainingSeconds, int expectedDelayInSeconds)
    {
        Step("0.0");
        Record(Lease(since: TimeSpan.FromSeconds(lastedSeconds), until: TimeSpan.FromSeconds(remainingSeconds)));

        _filter.OnStateElection(_context.Object);

        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.InRange((held.EnqueueAt - DateTime.UtcNow).TotalSeconds, expectedDelayInSeconds - 2, expectedDelayInSeconds + 1);
    }

    [Fact]
    public void OnStateElection_ReadsTheLease_WithTheClockOfTheStorage()
    {
        // The server's clock is an hour ahead of the storage's, which the client wrote the lease with
        Step("0.0");
        DateTime storageNow = DateTime.UtcNow.AddHours(-1);
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);
        _connection.Setup(x => x.GetUtcDateTime()).Returns(storageNow);
        Record(("0.0", JobId), ("changing", $"{JobHelper.SerializeDateTime(storageNow.AddSeconds(-5))}/{JobHelper.SerializeDateTime(storageNow.AddSeconds(100))}"));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DoesNotReadTheClock_OfAChainThatIsNotBeingChanged()
    {
        Step("0.0");
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);
        Record(("0.0", JobId));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        _connection.Verify(x => x.GetUtcDateTime(), Times.Never);
        Assert.Single(_connection.Invocations);
    }

    [Fact]
    public void OnStateElection_LetsAListedStepRun_OnceTheLeaseOfAClientThatStoppedLapsed()
    {
        // A replacement that stopped halfway: the record still lists the old steps
        Step("0.0");
        Record(("0.0", JobId), Lease(since: TimeSpan.FromMinutes(10), until: TimeSpan.FromSeconds(-1)));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DeletesAStepTheRecordDoesNotList_OnceTheLeaseOfAClientThatStoppedLapsed()
    {
        Step("0.0");
        Record(("0.0", "old"), Lease(since: TimeSpan.FromMinutes(10), until: TimeSpan.FromSeconds(-1)));

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Not a current step of chain {ChainId}", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_DeletesTheStep_OfAChainWhoseCreationStoppedHalfway()
    {
        // All there is of the chain: a record that lists no step, with a lease nobody renews
        Step("0.0");
        Record(Lease(since: TimeSpan.FromMinutes(10), until: TimeSpan.FromSeconds(-1)));

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Chain {ChainId} isn't recorded: it was deleted or never completely created", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_DeletesTheStep_WhenItsChainWasDeleted()
    {
        Step("0.0");

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
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetSetContains)).Returns(true);
        _connection.Setup(x => x.GetSetContains(JobChains.SetKey, ChainId)).Returns(true);

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ReadsTheListOfChains_OnAStorageThatCanNotLookOneUp()
    {
        Step("0.0");
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
    public void OnStateElection_SchedulesTheStepAgain_WhenTheRecordCanNotBeRead_LaterEachTime()
    {
        _context.ApplyContext.BackgroundJob.Id = Guid.NewGuid().ToString("N");
        Step("0.0");
        _connection.Setup(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));
        List<double> delays = [];

        for (int attempt = 0; attempt < 9; attempt++)
        {
            ElectStateContext context = new(_context.ApplyContext.Object);
            _filter.OnStateElection(context);

            ScheduledState held = Assert.IsType<ScheduledState>(context.CandidateState);
            Assert.Equal($"Waiting: the record of chain {ChainId} couldn't be read", held.Reason);
            delays.Add(Math.Round((held.EnqueueAt - DateTime.UtcNow).TotalSeconds));
        }

        Assert.Equal([15, 30, 60, 120, 240, 480, 900, 900, 900], delays);
    }

    [Fact]
    public void OnStateElection_StartsOverWithShortDelays_OnceTheRecordWasReadAgain()
    {
        _context.ApplyContext.BackgroundJob.Id = Guid.NewGuid().ToString("N");
        Step("0.0");
        _connection.SetupSequence(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("Storage is down"))
            .Throws(new InvalidOperationException("Storage is down"))
            .Returns("{}")
            .Throws(new InvalidOperationException("Storage is down"));
        List<double> delays = [];

        for (int attempt = 0; attempt < 4; attempt++)
        {
            ElectStateContext context = new(_context.ApplyContext.Object);
            _filter.OnStateElection(context);
            delays.Add(context.CandidateState is ScheduledState held ? Math.Round((held.EnqueueAt - DateTime.UtcNow).TotalSeconds) : -1);
        }

        Assert.Equal([15, 30, -1, 15], delays);
    }

    [Fact]
    public void OnStateElection_RemembersTheQueue_OfAStepItHolds()
    {
        Step("0.0");
        Record(Lease(since: TimeSpan.FromSeconds(10), until: TimeSpan.FromSeconds(100)));
        _connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData
        {
            Name = EnqueuedState.StateName,
            Data = new Dictionary<string, string> { ["Queue"] = "critical" },
        });

        _filter.OnStateElection(_context.Object);

        _connection.Verify(x => x.SetJobParameter(JobId, "HeldQueue", "\"critical\""));
    }

    [Fact]
    public void OnStateElection_LeavesTheQueueOfAHeldJobAlone_WhenTheJobIsEnqueued()
    {
        // The filter runs first: restoring the queue here would forget it before a later filter (a schedule
        // policy) schedules the job again. DrainModeFilter, which runs after those, restores it.
        _context.ApplyContext.NewStateObject = new EnqueuedState();
        _context.ApplyContext.OldStateName = ScheduledState.StateName;
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { ["HeldQueue"] = "\"critical\"" };

        _filter.OnStateElection(_context.Object);

        Assert.Equal(EnqueuedState.DefaultQueue, Assert.IsType<EnqueuedState>(_context.Object.CandidateState).Queue);
        Assert.Empty(_context.ApplyContext.Connection.Invocations);
        Assert.Empty(_context.ApplyContext.Transaction.Invocations);
    }

    [Fact]
    public void DefaultFilters_KeepTheQueueOfAHeldJob_WhenItsSchedulePolicyMovesItForwardAgain()
    {
        // The whole default chain of filters, in its order: this filter runs first, and must leave the queue to
        // DrainModeFilter, which restores it after the policy filter had its say
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        PolicyStorage storage = new();
        storage.Add(new SchedulePolicy("closed").ExcludeDate(today).ExcludeDate(today.AddDays(1)));
        StateMachine machine = new(JobFilterProviders.Providers);

        // Due while the policy blocks: moved forward, and the queue it was held from is still remembered
        ApplyStateContextMock blocked = DueJob(storage);
        IState moved = machine.ApplyState(blocked.Object);

        Assert.IsType<ScheduledState>(moved);
        storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), "HeldQueue", It.IsAny<string>()), Times.Never);

        // Due once the policy allows it: enqueued to that queue, which is then forgotten
        storage.Add(new SchedulePolicy("closed"), revision: 2);
        ApplyStateContextMock allowed = DueJob(storage);
        IState enqueued = machine.ApplyState(allowed.Object);

        Assert.Equal("critical", Assert.IsType<EnqueuedState>(enqueued).Queue);
        storage.Connection.Verify(x => x.SetJobParameter(JobId, "HeldQueue", null), Times.Once);
    }

    private static ApplyStateContextMock DueJob(PolicyStorage storage)
    {
        ApplyStateContextMock context = new()
        {
            Storage = storage.Storage,
            Connection = storage.Connection,
            NewStateObject = new EnqueuedState { IsDue = true },
            OldStateName = ScheduledState.StateName,
        };
        context.BackgroundJob.Id = JobId;
        context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [ScheduleOptions.PolicyParameter] = "\"closed\"",
            ["HeldQueue"] = "\"critical\"",
        };
        return context;
    }

    [Fact]
    public void OnStateElection_ChecksOnlyAJobAboutToRun()
    {
        Step("0.0");
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

    // A lease that began a while ago and lasts a while more (or lapsed, with a negative time)
    private static (string Key, string JobId) Lease(TimeSpan since, TimeSpan until)
    {
        DateTime now = DateTime.UtcNow;
        return ("changing", $"{JobHelper.SerializeDateTime(now - since)}/{JobHelper.SerializeDateTime(now + until)}");
    }

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
