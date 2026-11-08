#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Masterly.NightShift
{
    /// <summary>
    /// Builds CRON expressions step by step, so a schedule reads as code instead of five cryptic fields:
    /// <c>Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays().ToString()</c> gives
    /// <c>*/15 9-17 * * 1-5</c>. The calls can come in any order: each one fills its own part of the schedule,
    /// and the parts are combined and validated when <see cref="ToString"/> is called.
    /// </summary>
    /// <remarks>
    /// Two calls that would give the same field different values throw <see cref="InvalidOperationException"/>
    /// instead of one silently replacing the other. For example <see cref="At"/> and <see cref="EveryMinutes"/>
    /// both set the minute, <see cref="At"/> sets fixed hours that <see cref="EveryHours"/> and <see cref="Between"/>
    /// would contradict, and <see cref="OnWeekdays"/> and <see cref="OnWeekends"/> both set the days of week.
    /// Repeating a call with the same value is allowed, and <see cref="At"/> can be called several times to add
    /// times that share the minute.
    /// </remarks>
    public sealed class CronBuilder
    {
        private readonly Part _second = new Part("second");
        private readonly Part _minute = new Part("minute");
        private readonly Part _hourStep = new Part("hour interval");
        private readonly Part _hourRange = new Part("hour range");
        private readonly Part _dayOfMonth = new Part("day of month");
        private readonly Part _month = new Part("month");
        private readonly Part _dayOfWeek = new Part("day of week");
        private readonly SortedSet<int> _hours = new();
        private string? _firstAt;
        private bool _minuteInterval;
        private int _fromHour;
        private int _toHour;

        internal CronBuilder()
        {
        }

        /// <summary>
        /// Runs every second, or every <paramref name="seconds"/> seconds. Produces a 6-field expression.
        /// Combine with <see cref="Between"/> to limit the hours.
        /// </summary>
        /// <exception cref="InvalidOperationException">Another seconds interval was set before.</exception>
        public CronBuilder EverySeconds(int seconds = 1)
        {
            Validate(seconds, 1, 59, nameof(seconds));
            _second.Set(Step(seconds), Call("EverySeconds", seconds));
            return this;
        }

        /// <summary>Runs every minute, or every <paramref name="minutes"/> minutes. Combine with <see cref="Between"/> to limit the hours.</summary>
        /// <exception cref="InvalidOperationException"><see cref="At"/>, <see cref="AtMinute"/> or another interval already set the minute.</exception>
        public CronBuilder EveryMinutes(int minutes = 1)
        {
            Validate(minutes, 1, 59, nameof(minutes));
            _minute.Set(Step(minutes), Call("EveryMinutes", minutes));
            _minuteInterval = true;
            return this;
        }

        /// <summary>
        /// Runs every hour, or every <paramref name="hours"/> hours, at the minute set by <see cref="AtMinute"/> (default 0).
        /// Combine with <see cref="Between"/> to keep the runs within a range of hours: <c>EveryHours(2).Between(8, 18)</c>
        /// gives <c>0 8-18/2 * * *</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="At"/> already set fixed times of day, or another interval was set.</exception>
        public CronBuilder EveryHours(int hours = 1)
        {
            Validate(hours, 1, 23, nameof(hours));

            var call = Call("EveryHours", hours);
            if (_firstAt != null) throw AtConflict(_firstAt, call);

            _hourStep.Set(hours.ToString(CultureInfo.InvariantCulture), call);
            return this;
        }

        /// <summary>Runs every day, or every <paramref name="days"/> days, at the times given with <see cref="At"/>.</summary>
        /// <exception cref="InvalidOperationException">Another call already set the day of month.</exception>
        public CronBuilder EveryDays(int days = 1)
        {
            Validate(days, 1, 31, nameof(days));
            _dayOfMonth.Set(Step(days), Call("EveryDays", days));
            return this;
        }

        /// <summary>
        /// Runs every month, or every <paramref name="months"/> months, on the day given with <see cref="OnDayOfMonth"/>.
        /// Without a day of month or days of week, it runs on the 1st.
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="InMonths"/> or another interval already set the months.</exception>
        public CronBuilder EveryMonths(int months = 1)
        {
            Validate(months, 1, 12, nameof(months));
            _month.Set(Step(months), Call("EveryMonths", months));
            return this;
        }

        /// <summary>Runs at the given time of day. Call it several times for several runs a day; they must share the minute.</summary>
        /// <exception cref="InvalidOperationException">
        /// The minute differs from the one already set, or <see cref="EveryMinutes"/>, <see cref="EveryHours"/> or
        /// <see cref="Between"/> was called.
        /// </exception>
        public CronBuilder At(int hour, int minute = 0)
        {
            Validate(hour, 0, 23, nameof(hour));
            Validate(minute, 0, 59, nameof(minute));

            var call = Call("At", hour, minute);
            if (_hourStep.SetBy != null) throw AtConflict(call, _hourStep.SetBy);
            if (_hourRange.SetBy != null) throw AtConflict(call, _hourRange.SetBy);

            var minuteText = minute.ToString(CultureInfo.InvariantCulture);
            if (_firstAt != null && _minute.Value != minuteText)
            {
                throw new InvalidOperationException("Several times a day must share the same minute, for example 09:30 and 17:30.");
            }

            _minute.Set(minuteText, call);
            _hours.Add(hour);
            _firstAt ??= call;
            return this;
        }

        /// <summary>For hourly schedules: the minute within the hour.</summary>
        /// <exception cref="InvalidOperationException"><see cref="At"/>, <see cref="EveryMinutes"/> or another call already set a different minute.</exception>
        public CronBuilder AtMinute(int minute)
        {
            Validate(minute, 0, 59, nameof(minute));
            _minute.Set(minute.ToString(CultureInfo.InvariantCulture), Call("AtMinute", minute));
            return this;
        }

        /// <summary>
        /// Restricts runs to the hours from <paramref name="fromHour"/> to <paramref name="toHour"/>, both inclusive.
        /// It narrows <see cref="EverySeconds"/>, <see cref="EveryMinutes"/> and <see cref="EveryHours"/>; on its own
        /// it runs every hour of the range. A <paramref name="toHour"/> before <paramref name="fromHour"/> wraps past
        /// midnight: <c>Between(22, 6)</c> runs from 22:00 to 06:59.
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="At"/> already set fixed times of day, or another range was set.</exception>
        public CronBuilder Between(int fromHour, int toHour)
        {
            Validate(fromHour, 0, 23, nameof(fromHour));
            Validate(toHour, 0, 23, nameof(toHour));

            var call = Call("Between", fromHour, toHour);
            if (_firstAt != null) throw AtConflict(_firstAt, call);

            _hourRange.Set($"{fromHour.ToString(CultureInfo.InvariantCulture)}-{toHour.ToString(CultureInfo.InvariantCulture)}", call);
            _fromHour = fromHour;
            _toHour = toHour;
            return this;
        }

        /// <summary>Restricts runs to the given days of week.</summary>
        /// <exception cref="InvalidOperationException">Other days of week were set before.</exception>
        public CronBuilder OnDays(params DayOfWeek[] days)
        {
            if (days == null || days.Length == 0) throw new ArgumentException("At least one day is required.", nameof(days));

            var sorted = days.Select(static x => (int)x).Distinct().OrderBy(static x => x).Select(static x => x.ToString(CultureInfo.InvariantCulture));
            _dayOfWeek.Set(String.Join(",", sorted), "OnDays(...)");
            return this;
        }

        /// <summary>Restricts runs to Monday to Friday.</summary>
        /// <exception cref="InvalidOperationException">Other days of week were set before.</exception>
        public CronBuilder OnWeekdays()
        {
            _dayOfWeek.Set("1-5", "OnWeekdays()");
            return this;
        }

        /// <summary>Restricts runs to Saturday and Sunday.</summary>
        /// <exception cref="InvalidOperationException">Other days of week were set before.</exception>
        public CronBuilder OnWeekends()
        {
            _dayOfWeek.Set("0,6", "OnWeekends()");
            return this;
        }

        /// <summary>
        /// Runs on the given day of the month (1 to 31). Together with days of week, a run happens only on days
        /// that match both.
        /// </summary>
        /// <exception cref="InvalidOperationException">Another call already set the day of month.</exception>
        public CronBuilder OnDayOfMonth(int day)
        {
            Validate(day, 1, 31, nameof(day));
            _dayOfMonth.Set(day.ToString(CultureInfo.InvariantCulture), Call("OnDayOfMonth", day));
            return this;
        }

        /// <summary>Runs on the last day of the month.</summary>
        /// <exception cref="InvalidOperationException">Another call already set the day of month.</exception>
        public CronBuilder OnLastDayOfMonth()
        {
            _dayOfMonth.Set("L", "OnLastDayOfMonth()");
            return this;
        }

        /// <summary>
        /// Restricts runs to the given months (1 to 12), on the day given with <see cref="OnDayOfMonth"/>.
        /// Without a day of month or days of week, it runs on the 1st.
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="EveryMonths"/> or other months were set before.</exception>
        public CronBuilder InMonths(params int[] months)
        {
            if (months == null || months.Length == 0) throw new ArgumentException("At least one month is required.", nameof(months));
            foreach (var month in months) Validate(month, 1, 12, nameof(months));

            var sorted = months.Distinct().OrderBy(static x => x).Select(static x => x.ToString(CultureInfo.InvariantCulture));
            _month.Set(String.Join(",", sorted), "InMonths(...)");
            return this;
        }

        /// <summary>Returns the validated CRON expression.</summary>
        public override string ToString()
        {
            var second = _second.Value;
            var minute = _minute.Value ?? (second != null ? "*" : "0");
            var hour = BuildHour(second != null || _minuteInterval);
            var month = _month.Value ?? "*";
            var dayOfWeek = _dayOfWeek.Value ?? "*";

            // A month schedule runs on the 1st unless it says which days; days of week say it too
            var dayOfMonth = _dayOfMonth.Value ?? (_month.Value != null && _dayOfWeek.Value == null ? "1" : "*");

            var cron = $"{minute} {hour} {dayOfMonth} {month} {dayOfWeek}";
            if (second != null) cron = $"{second} {cron}";

            RecurringJobEntity.ParseCronExpression(cron);
            return cron;
        }

        /// <summary>The builder can be passed wherever a CRON string is expected.</summary>
        public static implicit operator string(CronBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return builder.ToString();
        }

        private string BuildHour(bool subHourInterval)
        {
            if (_hours.Count > 0) return String.Join(",", _hours.Select(static x => x.ToString(CultureInfo.InvariantCulture)));

            var step = _hourStep.Value;

            if (_hourRange.Value != null)
            {
                if (_fromHour == _toHour) return _fromHour.ToString(CultureInfo.InvariantCulture);
                return step == null || step == "1" ? _hourRange.Value : $"{_hourRange.Value}/{step}";
            }

            if (step != null) return step == "1" ? "*" : $"*/{step}";

            return subHourInterval ? "*" : "0";
        }

        private static InvalidOperationException AtConflict(string atCall, string otherCall)
        {
            return new InvalidOperationException(
                $"{atCall} can't be combined with {otherCall}: At sets fixed times of day. " +
                "For runs every few hours within a window, use EveryHours with AtMinute and Between, for example EveryHours(2).AtMinute(0).Between(9, 17).");
        }

        private static string Call(string method, params int[] arguments)
        {
            return $"{method}({String.Join(", ", arguments.Select(static x => x.ToString(CultureInfo.InvariantCulture)))})";
        }

        private static string Step(int value) => value == 1 ? "*" : $"*/{value.ToString(CultureInfo.InvariantCulture)}";

        private static void Validate(int value, int min, int max, string name)
        {
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(name, value, $"Value should be between {min} and {max}.");
            }
        }

        /// <summary>One CRON field, or part of one, and the call that set it.</summary>
        private sealed class Part
        {
            private readonly string _name;

            public Part(string name)
            {
                _name = name;
            }

            public string? Value { get; private set; }
            public string? SetBy { get; private set; }

            public void Set(string value, string call)
            {
                if (SetBy != null && !String.Equals(Value, value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{call} conflicts with {SetBy}: both set the {_name}.");
                }

                Value = value;
                SetBy ??= call;
            }
        }
    }
}
