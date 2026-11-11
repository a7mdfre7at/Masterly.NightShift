using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>What <see cref="JobTimeoutAttribute"/> does with a job that ran out of time.</summary>
public enum JobTimeoutAction
{
    /// <summary>
    /// The job fails with a <see cref="TimeoutException"/>, and <see cref="AutomaticRetryAttribute"/> retries it like
    /// any other failure. The default.
    /// </summary>
    Retry = 0,

    /// <summary>The job fails with the timeout as its exception, without retries.</summary>
    Fail = 1,

    /// <summary>
    /// The job is deleted with the reason <c>Canceled: Timed out after ...</c>, without retries, as
    /// <see cref="TerminateJobOutcome.Canceled"/> does.
    /// </summary>
    Cancel = 2,
}

/// <summary>
/// Cancels a job that runs longer than the given time and fails it with a <see cref="TimeoutException"/>, or, as
/// <see cref="OnTimeout"/> says, fails it without retries or deletes it as canceled.
/// </summary>
/// <remarks>
/// <para>The timeout is delivered through the job method's <see cref="CancellationToken"/> or
/// <see cref="IJobCancellationToken"/> parameter, so the method has to accept one and observe it. A method
/// without such a parameter can't be interrupted; it runs to completion and succeeds, but the fact that it
/// exceeded the timeout is recorded in the <c>TimeoutExceeded</c> job parameter.</para>
/// <para>This is an interceptor (see <see cref="IJobInterceptor"/>), so it runs inside the performer and
/// doesn't need a storage round trip.</para>
/// <para>Two timeouts on one job (one on the class and one on the method, say) both apply, nested: the token the
/// method gets is canceled by whichever expires first, and the <see cref="TimeoutException"/> names that one.</para>
/// </remarks>
public sealed class JobTimeoutAttribute : AsyncJobInterceptorAttribute
{
    /// <summary>Name of the job parameter set when a method without a cancellation parameter exceeds the timeout.</summary>
    public const string TimeoutExceededParameter = "TimeoutExceeded";

