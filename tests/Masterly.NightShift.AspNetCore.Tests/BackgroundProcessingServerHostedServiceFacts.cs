using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.AspNetCore.Tests.Stubs;
using Masterly.NightShift.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests
{
    public class BackgroundProcessingServerHostedServiceFacts
    {
        private readonly Mock<IBackgroundProcessingServer> _server = new Mock<IBackgroundProcessingServer>();
        private readonly HostApplicationLifetimeStub _lifetime = new HostApplicationLifetimeStub();

        [Fact]
        public void Ctor_ThrowsAnException_WhenServerIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new BackgroundProcessingServerHostedService(null, _lifetime));

            Assert.Equal("server", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenServerIsNull_WithoutLifetime()
        {
            var exception = Assert.Throws<ArgumentNullException>(
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
            var token = new CancellationTokenSource().Token;
            _server.Setup(x => x.WaitForShutdownAsync(token)).Returns(Task.CompletedTask);
            var service = new BackgroundProcessingServerHostedService(_server.Object);

            await service.StopAsync(token);

            _server.Verify(x => x.SendStop(), Times.Once);
            _server.Verify(x => x.WaitForShutdownAsync(token), Times.Once);
        }

        [Fact]
        public async Task StopAsync_DoesNothing_AfterDispose()
        {
            var service = new BackgroundProcessingServerHostedService(_server.Object);
            service.Dispose();

            await service.StopAsync(CancellationToken.None);

            _server.Verify(x => x.Dispose(), Times.Once);
            _server.Verify(x => x.SendStop(), Times.Never);
        }

        [Fact]
        public void AddNightShiftServer_WithAFactory_StopsTheServer_WhenTheApplicationIsStopping()
        {
            var services = new ServiceCollection()
                .AddSingleton(new Mock<JobStorage>().Object)
                .AddSingleton<IHostApplicationLifetime>(_lifetime)
                .AddNightShift(_ => { })
                .AddNightShiftServer(_ => _server.Object);

            using (var provider = services.BuildServiceProvider())
            {
                var hostedService = provider.GetServices<IHostedService>().Single();
                _lifetime.RaiseStopping();

                Assert.IsType<BackgroundProcessingServerHostedService>(hostedService);
                _server.Verify(x => x.SendStop(), Times.Once);
            }
        }
    }
}
