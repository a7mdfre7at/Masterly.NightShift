using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class JobDetailsPage(string jobId)
{
    public string JobId { get; } = jobId;

    /// <summary>The job whose page is being rendered, so the history renderers can read its own queue.</summary>
    internal Job? RenderedJob { get; set; }

    /// <summary>
    /// The queue "Run again with changes" starts with: the job's own queue (<see cref="Job.Queue"/>), else the
    /// queue it was last enqueued to when that isn't <c>default</c>; empty for <c>default</c>.
    /// </summary>
    internal static string GetRunAgainQueue(JobDetailsDto details)
    {
        if (details?.Job?.Queue != null)
        {
            return details.Job.Queue;
        }

        StateHistoryDto? enqueued = details?.History?.FirstOrDefault(static x => x.StateName == EnqueuedState.StateName);
        if (enqueued?.Data != null && enqueued.Data.TryGetValue("Queue", out string? queue) &&
            !string.IsNullOrEmpty(queue) && !EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase))
        {
            return queue;
        }

        return string.Empty;
    }
}
