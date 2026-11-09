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
        using var service = new BackgroundJobServerHostedService(
                   _storage.Object, _options, [], _lifetime);
        await service.StartAsync(CancellationToken.None);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

        _lifetime.RaiseStarted();

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_StartsTheServerAtOnce_WithoutALifetime()
    {
        using var service = new BackgroundJobServerHostedService(
                   _storage.Object, _options, []);
        await service.StartAsync(CancellationToken.None);

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_ReturnsAtOnce_WhenTheApplicationNeverStarted()
    {
        using var service = new BackgroundJobServerHostedService(
                   _storage.Object, _options, [], _lifetime);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_StopsAServer_ThatAlreadyGotTheStopSignalFromTheLifetime()
    {
        using var service = new BackgroundJobServerHostedService(
                   _storage.Object, _options, [], _lifetime);
        await service.StartAsync(CancellationToken.None);
        _lifetime.RaiseStarted();
        _lifetime.RaiseStopping();

        var stopping = service.StopAsync(CancellationToken.None);

        Assert.True(await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)) == stopping);
    }

    [Fact]
    public async Task AddNightShiftServer_StartsTheServer_OnlyAfterTheApplicationStarted()
    {
        var services = new ServiceCollection()
            .AddSingleton(_storage.Object)
            .AddSingleton<IHostApplicationLifetime>(_lifetime)
            .AddNightShift(_ => { })
            .AddNightShiftServer(x => x.WorkerCount = 1);

        using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetServices<IHostedService>().Single();
        await hostedService.StartAsync(CancellationToken.None);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

        _lifetime.RaiseStarted();

        Assert.IsType<BackgroundJobServerHostedService>(hostedService);
        _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
    }

    [Fact]
    public async Task StoppingAsync_SendsTheStopSignal_BeforeStopAsync()
    {
        using var fetching = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        var connection = new Mock<IStorageConnection>();
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
        using var service = new BackgroundJobServerHostedService(_storage.Object, _options, []);
        await service.StartAsync(CancellationToken.None);
        Assert.True(fetching.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        await service.StoppingAsync(CancellationToken.None);

        Assert.True(stopped.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoppingAsync_DoesNothing_WhenTheServerDidNotStart()
    {
        using var service = new BackgroundJobServerHostedService(_storage.Object, _options, [], _lifetime);

        var exception = await Record.ExceptionAsync(() => service.StoppingAsync(CancellationToken.None));

        Assert.Null(exception);
    }
}
