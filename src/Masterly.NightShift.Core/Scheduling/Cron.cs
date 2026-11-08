// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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

namespace Masterly.NightShift
{
    /// <summary>
    /// Builds, parses, describes and evaluates CRON expressions for recurring jobs: presets such as
    /// <see cref="Daily()"/>, plain-English schedules (<see cref="FromText"/>), descriptions
    /// (<see cref="Describe"/>) and upcoming occurrences (<see cref="GetNextOccurrences"/>).
    /// </summary>
    public static class Cron
    {
        /// <summary>
        /// Turns an English phrase into a CRON expression, for example "every 15 minutes", "daily at 3:30 pm",
        /// "weekdays at 9", "every monday and friday at 08:00", "every 10 minutes between 9 and 17 on weekdays",
        /// "monthly on the 15th at midnight", "last day of month at 23:00" or "every 30 seconds". A phrase that
        /// already is a CRON expression is returned unchanged.
        /// </summary>
        /// <param name="text">The schedule in plain English.</param>
        /// <returns>A CRON expression accepted by <see cref="RecurringJob"/>.</returns>
        /// <exception cref="FormatException">The phrase isn't understood or can't be expressed in CRON.</exception>
        public static string FromText(string text)
        {
            return CronTextParser.Parse(text);
        }

        /// <summary>
        /// Describes a CRON expression in English, for example "Every 15 minutes, between 09:00 and 17:59,
        /// Monday to Friday" for <c>*/15 9-17 * * 1-5</c>. Accepts 5- and 6-field expressions and the
        /// <c>@</c> presets.
        /// </summary>
        /// <param name="cronExpression">The expression.</param>
        /// <exception cref="FormatException">The expression can't be parsed.</exception>
        public static string Describe(string cronExpression)
        {
            if (cronExpression == null) throw new ArgumentNullException(nameof(cronExpression));

            RecurringJobEntity.ParseCronExpression(cronExpression);
            return CronDescriber.Describe(cronExpression);
        }

