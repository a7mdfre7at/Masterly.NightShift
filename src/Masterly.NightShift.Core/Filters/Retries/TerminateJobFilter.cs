using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Ends a job that threw <see cref="TerminateJobException"/>: a skipped or canceled job is deleted with its reason
/// instead of failing. A job terminated as <see cref="TerminateJobOutcome.Failed"/> stays failed, and
/// <see cref="AutomaticRetryAttribute"/> doesn't retry it. Added to <see cref="GlobalJobFilters.Filters"/> by default.
/// </summary>
/// <remarks>
/// Its <see cref="JobFilterAttribute.Order"/> is lower than the one of <see cref="AutomaticRetryAttribute"/>, so
/// a skipped or canceled job is deleted before any retry is considered.
/// </remarks>
public sealed class TerminateJobFilter : JobFilterAttribute, IElectStateFilter
{
    /// <summary>The prefix of the deletion reason of a skipped job.</summary>
    public const string SkippedReasonPrefix = "Skipped";

    /// <summary>The prefix of the deletion reason of a canceled job.</summary>
    public const string CanceledReasonPrefix = "Canceled";

    // Reasons are cut on the dashboard and in storage at about 100 characters
    private const int MaxReasonLength = 100;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(TerminateJobFilter));

    /// <summary>Initializes the filter.</summary>
    public TerminateJobFilter()
    {
        Order = 5;
    }

    /// <summary>
    /// Finds the <see cref="TerminateJobException"/> a failed state carries, directly or wrapped in other exceptions.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The exception, or <c>null</c> when the state isn't failed or failed for another reason.</returns>
    [CanBeNull]
    public static TerminateJobException? Find([CanBeNull] IState? state)
    {
        return state is FailedState failed ? Find(failed.Exception) : null;
    }

    /// <summary>
    /// Finds a <see cref="TerminateJobException"/>, directly or wrapped in other exceptions: a
    /// <see cref="JobPerformanceException"/>, the <see cref="AggregateException"/> of a task the job waited for (with
    /// that one exception only), or an exception of the job's own that keeps it as its inner exception.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The exception, or <c>null</c>.</returns>
    [CanBeNull]
    public static TerminateJobException? Find([CanBeNull] Exception? exception)
    {
        foreach (Exception inner in JobOutcomes.InnerChain(exception))
        {
            if (inner is TerminateJobException terminate)
            {
                return terminate;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        TerminateJobException? terminate = Find(context.CandidateState);
        if (terminate == null || !terminate.DeletesJob)
        {
            return;
        }

        string prefix = terminate.Outcome == TerminateJobOutcome.Canceled ? CanceledReasonPrefix : SkippedReasonPrefix;

        context.CandidateState = new DeletedState
        {
            Reason = FormatReason(prefix, terminate.Message),
        };

        Logger.Info($"Background job '{context.BackgroundJob.Id}' ended as {terminate.Outcome}: {terminate.Message}");
    }

    internal static string FormatReason(string prefix, string message)
    {
        string reason = $"{prefix}: {message}";
        return reason.Length > MaxReasonLength ? string.Concat(reason.AsSpan(0, MaxReasonLength - 1), "…") : reason;
    }
}
