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
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.Server;

namespace Hangfire
{
    /// <summary>
    /// Retries the job method in place, inside the same worker, when it throws. Nothing is written to storage
    /// between attempts and the job never enters the Failed state, so this is the right tool for short-lived
    /// transient errors (a deadlock, a dropped connection), while <see cref="AutomaticRetryAttribute"/> remains
    /// the right tool for longer back-off.
    /// </summary>
    /// <remarks>
    /// Cancellation (<see cref="OperationCanceledException"/>) and <see cref="JobAbortedException"/> are never
    /// retried. Delays grow linearly: the n-th retry waits <c>n × DelayMilliseconds</c>.
    /// </remarks>
    public sealed class RetryInProcessAttribute : AsyncJobInterceptorAttribute
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(RetryInProcessAttribute));

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="attempts">Number of additional attempts after the first failure.</param>
        public RetryInProcessAttribute(int attempts)
        {
            if (attempts < 0) throw new ArgumentOutOfRangeException(nameof(attempts), "Attempts should be zero or greater.");

            Attempts = attempts;
            DelayMilliseconds = 200;
            ExceptionTypes = Type.EmptyTypes;
        }

        /// <summary>Gets the number of additional attempts.</summary>
        public int Attempts { get; }

        /// <summary>Gets or sets the base delay between attempts. Default is 200 ms.</summary>
        public int DelayMilliseconds { get; set; }

        /// <summary>
        /// Gets or sets the exception types (including subclasses) worth retrying. Empty means every
        /// exception except cancellation.
        /// </summary>
        public Type[] ExceptionTypes { get; set; }

        /// <inheritdoc />
        protected override async Task<object?> InterceptAsync(JobInvocation invocation)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));

            var shutdownToken = invocation.Context.CancellationToken.ShutdownToken;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await invocation.ProceedAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < Attempts && ShouldRetry(ex))
                {
                    var delay = TimeSpan.FromMilliseconds(DelayMilliseconds * (attempt + 1L));

                    Logger.WarnException(
                        $"Background job '{invocation.Context.BackgroundJob.Id}' failed on in-process attempt {attempt + 1} of {Attempts + 1} and will be retried in {delay}.",
                        ex);

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, shutdownToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private bool ShouldRetry(Exception exception)
        {
            if (exception is OperationCanceledException || exception is JobAbortedException)
            {
                return false;
            }

            if (ExceptionTypes.Length == 0)
            {
                return true;
            }

            var exceptionType = exception.GetType();
            return ExceptionTypes.Any(type => type.IsAssignableFrom(exceptionType));
        }
    }
}
