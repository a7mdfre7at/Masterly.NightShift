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
using System.Text.RegularExpressions;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    /// <summary>
    /// Turns an English phrase into a moment: "in 20 minutes", "in 2 hours", "tomorrow at 9am", "today at 17:30",
    /// "next monday at 8", "friday 6pm", "at 15:00" (today, or tomorrow if that already passed), "tonight",
    /// "noon", "midnight", "end of day", "end of month", "2026-12-24 08:00", "now". Used by
    /// <c>BackgroundJob.Schedule(..., "tomorrow at 9am")</c> and the dashboard's run form.
    /// </summary>
    public static class TimeText
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex Relative = new Regex(@"^(?:in\s+)?(\d+(?:\.\d+)?|an?)\s*(seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|w|months?)(?:\s+from\s+now)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TimeOfDay = new Regex(@"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, DayOfWeek> Days = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday, ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday,
            ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday,
            ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday,
            ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
        };

        /// <summary>The time zone phrases like "tomorrow at 9" are read in. Default: the machine's local zone.</summary>
        public static TimeZoneInfo DefaultTimeZone { get; set; } = TimeZoneInfo.Local;

        /// <summary>Parses a phrase relative to now, in <see cref="DefaultTimeZone"/>. Returns UTC.</summary>
        public static DateTime Parse([NotNull] string text)
        {
            return Parse(text, DateTime.UtcNow, DefaultTimeZone);
        }

        /// <summary>Parses a phrase relative to <paramref name="utcNow"/>, in the given time zone. Returns UTC.</summary>
        /// <exception cref="FormatException">The phrase is not understood or lies in the past.</exception>
        public static DateTime Parse([NotNull] string text, DateTime utcNow, [CanBeNull] TimeZoneInfo? timeZone = null)
        {
            if (!TryParse(text, utcNow, timeZone, out var result))
            {
                throw new FormatException($"Unable to understand the time '{text}'. Try \"in 20 minutes\", \"tomorrow at 9am\" or \"2026-12-24 08:00\".");
            }

            return result;
        }

        /// <summary>Parses a phrase relative to <paramref name="utcNow"/>, in the given time zone. The result is UTC and never in the past.</summary>
        public static bool TryParse([NotNull] string text, DateTime utcNow, [CanBeNull] TimeZoneInfo? timeZone, out DateTime result)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            timeZone ??= DefaultTimeZone;
            utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            result = default;

            var phrase = Whitespace.Replace(text.Trim(), " ").ToLowerInvariant();
            if (phrase.Length == 0) return false;

            if (phrase == "now" || phrase == "immediately" || phrase == "asap")
            {
                result = utcNow;
                return true;
            }

            var relative = Relative.Match(phrase);
            if (relative.Success)
            {
                var amount = relative.Groups[1].Value is "a" or "an" ? 1 : Double.Parse(relative.Groups[1].Value, CultureInfo.InvariantCulture);
                var unit = relative.Groups[2].Value;
                TimeSpan delay;

                if (unit.StartsWith("s", StringComparison.Ordinal)) delay = TimeSpan.FromSeconds(amount);
                else if (unit.StartsWith("mo", StringComparison.Ordinal)) { result = AddMonths(utcNow, (int)amount, timeZone); return true; }
                else if (unit.StartsWith("m", StringComparison.Ordinal)) delay = TimeSpan.FromMinutes(amount);
                else if (unit.StartsWith("h", StringComparison.Ordinal)) delay = TimeSpan.FromHours(amount);
                else if (unit.StartsWith("d", StringComparison.Ordinal)) delay = TimeSpan.FromDays(amount);
                else delay = TimeSpan.FromDays(amount * 7);

                result = utcNow.Add(delay);
                return true;
            }

            // Absolute dates: "2026-12-24", "2026-12-24 08:00", "2026-12-24T08:00:00Z"
            if (Char.IsDigit(phrase[0]) && phrase.Length >= 8 && phrase.Contains("-"))
            {
                if (DateTime.TryParse(phrase, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var absolute))
                {
                    var hasZone = phrase.EndsWith("z", StringComparison.Ordinal) || phrase.Contains("+") || phrase.LastIndexOf('-') > 9;
                    result = hasZone ? DateTime.SpecifyKind(absolute, DateTimeKind.Utc) : ToUtc(DateTime.SpecifyKind(absolute, DateTimeKind.Unspecified), timeZone);
                    return result >= utcNow;
                }

                return false;
            }

            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
            var tokens = phrase.Split(' ').Where(static t => t != "on" && t != "the").ToList();
            DateTime? date = null;
            TimeSpan? time = null;
            var nextWeek = false;

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                switch (token)
                {
                    case "today": date = local.Date; continue;
                    case "tomorrow": date = local.Date.AddDays(1); continue;
                    case "tonight": date = local.Date; time ??= TimeSpan.FromHours(20); continue;
                    case "noon": case "midday": time = TimeSpan.FromHours(12); continue;
                    case "midnight": time = TimeSpan.Zero; date ??= local.Date.AddDays(1); continue;
                    case "morning": time ??= TimeSpan.FromHours(9); continue;
                    case "afternoon": time ??= TimeSpan.FromHours(14); continue;
                    case "evening": time ??= TimeSpan.FromHours(18); continue;
                    case "next": nextWeek = true; continue;
                    case "this": continue;
                    case "at": continue;
                    case "end":
                        if (i + 2 < tokens.Count && tokens[i + 1] == "of")
                        {
                            var what = tokens[i + 2];
                            i += 2;
                            if (what is "day" or "today") { date = local.Date; time = new TimeSpan(23, 59, 0); continue; }
                            if (what == "week") { date = local.Date.AddDays(((int)DayOfWeek.Sunday - (int)local.DayOfWeek + 7) % 7); time = new TimeSpan(23, 59, 0); continue; }
                            if (what == "month") { date = new DateTime(local.Year, local.Month, DateTime.DaysInMonth(local.Year, local.Month)); time = new TimeSpan(23, 59, 0); continue; }
                            if (what == "year") { date = new DateTime(local.Year, 12, 31); time = new TimeSpan(23, 59, 0); continue; }
                        }
                        return false;
                }

                if (token == "week" && nextWeek)
                {
                    date = local.Date.AddDays(((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7 is 0 ? 7 : ((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7);
                    time ??= TimeSpan.FromHours(9);
                    continue;
                }

                if (token == "month" && nextWeek)
                {
                    date = new DateTime(local.Year, local.Month, 1).AddMonths(1);
                    time ??= TimeSpan.FromHours(9);
                    continue;
                }

                if (Days.TryGetValue(token, out var day))
                {
                    // "friday" and "next friday" both mean the coming Friday; today's name means a week from now unless a time follows
                    var ahead = ((int)day - (int)local.DayOfWeek + 7) % 7;
                    if (ahead == 0 && (nextWeek || time == null)) ahead = 7;
                    date = local.Date.AddDays(ahead);
                    continue;
                }

                // "9", "9:30", "9am", "17:30"; "9 pm" arrives as two tokens
                var timeText = token;
                if (i + 1 < tokens.Count && (tokens[i + 1] == "am" || tokens[i + 1] == "pm"))
                {
                    timeText += tokens[i + 1];
                    i++;
                }

                var match = TimeOfDay.Match(timeText);
                if (!match.Success) return false;

                var hour = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var minute = match.Groups[2].Success ? Int32.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                var meridiem = match.Groups[3].Value;

                if (hour > 23 || minute > 59) return false;
                if (meridiem == "pm" && hour < 12) hour += 12;
                if (meridiem == "am" && hour == 12) hour = 0;

                time = new TimeSpan(hour, minute, 0);
            }

            if (date == null && time == null) return false;

            var when = (date ?? local.Date) + (time ?? TimeSpan.FromHours(9));

            // "at 15:00" with no date means the next 15:00
            if (date == null && when <= local) when = when.AddDays(1);

            // A day name without a time that already passed today was pushed a week; a date in the past is an error
            if (when < local.AddSeconds(-1)) return false;

            result = ToUtc(when, timeZone);
            return true;
        }

        private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            if (timeZone.IsInvalidTime(local))
            {
                local = local.AddHours(1);
            }

            return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        }

        private static DateTime AddMonths(DateTime utcNow, int months, TimeZoneInfo timeZone)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone).AddMonths(months);
            return ToUtc(local, timeZone);
        }
    }
}
