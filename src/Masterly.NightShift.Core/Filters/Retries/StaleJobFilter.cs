using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>
/// Handles jobs picked up again while they were processing, because their server died or stopped before they
/// finished. A default global filter; for a job that wasn't processing it costs a type check.
/// </summary>
/// <remarks>
/// <para>
/// Each such restart is counted in the <c>StaleRestarts</c> job parameter. With a limit set
/// (<see cref="MaxRestarts"/>, <c>UseStaleRestartLimit</c>), the restart past it fails the job for good, without a
/// retry, with the reason <c>Failed: stale: ...</c>, so a job that kills its server doesn't loop through the cluster.
/// </para>
/// <para>
/// The worker that picks the job up again records itself in the <c>Lease</c> job parameter. A worker's final state
/// change is refused while the lease names another worker, so a slow former owner that finishes late can't overwrite
/// the outcome of the run that replaced it. That check reads the parameters loaded with the job (SQL Server loads
/// them); a job that was never picked up again has no lease and costs nothing.
/// </para>
/// </remarks>
public sealed class StaleJobFilter : JobFilterAttribute, IElectStateFilter
{
    /// <summary>The job parameter that counts the restarts.</summary>
    public const string RestartsParameter = "StaleRestarts";

    /// <summary>The job parameter with the worker that owns a job picked up again.</summary>
    public const string LeaseParameter = "Lease";

    // The nightshift.job.outcome tag of nightshift.job.stale_restarts
    internal const string OutcomeRestarted = "restarted";
    internal const string OutcomeFailed = "failed";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(StaleJobFilter));

    /// <summary>Creates the filter.</summary>
    public StaleJobFilter()
    {
        // After the filters that hold a job instead of processing it, so only a real start is counted
        Order = 999;
    }

    /// <summary>
    /// How many times a job may be picked up again after its server stopped, or <c>null</c> (the default) for no
    /// limit. 0 fails a job the first time. A server that stops gracefully also puts its running jobs back, so a
    /// limit counts deployments too: set it above the number of restarts a long job may see.
    /// </summary>
    public static int? MaxRestarts
    {
        get;
        set => field = value is < 0 ? throw new ArgumentOutOfRangeException(nameof(value), value, "The limit can't be negative.") : value;
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateState is not ProcessingState processing) return;

        bool restart = ProcessingState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase);

        if (!restart)
        {
            // A job held and enqueued again after a restart takes the lease, so the old one doesn't refuse its
            // worker. Only from the loaded parameters: without them the lease is never checked either.
            if (context.BackgroundJob.ParametersSnapshot?.GetValueOrDefault(LeaseParameter) is { Length: > 0 })
            {
                Write(context, LeaseParameter, Serialize(processing.WorkerId));
            }

            return;
        }

        int restarts = (int.TryParse(Read(context, RestartsParameter), NumberStyles.Integer, CultureInfo.InvariantCulture, out int previous) ? previous : 0) + 1;

        // Counted before the limit is checked, so the job's parameters say how many times its server stopped
        // even when this restart failed it
        Write(context, RestartsParameter, restarts.ToString(CultureInfo.InvariantCulture));

        bool exceeded = MaxRestarts is { } max && restarts > max;
        CountRestart(context, exceeded);

        if (exceeded)
        {
            string reason = restarts == 1
                ? "its server stopped before it finished"
                : $"its server stopped {restarts} times before it finished";

            Logger.Warn($"Background job '{context.BackgroundJob.Id}' failed: {reason}, more than the {MaxRestarts} restarts allowed.");
            context.CandidateState = new FailedState(new TerminateJobException($"stale: {reason}", TerminateJobOutcome.Failed), processing.ServerId)
            {
                Reason = TerminateJobFilter.FormatReason("Failed", $"stale: {reason}"),
            };
            return;
        }

        Write(context, LeaseParameter, Serialize(processing.WorkerId));
    }

    // A restart past the limit fails the job instead of running it again, and is told apart by its outcome tag
    private static void CountRestart(ElectStateContext context, bool failed)
    {
        if (!NightShiftTelemetry.JobsStaleRestarted.Enabled)
        {
            return;
        }

        Job? job = context.BackgroundJob.Job;
        string? jobName = job != null ? $"{job.Type.Name}.{job.Method.Name}" : null;

        // Once the restart is committed: a state change that fails and is tried again would count it twice
        context.RunAfterCommit(() => NightShiftTelemetry.JobsStaleRestarted.Add(
            1,
            new KeyValuePair<string, object?>("nightshift.job.name", jobName),
            new KeyValuePair<string, object?>("nightshift.job.outcome", failed ? OutcomeFailed : OutcomeRestarted)));
    }

    /// <summary>
    /// Whether a worker may move a job out of Processing: refused when the job's lease, in its loaded parameters,
    /// names another worker.
    /// </summary>
    internal static bool IsLeaseHeldBy(IReadOnlyDictionary<string, string>? parameters, string workerId)
    {
        if (parameters == null || !parameters.TryGetValue(LeaseParameter, out string? lease) || string.IsNullOrEmpty(lease))
        {
            return true;
        }

        string? holder;

        try
        {
            holder = SerializationHelper.Deserialize<string>(lease, SerializationOption.Internal);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // This runs inside the state change, under the job's lock: an unreadable lease must not block the job
            // forever, so it counts as no lease
            Logger.WarnException($"The '{LeaseParameter}' job parameter can't be read, so it is ignored.", ex);
            return true;
        }

        return string.Equals(holder, workerId, StringComparison.Ordinal);
    }

    private static string Serialize(string value) => SerializationHelper.Serialize(value, SerializationOption.Internal);

    private static string? Read(ElectStateContext context, string name)
    {
        IReadOnlyDictionary<string, string>? snapshot = context.BackgroundJob.ParametersSnapshot;
        string? value = snapshot != null
            ? snapshot.GetValueOrDefault(name)
            : context.Connection.GetJobParameter(context.BackgroundJob.Id, name);

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void Write(ElectStateContext context, string name, string value)
    {
        if (context.Transaction is JobStorageTransaction transaction &&
            context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            transaction.SetJobParameter(context.BackgroundJob.Id, name, value);
        }
        else
        {
            context.Connection.SetJobParameter(context.BackgroundJob.Id, name, value);
        }
    }
}
