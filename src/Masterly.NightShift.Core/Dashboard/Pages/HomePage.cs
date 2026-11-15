using System.Globalization;
using System.Text;
using System.Text.Json;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class HomePage
{
    public static readonly List<DashboardMetric> Metrics = [];

    // The state names keep their keys as they are: the activity feed's entries name the states that way
    private static readonly JsonSerializerOptions StateNamesOptions = DashboardJson.Create(camelCaseDictionaryKeys: false, ignoreNulls: true, utcDates: false, indented: false);

    /// <summary>
    /// The tiles of the home page, one per job state: the list it links to, the metric the poller updates it with,
    /// and its number from the statistics the page has read already. Awaiting only when the storage counts it.
    /// </summary>
    internal static IReadOnlyList<HomeTile> GetTiles(StatisticsDto statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        List<HomeTile> tiles =
        [
            new(EnqueuedState.StateName, "/jobs/enqueued", "enqueued:count", Strings.JobsSidebarMenu_Enqueued, statistics.Enqueued,
                string.Format(CultureInfo.CurrentCulture, Strings.HomePage_Kpi_Queues, statistics.Queues)),
            new(ScheduledState.StateName, "/jobs/scheduled", "scheduled:count", Strings.JobsSidebarMenu_Scheduled, statistics.Scheduled, null),
            new(ProcessingState.StateName, "/jobs/processing", "processing:count", Strings.JobsSidebarMenu_Processing, statistics.Processing, null),
            new(SucceededState.StateName, "/jobs/succeeded", "succeeded:count", Strings.JobsSidebarMenu_Succeeded, statistics.Succeeded, null),
            new(FailedState.StateName, "/jobs/failed", "failed:count", Strings.JobsSidebarMenu_Failed, statistics.Failed, null),
            new(DeletedState.StateName, "/jobs/deleted", "deleted:count", Strings.JobsSidebarMenu_Deleted, statistics.Deleted, null),
        ];

        if (statistics.Awaiting.HasValue)
        {
            tiles.Add(new(AwaitingState.StateName, "/jobs/awaiting", "awaiting:count", Strings.JobsSidebarMenu_Awaiting, statistics.Awaiting.Value, null));
        }

        return tiles;
    }

    /// <summary>
    /// The names of the built-in states in the reader's language, by state, as JSON: the live activity's state chips
    /// and the watchlist of the layout show them, and keep the state's own name in <c>data-state</c> and its classes.
    /// </summary>
    internal static string StateNamesJson()
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (string state in (string[])[EnqueuedState.StateName, ScheduledState.StateName, ProcessingState.StateName,
                     SucceededState.StateName, FailedState.StateName, DeletedState.StateName, AwaitingState.StateName])
        {
            names[state] = HtmlHelper.StateDisplayName(state);
        }

        return JsonSerializer.Serialize(names, StateNamesOptions);
    }

    /// <summary>
    /// The sentence under the realtime graph before its first poll: the graph starts empty, so nothing yet in the last
    /// minute (the script sums up the points it shows from then on).
    /// </summary>
    internal static string RealtimeSummary()
    {
        return string.Format(CultureInfo.CurrentCulture, Strings.HomePage_GraphSummary_LastMinute, 0, 0);
    }

    /// <summary>The total of a graph's series.</summary>
    internal static long Sum(IDictionary<DateTime, long> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.Values.Sum();
    }

    /// <summary>The legend of a graph: a swatch in each series' state colour and its name.</summary>
    private NonEscapedString GraphLegend(bool deleted)
    {
        StringBuilder html = new("<ul class=\"ns-graph-legend\">");
        AppendLegendItem(html, "succeeded", Strings.JobsSidebarMenu_Succeeded);
        AppendLegendItem(html, "failed", Strings.JobsSidebarMenu_Failed);
        if (deleted)
        {
            AppendLegendItem(html, "deleted", Strings.JobsSidebarMenu_Deleted);
        }

        return new NonEscapedString(html.Append("</ul>").ToString());
    }

    private void AppendLegendItem(StringBuilder html, string group, string label)
    {
        html.Append("<li class=\"ns-state-").Append(group).Append("\"><span class=\"ns-graph-swatch\" aria-hidden=\"true\"></span>")
            .Append(Html.HtmlEncode(label)).Append("</li>");
    }
}

/// <summary>A tile of the home page: a job state, the list it links to and the metric that keeps its number current.</summary>
internal sealed record HomeTile(string StateName, string Path, string Metric, string Label, long Value, string? Sub)
{
    /// <summary>The colour group of the tile's state, which colours its edge.</summary>
    public string? Group => JobHistoryRenderer.GetStateColorGroup(StateName);
}
