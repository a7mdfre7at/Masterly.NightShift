#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>
    /// Retries the job method in place, inside the same worker, when it throws. Nothing is written to storage
    /// between attempts and the job never enters the Failed state, so this is the right tool for short-lived
    /// transient errors (a deadlock, a dropped connection), while <see cref="AutomaticRetryAttribute"/> remains
    /// the right tool for longer back-off.
    /// </summary>
    /// <remarks>
    /// Cancellation (<see cref="OperationCanceledException"/>) and <see cref="JobAbortedException"/> are never
    /// retried. With an empty <see cref="ExceptionTypes"/>, neither are <see cref="RetryLaterException"/> and
    /// <see cref="ConcurrencyLimitException"/>: they ask for a later attempt, which an immediate one would ignore. List
    /// them in <see cref="ExceptionTypes"/> to retry them in place anyway. Delays grow linearly: the n-th retry waits
    /// <c>n × DelayMilliseconds</c>, up to about 24.8 days. For a synchronous job method the worker thread waits out the delay, so every
    /// attempt runs on the worker thread; for an asynchronous one the delay is awaited.
    /// </remarks>
    public sealed class RetryInProcessAttribute : AsyncJobInterceptorAttribute
    {
        /// <summary>The largest <see cref="DelayMilliseconds"/>: 3,600,000 (one hour).</summary>
        public const int MaxDelayMilliseconds = 60 * 60 * 1000;

        // The longest single wait: a wait handle and Task.Delay accept at most Int32.MaxValue milliseconds on
        // .NET Framework (about 24.8 days)
        private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(Int32.MaxValue);

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(RetryInProcessAttribute));

        private int _delayMilliseconds;

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

        /// <summary>
        /// Gets or sets the base delay between attempts. Default is 200 ms. From 0 (no delay) to
        /// <see cref="MaxDelayMilliseconds"/> (one hour). The n-th retry waits n times this, but never longer than
        /// about 24.8 days.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative or above <see cref="MaxDelayMilliseconds"/>.</exception>
        public int DelayMilliseconds
        {
            get => _delayMilliseconds;
            set
            {
                if (value < 0 || value > MaxDelayMilliseconds)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"DelayMilliseconds should be between 0 and {MaxDelayMilliseconds} (one hour).");
                }

                _delayMilliseconds = value;
            }
        }

        /// <summary>
        /// Gets or sets the exception types (including subclasses) worth retrying. Empty means every exception except
        /// cancellation, <see cref="RetryLaterException"/> and <see cref="ConcurrencyLimitException"/>.
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
                    var delay = GetDelay(attempt);

                    Logger.WarnException(
                        $"Background job '{invocation.Context.BackgroundJob.Id}' failed on in-process attempt {attempt + 1} of {Attempts + 1} and will be retried in {delay}.",
                        ex);

                    if (delay <= TimeSpan.Zero) continue;

                    if (invocation.ReturnsTaskLike)
                    {
                        await Task.Delay(delay, shutdownToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // The worker thread waits for a synchronous job anyway: waiting here keeps every attempt on
                        // it, where an await would run the next attempts on a thread-pool thread
                        shutdownToken.WaitHandle.WaitOne(delay);
                        shutdownToken.ThrowIfCancellationRequested();
                    }
                }
            }
        }

        /// <summary>
        /// The wait before the retry that follows attempt <paramref name="attempt"/> (0 for the first):
        /// <c>(attempt + 1) × DelayMilliseconds</c>, at most about 24.8 days, so it never overflows what a wait accepts.
        /// </summary>
        internal TimeSpan GetDelay(int attempt)
        {
            var milliseconds = (double)DelayMilliseconds * (Math.Max(attempt, 0) + 1.0);
            return milliseconds >= MaxWait.TotalMilliseconds ? MaxWait : TimeSpan.FromMilliseconds(milliseconds);
        }

        private bool ShouldRetry(Exception exception)
        {
            if (exception is OperationCanceledException || exception is JobAbortedException)
            {
                return false;
            }

            if (ExceptionTypes.Length == 0)
            {
                // These ask for a later attempt; retrying at once would ignore that
                return !(exception is RetryLaterException || exception is ConcurrencyLimitException);
            }

            var exceptionType = exception.GetType();
            return ExceptionTypes.Any(type => type.IsAssignableFrom(exceptionType));
        }
    }
}
