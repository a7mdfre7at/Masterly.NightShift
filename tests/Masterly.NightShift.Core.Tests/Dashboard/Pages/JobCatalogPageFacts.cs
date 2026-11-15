using System.Net;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class JobCatalogPageFacts : IDisposable
{
    private readonly PageRenderer _renderer = new();
    private readonly HashSet<string> _recorded = [];

    public JobCatalogPageFacts()
    {
        JobCatalog.Clear();
        JobCatalog.Add<CatalogPageJobs>();
        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(_recorded);
    }

    public void Dispose()
    {
        JobCatalog.Clear();
    }

    [Fact, StaticLock]
    public void Render_ListsTheCatalogGroupedByType_WithSummaryAndFilter()
    {
        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains("<header class=\"ns-page-head\">", html);
        // Every method of the catalog: the types added here and the generated invokers of this assembly
        Assert.Contains($"<span class=\"ns-page-name\">{Encode(Strings.CatalogPage_Title)}</span> <span class=\"ns-count-chip js-list-count\">{JobCatalog.GetEntries().Count:N0}</span></h1>", html);
        Assert.Contains($"<p class=\"ns-page-subtitle\">{Encode(Strings.CatalogPage_Hint)}</p>", html);
        Assert.Contains($"<label for=\"catalogFilter\" class=\"visually-hidden\">{Encode(Strings.CatalogPage_Search)}</label>", html);
        Assert.Contains($"id=\"catalogFilter\" class=\"form-control js-catalog-filter\" placeholder=\"{Encode(Strings.CatalogPage_Search)}\"", html);
        string group = Regex.Match(html, $"data-type=\"{Regex.Escape(typeof(CatalogPageJobs).FullName!)}\" aria-labelledby=\"(catalog-type-[0-9]+)\"").Groups[1].Value;
        Assert.NotEmpty(group);
        Assert.Contains($"<h2 id=\"{group}\">", html);
        Assert.Contains($"<span class=\"ns-catalog-namespace\">{typeof(CatalogPageJobs).Namespace}.</span>CatalogPageJobs</h2>", html);
        Assert.Contains($"<span class=\"ns-count-chip\">4 {Encode(Strings.CatalogPage_Summary_Methods)}</span>", html);
        Assert.Contains("<div class=\"ns-empty-state ns-empty-state-compact js-catalog-nomatch\" hidden=\"hidden\">", html);
        PageStructure.AssertHeadingsInOrder(PageStructure.WithoutTemplates(html));
        PageStructure.AssertNoEmptyHeaderCells(html);
        Assert.DoesNotContain(Strings.CatalogPage_NoEntries_Html, html);
    }

    [Fact, StaticLock]
    public void Render_DescribesEachMethod_WithParametersFiltersAndFlags()
    {
        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains($"data-search=\"{typeof(CatalogPageJobs).FullName} Import path count mode\"", html);
        Assert.Contains("<code class=\"hf-catalog-method\">Import</code>", html);
        Assert.Contains("<code class=\"hf-catalog-method\">async SendAsync</code>", html);
        Assert.Contains("<code class=\"hf-catalog-method\">Cleanup</code>", html);
        Assert.Contains($"<span class=\"ns-chip ns-chip-status ns-catalog-flag\">{Encode(Strings.CatalogPage_Static)}</span>", html);
        Assert.Contains("<span class=\"hf-param \" title=\"string\">", html);
        Assert.Contains("<span class=\"hf-param-type\">int</span> count", html);
        Assert.Contains($"<span class=\"hf-param hf-param-injected\" title=\"{Encode(Strings.CatalogPage_Injected)}\">", html);
        Assert.Contains("<span class=\"ns-chip ns-chip-status\">" + DashboardMarkup.Icon("arrow-repeat") + " " + Encode(string.Format(Strings.CatalogPage_Fact_Retries, 0)) + "</span>", html);
        Assert.Contains("<li class=\"ns-list-item js-catalog-entry\"", html);
    }

    [Fact, StaticLock]
    public void Render_WithInsights_ShowsRunsMedianAndFailureRate()
    {
        SetupInsight("Import", succeeded: 8, failed: 2, durations: ["300", "200", "100"]);

        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains($"<span title=\"{Encode(Strings.InsightsPage_Table_Runs)}\">10&times;</span>", html);
        Assert.Contains($"<span title=\"{Encode(Strings.InsightsPage_Table_Median)}\">", html);
        Assert.Contains($"<span class=\"ns-catalog-failures\" title=\"{Encode(Strings.InsightsPage_Table_Failed)}\">{Encode(0.2.ToString("P0"))}</span>", html);
    }

    [Fact, StaticLock]
    public void Render_WithInsightsWithoutFailures_LeavesTheFailureRateOut()
    {
        SetupInsight("SendAsync", succeeded: 4, failed: 0, durations: ["50"]);

        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains($"<span title=\"{Encode(Strings.InsightsPage_Table_Runs)}\">4&times;</span>", html);
        Assert.DoesNotContain($"title=\"{Encode(Strings.InsightsPage_Table_Failed)}\"", html);
    }

    [Fact, StaticLock]
    public void Render_ReadsInsights_OnlyForTheMethodsJobInsightsHasRecorded()
    {
        // Every catalog method cost four reads per view, recorded or not
        SetupInsight("SendAsync", succeeded: 4, failed: 0, durations: ["50"]);

        _renderer.Render(new JobCatalogPage());

        _renderer.Connection.Verify(x => x.GetAllEntriesFromHash($"insights:{Entry("SendAsync").InsightsKey}"), Times.Once);
        _renderer.Connection.Verify(x => x.GetAllEntriesFromHash($"insights:{Entry("Import").InsightsKey}"), Times.Never);
        _renderer.Connection.Verify(x => x.GetCounter(It.Is<string>(k => k.Contains(Entry("Cleanup").InsightsKey))), Times.Never);
    }

    [Fact, StaticLock]
    public void Render_OffersFormFieldsForNullableBoolsAndFlags()
    {
        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains(Encode("{\"name\":\"enabled\",\"type\":\"bool?\",\"input\":\"select\""), html);
        Assert.Contains(Encode("\"options\":[\"true\",\"false\"],\"nullable\":true,\"multiple\":false"), html);
        Assert.Contains(Encode("{\"name\":\"flags\",\"type\":\"CatalogPageFlags\",\"input\":\"select\",\"default\":\"Read, Write\""), html);
        Assert.Contains(Encode("\"nullable\":false,\"multiple\":true"), html);
    }

    [Fact, StaticLock]
    public void Render_WhenNotReadOnly_OffersARunButtonPerMethodAndTheRunner()
    {
        string html = _renderer.Render(new JobCatalogPage());

        string runName = Encode(string.Format(Strings.CatalogPage_RunMethod, "CatalogPageJobs.Import"));
        Assert.Contains($"class=\"ns-icon-button js-catalog-run\" aria-haspopup=\"dialog\" aria-label=\"{runName}\" title=\"{runName}\"", html);
        Assert.Contains("<div class=\"ns-list-item-end ns-row-actions\">", html);
        Assert.Contains("<div id=\"catalogRunner\" hidden=\"hidden\"", html);
        Assert.Contains("<template class=\"js-catalog-template\">", html);
        Assert.Contains($"<legend>{Encode(Strings.CatalogPage_Table_Parameters)}</legend>", html);
        Assert.Contains($"<legend>{Encode(Strings.CatalogPage_Runner_How)}</legend>", html);
        Assert.Contains($"role=\"radiogroup\" aria-label=\"{Encode(Strings.CatalogPage_Runner_How)}\"", html);
        foreach (string field in (string[])["catalogQueue", "catalogDelay", "catalogRecurringId", "catalogCron"])
        {
            Assert.Contains($"<label for=\"{field}\">", html);
        }

        Assert.Contains($"data-text-required=\"{Encode(Strings.CatalogPage_Runner_Required)}\"", html);
        Assert.Contains($"data-text-json=\"{Encode(Strings.CatalogPage_Runner_InvalidJson)}\"", html);
        Assert.Contains("<button type=\"submit\" form=\"catalogRunnerForm\" class=\"btn btn-sm btn-primary js-catalog-submit\"", html);
        Assert.DoesNotContain("class=\"modal fade\" id=\"catalogRunner\"", html);
        Assert.Contains("data-method=\"Import\"", html);
        Assert.Contains($"data-id=\"{Encode(Entry("Import").Id)}\"", html);
        Assert.Contains(Encode("{\"name\":\"mode\",\"type\":\"CatalogPageMode\",\"input\":\"select\""), html);
        Assert.Contains(Encode("\"options\":[\"Fast\",\"Safe\"]"), html);
        Assert.Contains("id=\"catalogRunner\"", html);
        Assert.Contains("data-run-url=\"/catalog/run\"", html);
        Assert.Contains("data-preview-url=\"/recurring/preview\"", html);
        Assert.Contains($"data-text-redacted=\"{Encode(Strings.CatalogPage_Runner_Redacted)}\"", html);
        Assert.Contains($"data-text-redacted-tag=\"{Encode(Strings.CatalogPage_Runner_RedactedTag)}\"", html);
        // Create says what it does while it works
        Assert.Contains($"data-loading-text=\"{Encode(Strings.CatalogPage_Creating)}\">{Encode(Strings.CatalogPage_Submit)}</button>", html);
    }

    [Fact, StaticLock]
    public void Render_WhenReadOnly_HidesTheRunButtonsAndTheRunner()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains("<code class=\"hf-catalog-method\">Import</code>", html);
        Assert.DoesNotContain("js-catalog-run", html);
        Assert.DoesNotContain("catalogRunner", html);
    }

    [Fact, StaticLock]
    public void Render_ShowsHowAMethodRuns_FromItsFilters_AsChips()
    {
        JobCatalog.Add<CatalogFactJobs>();

        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains("<a class=\"ns-queue-chip\" href=\"/jobs/enqueued/critical\">", html);
        Assert.Contains(Fact("hourglass-split", string.Format(Strings.CatalogPage_Fact_Timeout, Duration(TimeSpan.FromSeconds(90)))), html);
        Assert.Contains(Fact("lock-fill", Strings.CatalogPage_Fact_OneAtATime), html);
        Assert.Contains(Fact("lock-fill", string.Format(Strings.CatalogPage_Fact_AtMost, 3)), html);
        Assert.Contains(Fact("lightning-fill", string.Format(Strings.CatalogPage_Fact_RateLimit, 10, Duration(TimeSpan.FromMinutes(1)))), html);
        Assert.Contains(Fact("clock", string.Format(Strings.CatalogPage_Fact_Window, "22:00-06:00")), html);
        // The type's retry filter applies to its methods; the method's own filters are listed by name when no fact
        // stands for them
        Assert.Contains(Fact("arrow-repeat", string.Format(Strings.CatalogPage_Fact_Retries, 2)), html);
        Assert.Contains("<span class=\"ns-chip ns-chip-status ns-catalog-filter\">PreventDuplicates</span>", html);
        Assert.DoesNotContain(">Queue</span>", html);
    }

    [Fact, StaticLock]
    public void Render_WhenReadOnly_KeepsTheFilterAndHeadings()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new JobCatalogPage());

        Assert.Contains("js-catalog-filter", html);
        Assert.DoesNotContain("ns-row-actions", html);
        PageStructure.AssertHeadingsInOrder(html);
    }

    private string Fact(string icon, string text) =>
        "<span class=\"ns-chip ns-chip-status\">" + DashboardMarkup.Icon(icon) + " " + Encode(text) + "</span>";

    private static string Duration(TimeSpan duration)
    {
        JobCatalogPage page = new();
        return new Masterly.NightShift.Dashboard.HtmlHelper(page).ToHumanDuration(duration, false)!.Trim();
    }

    private void SetupInsight(string method, long succeeded, long failed, string[] durations)
    {
        string key = Entry(method).InsightsKey;
        _recorded.Add(key);

        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"insights:{key}"))
            .Returns(new Dictionary<string, string> { { "LastDurationMs", "100" } });
        _renderer.Connection.Setup(x => x.GetCounter($"insights:{key}:ok")).Returns(succeeded);
        _renderer.Connection.Setup(x => x.GetCounter($"insights:{key}:failed")).Returns(failed);
        _renderer.Connection.Setup(x => x.GetAllItemsFromList($"insights:{key}:durations")).Returns([.. durations]);
    }

    private static JobCatalogEntry Entry(string method)
    {
        return JobCatalog.GetEntries().Single(x => x.Type == typeof(CatalogPageJobs) && x.Method.Name == method);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

[Flags]
public enum CatalogPageFlags
{
    None = 0,
    Read = 1,
    Write = 2
}

public enum CatalogPageMode
{
    Fast,
    Safe
}

[AutomaticRetry(Attempts = 2)]
public class CatalogFactJobs
{
    [Queue("critical")]
    [JobTimeout(90)]
    [DisableConcurrentExecution(60)]
    [Throttle(MaxConcurrency = 3)]
    [RateLimit(null, 10, 60)]
    [RunWindow("22:00-06:00")]
    [PreventDuplicates]
    public void Nightly()
    {
    }
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

    public void Configure(bool? enabled, CatalogPageFlags flags = CatalogPageFlags.Read | CatalogPageFlags.Write)
    {
    }
}
