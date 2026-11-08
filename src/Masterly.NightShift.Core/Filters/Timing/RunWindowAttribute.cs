#nullable enable

using System;
using System.Globalization;
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;

namespace Masterly.NightShift
{
    /// <summary>
    /// Lets a job run only inside a daily time window: <c>[RunWindow("09:00-17:00")]</c>,
    /// <c>[RunWindow("22:00-06:00", "Europe/Berlin", WeekdaysOnly = true)]</c>. A job picked up outside the
    /// window is put back to the Scheduled state for the window's next start, so heavy work waits for the
    /// night and customer-facing work waits for office hours without any code in the job. Overnight windows
    /// (end before start) are supported. Use <see cref="BlackoutAttribute"/> for the opposite rule.
    /// </summary>
    public class RunWindowAttribute : JobFilterAttribute, IElectStateFilter
    {
        private static readonly string[] TimeFormats = { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };

        private readonly TimeZoneInfo _timeZone;

        /// <summary>Allows the job to start between the two <c>HH:mm</c> times, in UTC or the given time zone.</summary>
        public RunWindowAttribute([NotNull] string window, [CanBeNull] string? timeZoneId = null)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            var parts = window.Split('-');
            if (parts.Length != 2 || !TryParseTime(parts[0], out var start) || !TryParseTime(parts[1], out var end))
            {
                throw new ArgumentException($"'{window}' is not a window in the form HH:mm-HH:mm.", nameof(window));
            }

            if (start == end) throw new ArgumentException("The window can't be empty.", nameof(window));

            Window = window.Trim();
            Start = start;
            End = end;
            _timeZone = String.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Utc : new DefaultTimeZoneResolver().GetTimeZoneById(timeZoneId!.Trim());
            Order = 15;
        }

        /// <summary>The window as given, <c>HH:mm-HH:mm</c>.</summary>
        public string Window { get; }

        /// <summary>When the window opens, as time of day.</summary>
        public TimeSpan Start { get; }

        /// <summary>When the window closes, as time of day. Before <see cref="Start"/> for an overnight window.</summary>
        public TimeSpan End { get; }

        /// <summary>The time zone the window is expressed in.</summary>
        public string TimeZoneId => _timeZone.Id;

        /// <summary>
        /// Limits the rule to Monday to Friday. On a <see cref="RunWindowAttribute"/> it also keeps the job off
        /// Saturdays and Sundays; on a <see cref="BlackoutAttribute"/> the blackout applies on weekdays only, and
        /// the job may start at any time at the weekend. The day is the one of the moment checked, in the
        /// window's time zone, so the part of an overnight window after midnight belongs to the next day.
        /// </summary>
        public bool WeekdaysOnly { get; set; }

