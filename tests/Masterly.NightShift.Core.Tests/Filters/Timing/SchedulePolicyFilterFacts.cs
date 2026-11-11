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
    public void OnCreating_ThrowsAnException_WhenThePolicyAllowsNothing()
    {
        _storage.Add(new SchedulePolicy("never").ExcludeDatesFrom("nowhere"));
        CreatingContext context = Creating(new ScheduledState(Saturday), "never", WhenBlocked.MoveForward);

        Assert.Throws<InvalidOperationException>(() => _filter.OnCreating(context));
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
        context.ApplyContext.Connection.Verify(x => x.SetJobParameter(
            "JobId",
            PerformContext.ScheduledForParameter,
            JobHelper.ToMillisecondTimestamp(NextMonday).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void OnStateElection_ChecksAgainTomorrow_WhenThePolicyAllowsNothing()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Saturday));
        _storage.Add(new SchedulePolicy("never").ExcludeDatesFrom("nowhere"));
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

    private ElectStateContextMock Electing(IState candidate, string policy, WhenBlocked? whenBlocked)
    {
        Dictionary<string, string> snapshot = [];
        if (policy != null) snapshot[ScheduleOptions.PolicyParameter] = SerializationHelper.Serialize(policy);
        if (whenBlocked.HasValue) snapshot[ScheduleOptions.WhenBlockedParameter] = SerializationHelper.Serialize(whenBlocked.Value.ToString());

        ElectStateContextMock context = new();
        context.ApplyContext.Storage = _storage.Storage;
        context.ApplyContext.Connection = _storage.Connection;
        context.ApplyContext.OldStateName = ScheduledState.StateName;
        context.ApplyContext.NewStateObject = candidate;
        context.ApplyContext.BackgroundJob.ParametersSnapshot = snapshot;
        return context;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
