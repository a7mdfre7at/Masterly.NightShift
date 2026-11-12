using Masterly.NightShift.Client;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public sealed class SchedulePolicyFilterFacts : IDisposable
{
    // Wednesday 2026-11-18 at 10:00 UTC
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Saturday = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextMonday = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    private readonly PolicyStorage _storage = new();
    private readonly SchedulePolicyFilter _filter = new();

    public SchedulePolicyFilterFacts()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Now));
        _storage.Add(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday));
    }

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        ScheduleDateProviders.Clear();
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_DoesNothing_ForAJobWithoutAPolicy()
    {
        CreatingContext context = Creating(new ScheduledState(Saturday), null, null);

        _filter.OnCreating(context);

        Assert.False(context.Canceled);
        Assert.IsType<ScheduledState>(context.InitialState);
        _storage.Connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_DoesNothing_ForAStateOtherThanEnqueuedOrScheduled()
    {
        CreatingContext context = Creating(new AwaitingState("parent"), "weekdays", WhenBlocked.Reject);

        _filter.OnCreating(context);

        Assert.False(context.Canceled);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_KeepsTheJob_WhenThePolicyAllowsItsTime()
    {
        ScheduledState state = new(Now.AddHours(1));
        CreatingContext context = Creating(state, "weekdays", WhenBlocked.Reject);

        _filter.OnCreating(context);

        Assert.False(context.Canceled);
        Assert.Same(state, context.InitialState);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_ChecksTheCurrentTime_ForAnEnqueuedJob()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        CreatingContext context = Creating(new EnqueuedState(), "weekdays", WhenBlocked.Reject);

        _filter.OnCreating(context);

        Assert.True(context.Canceled);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_Rejects_ByCancelingTheCreation()
    {
        CreatingContext context = Creating(new ScheduledState(Saturday), "weekdays", WhenBlocked.Reject);

        _filter.OnCreating(context);

        Assert.True(context.Canceled);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_Skips_ByCreatingTheJobDeleted()
    {
        CreatingContext context = Creating(new ScheduledState(Saturday), "weekdays", WhenBlocked.Skip);

        _filter.OnCreating(context);

        DeletedState state = Assert.IsType<DeletedState>(context.InitialState);
        Assert.Equal("Skipped: policy 'weekdays': Saturday isn't an allowed day", state.Reason);
        Assert.False(context.Canceled);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_MovesForward_ToTheNextAllowedInstant()
    {
        CreatingContext context = Creating(new ScheduledState(Saturday), "weekdays", WhenBlocked.MoveForward);

        _filter.OnCreating(context);

        ScheduledState state = Assert.IsType<ScheduledState>(context.InitialState);
        Assert.Equal(NextMonday, state.EnqueueAt);
        Assert.Equal("Moved by policy: policy 'weekdays': Saturday isn't an allowed day", state.Reason);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_MovesForward_WhenWhenBlockedIsMissingOrUnknown()
    {
        CreatingContext missing = Creating(new ScheduledState(Saturday), "weekdays", null);
        CreatingContext unknown = Creating(new ScheduledState(Saturday), "weekdays", null);
        unknown.Parameters[ScheduleOptions.WhenBlockedParameter] = "Sometimes";

        _filter.OnCreating(missing);
        _filter.OnCreating(unknown);

        Assert.Equal(NextMonday, Assert.IsType<ScheduledState>(missing.InitialState).EnqueueAt);
        Assert.Equal(NextMonday, Assert.IsType<ScheduledState>(unknown.InitialState).EnqueueAt);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_KeepsTheQueueOfAnEnqueuedJob_ItMovesForward()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        CreatingContext context = Creating(new EnqueuedState("critical"), "weekdays", WhenBlocked.MoveForward);

        _filter.OnCreating(context);

        Assert.IsType<ScheduledState>(context.InitialState);
        Assert.Equal("critical", context.Parameters[DrainModeFilter.HeldQueueParameter]);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_ThrowsAnException_WhenThePolicyDoesNotExist()
    {
        CreatingContext context = Creating(new ScheduledState(Saturday), "unknown", WhenBlocked.MoveForward);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _filter.OnCreating(context));

        Assert.Contains("'unknown'", exception.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_ThrowsAnException_WhenAWholeSearchFindsNoAllowedTime()
    {
        AddPolicyThatAllowsNothing();
        CreatingContext context = Creating(new ScheduledState(Saturday), "never", WhenBlocked.MoveForward);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _filter.OnCreating(context));

        Assert.Contains("'never' allows no time in the ten years", exception.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_IgnoresAnEnqueueThatIsNotDue()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState(), "weekdays", WhenBlocked.Skip);

        _filter.OnStateElection(context.Object);

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_IgnoresADueJobWithoutAPolicy()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, null, null);

        _filter.OnStateElection(context.Object);

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_LetsADueJobRun_WhenThePolicyAllowsNow()
    {
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.Skip);

        _filter.OnStateElection(context.Object);

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_LetsADueJobRun_WhenThePolicyNoLongerExists()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "deleted", WhenBlocked.Skip);

        _filter.OnStateElection(context.Object);

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
    }

    [Theory, GlobalLock(Reason = "Changes the clock")]
    [InlineData(WhenBlocked.Skip)]
    [InlineData(WhenBlocked.Reject)]
    public void OnStateElection_SkipsABlockedDueJob_WhenItIsNotMovedForward(WhenBlocked whenBlocked)
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", whenBlocked);

        _filter.OnStateElection(context.Object);

        DeletedState state = Assert.IsType<DeletedState>(context.Object.CandidateState);
        Assert.Equal("Skipped: policy 'weekdays': Saturday isn't an allowed day", state.Reason);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_MovesABlockedDueJobForward_AndUpdatesItsScheduledTime()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.MoveForward);

        _filter.OnStateElection(context.Object);

        ScheduledState state = Assert.IsType<ScheduledState>(context.Object.CandidateState);
        Assert.Equal(NextMonday, state.EnqueueAt);

        // Not during the election: a later filter may still elect another state
        context.ApplyContext.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        _filter.OnStateApplied(Applying(state, afterCommit: null), new Mock<IWriteOnlyTransaction>().Object);

        _storage.Connection.Verify(x => x.SetJobParameter("JobId", PerformContext.ScheduledForParameter, Millis(NextMonday)));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateApplied_WritesTheScheduledTime_OnlyOnceTheStateChangeIsCommitted()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.MoveForward);
        _filter.OnStateElection(context.Object);
        PostCommitActions afterCommit = new();

        _filter.OnStateApplied(Applying(context.Object.CandidateState, afterCommit), new Mock<IWriteOnlyTransaction>().Object);

        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), PerformContext.ScheduledForParameter, It.IsAny<string>()), Times.Never);

        afterCommit.Run();

        _storage.Connection.Verify(x => x.SetJobParameter("JobId", PerformContext.ScheduledForParameter, Millis(NextMonday)));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateApplied_WritesTheScheduledTime_InTheTransaction_WhenTheStorageCan()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        _storage.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(true);
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.MoveForward);
        _filter.OnStateElection(context.Object);
        Mock<JobStorageTransaction> transaction = new();

        _filter.OnStateApplied(Applying(context.Object.CandidateState, new PostCommitActions()), transaction.Object);

        transaction.Verify(x => x.SetJobParameter("JobId", PerformContext.ScheduledForParameter, Millis(NextMonday)));
        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateApplied_WritesNothing_WhenALaterFilterElectedAnotherState()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.MoveForward);
        _filter.OnStateElection(context.Object);

        // Another filter (a retry, a hold) replaced the candidate with a Scheduled state of its own
        _filter.OnStateApplied(Applying(new ScheduledState(Saturday.AddHours(1)), afterCommit: null), new Mock<IWriteOnlyTransaction>().Object);

        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_ReadsAPolicyOnce_ForJobsCreatedInQuickSuccession()
    {
        _filter.OnCreating(Creating(new ScheduledState(Saturday), "weekdays", WhenBlocked.Reject));
        int reads = ReadsOfPolicy("weekdays");

        _filter.OnCreating(Creating(new ScheduledState(Saturday), "weekdays", WhenBlocked.Reject));

        Assert.True(reads > 0);
        Assert.Equal(reads, ReadsOfPolicy("weekdays"));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_ReadsThePolicyAfresh_EvenWhenCreationCachedIt()
    {
        _filter.OnCreating(Creating(new ScheduledState(NextMonday), "weekdays", WhenBlocked.Skip));
        int reads = ReadsOfPolicy("weekdays");

        _filter.OnStateElection(Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.Skip).Object);

        Assert.True(ReadsOfPolicy("weekdays") > reads);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_FollowsAPolicyChangedAfterCreation_WithinTheCacheDuration()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        _filter.OnCreating(Creating(new ScheduledState(NextMonday), "weekdays", WhenBlocked.Skip));

        // Weekends are allowed now: the job due on Saturday must run, although creation cached the old policy
        _storage.Add(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Saturday, DayOfWeek.Sunday), revision: 2);
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.Skip);
        _filter.OnStateElection(context.Object);

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
    }

    [Fact, GlobalLock(Reason = "Changes the clock and the cache duration")]
    public void OnCreating_ReadsAPolicyAgain_OnceTheCachedOneIsOld()
    {
        TimeSpan previous = SchedulePolicyFilter.CalendarCacheDuration;
        SchedulePolicyFilter.CalendarCacheDuration = TimeSpan.Zero;

        try
        {
            CreatingContext first = Creating(new ScheduledState(Saturday), "weekdays", WhenBlocked.Reject);
            _filter.OnCreating(first);

            // The policy changed: weekends are allowed now
            _storage.Add(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Saturday, DayOfWeek.Sunday), revision: 2);
            CreatingContext second = Creating(new ScheduledState(Saturday), "weekdays", WhenBlocked.Reject);
            _filter.OnCreating(second);

            Assert.True(first.Canceled);
            Assert.False(second.Canceled);
        }
        finally
        {
            SchedulePolicyFilter.CalendarCacheDuration = previous;
        }
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_DoesNotRememberAMissingPolicy()
    {
        Assert.Throws<InvalidOperationException>(() => _filter.OnCreating(Creating(new ScheduledState(Saturday), "later", WhenBlocked.Reject)));

        _storage.Add(new SchedulePolicy("later").AllowWeekdays(DayOfWeek.Saturday));
        CreatingContext context = Creating(new ScheduledState(Saturday), "later", WhenBlocked.Reject);
        _filter.OnCreating(context);

        Assert.False(context.Canceled);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_ChecksAgainTomorrow_WhenThePolicyAllowsNothing()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        AddPolicyThatAllowsNothing();
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "never", WhenBlocked.MoveForward);

        _filter.OnStateElection(context.Object);

        ScheduledState state = Assert.IsType<ScheduledState>(context.Object.CandidateState);
        Assert.Equal(Saturday.AddDays(1), state.EnqueueAt);
        Assert.Equal("Moved by policy: 'never' allows no time in the next ten years", state.Reason);
    }

    [Fact]
    public void Filter_IsAGlobalFilter_RunBeforeDrainModeFilter()
    {
        List<object> filters = [.. GlobalJobFilters.Filters.Select(static x => x.Instance)];

        int policy = filters.FindIndex(static x => x is SchedulePolicyFilter);
        int drainMode = filters.FindIndex(static x => x is DrainModeFilter);

        Assert.True(policy >= 0);
        Assert.True(policy < drainMode);
    }

    [Fact]
    public void Methods_ThrowAnException_WhenContextIsNull()
    {
        Assert.Throws<ArgumentNullException>("context", () => _filter.OnCreating(null));
        Assert.Throws<ArgumentNullException>("context", () => _filter.OnStateElection(null));
    }

    private CreatingContext Creating(IState state, string policy, WhenBlocked? whenBlocked)
    {
        Dictionary<string, object> parameters = [];
        if (policy != null) parameters[ScheduleOptions.PolicyParameter] = policy;
        if (whenBlocked.HasValue) parameters[ScheduleOptions.WhenBlockedParameter] = whenBlocked.Value.ToString();

        return new CreatingContext(new CreateContext(
            _storage.Object,
            _storage.Connection.Object,
            Job.FromExpression(() => Run()),
            state,
            parameters));
    }

    private ElectStateContextMock Electing(IState candidate, string policy, WhenBlocked? whenBlocked, int? failedChecks = null)
    {
        Dictionary<string, string> snapshot = [];
        if (policy != null) snapshot[ScheduleOptions.PolicyParameter] = SerializationHelper.Serialize(policy);
        if (whenBlocked.HasValue) snapshot[ScheduleOptions.WhenBlockedParameter] = SerializationHelper.Serialize(whenBlocked.Value.ToString());
        if (failedChecks.HasValue) snapshot[SchedulePolicyFilter.CheckAttemptsParameter] = SerializationHelper.Serialize(failedChecks.Value);

        ElectStateContextMock context = new();
        context.ApplyContext.Storage = _storage.Storage;
        context.ApplyContext.Connection = _storage.Connection;
        context.ApplyContext.OldStateName = ScheduledState.StateName;
        context.ApplyContext.NewStateObject = candidate;
        context.ApplyContext.BackgroundJob.ParametersSnapshot = snapshot;
        return context;
    }

    private ApplyStateContext Applying(IState state, PostCommitActions afterCommit)
    {
        return new ApplyStateContext(
            _storage.Object,
            _storage.Connection.Object,
            new Mock<IWriteOnlyTransaction>().Object,
            new BackgroundJobMock().Object,
            state,
            ScheduledState.StateName)
        {
            AfterCommit = afterCommit,
        };
    }

    private int ReadsOfPolicy(string name)
    {
        string key = SchedulePolicies.GetKey(name);
        return _storage.Connection.Invocations.Count(x => x.Method.Name == nameof(IStorageConnection.GetAllEntriesFromHash) && (string)x.Arguments[0] == key);
    }

    private static string Millis(DateTime value)
    {
        return JobHelper.ToMillisecondTimestamp(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory, GlobalLock(Reason = "Changes the clock")]
    [InlineData(WhenBlocked.MoveForward)]
    [InlineData(WhenBlocked.Skip)]
    [InlineData(WhenBlocked.Reject)]
    public void OnCreating_MakesAJobThatWouldRunNowWait_WhenThePolicyCanNotBeEvaluated(WhenBlocked whenBlocked)
    {
        AddPolicyWithAMissingProvider();
        CreatingContext context = Creating(new EnqueuedState(), "holidays", whenBlocked);

        _filter.OnCreating(context);

        Assert.False(context.Canceled);
        ScheduledState state = Assert.IsType<ScheduledState>(context.InitialState);
        Assert.Equal(Now.AddMinutes(1), state.EnqueueAt);
        Assert.Equal(
            "Waiting for policy, next check in 1 min: policy 'holidays': date provider 'nowhere' isn't registere…",
            state.Reason);
        Assert.Equal(1, context.Parameters[SchedulePolicyFilter.CheckAttemptsParameter]);

        // The job was meant to run now, not when its policy is checked next
        Assert.Equal(JobHelper.ToMillisecondTimestamp(Now), context.Parameters[PerformContext.ScheduledForParameter]);
    }

    [Theory, GlobalLock(Reason = "Changes the clock")]
    [InlineData(WhenBlocked.MoveForward)]
    [InlineData(WhenBlocked.Skip)]
    [InlineData(WhenBlocked.Reject)]
    public void OnCreating_LeavesADelayedJobAtItsTime_WhenThePolicyCanNotBeEvaluated(WhenBlocked whenBlocked)
    {
        AddPolicyWithAMissingProvider();
        ScheduledState state = new(Now.AddHours(3));
        CreatingContext context = Creating(state, "holidays", whenBlocked);

        _filter.OnCreating(context);

        Assert.False(context.Canceled);
        Assert.Same(state, context.InitialState);
        Assert.Equal(Now.AddHours(3), state.EnqueueAt);
        Assert.False(context.Parameters.ContainsKey(SchedulePolicyFilter.CheckAttemptsParameter));
        Assert.False(context.Parameters.ContainsKey(PerformContext.ScheduledForParameter));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_KeepsTheQueueOfAnEnqueuedJob_ThatWaitsForItsPolicy()
    {
        AddPolicyWithAMissingProvider();
        CreatingContext context = Creating(new EnqueuedState("critical"), "holidays", WhenBlocked.Reject);

        _filter.OnCreating(context);

        Assert.IsType<ScheduledState>(context.InitialState);
        Assert.Equal("critical", context.Parameters[DrainModeFilter.HeldQueueParameter]);
    }

    [Fact, GlobalLock(Reason = "Changes the clock and the date providers")]
    public void OnCreating_MakesTheJobWait_WhenADateProviderThrows()
    {
        ScheduleDateProviders.Set("flaky", new FlakyProvider { Down = true });
        _storage.Add(new SchedulePolicy("flaky-days").ExcludeDatesFrom("flaky"));
        CreatingContext context = Creating(new EnqueuedState(), "flaky-days", WhenBlocked.Skip);

        _filter.OnCreating(context);

        ScheduledState state = Assert.IsType<ScheduledState>(context.InitialState);
        Assert.Equal(Now.AddMinutes(1), state.EnqueueAt);
        Assert.StartsWith("Waiting for policy, next check in 1 min: policy 'flaky-days': date provider 'flaky' failed", state.Reason, StringComparison.Ordinal);
    }

    [Theory, GlobalLock(Reason = "Changes the clock")]
    [InlineData(WhenBlocked.Skip)]
    [InlineData(WhenBlocked.Reject)]
    public void OnCreating_StillSkipsOrRejects_WhenARuleBlocksTheTime_WhateverTheFailingProvider(WhenBlocked whenBlocked)
    {
        _storage.Add(WeekdaysWithAMissingProvider());
        CreatingContext context = Creating(new ScheduledState(Saturday), "weekday-holidays", whenBlocked);

        _filter.OnCreating(context);

        Assert.Equal(whenBlocked == WhenBlocked.Reject, context.Canceled);
        Assert.Equal(whenBlocked == WhenBlocked.Skip, context.InitialState is DeletedState);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnCreating_MovesABlockedJob_ToTheFirstInstantThePolicyCanNotEvaluate_InsteadOfThrowing()
    {
        _storage.Add(WeekdaysWithAMissingProvider());
        CreatingContext context = Creating(new ScheduledState(Saturday), "weekday-holidays", WhenBlocked.MoveForward);

        _filter.OnCreating(context);

        // Saturday and Sunday are blocked; whether Monday is a holiday is asked again when the job is due
        ScheduledState state = Assert.IsType<ScheduledState>(context.InitialState);
        Assert.Equal(NextMonday, state.EnqueueAt);
        Assert.Equal("Moved by policy: policy 'weekday-holidays': Saturday isn't an allowed day", state.Reason);
    }

    [Theory, GlobalLock(Reason = "Changes the clock")]
    [InlineData(WhenBlocked.MoveForward)]
    [InlineData(WhenBlocked.Skip)]
    [InlineData(WhenBlocked.Reject)]
    public void OnStateElection_MakesADueJobWait_WhenThePolicyCanNotBeEvaluated_AndNeverSkipsIt(WhenBlocked whenBlocked)
    {
        AddPolicyWithAMissingProvider();
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "holidays", whenBlocked);

        _filter.OnStateElection(context.Object);

        ScheduledState state = Assert.IsType<ScheduledState>(context.Object.CandidateState);
        Assert.Equal(Now.AddMinutes(1), state.EnqueueAt);
        Assert.Equal(
            "Waiting for policy, next check in 1 min: policy 'holidays': date provider 'nowhere' isn't registere…",
            state.Reason);

        // Not during the election: a later filter may still elect another state
        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory, GlobalLock(Reason = "Changes the clock")]
    [InlineData(0, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 60)]
    [InlineData(4, 60)]
    [InlineData(500, 60)]
    public void OnStateElection_WaitsLongerAfterEachFailedCheck_AndCountsThem(int failedChecks, int minutes)
    {
        AddPolicyWithAMissingProvider();
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "holidays", WhenBlocked.Skip, failedChecks);

        _filter.OnStateElection(context.Object);

        ScheduledState state = Assert.IsType<ScheduledState>(context.Object.CandidateState);
        Assert.Equal(Now.AddMinutes(minutes), state.EnqueueAt);
        Assert.StartsWith($"Waiting for policy, next check in {minutes} min: policy 'holidays'", state.Reason, StringComparison.Ordinal);

        _filter.OnStateApplied(Applying(state, afterCommit: null), new Mock<IWriteOnlyTransaction>().Object);

        // The only parameter written: the retry count and the time the job was meant to run stay as they are
        _storage.Connection.Verify(x => x.SetJobParameter("JobId", SchedulePolicyFilter.CheckAttemptsParameter, (failedChecks + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), Times.Once);
        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateApplied_CountsTheFailedCheck_InTheTransaction_WhenTheStorageCan()
    {
        AddPolicyWithAMissingProvider();
        _storage.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(true);
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "holidays", WhenBlocked.MoveForward, failedChecks: 2);
        _filter.OnStateElection(context.Object);
        Mock<JobStorageTransaction> transaction = new();

        _filter.OnStateApplied(Applying(context.Object.CandidateState, new PostCommitActions()), transaction.Object);

        transaction.Verify(x => x.SetJobParameter("JobId", SchedulePolicyFilter.CheckAttemptsParameter, "3"));
        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateApplied_CountsNothing_WhenALaterFilterElectedAnotherState()
    {
        AddPolicyWithAMissingProvider();
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "holidays", WhenBlocked.MoveForward);
        _filter.OnStateElection(context.Object);

        _filter.OnStateApplied(Applying(new ScheduledState(Now.AddHours(1)), afterCommit: null), new Mock<IWriteOnlyTransaction>().Object);

        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock and the date providers")]
    public void OnStateElection_LetsTheJobRun_AndClearsTheCount_OnceThePolicyCanBeEvaluated()
    {
        FlakyProvider provider = new() { Down = true };
        ScheduleDateProviders.Set("flaky", provider);
        _storage.Add(new SchedulePolicy("flaky-days").ExcludeDatesFrom("flaky"));

        ElectStateContextMock first = Electing(new EnqueuedState { IsDue = true }, "flaky-days", WhenBlocked.Skip);
        _filter.OnStateElection(first.Object);
        ElectStateContextMock second = Electing(new EnqueuedState { IsDue = true }, "flaky-days", WhenBlocked.Skip, failedChecks: 1);
        _filter.OnStateElection(second.Object);

        provider.Down = false;
        ElectStateContextMock third = Electing(new EnqueuedState { IsDue = true }, "flaky-days", WhenBlocked.Skip, failedChecks: 2);
        _filter.OnStateElection(third.Object);

        Assert.Equal(Now.AddMinutes(1), Assert.IsType<ScheduledState>(first.Object.CandidateState).EnqueueAt);
        Assert.Equal(Now.AddMinutes(5), Assert.IsType<ScheduledState>(second.Object.CandidateState).EnqueueAt);
        Assert.IsType<EnqueuedState>(third.Object.CandidateState);
        _storage.Connection.Verify(x => x.SetJobParameter("JobId", SchedulePolicyFilter.CheckAttemptsParameter, null), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_ClearsTheCount_WhenThePolicyEvaluatesAndBlocks_AndStillMovesTheJob()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.MoveForward, failedChecks: 3);

        _filter.OnStateElection(context.Object);

        ScheduledState state = Assert.IsType<ScheduledState>(context.Object.CandidateState);
        Assert.Equal(NextMonday, state.EnqueueAt);
        Assert.Equal("Moved by policy: policy 'weekdays': Saturday isn't an allowed day", state.Reason);
        _storage.Connection.Verify(x => x.SetJobParameter("JobId", SchedulePolicyFilter.CheckAttemptsParameter, null), Times.Once);

        _filter.OnStateApplied(Applying(state, afterCommit: null), new Mock<IWriteOnlyTransaction>().Object);

        _storage.Connection.Verify(x => x.SetJobParameter("JobId", PerformContext.ScheduledForParameter, Millis(NextMonday)));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_StillSkipsABlockedDueJob_ThatWaitedForItsPolicyBefore()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.Skip, failedChecks: 2);

        _filter.OnStateElection(context.Object);

        DeletedState state = Assert.IsType<DeletedState>(context.Object.CandidateState);
        Assert.Equal("Skipped: policy 'weekdays': Saturday isn't an allowed day", state.Reason);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_WritesNothing_ForAJobThatNeverWaitedForItsPolicy()
    {
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.Skip);

        _filter.OnStateElection(context.Object);

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
        _storage.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_LetsTheJobRun_WhenTheCountCanNotBeCleared()
    {
        _storage.Connection.Setup(x => x.SetJobParameter("JobId", SchedulePolicyFilter.CheckAttemptsParameter, null))
            .Throws(new InvalidOperationException("storage down"));
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekdays", WhenBlocked.Skip, failedChecks: 2);

        _filter.OnStateElection(context.Object);

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_StartsTheCountAgain_WhenItCanNotBeRead()
    {
        AddPolicyWithAMissingProvider();
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "holidays", WhenBlocked.Skip);
        Dictionary<string, string> snapshot = new(context.ApplyContext.BackgroundJob.ParametersSnapshot)
        {
            [SchedulePolicyFilter.CheckAttemptsParameter] = "not a number",
        };
        context.ApplyContext.BackgroundJob.ParametersSnapshot = snapshot;

        _filter.OnStateElection(context.Object);

        Assert.Equal(Now.AddMinutes(1), Assert.IsType<ScheduledState>(context.Object.CandidateState).EnqueueAt);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_MovesABlockedDueJob_ToTheFirstInstantThePolicyCanNotEvaluate()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        _storage.Add(WeekdaysWithAMissingProvider());
        ElectStateContextMock context = Electing(new EnqueuedState { IsDue = true }, "weekday-holidays", WhenBlocked.MoveForward);

        _filter.OnStateElection(context.Object);

        // Not "no time in the next ten years": the search stopped at an instant it couldn't evaluate
        ScheduledState state = Assert.IsType<ScheduledState>(context.Object.CandidateState);
        Assert.Equal(NextMonday, state.EnqueueAt);
        Assert.Equal("Moved by policy: policy 'weekday-holidays': Saturday isn't an allowed day", state.Reason);

        _filter.OnStateApplied(Applying(state, afterCommit: null), new Mock<IWriteOnlyTransaction>().Object);

        _storage.Connection.Verify(x => x.SetJobParameter("JobId", PerformContext.ScheduledForParameter, Millis(NextMonday)));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 60)]
    [InlineData(int.MaxValue, 60)]
    public void GetCheckDelay_Grows_ThenStaysAtAnHour(int failedChecks, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), SchedulePolicyFilter.GetCheckDelay(failedChecks));
    }

    [Fact]
    public void CheckAttemptsParameter_FitsTheLengthOfAJobParameterName()
    {
        Assert.True(SchedulePolicyFilter.CheckAttemptsParameter.Length <= 40);
    }

    // No instant can be evaluated: the date provider isn't registered in this process
    private void AddPolicyWithAMissingProvider()
    {
        _storage.Add(new SchedulePolicy("holidays").ExcludeDatesFrom("nowhere"));
    }

    // Weekends are blocked by a rule; a weekday can't be evaluated, as its date provider isn't registered
    private static SchedulePolicy WeekdaysWithAMissingProvider()
    {
        return new SchedulePolicy("weekday-holidays")
            .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
            .ExcludeDatesFrom("nowhere");
    }

    // Every instant is evaluated, and blocked: the policy blocks the times a policy without rules allows
    private void AddPolicyThatAllowsNothing()
    {
        _storage.Add(new SchedulePolicy("always"));
        _storage.Add(new SchedulePolicy("never").ExceptWhen("always"));
    }

    private sealed class FlakyProvider : IScheduleDateProvider
    {
        public bool Down { get; set; }

        public bool Contains(DateOnly day) => Down ? throw new InvalidOperationException("database down") : false;
    }
}
