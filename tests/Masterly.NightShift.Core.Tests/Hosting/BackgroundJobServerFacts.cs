using Masterly.NightShift.Client;
using Masterly.NightShift.Server;

// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable ObjectCreationAsStatement

namespace Masterly.NightShift.Core.Tests;

public class BackgroundJobServerFacts
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<JobStorage> _jobStorage = new();
    private readonly Mock<IStorageConnection> _storageConnection = new();
    private readonly BackgroundJobServerOptions _serverOptions = new()
    {
        ServerName = "facts-server",
        WorkerCount = 2,
        Queues = ["critical", "default"],
        IsLightweightServer = true,
        ShutdownTimeout = TimeSpan.FromSeconds(15),
    };

    public BackgroundJobServerFacts()
    {
        _jobStorage.Setup(x => x.GetConnection()).Returns(_storageConnection.Object);

        // Behaves like a real storage: waits for a job until the server stops
        _storageConnection
            .Setup(x => x.FetchNextJob(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns<string[], CancellationToken>((_, token) =>
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return null;
            });
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobServer(_serverOptions, (JobStorage)null));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenOptionsAreNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobServer(null, _jobStorage.Object));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenAdditionalProcessesAreNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobServer(_serverOptions, _jobStorage.Object, null));

        Assert.Equal("additionalProcesses", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenOnlySomeCustomServicesAreGiven()
    {
        Mock<IBackgroundJobFactory> factory = new();
        Mock<IBackgroundJobPerformer> performer = new();
        Mock<IBackgroundJobStateChanger> stateChanger = new();

        Assert.Equal("performer", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
            _serverOptions, _jobStorage.Object, [], null, null, factory.Object, null, stateChanger.Object)).ParamName);
        Assert.Equal("factory", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
            _serverOptions, _jobStorage.Object, [], null, null, null, performer.Object, stateChanger.Object)).ParamName);
        Assert.Equal("stateChanger", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
            _serverOptions, _jobStorage.Object, [], null, null, factory.Object, performer.Object, null)).ParamName);
    }

    [Fact]
    public void Ctor_StartsTheServer_ThatAnnouncesItsQueuesAndWorkerCount_AndRemovesItselfOnShutdown()
    {
        ManualResetEventSlim announced = new();
        string serverId = null;
        ServerContext serverContext = null;
        _storageConnection
            .Setup(x => x.AnnounceServer(It.IsAny<string>(), It.IsAny<ServerContext>()))
            .Callback<string, ServerContext>((id, context) =>
            {
                serverId = id;
                serverContext = context;
                announced.Set();
            });

        using (BackgroundJobServer server = new(_serverOptions, _jobStorage.Object, []))
        {
            Assert.True(announced.Wait(WaitTimeout, TestContext.Current.CancellationToken));

            server.SendStop();
            Assert.True(server.WaitForShutdown(WaitTimeout));
        }

        Assert.StartsWith("facts-server:", serverId);
        Assert.Equal(new[] { "critical", "default" }, serverContext.Queues);
        Assert.Equal(2, serverContext.WorkerCount);
        _storageConnection.Verify(x => x.RemoveServer(serverId), Times.Once);
    }

    [Fact, StaticLock]
    public void Ctor_AddsTheJobMonitorProcess_EvenWithoutMonitors_SoMonitorsRegisteredLaterAreChecked()
    {
        JobMonitors.Clear();

        using BackgroundJobServer server = new(_serverOptions, _jobStorage.Object, []);

        Assert.Contains(server.Processes, static x => x.ToString() == nameof(JobMonitorProcess));
        server.SendStop();
        Assert.True(server.WaitForShutdown(WaitTimeout));
    }

    [Fact]
    public void Ctor_AddsTheChainMaintenanceProcess()
    {
        using BackgroundJobServer server = new(_serverOptions, _jobStorage.Object, []);

        Assert.Contains(server.Processes, static x => x.ToString() == nameof(JobChainMaintenanceProcess));
        server.SendStop();
        Assert.True(server.WaitForShutdown(WaitTimeout));
    }

    [Fact]
    public void Ctor_WithCustomServices_RunsTheAdditionalProcesses()
    {
        ManualResetEventSlim executed = new();
        Mock<IBackgroundProcess> process = new();
        process
            .Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>()))
            .Callback<BackgroundProcessContext>(context =>
            {
                executed.Set();
                context.StoppingToken.WaitHandle.WaitOne();
            });

        using (BackgroundJobServer server = new(
            _serverOptions,
            _jobStorage.Object,
            [process.Object],
            null,
            null,
            new Mock<IBackgroundJobFactory>().Object,
            new Mock<IBackgroundJobPerformer>().Object,
            new Mock<IBackgroundJobStateChanger>().Object))
        {
            Assert.True(executed.Wait(WaitTimeout, TestContext.Current.CancellationToken));

            server.SendStop();
            Assert.True(server.WaitForShutdown(WaitTimeout));
        }

        process.Verify(x => x.Execute(It.Is<BackgroundProcessContext>(context => context.ServerId.StartsWith("facts-server:"))));
    }

    [Fact]
    public async Task DisposeAsync_StopsTheServerAndDisposesIt()
    {
        BackgroundJobServer server = new(_serverOptions, _jobStorage.Object);

        await server.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => server.WaitForShutdown(TimeSpan.Zero));
    }

    [Fact]
    public async Task DisposeAsync_DoesNothing_WhenCalledAgain()
    {
        BackgroundJobServer server = new(_serverOptions, _jobStorage.Object);
        await server.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await server.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_DoesNothing_AfterDispose()
    {
        BackgroundJobServer server = new(_serverOptions, _jobStorage.Object);
        server.Dispose();

        Exception? exception = await Record.ExceptionAsync(async () => await server.DisposeAsync());

        Assert.Null(exception);
    }
}
