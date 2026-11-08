using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests.HealthChecks
{
    public class NightShiftHealthCheckFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi>();
        private readonly List<ServerDto> _servers = new List<ServerDto>();

        public NightShiftHealthCheckFacts()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
            _monitor.Setup(x => x.Servers()).Returns(_servers);
        }

        private void AddServer(string name, TimeSpan heartbeatAge)
        {
            _servers.Add(new ServerDto { Name = name, Heartbeat = DateTime.UtcNow - heartbeatAge, StartedAt = DateTime.UtcNow.AddHours(-1), Queues = new[] { "default" } });
        }

        private async Task<HealthReportEntry> CheckAsync(Action<IHealthChecksBuilder> configure)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(_storage.Object);
            configure(services.AddHealthChecks());

            using (var provider = services.BuildServiceProvider())
            {
                var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
                return Assert.Single(report.Entries).Value;
            }
        }

        [Fact]
        public async Task Cluster_IsHealthy_WhenAServerSentAHeartbeatRecently()
        {
            AddServer("web-1:1:a", TimeSpan.FromSeconds(10));
            AddServer("web-2:1:b", TimeSpan.FromHours(1));

            var entry = await CheckAsync(builder => builder.AddNightShift());

            Assert.Equal(HealthStatus.Healthy, entry.Status);
            Assert.Equal(1, entry.Data["alive"]);
            Assert.Equal(2, entry.Data["servers"]);
        }

        [Fact]
        public async Task Cluster_IsUnhealthy_WithoutALiveServer()
        {
            AddServer("web-1:1:a", TimeSpan.FromMinutes(5));

            var entry = await CheckAsync(builder => builder.AddNightShift());

            Assert.Equal(HealthStatus.Unhealthy, entry.Status);
            Assert.Equal("No NightShift server sent a heartbeat recently.", entry.Description);
        }

        [Fact]
        public async Task Cluster_UsesTheConfiguredFailureStatus_AndHeartbeatTimeout()
        {
            AddServer("web-1:1:a", TimeSpan.FromMinutes(5));

            var degraded = await CheckAsync(builder => builder.AddNightShift(failureStatus: HealthStatus.Degraded));
            var healthy = await CheckAsync(builder => builder.AddNightShift(options => options.HeartbeatTimeout = TimeSpan.FromMinutes(10)));

            Assert.Equal(HealthStatus.Degraded, degraded.Status);
            Assert.Equal(HealthStatus.Healthy, healthy.Status);
        }

        [Fact]
        public async Task Check_HidesTheStorageError_FromTheResult()
        {
            _monitor.Setup(x => x.Servers()).Throws(new InvalidOperationException("Login failed for user 'sa' on db-prod-01"));

            var entry = await CheckAsync(builder => builder.AddNightShift());

            Assert.Equal(HealthStatus.Unhealthy, entry.Status);
            Assert.Equal("The NightShift storage did not answer.", entry.Description);
            Assert.Null(entry.Exception);
        }

        [Fact]
        public async Task Process_ReportsDegraded_NotUnhealthy_WhenTheStorageDoesNotAnswer()
        {
            // A liveness probe must not restart every pod during a database outage
            _monitor.Setup(x => x.Servers()).Throws(new InvalidOperationException("Login failed"));

            var entry = await CheckAsync(builder => builder.AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process));

            Assert.Equal(HealthStatus.Degraded, entry.Status);
            Assert.DoesNotContain("Login failed", entry.Description);
        }

        [Fact]
        public async Task Check_UsesTheStorageFromTheOptions_WhenGiven()
        {
            var other = new Mock<JobStorage>();
            var otherMonitor = new Mock<IMonitoringApi>();
            other.Setup(x => x.GetMonitoringApi()).Returns(otherMonitor.Object);
            otherMonitor.Setup(x => x.Servers()).Returns(new List<ServerDto> { new ServerDto { Name = "x", Heartbeat = DateTime.UtcNow } });

            var entry = await CheckAsync(builder => builder.AddNightShift(options => options.Storage = other.Object, name: "other"));

            Assert.Equal(HealthStatus.Healthy, entry.Status);
            _monitor.Verify(x => x.Servers(), Times.Never);
        }

        [Fact]
        public async Task Process_IsUnhealthy_WhenNoServerRunsInThisProcess()
        {
            AddServer("elsewhere:1:a", TimeSpan.FromSeconds(1));

            var entry = await CheckAsync(builder => builder.AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process));

            Assert.Equal(HealthStatus.Unhealthy, entry.Status);
            Assert.Equal("No NightShift server runs in this process.", entry.Description);
        }

        [Fact]
        public async Task Process_ChecksTheHeartbeat_OfTheServersStartedInThisProcess_Only()
        {
            var serverId = $"this-process:1:{Guid.NewGuid()}";
            AddServer("elsewhere:1:a", TimeSpan.FromSeconds(1));
            AddServer(serverId, TimeSpan.FromMinutes(5));

            var services = new ServiceCollection();
            services.AddHealthChecks().AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process);
            var tracker = Assert.Single(services.BuildServiceProvider().GetServices<IBackgroundProcess>());

            using (var stopping = new CancellationTokenSource())
            {
                var context = new BackgroundProcessContext(serverId, _storage.Object, new Dictionary<string, object>(), Guid.NewGuid(), stopping.Token, stopping.Token, stopping.Token);
                var running = Task.Run(() => tracker.Execute(context), TestContext.Current.CancellationToken);

                try
                {
                    await WaitUntilTrackedAsync(builder => builder.AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process));

                    var silent = await CheckAsync(builder => builder.AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process));
                    Assert.Equal(HealthStatus.Unhealthy, silent.Status);
                    Assert.Equal(1, silent.Data["silent"]);

                    _servers.Single(x => x.Name == serverId).Heartbeat = DateTime.UtcNow;

                    var alive = await CheckAsync(builder => builder.AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process));
                    Assert.Equal(HealthStatus.Healthy, alive.Status);
                    Assert.Equal(1, alive.Data["servers"]);
                }
                finally
                {
                    stopping.Cancel();
                    await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
            }

            var stopped = await CheckAsync(builder => builder.AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process));
            Assert.Equal("No NightShift server runs in this process.", stopped.Description);
        }

        [Fact]
        public void AddNightShift_RegistersTheServerTracker_OnlyForTheProcessScope_AndOnlyOnce()
        {
            var cluster = new ServiceCollection();
            cluster.AddHealthChecks().AddNightShift();

            var process = new ServiceCollection();
            process.AddHealthChecks()
                .AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process, name: "a")
                .AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process, name: "b");

            Assert.DoesNotContain(cluster, x => x.ServiceType == typeof(IBackgroundProcess));
            Assert.Single(process, x => x.ServiceType == typeof(IBackgroundProcess));
        }

        [Fact]
        public void AddNightShift_ThrowsAnException_WhenTheBuilderIsNull_OrTheTimeoutIsNotPositive()
        {
            Assert.Throws<ArgumentNullException>("builder", () => NightShiftHealthChecksBuilderExtensions.AddNightShift(null));
            Assert.Throws<ArgumentException>("configure", () => new ServiceCollection().AddHealthChecks().AddNightShift(options => options.HeartbeatTimeout = TimeSpan.Zero));
        }

        private async Task WaitUntilTrackedAsync(Action<IHealthChecksBuilder> configure)
        {
            for (var i = 0; i < 100; i++)
            {
                var entry = await CheckAsync(configure);
                if (entry.Description != "No NightShift server runs in this process.") return;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Fail("The tracker did not record the server.");
        }
    }
}
