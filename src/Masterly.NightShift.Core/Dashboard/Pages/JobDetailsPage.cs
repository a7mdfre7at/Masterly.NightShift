using System.Globalization;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class JobDetailsPage(string jobId)
{
    private const string RecurringJobIdParameter = "RecurringJobId";

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

    /// <summary>
    /// The queue the header shows: the job's own queue, else the queue it was last enqueued to, or <c>null</c>
    /// for a job that was never enqueued.
    /// </summary>
    internal static string? GetQueue(JobDetailsDto details)
    {
        if (details.Job?.Queue != null)
        {
            return details.Job.Queue;
        }

        StateHistoryDto? enqueued = details.History.FirstOrDefault(static x => x.StateName == EnqueuedState.StateName);
        return enqueued?.Data != null && enqueued.Data.TryGetValue("Queue", out string? queue) && !string.IsNullOrEmpty(queue)
            ? queue
            : null;
    }

    /// <summary>The id of the recurring job that created the job, or <c>null</c>.</summary>
    internal static string? GetRecurringJobId(JobDetailsDto details)
    {
        if (!details.Properties.TryGetValue(RecurringJobIdParameter, out string? serialized))
        {
            return null;
        }

        try
        {
            return BatchJob.ParseParameter(serialized);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Written by another version or by hand: the header leaves the link out
            return null;
        }
    }

    /// <summary>
    /// What the summary tiles show: when the job was created, when it last started processing and on which server,
    /// how long that run took (or has taken so far) and how many times it was retried.
    /// </summary>
    /// <param name="details">The job, its history newest first.</param>
    /// <param name="now">The storage's time, for a run that hasn't ended.</param>
    internal static JobDetailsSummary Summarize(JobDetailsDto details, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(details);

        int retryCount = details.Properties.TryGetValue("RetryCount", out string? retries)
            && int.TryParse(retries, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;

        int processingIndex = -1;
        for (int i = 0; i < details.History.Count; i++)
        {
            if (details.History[i].StateName == ProcessingState.StateName)
            {
                processingIndex = i;
                break;
            }
        }

        if (processingIndex < 0)
        {
            return new JobDetailsSummary(details.CreatedAt, null, null, false, retryCount, null);
        }

        StateHistoryDto processing = details.History[processingIndex];
        IDictionary<string, string> data = processing.Data ?? new Dictionary<string, string>();

        // Older data names the server ServerName
        string? serverId = data.TryGetValue("ServerId", out string? id) ? id : data.TryGetValue("ServerName", out string? name) ? name : null;
        DateTime startedAt = (data.TryGetValue("StartedAt", out string? started) ? StateDataValues.ReadDateTime(started) : null) ?? processing.CreatedAt;

        TimeSpan? duration;
        bool running = processingIndex == 0;
        if (running)
        {
            duration = now - startedAt;
        }
        else
        {
            StateHistoryDto next = details.History[processingIndex - 1];
            TimeSpan? performance = next.StateName == SucceededState.StateName && next.Data != null && next.Data.TryGetValue("PerformanceDuration", out string? value)
                ? StateDataValues.ReadMilliseconds(value)
                : null;
            duration = performance ?? next.CreatedAt - startedAt;
        }

        return new JobDetailsSummary(details.CreatedAt, startedAt, duration >= TimeSpan.Zero ? duration : null, running, retryCount, serverId);
    }
}

/// <summary>What the summary tiles of the job page show (see <see cref="JobDetailsPage.Summarize"/>).</summary>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="StartedAt">When it last started processing, or <c>null</c> when it never did.</param>
/// <param name="Duration">How long that run took, or has taken so far; <c>null</c> when unknown.</param>
/// <param name="Running">Whether the run hasn't ended.</param>
/// <param name="RetryCount">How many times it was retried.</param>
/// <param name="ServerId">The server of the last run, or <c>null</c>.</param>
internal sealed record JobDetailsSummary(DateTime? CreatedAt, DateTime? StartedAt, TimeSpan? Duration, bool Running, int RetryCount, string? ServerId);
