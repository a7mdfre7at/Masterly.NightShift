#nullable enable

using System;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Expires jobs that end up in the <see cref="FailedState"/> (their retries are used up) after a retention period.
    /// Added to the global filters once by <see cref="GlobalConfigurationExtensions.UseFailedJobRetention"/>.
    /// </summary>
    /// <remarks>
    /// Like <see cref="ExpireAfterAttribute"/>, it sets the expiration in <see cref="OnStateUnapplied"/>, before any
    /// filter's <see cref="IApplyStateFilter.OnStateApplied"/>, and the state machine expires the job in the same
    /// transaction. It is a global filter with the default order, so an <see cref="ExpireAfterAttribute"/> with
    /// <see cref="ExpireAfterAttribute.IncludeFailed"/> on the job's method or type runs later and wins.
    /// </remarks>
    internal sealed class FailedJobRetentionFilter : IApplyStateFilter
    {
        private long _retentionTicks;

        public FailedJobRetentionFilter(TimeSpan retention)
        {
            Validate(retention, nameof(retention));
            _retentionTicks = retention.Ticks;
        }

        /// <summary>How long a failed job is kept. Changed when <c>UseFailedJobRetention</c> is called again.</summary>
        public TimeSpan Retention
        {
            get => TimeSpan.FromTicks(Volatile.Read(ref _retentionTicks));
            set
            {
                Validate(value, nameof(value));
                Volatile.Write(ref _retentionTicks, value.Ticks);
            }
        }

        /// <summary>Throws when <paramref name="retention"/> is not positive or longer than <see cref="ExpireAfterAttribute.MaxDays"/>.</summary>
        internal static void Validate(TimeSpan retention, [NotNull] string parameterName)
        {
            if (retention <= TimeSpan.Zero || retention > TimeSpan.FromDays(ExpireAfterAttribute.MaxDays))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    retention,
                    $"The retention period must be positive and at most {ExpireAfterAttribute.MaxDays} days.");
            }
        }

        internal static bool IsFailed([NotNull] IState state)
        {
            return FailedState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            SetExpiration(context);
        }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            // Not repeated here: this filter runs before a per-method [ExpireAfter(IncludeFailed = true)], and setting
            // the global value again would hide the per-method one from filters that run in between (the console's).
        }

        private void SetExpiration(ApplyStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (IsFailed(context.NewState))
            {
                context.JobExpirationTimeout = Retention;
                context.ExpireInNonFinalState = true;
            }
        }
    }
}
