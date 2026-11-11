using System.Diagnostics;
using System.Runtime.CompilerServices;
using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Diagnostics;

/// <summary>
/// Emits traces and metrics for background jobs, see <see cref="NightShiftTelemetry"/>.
/// The trace context of the code that creates a job is stored in the job's parameters,
/// so the span of its execution joins the same trace.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class TelemetryFilter : JobFilterAttribute, IClientFilter, IServerFilter, IApplyStateFilter
{
    internal const string TraceParentParameter = "TraceParent";
    internal const string TraceStateParameter = "TraceState";

    internal const string OutcomeSucceeded = "succeeded";
    internal const string OutcomeFailed = "failed";
    internal const string OutcomeCanceled = "canceled";
    internal const string OutcomeRetryLater = "retry_later";
    internal const string OutcomeSkipped = "skipped";

    private const string CreateActivityKey = "Masterly.NightShift.Telemetry.CreateActivity";
    private const string ProcessActivityKey = "Masterly.NightShift.Telemetry.ProcessActivity";
    private const string ProcessStartedKey = "Masterly.NightShift.Telemetry.ProcessStarted";
    private const string ActiveCountedKey = "Masterly.NightShift.Telemetry.ActiveCounted";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(TelemetryFilter));

    // The queue a worker fetched a job from, noted when the job moves from Enqueued to Processing and read
    // when the worker performs it. The worker passes the same BackgroundJob instance from that state change
    // to the perform context, and the entry goes away with it.
    private static readonly ConditionalWeakTable<BackgroundJob, string> FetchedQueues = [];

    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? jobName = GetJobName(context.Job);
        Activity? activity = NightShiftTelemetry.ActivitySource.StartActivity($"create {jobName}", ActivityKind.Producer);

        if (activity != null)
        {
            context.Items[CreateActivityKey] = activity;

            if (activity.IsAllDataRequested)
            {
                SetCommonTags(activity, "create", context.Job, GetQueue(context.Job, context.InitialState));
                activity.SetTag("nightshift.job.state", context.InitialState?.Name);
            }
        }

        // Propagate even when nobody listens to Masterly.NightShift, so the job still joins the caller's trace
        Activity? traceContext = activity ?? Activity.Current;
        if (traceContext != null && traceContext.IdFormat == ActivityIdFormat.W3C)
        {
            context.SetJobParameter(TraceParentParameter, traceContext.Id!); // A started activity has an id

            if (!string.IsNullOrEmpty(traceContext.TraceStateString))
            {
                context.SetJobParameter(TraceStateParameter, traceContext.TraceStateString);
            }
        }
    }

    public void OnCreated(CreatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool failed = context.Exception != null && !context.ExceptionHandled;

        if (NightShiftTelemetry.JobsCreated.Enabled && !context.Canceled)
        {
            NightShiftTelemetry.JobsCreated.Add(
                1,
                Tag("nightshift.job.name", GetJobName(context.Job)),
                Tag("nightshift.job.state", context.InitialState?.Name),
                Tag("error.type", failed ? GetErrorType(context.Exception) : null));
        }

        if (context.Items.TryGetValue(CreateActivityKey, out object? value) && value is Activity activity)
        {
            if (activity.IsAllDataRequested)
            {
                // State election ran in between: [Queue] changes the initial Enqueued state in place
                activity.SetTag("messaging.destination.name", GetQueue(context.Job, context.InitialState));

                if (context.BackgroundJob != null)
                {
                    activity.SetTag("messaging.message.id", context.BackgroundJob.Id);
                }
            }

            if (failed)
            {
                SetError(activity, context.Exception!); // A failed run has its exception
            }

            activity.Stop();
        }
    }

    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Job? job = context.BackgroundJob.Job;
        string? jobName = GetJobName(job);

        if (NightShiftTelemetry.ActivitySource.HasListeners())
        {
            // Reading parameters may query the storage, so do it only when someone listens
            string? traceParent = context.GetJobParameter<string>(TraceParentParameter, allowStale: true);
            string? traceState = context.GetJobParameter<string>(TraceStateParameter, allowStale: true);

            ActivityContext.TryParse(traceParent, traceState, isRemote: true, out ActivityContext parentContext);

            Activity? activity = NightShiftTelemetry.ActivitySource.StartActivity(
                $"process {jobName}",
                ActivityKind.Consumer,
                parentContext);

            if (activity != null)
            {
                context.Items[ProcessActivityKey] = activity;

                if (activity.IsAllDataRequested)
                {
                    string? queue = FetchedQueues.TryGetValue(context.BackgroundJob, out string? fetched) ? fetched : job?.Queue;

                    SetCommonTags(activity, "process", job, queue);
                    activity.SetTag("messaging.message.id", context.BackgroundJob.Id);
                }
            }

            FetchedQueues.Remove(context.BackgroundJob);
        }

        if (NightShiftTelemetry.ActiveJobs.Enabled)
        {
            NightShiftTelemetry.ActiveJobs.Add(1, Tag("nightshift.job.name", jobName));

            // Decrement only what was incremented, even if a listener attaches in between
            context.Items[ActiveCountedKey] = true;
        }

        if (NightShiftTelemetry.JobDuration.Enabled)
        {
            context.Items[ProcessStartedKey] = Stopwatch.GetTimestamp();
        }

        // Only for jobs meant to run at a moment: a fire-and-forget job has no moment to be late for. Only from the
        // parameters loaded with the job, so it never queries the storage
        if (NightShiftTelemetry.JobLateness.Enabled && context.BackgroundJob.ParametersSnapshot != null && context.ScheduledFor.HasValue)
        {
            NightShiftTelemetry.JobLateness.Record(
                context.Lateness.TotalSeconds,
                Tag("nightshift.job.name", jobName),
                Tag("nightshift.recurring_job.id", context.RecurringJobId));
        }
    }

    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string outcome = GetOutcome(context);
        string? errorType = outcome == OutcomeFailed ? GetErrorType(context.Exception) : null;

        string? jobName = GetJobName(context.BackgroundJob.Job);

        if (context.Items.ContainsKey(ActiveCountedKey))
        {
            NightShiftTelemetry.ActiveJobs.Add(-1, Tag("nightshift.job.name", jobName));
        }

        if (NightShiftTelemetry.JobsCompleted.Enabled)
        {
            NightShiftTelemetry.JobsCompleted.Add(1, Tag("nightshift.job.name", jobName), Tag("nightshift.job.outcome", outcome), Tag("error.type", errorType));
        }

        if (context.Items.TryGetValue(ProcessStartedKey, out object? started) && started is long startedAt)
        {
            NightShiftTelemetry.JobDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                Tag("nightshift.job.name", jobName),
                Tag("nightshift.job.outcome", outcome),
                Tag("error.type", errorType));
        }

        if (context.Items.TryGetValue(ProcessActivityKey, out object? value) && value is Activity activity)
        {
            if (activity.IsAllDataRequested)
            {
                activity.SetTag("nightshift.job.outcome", outcome);
            }

            if (outcome == OutcomeFailed)
            {
                SetError(activity, context.Exception!); // A failed run has its exception
            }
            else if (outcome is OutcomeSkipped or OutcomeCanceled && activity.IsAllDataRequested)
            {
                // Why the run ended without doing its work, as an event on the span
                activity.AddEvent(new ActivityEvent($"nightshift.job.{outcome}", tags: new ActivityTagsCollection
                {
                    { "nightshift.job.reason", context.Exception != null ? JobOutcomes.Unwrap(context.Exception).Message : null },
                }));
            }

            activity.Stop();
        }
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        // AutomaticRetryAttribute turned the failure of an attempt into a retry
        if (NightShiftTelemetry.JobsRetried.Enabled &&
            context.NewState.Reason?.StartsWith("Retry attempt", StringComparison.OrdinalIgnoreCase) == true &&
            ProcessingState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase))
        {
            NightShiftTelemetry.JobsRetried.Add(1, Tag("nightshift.job.name", GetJobName(context.BackgroundJob.Job)));
        }

        if (context.NewState is not ProcessingState) return;
        if (!EnqueuedState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase)) return;

        // Reading the Enqueued state queries the storage, so do it only when someone listens
        if (!NightShiftTelemetry.ActivitySource.HasListeners()) return;

        string? queue = ReadFetchedQueue(context);
        if (queue != null)
        {
            FetchedQueues.AddOrUpdate(context.BackgroundJob, queue);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    internal static string GetOutcome(PerformedContext context)
    {
        return JobOutcomes.Classify(context) switch
        {
            JobOutcome.Canceled => OutcomeCanceled,
            JobOutcome.RetryLater => OutcomeRetryLater,
            JobOutcome.Skipped => OutcomeSkipped,
            // The worker records an attempt whose concurrency wait timed out as failed, so the metrics do too;
            // error.type (ConcurrencyLimitException, DistributedLockTimeoutException) tells it apart
            JobOutcome.NotRun or JobOutcome.Failed => OutcomeFailed,
            _ => OutcomeSucceeded,
        };
    }

    private static void SetCommonTags(Activity activity, string operation, Job? job, string? queue)
    {
        activity.SetTag("messaging.system", "nightshift");
        activity.SetTag("messaging.operation.name", operation);
        activity.SetTag("messaging.operation.type", operation);
        activity.SetTag("messaging.destination.name", queue);
        activity.SetTag("nightshift.job.name", GetJobName(job));

        if (job?.Type != null)
        {
            activity.SetTag("code.function.name", $"{job.Type.FullName}.{job.Method?.Name}");
        }
    }

    private static void SetError(Activity activity, Exception exception)
    {
        Exception error = Unwrap(exception);
        activity.SetStatus(ActivityStatusCode.Error, error.Message);

        if (!activity.IsAllDataRequested) return;

        activity.SetTag("error.type", error.GetType().FullName);
#if NET9_0_OR_GREATER
        activity.AddException(error);
#else
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", error.GetType().FullName },
            { "exception.message", error.Message },
            { "exception.stacktrace", error.ToString() }
        }));
