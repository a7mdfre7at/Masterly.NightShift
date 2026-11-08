#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>
    /// Cancels a job that runs longer than the given time and fails it with a <see cref="TimeoutException"/>.
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
        /// <see cref="Int32.MaxValue"/> milliseconds on .NET Framework, so that is the limit on every framework.
        /// </summary>
        public const int MaxTimeoutInSeconds = Int32.MaxValue / 1000;

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
            if (timeoutInSeconds <= 0 || timeoutInSeconds > MaxTimeoutInSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeoutInSeconds),
                    timeoutInSeconds,
                    $"Timeout should be between 1 and {MaxTimeoutInSeconds} seconds (about 24.8 days), the longest a timer accepts on every framework.");
            }

            Timeout = TimeSpan.FromSeconds(timeoutInSeconds);
        }

        /// <summary>Gets the maximum run time.</summary>
        public TimeSpan Timeout { get; }

        /// <inheritdoc />
        protected override async Task<object?> InterceptAsync(JobInvocation invocation)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));

            var shutdownToken = invocation.Context.CancellationToken.ShutdownToken;

            // The timer is this attribute's own, so it knows whether it timed out itself or an outer one did. The
            // token the method gets is linked to the tokens already in place (those of an outer JobTimeout, for
            // example), so every timeout applies and the shortest wins.
            using (var timer = new CancellationTokenSource())
            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(GetTokensToLink(invocation, shutdownToken, timer.Token)))
            {
                var originalArguments = ReplaceTokens(invocation, timeoutSource.Token);
                var startedAt = DateTime.UtcNow;

                timer.CancelAfter(Timeout);

                try
                {
                    var result = await invocation.ProceedAsync().ConfigureAwait(false);

                    if (originalArguments == null && DateTime.UtcNow - startedAt > Timeout)
                    {
                        RecordExceeded(invocation.Context);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (timer.IsCancellationRequested && !shutdownToken.IsCancellationRequested && !invocation.Context.CancellationToken.IsAborted())
                {
                    // Kept short: retry reasons are cut to about 100 characters on the dashboard and in storage
                    throw new TimeoutException($"Timed out after {FormatTimeout()} (JobTimeout)");
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
        }

        // A method without a cancellation parameter ran too long. With nested timeouts the inner one records first;
        // a longer outer one doesn't overwrite a shorter one already recorded. Only a record: the job succeeded, and a
        // storage error doesn't change that.
        private void RecordExceeded(PerformContext context)
        {
            lock (context.Items)
            {
                if (context.Items.TryGetValue(ExceededItem, out var recorded) && recorded is TimeSpan shorter && shorter <= Timeout) return;
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
            var tokens = new List<CancellationToken> { shutdownToken, timerToken };
            var parameterTypes = invocation.Invoker.ParameterTypes;

            for (var i = 0; i < parameterTypes.Length; i++)
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

            return tokens.ToArray();
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
            var parameterTypes = invocation.Invoker.ParameterTypes;

            for (var i = 0; i < parameterTypes.Length; i++)
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
            var parameterTypes = invocation.Invoker.ParameterTypes;

            for (var i = 0; i < parameterTypes.Length; i++)
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
        internal sealed class LinkedJobCancellationToken : IJobCancellationToken
        {
            public LinkedJobCancellationToken(IJobCancellationToken inner, CancellationToken shutdownToken)
            {
                Inner = inner;
                ShutdownToken = shutdownToken;
            }

            /// <summary>The token this one wraps: the server's, or the one of an outer timeout.</summary>
            public IJobCancellationToken Inner { get; }

            public CancellationToken ShutdownToken { get; }

            public void ThrowIfCancellationRequested()
            {
                ShutdownToken.ThrowIfCancellationRequested();
                Inner.ThrowIfCancellationRequested();
            }
        }
    }
}
