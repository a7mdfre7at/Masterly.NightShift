using System;
using System.Collections.Generic;
using System.Net;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using System.Text.Json;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class HomePageFacts
    {
        private static readonly DateTime Hour = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_Always_ShowsTheRealtimeGraphWithTheCurrentCounters()
        {
            _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Succeeded = 12, Failed = 3, Deleted = 1 });

            var html = _renderer.Render(new HomePage());

            Assert.Contains($"<h3>{Encode(Strings.HomePage_RealtimeGraph)}</h3>", html);
            Assert.Contains("id=\"realtimeGraph\" data-succeeded=\"12\" data-failed=\"3\" data-deleted=\"1\"", html);
            Assert.Contains($"data-deleted-string=\"{Encode(Strings.JobsSidebarMenu_Deleted)}\"", html);
        }

        [Fact]
        public void Render_Always_ShowsTheActivityFeedPlaceholder()
        {
            var html = _renderer.Render(new HomePage());

            Assert.Contains($"{Encode(Strings.HomePage_Activity)} <small class=\"text-muted\">{Encode(Strings.HomePage_Activity_Hint)}</small>", html);
            Assert.Contains($"data-url=\"/activity\" data-job-url=\"/jobs/details/\" data-empty=\"{Encode(Strings.HomePage_Activity_Empty)}\"", html);
            Assert.Contains($"<li class=\"hf-activity-empty text-muted\">{Encode(Strings.HomePage_Activity_Empty)}</li>", html);
        }

        [Fact]
        public void Render_ByDefault_ShowsTheHourlyHistory()
        {
            var succeeded = new Dictionary<DateTime, long> { { Hour, 5 } };
            var failed = new Dictionary<DateTime, long> { { Hour, 2 } };
            _renderer.Monitoring.Setup(x => x.HourlySucceededJobs()).Returns(succeeded);
            _renderer.Monitoring.Setup(x => x.HourlyFailedJobs()).Returns(failed);

            var html = _renderer.Render(new HomePage());

            Assert.Contains($"<a href=\"?period=day\" class=\"btn btn-sm btn-default active\">{Encode(Strings.Common_PeriodDay)}</a>", html);
            Assert.Contains($"<a href=\"?period=week\" class=\"btn btn-sm btn-default \">{Encode(Strings.Common_PeriodWeek)}</a>", html);
            Assert.Contains("id=\"historyGraph\"", html);
            Assert.Contains($"data-succeeded=\"{Encode(JsonSerializer.Serialize(succeeded))}\"", html);
            Assert.Contains($"data-failed=\"{Encode(JsonSerializer.Serialize(failed))}\"", html);
            Assert.Contains("data-deleted=\"\"", html);
            Assert.Contains("data-period=\"day\"", html);
            _renderer.Monitoring.Verify(x => x.SucceededByDatesCount(), Times.Never);
        }

        [Fact]
        public void Render_ForTheWeek_ShowsTheDailyHistory()
        {
            var succeeded = new Dictionary<DateTime, long> { { Hour.Date, 50 } };
            _renderer.Monitoring.Setup(x => x.SucceededByDatesCount()).Returns(succeeded);
            _renderer.Request.Query["period"] = "WEEK";

            var html = _renderer.Render(new HomePage());

            Assert.Contains($"<a href=\"?period=day\" class=\"btn btn-sm btn-default \">", html);
            Assert.Contains($"<a href=\"?period=week\" class=\"btn btn-sm btn-default active\">", html);
            Assert.Contains($"data-succeeded=\"{Encode(JsonSerializer.Serialize(succeeded))}\"", html);
            Assert.Contains("data-period=\"WEEK\"", html);
            _renderer.Monitoring.Verify(x => x.HourlySucceededJobs(), Times.Never);
        }

        [Fact]
        public void Render_ForAnUnknownPeriod_LeavesTheHistoryGraphOut()
        {
            _renderer.Request.Query["period"] = "month";

            var html = _renderer.Render(new HomePage());

            Assert.Contains($"<h3>{Encode(Strings.HomePage_HistoryGraph)}</h3>", html);
            Assert.DoesNotContain("historyGraph", html);
            Assert.DoesNotContain("btn-default active", html);
        }

        [Theory]
        [InlineData("day")]
        [InlineData("week")]
        public void Render_WhenTheStorageGraphsDeletedJobs_IncludesThem(string period)
        {
            var deleted = new Dictionary<DateTime, long> { { Hour, 7 } };
            var monitor = new Mock<JobStorageMonitor>();
            monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto());
            monitor.Setup(x => x.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long>());
            monitor.Setup(x => x.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long>());
            monitor.Setup(x => x.HourlyDeletedJobs()).Returns(deleted);
            monitor.Setup(x => x.SucceededByDatesCount()).Returns(new Dictionary<DateTime, long>());
            monitor.Setup(x => x.FailedByDatesCount()).Returns(new Dictionary<DateTime, long>());
            monitor.Setup(x => x.DeletedByDatesCount()).Returns(deleted);
            _renderer.Storage.Setup(x => x.GetMonitoringApi()).Returns(monitor.Object);
            _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Monitoring.DeletedStateGraphs)).Returns(true);
            _renderer.Request.Query["period"] = period;

            var html = _renderer.Render(new HomePage());

            Assert.Contains($"data-deleted=\"{Encode(JsonSerializer.Serialize(deleted))}\"", html);
        }

        [Fact, GlobalLock(Reason = "Changes HomePage.Metrics")]
        public void Render_WithoutHomeMetrics_LeavesTheKpiGridOut()
        {
            var saved = new List<DashboardMetric>(HomePage.Metrics);
            HomePage.Metrics.Clear();

            try
            {
                var html = _renderer.Render(new HomePage());

                Assert.DoesNotContain("hf-kpi-grid", html);
            }
            finally
            {
                HomePage.Metrics.AddRange(saved);
            }
        }

        [Fact, GlobalLock(Reason = "Changes HomePage.Metrics")]
        public void Render_WithHomeMetrics_ShowsThemAsBlocks()
        {
            var saved = new List<DashboardMetric>(HomePage.Metrics);
            HomePage.Metrics.Clear();
            HomePage.Metrics.Add(DashboardMetrics.ServerCount);
            _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Servers = 0 });

            try
            {
                var html = _renderer.Render(new HomePage());

                Assert.Contains("<div class=\"hf-kpi-grid\">", html);
                Assert.Contains("<div class=\"metric-body\" data-metric=\"servers:count\">", html);
                Assert.Contains($"{Encode(Strings.Metrics_Servers)}", html);
            }
            finally
            {
                HomePage.Metrics.Clear();
                HomePage.Metrics.AddRange(saved);
            }
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
