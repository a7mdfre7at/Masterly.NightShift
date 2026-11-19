using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests;

public class ThrottleAttributeFacts : IDisposable
{
    private const string Key = "throttle:resource";

    private readonly PerformContextMock _context = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new();
    private readonly HashSet<string> _members = [];
    private readonly DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly long _timestamp;

    public ThrottleAttributeFacts()
    {
        _timestamp = JobHelper.ToTimestamp(_now);

        _context.Connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        _context.Connection.Setup(x => x.GetAllItemsFromSet(Key)).Returns(_members);
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
        _context.BackgroundJob.Job = Job.FromExpression(() => Sample());

        // Other jobs holding slots are running, unless a test says otherwise
        _context.Connection.Setup(x => x.GetStateData(It.Is<string>(id => id != "JobId")))
            .Returns(new StateData { Name = ProcessingState.StateName, Data = new Dictionary<string, string>() });

        ConcurrencyControl.StateChanger = _stateChanger.Object;
        ConcurrencyControl.Clock = () => _now;
    }

    public void Dispose()
    {
        ConcurrencyControl.StateChanger = new BackgroundJobStateChanger();
        ConcurrencyControl.Clock = () => DateTime.UtcNow;
    }

    [Fact]
    public void Defaults_AreAMutexThatReschedules()
    {
        ThrottleAttribute attribute = new("resource");

        Assert.Equal(1, attribute.MaxConcurrency);
        Assert.Equal(ConcurrencyAction.Reschedule, attribute.Action);
        Assert.Equal(15, attribute.RetryDelaySeconds);
        Assert.Equal(300, attribute.MaxRetryDelaySeconds);
    }

