using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages;

/// <summary>
/// The content of the Failed list's side sheet (<c>/jobs/failed/details/{id}</c>): the job's latest failure, read
/// with one <see cref="IMonitoringApi.JobDetails"/> when the sheet opens, so the list doesn't format and send every
/// stack trace on each load and Live refresh. A fragment without the layout, loaded by <c>NightShift.sheet</c>.
/// </summary>
internal partial class FailedJobSheet(string jobId)
{
    public string JobId { get; } = jobId;

    /// <summary>The latest Failed state in the job's history (newest first), or <c>null</c> when it never failed.</summary>
    internal static StateHistoryDto? LatestFailure(JobDetailsDto? job)
    {
        return job?.History?.FirstOrDefault(static x => FailedState.StateName.Equals(x.StateName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the failure is the job's current state, so it can still be requeued or deleted from here.</summary>
    internal static bool IsCurrent(JobDetailsDto job, StateHistoryDto failure)
    {
        return job.History.Count > 0 && ReferenceEquals(job.History[0], failure);
    }

    /// <summary>A non-empty value of the state's data, or <c>null</c>.</summary>
    internal static string? DataValue(StateHistoryDto state, string name)
    {
        return state.Data != null && state.Data.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value) ? value : null;
    }
}
