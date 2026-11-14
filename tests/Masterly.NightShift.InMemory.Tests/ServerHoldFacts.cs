using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// The time a paused server held a job doesn't count as lateness, and only that time: a hold ends when the job
/// leaves the Scheduled state it was held in, whatever comes next, and only held time after the moment the job
/// was meant to run is left out. Through the state machine and the default filters, on a real storage.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class ServerHoldFacts : IDisposable
{
    private readonly TimeProvider _before = JobClock.TimeProvider;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 18, 17, 59, 45, TimeSpan.Zero));
    private readonly FlakyDates _dates = new();
    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobStateChanger _stateChanger = new(JobFilterProviders.Providers);

    public ServerHoldFacts()
    {
        GlobalConfiguration.Configuration.UseTimeProvider(_clock);
        GlobalConfiguration.Configuration.UseScheduleDateProvider("server-hold-dates", _dates);
    }

    public void Dispose()
    {
        _dates.Down = false;
        GlobalConfiguration.Configuration.UseTimeProvider(_before);
        DrainMode.InvalidateCache();
    }

    public static void Report()
    {
    }

    [Fact]
    public void Lateness_OfAJobAPolicyMovedAfterAPausedServerHeldIt_CountsFromTheNewTime_WithoutTheHold()
    {
        _storage.SaveSchedulePolicy(new SchedulePolicy("office").InTimeZone(TimeZoneInfo.Utc).AllowBetween("09:00", "18:00"));
        using IStorageConnection connection = _storage.GetConnection();

        // 17:59:45, inside the policy's hours: the job is enqueued, and a worker of a paused server takes it
        string id = new BackgroundJobClient(_storage).Enqueue(() => Report(), new ScheduleOptions { Policy = "office", WhenBlocked = WhenBlocked.MoveForward })!;
        DrainMode.PauseServer(connection, "paused-server");
        Assert.Equal(ScheduledState.StateName, ChangeState(connection, id, Processing("paused-server"), EnqueuedState.StateName)?.Name);

        // 18:00:16: the hold is over, the delayed job scheduler enqueues the job, and its policy moves it to 09:00
        _clock.Advance(TimeSpan.FromSeconds(31));
        IState? moved = ChangeState(connection, id, Due(), ScheduledState.StateName);
        Assert.Equal(new DateTime(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc), Assert.IsType<ScheduledState>(moved).EnqueueAt);

        // 09:00:05 the next day: enqueued at its new time
        _clock.MoveTo(new DateTime(2026, 11, 19, 9, 0, 5, DateTimeKind.Utc));
        Assert.Equal(EnqueuedState.StateName, ChangeState(connection, id, Due(), ScheduledState.StateName)?.Name);

        // 14:00: a server of the cluster starts it, five hours after the time it was moved to
        _clock.MoveTo(new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc));
        PerformContext context = Start(connection, id);

        Assert.Equal(new DateTime(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc), context.ScheduledFor);
        Assert.Equal(TimeSpan.FromHours(5), context.Lateness);
    }

    [Fact]
    public void Lateness_OfAJobThatWaitedForItsPolicyAfterAPausedServerHeldIt_CountsTheWait_WithoutTheHold()
    {
        _storage.SaveSchedulePolicy(new SchedulePolicy("hold-days").ExcludeDatesFrom("server-hold-dates"));
        using IStorageConnection connection = _storage.GetConnection();

        // 17:59:45: created and taken at once by a worker of a paused server
        string id = new BackgroundJobClient(_storage).Enqueue(() => Report(), new ScheduleOptions { Policy = "hold-days" })!;
        DrainMode.PauseServer(connection, "paused-server");
        Assert.Equal(ScheduledState.StateName, ChangeState(connection, id, Processing("paused-server"), EnqueuedState.StateName)?.Name);

        // 18:00:16: the hold is over, but the policy can't be evaluated, so the job waits a minute for it
        _clock.Advance(TimeSpan.FromSeconds(31));
        _dates.Down = true;
        IState? waiting = ChangeState(connection, id, Due(), ScheduledState.StateName);
        Assert.StartsWith("Waiting for policy", Assert.IsType<ScheduledState>(waiting).Reason, StringComparison.Ordinal);

        // 18:01:16: the policy can be evaluated again, and the job runs at 18:01:25
        _clock.Advance(TimeSpan.FromMinutes(1));
        _dates.Down = false;
        Assert.Equal(EnqueuedState.StateName, ChangeState(connection, id, Due(), ScheduledState.StateName)?.Name);
        _clock.Advance(TimeSpan.FromSeconds(9));
        PerformContext context = Start(connection, id);

        // 100 seconds after it was created, of which the paused server held it for 31: the wait for the policy counts
        Assert.Equal(TimeSpan.FromSeconds(69), context.Lateness);
    }

    [Fact]
    public void Lateness_LeavesOutTheHoldsOfPausedServers_ThatFollowTheMomentTheJobWasMeantToRun()
    {
        using IStorageConnection connection = _storage.GetConnection();

        // 17:59:45: created, held twice by paused servers for 31 seconds each, then run 2 minutes after it was created
        string id = new BackgroundJobClient(_storage).Enqueue(() => Report())!;
        DrainMode.PauseServer(connection, "paused-server");
        Assert.Equal(ScheduledState.StateName, ChangeState(connection, id, Processing("paused-server"), EnqueuedState.StateName)?.Name);
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(EnqueuedState.StateName, ChangeState(connection, id, Due(), ScheduledState.StateName)?.Name);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ScheduledState.StateName, ChangeState(connection, id, Processing("paused-server"), EnqueuedState.StateName)?.Name);
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(EnqueuedState.StateName, ChangeState(connection, id, Due(), ScheduledState.StateName)?.Name);

        _clock.Advance(TimeSpan.FromSeconds(48));
        PerformContext context = Start(connection, id);

        Assert.Equal(TimeSpan.FromSeconds(120 - 62), context.Lateness);
    }

    [Fact]
    public void Lateness_OfAJobWithAQueueOfItsOwn_CountsTheWaitInItsQueueAfterTheHold()
    {
        using IStorageConnection connection = _storage.GetConnection();

        // 17:59:45: created with a queue of its own, and taken at once by a worker of a paused server
        string id = new BackgroundJobClient(_storage).Enqueue(() => Report(), new ScheduleOptions { Queue = "critical" })!;
        DrainMode.PauseServer(connection, "paused-server");
        using (IFetchedJob fetched = connection.FetchNextJob(["critical"], CancellationToken.None))
        {
            Assert.Equal(ScheduledState.StateName, ChangeState(connection, id, Processing("paused-server"), EnqueuedState.StateName)?.Name);
            fetched.RemoveFromQueue();
        }

        // 18:00:16: the hold is over; the delayed job scheduler puts the job into its queue, leaving it Scheduled,
        // and no filter sees it
        _clock.Advance(TimeSpan.FromSeconds(31));
        RunDelayedJobScheduler();
        Assert.Equal(1L, _storage.GetMonitoringApi().EnqueuedCount("critical"));
        Assert.Equal(ScheduledState.StateName, connection.GetStateData(id)!.Name);

        // The queue is busy: a server that may work takes the job two hours later
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(ProcessingState.StateName, ChangeState(connection, id, Processing("working-server"), ScheduledState.StateName)?.Name);
        PerformContext context = Start(connection, id);

        // The hold counts up to the time it was due, 18:00:15, and the scheduler's polling allowance after it: the
        // two hours in the queue are the job's own lateness
        Assert.Equal(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(31) - HeldAtMost, context.Lateness);
    }

    [Fact]
    public void Lateness_OfAJobWithAQueueOfItsOwn_ThatAPausedServerTakesAgainFromItsQueue_CountsTheWaitBetweenTheHolds()
    {
        using IStorageConnection connection = _storage.GetConnection();

        // Held at 17:59:45, put into its queue by the scheduler at 18:00:16, and taken from it two hours later by a
        // worker of the paused server, which holds it again
        string id = new BackgroundJobClient(_storage).Enqueue(() => Report(), new ScheduleOptions { Queue = "critical" })!;
        DrainMode.PauseServer(connection, "paused-server");
        Assert.Equal(ScheduledState.StateName, ChangeState(connection, id, Processing("paused-server"), EnqueuedState.StateName)?.Name);
        _clock.Advance(TimeSpan.FromSeconds(31));
        RunDelayedJobScheduler();
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(ScheduledState.StateName, ChangeState(connection, id, Processing("paused-server"), ScheduledState.StateName)?.Name);

        // The second hold is over at once for the test: a server that may work takes the job
        Assert.Equal(ProcessingState.StateName, ChangeState(connection, id, Processing("working-server"), ScheduledState.StateName)?.Name);
        PerformContext context = Start(connection, id);

        Assert.Equal(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(31) - HeldAtMost, context.Lateness);
    }

    // The longest a hold counts: the hold interval and the scheduler's polling allowance
    private static TimeSpan HeldAtMost => DrainMode.HoldInterval + TimeSpan.FromSeconds(15);

    private void RunDelayedJobScheduler()
    {
        new DelayedJobScheduler(TimeSpan.FromMilliseconds(1)).Execute(new BackgroundProcessContext(
            "scheduler", _storage, new Dictionary<string, object>(), Guid.NewGuid(), CancellationToken.None, CancellationToken.None, CancellationToken.None));
    }

    private IState? ChangeState(IStorageConnection connection, string id, IState state, string expectedState)
    {
        return _stateChanger.ChangeState(new StateChangeContext(_storage, connection, id, state, expectedState));
    }

    // What a worker sees when it starts the job: the job with the parameters loaded with it
    private PerformContext Start(IStorageConnection connection, string id)
    {
        JobData data = connection.GetJobData(id)!;
        return new PerformContext(_storage, connection, new BackgroundJob(id, data.Job, data.CreatedAt, data.ParametersSnapshot), new JobCancellationToken(false));
    }

    // The Processing state a worker of the given server elects for a job it fetched
    private static ProcessingState Processing(string serverId)
    {
        return (ProcessingState)typeof(ProcessingState)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(string)])!
            .Invoke([serverId, "worker"]);
    }

    // As the delayed job scheduler enqueues a job that came due
    private static EnqueuedState Due()
    {
        EnqueuedState state = new() { Reason = "Triggered by DelayedJobScheduler" };
        typeof(EnqueuedState).GetProperty("IsDue", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, true);
        return state;
    }

    private sealed class FlakyDates : IScheduleDateProvider
    {
        public bool Down
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool Contains(DateOnly day) => Down ? throw new InvalidOperationException("holiday database down") : false;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public void MoveTo(DateTime utc) => Interlocked.Exchange(ref _ticks, utc.Ticks);
    }
}
