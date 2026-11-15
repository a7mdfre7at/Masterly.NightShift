using System.Globalization;
using System.Net;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class ForecastPageFacts
{
    private readonly PageRenderer _renderer = new();
    private readonly HashSet<string> _recurringJobIds = [];

    public ForecastPageFacts()
    {
        _renderer.Connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(_recurringJobIds);
    }

    [Fact]
    public void Render_SaysThereAreNoJobs_WhenNothingIsScheduled()
    {
        string html = _renderer.Render(new ForecastPage());

        Assert.Contains(H(Strings.RecurringJobsPage_NoJobs), html);
        Assert.DoesNotContain("hf-forecast-row", html);
        Assert.Contains("href=\"?window=24h\" class=\"btn btn-sm btn-outline-secondary active\"", html);
        Assert.Contains("href=\"?window=7d\" class=\"btn btn-sm btn-outline-secondary \"", html);
        Assert.Contains("href=\"/recurring\"", html);
    }

    [Fact]
    public void Render_ShowsThirteenTimeTicks_ForTheDefaultDayWindow()
    {
        AddRecurringJob("hourly", "0 * * * *", Job.FromExpression(() => SampleJobs.Unknown()));

        string html = _renderer.Render(new ForecastPage());

        Assert.Equal(13, CountOf(html, "class=\"hf-forecast-tick\""));
        Assert.Contains("style=\"left: 100.00%\"", html);
        Assert.DoesNotContain("data-moment-local-format=\"shortDate\"", html);
    }

    [Fact]
    public void Render_ShowsEightDateTicks_ForTheWeekWindow()
    {
        _renderer.Request.Query["window"] = "7D";
        AddRecurringJob("hourly", "0 * * * *", Job.FromExpression(() => SampleJobs.Unknown()));

        string html = _renderer.Render(new ForecastPage());

        Assert.Contains("href=\"?window=7d\" class=\"btn btn-sm btn-outline-secondary active\"", html);
        Assert.Equal(8, CountOf(html, "class=\"hf-forecast-tick\""));
        Assert.Equal(8, CountOf(html, "data-moment-local-format=\"shortDate\""));
        Assert.Contains("data-moment-local-format=\"shortDateTime\"", html);
    }

    [Fact]
    public void Render_DrawsEachRun_AndMarksCollidingRunsAsHotSpots()
    {
        AddRecurringJob("alpha", "0 * * * *", Job.FromExpression(() => SampleJobs.Unknown()));
        AddRecurringJob("beta", "0 * * * *", Job.FromExpression(() => SampleJobs.Unknown()));

        string html = _renderer.Render(new ForecastPage());

        Assert.Contains("<a href=\"/recurring?edit=alpha\">alpha</a>", html);
        Assert.Contains("<a href=\"/recurring?edit=beta\">beta</a>", html);
        Assert.Contains("insights-tile insights-tile-danger", html);
        Assert.Contains("id=\"forecast-hotspots\"", html);
        Assert.Equal(10, CountOf(html, "<span class=\"badge text-bg-danger\">2</span>"));
        Assert.Contains("<a class=\"hf-chip\" href=\"/recurring?edit=alpha\">alpha</a>", html);
        Assert.Contains("hf-forecast-run hf-forecast-run-hot hf-forecast-run-estimated", html);
        Assert.Contains("UTC &middot; ", html);
        Assert.Contains("&times; &middot; " + Duration(ScheduleForecast.DefaultDuration) + "*", html);
        Assert.Contains("<div class=\"insights-tile-value\"> <span data-moment-local=", html);
    }

    [Fact]
    public void Render_ShowsTheKnownDuration_WithoutTheEstimateMark()
    {
        Job job = Job.FromExpression(() => SampleJobs.Known());
        AddRecurringJob("known", "0 */6 * * *", job);
        AddInsight(job, 60000);

        string html = _renderer.Render(new ForecastPage());

        Assert.Contains("hf-forecast-run  \"", html);
        Assert.DoesNotContain("hf-forecast-run-estimated\"", html);
        Assert.DoesNotContain("insights-tile-danger", html);
        Assert.DoesNotContain("id=\"forecast-hotspots\"", html);
        Assert.Contains("&times; &middot; " + Duration(TimeSpan.FromMinutes(1)), html);
        Assert.DoesNotContain(Duration(TimeSpan.FromMinutes(1)) + "*", html);
    }

    [Fact]
    public void Render_DrawsOneDenseBar_ForAJobThatRunsTooOftenToDrawEachRun()
    {
        AddRecurringJob("every-minute", "* * * * *", Job.FromExpression(() => SampleJobs.Unknown()));

        string html = _renderer.Render(new ForecastPage());

        Assert.Contains("hf-forecast-run hf-forecast-run-dense hf-forecast-run-estimated", html);
        Assert.Contains("title=\"" + H(string.Format(Strings.ForecastPage_Dense, 1440)) + "\"", html);
        Assert.DoesNotContain("id=\"forecast-hotspots\"", html);
    }

    [Fact]
    public void Render_ShowsATruncatedJob_AsAtLeastTheCap_WithABandToTheWindowsEnd()
    {
        _renderer.Request.Query["window"] = "7d";
        AddRecurringJob("every-minute", "* * * * *", Job.FromExpression(() => SampleJobs.Unknown()));

        string html = _renderer.Render(new ForecastPage());

        string title = H(string.Format(Strings.ForecastPage_Truncated, ScheduleForecast.MaxOccurrencesPerJob));
        Assert.Contains("&ge;2000&times;", html);
        Assert.Contains("title=\"" + title + "\"", html);
        // The band starts at the first run (within a minute) and reaches the window's end; when the first run is
        // less than about 3 seconds away, the width rounds to 100.000%
        Assert.Matches("hf-forecast-run-dense[^>]*style=\"left: 0\\.0[0-9]{2}%; width: (99\\.9[0-9]{2}|100\\.000)%\"", html);
    }

    [Fact]
    public void Render_ShowsPausedJobs_WithoutRuns()
    {
        AddRecurringJob("paused", "0 * * * *", Job.FromExpression(() => SampleJobs.Unknown()), new Dictionary<string, string> { { "Paused", "1" } });

        string html = _renderer.Render(new ForecastPage());

        Assert.Contains("hf-forecast-row hf-forecast-row-paused", html);
        Assert.Contains("<span class=\"badge text-bg-warning\">" + H(Strings.ForecastPage_Paused) + "</span>", html);
        Assert.DoesNotContain("UTC &middot;", html);
        Assert.Contains("<div class=\"insights-tile-value\">&ndash;</div>", html);
    }

    [Fact]
    public void Render_ShowsTheError_OfAJobWithABadCronExpression()
    {
        AddRecurringJob("broken", "not a cron", Job.FromExpression(() => SampleJobs.Unknown()));

        string html = _renderer.Render(new ForecastPage());

        Assert.Contains("hf-forecast-error", html);
        Assert.Contains("<div class=\"hf-forecast-label\" title=\"not a cron\">", html);
    }

    private void AddRecurringJob(string id, string cron, Job job, Dictionary<string, string> extra = null)
    {
        Dictionary<string, string> hash = new()
        {
            { "Cron", cron },
            { "Job", InvocationData.SerializeJob(job).SerializePayload() },

            // The scheduler has a next execution for every job it runs
            { "NextExecution", JobHelper.SerializeDateTime(DateTime.UtcNow) },
        };

        foreach (KeyValuePair<string, string> pair in extra ?? [])
        {
            hash[pair.Key] = pair.Value;
        }

        _recurringJobIds.Add(id);
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{id}")).Returns(hash);
    }

    private void AddInsight(Job job, long durationMs)
    {
        string key = JobInsightsAttribute.GetKey(job);
        string duration = durationMs.ToString(CultureInfo.InvariantCulture);

        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"insights:{key}")).Returns(new Dictionary<string, string> { { "LastDurationMs", duration } });
        _renderer.Connection.Setup(x => x.GetAllItemsFromList($"insights:{key}:durations")).Returns([duration, duration, duration]);
    }

    private static string Duration(TimeSpan duration)
    {
        ForecastPage page = new();
        page.Assign(new DashboardContextStub(new DashboardOptions()));
        return new HtmlHelper(page).ToHumanDuration(duration, false);
    }

    private static int CountOf(string html, string fragment)
    {
        return (html.Length - html.Replace(fragment, string.Empty).Length) / fragment.Length;
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);

    public static class SampleJobs
    {
        public static void Known()
        {
        }

        public static void Unknown()
        {
        }
    }
}
