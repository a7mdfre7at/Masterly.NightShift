#nullable enable

using System;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    /// <summary>
    /// Operations on recurring jobs that don't need the job's method: pausing, resuming and changing the
    /// schedule. The dashboard uses them. A separate interface keeps <see cref="IRecurringJobManager"/>
    /// implementations in other packages compiling; the extension methods in
    /// <see cref="RecurringJobManagerExtensions"/> forward to this interface when the manager implements it.
    /// </summary>
    public interface IRecurringJobManagerV3 : IRecurringJobManagerV2
    {
        /// <summary>
        /// Takes the job out of the schedule until <see cref="Resume"/>. Its definition is kept, and it can
        /// still be triggered by hand. Re-registering the job with <c>AddOrUpdate</c> doesn't resume it.
        /// </summary>
        /// <param name="recurringJobId">The recurring job identifier.</param>
        /// <returns><c>false</c> when the job doesn't exist.</returns>
        bool Pause([NotNull] string recurringJobId);

        /// <summary>
        /// Puts a paused job back on the schedule from now. Occurrences missed while paused aren't run.
        /// </summary>
        /// <param name="recurringJobId">The recurring job identifier.</param>
        /// <returns><c>false</c> when the job doesn't exist.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
            Justification = "Public API. 'Resume' is only a keyword in Visual Basic, where it can be escaped.")]
        bool Resume([NotNull] string recurringJobId);

        /// <summary>
        /// Changes when and where a job runs without touching what it runs. Arguments left <c>null</c> keep
        /// their current value.
        /// </summary>
        /// <param name="recurringJobId">The recurring job identifier.</param>
        /// <param name="cronExpression">New CRON expression, or <c>null</c> to keep the current one.</param>
        /// <param name="timeZone">New time zone, or <c>null</c> to keep the current one.</param>
        /// <param name="queue">
        /// New queue, or <c>null</c> to keep the current one. On storages that support per-job queues
        /// (<see cref="Storage.JobStorageFeatures.JobQueueProperty"/>), a queue other than <c>default</c> also
        /// becomes the job's own queue, so retries of a run go back to it. On other storages it applies only to the
        /// first enqueue of each run.
        /// </param>
        /// <param name="misfireHandling">New misfire handling, or <c>null</c> to keep the current one.</param>
        /// <returns><c>false</c> when the job doesn't exist.</returns>
        /// <exception cref="ArgumentException">The CRON expression is invalid.</exception>
        bool UpdateSchedule(
            [NotNull] string recurringJobId,
            [CanBeNull] string? cronExpression,
            [CanBeNull] TimeZoneInfo? timeZone = null,
            [CanBeNull] string? queue = null,
            MisfireHandlingMode? misfireHandling = null);
    }
}
