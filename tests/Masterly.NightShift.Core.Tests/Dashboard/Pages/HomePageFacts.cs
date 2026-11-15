using System.Globalization;
using System.Net;
using System.Text.Json;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class HomePageFacts
{
    private static readonly DateTime Hour = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_Always_ShowsTheRealtimeGraphWithTheCurrentCounters()
    {
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Succeeded = 12, Failed = 3, Deleted = 1 });

        string html = _renderer.Render(new HomePage());

        Assert.Contains($"<h2 id=\"home-realtime\">{Encode(Strings.HomePage_RealtimeGraph)}</h2>", html);
        Assert.Contains("id=\"realtimeGraph\" data-succeeded=\"12\" data-failed=\"3\" data-deleted=\"1\"", html);
        Assert.Contains($"data-deleted-string=\"{Encode(Strings.JobsSidebarMenu_Deleted)}\"", html);
    }

    [Fact]
    public void Render_SumsUpTheRealtimeGraph_BeforeItsFirstPoll_AndNamesTheActivitysStates_InTheReadersLanguage()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        string html;
        string sentence;
        try
        {
            CultureHelper.SetCurrentUICulture("de");
            html = _renderer.Render(new HomePage());
            sentence = string.Format(CultureInfo.CurrentCulture, Strings.HomePage_GraphSummary_LastMinute, 0, 0);
        }
        finally
        {
            CultureHelper.SetCurrentUICulture(previous);
        }

        Assert.NotEqual(string.Format(CultureInfo.InvariantCulture, "Last minute: {0} succeeded, {1} failed", 0, 0), sentence);
        Assert.Contains($"class=\"ns-graph-summary js-graph-summary\" data-format=\"", html);
        Assert.Contains($"\">{Encode(sentence)}</p>", html);
        Assert.Contains("&quot;Succeeded&quot;:&quot;Erfolgreich&quot;", html);
        Assert.Contains("&quot;Awaiting&quot;:", html);
    }

    [Fact]
    public void Render_GivesTheGraphsALegend_AndASummaryForScreenReaders()
    {
        _renderer.Monitoring.Setup(x => x.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long> { { Hour, 5 }, { Hour.AddHours(-1), 7 } });
        _renderer.Monitoring.Setup(x => x.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long> { { Hour, 2 } });

        string html = _renderer.Render(new HomePage());

        Assert.Equal(2, CountOf(html, "<ul class=\"ns-graph-legend\">"));
        Assert.Contains($"<li class=\"ns-state-succeeded\"><span class=\"ns-graph-swatch\" aria-hidden=\"true\"></span>{Encode(Strings.JobsSidebarMenu_Succeeded)}</li>", html);
        Assert.Contains($"<li class=\"ns-state-failed\"><span class=\"ns-graph-swatch\" aria-hidden=\"true\"></span>{Encode(Strings.JobsSidebarMenu_Failed)}</li>", html);
        // The realtime graph always has a deleted series; the history graph only when the storage graphs them
        Assert.Equal(1, CountOf(html, "<li class=\"ns-state-deleted\">"));
        Assert.Contains($"role=\"img\" aria-label=\"{Encode(Strings.HomePage_RealtimeGraph)}\" aria-describedby=\"home-realtime-summary\"", html);
        Assert.Contains($"<p id=\"home-realtime-summary\" class=\"ns-graph-summary js-graph-summary\" data-format=\"{Encode(Strings.HomePage_GraphSummary_LastMinute)}\">{Encode(string.Format(Strings.HomePage_GraphSummary_LastMinute, 0, 0))}</p>", html);
        Assert.Contains($"role=\"img\" aria-label=\"{Encode(Strings.HomePage_HistoryGraph)}\" aria-describedby=\"home-history-summary\"", html);
        Assert.Contains($"<p id=\"home-history-summary\" class=\"ns-graph-summary\">{Encode(string.Format(Strings.HomePage_GraphSummary_Day, 12, 2))}</p>", html);
    }

    [Fact]
    public void Render_SumsUpTheWeek_ForTheWeeksGraph()
    {
        _renderer.Monitoring.Setup(x => x.SucceededByDatesCount()).Returns(new Dictionary<DateTime, long> { { Hour, 40 } });
        _renderer.Monitoring.Setup(x => x.FailedByDatesCount()).Returns(new Dictionary<DateTime, long> { { Hour, 3 } });
        _renderer.Request.Query["period"] = "week";

        string html = _renderer.Render(new HomePage());

        Assert.Contains($"<p id=\"home-history-summary\" class=\"ns-graph-summary\">{Encode(string.Format(Strings.HomePage_GraphSummary_Week, 40, 3))}</p>", html);
    }

    [Fact]
    public void Render_HasAHeaderBand_WithTheClusterFacts_AndTheWallboardAsItsOneAction()
    {
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Servers = 2, Recurring = 5, Retries = 3 });

        string html = _renderer.Render(new HomePage());

        Assert.Contains("<header class=\"ns-page-head ns-home-head\">", html);
        Assert.Contains($"<h1 id=\"page-title\" class=\"hf-page-header\"><span class=\"ns-page-name\">{Encode(Strings.HomePage_Title)}</span></h1>", html);
        Assert.Contains($"href=\"/servers\">{DashboardMarkup.Icon("hdd-fill")} {Encode(Strings.Metrics_Servers)} <strong data-metric=\"servers:count\">2</strong></a>", html);
        Assert.Contains($"href=\"/recurring\">{DashboardMarkup.Icon("clock")} {Encode(Strings.Metrics_RecurringJobs)} <strong data-metric=\"recurring:count\">5</strong></a>", html);
        Assert.Contains($"href=\"/retries\">{DashboardMarkup.Icon("arrow-repeat")} {Encode(Strings.Metrics_Retries)} <strong data-metric=\"retries:count\">3</strong></a>", html);
        Assert.Equal(1, CountOf(html, "class=\"btn btn-sm ns-btn-tonal\" href=\"/wallboard\""));
        Assert.DoesNotContain("btn-primary", html);
        PageStructure.AssertHeadingsInOrder(html);
    }

    [Fact]
    public void Render_HasATilePerState_ThatLinksToItsList_AndIsKeptCurrentByThePoller()
    {
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Enqueued = 4, Queues = 2, Scheduled = 5, Processing = 6, Succeeded = 1234, Failed = 8, Deleted = 9, Awaiting = 10 });

        string html = _renderer.Render(new HomePage());

        Assert.Equal(7, CountOf(html, "class=\"ns-summary-tile ns-home-tile"));
        Assert.Contains("<a class=\"ns-summary-tile ns-home-tile ns-state-enqueued\" href=\"/jobs/enqueued\">", html);
        Assert.Contains("<span class=\"ns-summary-value\" data-metric=\"enqueued:count\">4</span>", html);
        Assert.Contains($"<span class=\"ns-summary-sub\">{Encode(string.Format(Strings.HomePage_Kpi_Queues, 2))}</span>", html);
        Assert.Contains("<a class=\"ns-summary-tile ns-home-tile ns-state-scheduled\" href=\"/jobs/scheduled\">", html);
        Assert.Contains("<a class=\"ns-summary-tile ns-home-tile ns-state-processing\" href=\"/jobs/processing\">", html);
        Assert.Contains("<a class=\"ns-summary-tile ns-home-tile ns-state-succeeded\" href=\"/jobs/succeeded\">", html);
        Assert.Contains($"data-metric=\"succeeded:count\">{1234L.ToString("N0", CultureInfo.CurrentCulture)}</span>", html);
        Assert.Contains("<a class=\"ns-summary-tile ns-home-tile ns-state-failed\" href=\"/jobs/failed\">", html);
        Assert.Contains($"{DashboardMarkup.Icon("x-circle-fill")} {Encode(Strings.JobsSidebarMenu_Failed)}</span>", html);
        Assert.Contains("<a class=\"ns-summary-tile ns-home-tile ns-state-deleted\" href=\"/jobs/deleted\">", html);
        Assert.Contains("<a class=\"ns-summary-tile ns-home-tile ns-state-awaiting\" href=\"/jobs/awaiting\">", html);
        Assert.Contains("data-metric=\"awaiting:count\">10</span>", html);
    }

    [Fact]
    public void Render_LeavesTheAwaitingTileOut_WhenTheStorageDoesNotCountAwaitingJobs()
    {
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Awaiting = null, Retries = null });

        string html = _renderer.Render(new HomePage());

        Assert.Equal(6, CountOf(html, "class=\"ns-summary-tile ns-home-tile"));
        Assert.DoesNotContain("ns-state-awaiting\" href=\"/jobs/awaiting\"", html);
        Assert.DoesNotContain("<strong data-metric=\"retries:count\">", html);
    }

    [Fact]
    public void EnqueuedCount_IsAMetricThePollerCanAskFor()
    {
        Assert.Contains(DashboardMetrics.GetMetrics(), x => x.Name == "enqueued:count");
    }

    [Fact]
    public void Render_Always_ShowsTheActivityFeedPlaceholder()
    {
        string html = _renderer.Render(new HomePage());

        Assert.Contains($"{Encode(Strings.HomePage_Activity)} <small class=\"text-muted\">{Encode(Strings.HomePage_Activity_Hint)}</small>", html);
        Assert.Contains($"data-url=\"/activity\" data-job-url=\"/jobs/details/\" data-empty=\"{Encode(Strings.HomePage_Activity_Empty)}\"", html);
        Assert.Contains($"<li class=\"hf-activity-empty text-muted\">{Encode(Strings.HomePage_Activity_Empty)}</li>", html);
    }

    [Fact]
    public void Render_ByDefault_ShowsTheHourlyHistory()
    {
        Dictionary<DateTime, long> succeeded = new() { { Hour, 5 } };
        Dictionary<DateTime, long> failed = new() { { Hour, 2 } };
        _renderer.Monitoring.Setup(x => x.HourlySucceededJobs()).Returns(succeeded);
        _renderer.Monitoring.Setup(x => x.HourlyFailedJobs()).Returns(failed);

        string html = _renderer.Render(new HomePage());

        Assert.Contains($"<a href=\"?period=day\" class=\"btn btn-sm btn-outline-secondary active\">{Encode(Strings.Common_PeriodDay)}</a>", html);
        Assert.Contains($"<a href=\"?period=week\" class=\"btn btn-sm btn-outline-secondary \">{Encode(Strings.Common_PeriodWeek)}</a>", html);
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
        Dictionary<DateTime, long> succeeded = new() { { Hour.Date, 50 } };
        _renderer.Monitoring.Setup(x => x.SucceededByDatesCount()).Returns(succeeded);
        _renderer.Request.Query["period"] = "WEEK";

        string html = _renderer.Render(new HomePage());

        Assert.Contains($"<a href=\"?period=day\" class=\"btn btn-sm btn-outline-secondary \">", html);
        Assert.Contains($"<a href=\"?period=week\" class=\"btn btn-sm btn-outline-secondary active\">", html);
        Assert.Contains($"data-succeeded=\"{Encode(JsonSerializer.Serialize(succeeded))}\"", html);
        Assert.Contains("data-period=\"WEEK\"", html);
        _renderer.Monitoring.Verify(x => x.HourlySucceededJobs(), Times.Never);
    }

    [Fact]
    public void Render_ForAnUnknownPeriod_LeavesTheHistoryGraphOut()
    {
        _renderer.Request.Query["period"] = "month";

        string html = _renderer.Render(new HomePage());

        Assert.Contains($"<h2 id=\"home-history\">{Encode(Strings.HomePage_HistoryGraph)}</h2>", html);
        Assert.DoesNotContain("home-history-summary", html);
        Assert.DoesNotContain("historyGraph", html);
        Assert.DoesNotContain("btn-outline-secondary active", html);
    }

    [Theory]
    [InlineData("day")]
    [InlineData("week")]
    public void Render_WhenTheStorageGraphsDeletedJobs_IncludesThem(string period)
    {
        Dictionary<DateTime, long> deleted = new() { { Hour, 7 } };
        Mock<JobStorageMonitor> monitor = new();
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

        string html = _renderer.Render(new HomePage());

        Assert.Contains($"data-deleted=\"{Encode(JsonSerializer.Serialize(deleted))}\"", html);
    }

    [Fact, GlobalLock(Reason = "Changes HomePage.Metrics")]
    public void Render_WithoutHomeMetrics_LeavesTheKpiGridOut()
    {
        List<DashboardMetric> saved = [.. HomePage.Metrics];
        HomePage.Metrics.Clear();

        try
        {
            string html = _renderer.Render(new HomePage());

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
        List<DashboardMetric> saved = [.. HomePage.Metrics];
        HomePage.Metrics.Clear();
        HomePage.Metrics.Add(DashboardMetrics.ServerCount);
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Servers = 0 });

        try
        {
            string html = _renderer.Render(new HomePage());

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

    private static int CountOf(string html, string value) => html.Split([value], StringSplitOptions.None).Length - 1;
}
