using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class PrometheusDispatcherFacts : IDisposable
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();

        public PrometheusDispatcherFacts()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Enqueued = 12, Failed = 3, Succeeded = 1000, Recurring = 4, Awaiting = 1 });
            _monitor.Setup(x => x.Servers()).Returns(new List<ServerDto> { new ServerDto { Name = "web-1", Heartbeat = DateTime.UtcNow, StartedAt = DateTime.UtcNow, Queues = new[] { "default", "de\"fault" }, WorkersCount = 5 } });
            _monitor.Setup(x => x.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto> { new QueueWithTopEnqueuedJobsDto { Name = "de\"fault", Length = 12, Fetched = 2 } });
            _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(new Dictionary<string, string>());
            _connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns(new HashSet<string>());
            DrainMode.InvalidateCache();
        }

        public void Dispose()
        {
            DrainMode.InvalidateCache();
        }

        [Fact, StaticLock]
        public async Task Dispatch_WritesTheExpositionFormat()
        {
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);

            await new PrometheusDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.StartsWith("text/plain; version=0.0.4", context.Response.ContentType);
            var text = context.Response.Text;
            Assert.Contains("# TYPE nightshift_jobs gauge\n", text);
            Assert.Contains("nightshift_jobs{state=\"enqueued\"} 12\n", text);
            Assert.Contains("nightshift_jobs{state=\"failed\"} 3\n", text);
            Assert.Contains("nightshift_jobs_total{state=\"succeeded\"} 1000\n", text);
            Assert.Contains("nightshift_servers 1\n", text);
            Assert.Contains("nightshift_health_status 0\n", text);
            Assert.Contains("nightshift_processing_paused 0\n", text);
            Assert.Contains("nightshift_recurring_jobs 4\n", text);
            Assert.Contains("nightshift_queue_length{queue=\"de\\\"fault\"} 12\n", text);
            Assert.Contains("nightshift_queue_fetched{queue=\"de\\\"fault\"} 2\n", text);
        }

        [Fact, StaticLock]
        public void Render_CountsLateRecurringJobs_FromTheHealthCheck()
        {
            var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { "nightly", "weekly", "on-time" });
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:nightly")).Returns(new Dictionary<string, string> { { "Cron", "0 1 * * *" }, { "NextExecution", JobHelper.SerializeDateTime(now.AddHours(-2)) } });
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:weekly")).Returns(new Dictionary<string, string> { { "Cron", "0 1 * * 1" }, { "NextExecution", JobHelper.SerializeDateTime(now.AddMinutes(-10)) } });
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:on-time")).Returns(new Dictionary<string, string> { { "Cron", "* * * * *" }, { "NextExecution", JobHelper.SerializeDateTime(now.AddMinutes(1)) } });

            var text = PrometheusDispatcher.Render(_storage.Object, new DashboardOptions(), now);

            Assert.Contains("# TYPE nightshift_recurring_jobs_late gauge\n", text);
            Assert.Contains("nightshift_recurring_jobs_late 2\n", text);
        }

        [Fact, StaticLock]
        public void Render_ReportsZeroLateRecurringJobs_WhenAllAreOnSchedule()
        {
            var text = PrometheusDispatcher.Render(_storage.Object, new DashboardOptions(), DateTime.UtcNow);

            Assert.Contains("nightshift_recurring_jobs_late 0\n", text);
        }

        [Fact, StaticLock]
        public void Render_KeepsTheOtherMetrics_AndSaysWhy_WhenInsightsCanNotBeRead()
        {
            _connection.Setup(x => x.GetAllItemsFromSet(QueueInsights.IndexKey)).Throws(new InvalidOperationException("boom"));

            var text = PrometheusDispatcher.Render(_storage.Object, new DashboardOptions(), DateTime.UtcNow);

            Assert.Contains("# insights unavailable: InvalidOperationException\n", text);
            Assert.Contains("nightshift_jobs{state=\"enqueued\"} 12\n", text);
            Assert.DoesNotContain("nightshift_queue_latency_seconds", text);
            Assert.DoesNotContain("nightshift_method_runs_total", text);
        }

        [Fact, StaticLock]
        public async Task Dispatch_IncludesInsights_WhenRecorded()
        {
            _connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(new HashSet<string> { "App.Jobs.Send" });
            _connection.Setup(x => x.GetAllEntriesFromHash("insights:App.Jobs.Send")).Returns(new Dictionary<string, string> { { "LastDurationMs", "10" } });
            _connection.Setup(x => x.GetAllItemsFromList("insights:App.Jobs.Send:durations")).Returns(new List<string> { "100", "200", "300" });
            _connection.Setup(x => x.GetCounter("insights:App.Jobs.Send:ok")).Returns(7);
            _connection.Setup(x => x.GetCounter("insights:App.Jobs.Send:failed")).Returns(1);
            _connection.Setup(x => x.GetAllItemsFromSet(QueueInsights.IndexKey)).Returns(new HashSet<string> { "default" });
            _connection.Setup(x => x.GetAllEntriesFromHash("queue:default:insights")).Returns(new Dictionary<string, string>());
            _connection.Setup(x => x.GetAllItemsFromList("queue:default:latency")).Returns(new List<string> { "1000", "3000" });
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);

            await new PrometheusDispatcher().Dispatch(context);

            var text = context.Response.Text;
            Assert.Contains("nightshift_method_runs_total{method=\"App.Jobs.Send\",result=\"succeeded\"} 7\n", text);
            Assert.Contains("nightshift_method_runs_total{method=\"App.Jobs.Send\",result=\"failed\"} 1\n", text);
            Assert.Contains("nightshift_method_duration_seconds{method=\"App.Jobs.Send\",quantile=\"0.5\"} 0.2\n", text);
            Assert.Contains("nightshift_queue_latency_seconds{queue=\"default\",quantile=\"0.5\"} 3\n", text);
            Assert.Contains("nightshift_queue_started_per_minute{queue=\"default\"} 0\n", text);
        }
    }
}
