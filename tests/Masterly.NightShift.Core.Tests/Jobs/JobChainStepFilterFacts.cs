using Masterly.NightShift.Logging;

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
    public void OnStateElection_GivesTheStepBack_WithoutWritingAnything_WhileItsChainIsBeingCreated_ForAMoment()
    {
        // A worker fetched the first step a few milliseconds after the client began to create the chain
        JobGiveBack giveBack = FetchedByAWorker();
        Step("0.0");
        Record(Lease(since: TimeSpan.FromMilliseconds(40), until: TimeSpan.FromSeconds(119)));

        _filter.OnStateElection(_context.Object);

        // No state, no queue to remember, no write at all: the worker puts the job back as it is
        Assert.True(giveBack.Requested);
        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        Assert.Empty(_context.Object.TraversedStates);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        Assert.Empty(_context.ApplyContext.Transaction.Invocations);
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(4, 10, 10)]
    [InlineData(60, 60, 200)]
    [InlineData(1000, 250, 250)]
    [InlineData(4000, 250, 250)]
    public void OnStateElection_KeepsAStepItGivesBack_ForAsLongAsTheCreationHasLasted_WithinBounds(int lastedMs, int atLeastMs, int atMostMs)
    {
        JobGiveBack giveBack = FetchedByAWorker();
        Step("0.0");
        Record(Lease(since: TimeSpan.FromMilliseconds(lastedMs), until: TimeSpan.FromSeconds(100)));

        _filter.OnStateElection(_context.Object);

        Assert.True(giveBack.Requested);
        Assert.InRange(giveBack.Pause, TimeSpan.FromMilliseconds(atLeastMs), TimeSpan.FromMilliseconds(atMostMs));
        Assert.InRange(giveBack.Pause, JobChainStepFilter.MinGiveBackPause, JobChainStepFilter.MaxGiveBackPause);
    }

    [Fact]
    public void OnStateElection_HoldsTheStep_OnceTheCreationOfItsChainTakesUnusuallyLong()
    {
        // Longer than a creation ever takes: the client is slow, or has stopped
        JobGiveBack giveBack = FetchedByAWorker();
        Step("0.0");
        Record(Lease(since: JobChainStepFilter.QuietTime + TimeSpan.FromSeconds(1), until: TimeSpan.FromSeconds(100)));

        _filter.OnStateElection(_context.Object);

        Assert.False(giveBack.Requested);
        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.Equal($"Waiting for chain {ChainId} to be recorded", held.Reason);
    }

    [Fact]
    public void OnStateElection_HoldsTheStep_OfAChainThatIsBeingRetriedOrReplaced_HoweverShortlyThatBegan()
    {
        // The record lists steps: not a creation
        JobGiveBack giveBack = FetchedByAWorker();
        Step("0.0");
        Record(("0.0", JobId), Lease(since: TimeSpan.FromMilliseconds(40), until: TimeSpan.FromSeconds(119)));

        _filter.OnStateElection(_context.Object);

        Assert.False(giveBack.Requested);
        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_HoldsTheStep_WhenTheStateChangeDoesNotComeFromAWorkerThatCanGiveItBack()
    {
        Step("0.0");
        Record(Lease(since: TimeSpan.FromMilliseconds(40), until: TimeSpan.FromSeconds(119)));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Theory]
    [InlineData("Processing")]
    [InlineData("Scheduled")]
    public void OnStateElection_HoldsTheStep_ThatIsNotWaitingInItsQueue(string currentState)
    {
        // Picked up again after its server stopped, or fetched from a queue it was left in: not the first step of
        // a chain that is being created
        JobGiveBack giveBack = FetchedByAWorker();
        _context.ApplyContext.OldStateName = currentState;
        Step("0.0");
        Record(Lease(since: TimeSpan.FromMilliseconds(40), until: TimeSpan.FromSeconds(119)));

        _filter.OnStateElection(_context.Object);

        Assert.False(giveBack.Requested);
        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_HoldsTheStep_WhenTheCreationBeganInTheFuture_ByTheClockItIsReadWith()
    {
        // The clocks of the client and of the server disagree (a storage without a clock of its own): nothing
        // would bound how long the step goes back and forth
        JobGiveBack giveBack = FetchedByAWorker();
        Step("0.0");
        Record(Lease(since: TimeSpan.FromSeconds(-30), until: TimeSpan.FromSeconds(150)));

        _filter.OnStateElection(_context.Object);

        Assert.False(giveBack.Requested);
        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_LetsTheStepRun_OnceTheRecordListsIt_AlsoForAWorkerThatCouldGiveItBack()
    {
        JobGiveBack giveBack = FetchedByAWorker();
        Step("0.0");
        Record(("0.0", JobId));

        _filter.OnStateElection(_context.Object);

        Assert.False(giveBack.Requested);
        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
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
        // Old enough for its record to have expired, and marked as a step that was recorded
        Step("0.0", recorded: true);
        Created(JobChain.RecordRetention + TimeSpan.FromDays(1));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_LetsTheStepRun_OnceTheTimeItsMarkSaysTheRecordWasKeptIsOver_WhateverThisProcessIsSetTo()
    {
        // Recorded by a process that keeps records for a day: two days later the record expired, although this
        // process keeps records for thirty
        Step("0.0");
        Mark("86400");
        Created(TimeSpan.FromDays(2));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DeletesAStepWithoutRecord_ThatIsYoungerThanItsMarkSaysTheRecordIsKept_WhateverThisProcessIsSetTo()
    {
        // Recorded by a process that keeps records for ninety days: after forty the record can't have expired,
        // although this process keeps records for a day, so the chain was deleted
        TimeSpan retention = JobChain.RecordRetention;
        JobChain.RecordRetention = TimeSpan.FromDays(1);

        try
        {
            Step("0.0");
            Mark("7776000");
            Created(TimeSpan.FromDays(40));

            _filter.OnStateElection(_context.Object);
        }
        finally
        {
            JobChain.RecordRetention = retention;
        }

        Assert.IsType<DeletedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_GoesByItsOwnSetting_ForAMarkOfAnEarlierBuild_ThatDoesNotSayHowLongTheRecordIsKept()
    {
        TimeSpan retention = JobChain.RecordRetention;
        JobChain.RecordRetention = TimeSpan.FromDays(2);

        try
        {
            Step("0.0", recorded: true);
            Created(TimeSpan.FromDays(3));

            _filter.OnStateElection(_context.Object);
        }
        finally
        {
            JobChain.RecordRetention = retention;
        }

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Theory]
    [InlineData("Failed", "Enqueued")]
    [InlineData("Failed", "Deleted")]
    [InlineData("Failed", "Scheduled")]
    [InlineData("Succeeded", "Enqueued")]
    [InlineData("Deleted", "Enqueued")]
    public void OnStateApplied_HasTheChainLookedAtSoon_WhenAStepLeavesTheStateItFinishedIn_OnceThatIsCommitted(string from, string to)
    {
        ApplyStateContext context = Left(from, to);

        _filter.OnStateApplied(context, context.Transaction);

        // Nothing in the state change's own transaction, and nothing before it is committed
        Assert.Empty(_expiring.Invocations);
        Assert.Empty(Mock.Get(context.Transaction).Invocations);

        context.AfterCommit.Run();

        // In an entry of its own, which only the look it asks for removes: the chain's place among the chains to
        // look at, which a look in progress is about to write from what it read before, is left alone
        _expiring.Verify(x => x.AddToSet("chains:soon", It.Is<string>(entry => entry.StartsWith(ChainId + "|", StringComparison.Ordinal)), It.IsInRange(
            (double)JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(4)), (double)JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(6)), Moq.Range.Inclusive)), Times.Once);
        _expiring.Verify(x => x.AddToSet(It.IsNotIn("chains:soon"), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _expiring.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _expiring.Verify(x => x.Commit());
    }

    [Theory]
    [InlineData("Enqueued", "Processing")]
    [InlineData("Processing", "Succeeded")]
    [InlineData("Processing", "Failed")]
    [InlineData("Processing", "Scheduled")]
    [InlineData("Scheduled", "Enqueued")]
    [InlineData("Awaiting", "Enqueued")]
    [InlineData("Failed", "Failed")]
    public void OnStateApplied_WritesNothing_ForTheStateChangesOfAStepThatRuns(string from, string to)
    {
        ApplyStateContext context = Left(from, to);

        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();

        Assert.Empty(_connection.Invocations);
        Assert.Empty(_expiring.Invocations);
    }

    [Fact]
    public void OnStateApplied_ReadsTheChain_OfAJobThatLeavesTheFailedState_WhenTheStorageLoadsNoParameters_AfterTheCommit()
    {
        ApplyStateContextMock mock = LeftMock(FailedState.StateName, EnqueuedState.StateName);
        mock.BackgroundJob.ParametersSnapshot = null;
        _connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainIdParameter)).Returns($"\"{ChainId}\"");
        ApplyStateContext context = WithCommit(mock);

        _connection.Setup(x => x.GetHashTtl(JobChains.GetKey(ChainId))).Returns(TimeSpan.FromDays(3));
        JobChains.ForgetExpiry(ChainId);

        _filter.OnStateApplied(context, context.Transaction);
        Assert.Empty(_connection.Invocations);

        context.AfterCommit.Run();

        _expiring.Verify(x => x.AddToSet("chains:soon", It.Is<string>(entry => entry.StartsWith(ChainId + "|", StringComparison.Ordinal)), It.IsAny<double>()));

        // And its record is kept longer, which is done at each state change only where the parameters are loaded
        _expiring.Verify(x => x.ExpireHash(JobChains.GetKey(ChainId), It.IsInRange(TimeSpan.FromDays(29.9), TimeSpan.FromDays(30), Moq.Range.Inclusive)));
    }

    [Fact]
    public void OnStateApplied_WritesNothing_ForAJobOutsideAChain_ThatLeavesTheFailedState()
    {
        ApplyStateContextMock loaded = LeftMock(FailedState.StateName, EnqueuedState.StateName);
        loaded.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { ["RetryCount"] = "3" };
        ApplyStateContext context = WithCommit(loaded);
        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();

        Assert.Empty(_connection.Invocations);

        // Its parameters aren't loaded with it: one read tells, after the commit
        ApplyStateContextMock unloaded = LeftMock(FailedState.StateName, EnqueuedState.StateName);
        unloaded.BackgroundJob.ParametersSnapshot = null;
        context = WithCommit(unloaded);
        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();

        _connection.Verify(x => x.GetJobParameter(JobId, JobChains.ChainIdParameter), Times.Once);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void OnStateApplied_NeverThrows_WhenTheChainCanNotBeGivenALook()
    {
        ApplyStateContext context = Left(FailedState.StateName, EnqueuedState.StateName);
        _expiring.Setup(x => x.Commit()).Throws(new InvalidOperationException("Storage is down"));

        _filter.OnStateApplied(context, context.Transaction);
        context.AfterCommit.Run();
    }

    [Fact]
    public void OnStateApplied_AsksForALook_InANewEntryEachTime_SoNoneReplacesAnother()
    {
        List<string> entries = [];
        _expiring.Setup(x => x.AddToSet("chains:soon", It.IsAny<string>(), It.IsAny<double>()))
            .Callback<string, string, double>((_, entry, _) => entries.Add(entry));

        foreach (string from in (string[])[FailedState.StateName, SucceededState.StateName, DeletedState.StateName])
        {
            ApplyStateContext context = Left(from, EnqueuedState.StateName);
            _filter.OnStateApplied(context, context.Transaction);
            context.AfterCommit.Run();
        }

        // A look that removes the entry it answered can't take one that was asked for since with it
        Assert.Equal(3, entries.Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry => Assert.StartsWith(ChainId + "|", entry, StringComparison.Ordinal));
        Assert.All(entries, static entry => Assert.InRange(entry.Length, 1, 256));
    }

    [Fact]
    public void OnStateElection_DeletesAnOldStep_OfAChainThatWasNeverCompletelyCreated()
    {
        // Scheduled far ahead by a client that stopped before it recorded the chain, and due after what it started
        // of the record is gone: old, but never listed
        Step("0.0");
        Created(JobChain.RecordRetention + TimeSpan.FromDays(16));

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Chain {ChainId} isn't recorded: it was deleted or never completely created", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_ReadsTheMark_OfAnOldStep_WhenTheStorageLoadsNoParameters()
    {
        _context.ApplyContext.Connection = _connection.As<IStorageConnection>();
        _connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
        _connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainIdParameter)).Returns($"\"{ChainId}\"");
        _connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainStepParameter)).Returns("\"0.0\"");
        _connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainRecordedParameter)).Returns("true");
        Created(JobChain.RecordRetention + TimeSpan.FromDays(1));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DoesNotHoldAStep_ForALeaseThatEndsMuchLaterThanOneIsEverWritten()
    {
        // A damaged record mustn't hold the steps of its chain for good
        Step("0.0");
        Record(("0.0", JobId), Lease(since: TimeSpan.FromMinutes(1), until: TimeSpan.FromDays(400)));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_HoldsAStep_ForALeaseWrittenWithAClockSomewhatAhead()
    {
        // Where the storage has no clock, the client's: a few minutes ahead of this server's
        Step("0.0");
        Record(("0.0", JobId), Lease(since: TimeSpan.FromMinutes(-10), until: TimeSpan.FromMinutes(12)));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_NeverThrows_WhenTheQueueOfAStepItHoldsCanNotBeKept()
    {
        Step("0.0");
        Record(Lease(since: TimeSpan.FromSeconds(10), until: TimeSpan.FromSeconds(100)));
        _connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData
        {
            Name = EnqueuedState.StateName,
            Data = new Dictionary<string, string> { ["Queue"] = "critical" },
        });
        _connection.Setup(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_NeverThrows_WhenNothingOfTheStorageWorks()
    {
        // Not even from the path that handles a record it can't read
        Step("0.0");
        _connection.Setup(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));
        _connection.Setup(x => x.GetStateData(It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));
        _connection.Setup(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));
        _connection.Setup(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact, GlobalLock(Reason = "Replaces the log provider")]
    public void OnStateElection_WarnsOnceForAJob_WhoseRecordCanNotBeRead_ThenStaysQuiet()
    {
        string jobId = Guid.NewGuid().ToString("N");
        _context.ApplyContext.BackgroundJob.Id = jobId;
        Step("0.0");
        _connection.Setup(x => x.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("Storage is down"));
        RecordingLogProvider log = new();

        using (log.Use())
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                _filter.OnStateElection(new ElectStateContext(_context.ApplyContext.Object));
            }
        }

        List<RecordingLogProvider.LogEntry> entries = [.. log.Entries.Where(x => x.Message.Contains(jobId, StringComparison.Ordinal))];
        Assert.Single(entries, static x => x.Level == LogLevel.Warn);
        Assert.Equal(3, entries.Count(static x => x.Level == LogLevel.Debug));
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

    [Fact]
    public void OnStateElection_WritesTheNoteOfARetry_WithTheStateThatHoldsItsStep_WhereTheStorageCan()
    {
        JobChains.RetryHold hold = new(DateTime.UtcNow.AddMinutes(2), Note(generation: 3)) { Reason = "Chain retried from this step" };
        _context.ApplyContext.NewStateObject = hold;
        _context.ApplyContext.OldStateName = FailedState.StateName;
        _context.ApplyContext.Transaction = _expiring.As<IWriteOnlyTransaction>();
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(true);

        _filter.OnStateElection(_context.Object);

        // In the transaction of the state change: the step is never held without it, nor marked without being held
        _expiring.Verify(x => x.SetJobParameter(JobId, "ChainRetry", hold.Marker), Times.Once);
        Assert.True(hold.Marked);
        Assert.Same(hold, _context.Object.CandidateState);
        Assert.Empty(_context.ApplyContext.Connection.Invocations);
    }

    [Fact]
    public void OnStateElection_LeavesTheNoteOfARetry_ToTheClient_WhereTheStorageCanNotWriteItWithTheState()
    {
        JobChains.RetryHold hold = new(DateTime.UtcNow.AddMinutes(2), Note(generation: 3));
        _context.ApplyContext.NewStateObject = hold;
        _context.ApplyContext.OldStateName = FailedState.StateName;
        _context.ApplyContext.Transaction = _expiring.As<IWriteOnlyTransaction>();

        _filter.OnStateElection(_context.Object);

        Assert.False(hold.Marked);
        _expiring.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(_context.ApplyContext.Connection.Invocations);
    }

    [Fact]
    public void OnStateElection_PutsAStepBack_WhoseRetryWasNeverRecorded_AndDoesNotRunIt()
    {
        // Held for a retry that began at generation 3; nobody says any more that the chain is being changed, and
        // the record is still at generation 3
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName, data: new Dictionary<string, string> { ["FailedAt"] = "2026-01-01T00:00:00.0000000Z", ["ExceptionMessage"] = "It broke" });
        Record(3, ("0.0", "41"), ("1.0", JobId), ("2.0", "43"));

        _filter.OnStateElection(_context.Object);

        // The state it was in, with its data and a reason that says why it is there again; not a new failure
        JobChains.RestoredState restored = Assert.IsType<JobChains.RestoredState>(_context.Object.CandidateState);
        Assert.Equal(FailedState.StateName, restored.Name);
        Assert.Equal($"The retry of chain {ChainId} was abandoned", restored.Reason);
        Assert.False(restored.IsFinal);
        Assert.Equal("It broke", restored.SerializeData()["ExceptionMessage"]);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", restored.SerializeData()["FailedAt"]);

        // And not by way of the Processing state: the job's history doesn't say that it ran
        Assert.Empty(_context.Object.TraversedStates);
    }

    [Theory]
    [InlineData("Succeeded", true)]
    [InlineData("Deleted", true)]
    [InlineData("Failed", false)]
    public void OnStateElection_PutsAStepBack_IntoAStateThatIsFinalOrNot_AsTheStateItWasInIs(string state, bool final)
    {
        // So the job expires again when it had finished, and stays when it had failed
        Step("1.0");
        HeldForARetry(generation: 1, state: state);
        Record(1, ("0.0", "41"), ("1.0", JobId));

        _filter.OnStateElection(_context.Object);

        Assert.Equal(state, _context.Object.CandidateState.Name);
        Assert.Equal(final, _context.Object.CandidateState.IsFinal);
    }

    [Fact]
    public void OnStateElection_TakesTheNoteOfAnAbandonedRetryOff_InTheTransactionThatPutsTheStepBack()
    {
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName);
        Record(3, ("0.0", "41"), ("1.0", JobId));
        _context.ApplyContext.Transaction = _expiring.As<IWriteOnlyTransaction>();
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(true);

        _filter.OnStateElection(_context.Object);

        _expiring.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Once);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_TakesTheNoteOfAnAbandonedRetryOff_OnlyOnceTheStepIsPutBack_WhereTheStorageCanNotDoBothAtOnce()
    {
        // Were it taken off before, a state change that fails would leave a step that runs when it is tried again
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName);
        Record(3, ("0.0", "41"), ("1.0", JobId));
        _context.ApplyContext.Object.AfterCommit = new PostCommitActions();

        _filter.OnStateElection(_context.Object);

        _connection.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Never);

        _context.ApplyContext.Object.AfterCommit.Run();

        _connection.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Once);
    }

    [Fact]
    public void OnStateElection_HoldsAStepHeldForARetry_WhileTheClientStillSaysItIsAtIt_AndKeepsItsNote()
    {
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName);
        Record(3, ("0.0", "41"), ("1.0", JobId), Lease(since: TimeSpan.FromMinutes(3), until: TimeSpan.FromSeconds(50)));
        _context.ApplyContext.Object.AfterCommit = new PostCommitActions();

        _filter.OnStateElection(_context.Object);
        _context.ApplyContext.Object.AfterCommit.Run();

        ScheduledState held = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.Equal($"Waiting for chain {ChainId} to be recorded", held.Reason);
        _connection.Verify(x => x.SetJobParameter(JobId, "ChainRetry", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_LetsAStepHeldForARetryRun_OnceTheRetryIsRecorded()
    {
        // The record is past the generation the retry started from: the retry happened
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName);
        Record(4, ("0.0", "41"), ("1.0", JobId), ("2.0", "50"));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DecidesAStepHeldForARetry_AsAnyStep_WhenTheRecordIsGoneOrWasWrittenAnew()
    {
        // The chain was deleted while the step was held
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName);
        _connection.Setup(x => x.GetAllEntriesFromHash(JobChains.GetKey(ChainId))).Returns((Dictionary<string, string>)null);
        _context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Chain {ChainId} isn't recorded: it was deleted or never completely created", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_DeletesAStepHeldForARetry_ThatARecordOfAnotherGenerationDoesNotList()
    {
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName);
        Record(1, ("0.0", "90"), ("1.0", "91"));

        _filter.OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal($"Not a current step of chain {ChainId}", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_TakesNoAccountOfTheNoteOfARetry_OnAJobPickedUpAgainWhileItWasProcessing()
    {
        // The note couldn't be taken off when the step was let run: it is stale, and must not put the job back
        Step("1.0");
        HeldForARetry(generation: 3, state: FailedState.StateName);
        Record(3, ("0.0", "41"), ("1.0", JobId));
        _context.ApplyContext.OldStateName = ProcessingState.StateName;

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_LetsAListedStepRun_WhenTheNoteOfARetryOnItCanNotBeRead()
    {
        Step("1.0");
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>(_context.ApplyContext.BackgroundJob.ParametersSnapshot) { ["ChainRetry"] = "not json" };
        Record(3, ("0.0", "41"), ("1.0", JobId));

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ReadsTheNoteOfARetry_OnAStorageThatLoadsNoParameters_AndTakesItOffItself()
    {
        _context.ApplyContext.Connection = _connection.As<IStorageConnection>();
        _connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainIdParameter)).Returns($"\"{ChainId}\"");
        _connection.Setup(x => x.GetJobParameter(JobId, JobChains.ChainStepParameter)).Returns("\"1.0\"");
        _connection.Setup(x => x.GetJobParameter(JobId, "ChainRetry")).Returns(Note(generation: 3));
        Record(4, ("0.0", "41"), ("1.0", JobId));
        _context.ApplyContext.Object.AfterCommit = new PostCommitActions();

        _filter.OnStateElection(_context.Object);
        _context.ApplyContext.Object.AfterCommit.Run();

        // Recorded: the step runs, and nothing else would take the note off there
        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Once);
    }

    [Theory]
    [InlineData("Scheduled", "Enqueued")]
    [InlineData("Enqueued", "Scheduled")]
    [InlineData("Scheduled", "Scheduled")]
    public void OnStateApplied_KeepsTheNoteOfARetry_WhileTheStepGoesFromItsHoldTowardsAWorker(string from, string to)
    {
        ApplyStateContextMock mock = WithTheNoteOfARetry(from, to, transactional: true);

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);
        mock.Object.AfterCommit.Run();

        _expiring.Verify(x => x.SetJobParameter(It.IsAny<string>(), "ChainRetry", It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), "ChainRetry", It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("Enqueued", "Processing")]
    [InlineData("Enqueued", "Failed")]
    [InlineData("Scheduled", "Deleted")]
    [InlineData("Scheduled", "Succeeded")]
    [InlineData("Failed", "Enqueued")]
    [InlineData("Deleted", "Enqueued")]
    [InlineData("Processing", "Succeeded")]
    public void OnStateApplied_TakesTheNoteOfARetryOff_WithAnyOtherStateChange_InItsTransaction(string from, string to)
    {
        ApplyStateContextMock mock = WithTheNoteOfARetry(from, to, transactional: true);

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);

        _expiring.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Once);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), "ChainRetry", It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("Failed", "Enqueued")]
    [InlineData("Deleted", "Enqueued")]
    [InlineData("Succeeded", "Scheduled")]
    [InlineData("Processing", "Succeeded")]
    public void OnStateApplied_TakesAStaleNoteOff_BeforeTheStateChangeIsCommitted_WhereTheStorageCanNotDoBothAtOnce(string from, string to)
    {
        // The job wasn't held when it left that state: the note was never followed by a hold, or was left
        // behind. Were it still there after a requeue, the job would be put back instead of running.
        ApplyStateContextMock mock = WithTheNoteOfARetry(from, to, transactional: false);

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);

        _connection.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Once);
    }

    [Theory]
    [InlineData("Enqueued", "Processing")]
    [InlineData("Enqueued", "Failed")]
    [InlineData("Scheduled", "Deleted")]
    public void OnStateApplied_TakesTheNoteOfAStepThatLeavesItsHoldOff_OnlyOnceThatIsCommitted_WhereTheStorageCanNotDoBothAtOnce(string from, string to)
    {
        // Were it taken off before, a state change that fails would leave a step held without its note
        ApplyStateContextMock mock = WithTheNoteOfARetry(from, to, transactional: false);

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);

        _connection.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Never);

        mock.Object.AfterCommit.Run();

        _connection.Verify(x => x.SetJobParameter(JobId, "ChainRetry", null), Times.Once);
    }

    [Fact]
    public void OnStateApplied_LeavesTheNoteWrittenWithTheHold_OfAStepThatIsHeldAgain()
    {
        ApplyStateContextMock mock = WithTheNoteOfARetry(
            FailedState.StateName,
            ScheduledState.StateName,
            transactional: true,
            new JobChains.RetryHold(DateTime.UtcNow.AddMinutes(2), Note(generation: 4)));

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);

        _expiring.Verify(x => x.SetJobParameter(It.IsAny<string>(), "ChainRetry", null), Times.Never);
    }

    [Fact]
    public void OnStateApplied_DoesNotThrow_WhenTheNoteOfARetryCanNotBeTakenOff()
    {
        ApplyStateContextMock mock = WithTheNoteOfARetry(FailedState.StateName, EnqueuedState.StateName, transactional: false);
        _connection.Setup(x => x.SetJobParameter(JobId, "ChainRetry", null)).Throws<InvalidOperationException>();

        _filter.OnStateApplied(mock.Object, mock.Object.Transaction);
    }

    // A step that carries the note of a retry and goes from a state to another
    private ApplyStateContextMock WithTheNoteOfARetry(string from, string to, bool transactional, IState newState = null)
    {
        ApplyStateContextMock mock = LeftMock(from, to);
        mock.NewStateObject = newState ?? mock.NewStateObject;
        mock.Transaction = _expiring.As<IWriteOnlyTransaction>();
        mock.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(transactional);
        mock.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [JobChains.ChainIdParameter] = $"\"{ChainId}\"",
            ["ChainRetry"] = Note(generation: 3),
        };
        mock.BackgroundJob.CreatedAt = DateTime.UtcNow;
        mock.Object.AfterCommit = new PostCommitActions();
        return mock;
    }

    private static string Note(int generation, string state = "Failed", Dictionary<string, string> data = null)
    {
        return new JobChains.RetryMarker { Id = "0123456789abcdef", Generation = generation, State = state, Reason = "The old reason", Data = data ?? [] }.Serialize();
    }

    // The step carries the note of a retry of its chain that held it
    private void HeldForARetry(int generation, string state, Dictionary<string, string> data = null)
    {
        Dictionary<string, string> parameters = new(_context.ApplyContext.BackgroundJob.ParametersSnapshot) { ["ChainRetry"] = Note(generation, state, data) };
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = parameters;
    }

    // The whole record of the chain, with its generation
    private void Record(int generation, params (string Key, string JobId)[] jobs)
    {
        string serialized = "{" + string.Join(",", jobs.Select(static x => $"\"{x.Key}\":\"{x.JobId}\"")) + "}";
        _connection.Setup(x => x.GetValueFromHash(JobChains.GetKey(ChainId), "Jobs")).Returns(serialized);
        _connection.Setup(x => x.GetAllEntriesFromHash(JobChains.GetKey(ChainId))).Returns(new Dictionary<string, string>
        {
            ["Jobs"] = serialized,
            ["Definition"] = "{}",
            ["Generation"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    // The state change of a worker that fetched the job and can put it back in its queue
    private JobGiveBack FetchedByAWorker()
    {
        JobGiveBack giveBack = new();
        _context.ApplyContext.GiveBack = giveBack;
        return giveBack;
    }

    private void Step(string key, bool recorded = false)
    {
        _connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
        _context.ApplyContext.Connection = _connection.As<IStorageConnection>();

        Dictionary<string, string> parameters = new()
        {
            [JobChains.ChainIdParameter] = $"\"{ChainId}\"",
            [JobChains.ChainStepParameter] = $"\"{key}\"",
        };

        if (recorded)
        {
            parameters[JobChains.ChainRecordedParameter] = "true";
        }

        _context.ApplyContext.BackgroundJob.ParametersSnapshot = parameters;
    }

    private void Mark(string value)
    {
        Dictionary<string, string> parameters = new(_context.ApplyContext.BackgroundJob.ParametersSnapshot) { [JobChains.ChainRecordedParameter] = value };
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = parameters;
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

    // A step of the chain, created a moment ago, that goes from a state to another
    private ApplyStateContextMock LeftMock(string from, string to)
    {
        Mock<IState> state = new();
        state.Setup(x => x.Name).Returns(to);

        ApplyStateContextMock mock = ApplyMock(state.Object);
        mock.OldStateName = from;
        mock.BackgroundJob.Id = JobId;
        mock.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { [JobChains.ChainIdParameter] = $"\"{ChainId}\"" };
        mock.BackgroundJob.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
        return mock;
    }

    private ApplyStateContext Left(string from, string to) => WithCommit(LeftMock(from, to));

    private static ApplyStateContext WithCommit(ApplyStateContextMock mock)
    {
        mock.Object.AfterCommit = new PostCommitActions();
        return mock.Object;
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
