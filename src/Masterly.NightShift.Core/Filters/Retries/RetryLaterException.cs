#nullable enable

using System;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    /// <summary>
    /// Thrown by a job to say <i>"not now, try again at this time"</i>. <see cref="AutomaticRetryAttribute"/>
    /// reschedules the job for exactly the requested moment instead of using its back-off, and the attempt
    /// does not count against <see cref="AutomaticRetryAttribute.Attempts"/>. Typical uses: an upstream API
    /// answered <c>429</c> with a <c>Retry-After</c> header, a resource is being rebuilt, a partner system is in
    /// its maintenance window.
    /// </summary>
    /// <remarks>
    /// <para>The number of times a job can ask to be retried later is capped by
    /// <see cref="AutomaticRetryAttribute.MaxRetryLaterRequests"/> (100 by default), so a job that keeps asking
    /// still fails eventually. The job parameter <c>RetryLaterCount</c> holds the number of requests so far.</para>
    /// <para>Without the <see cref="AutomaticRetryAttribute"/> filter the exception behaves like any other
    /// failure.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var response = await http.GetAsync(url);
    /// if (response.StatusCode == HttpStatusCode.TooManyRequests)
    /// {
    ///     throw new RetryLaterException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1), "Rate limited by the partner API");
    /// }
    /// </code>
    /// </example>
    [Serializable]
    public class RetryLaterException : Exception
    {
        /// <summary>The longest delay a job may ask for: one year.</summary>
        public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(365);

        /// <summary>Asks for a retry after the given delay.</summary>
        public RetryLaterException(TimeSpan delay)
            : this(delay, null)
        {
        }

        /// <summary>Asks for a retry after the given delay, with a reason shown in the dashboard.</summary>
        public RetryLaterException(TimeSpan delay, [CanBeNull] string? reason)
            : base(reason ?? $"The job asked to be retried in {delay}.")
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), delay, "The delay can't be negative.");
            if (delay > MaxDelay) throw new ArgumentOutOfRangeException(nameof(delay), delay, "The delay can't exceed one year.");

            Delay = delay;
            Reason = reason;
        }

        /// <summary>Asks for a retry at the given moment (UTC, or local time converted to UTC).</summary>
        public RetryLaterException(DateTime retryAt)
            : this(retryAt, null)
        {
        }

        /// <summary>Asks for a retry at the given moment (UTC, or local time converted to UTC), with a reason shown in the dashboard.</summary>
        public RetryLaterException(DateTime retryAt, [CanBeNull] string? reason)
            : base(reason ?? $"The job asked to be retried at {retryAt.ToUniversalTime():u}.")
        {
            RetryAt = retryAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(retryAt, DateTimeKind.Utc) : retryAt.ToUniversalTime();
            Reason = reason;
        }

        /// <summary>The requested delay, when the job asked for a delay.</summary>
        public TimeSpan? Delay { get; }

        /// <summary>The requested moment (UTC), when the job asked for a moment.</summary>
        public DateTime? RetryAt { get; }

        /// <summary>The reason given by the job, if any.</summary>
        public string? Reason { get; }

        /// <summary>Resolves the moment the job should run again, given the current time.</summary>
        public DateTime GetRetryAt(DateTime utcNow)
        {
            if (RetryAt.HasValue) return RetryAt.Value;

            var target = utcNow.Add(Delay ?? TimeSpan.Zero);
            return target < utcNow ? utcNow : target;
        }
    }
}
