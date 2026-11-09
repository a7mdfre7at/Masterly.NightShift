using System;
using Masterly.NightShift.AspNetCore.Tests.Stubs;
using Masterly.NightShift.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests
{
    public class NightShiftApplicationBuilderExtensionsFacts
    {
        private readonly Mock<IBackgroundProcessingServer> _server = new Mock<IBackgroundProcessingServer>();
        private readonly HostApplicationLifetimeStub _lifetime = new HostApplicationLifetimeStub();

        [Fact]
        public void RegisterNightShiftServer_SendsTheStopSignal_WhenTheApplicationIsStopping()
        {
            using (var provider = BuildProvider())
            {
                provider.RegisterNightShiftServer(_server.Object);

                _lifetime.RaiseStopping();

                _server.Verify(x => x.SendStop(), Times.Once);
                _server.Verify(x => x.Dispose(), Times.Never);
            }
        }

        [Fact]
        public void RegisterNightShiftServer_DisposesTheServer_WhenTheApplicationHasStopped()
        {
            using (var provider = BuildProvider())
            {
                provider.RegisterNightShiftServer(_server.Object);

                _lifetime.RaiseStopped();

                _server.Verify(x => x.Dispose(), Times.Once);
            }
        }

        [Fact]
        public void RegisterNightShiftServer_ThrowsAnException_WithoutAHostLifetime()
        {
            using (var provider = new ServiceCollection().BuildServiceProvider())
            {
                Assert.Throws<InvalidOperationException>(() => provider.RegisterNightShiftServer(_server.Object));
            }
        }

        private ServiceProvider BuildProvider()
        {
            return new ServiceCollection()
                .AddSingleton<IHostApplicationLifetime>(_lifetime)
                .BuildServiceProvider();
        }
    }
}
