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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Masterly.NightShift
{
    /// <summary>
    /// Builds CRON expressions step by step, so a schedule reads as code instead of five cryptic fields:
    /// <c>Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays().ToString()</c> gives
    /// <c>*/15 9-17 * * 1-5</c>. The result is validated when <see cref="ToString"/> is called.
    /// </summary>
    public sealed class CronBuilder
    {
        private string? _second;
        private string _minute = "0";
        private string _hour = "0";
        private string _dayOfMonth = "*";
        private string _month = "*";
        private string _dayOfWeek = "*";
        private readonly SortedSet<int> _hours = new();
        private int? _minuteOfHour;

        internal CronBuilder()
        {
        }

        /// <summary>Runs every second, or every <paramref name="seconds"/> seconds. Produces a 6-field expression.</summary>
        public CronBuilder EverySeconds(int seconds = 1)
        {
            Validate(seconds, 1, 59, nameof(seconds));
            _second = Step(seconds);
            _minute = "*";
            _hour = "*";
            return this;
        }

        /// <summary>Runs every minute, or every <paramref name="minutes"/> minutes.</summary>
        public CronBuilder EveryMinutes(int minutes = 1)
        {
            Validate(minutes, 1, 59, nameof(minutes));
            _minute = Step(minutes);
            _hour = "*";
            return this;
        }

        /// <summary>Runs every hour, or every <paramref name="hours"/> hours, at the minute set by <see cref="AtMinute"/> (default 0).</summary>
        public CronBuilder EveryHours(int hours = 1)
        {
            Validate(hours, 1, 23, nameof(hours));
            _hour = Step(hours);
            _minute = (_minuteOfHour ?? 0).ToString(CultureInfo.InvariantCulture);
            return this;
        }

        /// <summary>Runs every day, or every <paramref name="days"/> days, at the times given with <see cref="At"/>.</summary>
        public CronBuilder EveryDays(int days = 1)
        {
            Validate(days, 1, 31, nameof(days));
            _dayOfMonth = Step(days);
            return this;
        }

        /// <summary>Runs every month, or every <paramref name="months"/> months, on the day given with <see cref="OnDayOfMonth"/> (default 1).</summary>
        public CronBuilder EveryMonths(int months = 1)
        {
            Validate(months, 1, 12, nameof(months));
            _month = Step(months);
            if (_dayOfMonth == "*") _dayOfMonth = "1";
            return this;
        }

        /// <summary>Runs at the given time of day. Call it several times for several runs a day; they must share the minute.</summary>
        public CronBuilder At(int hour, int minute = 0)
        {
            Validate(hour, 0, 23, nameof(hour));
            Validate(minute, 0, 59, nameof(minute));

            if (_minuteOfHour.HasValue && _minuteOfHour != minute)
            {
                throw new InvalidOperationException("Several times a day must share the same minute, for example 09:30 and 17:30.");
            }

            _minuteOfHour = minute;
            _hours.Add(hour);
            _minute = minute.ToString(CultureInfo.InvariantCulture);
            _hour = String.Join(",", _hours.Select(static x => x.ToString(CultureInfo.InvariantCulture)));
            return this;
        }

        /// <summary>For hourly schedules: the minute within the hour.</summary>
        public CronBuilder AtMinute(int minute)
        {
            Validate(minute, 0, 59, nameof(minute));
            _minuteOfHour = minute;
            _minute = minute.ToString(CultureInfo.InvariantCulture);
            return this;
        }

        /// <summary>Restricts runs to the hours from <paramref name="fromHour"/> to <paramref name="toHour"/>, both inclusive.</summary>
        public CronBuilder Between(int fromHour, int toHour)
        {
            Validate(fromHour, 0, 23, nameof(fromHour));
            Validate(toHour, 0, 23, nameof(toHour));
            if (toHour < fromHour) throw new ArgumentException("The end hour should not be before the start hour.", nameof(toHour));

            _hour = fromHour == toHour ? fromHour.ToString(CultureInfo.InvariantCulture) : $"{fromHour}-{toHour}";
            return this;
        }

        /// <summary>Restricts runs to the given days of week.</summary>
        public CronBuilder OnDays(params DayOfWeek[] days)
        {
            if (days == null || days.Length == 0) throw new ArgumentException("At least one day is required.", nameof(days));

            _dayOfWeek = String.Join(",", days.Select(static x => (int)x).Distinct().OrderBy(static x => x).Select(static x => x.ToString(CultureInfo.InvariantCulture)));
            return this;
        }

        /// <summary>Restricts runs to Monday to Friday.</summary>
        public CronBuilder OnWeekdays()
        {
            _dayOfWeek = "1-5";
            return this;
        }

        /// <summary>Restricts runs to Saturday and Sunday.</summary>
        public CronBuilder OnWeekends()
        {
            _dayOfWeek = "0,6";
            return this;
        }

        /// <summary>Runs on the given day of the month (1 to 31).</summary>
        public CronBuilder OnDayOfMonth(int day)
        {
            Validate(day, 1, 31, nameof(day));
            _dayOfMonth = day.ToString(CultureInfo.InvariantCulture);
            return this;
        }

        /// <summary>Runs on the last day of the month.</summary>
        public CronBuilder OnLastDayOfMonth()
        {
            _dayOfMonth = "L";
            return this;
        }

        /// <summary>Restricts runs to the given months (1 to 12).</summary>
        public CronBuilder InMonths(params int[] months)
        {
            if (months == null || months.Length == 0) throw new ArgumentException("At least one month is required.", nameof(months));
            foreach (var month in months) Validate(month, 1, 12, nameof(months));

            _month = String.Join(",", months.Distinct().OrderBy(static x => x).Select(static x => x.ToString(CultureInfo.InvariantCulture)));
            if (_dayOfMonth == "*") _dayOfMonth = "1";
            return this;
        }

        /// <summary>Returns the validated CRON expression.</summary>
        public override string ToString()
        {
            var cron = $"{_minute} {_hour} {_dayOfMonth} {_month} {_dayOfWeek}";
            if (_second != null) cron = $"{_second} {cron}";

            RecurringJobEntity.ParseCronExpression(cron);
            return cron;
        }

        /// <summary>The builder can be passed wherever a CRON string is expected.</summary>
        public static implicit operator string(CronBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return builder.ToString();
        }

        private static string Step(int value) => value == 1 ? "*" : $"*/{value.ToString(CultureInfo.InvariantCulture)}";

        private static void Validate(int value, int min, int max, string name)
        {
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(name, value, $"Value should be between {min} and {max}.");
            }
        }
    }
}