        /// <summary>
        /// Like <see cref="Describe"/>, but returns <c>null</c> for expressions it can't parse.
        /// </summary>
        /// <param name="cronExpression">The expression.</param>
        public static string? TryDescribe(string? cronExpression)
        {
            if (cronExpression == null) return null;

            try
            {
                return Describe(cronExpression);
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
            {
                return null;
            }
        }

        /// <summary>
        /// Starts a fluent <see cref="CronBuilder"/>, for example
        /// <c>Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays()</c>.
        /// </summary>
        public static CronBuilder Builder()
        {
            return new CronBuilder();
        }

        /// <summary>
        /// Like <see cref="FromText"/>, but returns <c>false</c> instead of throwing.
        /// </summary>
        /// <param name="text">The schedule in plain English.</param>
        /// <param name="cronExpression">The CRON expression, or <c>null</c> when the method returns <c>false</c>.</param>
        public static bool TryFromText(string? text, out string? cronExpression)
        {
            if (text == null)
            {
                cronExpression = null;
                return false;
            }

            try
            {
                cronExpression = CronTextParser.Parse(text);
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
            {
                cronExpression = null;
                return false;
            }
        }

        /// <summary>
        /// Computes the next occurrences of a CRON expression, as the dashboard preview shows them.
        /// </summary>
        /// <param name="cronExpression">A 5- or 6-part CRON expression.</param>
        /// <param name="timeZone">Time zone the expression is evaluated in.</param>
        /// <param name="fromUtc">Occurrences strictly after this UTC instant are returned.</param>
        /// <param name="count">How many occurrences to return.</param>
        /// <returns>Up to <paramref name="count"/> UTC instants; fewer when the expression stops firing.</returns>
        public static IReadOnlyList<DateTime> GetNextOccurrences(string cronExpression, TimeZoneInfo timeZone, DateTime fromUtc, int count)
        {
            if (cronExpression == null) throw new ArgumentNullException(nameof(cronExpression));
            if (timeZone == null) throw new ArgumentNullException(nameof(timeZone));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            var expression = RecurringJobEntity.ParseCronExpression(cronExpression);
            var result = new List<DateTime>(count);
            DateTime? next = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);

            while (result.Count < count && (next = expression.GetNextOccurrence(next.Value, timeZone, inclusive: false)) != null)
            {
                result.Add(next.Value);
            }

            return result;
        }

        /// <summary>
        /// Returns cron expression that fires every minute.
        /// </summary>
        public static string Minutely()
        {
            return "* * * * *";
        }

        /// <summary>
        /// Returns cron expression that fires every hour at the first minute.
        /// </summary>
        public static string Hourly()
        {
            return Hourly(minute: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every hour at the specified minute.
        /// </summary>
        /// <param name="minute">The minute in which the schedule will be activated (0-59).</param>
        public static string Hourly(int minute)
        {
            return $"{minute} * * * *";
        }

        /// <summary>
        /// Returns cron expression that fires every day at 00:00 UTC.
        /// </summary>
        public static string Daily()
        {
            return Daily(hour: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every day at the first minute of
        /// the specified hour in UTC.
        /// </summary>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        public static string Daily(int hour)
        {
            return Daily(hour, minute: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every day at the specified hour and minute
        /// in UTC.
        /// </summary>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        /// <param name="minute">The minute in which the schedule will be activated (0-59).</param>
        public static string Daily(int hour, int minute)
        {
            return $"{minute} {hour} * * *";
        }

        /// <summary>
        /// Returns cron expression that fires every week at Monday, 00:00 UTC.
        /// </summary>
        public static string Weekly()
        {
            return Weekly(DayOfWeek.Monday);
        }

        /// <summary>
        /// Returns cron expression that fires every week at 00:00 UTC of the specified
        /// day of the week.
        /// </summary>
        /// <param name="dayOfWeek">The day of week in which the schedule will be activated.</param>
        public static string Weekly(DayOfWeek dayOfWeek)
        {
            return Weekly(dayOfWeek, hour: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every week at the first minute
        /// of the specified day of week and hour in UTC.
        /// </summary>
        /// <param name="dayOfWeek">The day of week in which the schedule will be activated.</param>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        public static string Weekly(DayOfWeek dayOfWeek, int hour)
        {
            return Weekly(dayOfWeek, hour, minute: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every week at the specified day
        /// of week, hour and minute in UTC.
        /// </summary>
        /// <param name="dayOfWeek">The day of week in which the schedule will be activated.</param>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        /// <param name="minute">The minute in which the schedule will be activated (0-59).</param>
        public static string Weekly(DayOfWeek dayOfWeek, int hour, int minute)
        {
            return $"{minute} {hour} * * {(int) dayOfWeek}";
        }

        /// <summary>
        /// Returns cron expression that fires every month at 00:00 UTC of the first
        /// day of month.
        /// </summary>
        public static string Monthly()
        {
            return Monthly(day: 1);
        }

        /// <summary>
        /// Returns cron expression that fires every month at 00:00 UTC of the specified
        /// day of month.
        /// </summary>
        /// <param name="day">The day of month in which the schedule will be activated (1-31).</param>
        public static string Monthly(int day)
        {
            return Monthly(day, hour: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every month at the first minute of the
        /// specified day of month and hour in UTC.
        /// </summary>
        /// <param name="day">The day of month in which the schedule will be activated (1-31).</param>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        public static string Monthly(int day, int hour)
        {
            return Monthly(day, hour, minute: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every month at the specified day of month,
        /// hour and minute in UTC.
        /// </summary>
        /// <param name="day">The day of month in which the schedule will be activated (1-31).</param>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        /// <param name="minute">The minute in which the schedule will be activated (0-59).</param>
        public static string Monthly(int day, int hour, int minute)
        {
            return $"{minute} {hour} {day} * *";
        }

        /// <summary>
        /// Returns cron expression that fires every year on Jan, 1st at 00:00 UTC.
        /// </summary>
        public static string Yearly()
        {
            return Yearly(month: 1);
        }

        /// <summary>
        /// Returns cron expression that fires every year in the first day at 00:00 UTC
        /// of the specified month.
        /// </summary>
        /// <param name="month">The month in which the schedule will be activated (1-12).</param>
        public static string Yearly(int month)
        {
            return Yearly(month, day: 1);
        }

        /// <summary>
        /// Returns cron expression that fires every year at 00:00 UTC of the specified
        /// month and day of month.
        /// </summary>
        /// <param name="month">The month in which the schedule will be activated (1-12).</param>
        /// <param name="day">The day of month in which the schedule will be activated (1-31).</param>
        public static string Yearly(int month, int day)
        {
            return Yearly(month, day, hour: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every year at the first minute of the
        /// specified month, day and hour in UTC.
        /// </summary>
        /// <param name="month">The month in which the schedule will be activated (1-12).</param>
        /// <param name="day">The day of month in which the schedule will be activated (1-31).</param>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        public static string Yearly(int month, int day, int hour)
        {
            return Yearly(month, day, hour, minute: 0);
        }

        /// <summary>
        /// Returns cron expression that fires every year at the specified month, day,
        /// hour and minute in UTC.
        /// </summary>
        /// <param name="month">The month in which the schedule will be activated (1-12).</param>
        /// <param name="day">The day of month in which the schedule will be activated (1-31).</param>
        /// <param name="hour">The hour in which the schedule will be activated (0-23).</param>
        /// <param name="minute">The minute in which the schedule will be activated (0-59).</param>
        public static string Yearly(int month, int day, int hour, int minute)
        {
            return $"{minute} {hour} {day} {month} *";
        }

        /// <summary>
        /// Returns cron expression that never fires. Specifically 31st of February.
        /// </summary>
        public static string Never()
        {
            return Yearly(2, 31);
        }

        /// <summary>
        /// Returns cron expression that fires every <paramref name="interval"/> minutes.
        /// </summary>
        /// <remarks>
        /// Please note that only those intervals into which the number 60 is evenly divisible make sense
        /// in Cron expressions, such as 2, 5, 10, 15, 30, etc. Intervals such as 7, 13, 25 will not work
        /// correctly.
        /// </remarks>
        /// <param name="interval">The number of minutes to wait between every activation.</param>
        public static string MinuteInterval(int interval)
        {
            return $"*/{interval} * * * *";
        }

        /// <summary>
        /// Returns cron expression that fires every <paramref name="interval"/> hours.
        /// </summary>
        /// <remarks>
        /// Please note that only those intervals into which the number 24 is evenly divisible make sense
        /// in Cron expressions, such as 2, 4, 6, 8, 12, etc. Intervals such as 7, 13, 25 will not work
        /// correctly.
        /// </remarks>
        /// <param name="interval">The number of hours to wait between every activation.</param>
        public static string HourInterval(int interval)
        {
            return $"0 */{interval} * * *";
        }

        /// <summary>
        /// Returns cron expression that fires every <paramref name="interval"/> days.
        /// </summary>
        /// <remarks>
        /// Please note that only those intervals into which the number 30 is evenly divisible make sense
        /// in Cron expressions, such as 2, 5, 6, 10, 15, etc. Intervals such as 7, 13, 25 will not work
        /// correctly. However, even in this case the actual intervals may be longer in months with
        /// 31 or 28 days.
        /// </remarks>
        /// <param name="interval">The number of days to wait between every activation.</param>
        public static string DayInterval(int interval)
        {
            return $"0 0 */{interval} * *";
        }

        /// <summary>
        /// Returns cron expression that fires every <paramref name="interval"/> months.
        /// </summary>
        /// <remarks>
        /// Please note that only those intervals into which the number 12 is evenly divisible make sense
        /// in Cron expressions, such as 2, 3, 4, 6. Intervals such as 7, 13, 25 will not work correctly.
        /// </remarks>
        /// <param name="interval">The number of months to wait between every activation.</param>
        public static string MonthInterval(int interval)
        {
            return $"0 0 1 */{interval} *";
        }
    }
}
