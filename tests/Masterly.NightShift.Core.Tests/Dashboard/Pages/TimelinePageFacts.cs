using System.Net;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class TimelinePageFacts
{
    private readonly PageRenderer _renderer = new();
    private readonly long _nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

    [Fact]
    public void Render_WithoutActivity_ShowsAnEmptyStateThatSaysWhatToDoNext()
    {
        string html = _renderer.Render(new TimelinePage());

        Assert.Contains("<header class=\"ns-page-head\">", html);
        Assert.Contains($"<span class=\"ns-page-name\">{Encode(Strings.TimelinePage_Title)}</span></h1>", html);
        Assert.Contains($"<p class=\"ns-page-subtitle\">{Encode(Strings.TimelinePage_Hint)}</p>", html);
        Assert.Contains($"<h2 class=\"ns-empty-state-title\">{Encode(Strings.TimelinePage_Empty_Title)}</h2><p>{Encode(Strings.TimelinePage_Empty)}</p>", html);
        Assert.Contains($"href=\"/catalog\">{Encode(Strings.NavigationMenu_Catalog)}</a>", html);
        Assert.DoesNotContain("hf-tl-track", html);
        Assert.DoesNotContain("ns-run-list", html);
        PageStructure.AssertHeadingsInOrder(html);
        _renderer.Connection.Verify(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, ActivityFeed.MaxEntries - 1));
    }

    [Fact]
    public void Render_WithRuns_ShowsTheSummaryLegendAndTimeAxis()
    {
        SetupActivity(
            Entry(-60000, "1", ProcessingState.StateName, "srv-1:a", "worker-aaaa-1111"),
            Entry(-50000, "1", SucceededState.StateName));

        string html = _renderer.Render(new TimelinePage());

        Assert.Contains("<h2 id=\"timeline-workers\">" + Encode(Strings.ServersPage_Table_Workers) + "</h2>", html);
        Assert.Contains("<span class=\"ns-section-hint\">" + Encode(string.Format(Strings.TimelinePage_Summary, 1, 1, 1, "")).Split(' ')[0], html);
        Assert.Contains("<i class=\"hf-tl-swatch hf-tl-succeeded\" aria-hidden=\"true\"></i> " + Encode(Strings.JobsSidebarMenu_Succeeded), html);
        Assert.Contains("<i class=\"hf-tl-swatch hf-tl-held\" aria-hidden=\"true\"></i> " + Encode(Strings.TimelinePage_Legend_Held), html);
        Assert.Contains("<i class=\"hf-tl-swatch hf-tl-running\" aria-hidden=\"true\"></i> " + Encode(Strings.TimelinePage_Running), html);
        Assert.Contains("<div class=\"hf-card hf-tl\" role=\"group\" aria-labelledby=\"timeline-workers\">", html);
        Assert.Contains("class=\"hf-tl-tick hf-tl-tick-first\" style=\"left: 0%\"", html);
        Assert.Contains("class=\"hf-tl-tick \" style=\"left: 50%\"", html);
        Assert.Contains("class=\"hf-tl-tick hf-tl-tick-last\" style=\"left: 100%\"", html);
        Assert.Contains(" UTC</span>", html);
        Assert.DoesNotContain(Encode(Strings.TimelinePage_Empty), html);
    }

    [Fact]
    public void Render_WithRuns_DrawsOneLanePerWorkerGroupedByServer()
    {
        SetupActivity(
            Entry(-60000, "1", ProcessingState.StateName, "srv-1:a", "worker-aaaa-1111"),
            Entry(-50000, "1", SucceededState.StateName),
            Entry(-45000, "2", ProcessingState.StateName, "srv-1:a", "worker-bbbb-2222"),
            Entry(-40000, "2", FailedState.StateName),
            Entry(-30000, "3", ProcessingState.StateName, "srv-2:b", "w3"),
            Entry(-20000, "3", DeletedState.StateName));

        string html = _renderer.Render(new TimelinePage());

        Assert.Equal(2, CountOf(html, "<div class=\"hf-tl-server\">"));
        Assert.Contains("title=\"srv-1:a\">srv-1</span>", html);
        Assert.Contains("<div class=\"hf-tl-label\" title=\"worker-aaaa-1111\"><code>worker-a</code></div>", html);
        Assert.Contains("<div class=\"hf-tl-label\" title=\"w3\"><code>w3</code></div>", html);
    }

    [Fact]
    public void Render_WithRuns_ColorsEachSpanByItsOutcome()
    {
        SetupActivity(
            Entry(-60000, "1", ProcessingState.StateName, "srv", "w1", "Billing.Charge"),
            Entry(-50000, "1", SucceededState.StateName),
            Entry(-45000, "2", ProcessingState.StateName, "srv", "w2"),
            Entry(-40000, "2", FailedState.StateName),
            Entry(-35000, "3", ProcessingState.StateName, "srv", "w3"),
            Entry(-30000, "3", DeletedState.StateName),
            Entry(-25000, "4", ProcessingState.StateName, "srv", "w4"),
            Entry(-20000, "4", ScheduledState.StateName),
            Entry(-10000, "5", ProcessingState.StateName, "srv", "w5"));

        string html = _renderer.Render(new TimelinePage());

        Assert.Contains("<a class=\"hf-tl-span hf-tl-succeeded\" href=\"/jobs/details/1\" style=\"left: 0%;", html);
        Assert.Contains("class=\"hf-tl-span hf-tl-failed\" href=\"/jobs/details/2\"", html);
        Assert.Contains("class=\"hf-tl-span hf-tl-deleted\" href=\"/jobs/details/3\"", html);
        Assert.Contains("class=\"hf-tl-span hf-tl-held\" href=\"/jobs/details/4\"", html);
        Assert.Contains("class=\"hf-tl-span hf-tl-running\" href=\"/jobs/details/5\"", html);
        Assert.Contains("title=\"" + Encode("Billing.Charge · Succeeded · "), html);
        Assert.Contains("title=\"" + Encode("4 · Scheduled · "), html);
        Assert.Contains("title=\"" + Encode($"5 · {Strings.TimelinePage_Running} · "), html);
        // A span has no text: its accessible name is the same as its tooltip
        Assert.Equal(5, Regex.Matches(html, "<a class=\"hf-tl-span [^\"]+\" href=\"[^\"]+\" style=\"[^\"]+\" title=\"([^\"]+)\" aria-label=\"\\1\"></a>").Count);
    }

    [Fact]
    public void Render_ListsTheRecentRuns_NewestFirst_WithTheirStateWorkerAndTimes()
    {
        SetupActivity(
            Entry(-60000, "1", ProcessingState.StateName, "srv-1:a", "worker-aaaa-1111", "Billing.Charge"),
            Entry(-50000, "1", SucceededState.StateName),
            Entry(-45000, "2", ProcessingState.StateName, "srv-1:a", "worker-bbbb-2222"),
            Entry(-40000, "2", FailedState.StateName),
            Entry(-10000, "3", ProcessingState.StateName, "srv-1:a", "worker-aaaa-1111"));

        string html = _renderer.Render(new TimelinePage());

        Assert.Contains("<h2 id=\"timeline-runs\">" + Encode(Strings.TimelinePage_RecentRuns) + "</h2>", html);
        Assert.Equal(3, CountOf(html, "<li class=\"ns-run\">"));
        int running = html.IndexOf("<a class=\"job-method\" href=\"/jobs/details/3\">#3</a>", StringComparison.Ordinal);
        int failed = html.IndexOf("<a class=\"job-method\" href=\"/jobs/details/2\">#2</a>", StringComparison.Ordinal);
        int succeeded = html.IndexOf("<a class=\"job-method\" href=\"/jobs/details/1\">Billing.Charge</a>", StringComparison.Ordinal);
        Assert.True(running > 0 && running < failed && failed < succeeded, $"{running} {failed} {succeeded}");
        Assert.Contains("<span class=\"ns-dot ns-state-processing\" aria-hidden=\"true\"></span>", html);
        Assert.Contains("<span class=\"ns-dot ns-state-failed\" aria-hidden=\"true\"></span>", html);
        Assert.Contains("<span>" + Encode(Strings.TimelinePage_Running) + "</span>", html);
        Assert.Contains("<code title=\"worker-aaaa-1111\">worker-a</code>", html);
        Assert.Contains("<div class=\"ns-run-time\"><span data-moment=", html);
        Assert.Contains("<span class=\"ns-time-exact\"><span data-moment-local=", html);
        PageStructure.AssertHeadingsInOrder(html);
    }

    [Fact]
    public void Render_WithRunsOlderThanTheWindow_ClipsThemToTheWindow()
    {
        SetupActivity(
            Entry(-(long)TimeSpan.FromHours(1).TotalMilliseconds, "1", ProcessingState.StateName, "srv", "w1"),
            Entry(-60000, "1", SucceededState.StateName));

        string html = _renderer.Render(new TimelinePage());

        Assert.Contains("class=\"hf-tl-span hf-tl-succeeded\" href=\"/jobs/details/1\" style=\"left: 0%; width: ", html);
    }

    private void SetupActivity(params ActivityEntry[] chronological)
    {
        List<string> newestFirst = [.. Enumerable.Reverse(chronological).Select(x => SerializationHelper.Serialize(x, SerializationOption.User))];
        _renderer.Connection.Setup(x => x.GetRangeFromList(ActivityFeed.ListKey, It.IsAny<int>(), It.IsAny<int>())).Returns(newestFirst);
    }

    private ActivityEntry Entry(long offsetMs, string jobId, string state, string serverId = null, string workerId = null, string method = null)
    {
        return new ActivityEntry { Timestamp = _nowMs + offsetMs, JobId = jobId, State = state, ServerId = serverId, WorkerId = workerId, Method = method };
    }

    private static int CountOf(string html, string value) => html.Split([value], StringSplitOptions.None).Length - 1;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
