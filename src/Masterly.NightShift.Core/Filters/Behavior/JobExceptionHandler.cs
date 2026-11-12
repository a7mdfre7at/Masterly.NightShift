using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Handles the exceptions of every background job in one place, for example to report them to an error tracker.
/// Register it with <c>UseJobExceptionHandler</c>, or with <c>AddNightShiftJobExceptionHandler</c> in a service
/// collection.
/// </summary>
/// <remarks>
/// <para>The handler is called synchronously on the worker thread, after the job ran and before its state changes:
/// the worker takes no other job and the job's state doesn't change until it returns, so it must be fast. Hand slow
/// work, such as a request to an error tracker, to a queue or a client that sends in the background. It is called
/// for every failed attempt, including the ones <see cref="AutomaticRetryAttribute"/> retries, so it can be called
/// several times for one job.</para>
/// <para>An exception the handler throws is logged and ignored: it never changes what happens to the job.</para>
/// </remarks>
public interface IJobExceptionHandler
{
    /// <summary>
    /// Called when an attempt of a job failed: the job method threw, or a server filter gave up before calling it.
    /// Not called for <see cref="RetryLaterException"/> or a job that ended as skipped.
    /// </summary>
    /// <param name="context">The job and the exception.</param>
    void OnJobException([NotNull] JobExceptionContext context);

    /// <summary>
    /// Called when a run of a job was canceled: a state change aborted it (it was deleted or requeued while it
    /// ran), the server shut down while it ran, or it ended with <see cref="TerminateJobOutcome.Canceled"/>,
    /// <c>[JobTimeout(OnTimeout = JobTimeoutAction.Cancel)]</c> included.
    /// </summary>
    /// <param name="context">The job and the exception that canceled it.</param>
    void OnJobCanceled([NotNull] JobExceptionContext context);
}

/// <summary>The job and the exception passed to an <see cref="IJobExceptionHandler"/>.</summary>
public sealed class JobExceptionContext
{
    internal JobExceptionContext(PerformedContext context, Exception exception)
    {
        PerformContext = context;
        Exception = exception;
    }

    /// <summary>Gets the context of the run: the job, its parameters, the storage connection and console.</summary>
    [NotNull]
    public PerformContext PerformContext { get; }

    /// <summary>Gets the job.</summary>
    [NotNull]
    public BackgroundJob BackgroundJob => PerformContext.BackgroundJob;

    /// <summary>
    /// Gets the exception: the one the job method threw, not the <see cref="JobPerformanceException"/> that wraps it
    /// on its way to the server filters.
    /// </summary>
    [NotNull]
    public Exception Exception { get; }

    /// <summary>Gets how many times the job has been retried before this attempt: 0 on its first run.</summary>
    public int RetryCount => PerformContext.RetryCount;
}

/// <summary>
/// The server filter that calls an <see cref="IJobExceptionHandler"/>. <c>UseJobExceptionHandler</c> adds one to
/// <see cref="GlobalJobFilters.Filters"/>; add one to a filter collection yourself to limit it to some jobs.
/// </summary>
/// <remarks>
/// It calls the handler synchronously on the worker thread, in <see cref="OnPerformed"/>, so a slow handler holds
/// the worker and delays the job's next state; see <see cref="IJobExceptionHandler"/>.
/// </remarks>
/// <param name="handler">The handler.</param>
public sealed class JobExceptionHandlerFilter([NotNull] IJobExceptionHandler handler) : IServerFilter
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobExceptionHandlerFilter));

    /// <summary>Gets the handler.</summary>
    [NotNull]
    public IJobExceptionHandler Handler { get; } = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Exception == null || context.ExceptionHandled) return;

        JobOutcome outcome = JobOutcomes.Classify(context);
        if (outcome is not (JobOutcome.Failed or JobOutcome.NotRun or JobOutcome.Canceled)) return;

        JobExceptionContext exceptionContext = new(context, JobOutcomes.Unwrap(context.Exception));

        try
        {
            if (outcome == JobOutcome.Canceled)
            {
                Handler.OnJobCanceled(exceptionContext);
            }
            else
            {
                Handler.OnJobException(exceptionContext);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException(
                $"The job exception handler '{Handler.GetType().Name}' threw while handling background job '{context.BackgroundJob.Id}'. The job isn't affected.",
                ex);
        }
    }
}

/// <summary>The handler <c>UseJobExceptionHandler</c> builds from delegates.</summary>
internal sealed class DelegateJobExceptionHandler(Action<JobExceptionContext> onException, Action<JobExceptionContext>? onCanceled) : IJobExceptionHandler
{
    private readonly Action<JobExceptionContext> _onException = onException;
    private readonly Action<JobExceptionContext>? _onCanceled = onCanceled;

    public void OnJobException(JobExceptionContext context) => _onException(context);

    public void OnJobCanceled(JobExceptionContext context) => _onCanceled?.Invoke(context);

    /// <summary>Whether both handlers call the same delegates, so registering the second adds nothing.</summary>
    public bool HasSameDelegates(DelegateJobExceptionHandler other) => _onException.Equals(other._onException) && Equals(_onCanceled, other._onCanceled);
}