#endif
    }

    private static string? GetErrorType(Exception? exception)
    {
        return exception != null ? Unwrap(exception).GetType().FullName : null;
    }

    private static Exception Unwrap(Exception exception)
    {
        // Job method exceptions reach server filters wrapped, the original one is more useful
        return JobOutcomes.Unwrap(exception);
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value)
    {
        return new KeyValuePair<string, object?>(key, value);
    }

    private static string? GetJobName(Job? job)
    {
        return job?.Type != null ? $"{job.Type.Name}.{job.Method?.Name}" : null;
    }

    private static string? GetQueue(Job job, IState? state)
    {
        return state is EnqueuedState enqueued ? GetEnqueuedQueue(job, enqueued.Queue) : job?.Queue;
    }

    private static string? ReadFetchedQueue(ApplyStateContext context)
    {
        try
        {
            // The transition isn't committed yet, so the current state is still the Enqueued one
            StateData? enqueued = context.Connection.GetStateData(context.BackgroundJob.Id);
            string? stateQueue = enqueued?.Data != null && enqueued.Data.TryGetValue("Queue", out string? value) ? value : null;

            return GetEnqueuedQueue(context.BackgroundJob.Job, stateQueue);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Telemetry must never break a state change; the span falls back to the job's own queue
            Logger.DebugException($"Could not read the queue of background job '{context.BackgroundJob.Id}' for telemetry.", ex);
            return null;
        }
    }

    private static string? GetEnqueuedQueue(Job? job, string? stateQueue)
    {
        // The same choice EnqueuedState's handler makes when it adds the job to a queue: the state's queue,
        // unless it's the default one and the job has its own
        if (string.IsNullOrEmpty(stateQueue)) return job?.Queue;

        return job?.Queue == null || !EnqueuedState.DefaultQueue.Equals(stateQueue, StringComparison.OrdinalIgnoreCase)
            ? stateQueue
            : job.Queue;
    }
}
