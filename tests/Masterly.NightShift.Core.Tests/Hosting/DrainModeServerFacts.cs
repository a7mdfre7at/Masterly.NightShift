using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests;

public class DrainModeServerFacts : IDisposable
{
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Dictionary<string, string> _hash = [];

    public DrainModeServerFacts()
    {
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
        _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => { foreach (KeyValuePair<string, string> pair in pairs) { _hash[pair.Key] = pair.Value; } });
        DrainMode.InvalidateCache();
    }

    public void Dispose()
    {
        DrainMode.InvalidateCache();
    }

    [Fact, StaticLock]
    public void PauseServer_AndResumeServer_RoundTrip()
    {
        DrainMode.PauseServer(_connection.Object, "web-1", "deploying");

        DrainModeStatus status = DrainMode.Read(_connection.Object);
        Assert.False(status.Active);
        Assert.Equal("deploying", status.PausedServers["web-1"]);
        Assert.True(status.IsServerPaused("web-1"));
        Assert.False(status.IsServerPaused("web-2"));
        Assert.Empty(status.PausedQueues);

        DrainMode.ResumeServer(_connection.Object, "web-1");

        Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
    }

    [Fact, StaticLock]
    public void Filter_HoldsJobsOnPausedServers_Only()
    {
        DrainMode.PauseServer(_connection.Object, "web-1");
        DrainModeFilter filter = new();

        ElectStateContextMock held = Elect("1", "web-1");
        filter.OnStateElection(held.Object);
        ScheduledState scheduled = Assert.IsType<ScheduledState>(held.Object.CandidateState);
        Assert.Equal("Held: server 'web-1' is paused", scheduled.Reason);

        ElectStateContextMock running = Elect("2", "web-2");
        filter.OnStateElection(running.Object);
        Assert.IsType<ProcessingState>(running.Object.CandidateState);

        // A job that runs costs no read; a held one is read once, to remember its queue
        _connection.Verify(x => x.GetStateData("2"), Times.Never);
    }

    [Fact, StaticLock]
    public void PauseServer_WithTheFullServerId_HoldsTheJobsOfThatServer_Only()
    {
        DrainMode.PauseServer(_connection.Object, PausedServerId, "patching");
        DrainModeFilter filter = new();

        ElectStateContextMock held = Elect("1", PausedServerId);
        filter.OnStateElection(held.Object);
        ScheduledState scheduled = Assert.IsType<ScheduledState>(held.Object.CandidateState);
        Assert.Equal($"Held: server '{PausedServerId}' is paused", scheduled.Reason);

        // Same machine and process, but another server instance (for example, the same server after a restart)
        ElectStateContextMock running = Elect("2", OtherServerId);
        filter.OnStateElection(running.Object);
        Assert.IsType<ProcessingState>(running.Object.CandidateState);

        DrainModeStatus status = DrainMode.Read(_connection.Object);
        Assert.True(status.IsServerPaused(PausedServerId));
        Assert.False(status.IsServerPaused(OtherServerId));
        Assert.Equal("patching", status.PausedServers[PausedServerId]);
    }

    [Fact, StaticLock]
    public void PauseServer_WithTheServerNameOnly_DoesNotHoldTheJobsOfThatServer()
    {
        DrainMode.PauseServer(_connection.Object, "web-1");
        DrainModeFilter filter = new();

        ElectStateContextMock context = Elect("1", PausedServerId);
        filter.OnStateElection(context.Object);

        Assert.IsType<ProcessingState>(context.Object.CandidateState);
        Assert.False(DrainMode.Read(_connection.Object).IsServerPaused(PausedServerId));
    }

    [Fact, StaticLock]
    public void ResumeServer_WithTheOldServerId_ClearsThePauseOfAStoppedServer()
    {
        DrainMode.PauseServer(_connection.Object, PausedServerId);

        DrainMode.ResumeServer(_connection.Object, PausedServerId);

        Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
    }

    [Fact]
    public void ClearStaleServerPauses_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => DrainMode.ClearStaleServerPauses(null));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact, StaticLock]
    public void ClearStaleServerPauses_ClearsThePausesOfServersThatAreNoLongerRunning_Only()
    {
        DrainMode.PauseServer(_connection.Object, PausedServerId, "patching");
        DrainMode.PauseServer(_connection.Object, OtherServerId, "stopped since");
        Mock<JobStorage> storage = CreateStorage(PausedServerId, "web-2:77:6b4e9c1a-2f3d-4e5f-8a9b-0c1d2e3f4a5b");

        IReadOnlyList<string> cleared = DrainMode.ClearStaleServerPauses(storage.Object);

        Assert.Equal([OtherServerId], cleared);
        DrainModeStatus status = DrainMode.Read(_connection.Object);
        Assert.Equal([PausedServerId], status.PausedServers.Keys);
        Assert.Equal("patching", status.PausedServers[PausedServerId]);
    }

    [Fact, StaticLock]
    public void ClearStaleServerPauses_ChangesNothing_WhenNoServerIsPaused()
    {
        Mock<JobStorage> storage = CreateStorage(PausedServerId);

        IReadOnlyList<string> cleared = DrainMode.ClearStaleServerPauses(storage.Object);

        Assert.Empty(cleared);
        storage.Verify(x => x.GetMonitoringApi(), Times.Never);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact, StaticLock]
    public void ClearStaleServerPauses_ChangesNothing_WhenEveryPausedServerIsRunning()
    {
        DrainMode.PauseServer(_connection.Object, PausedServerId);
        Mock<JobStorage> storage = CreateStorage(PausedServerId);
        _connection.Invocations.Clear();

        IReadOnlyList<string> cleared = DrainMode.ClearStaleServerPauses(storage.Object);

        Assert.Empty(cleared);
        Assert.True(DrainMode.Read(_connection.Object).IsServerPaused(PausedServerId));
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact, StaticLock]
    public void PauseServer_KeepsThePlainField_ForAnIdThatFits()
    {
        DrainMode.PauseServer(_connection.Object, PausedServerId, "patching");

        Assert.Equal("patching", _hash[DrainMode.ServerFieldPrefix + PausedServerId]);
    }

    [Fact, StaticLock]
    public void PauseServer_WithALongServerId_WritesAFieldTheStorageKeeps_AndReadsTheFullIdBack()
    {
        // A Kubernetes pod name can be 63 characters long; SQL Server keeps hash fields of up to 100
        DrainMode.PauseServer(_connection.Object, LongServerId, "draining the node");

        Assert.All(_hash.Keys, field => Assert.InRange(field.Length, 1, 100));
        DrainModeStatus status = DrainMode.Read(_connection.Object);
        Assert.Equal([LongServerId], status.PausedServers.Keys);
        Assert.Equal("draining the node", status.PausedServers[LongServerId]);
        Assert.True(status.IsServerPaused(LongServerId));
        Assert.True(status.IsServerPaused(LongServerId.ToUpperInvariant()));
    }

    [Fact, StaticLock]
    public void PauseServer_WithALongServerId_HoldsTheJobsOfThatServer_AndResumeServerClearsIt()
    {
        DrainMode.PauseServer(_connection.Object, LongServerId);

        ElectStateContextMock held = Elect("1", LongServerId);
        new DrainModeFilter().OnStateElection(held.Object);
        Assert.IsType<ScheduledState>(held.Object.CandidateState);
        Assert.Null(DrainMode.Read(_connection.Object).PausedServers[LongServerId]);

        DrainMode.ResumeServer(_connection.Object, LongServerId.ToUpperInvariant());

        Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
        Assert.Single(_hash);
    }

    [Fact, StaticLock]
    public void ClearStaleServerPauses_ClearsThePauseOfAStoppedServerWithALongId()
    {
        DrainMode.PauseServer(_connection.Object, LongServerId, "stopped since");
        DrainMode.PauseServer(_connection.Object, PausedServerId, "patching");
        Mock<JobStorage> storage = CreateStorage(PausedServerId);

        IReadOnlyList<string> cleared = DrainMode.ClearStaleServerPauses(storage.Object);

        Assert.Equal([LongServerId], cleared);
        Assert.Equal([PausedServerId], DrainMode.Read(_connection.Object).PausedServers.Keys);
        Assert.Equal(2, _hash.Count);
    }

    // Server ids are "{name}:{process id}:{guid}", see BackgroundServerProcess
    [Fact, GlobalLock(Reason = "Changes the clock and uses the drain mode cache")]
    public void Filter_NotesWhenAPausedServerHoldsAJob_AndAddsToTheTimeItWasHeldBefore()
    {
        DateTime now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        string nowText = JobHelper.ToMillisecondTimestamp(now).ToString(System.Globalization.CultureInfo.InvariantCulture);
        DrainMode.PauseServer(_connection.Object, "web-1");
        ElectStateContextMock first = Elect("1", "web-1");
        first.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();
        ElectStateContextMock again = Elect("2", "web-1");
        again.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { [DrainModeFilter.ServerHoldParameter] = "\"5000\"" };

        WithClock(now, () =>
        {
            new DrainModeFilter().OnStateElection(first.Object);
            new DrainModeFilter().OnStateElection(again.Object);
        });

        Assert.IsType<ScheduledState>(first.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter("1", DrainModeFilter.ServerHoldParameter, $"\"0|{nowText}\""), Times.Once);
        _connection.Verify(x => x.SetJobParameter("2", DrainModeFilter.ServerHoldParameter, $"\"5000|{nowText}\""), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Filter_NotesNoHold_ForTheClusterWideSwitch_OrAPausedQueue()
    {
        // Nothing could have run these jobs meanwhile, so they are as late as the hold makes them
        DrainMode.PauseQueue(_connection.Object, "default");
        _connection.Setup(x => x.GetStateData("1")).Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string> { ["Queue"] = "default" } });
        ElectStateContextMock byQueue = Elect("1", "web-1");
        byQueue.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();
        new DrainModeFilter().OnStateElection(byQueue.Object);

        DrainMode.Enable(_connection.Object);
        ElectStateContextMock bySwitch = Elect("2", "web-1");
        bySwitch.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();
        new DrainModeFilter().OnStateElection(bySwitch.Object);

        Assert.Equal("Held: queue 'default' is paused", Assert.IsType<ScheduledState>(byQueue.Object.CandidateState).Reason);
        Assert.Equal(DrainModeFilter.HeldReason, Assert.IsType<ScheduledState>(bySwitch.Object.CandidateState).Reason);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), DrainModeFilter.ServerHoldParameter, It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void Filter_EndsTheHoldOfAPausedServer_WhenTheJobIsEnqueuedAgain()
    {
        DateTime heldAt = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        string heldAtText = JobHelper.ToMillisecondTimestamp(heldAt).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ElectStateContextMock context = Enqueue("1", ScheduledState.StateName, $"\"5000|{heldAtText}\"");

        // The hold was for 30 seconds, and the scheduler enqueued the job a second after that
        WithClock(heldAt.AddSeconds(31), () => new DrainModeFilter().OnStateElection(context.Object));

        Assert.IsType<EnqueuedState>(context.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter("1", DrainModeFilter.ServerHoldParameter, "\"36000\""), Times.Once);
    }

    [Theory]
    [InlineData("Scheduled", null)]
    [InlineData("Scheduled", "\"36000\"")]
    [InlineData("Failed", "\"5000|1763000000000\"")]
    public void Filter_WritesNothing_WhenAJobIsEnqueuedThatNoPausedServerHolds(string currentState, string hold)
    {
        ElectStateContextMock context = Enqueue("1", currentState, hold);

        new DrainModeFilter().OnStateElection(context.Object);

        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Filter_WritesTheHoldInTheTransactionOfTheStateChange_WhenTheStorageCan()
    {
        DrainMode.PauseServer(_connection.Object, "web-1");
        Mock<JobStorageTransaction> transaction = new();
        ElectStateContextMock context = Elect("1", "web-1");
        context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();
        context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(true);
        context.ApplyContext.Transaction = transaction.As<IWriteOnlyTransaction>();

        new DrainModeFilter().OnStateElection(context.Object);

        transaction.Verify(x => x.SetJobParameter("1", DrainModeFilter.ServerHoldParameter, It.Is<string>(value => value.StartsWith("\"0|", StringComparison.Ordinal))), Times.Once);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), DrainModeFilter.ServerHoldParameter, It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Filter_HoldsTheJob_WhenTheHoldCanNotBeNoted_OrTheStorageLoadsNoParametersWithTheJob()
    {
        DrainMode.PauseServer(_connection.Object, "web-1");
        _connection.Setup(x => x.SetJobParameter("1", DrainModeFilter.ServerHoldParameter, It.IsAny<string>())).Throws(new InvalidOperationException("storage down"));
        ElectStateContextMock failing = Elect("1", "web-1");
        failing.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();
        ElectStateContextMock withoutParameters = Elect("2", "web-1");

        new DrainModeFilter().OnStateElection(failing.Object);
        new DrainModeFilter().OnStateElection(withoutParameters.Object);

        Assert.IsType<ScheduledState>(failing.Object.CandidateState);
        Assert.IsType<ScheduledState>(withoutParameters.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter("2", It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ServerHoldParameter_FitsTheLengthOfAJobParameterName()
    {
        Assert.True(DrainModeFilter.ServerHoldParameter.Length <= 40);
    }

    private const string PausedServerId = "web-1:4242:0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string OtherServerId = "web-1:4242:7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string LongServerId = "orders-api-worker-deployment-7d9f8b6c5d-x2k4p-production-west-europe:1:0f8fad5b-d9cb-469f-a165-70867728950e";

    private Mock<JobStorage> CreateStorage(params string[] runningServerIds)
    {
        Mock<IMonitoringApi> monitoring = new();
        monitoring.Setup(x => x.Servers()).Returns([.. runningServerIds.Select(id => new ServerDto { Name = id })]);

        Mock<JobStorage> storage = new();
        storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        storage.Setup(x => x.GetMonitoringApi()).Returns(monitoring.Object);
        return storage;
    }

    // The election of the Enqueued state for a job in the given state, with the hold its parameters record
    private ElectStateContextMock Enqueue(string jobId, string currentState, string hold)
    {
        Dictionary<string, string> parameters = [];
        if (hold != null)
        {
            parameters[DrainModeFilter.ServerHoldParameter] = hold;
        }

        ElectStateContextMock context = new();
        context.ApplyContext.BackgroundJob.Id = jobId;
        context.ApplyContext.BackgroundJob.ParametersSnapshot = parameters;
        context.ApplyContext.Connection = _connection;
        context.ApplyContext.OldStateName = currentState;
        context.ApplyContext.NewStateObject = new EnqueuedState();
        return context;
    }

    private static void WithClock(DateTime now, Action action)
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(now));
        try
        {
            action();
        }
        finally
        {
            JobClock.TimeProvider = TimeProvider.System;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ElectStateContextMock Elect(string jobId, string serverId)
    {
        ElectStateContextMock context = new();
        context.ApplyContext.BackgroundJob.Id = jobId;
        context.ApplyContext.Connection = _connection;
        context.ApplyContext.NewStateObject = new ProcessingState(serverId, "worker");
        return context;
    }
}
