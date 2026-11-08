using System;
using System.Collections.Generic;
using System.Linq;
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
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
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
            Assert.Contains("nightshift_jobs_finished_total{state=\"succeeded\"} 1000\n", text);
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

        [Fact, StaticLock]
        public void Render_WritesEveryFamily_AsOneGroup_AfterItsTypeLine()
        {
            foreach (var method in new[] { "A.Run", "B.Run" })
            {
                _connection.Setup(x => x.GetAllEntriesFromHash($"insights:{method}")).Returns(new Dictionary<string, string> { { "LastDurationMs", "10" } });
                _connection.Setup(x => x.GetAllItemsFromList($"insights:{method}:durations")).Returns(new List<string> { "100" });
            }

            foreach (var queue in new[] { "critical", "default" })
            {
                _connection.Setup(x => x.GetAllEntriesFromHash($"queue:{queue}:insights")).Returns(new Dictionary<string, string>());
                _connection.Setup(x => x.GetAllItemsFromList($"queue:{queue}:latency")).Returns(new List<string> { "1000" });
            }

            _connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(new HashSet<string> { "A.Run", "B.Run" });
            _connection.Setup(x => x.GetAllItemsFromSet(QueueInsights.IndexKey)).Returns(new HashSet<string> { "critical", "default" });

            var text = PrometheusDispatcher.Render(_storage.Object, new DashboardOptions(), DateTime.UtcNow);

            var finished = new HashSet<string>();
            var typed = new HashSet<string>();
            string current = null;
            foreach (var line in text.Split('\n').Where(l => l.Length > 0))
            {
                if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
                {
                    typed.Add(line.Split(' ')[2]);
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                var family = line.Split('{', ' ')[0];
                Assert.Contains(family, typed);
                if (family != current)
                {
                    Assert.DoesNotContain(family, finished);
                    if (current != null) finished.Add(current);
                    current = family;
                }
            }

            Assert.Contains("nightshift_method_duration_seconds", finished.Concat(new[] { current }));
            Assert.Contains("nightshift_queue_started_per_minute", finished);
        }

        [Fact, StaticLock]
        public void Render_GivesEveryFamily_ANameNoOtherFamilyResolvesTo()
        {
            _connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(new HashSet<string> { "A.Run" });
            _connection.Setup(x => x.GetAllEntriesFromHash("insights:A.Run")).Returns(new Dictionary<string, string> { { "LastDurationMs", "10" } });
            _connection.Setup(x => x.GetAllItemsFromList("insights:A.Run:durations")).Returns(new List<string> { "100" });

            var text = PrometheusDispatcher.Render(_storage.Object, new DashboardOptions(), DateTime.UtcNow);

            // OpenMetrics parsers name a counter's family without its "_total" suffix, and reject a scrape where
            // that name is also another family's ("nightshift_jobs" gauge next to a "nightshift_jobs_total" counter)
            var families = text.Split('\n')
                .Where(static l => l.StartsWith("# TYPE ", StringComparison.Ordinal))
                .Select(static l => l.Split(' '))
                .Select(static p => p[3] == "counter" && p[2].EndsWith("_total", StringComparison.Ordinal) ? p[2].Substring(0, p[2].Length - "_total".Length) : p[2])
                .ToList();

            Assert.Contains("nightshift_jobs", families);
            Assert.Contains("nightshift_jobs_finished", families);
            Assert.Contains("nightshift_method_runs", families);
            Assert.Equal(families.Count, families.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact, StaticLock]
        public void Render_LeavesOutInsights_WhenTheStorageHasNoExtendedApi()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
            _connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(new HashSet<string> { "App.Jobs.Send" });

            var text = PrometheusDispatcher.Render(_storage.Object, new DashboardOptions(), DateTime.UtcNow);

            Assert.Contains("nightshift_jobs{state=\"enqueued\"} 12\n", text);
            Assert.DoesNotContain("nightshift_method_runs_total", text);
            _connection.Verify(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey), Times.Never);
        }

        [Fact, StaticLock]
        public void RenderCached_ReusesTheText_ForTheCacheDuration()
        {
            var options = new DashboardOptions();
            var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

            var first = PrometheusDispatcher.RenderCached(_storage.Object, options, now);
            var second = PrometheusDispatcher.RenderCached(_storage.Object, options, now.AddSeconds(3));
            PrometheusDispatcher.RenderCached(_storage.Object, options, now.Add(DashboardHealth.CacheDuration).AddSeconds(1));

            Assert.Same(first, second);
            _monitor.Verify(x => x.GetStatistics(), Times.Exactly(2));
        }
    }
}
