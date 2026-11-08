#nullable enable

using System;
using System.Globalization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Sets how long a job's data is kept after it finishes, per method: <c>[ExpireAfter(days: 30)]</c> keeps an
    /// audit-relevant job for a month, <c>[ExpireAfter("00:10:00")]</c> drops a chatty one after ten minutes.
    /// Without it the storage default applies (one day). Applies to succeeded and deleted jobs; with
    /// <see cref="IncludeFailed"/> also to jobs that end up failed once their retries are used up. Without it, and
    /// without <c>UseFailedJobRetention</c>, failed jobs never expire.
    /// </summary>
    /// <remarks>
    /// The expiration is set in <see cref="OnStateUnapplied"/>, which the state machine calls for every apply
    /// filter before any <see cref="IApplyStateFilter.OnStateApplied"/>. So every apply filter, whatever its
    /// order, sees the per-method value in <see cref="ApplyStateContext.JobExpirationTimeout"/>: the job's
    /// console lines, for example, expire together with the job.
    /// <para>
    /// A failed job isn't in a final state: the state machine normally persists it. With <see cref="IncludeFailed"/>,
    /// the attribute asks the state machine to expire it in the same transaction instead. A failure that
    /// <see cref="AutomaticRetryAttribute"/> turns into a retry reaches the Scheduled state, not Failed, so it gets no
    /// expiration. Any later state change (requeue, reschedule) persists the job again, unless the new state is final
    /// and expires as usual.
    /// </para>
    /// </remarks>
    public sealed class ExpireAfterAttribute : JobFilterAttribute, IApplyStateFilter
    {
        /// <summary>
        /// The longest duration accepted: 36,500 days (about 100 years). A longer one would overflow the expiration
        /// date that storages compute from it.
        /// </summary>
        public const int MaxDays = 36500;

        /// <summary>
        /// Keeps finished jobs for the given <see cref="TimeSpan"/>, written as <c>d.hh:mm:ss</c> or <c>hh:mm:ss</c>.
        /// Positive, and at most <see cref="MaxDays"/> days.
        /// </summary>
        /// <exception cref="ArgumentException">The text is not a time span, or it is not positive, or it is too long.</exception>
        public ExpireAfterAttribute([NotNull] string timeSpan)
        {
            if (timeSpan == null) throw new ArgumentNullException(nameof(timeSpan));
            if (!TimeSpan.TryParse(timeSpan, CultureInfo.InvariantCulture, out var parsed) || parsed <= TimeSpan.Zero)
            {
                throw new ArgumentException($"'{timeSpan}' is not a positive time span.", nameof(timeSpan));
            }

            if (parsed > TimeSpan.FromDays(MaxDays))
            {
                throw new ArgumentException($"'{timeSpan}' is longer than {MaxDays} days.", nameof(timeSpan));
            }

            Duration = parsed;
            Order = 50;
        }

        /// <summary>Keeps finished jobs for the given number of days, from 1 to <see cref="MaxDays"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The number of days is not between 1 and <see cref="MaxDays"/>.</exception>
        public ExpireAfterAttribute(int days)
        {
            if (days <= 0 || days > MaxDays) throw new ArgumentOutOfRangeException(nameof(days), days, $"The number of days must be between 1 and {MaxDays}.");

            Duration = TimeSpan.FromDays(days);
            Order = 50;
        }

        /// <summary>How long finished jobs are kept.</summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Also expires the job after <see cref="Duration"/> when it reaches the <see cref="FailedState"/>, that is
        /// when its retries are used up. A job requeued, rescheduled or deleted before then follows the new state.
        /// <c>false</c> by default: failed jobs are kept until someone retries or deletes them, or until the global
        /// retention set with <c>UseFailedJobRetention</c> applies.
        /// </summary>
        public bool IncludeFailed { get; set; }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            // Normally already done in OnStateUnapplied; repeated for callers that only invoke OnStateApplied.
            SetExpiration(context);
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            // Runs before every OnStateApplied, so filters that read the timeout there (the job console filter
            // registered globally with a lower order, for one) already see the per-method value.
            SetExpiration(context);
        }

        private void SetExpiration(ApplyStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.NewState.IsFinal)
            {
                context.JobExpirationTimeout = Duration;
            }
            else if (IncludeFailed && FailedJobRetentionFilter.IsFailed(context.NewState))
            {
                context.JobExpirationTimeout = Duration;
                context.ExpireInNonFinalState = true;
            }
        }
    }
}