    [Fact]
    public void Properties_RejectValuesThatWouldDisableTheLimitOrOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleAttribute { StaleTimeoutMinutes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleAttribute { StaleTimeoutMinutes = -5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleAttribute { StaleTimeoutMinutes = int.MaxValue });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleAttribute { RetryDelaySeconds = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleAttribute { MaxRetryDelaySeconds = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleAttribute { WaitTimeoutSeconds = -1 });
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]
    [InlineData(1000, 300)]
    public void GetRetryDelay_DoublesOnEveryReschedule_UpToTheMaximum(int reschedules, int expectedSeconds)
    {
        ThrottleAttribute attribute = new();

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), attribute.GetRetryDelay(reschedules));
    }

    [Fact]
    public void GetRetryDelay_DoesNotGrow_WhenTheMaximumIsNotAboveTheDelay()
    {
        ThrottleAttribute attribute = new() { RetryDelaySeconds = 20, MaxRetryDelaySeconds = 0 };

        Assert.Equal(TimeSpan.FromSeconds(20), attribute.GetRetryDelay(7));
    }

    [Fact, StaticLock]
    public void OnPerforming_BacksOff_WhenTheSameJobIsRescheduledAgain()
    {
        ThrottleAttribute attribute = new("resource") { RetryDelaySeconds = 20 };
        _members.Add($"{_timestamp}:other");
        _context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { "ThrottleReschedules", "3" } };
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", "ThrottleReschedules", "4"));
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(160)
            && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(161))));
    }

    [Fact, StaticLock]
    public void OnPerforming_CountsTheFirstReschedule()
    {
        ThrottleAttribute attribute = new("resource");
        _members.Add($"{_timestamp}:other");

        attribute.OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.SetJobParameter("JobId", "ThrottleReschedules", "1"));
    }

    [Fact, StaticLock]
    public void OnPerforming_ForgetsTheRescheduleCount_WhenTheJobGetsASlot()
    {
        ThrottleAttribute attribute = new("resource");
        _context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { "ThrottleReschedules", "6" } };

        attribute.OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.SetJobParameter("JobId", "ThrottleReschedules", null));
    }

    [Fact, StaticLock]
    public void OnPerforming_KeepsTheSlot_WhenTheRescheduleCountCannotBeCleared()
    {
        // Only bookkeeping: failing the job here would skip OnPerformed and leave the slot taken
        ThrottleAttribute attribute = new("resource");
        _context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { "ThrottleReschedules", "6" } };
        _context.Connection.Setup(x => x.SetJobParameter("JobId", "ThrottleReschedules", null)).Throws(new InvalidOperationException("storage hiccup"));
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);
        attribute.OnPerformed(_context.GetPerformedContext());

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
    }

    [Fact, StaticLock]
    public void OnPerforming_StillReschedules_WhenTheRescheduleCountCannotBeWritten()
    {
        // Only bookkeeping for the back-off: a storage error mustn't fail a job that never ran
        ThrottleAttribute attribute = new("resource");
        _members.Add($"{_timestamp}:other");
        _context.Connection.Setup(x => x.SetJobParameter("JobId", "ThrottleReschedules", It.IsAny<string>())).Throws(new InvalidOperationException("storage hiccup"));
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is ScheduledState)));
    }

    [Theory]
    [InlineData(0, 10000, 250)]
    [InlineData(1, 10000, 500)]
    [InlineData(4, 10000, 4000)]
    [InlineData(9, 10000, 4000)]
    [InlineData(2, 300, 300)]
    [InlineData(5, 1, 1)]
    public void GetWaitDelay_BacksOff_ButNeverPastTheWaitTimeout(int attempt, int remainingMilliseconds, int expectedMilliseconds)
    {
        TimeSpan delay = ThrottleAttribute.GetWaitDelay(attempt, TimeSpan.FromMilliseconds(remainingMilliseconds));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Fact, StaticLock]
    public void OnPerforming_FailsWhenTheWaitTimeoutEnds_NotAtTheNextPoll()
    {
        // The polls would be 250 ms, 500 ms and 1 s apart: the third one is cut to what is left of the second
        ThrottleAttribute attribute = new("resource") { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 1 };
        _members.Add($"{_timestamp}:other-job");
        Stopwatch elapsed = Stopwatch.StartNew();
        ConcurrencyControl.Clock = () => _now + elapsed.Elapsed;

        Assert.Throws<ConcurrencyLimitException>(() => attribute.OnPerforming(_context.GetPerformingContext()));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(1600), $"Waited {elapsed.Elapsed}");
    }

    [Fact, StaticLock]
    public void OnPerforming_RemembersTheQueueFromTheHistory_OfAJobRequeuedWhileRunning()
    {
        ThrottleAttribute attribute = new("resource");
        _members.Add($"{_timestamp}:other-job");
        PerformContext performContext = _context.Object;
        _context.Connection.Setup(x => x.GetStateData("JobId"))
            .Returns(new StateData { Name = ProcessingState.StateName, Data = new Dictionary<string, string> { { "ServerId", "old" } } });
        Mock<IMonitoringApi> monitoring = new();
        monitoring.Setup(x => x.JobDetails("JobId")).Returns(new JobDetailsDto
        {
            Properties = new Dictionary<string, string>(),
            History =
            [
                new() { StateName = ProcessingState.StateName, CreatedAt = _now.AddMinutes(-1), Data = new Dictionary<string, string>() },
                new() { StateName = EnqueuedState.StateName, CreatedAt = _now.AddMinutes(-2), Data = new Dictionary<string, string> { { "Queue", "emails" } } },
            ],
        });
        _context.Storage.Setup(x => x.GetMonitoringApi()).Returns(monitoring.Object);

        attribute.OnStateElection(ElectProcessing(performContext, ProcessingState.StateName));
        attribute.OnPerforming(new PerformingContext(performContext));

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is ScheduledState)));
        _context.Connection.Verify(x => x.SetJobParameter("JobId", DrainModeFilter.HeldQueueParameter, "\"emails\""));
    }

    [Fact, StaticLock]
    public void OnPerforming_DoesNotWriteTheRescheduleCount_WhenTheJobGetsASlotAtOnce()
    {
        ThrottleAttribute attribute = new("resource");

        attribute.OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ExpiresTheSet_AfterTheStaleTimeout_WhenTheStorageSupportsIt()
    {
        Mock<JobStorageTransaction> storageTransaction = new();
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        ThrottleAttribute attribute = new("resource") { StaleTimeoutMinutes = 90 };

        attribute.OnPerforming(_context.GetPerformingContext());

        storageTransaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        storageTransaction.Verify(x => x.ExpireSet(Key, TimeSpan.FromMinutes(90)));
    }

    [Fact, StaticLock]
    public void OnPerforming_DoesNotExpireTheSet_WhenTheStorageDoesNotSupportIt()
    {
        Mock<JobStorageTransaction> storageTransaction = new();
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
        ThrottleAttribute attribute = new("resource");

        attribute.OnPerforming(_context.GetPerformingContext());

        storageTransaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ShortensTheKey_OfALongResource()
    {
        string tenant = new('t', 95);
        _context.BackgroundJob.Job = Job.FromExpression(() => SampleWithArgument(tenant));
        _context.Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
        ThrottleAttribute attribute = new("tenant-{0}");

        attribute.OnPerforming(_context.GetPerformingContext());

        _transaction.Verify(x => x.AddToSet(
            It.Is<string>(key => key.StartsWith("throttle:tenant-ttt", StringComparison.Ordinal) && key.Contains("~") && key.Length <= 100),
            $"{_timestamp}:JobId",
            _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_RemembersTheQueueGivenOnlyByTheEnqueuedState_WhenItReschedules()
    {
        ThrottleAttribute attribute = new("resource");
        _members.Add($"{_timestamp}:other-job");
        PerformContext performContext = _context.Object;
        _context.Connection.Setup(x => x.GetStateData("JobId"))
            .Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string> { { "Queue", "emails" } } });

        // The worker elects Processing for the same background job it then performs
        attribute.OnStateElection(ElectProcessing(performContext));
        attribute.OnPerforming(new PerformingContext(performContext));

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is ScheduledState)));
        _context.Connection.Verify(x => x.SetJobParameter("JobId", DrainModeFilter.HeldQueueParameter, "\"emails\""));
    }

    [Fact, StaticLock]
    public void OnStateElection_DoesNotReadTheState_OfAJobWithAQueueOfItsOwn()
    {
        ThrottleAttribute attribute = new("resource");
        _context.BackgroundJob.Job = Job.FromExpression(() => Sample(), "emails");

        attribute.OnStateElection(ElectProcessing(_context.Object));

        _context.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_DoesNotRememberTheDefaultQueue_WhenItReschedules()
    {
        ThrottleAttribute attribute = new("resource");
        _members.Add($"{_timestamp}:other-job");
        PerformContext performContext = _context.Object;
        _context.Connection.Setup(x => x.GetStateData("JobId"))
            .Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string> { { "Queue", "default" } } });

        attribute.OnStateElection(ElectProcessing(performContext));
        attribute.OnPerforming(new PerformingContext(performContext));

        _context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), DrainModeFilter.HeldQueueParameter, It.IsAny<string>()), Times.Never);
    }

    private static ElectStateContext ElectProcessing(PerformContext performContext, string currentState = null)
    {
        return new ElectStateContext(new ApplyStateContext(
            performContext.Storage,
            performContext.Connection,
            new Mock<IWriteOnlyTransaction>().Object,
            performContext.BackgroundJob,
            new ProcessingState("server", "worker"),
            currentState ?? EnqueuedState.StateName,
            Masterly.NightShift.Profiling.EmptyProfiler.Instance,
            new Mock<IStateMachine>().Object));
    }

    [Fact, StaticLock]
    public void OnPerforming_KeepsTheExactKey_OfAResourceThatFits()
    {
        // 84 characters: shortened by an earlier build, although throttle:{resource} fits in 100
        string tenant = new('t', 77);
        _context.BackgroundJob.Job = Job.FromExpression(() => SampleWithArgument(tenant));
        _context.Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
        ThrottleAttribute attribute = new("tenant-{0}");

        attribute.OnPerforming(_context.GetPerformingContext());

        _transaction.Verify(x => x.AddToSet($"throttle:tenant-{tenant}", $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_ReschedulesWithTheWorkersStateChanger()
    {
        Mock<IBackgroundJobStateChanger> workerStateChanger = new();
        ThrottleAttribute attribute = new("resource");
        _members.Add($"{_timestamp}:other");
        PerformingContext performing = _context.GetPerformingContext();
        performing.Items[ConcurrencyControl.StateChangerItem] = workerStateChanger.Object;

        attribute.OnPerforming(performing);

        workerStateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is ScheduledState)));
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        Assert.True(performing.Items.ContainsKey(ConcurrencyControl.RescheduledItem));
    }

    [Fact, StaticLock]
    public void OnPerforming_AcquiresASlot_WhenBelowTheLimit()
    {
        ThrottleAttribute attribute = new("resource") { MaxConcurrency = 2 };
        _members.Add($"{_timestamp}:other");
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.Commit());
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ReschedulesTheJob_WhenAllSlotsAreTaken()
    {
        ThrottleAttribute attribute = new("resource") { RetryDelaySeconds = 20 };
        _members.Add($"{_timestamp}:other");
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "JobId"
            && c.NewState is ScheduledState
            && ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(20)
            && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(21)
            && c.ExpectedStates.Contains(ProcessingState.StateName))));
    }

    [Fact, StaticLock]
    public void OnPerforming_CancelsTheJob_WhenActionIsSkip()
    {
        ThrottleAttribute attribute = new("resource") { Action = ConcurrencyAction.Skip };
        _members.Add($"{_timestamp}:other");
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        Assert.True(performing.Items.ContainsKey(BackgroundJobPerformer.ContextCanceledKey));
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ThrowsAnException_WhenActionIsWait_AndTheTimeoutExpires()
    {
        ThrottleAttribute attribute = new("resource") { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 };
        _members.Add($"{_timestamp}:other");

        ConcurrencyLimitException exception = Assert.Throws<ConcurrencyLimitException>(() => attribute.OnPerforming(_context.GetPerformingContext()));

        Assert.Equal("resource", exception.Resource);
    }

    [Fact, StaticLock]
    public void OnPerforming_ThrowsOperationCanceled_WhenShutdownIsRequestedWhileWaitingForASlot()
    {
        ThrottleAttribute attribute = new("resource") { Action = ConcurrencyAction.Wait };
        _members.Add($"{_timestamp}:other");
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));

        Assert.Throws<OperationCanceledException>(() => attribute.OnPerforming(_context.GetPerformingContext()));

        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ReclaimsStaleSlots()
    {
        ThrottleAttribute attribute = new("resource") { StaleTimeoutMinutes = 30 };
        string stale = $"{_timestamp - 31 * 60}:crashed";
        _members.Add(stale);
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(Key, stale));
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_ReclaimsTheSlotOfItsOwnPreviousAttempt()
    {
        ThrottleAttribute attribute = new("resource");
        string previous = $"{_timestamp - 5}:JobId";
        _members.Add(previous);
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(Key, previous));
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_TreatsUnparsableMembersAsStale()
    {
        ThrottleAttribute attribute = new("resource");
        _members.Add("garbage");
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(Key, "garbage"));
    }

    [Fact, StaticLock]
    public void OnPerformed_ReleasesTheAcquiredSlot()
    {
        ThrottleAttribute attribute = new("resource");
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);
        PerformedContext performed = new(performing, null, false, null);

        attribute.OnPerformed(performed);

        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
        _transaction.Verify(x => x.Commit(), Times.Exactly(2));
    }

    [Fact, StaticLock]
    public void OnPerformed_DoesNothing_WhenNoSlotWasAcquired()
    {
        ThrottleAttribute attribute = new("resource");

        attribute.OnPerformed(_context.GetPerformedContext());

        _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void Resource_IsFormattedWithJobArguments_AndLowercased()
    {
        ThrottleAttribute attribute = new("tenant-{0}");
        _context.BackgroundJob.Job = Job.FromExpression(() => SampleWithArgument("Contoso"));
        _context.Connection.Setup(x => x.GetAllItemsFromSet("throttle:tenant-contoso")).Returns([]);

        attribute.OnPerforming(_context.GetPerformingContext());

        _transaction.Verify(x => x.AddToSet("throttle:tenant-contoso", $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void Resource_DefaultsToTheFullTypeName_MethodAndParameterTypes()
    {
        ThrottleAttribute attribute = new();
        string expectedKey = "throttle:masterly.nightshift.core.tests.throttleattributefacts.sample()";
        _context.Connection.Setup(x => x.GetAllItemsFromSet(expectedKey)).Returns([]);

        attribute.OnPerforming(_context.GetPerformingContext());

        _transaction.Verify(x => x.AddToSet(expectedKey, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerformed_DoesNotThrow_WhenTheSlotCannotBeReleased_AfterTryingThreeTimes()
    {
        ThrottleAttribute attribute = new("resource");
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);
        _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("storage is down"));

        attribute.OnPerformed(new PerformedContext(performing, null, false, null));

        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"), Times.Exactly(3));
    }

    [Fact, StaticLock]
    public void OnPerformed_ReleasesTheSlot_WhenTheFirstAttemptHitsATransientStorageError()
    {
        ThrottleAttribute attribute = new("resource");
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);
        int releases = 0;
        _transaction.Setup(x => x.Commit()).Callback(() =>
        {
            if (++releases == 1)
            {
                throw new InvalidOperationException("deadlock victim");
            }
        });

        attribute.OnPerformed(new PerformedContext(performing, null, false, null));

        Assert.Equal(2, releases);
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"), Times.Exactly(2));
    }

    [Fact, StaticLock]
    public void Perform_SucceedsAfterTheJobMethodRan_WhenTheSlotCannotBeReleased()
    {
        int commits = 0;
        _transaction.Setup(x => x.Commit()).Callback(() =>
        {
            if (++commits >= 2)
            {
                throw new InvalidOperationException("storage is down");
            }
        });
        JobFilterCollection filters = new()
        {
            new ThrottleAttribute("resource")
        };
        BackgroundJobPerformer performer = new(filters, new JobActivator());

        performer.Perform(_context.Object);

        Assert.Equal(4, commits);
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"), Times.Exactly(3));
    }

    [Theory, StaticLock]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Scheduled")]
    [InlineData("Enqueued")]
    [InlineData("Deleted")]
    public void OnPerforming_ReclaimsASlot_WhoseJobIsNoLongerProcessing_WhenAllSlotsAreTaken(string holderState)
    {
        // Its release failed: a slot is held only while the job is Processing
        ThrottleAttribute attribute = new("resource");
        string leaked = $"{_timestamp - 60}:finished";
        _members.Add(leaked);
        _context.Connection.Setup(x => x.GetStateData("finished")).Returns(new StateData { Name = holderState, Data = new Dictionary<string, string>() });
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(Key, leaked));
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ReclaimsASlot_WhoseJobNoLongerExists()
    {
        ThrottleAttribute attribute = new("resource");
        string leaked = $"{_timestamp - 60}:expired";
        _members.Add(leaked);
        _context.Connection.Setup(x => x.GetStateData("expired")).Returns(default(StateData));
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(Key, leaked));
    }

    [Fact, StaticLock]
    public void OnPerforming_ReclaimsOnlyTheSlotsOfFinishedJobs_AndCountsTheRest()
    {
        ThrottleAttribute attribute = new("resource") { MaxConcurrency = 2, Action = ConcurrencyAction.Skip };
        _members.Add($"{_timestamp - 60}:running-1");
        _members.Add($"{_timestamp - 30}:running-2");
        _members.Add($"{_timestamp - 90}:finished");
        _context.Connection.Setup(x => x.GetStateData("finished")).Returns(new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() });
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        // Two running jobs still fill both slots: the leaked one is removed, but the job may not start
        Assert.True(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp - 90}:finished"));
        _transaction.Verify(x => x.RemoveFromSet(Key, It.Is<string>(m => m.EndsWith(":running-1") || m.EndsWith(":running-2"))), Times.Never);
        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_KeepsASlot_WhenTheStateOfItsJobCannotBeRead()
    {
        ThrottleAttribute attribute = new("resource") { Action = ConcurrencyAction.Skip };
        _members.Add($"{_timestamp - 60}:unknown");
        _context.Connection.Setup(x => x.GetStateData("unknown")).Throws(new InvalidOperationException("storage is down"));
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_DoesNotReadTheHoldersStates_WhileASlotIsFree()
    {
        ThrottleAttribute attribute = new("resource") { MaxConcurrency = 2 };
        _members.Add($"{_timestamp}:other");

        attribute.OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void GetSlots_ReturnsTheJobsHoldingSlots_OldestFirst_AndSkipsUnparsableMembers()
    {
        _members.Add($"{_timestamp}:b");
        _members.Add($"{_timestamp - 60}:a");
        _members.Add("garbage");

        IReadOnlyList<ThrottleAttribute.ThrottleSlot> slots = ThrottleAttribute.GetSlots(_context.Connection.Object, "resource");

        Assert.Equal(["a", "b"], slots.Select(static x => x.JobId));
        Assert.Equal(_now.AddSeconds(-60), slots[0].AcquiredAt);
        Assert.Equal(DateTimeKind.Utc, slots[0].AcquiredAt.Kind);
        Assert.Equal(_now, slots[1].AcquiredAt);
    }

    [Fact]
    public void GetSlots_ReturnsAnEmptyList_WhenNoSlotIsTaken()
    {
        Assert.Empty(ThrottleAttribute.GetSlots(_context.Connection.Object, "resource"));
    }

    [Fact]
    public void GetSlots_FindsTheResource_LikeTheFilterFormatsIt()
    {
        string tenant = new('T', 120);
        string stored = ConcurrencyControl.ShortenResource($"tenant-{tenant}".ToLowerInvariant(), ThrottleAttribute.MaxResourceLength);
        _context.Connection.Setup(x => x.GetAllItemsFromSet($"throttle:{stored}")).Returns([$"{_timestamp}:a"]);

        Assert.Single(ThrottleAttribute.GetSlots(_context.Connection.Object, $"Tenant-{tenant}"));
        Assert.Single(ThrottleAttribute.GetSlots(_context.Connection.Object, stored));
    }

    [Fact]
    public void GetSlots_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ThrottleAttribute.GetSlots(null, "resource"));
        Assert.Throws<ArgumentNullException>(() => ThrottleAttribute.GetSlots(_context.Connection.Object, null));
    }

    [Fact]
    public void Release_RemovesEverySlotOfTheJob_UnderTheThrottleLock()
    {
        _members.Add($"{_timestamp}:a");
        _members.Add($"{_timestamp - 10}:a");
        _members.Add($"{_timestamp}:b");

        bool released = ThrottleAttribute.Release(_context.Connection.Object, "Resource", "a");

        Assert.True(released);
        _context.Connection.Verify(x => x.AcquireDistributedLock("throttle:resource:lock", It.IsAny<TimeSpan>()));
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:a"));
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp - 10}:a"));
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:b"), Times.Never);
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void Release_ReturnsFalse_AndWritesNothing_WhenTheJobHoldsNoSlot()
    {
        _members.Add($"{_timestamp}:b");

        bool released = ThrottleAttribute.Release(_context.Connection.Object, "resource", "a");

        Assert.False(released);
        _context.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void Release_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ThrottleAttribute.Release(null, "resource", "a"));
        Assert.Throws<ArgumentNullException>(() => ThrottleAttribute.Release(_context.Connection.Object, null, "a"));
        Assert.Throws<ArgumentNullException>(() => ThrottleAttribute.Release(_context.Connection.Object, "resource", null));
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Sample()
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void SampleWithArgument(string tenant)
    {
    }
}
