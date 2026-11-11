using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class RecurringJobDetailsPage(string id)
{
    private const int NextRunsShown = 10;
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(RecurringJobDetailsPage));

    public string Id { get; } = RouteValues.Decode(id);

    // The next runs as the scheduler will create them: a bad CRON expression or an unknown time zone shows none
    private IReadOnlyList<DateTime> ReadNextRuns(RecurringJobDto job)
    {
        if (job.Cron == null || job.Paused) return [];

        try
        {
            return Storage.GetNextOccurrences(Id, NextRunsShown);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The next runs of recurring job '{Id}' couldn't be computed.", ex);
            return [];
        }
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 1
            ? string.Create(CultureInfo.CurrentCulture, $"{duration.TotalMilliseconds:0} ms")
            : duration.TotalMinutes < 1
                ? string.Create(CultureInfo.CurrentCulture, $"{duration.TotalSeconds:0.#} s")
                : duration.TotalHours < 1
                    ? string.Create(CultureInfo.CurrentCulture, $"{duration.TotalMinutes:0.#} min")
                    : string.Create(CultureInfo.CurrentCulture, $"{duration.TotalHours:0.#} h");
    }
}
