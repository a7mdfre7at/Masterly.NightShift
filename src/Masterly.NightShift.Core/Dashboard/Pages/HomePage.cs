using System.Globalization;
using System.Text;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class HomePage
{
    public static readonly List<DashboardMetric> Metrics = [];

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
