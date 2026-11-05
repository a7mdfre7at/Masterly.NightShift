using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class DashboardHealthFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly List<ServerDto> _servers = new List<ServerDto>();
        private readonly List<QueueWithTopEnqueuedJobsDto> _queues = new List<QueueWithTopEnqueuedJobsDto>();
        private readonly Dictionary<string, string> _control = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<string, string>> _recurring = new Dictionary<string, Dictionary<string, string>>();
        private readonly DashboardOptions _options = new DashboardOptions();

        public DashboardHealthFacts()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Enqueued = 3, Failed = 1, Servers = 1 });
            _monitor.Setup(x => x.Servers()).Returns(_servers);
            _monitor.Setup(x => x.Queues()).Returns(_queues);
            _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(_control);
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(() => new HashSet<string>(_recurring.Keys));
            _connection.Setup(x => x.GetAllEntriesFromHash(It.Is<string>(k => k.StartsWith("recurring-job:")))).Returns<string>(key => _recurring.TryGetValue(key.Substring("recurring-job:".Length), out var hash) ? hash : null);
            DrainMode.InvalidateCache();
            HealthDispatcher.Clock = () => _now;
        }

        public void Dispose()
        {
            DrainMode.InvalidateCache();
            HealthDispatcher.Clock = () => DateTime.UtcNow;
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Work() { }

        private void AddServer(string name, TimeSpan heartbeatAge, params string[] queues)
        {
            _servers.Add(new ServerDto { Name = name, Heartbeat = _now - heartbeatAge, StartedAt = _now.AddHours(-1), Queues = queues, WorkersCount = 4 });
        }

        private void AddRecurring(string id, DateTime? nextExecution, bool paused = false, string error = null)
        {
            var hash = new Dictionary<string, string>
            {
                { "Cron", "* * * * *" },
                { "Job", InvocationData.SerializeJob(Job.FromExpression(() => Work())).SerializePayload() },
                { "Paused", paused ? "1" : "0" },
            };
            if (nextExecution.HasValue) hash["NextExecution"] = JobHelper.SerializeDateTime(nextExecution.Value);
            if (error != null) hash["Error"] = error;
            _recurring[id] = hash;
        }

        private static HealthCheck Check(HealthReport report, string name) => report.Checks.Single(c => c.Name == name);

        [Fact, StaticLock]
        public void Check_IsUnhealthy_WhenTheStorageThrows()
        {
            _monitor.Setup(x => x.GetStatistics()).Throws(new InvalidOperationException("connection refused"));

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Unhealthy, report.Status);
            Assert.Equal(HealthStatus.Unhealthy, Check(report, "storage").Status);
            Assert.Contains("connection refused", Check(report, "storage").Detail);
            Assert.Single(report.Checks);
        }

        [Fact, StaticLock]
        public void Check_IsUnhealthy_WithoutLiveServers()
        {
            AddServer("dead", TimeSpan.FromHours(1), "default");

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Unhealthy, report.Status);
            Assert.Equal(HealthStatus.Unhealthy, Check(report, "servers").Status);
            Assert.Equal(0, report.Servers);
        }

        [Fact, StaticLock]
        public void Check_IsHealthy_WhenEverythingIsFine()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            _queues.Add(new QueueWithTopEnqueuedJobsDto { Name = "default", Length = 3 });
            AddRecurring("nightly", _now.AddHours(2));

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Healthy, report.Status);
            Assert.All(report.Checks, c => Assert.Equal(HealthStatus.Healthy, c.Status));
            Assert.Equal(new[] { "storage", "servers", "queues", "processing", "recurring" }, report.Checks.Select(c => c.Name));
            Assert.Equal(3, report.Statistics.Enqueued);
            Assert.False(report.Paused);
        }

        [Fact, StaticLock]
        public void Check_IsDegraded_ForStaleServers_UnservedQueues_Drain_AndLateRecurringJobs()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            AddServer("web-2", TimeSpan.FromMinutes(10), "reports");
            _queues.Add(new QueueWithTopEnqueuedJobsDto { Name = "reports", Length = 7 });
            _queues.Add(new QueueWithTopEnqueuedJobsDto { Name = "empty-unserved", Length = 0 });
            _control["Drain"] = "1";
            AddRecurring("late", _now.AddMinutes(-5));
            AddRecurring("paused-late", _now.AddMinutes(-5), paused: true);
            AddRecurring("fine", _now.AddMinutes(5));

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Degraded, report.Status);
            Assert.Equal(HealthStatus.Degraded, Check(report, "servers").Status);
            Assert.Equal(HealthStatus.Degraded, Check(report, "queues").Status);
            Assert.Contains("reports", Check(report, "queues").Detail);
            Assert.DoesNotContain("empty-unserved", Check(report, "queues").Detail);
            Assert.Equal(HealthStatus.Degraded, Check(report, "processing").Status);
            Assert.Equal(HealthStatus.Degraded, Check(report, "recurring").Status);
            Assert.Contains("late", Check(report, "recurring").Detail);
            Assert.DoesNotContain("paused-late", Check(report, "recurring").Detail);
            Assert.True(report.Paused);
        }

        [Fact, StaticLock]
        public void Check_ReportsRecurringJobsDisabledByErrors()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            AddRecurring("broken", null, error: "Type not found");

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Degraded, Check(report, "recurring").Status);
            Assert.Contains("broken", Check(report, "recurring").Detail);
        }

        [Fact, StaticLock]
        public void Check_IsDegraded_WhenQueuesOrServersArePaused_AndNamesThem()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            _control[DrainMode.QueueFieldPrefix + "reports"] = "maintenance";
            _control[DrainMode.QueueFieldPrefix + "billing"] = String.Empty;
            _control[DrainMode.ServerFieldPrefix + "web-2"] = String.Empty;

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Degraded, report.Status);
            Assert.Equal(HealthStatus.Degraded, Check(report, "processing").Status);
            Assert.Equal("queues paused: billing, reports; servers paused: web-2", Check(report, "processing").Detail);
            Assert.False(report.Paused);
        }

        [Fact, StaticLock]
        public void Check_IsDegraded_WhenOnlyServersArePaused()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            _control[DrainMode.ServerFieldPrefix + "web-1"] = "upgrading";

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal("servers paused: web-1", Check(report, "processing").Detail);
        }

        [Fact, StaticLock]
        public void Check_IncludesTheDrainReason()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            _control["Drain"] = "1";
            _control["DrainReason"] = "deploying";

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal("paused: deploying", Check(report, "processing").Detail);
            Assert.True(report.Paused);
        }

        [Fact, StaticLock]
        public void Check_IsDegraded_WhenRecurringJobsCannotBeRead()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Throws(new InvalidOperationException("timeout"));

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Degraded, report.Status);
            Assert.Equal(HealthStatus.Degraded, Check(report, "recurring").Status);
            Assert.Equal("InvalidOperationException: timeout", Check(report, "recurring").Detail);
            Assert.Equal(HealthStatus.Healthy, Check(report, "processing").Status);
        }

        [Fact, StaticLock]
        public void Check_ListsAtMostFiveLateRecurringJobs()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            AddRecurring("a", _now.AddMinutes(-5));
            AddRecurring("b", _now.AddMinutes(-5));
            AddRecurring("c", _now.AddMinutes(-5));
            AddRecurring("d", _now.AddMinutes(-5));
            AddRecurring("e", _now.AddMinutes(-5));
            AddRecurring("f", _now.AddMinutes(-5));

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            var recurring = Check(report, "recurring");
            Assert.StartsWith("6 late: ", recurring.Detail);
            Assert.EndsWith(", …", recurring.Detail);
            Assert.Equal(5, recurring.Detail.Substring("6 late: ".Length).Split(new[] { ", " }, StringSplitOptions.None).Length - 1);
        }

        [Fact, StaticLock]
        public void Check_TreatsNullServerAndQueueListsAsEmpty()
        {
            _monitor.Setup(x => x.Servers()).Returns((IList<ServerDto>)null);
            _monitor.Setup(x => x.Queues()).Returns((IList<QueueWithTopEnqueuedJobsDto>)null);

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal("no servers are registered", Check(report, "servers").Detail);
            Assert.Equal("0 queues, 0 jobs waiting", Check(report, "queues").Detail);
            Assert.Empty(report.Queues);
        }

        [Fact]
        public void Check_ThrowsOnNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => DashboardHealth.Check(null, _options, _now));
            Assert.Throws<ArgumentNullException>(() => DashboardHealth.Check(_storage.Object, null, _now));
        }

        [Fact]
        public void Describe_ReturnsTheLowercaseStatusName()
        {
            Assert.Equal("healthy", HealthReport.Describe(HealthStatus.Healthy));
            Assert.Equal("degraded", HealthReport.Describe(HealthStatus.Degraded));
            Assert.Equal("unhealthy", HealthReport.Describe(HealthStatus.Unhealthy));
        }

        [Fact]
        public void ToJson_IncludesQueuesAndStatistics()
        {
            var report = new HealthReport(
                HealthStatus.Degraded,
                _now,
                new[] { new HealthCheck("queues", HealthStatus.Degraded, "no live server listens to: reports") },
                new StatisticsDto { Enqueued = 7, Scheduled = 2, Processing = 1, Failed = 3, Awaiting = 4, Recurring = 5, Retries = 6 },
                new[] { new QueueWithTopEnqueuedJobsDto { Name = "reports", Length = 7, Fetched = 1 } },
                servers: 2,
                paused: true);

            var json = JsonSerializer.SerializeToNode(report.ToJson());

            Assert.Equal("degraded", json["status"].GetValue<string>());
            Assert.Equal("degraded", json["checks"][0]["status"].GetValue<string>());
            Assert.Equal("reports", json["queues"][0]["name"].GetValue<string>());
            Assert.Equal(7, json["queues"][0]["length"].GetValue<long>());
            Assert.Equal(1, json["queues"][0]["fetched"].GetValue<long>());
            Assert.Equal(2, json["servers"].GetValue<int>());
            Assert.True(json["paused"].GetValue<bool>());
            Assert.Equal(7, json["enqueued"].GetValue<long>());
            Assert.Equal(6, json["retries"].GetValue<long>());
        }

        [Fact]
        public void ToJson_WritesNullStatistics_WhenTheStorageDidNotAnswer()
        {
            var report = new HealthReport(HealthStatus.Unhealthy, _now, Array.Empty<HealthCheck>(), null, Array.Empty<QueueWithTopEnqueuedJobsDto>(), 0, false);

            var json = JsonSerializer.SerializeToNode(report.ToJson());

            Assert.Equal("unhealthy", json["status"].GetValue<string>());
            Assert.Null(json["enqueued"]);
            Assert.Empty(json["queues"].AsArray());
        }

        [Fact]
        public void IsLate_IgnoresPausedRemovedErroredAndFutureJobs()
        {
            Assert.True(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2) }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddSeconds(-30) }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2), Paused = true }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2), Removed = true }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2), Error = "x" }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto(), _now));
        }

        [Fact, StaticLock]
        public async Task Dispatcher_Answers200_WhenHealthy_And503_WhenUnhealthy()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            var healthy = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);

            await new HealthDispatcher().Dispatch(healthy);

            Assert.Equal(200, healthy.Response.StatusCode);
            Assert.Equal("application/json", healthy.Response.ContentType);
            var json = JsonNode.Parse(healthy.Response.Text);
            Assert.Equal("healthy", json["status"].GetValue<string>());
            Assert.Equal(1, json["servers"].GetValue<int>());
            Assert.Equal(JobHelper.ToTimestamp(_now), json["checkedAt"].GetValue<long>());
            Assert.Equal(5, (json["checks"].AsArray()).Count);

            _servers.Clear();
            var unhealthy = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);

            await new HealthDispatcher().Dispatch(unhealthy);

            Assert.Equal(503, unhealthy.Response.StatusCode);
            Assert.Equal("unhealthy", JsonNode.Parse(unhealthy.Response.Text)["status"].GetValue<string>());
        }
    }
}
