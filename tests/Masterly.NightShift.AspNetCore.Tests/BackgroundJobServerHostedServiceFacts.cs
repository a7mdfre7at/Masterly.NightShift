using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.AspNetCore.Tests.Stubs;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests
{
    // A BackgroundJobServer writes the storage options to the log once, while it starts, so
    // JobStorage.WriteOptionsToLog tells whether the hosted service has started its server yet.
    public class BackgroundJobServerHostedServiceFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly HostApplicationLifetimeStub _lifetime = new HostApplicationLifetimeStub();
        private readonly BackgroundJobServerOptions _options = new BackgroundJobServerOptions { WorkerCount = 1 };

        public BackgroundJobServerHostedServiceFacts()
        {
            _storage.Setup(x => x.GetConnection()).Returns(() => new Mock<IStorageConnection>().Object);
        }

        [Fact]
        public async Task StartAsync_WaitsForTheApplicationToStart_BeforeStartingTheServer()
        {
            using (var service = new BackgroundJobServerHostedService(
                       _storage.Object, _options, Array.Empty<IBackgroundProcess>(), _lifetime))
            {
                await service.StartAsync(CancellationToken.None);
                _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

                _lifetime.RaiseStarted();

                _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
            }
        }

        [Fact]
        public async Task StartAsync_StartsTheServerAtOnce_WithoutALifetime()
        {
            using (var service = new BackgroundJobServerHostedService(
                       _storage.Object, _options, Array.Empty<IBackgroundProcess>()))
            {
                await service.StartAsync(CancellationToken.None);

                _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
            }
        }

        [Fact]
        public async Task StopAsync_ReturnsAtOnce_WhenTheApplicationNeverStarted()
        {
            using (var service = new BackgroundJobServerHostedService(
                       _storage.Object, _options, Array.Empty<IBackgroundProcess>(), _lifetime))
            {
                await service.StartAsync(CancellationToken.None);

                await service.StopAsync(CancellationToken.None);

                _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);
            }
        }

        [Fact]
        public async Task StopAsync_StopsAServer_ThatAlreadyGotTheStopSignalFromTheLifetime()
        {
            using (var service = new BackgroundJobServerHostedService(
                       _storage.Object, _options, Array.Empty<IBackgroundProcess>(), _lifetime))
            {
                await service.StartAsync(CancellationToken.None);
                _lifetime.RaiseStarted();
                _lifetime.RaiseStopping();

                var stopping = service.StopAsync(CancellationToken.None);

                Assert.True(await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)) == stopping);
            }
        }

        [Fact]
        public async Task AddNightShiftServer_StartsTheServer_OnlyAfterTheApplicationStarted()
        {
            var services = new ServiceCollection()
                .AddSingleton(_storage.Object)
                .AddSingleton<IHostApplicationLifetime>(_lifetime)
                .AddNightShift(_ => { })
                .AddNightShiftServer(x => x.WorkerCount = 1);

            using (var provider = services.BuildServiceProvider())
            {
                var hostedService = provider.GetServices<IHostedService>().Single();
                await hostedService.StartAsync(CancellationToken.None);
                _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Never);

                _lifetime.RaiseStarted();

                Assert.IsType<BackgroundJobServerHostedService>(hostedService);
                _storage.Verify(x => x.WriteOptionsToLog(It.IsAny<ILog>()), Times.Once);
            }
        }
    }
}
