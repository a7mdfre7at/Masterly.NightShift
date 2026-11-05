// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
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
    /// </remarks>
    public sealed class JobTimeoutAttribute : AsyncJobInterceptorAttribute
    {
        /// <summary>Name of the job parameter set when a method without a cancellation parameter exceeds the timeout.</summary>
        public const string TimeoutExceededParameter = "TimeoutExceeded";

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="timeoutInSeconds">Maximum run time of the job, in seconds.</param>
        public JobTimeoutAttribute(int timeoutInSeconds)
        {
            if (timeoutInSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutInSeconds), "Timeout should be greater than zero.");
            Timeout = TimeSpan.FromSeconds(timeoutInSeconds);
        }

        /// <summary>Gets the maximum run time.</summary>
        public TimeSpan Timeout { get; }

        /// <inheritdoc />
        protected override async Task<object?> InterceptAsync(JobInvocation invocation)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));

            var shutdownToken = invocation.Context.CancellationToken.ShutdownToken;

            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
            {
                var cancellable = ReplaceTokens(invocation, timeoutSource.Token);
                var startedAt = DateTime.UtcNow;

                timeoutSource.CancelAfter(Timeout);

                try
                {
                    var result = await invocation.ProceedAsync().ConfigureAwait(false);

                    if (!cancellable && DateTime.UtcNow - startedAt > Timeout)
                    {
                        invocation.Context.SetJobParameter(TimeoutExceededParameter, Timeout);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !shutdownToken.IsCancellationRequested && !invocation.Context.CancellationToken.IsAborted())
                {
                    // Kept short: retry reasons are cut to about 100 characters on the dashboard and in storage
                    throw new TimeoutException($"Timed out after {FormatTimeout()} (JobTimeout)");
                }
            }
        }

        private string FormatTimeout()
        {
            if (Timeout.TotalSeconds < 60) return $"{Timeout.TotalSeconds:0} seconds";
            if (Timeout.TotalMinutes < 60) return $"{Timeout.TotalMinutes:0.#} minutes";
            return $"{Timeout.TotalHours:0.#} hours";
        }

        private static bool ReplaceTokens(JobInvocation invocation, CancellationToken token)
        {
            var replaced = false;
            var parameterTypes = invocation.Invoker.ParameterTypes;

            for (var i = 0; i < parameterTypes.Length; i++)
            {
                if (parameterTypes[i] == typeof(CancellationToken))
                {
                    invocation.Arguments[i] = token;
                    replaced = true;
                }
                else if (parameterTypes[i] == typeof(IJobCancellationToken) && invocation.Arguments[i] is IJobCancellationToken original)
                {
                    invocation.Arguments[i] = new LinkedJobCancellationToken(original, token);
                    replaced = true;
                }
            }

            return replaced;
        }

        private sealed class LinkedJobCancellationToken : IJobCancellationToken
        {
            private readonly IJobCancellationToken _inner;

            public LinkedJobCancellationToken(IJobCancellationToken inner, CancellationToken shutdownToken)
            {
                _inner = inner;
                ShutdownToken = shutdownToken;
            }

            public CancellationToken ShutdownToken { get; }

            public void ThrowIfCancellationRequested()
            {
                ShutdownToken.ThrowIfCancellationRequested();
                _inner.ThrowIfCancellationRequested();
            }
        }
    }
}
