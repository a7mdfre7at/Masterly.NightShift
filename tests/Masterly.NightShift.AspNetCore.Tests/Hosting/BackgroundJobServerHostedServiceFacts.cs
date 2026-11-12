using Masterly.NightShift.AspNetCore.Tests.Stubs;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift.AspNetCore.Tests;

// A BackgroundJobServer writes the storage options to the log once, while it starts, so
// JobStorage.WriteOptionsToLog tells whether the hosted service has started its server yet.
public class BackgroundJobServerHostedServiceFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly HostApplicationLifetimeStub _lifetime = new();
    private readonly BackgroundJobServerOptions _options = new() { WorkerCount = 1 };

    public BackgroundJobServerHostedServiceFacts()
    {
        _storage.Setup(x => x.GetConnection()).Returns(() => new Mock<IStorageConnection>().Object);
    }

    [Fact]
    public async Task StartAsync_WaitsForTheApplicationToStart_BeforeStartingTheServer()
    {
        using BackgroundJobServerHostedService service = new(
                   _storage.Object, _options, [], _lifetime);
        await service.StartAsync(CancellationToken.None);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

        _lifetime.RaiseStarted();

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_StartsTheServerAtOnce_WithoutALifetime()
    {
        using BackgroundJobServerHostedService service = new(
                   _storage.Object, _options, []);
        await service.StartAsync(CancellationToken.None);

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_DoesNotStartAManualServer_UntilTheControlStartsIt()
    {
        _options.StartMode = ServerStartMode.Manual;
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(_storage.Object)
            .AddSingleton(_options)
            .AddNightShift(_ => { })
            .AddNightShiftServer()
            .BuildServiceProvider();

        IHostedService[] hosted = [.. provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>()];
        IBackgroundJobServerControl control = provider.GetRequiredService<IBackgroundJobServerControl>();
        await hosted[0].StartAsync(CancellationToken.None);

        Assert.False(control.IsRunning);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

        await control.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(control.IsRunning);

        await control.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(control.IsRunning);

        // Started again, as a new server
        await control.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(control.IsRunning);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Exactly(2));

        await control.StopAsync(TestContext.Current.CancellationToken);
        foreach (IHostedService service in hosted) ((IDisposable)service).Dispose();
    }

    [Fact]
    public async Task Control_StopsAnAutomaticServer_TooAsync()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(_storage.Object)
            .AddSingleton(_options)
            .AddNightShift(_ => { })
            .AddNightShiftServer()
            .BuildServiceProvider();

        BackgroundJobServerHostedService hosted = provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>().Single();
        IBackgroundJobServerControl control = provider.GetRequiredService<IBackgroundJobServerControl>();
        await hosted.StartAsync(CancellationToken.None);
        _lifetime.RaiseStarted();

        await control.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(control.IsRunning);
        hosted.Dispose();
    }

    [Fact]
    public async Task Control_StartingTheServerBeforeTheApplicationStarted_StartsOnlyOneServer()
    {
        using ServiceProvider provider = BuildProviderWithLifetime();
        BackgroundJobServerHostedService hosted = provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>().Single();
        IBackgroundJobServerControl control = provider.GetRequiredService<IBackgroundJobServerControl>();
        await hosted.StartAsync(CancellationToken.None);

        await control.StartAsync(TestContext.Current.CancellationToken);
        _lifetime.RaiseStarted();

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
        await control.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(control.IsRunning);
    }

    [Fact]
    public async Task Control_StoppingTheServerBeforeTheApplicationStarted_KeepsItStopped()
    {
        using ServiceProvider provider = BuildProviderWithLifetime();
        BackgroundJobServerHostedService hosted = provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>().Single();
        IBackgroundJobServerControl control = provider.GetRequiredService<IBackgroundJobServerControl>();
        await hosted.StartAsync(CancellationToken.None);

        await control.StopAsync(TestContext.Current.CancellationToken);
        _lifetime.RaiseStarted();

        Assert.False(control.IsRunning);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

        // The control can still start it
        await control.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(control.IsRunning);
        await control.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Control_CannotStartTheServer_OnceTheHostStoppedIt()
    {
        using ServiceProvider provider = BuildProviderWithLifetime();
        BackgroundJobServerHostedService hosted = provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>().Single();
        IBackgroundJobServerControl control = provider.GetRequiredService<IBackgroundJobServerControl>();
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        // Nothing would stop or dispose a server started now
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartAsync(TestContext.Current.CancellationToken));

        Assert.False(control.IsRunning);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_BeforeTheApplicationStarted_KeepsTheServerFromStartingLater()
    {
        using BackgroundJobServerHostedService service = new(_storage.Object, _options, [], _lifetime);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);
        _lifetime.RaiseStarted();

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_ReturnsAtOnce_WhenTheApplicationNeverStarted()
    {
        using BackgroundJobServerHostedService service = new(
                   _storage.Object, _options, [], _lifetime);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_StopsAServer_ThatAlreadyGotTheStopSignalFromTheLifetime()
    {
        using BackgroundJobServerHostedService service = new(
                   _storage.Object, _options, [], _lifetime);
        await service.StartAsync(CancellationToken.None);
        _lifetime.RaiseStarted();
        _lifetime.RaiseStopping();

        Task stopping = service.StopAsync(CancellationToken.None);

        Assert.True(await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)) == stopping);
    }

    [Fact]
    public async Task AddNightShiftServer_StartsTheServer_OnlyAfterTheApplicationStarted()
    {
        IServiceCollection services = new ServiceCollection()
            .AddSingleton(_storage.Object)
            .AddSingleton<IHostApplicationLifetime>(_lifetime)
            .AddNightShift(_ => { })
            .AddNightShiftServer(x => x.WorkerCount = 1);

        using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService hostedService = provider.GetServices<IHostedService>().Single();
        await hostedService.StartAsync(CancellationToken.None);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

        _lifetime.RaiseStarted();

        Assert.IsType<BackgroundJobServerHostedService>(hostedService);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
    }

    [Fact]
    public async Task StoppingAsync_SendsTheStopSignal_BeforeStopAsync()
    {
        using ManualResetEventSlim fetching = new();
        using ManualResetEventSlim stopped = new();
        Mock<IStorageConnection> connection = new();
        connection
            .Setup(x => x.FetchNextJob(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns<string[], CancellationToken>((_, token) =>
            {
                fetching.Set();
                token.WaitHandle.WaitOne();
                stopped.Set();
                token.ThrowIfCancellationRequested();
                return null;
            });
        _storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        using BackgroundJobServerHostedService service = new(_storage.Object, _options, []);
        await service.StartAsync(CancellationToken.None);
        Assert.True(fetching.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        await service.StoppingAsync(CancellationToken.None);

        Assert.True(stopped.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    private ServiceProvider BuildProviderWithLifetime()
    {
        return new ServiceCollection()
            .AddSingleton(_storage.Object)
            .AddSingleton(_options)
            .AddSingleton<IHostApplicationLifetime>(_lifetime)
            .AddNightShift(_ => { })
            .AddNightShiftServer()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task StoppingAsync_DoesNothing_WhenTheServerDidNotStart()
    {
        using BackgroundJobServerHostedService service = new(_storage.Object, _options, [], _lifetime);

        Exception? exception = await Record.ExceptionAsync(() => service.StoppingAsync(CancellationToken.None));

        Assert.Null(exception);
    }
}
