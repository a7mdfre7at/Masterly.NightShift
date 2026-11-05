using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class JobCatalogPageFacts : IDisposable
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        public JobCatalogPageFacts()
        {
            JobCatalog.Clear();
            JobCatalog.Add<CatalogPageJobs>();
        }

        public void Dispose()
        {
            JobCatalog.Clear();
        }

        [Fact, StaticLock]
        public void Render_ListsTheCatalogGroupedByType_WithSummaryAndFilter()
        {
            var html = _renderer.Render(new JobCatalogPage());

            Assert.Contains($"{Encode(Strings.CatalogPage_Title)} <small class=\"text-muted\">{Encode(Strings.CatalogPage_Hint)}</small>", html);
            Assert.Contains($"placeholder=\"{Encode(Strings.CatalogPage_Search)}\"", html);
            Assert.Contains($"data-type=\"{typeof(CatalogPageJobs).FullName}\"", html);
            Assert.Contains($"<span class=\"text-muted\">{typeof(CatalogPageJobs).Namespace}.</span>", html);
            Assert.Contains("CatalogPageJobs</h3>", html);
            Assert.Contains($"<span class=\"text-muted\">3 {Encode(Strings.CatalogPage_Summary_Methods)}</span>", html);
            Assert.DoesNotContain(Strings.CatalogPage_NoEntries_Html, html);
        }

        [Fact, StaticLock]
        public void Render_DescribesEachMethod_WithParametersFiltersAndFlags()
        {
            var html = _renderer.Render(new JobCatalogPage());

            Assert.Contains($"data-search=\"{typeof(CatalogPageJobs).FullName} Import path count mode\"", html);
            Assert.Contains("<code class=\"hf-catalog-method\">Import</code>", html);
            Assert.Contains("<code class=\"hf-catalog-method\">async SendAsync</code>", html);
            Assert.Contains("<code class=\"hf-catalog-method\">Cleanup</code>", html);
            Assert.Contains($"<span class=\"label label-default\">{Encode(Strings.CatalogPage_Static)}</span>", html);
            Assert.Contains("<span class=\"hf-param \" title=\"string\">", html);
            Assert.Contains("<span class=\"hf-param-type\">int</span> count", html);
            Assert.Contains($"<span class=\"hf-param hf-param-injected\" title=\"{Encode(Strings.CatalogPage_Injected)}\">", html);
            Assert.Contains("<span class=\"label label-info\">AutomaticRetry</span>", html);
        }

        [Fact, StaticLock]
        public void Render_WithInsights_ShowsRunsMedianAndFailureRate()
        {
            SetupInsight("Import", succeeded: 8, failed: 2, durations: new[] { "300", "200", "100" });

            var html = _renderer.Render(new JobCatalogPage());

            Assert.Contains($"<span title=\"{Encode(Strings.InsightsPage_Table_Runs)}\">10&times;</span>", html);
            Assert.Contains($"<span class=\"text-muted\" title=\"{Encode(Strings.InsightsPage_Table_Median)}\">", html);
            Assert.Contains($"<span class=\"text-danger\" title=\"{Encode(Strings.InsightsPage_Table_Failed)}\">{Encode(0.2.ToString("P0"))}</span>", html);
        }

        [Fact, StaticLock]
        public void Render_WithInsightsWithoutFailures_LeavesTheFailureRateOut()
        {
            SetupInsight("SendAsync", succeeded: 4, failed: 0, durations: new[] { "50" });

            var html = _renderer.Render(new JobCatalogPage());

            Assert.Contains($"<span title=\"{Encode(Strings.InsightsPage_Table_Runs)}\">4&times;</span>", html);
            Assert.DoesNotContain($"title=\"{Encode(Strings.InsightsPage_Table_Failed)}\"", html);
        }

        [Fact, StaticLock]
        public void Render_WhenNotReadOnly_OffersARunButtonPerMethodAndTheRunner()
        {
            var html = _renderer.Render(new JobCatalogPage());

            Assert.Contains("class=\"btn btn-sm btn-primary js-catalog-run\"", html);
            Assert.Contains("data-method=\"Import\"", html);
            Assert.Contains($"data-id=\"{Encode(Entry("Import").Id)}\"", html);
            Assert.Contains(Encode("{\"name\":\"mode\",\"type\":\"CatalogPageMode\",\"input\":\"select\""), html);
            Assert.Contains(Encode("\"options\":[\"Fast\",\"Safe\"]"), html);
            Assert.Contains("id=\"catalogRunner\"", html);
            Assert.Contains("data-run-url=\"/catalog/run\"", html);
            Assert.Contains("data-preview-url=\"/recurring/preview\"", html);
            Assert.Contains($"data-text-redacted=\"{Encode(Strings.CatalogPage_Runner_Redacted)}\"", html);
            Assert.Contains($"data-loading-text=\"{Encode(Strings.RecurringJobsPage_Saving)}\">{Encode(Strings.CatalogPage_Submit)}</button>", html);
        }

        [Fact, StaticLock]
        public void Render_WhenReadOnly_HidesTheRunButtonsAndTheRunner()
        {
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new JobCatalogPage());

            Assert.Contains("<code class=\"hf-catalog-method\">Import</code>", html);
            Assert.DoesNotContain("js-catalog-run", html);
            Assert.DoesNotContain("catalogRunner", html);
        }

        private void SetupInsight(string method, long succeeded, long failed, string[] durations)
        {
            var key = Entry(method).InsightsKey;

            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"insights:{key}"))
                .Returns(new Dictionary<string, string> { { "LastDurationMs", "100" } });
            _renderer.Connection.Setup(x => x.GetCounter($"insights:{key}:ok")).Returns(succeeded);
            _renderer.Connection.Setup(x => x.GetCounter($"insights:{key}:failed")).Returns(failed);
            _renderer.Connection.Setup(x => x.GetAllItemsFromList($"insights:{key}:durations")).Returns(durations.ToList());
        }

        private static JobCatalogEntry Entry(string method)
        {
            return JobCatalog.GetEntries().Single(x => x.Type == typeof(CatalogPageJobs) && x.Method.Name == method);
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public enum CatalogPageMode
    {
        Fast,
        Safe
    }

    public class CatalogPageJobs
    {
        public void Import(string path, int count = 5, CatalogPageMode mode = CatalogPageMode.Fast, CancellationToken token = default)
        {
        }

        public Task SendAsync(string to)
        {
            return Task.CompletedTask;
        }

        [AutomaticRetry(Attempts = 0)]
        public static void Cleanup()
        {
        }
    }
}
