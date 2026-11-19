using Masterly.NightShift.AspNetCore.Tests.Stubs;
using Masterly.NightShift.Server;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift.AspNetCore.Tests;

public class BackgroundProcessingServerHostedServiceFacts
{
    private readonly Mock<IBackgroundProcessingServer> _server = new();
    private readonly HostApplicationLifetimeStub _lifetime = new();

    [Fact]
    public void Ctor_ThrowsAnException_WhenServerIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessingServerHostedService(null, _lifetime));

        Assert.Equal("server", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenServerIsNull_WithoutLifetime()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessingServerHostedService(null));

        Assert.Equal("server", exception.ParamName);
    }

    [Fact]
    public void ApplicationStopping_SendsTheStopSignal_BeforeTheHostStopsTheService()
    {
        using (new BackgroundProcessingServerHostedService(_server.Object, _lifetime))
        {
            _lifetime.RaiseStopping();

            _server.Verify(x => x.SendStop(), Times.Once);
        }
    }

    [Fact]
    public void Ctor_DoesNotSendTheStopSignal_WhileTheApplicationRuns()
    {
        using (new BackgroundProcessingServerHostedService(_server.Object, _lifetime))
        {
            _lifetime.RaiseStarted();

            _server.Verify(x => x.SendStop(), Times.Never);
        }
    }

    [Fact]
    public async Task StopAsync_SendsTheStopSignal_AndWaitsForShutdown()
    {
        using CancellationTokenSource cancellation = new();
        CancellationToken token = cancellation.Token;
        _server.Setup(x => x.WaitForShutdownAsync(token)).Returns(Task.CompletedTask);
        BackgroundProcessingServerHostedService service = new(_server.Object);

        await service.StopAsync(token);

        _server.Verify(x => x.SendStop(), Times.Once);
        _server.Verify(x => x.WaitForShutdownAsync(token), Times.Once);
    }

    [Fact]
    public async Task StopAsync_DoesNothing_AfterDispose()
    {
        BackgroundProcessingServerHostedService service = new(_server.Object);
        service.Dispose();

        await service.StopAsync(CancellationToken.None);

        _server.Verify(x => x.Dispose(), Times.Once);
        _server.Verify(x => x.SendStop(), Times.Never);
    }

    [Fact]
    public void AddNightShiftServer_WithAFactory_StopsTheServer_WhenTheApplicationIsStopping()
    {
        IServiceCollection services = new ServiceCollection()
            .AddSingleton(new Mock<JobStorage>().Object)
            .AddSingleton<IHostApplicationLifetime>(_lifetime)
            .AddNightShift(_ => { })
            .AddNightShiftServer(_ => _server.Object);

        using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService hostedService = provider.GetServices<IHostedService>().Single();
        _lifetime.RaiseStopping();

        Assert.IsType<BackgroundProcessingServerHostedService>(hostedService);
        _server.Verify(x => x.SendStop(), Times.Once);
    }
}