    /// <summary>
    /// The longest timeout, in seconds: 2,147,483 seconds (about 24.8 days). A timer accepts at most
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public const int MaxTimeoutInSeconds = int.MaxValue / 1000;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobTimeoutAttribute));

    // PerformContext.Items entry with the shortest timeout already recorded in TimeoutExceededParameter
    private const string ExceededItem = "X_NS_TimeoutExceeded";

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="timeoutInSeconds">
    /// Maximum run time of the job, in seconds: from 1 to <see cref="MaxTimeoutInSeconds"/> (about 24.8 days).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is zero, negative, or above <see cref="MaxTimeoutInSeconds"/>.</exception>
    public JobTimeoutAttribute(int timeoutInSeconds)
    {
        if (timeoutInSeconds is <= 0 or > MaxTimeoutInSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutInSeconds),
                timeoutInSeconds,
                $"Timeout should be between 1 and {MaxTimeoutInSeconds} seconds (about 24.8 days), the longest a timer accepts.");
        }

        Timeout = TimeSpan.FromSeconds(timeoutInSeconds);
    }

    /// <summary>Gets the maximum run time.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Gets or sets what happens to a job that ran out of time: retried as a failure (the default), failed without
    /// retries, or deleted as canceled.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't a defined <see cref="JobTimeoutAction"/>.</exception>
    public JobTimeoutAction OnTimeout
    {
        get;
        set => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown timeout action.");
    }

    /// <inheritdoc />
    protected override async Task<object?> InterceptAsync(JobInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        CancellationToken shutdownToken = invocation.Context.CancellationToken.ShutdownToken;

        // The timer is this attribute's own, so it knows whether it timed out itself or an outer one did. The
        // token the method gets is linked to the tokens already in place (those of an outer JobTimeout, for
        // example), so every timeout applies and the shortest wins.
        using CancellationTokenSource timer = new();
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(GetTokensToLink(invocation, shutdownToken, timer.Token));
        object?[]? originalArguments = ReplaceTokens(invocation, timeoutSource.Token);
        DateTime startedAt = JobClock.UtcNow;

        timer.CancelAfter(Timeout);

        try
        {
            object? result = await invocation.ProceedAsync().ConfigureAwait(false);

            if (originalArguments == null && JobClock.UtcNow - startedAt > Timeout)
            {
                RecordExceeded(invocation.Context);
            }

            return result;
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !shutdownToken.IsCancellationRequested && !invocation.Context.CancellationToken.IsAborted())
        {
            // Kept short: retry reasons are cut to about 100 characters on the dashboard and in storage
            TimeoutException timeout = new($"Timed out after {FormatTimeout()} (JobTimeout)");

            throw OnTimeout switch
            {
                JobTimeoutAction.Fail => new TerminateJobException(timeout.Message, TerminateJobOutcome.Failed, timeout),
                JobTimeoutAction.Cancel => new TerminateJobException(timeout.Message, TerminateJobOutcome.Canceled, timeout),
                _ => timeout,
            };
        }
        finally
        {
            // The arguments array is shared by every call of the chain, so an outer interceptor that calls
            // again (RetryInProcess) would otherwise get this attempt's token, already canceled on a timeout
            if (originalArguments != null)
            {
                RestoreTokens(invocation, originalArguments);
            }
        }
    }

    // A method without a cancellation parameter ran too long. With nested timeouts the inner one records first;
    // a longer outer one doesn't overwrite a shorter one already recorded. Only a record: the job succeeded, and a
    // storage error doesn't change that.
    private void RecordExceeded(PerformContext context)
    {
        lock (context.Items)
        {
            if (context.Items.TryGetValue(ExceededItem, out object? recorded) && recorded is TimeSpan shorter && shorter <= Timeout) return;
            context.Items[ExceededItem] = Timeout;
        }

        try
        {
            context.SetJobParameter(TimeoutExceededParameter, Timeout);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException(
                $"Background job '{context.BackgroundJob.Id}' ran longer than its timeout of {FormatTimeout()}, but the '{TimeoutExceededParameter}' parameter could not be set.",
                ex);
        }
    }

    // The server's token, this attribute's timer, and every token already passed in place of a cancellation
    // parameter, each once.
    private static CancellationToken[] GetTokensToLink(JobInvocation invocation, CancellationToken shutdownToken, CancellationToken timerToken)
    {
        List<CancellationToken> tokens = [shutdownToken, timerToken];
        Type[] parameterTypes = invocation.Invoker.ParameterTypes;

        for (int i = 0; i < parameterTypes.Length; i++)
        {
            CancellationToken token;

            if (parameterTypes[i] == typeof(CancellationToken) && invocation.Arguments[i] is CancellationToken argument)
            {
                token = argument;
            }
            else if (parameterTypes[i] == typeof(IJobCancellationToken) && invocation.Arguments[i] is IJobCancellationToken jobToken)
            {
                token = jobToken.ShutdownToken;
            }
            else
            {
                continue;
            }

            if (token.CanBeCanceled && !tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return [.. tokens];
    }

    private string FormatTimeout()
    {
        if (Timeout.TotalSeconds < 60) return $"{Timeout.TotalSeconds:0} seconds";
        if (Timeout.TotalMinutes < 60) return $"{Timeout.TotalMinutes:0.#} minutes";
        return $"{Timeout.TotalHours:0.#} hours";
    }

    // Returns a copy of the arguments taken before the first replacement, or null when nothing was replaced
    private static object?[]? ReplaceTokens(JobInvocation invocation, CancellationToken token)
    {
        object?[]? originalArguments = null;
        Type[] parameterTypes = invocation.Invoker.ParameterTypes;

        for (int i = 0; i < parameterTypes.Length; i++)
        {
            if (parameterTypes[i] == typeof(CancellationToken))
            {
                originalArguments ??= (object?[])invocation.Arguments.Clone();
                invocation.Arguments[i] = token;
            }
            else if (parameterTypes[i] == typeof(IJobCancellationToken) && invocation.Arguments[i] is IJobCancellationToken original)
            {
                originalArguments ??= (object?[])invocation.Arguments.Clone();
                invocation.Arguments[i] = new LinkedJobCancellationToken(original, token);
            }
        }

        return originalArguments;
    }

    private static void RestoreTokens(JobInvocation invocation, object?[] originalArguments)
    {
        Type[] parameterTypes = invocation.Invoker.ParameterTypes;

        for (int i = 0; i < parameterTypes.Length; i++)
        {
            if (parameterTypes[i] == typeof(CancellationToken) || parameterTypes[i] == typeof(IJobCancellationToken))
            {
                invocation.Arguments[i] = originalArguments[i];
            }
        }
    }

    /// <summary>
    /// The <see cref="IJobCancellationToken"/> a job gets under a timeout: <see cref="ShutdownToken"/> is the
    /// timeout's token (linked to the token it wraps), and <see cref="ThrowIfCancellationRequested"/> checks both,
    /// so the wrapped token still reports an aborted job.
    /// </summary>
    internal sealed class LinkedJobCancellationToken(IJobCancellationToken inner, CancellationToken shutdownToken) : IJobCancellationToken
    {
        /// <summary>The token this one wraps: the server's, or the one of an outer timeout.</summary>
        public IJobCancellationToken Inner { get; } = inner;

        public CancellationToken ShutdownToken { get; } = shutdownToken;

        public void ThrowIfCancellationRequested()
        {
            ShutdownToken.ThrowIfCancellationRequested();
            Inner.ThrowIfCancellationRequested();
        }
    }
}
