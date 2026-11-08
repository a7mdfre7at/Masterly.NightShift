#nullable enable

using System;
using System.Diagnostics;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Diagnostics
{
    /// <summary>How one execution of a background job ended, as a server filter sees it in <c>OnPerformed</c>.</summary>
    internal enum JobOutcome
    {
        /// <summary>The job method returned, or a server filter handled its exception.</summary>
        Succeeded,

        /// <summary>The job threw, and the worker records the attempt as failed.</summary>
        Failed,

        /// <summary>
        /// The job didn't finish on its own: a server filter canceled it, a state change aborted it, or the server
        /// shut down while it ran.
        /// </summary>
        Canceled,

        /// <summary>The job threw <see cref="RetryLaterException"/> to ask to run again later.</summary>
        RetryLater,

        /// <summary>
        /// The job method never ran: a server filter gave up waiting for a concurrency slot or a distributed lock
        /// (<see cref="ConcurrencyLimitException"/> from <c>[Throttle]</c> or <c>[RateLimit]</c>,
        /// <see cref="DistributedLockTimeoutException"/> from <c>[DisableConcurrentExecution]</c>). The worker still
        /// records the attempt as failed.
        /// </summary>
        NotRun,
    }

    /// <summary>
    /// Classifies a finished execution the same way for every server filter that reports outcomes
    /// (telemetry, insights), following what the performer and the worker do with each exception.
    /// </summary>
    internal static class JobOutcomes
    {
        public static JobOutcome Classify([NotNull] PerformedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.Canceled) return JobOutcome.Canceled;

            var exception = context.Exception;
            if (exception == null || context.ExceptionHandled) return JobOutcome.Succeeded;

            // A state change (deletion, requeue, ...) aborted the job while it ran
            if (exception is JobAbortedException) return JobOutcome.Canceled;

            // The performer lets an OperationCanceledException through unwrapped when the server is shutting down,
            // and the job is enqueued again. One the job threw for any other reason arrives wrapped in a
            // JobPerformanceException, and the worker records it as a failure.
            if (exception is OperationCanceledException && context.CancellationToken.ShutdownToken.IsCancellationRequested)
            {
                return JobOutcome.Canceled;
            }

            var thrown = Unwrap(exception);
            if (thrown is RetryLaterException) return JobOutcome.RetryLater;

            // A wait for a slot or a lock that timed out before the performer called the method. The same exception
            // thrown by the method itself (its own lock timed out) is a failure of the method.
            if ((thrown is ConcurrencyLimitException || thrown is DistributedLockTimeoutException) && !MethodInvoked(context))
            {
                return JobOutcome.NotRun;
            }

            return JobOutcome.Failed;
        }

        /// <summary>
        /// Returns the exception the job method threw: it reaches server filters wrapped in a
        /// <see cref="JobPerformanceException"/>.
        /// </summary>
        [NotNull]
        public static Exception Unwrap([NotNull] Exception exception)
        {
            return exception is JobPerformanceException && exception.InnerException != null
                ? exception.InnerException
                : exception;
        }

        /// <summary>
        /// Returns when the performer called the job method, as a <see cref="Stopwatch"/> timestamp, or <c>null</c>
        /// when it didn't call it (a filter threw or canceled first) or didn't say when.
        /// </summary>
        public static long? GetMethodInvokedTimestamp([NotNull] PerformContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            object? value;

            // A thread the job started may still add items (PerformContext.Console does, under this lock)
            lock (context.Items)
            {
                context.Items.TryGetValue(ConcurrencyControl.MethodInvokedItem, out value);
            }

            return value is long timestamp ? timestamp : null;
        }

        private static bool MethodInvoked(PerformContext context)
        {
            lock (context.Items)
            {
                return context.Items.ContainsKey(ConcurrencyControl.MethodInvokedItem);
            }
        }
    }
}
