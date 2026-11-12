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

    private ElectStateContextMock Elect(string jobId, string serverId)
    {
        ElectStateContextMock context = new();
        context.ApplyContext.BackgroundJob.Id = jobId;
        context.ApplyContext.Connection = _connection;
        context.ApplyContext.NewStateObject = new ProcessingState(serverId, "worker");
        return context;
    }
}