        /// <summary>Whether the window is a blackout (the job may run only outside it).</summary>
        protected virtual bool Invert => false;

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <inheritdoc />
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.CandidateState is EnqueuedState enqueuedState)
            {
                // A job this filter held is enqueued again: back to the queue it was held from
                HeldQueue.Restore(context, enqueuedState);
                return;
            }

            if (context.CandidateState is not ProcessingState) return;

            var nowUtc = Clock();
            var nextStartUtc = GetNextStart(nowUtc, out var allowedNow);
            if (allowedNow) return;

            // The job comes back with a new Enqueued state for the default queue: keep a queue given only by its state
            HeldQueue.Remember(context);
            context.CandidateState = new ScheduledState(nextStartUtc)
            {
                Reason = Invert
                    ? $"Blackout {Window} ({_timeZone.Id}); runs at {nextStartUtc:yyyy-MM-dd HH:mm} UTC"
                    : $"Outside the run window {Window} ({_timeZone.Id}); runs at {nextStartUtc:yyyy-MM-dd HH:mm} UTC",
            };
        }

        /// <summary>
        /// Tells whether the job may start now and, if not, when it may: the next moment (UTC) the rules allow.
        /// </summary>
        public DateTime GetNextStart(DateTime utcNow, out bool allowedNow)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), _timeZone);

            if (IsAllowed(local))
            {
                allowedNow = true;
                return utcNow;
            }

            allowedNow = false;

            // Walk forward from boundary to boundary (where the rules change), at least six days ahead
            var candidate = local;
            for (var i = 0; i < 20; i++)
            {
                candidate = SkipInvalidTime(NextBoundary(candidate));
                if (IsAllowed(candidate))
                {
                    return ToUtc(candidate, utcNow);
                }
            }

            return utcNow.AddDays(1);
        }

        // A boundary inside the hour repeated when clocks go back happens twice. The first time that is still ahead
        // counts (the daylight saving one, then the standard one), so the job neither starts an hour late nor skips
        // the night. TimeZoneInfo.ConvertTimeToUtc would always pick the standard, later one.
        private DateTime ToUtc(DateTime local, DateTime utcNow)
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            if (_timeZone.IsAmbiguousTime(unspecified))
            {
                // The larger offset gives the earlier instant
                foreach (var offset in _timeZone.GetAmbiguousTimeOffsets(unspecified).OrderByDescending(static x => x))
                {
                    var utc = DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc);
                    if (utc >= utcNow) return utc;
                }
            }

            return TimeZoneInfo.ConvertTimeToUtc(unspecified, _timeZone);
        }

        // A boundary inside the hour skipped when clocks go forward doesn't exist, and converting it to UTC
        // throws. The first moment that exists is the end of that gap.
        private DateTime SkipInvalidTime(DateTime local)
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            for (var minutes = 0; minutes < MinutesPerDay && _timeZone.IsInvalidTime(unspecified); minutes++)
            {
                unspecified = unspecified.AddMinutes(1);
            }

            return unspecified;
        }

        private const int MinutesPerDay = 24 * 60;

        private bool IsAllowed(DateTime local)
        {
            // With WeekdaysOnly the rule is off at the weekend: a run window is closed, a blackout is lifted
            var ruleApplies = !WeekdaysOnly || (local.DayOfWeek != DayOfWeek.Saturday && local.DayOfWeek != DayOfWeek.Sunday);
            var inside = IsInside(local.TimeOfDay);

            return Invert
                ? !(ruleApplies && inside)
                : ruleApplies && inside;
        }

        private bool IsInside(TimeSpan time)
        {
            return Start < End
                ? time >= Start && time < End
                : time >= Start || time < End;
        }

        private DateTime NextBoundary(DateTime local)
        {
            var date = local.Date;
            var time = local.TimeOfDay;

            // The boundaries of a day are Start and End; take the first one after "local", else tomorrow's first.
            // With WeekdaysOnly the rules also change at midnight, when a weekend starts or ends.
            var first = Start < End ? Start : End;
            var second = Start < End ? End : Start;

            if (time < first) return date + first;
            if (time < second) return date + second;
            return WeekdaysOnly ? date.AddDays(1) : date.AddDays(1) + first;
        }

        private static bool TryParseTime(string text, out TimeSpan time)
        {
            text = text.Trim();

            if (TimeSpan.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1))
            {
                return true;
            }

            if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) && hour >= 0 && hour < 24)
            {
                time = TimeSpan.FromHours(hour);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Keeps a job from starting inside a daily time window: <c>[Blackout("02:00-04:00")]</c> for the nightly
    /// database maintenance, <c>[Blackout("09:00-17:00", "America/New_York")]</c> to spare the trading day.
    /// A job picked up inside the window waits for its end. With <c>WeekdaysOnly = true</c> the blackout applies
    /// Monday to Friday only. The opposite of <see cref="RunWindowAttribute"/>.
    /// </summary>
    public sealed class BlackoutAttribute : RunWindowAttribute
    {
        /// <summary>Forbids the job to start between the two <c>HH:mm</c> times, in UTC or the given time zone.</summary>
        public BlackoutAttribute([NotNull] string window, [CanBeNull] string? timeZoneId = null)
            : base(window, timeZoneId)
        {
        }

        /// <inheritdoc />
        protected override bool Invert => true;
    }
}
