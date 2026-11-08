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
        public void Check_IsUnhealthy_WhenTheStorageThrows_AndLogsTheErrorInsteadOfShowingIt()
        {
            var provider = new RecordingLogProvider();
            _monitor.Setup(x => x.GetStatistics()).Throws(new InvalidOperationException("Login failed for user 'sa' on db-prod-01"));

            HealthReport report;
            using (provider.Use())
            {
                report = DashboardHealth.Check(_storage.Object, _options, _now);
            }

            Assert.Equal(HealthStatus.Unhealthy, report.Status);
            Assert.Equal(HealthStatus.Unhealthy, Check(report, "storage").Status);
            Assert.Equal("the storage did not answer, see the log", Check(report, "storage").Detail);
            Assert.DoesNotContain("db-prod-01", JsonSerializer.Serialize(report.ToJson()));
            Assert.Single(report.Checks);
            Assert.Contains(provider.Entries, e => e.Level == Masterly.NightShift.Logging.LogLevel.Warn && e.Exception is InvalidOperationException);
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
            AddServer("web-2", TimeSpan.FromSeconds(5), "default");
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
        public void Check_IgnoresPausesOfServersThatAreNoLongerRegistered()
        {
            AddServer("web-1:2:new", TimeSpan.FromSeconds(5), "default");
            _control[DrainMode.ServerFieldPrefix + "web-1:1:old"] = "before deploy";

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Healthy, report.Status);
            Assert.Equal(HealthStatus.Healthy, Check(report, "processing").Status);
            Assert.Equal("running", Check(report, "processing").Detail);
        }

        [Fact, StaticLock]
        public void Check_NamesOnlyThePausedServersThatAreStillRegistered()
        {
            AddServer("web-1:2:new", TimeSpan.FromSeconds(5), "default");
            _control[DrainMode.ServerFieldPrefix + "web-1:1:old"] = String.Empty;
            _control[DrainMode.ServerFieldPrefix + "web-1:2:new"] = String.Empty;

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Degraded, Check(report, "processing").Status);
            Assert.Equal("servers paused: web-1:2:new", Check(report, "processing").Detail);
        }

        [Fact]
        public void GetStalePausedServers_ReturnsThePausedIdsThatMatchNoServer_InOrder()
        {
            _control[DrainMode.ServerFieldPrefix + "b-gone"] = String.Empty;
            _control[DrainMode.ServerFieldPrefix + "a-gone"] = String.Empty;
            _control[DrainMode.ServerFieldPrefix + "WEB-1"] = String.Empty;
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            var drain = DrainMode.Read(_connection.Object);

            Assert.Equal(new[] { "a-gone", "b-gone" }, DashboardHealth.GetStalePausedServers(drain, _servers));
            Assert.Equal(new[] { "WEB-1", "a-gone", "b-gone" }, DashboardHealth.GetStalePausedServers(drain, null));
            Assert.Empty(DashboardHealth.GetStalePausedServers(DrainModeStatus.Inactive, _servers));
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
            Assert.Equal("could not read the recurring jobs, see the log", Check(report, "recurring").Detail);
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

        [Fact, StaticLock]
        public void Check_ReadsTheQueues_ThroughQueueCounts_WhenTheStorageHasTheFeature_AndReportsTheSame()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            _queues.Add(new QueueWithTopEnqueuedJobsDto { Name = "default", Length = 3, Fetched = 1 });
            _queues.Add(new QueueWithTopEnqueuedJobsDto { Name = "reports", Length = 2 });
            var expectedReport = DashboardHealth.Check(_storage.Object, _options, _now);
            var expectedMetrics = PrometheusDispatcher.Render(_storage.Object, _options, _now);

            var monitor = UseStorageMonitor(withQueueCountsFeature: true);
            monitor.Setup(x => x.Queues()).Throws(new InvalidOperationException("Queues() also reads the first jobs of each queue"));
            monitor.Setup(x => x.QueueCounts()).Returns(_queues);

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(WithoutTimings(expectedReport), WithoutTimings(report));
            Assert.Equal(expectedMetrics, PrometheusDispatcher.Render(_storage.Object, _options, _now));
            monitor.Verify(x => x.Queues(), Times.Never);
        }

        [Fact, StaticLock]
        public void Check_ReadsTheQueues_ThroughQueues_WhenTheStorageLacksTheFeature()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            var monitor = UseStorageMonitor(withQueueCountsFeature: false);
            monitor.Setup(x => x.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto> { new QueueWithTopEnqueuedJobsDto { Name = "default", Length = 4 } });

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal("1 queues, 4 jobs waiting", Check(report, "queues").Detail);
            monitor.Verify(x => x.QueueCounts(), Times.Never);
        }

        [Fact]
        public void QueueCounts_CallsQueues_ByDefault()
        {
            var queues = new List<QueueWithTopEnqueuedJobsDto> { new QueueWithTopEnqueuedJobsDto { Name = "default", Length = 1 } };
            var monitor = new Mock<JobStorageMonitor> { CallBase = true };
            monitor.Setup(x => x.Queues()).Returns(queues);

            Assert.Same(queues, monitor.Object.QueueCounts());
        }

        private Mock<JobStorageMonitor> UseStorageMonitor(bool withQueueCountsFeature)
        {
            var monitor = new Mock<JobStorageMonitor>();
            monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Enqueued = 3, Failed = 1, Servers = 1 });
            monitor.Setup(x => x.Servers()).Returns(_servers);
            _storage.Setup(x => x.GetMonitoringApi()).Returns(monitor.Object);
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Monitoring.QueueCounts)).Returns(withQueueCountsFeature);
            return monitor;
        }

        private static string WithoutTimings(HealthReport report)
        {
            // The storage check says how long the storage took to answer
            var json = JsonSerializer.SerializeToNode(report.ToJson());
            foreach (var check in json["checks"].AsArray())
            {
                if ((string)check["name"] == "storage") check["detail"] = null;
            }

            return json.ToJsonString();
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

        private void AdvertiseScoreQueries()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(true);
            _connection.Setup(x => x.GetFirstByLowestScoreFromSet("recurring-jobs", It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>())).Returns(new List<string>());
        }

        [Fact, StaticLock]
        public void Check_FindsLateRecurringJobs_FromTheSetScores_WithoutReadingEveryJob()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            AdvertiseScoreQueries();
            var lateBefore = JobHelper.ToTimestamp(_now - DashboardHealth.RecurringLateThreshold);
            _connection.Setup(x => x.GetFirstByLowestScoreFromSet("recurring-jobs", 0, lateBefore, DashboardHealth.MaxRecurringCandidates)).Returns(new List<string> { "nightly", "hourly" });

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal("2 late: nightly, hourly", Check(report, "recurring").Detail);
            Assert.Equal(2, report.LateRecurringJobs);
            _connection.Verify(x => x.GetAllItemsFromSet("recurring-jobs"), Times.Never);
            _connection.Verify(x => x.GetAllEntriesFromHash(It.Is<string>(k => k.StartsWith("recurring-job:"))), Times.Never);
            _connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Check_FindsRecurringJobsDisabledByErrors_AmongTheUnscheduledOnes()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            AdvertiseScoreQueries();
            _monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Recurring = 7 });
            _connection.Setup(x => x.GetFirstByLowestScoreFromSet("recurring-jobs", -1, -1, DashboardHealth.MaxRecurringCandidates)).Returns(new List<string> { "paused", "broken" });
            _connection.Setup(x => x.GetValueFromHash("recurring-job:broken", "Error")).Returns("Type not found");

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal("1 disabled by errors: broken", Check(report, "recurring").Detail);
            Assert.Equal(0, report.LateRecurringJobs);
        }

        [Fact, StaticLock]
        public void Check_CountsRecurringJobsOnSchedule_FromTheStatistics()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            AdvertiseScoreQueries();
            _monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Recurring = 7 });

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal(HealthStatus.Healthy, Check(report, "recurring").Status);
            Assert.Equal("7 on schedule", Check(report, "recurring").Detail);
        }

        [Fact, StaticLock]
        public void GetReport_ReusesAReport_ForTheCacheDuration_PerStorageAndServerThreshold()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");

            var first = DashboardHealth.GetReport(_storage.Object, _options, _now);
            var second = DashboardHealth.GetReport(_storage.Object, _options, _now.AddSeconds(4));
            var otherThreshold = DashboardHealth.GetReport(_storage.Object, new DashboardOptions { ServerPossiblyAbortedThreshold = TimeSpan.FromMinutes(10) }, _now.AddSeconds(4));
            var later = DashboardHealth.GetReport(_storage.Object, _options, _now.Add(DashboardHealth.CacheDuration).AddSeconds(1));

            Assert.Same(first, second);
            Assert.NotSame(first, otherThreshold);
            Assert.NotSame(first, later);
            _monitor.Verify(x => x.GetStatistics(), Times.Exactly(3));
        }

        [Fact, StaticLock]
        public void GetReport_SharesTheReport_BetweenOptionsInstancesWithTheSameServerThreshold()
        {
            // The dashboard, MapNightShiftHealth and MapNightShiftMetrics usually have options objects of their own
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");

            var dashboard = DashboardHealth.GetReport(_storage.Object, _options, _now);
            var health = DashboardHealth.GetReport(_storage.Object, new DashboardOptions { DashboardTitle = "Other" }, _now.AddSeconds(1));
            var otherThreshold = DashboardHealth.GetReport(_storage.Object, new DashboardOptions { ServerPossiblyAbortedThreshold = TimeSpan.FromMinutes(10) }, _now.AddSeconds(2));
            var dashboardAgain = DashboardHealth.GetReport(_storage.Object, _options, _now.AddSeconds(3));

            Assert.Same(dashboard, health);
            Assert.Same(dashboard, dashboardAgain);
            Assert.NotSame(dashboard, otherThreshold);
            _monitor.Verify(x => x.GetStatistics(), Times.Exactly(2));
        }

        [Fact]
        public void IsLate_IgnoresPausedRemovedDisabledAndFutureJobs()
        {
            Assert.True(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2) }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddSeconds(-30) }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2), Paused = true }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2), Removed = true }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { Error = "x" }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto(), _now));
        }

        [Fact]
        public void IsLate_CountsAnOverdueErrorRetry_LikeTheScoreQueryDoes()
        {
            // The scheduler failed and set the retry as the next run; nobody tried again for two minutes
            Assert.True(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(-2), Error = "x", RetryAttempt = 1 }, _now));
            Assert.False(DashboardHealth.IsLate(new RecurringJobDto { NextExecution = _now.AddMinutes(1), Error = "x", RetryAttempt = 1 }, _now));
        }

        [Fact, StaticLock]
        public void Check_CountsAnOverdueErrorRetry_AsLate_WhenItReadsEveryRecurringJob()
        {
            AddServer("web-1", TimeSpan.FromSeconds(5), "default");
            AddRecurring("retrying", _now.AddMinutes(-2), error: "Timeout");
            AddRecurring("disabled", null, error: "Type not found");

            var report = DashboardHealth.Check(_storage.Object, _options, _now);

            Assert.Equal("1 late: retrying", Check(report, "recurring").Detail);
            Assert.Equal(1, report.LateRecurringJobs);
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

            // A report is reused for the cache duration, whatever options object asks for it
            _servers.Clear();
            HealthDispatcher.Clock = () => _now.Add(DashboardHealth.CacheDuration);
            var unhealthy = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);

            await new HealthDispatcher().Dispatch(unhealthy);

            Assert.Equal(503, unhealthy.Response.StatusCode);
            Assert.Equal("unhealthy", JsonNode.Parse(unhealthy.Response.Text)["status"].GetValue<string>());
        }
    }
}
