using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class InsightsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly HashSet<string> _insightKeys = new HashSet<string>();
        private readonly HashSet<string> _circuits = new HashSet<string>();

        public InsightsPageFacts()
        {
            _renderer.Connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(_insightKeys);
            _renderer.Connection.Setup(x => x.GetAllItemsFromSet(CircuitBreakerAttribute.CircuitsSetKey)).Returns(_circuits);
        }

        [Fact]
        public void Render_SaysInsightsAreNotSupported_WhenTheConnectionIsNotAJobStorageConnection()
        {
            _renderer.Storage.Setup(x => x.GetReadOnlyConnection()).Returns(new Mock<IStorageConnection>().Object);

            var html = _renderer.Render(new InsightsPage());

            Assert.Contains(H(Strings.InsightsPage_NotSupported), html);
            Assert.DoesNotContain("insights-summary", html);
        }

        [Fact]
        public void Render_SaysInsightsAreNotSupported_WhenTheStorageHasNoExtendedApi()
        {
            // The base JobStorageConnection throws for counters and lists: the page answered 500
            _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
            _renderer.Connection.Setup(x => x.GetCounter(It.IsAny<string>())).Throws(new NotSupportedException());
            _renderer.Connection.Setup(x => x.GetAllItemsFromList(It.IsAny<string>())).Throws(new NotSupportedException());

            var html = _renderer.Render(new InsightsPage());

            Assert.Contains(H(Strings.InsightsPage_NotSupported), html);
            Assert.DoesNotContain("insights-summary", html);
        }

        [Fact]
        public void Render_ShowsEmptySummaryTiles_AndTheNoDataHint_WhenNothingWasRecorded()
        {
            var html = _renderer.Render(new InsightsPage());

            Assert.Contains("insights-summary", html);
            Assert.Contains("<div class=\"insights-tile-value\">0</div>", html);
            Assert.Contains("<div class=\"insights-tile-value\">–</div>", html);
            Assert.DoesNotContain("insights-tile-danger", html);
            Assert.Contains(Strings.InsightsPage_NoData_Html, html);
            Assert.DoesNotContain("id=\"insights-anomalies\"", html);
            Assert.DoesNotContain("id=\"insights-circuits\"", html);
            Assert.DoesNotContain("id=\"insights-jobs\"", html);
        }

        [Fact]
        public void Render_SaysThereAreNoAnomalies_WhenEveryMethodBehaves()
        {
            AddHealthyInsight();

            var html = _renderer.Render(new InsightsPage());

            Assert.Contains("id=\"insights-anomalies\"", html);
            Assert.Contains("hf-anomalies-ok", html);
            Assert.Contains(H(Strings.InsightsPage_Anomalies_None), html);
            Assert.DoesNotContain("insights-tile-danger", html);
        }

        [Fact]
        public void Render_ListsEveryReason_OfAnAnomalousMethod()
        {
            AddAnomalousInsight();
            AddFailingInsight();

            var html = _renderer.Render(new InsightsPage());

            Assert.DoesNotContain("hf-anomalies-ok", html);
            Assert.Contains("<code>Jobs.Slow</code>", html);
            Assert.Contains("<code>Jobs.Failing</code>", html);
            Assert.Equal(2, CountOf(html, "<div class=\"hf-anomaly\">"));
            Assert.Contains("<span class=\"label label-warning\">" + H(String.Format(Strings.InsightsPage_Anomaly_SlowingDown, Duration(2000), Duration(100))) + "</span>", html);
            Assert.Contains("<span class=\"label label-warning\">" + H(String.Format(Strings.InsightsPage_Anomaly_SlowRun, Duration(7000))) + "</span>", html);
            Assert.Contains("<span class=\"label label-danger\">" + H(String.Format(Strings.InsightsPage_Anomaly_FailureRate, 0.5.ToString("P0"))) + "</span>", html);
            Assert.Equal(1, CountOf(html, "<span class=\"hf-anomaly-link\">"));
            Assert.Contains("<span class=\"hf-anomaly-link\"><a href=\"/jobs/details/42\">#42</a></span>", html);
            Assert.Contains("insights-tile insights-tile-danger", html);
        }

        [Fact]
        public void Render_ShowsAStatisticsRow_PerMethod_WithFailureAndInvokerLabels()
        {
            AddAnomalousInsight();
            AddHealthyInsight();
            AddNeverRunInsight();

            var html = _renderer.Render(new InsightsPage());

            Assert.Contains("id=\"insights-jobs\"", html);
            Assert.Contains("<span class=\"label label-danger\">10 (" + 0.5.ToString("P0"), html);
            Assert.Contains("<span class=\"label label-warning\">1 (" + 0.1.ToString("P0"), html);
            Assert.Contains("<span class=\"label label-default\">0 (" + 0d.ToString("P0"), html);
            Assert.Contains("<span class=\"label label-success\">Generated</span>", html);
            Assert.Contains("<span class=\"label label-info\">Compiled</span>", html);
            Assert.Contains("<span class=\"label label-default\">Reflection</span>", html);
            Assert.Contains("title=\"" + H(Strings.InsightsPage_SlowRun_Tooltip) + "\"", html);
            Assert.Contains("<a href=\"/jobs/details/42\" class=\"text-decoration-none\"><span data-moment=", html);
            Assert.Contains("sparkline-last sparkline-last-failed", html);
            Assert.Contains("<circle class=\"sparkline-last\"", html);
        }

        [Fact]
        public void Render_ShowsTheLastRunWithoutALink_WhenTheJobIdIsUnknown()
        {
            AddHealthyInsight();

            var html = _renderer.Render(new InsightsPage());

            Assert.DoesNotContain("class=\"text-decoration-none\"", html);
            Assert.Contains("<span data-moment=", Section(html, "id=\"insights-jobs\""));
        }

        [Fact]
        public void Render_ListsTheCircuits_OpenOnesFirst_WithResetButtons()
        {
            AddCircuit("payments", CircuitBreakerAttribute.OpenState, DateTime.UtcNow.AddMinutes(-2), 5);
            AddCircuit("mail", CircuitBreakerAttribute.HalfOpenState, DateTime.UtcNow.AddMinutes(-1), 1);
            AddCircuit("archive", CircuitBreakerAttribute.ClosedState, null, 2);
            AddCircuit("search", CircuitBreakerAttribute.ClosedState, null, 0);

            var html = _renderer.Render(new InsightsPage());

            Assert.Contains("id=\"insights-circuits\"", html);
            Assert.Contains("label label-danger text-uppercase\">Open</span>", html);
            Assert.Contains("label label-warning text-uppercase\">HalfOpen</span>", html);
            Assert.Equal(2, CountOf(html, "label label-success text-uppercase\">Closed</span>"));
            Assert.True(html.IndexOf("<code>mail</code>", StringComparison.Ordinal) < html.IndexOf("<code>archive</code>", StringComparison.Ordinal));
            Assert.True(html.IndexOf("<code>payments</code>", StringComparison.Ordinal) < html.IndexOf("<code>search</code>", StringComparison.Ordinal));
            Assert.Contains("data-ajax=\"/insights/circuits/reset/payments\"", html);
            Assert.Contains("data-ajax=\"/insights/circuits/reset/mail\"", html);
            Assert.Contains("data-ajax=\"/insights/circuits/reset/archive\"", html);
            Assert.DoesNotContain("/insights/circuits/reset/search", html);
            Assert.Equal(2, CountOf(Section(html, "id=\"insights-circuits\""), "<span data-moment="));
        }

        [Fact]
        public void Render_HidesTheResetButtons_WhenTheDashboardIsReadOnly()
        {
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
            AddCircuit("payments", CircuitBreakerAttribute.OpenState, DateTime.UtcNow, 5);

            var html = _renderer.Render(new InsightsPage());

            Assert.Contains("<code>payments</code>", html);
            Assert.DoesNotContain("/insights/circuits/reset/", html);
            Assert.DoesNotContain("<th class=\"min-width\"></th>", html);
        }

        [Fact]
        public void Render_ShowsTheActivityHeatmap_WithoutAnyCircuitBreakers()
        {
            var html = _renderer.Render(new InsightsPage());

            Assert.Contains("id=\"insights-heatmap\"", html);
            Assert.Equal(7 * 24, CountOf(html, "<div class=\"hf-heatmap-cell"));
            Assert.DoesNotContain("id=\"insights-circuits\"", html);
        }

        [Fact]
        public void Render_ShowsAnEmptyActivityHeatmap_WhenNoJobFinishedThisWeek()
        {
            AddCircuit("payments", CircuitBreakerAttribute.OpenState, DateTime.UtcNow, 1);

            var html = _renderer.Render(new InsightsPage());

            Assert.Contains("id=\"insights-heatmap\"", html);
            Assert.Contains(H(Strings.InsightsPage_Heatmap_Empty), html);
            Assert.Equal(24, CountOf(html, "<div class=\"hf-heatmap-hour\">"));
            Assert.Equal(7, CountOf(html, "<div class=\"hf-heatmap-day\">"));
            Assert.Equal(7 * 24, CountOf(html, "<div class=\"hf-heatmap-cell"));
            Assert.Contains("<div class=\"hf-heatmap-hour\">00</div>", html);
            Assert.Contains("<div class=\"hf-heatmap-hour\">21</div>", html);
            Assert.DoesNotContain("hf-heatmap-bad", html);
        }

        [Fact]
        public void Render_ShadesTheHeatmap_AndMarksHoursWithMoreFailures()
        {
            AddCircuit("payments", CircuitBreakerAttribute.OpenState, DateTime.UtcNow, 1);
            _renderer.Connection.Setup(x => x.GetCounter(It.Is<string>(k => k.StartsWith("stats:succeeded:", StringComparison.Ordinal)))).Returns(1);
            _renderer.Connection.Setup(x => x.GetCounter(It.Is<string>(k => k.StartsWith("stats:failed:", StringComparison.Ordinal)))).Returns(3);

            var html = _renderer.Render(new InsightsPage());

            Assert.DoesNotContain(H(Strings.InsightsPage_Heatmap_Empty), html);
            Assert.Contains("hf-heatmap-cell hf-heatmap-bad", html);
            Assert.Contains("style=\"--hf-heat: 1\"", html);
        }

        private void AddAnomalousInsight()
        {
            // Ten quick runs, then ten slow ones ending with a very slow, failed run; half of all runs failed
            var newestFirst = new[] { 7000L }.Concat(Enumerable.Repeat(2000L, 9)).Concat(Enumerable.Repeat(100L, 10));
            AddInsight("Jobs.Slow", 10, 10, newestFirst, new Dictionary<string, string>
            {
                { "LastRunAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-1)) },
                { "LastDurationMs", "7000" },
                { "LastJobId", "42" },
                { "LastFailed", "1" },
                { "Invoker", "Generated" },
            });
        }

        private void AddFailingInsight()
        {
            AddInsight("Jobs.Failing", 5, 5, Enumerable.Empty<long>(), new Dictionary<string, string> { { "Invoker", "Generated" } });
        }

        private void AddHealthyInsight()
        {
            AddInsight("Jobs.Fine", 9, 1, new[] { 100L, 100L, 100L }, new Dictionary<string, string>
            {
                { "LastRunAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddHours(-2)) },
                { "LastDurationMs", "100" },
                { "Invoker", "Compiled" },
            });
        }

        private void AddNeverRunInsight()
        {
            AddInsight("Jobs.Never", 0, 0, Enumerable.Empty<long>(), new Dictionary<string, string> { { "Invoker", "Reflection" } });
        }

        private void AddInsight(string key, long succeeded, long failed, IEnumerable<long> durationsNewestFirst, Dictionary<string, string> hash)
        {
            _insightKeys.Add(key);
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"insights:{key}")).Returns(hash);
            _renderer.Connection.Setup(x => x.GetAllItemsFromList($"insights:{key}:durations"))
                .Returns(durationsNewestFirst.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList());
            _renderer.Connection.Setup(x => x.GetCounter($"insights:{key}:ok")).Returns(succeeded);
            _renderer.Connection.Setup(x => x.GetCounter($"insights:{key}:failed")).Returns(failed);
        }

        private void AddCircuit(string resource, string state, DateTime? openedAt, int failures)
        {
            _circuits.Add(resource);

            var hash = new Dictionary<string, string> { { "State", state } };
            if (openedAt.HasValue)
            {
                hash.Add("OpenedAt", new DateTimeOffset(openedAt.Value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            }

            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"circuit:{resource}")).Returns(hash);
            _renderer.Connection.Setup(x => x.GetAllItemsFromSet($"circuit:{resource}:failures"))
                .Returns(new HashSet<string>(Enumerable.Range(0, failures).Select(x => x.ToString(CultureInfo.InvariantCulture))));
        }

        private static string Section(string html, string marker) => html.Substring(html.IndexOf(marker, StringComparison.Ordinal));

        private static string Duration(long milliseconds)
        {
            var page = new InsightsPage();
            page.Assign(new DashboardContextStub(new DashboardOptions()));
            return new HtmlHelper(page).ToHumanDuration(TimeSpan.FromMilliseconds(milliseconds), false);
        }

        private static int CountOf(string html, string fragment)
        {
            return (html.Length - html.Replace(fragment, String.Empty).Length) / fragment.Length;
        }

        private static string H(string text) => WebUtility.HtmlEncode(text);
    }
}
