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
    /// <remarks>
    /// With "tonight", "afternoon" or "evening", an hour from 1 to 11 written without am or pm is in the afternoon:
    /// "tonight at 9" is 21:00 and "tomorrow afternoon at 3" is 15:00. "12" is noon in the afternoon and the midnight
    /// that ends the evening with "tonight" or "evening", and so is "midnight" there: "tomorrow evening at midnight" is
    /// 00:00 the day after tomorrow. An explicit am or pm, and a 24-hour time such as "19:15" or "07:30", are used as
    /// written; "morning" keeps the hour.
    /// </remarks>
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

        // "in 99999999 days" is refused rather than overflowing: relative phrases reach at most ten years ahead
        private static readonly TimeSpan MaxRelativeDelay = TimeSpan.FromDays(3653);
        private const int MaxRelativeMonths = 120;

        /// <summary>The time zone phrases like "tomorrow at 9" are read in. Default: the machine's local zone.</summary>
        public static TimeZoneInfo DefaultTimeZone { get; set; } = TimeZoneInfo.Local;

        /// <summary>Parses a phrase relative to now, in <see cref="DefaultTimeZone"/>. Returns UTC.</summary>
        public static DateTime Parse([NotNull] string text)
        {
            return Parse(text, DateTime.UtcNow, DefaultTimeZone);
        }

        /// <summary>Parses a phrase relative to <paramref name="utcNow"/>, in the given time zone. Returns UTC.</summary>
        /// <exception cref="FormatException">
        /// The phrase is not understood, lies in the past, or asks for a part of a month ("in 1.5 months").
        /// </exception>
        public static DateTime Parse([NotNull] string text, DateTime utcNow, [CanBeNull] TimeZoneInfo? timeZone = null)
        {
            if (!TryParse(text, utcNow, timeZone, out var result, out var error))
            {
                throw new FormatException(error ?? $"Unable to understand the time '{text}'. Try \"in 20 minutes\", \"tomorrow at 9am\" or \"2026-12-24 08:00\".");
            }

            return result;
        }

        /// <summary>
        /// Parses a phrase relative to <paramref name="utcNow"/>, in the given time zone. The result is UTC and never in the
        /// past. Returns <c>false</c> for a phrase it doesn't understand, one in the past, a part of a month ("in 1.5
        /// months"), and a relative phrase more than ten years ahead ("in 99999999 days").
        /// </summary>
        public static bool TryParse([NotNull] string text, DateTime utcNow, [CanBeNull] TimeZoneInfo? timeZone, out DateTime result)
        {
            return TryParse(text, utcNow, timeZone, out result, out _);
        }

        // The error, when there is one more useful than "unable to understand", is the message Parse throws
        private static bool TryParse(string text, DateTime utcNow, TimeZoneInfo? timeZone, out DateTime result, out string? error)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            try
            {
                return TryParseCore(text, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone ?? DefaultTimeZone, out result, out error);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is OverflowException)
            {
                // A moment beyond DateTime's range ("end of year" in year 9999, for example) is not a valid answer
                result = default;
                error = null;
                return false;
            }
        }

        private static bool TryParseCore(string text, DateTime utcNow, TimeZoneInfo timeZone, out DateTime result, out string? error)
        {
            result = default;
            error = null;

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
                double seconds;

                if (unit.StartsWith("s", StringComparison.Ordinal)) seconds = amount;
                else if (unit.StartsWith("mo", StringComparison.Ordinal))
                {
                    if (amount > MaxRelativeMonths) return false;

                    // Months differ in length, so "in 1.5 months" has no single answer
                    if (amount != Math.Floor(amount))
                    {
                        error = $"Unable to use the time '{text}': months must be a whole number. Use days or weeks for a part of a month, for example \"in 45 days\".";
                        return false;
                    }

                    result = AddMonths(utcNow, (int)amount, timeZone);
                    return true;
                }
                else if (unit.StartsWith("m", StringComparison.Ordinal)) seconds = amount * 60;
                else if (unit.StartsWith("h", StringComparison.Ordinal)) seconds = amount * 3600;
                else if (unit.StartsWith("d", StringComparison.Ordinal)) seconds = amount * 86400;
                else seconds = amount * 7 * 86400;

                if (seconds > MaxRelativeDelay.TotalSeconds) return false;

                result = utcNow.AddSeconds(seconds);
                return true;
            }

            // Absolute dates: "2026-12-24", "2026-12-24 08:00", "2026-12-24T08:00:00Z"
            if (Char.IsDigit(phrase[0]) && phrase.Length >= 8 && phrase.Contains("-"))
            {
                if (DateTime.TryParse(phrase, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var absolute))
                {
                    var hasZone = phrase.EndsWith("z", StringComparison.Ordinal) || phrase.Contains("+") || phrase.LastIndexOf('-') > 9;
                    result = hasZone ? DateTime.SpecifyKind(absolute, DateTimeKind.Utc) : ToUtc(DateTime.SpecifyKind(absolute, DateTimeKind.Unspecified), timeZone);
                    if (result >= utcNow) return true;

                    error = $"The time '{text}' is in the past.";
                    return false;
                }

                return false;
            }

            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
            var tokens = phrase.Split(' ').Where(static t => t != "on" && t != "the").ToList();
            DateTime? date = null;
            TimeSpan? time = null;
            var nextWeek = false;
            var todayByName = false;

            // "tonight" and "this evening" mean today, like "today at 20:00": once that time has passed they are
            // refused, while a time of day without a day ("evening", "at 18:00") moves to tomorrow
            string? todayByWord = null;

            // "tonight at 9" and "tomorrow afternoon at 3" mean 21:00 and 15:00: an hour from 1 to 12 written without
            // am or pm takes the part of the day the phrase names (see AfterNoon below)
            int? bareHour = null;
            string? partOfDay = null;

            // "midnight" ends the day, or the evening the phrase names (settled after the loop)
            var midnight = false;

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                switch (token)
                {
                    case "today": date = local.Date; continue;
                    case "tomorrow": date = local.Date.AddDays(1); continue;
                    case "tonight": date = local.Date; time ??= TimeSpan.FromHours(20); todayByWord = token; partOfDay = "evening"; continue;
                    case "noon": case "midday": time = TimeSpan.FromHours(12); bareHour = null; midnight = false; continue;
                    case "midnight": time = TimeSpan.Zero; bareHour = null; midnight = true; continue;
                    case "morning": time ??= TimeSpan.FromHours(9); partOfDay ??= token; continue;
                    case "afternoon": time ??= TimeSpan.FromHours(14); if (partOfDay != "evening") partOfDay = token; continue;
                    case "evening": time ??= TimeSpan.FromHours(18); partOfDay = token; continue;
                    case "next": nextWeek = true; continue;
                    case "this":
                        if (i + 1 < tokens.Count && tokens[i + 1] is "morning" or "afternoon" or "evening")
                        {
                            date ??= local.Date;
                            todayByWord = $"this {tokens[i + 1]}";
                        }

                        continue;
                    case "at": continue;
                    case "end":
                        if (i + 2 < tokens.Count && tokens[i + 1] == "of")
                        {
                            var what = tokens[i + 2];
                            i += 2;
                            bareHour = null;
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
                    // "friday" and "next friday" both mean the coming Friday. Today's name is settled after the loop,
                    // because the time that decides it can come before or after the name
                    var ahead = ((int)day - (int)local.DayOfWeek + 7) % 7;
                    if (ahead == 0 && nextWeek) ahead = 7;
                    date = local.Date.AddDays(ahead);
                    todayByName = ahead == 0;
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
                midnight = false;

                // "07:30" with a leading zero is a 24-hour time, written as meant
                bareHour = meridiem.Length == 0 && hour >= 1 && hour <= 12 && match.Groups[1].Value[0] != '0' ? hour : null;
            }

            if (date == null && time == null) return false;

            if (bareHour != null && time != null && partOfDay is "afternoon" or "evening")
            {
                time = AfterNoon(bareHour.Value, partOfDay, time.Value);
            }

            // "midnight" ends the day: 00:00 the next day. With "tonight" or "evening" it is the midnight that ends that
            // evening, like "12": "tomorrow evening at midnight" is 00:00 the day after tomorrow
            if (midnight)
            {
                if (partOfDay == "evening") time = TimeSpan.FromHours(24);
                else date ??= local.Date.AddDays(1);
            }

            // "tonight at 00:00" is the midnight that ends tonight too
            if (todayByWord == "tonight" && time == TimeSpan.Zero && date == local.Date)
            {
                date = local.Date.AddDays(1);
            }

            // Today's name means today when the phrase gives a time still ahead today ("wednesday at 18:00",
            // "18:00 wednesday"), and a week from now otherwise ("wednesday", "wednesday at 9am" after 9am)
            if (todayByName && date == local.Date && (time == null || local.Date + time.Value <= local))
            {
                date = local.Date.AddDays(7);
            }

            var when = (date ?? local.Date) + (time ?? TimeSpan.FromHours(9));

            // "at 15:00" with no date means the next 15:00
            if (date == null && when <= local) when = when.AddDays(1);

            // A day name without a time that already passed today was pushed a week; a date in the past is an error
            if (when < local.AddSeconds(-1))
            {
                error = todayByWord != null && when.Date == local.Date
                    ? $"The time '{text}' is in the past: '{todayByWord}' is {when.ToString("HH:mm", CultureInfo.InvariantCulture)} today. Add a time still ahead today, or name another day, such as \"tomorrow evening\"."
                    : $"The time '{text}' is in the past.";
                return false;
            }

            result = ToUtc(when, timeZone);
            return true;
        }

        /// <summary>
        /// The time of an hour from 1 to 12 written without am or pm in the afternoon or the evening: 1 to 11 are pm
        /// ("evening at 7" is 19:00). 12 is noon in the afternoon and, in the evening or tonight, the midnight that ends
        /// that evening, returned as 24 hours so it lands on the next day.
        /// </summary>
        private static TimeSpan AfterNoon(int hour, string partOfDay, TimeSpan time)
        {
            if (hour < 12) return time.Add(TimeSpan.FromHours(12));
            return partOfDay == "evening" ? time.Add(TimeSpan.FromHours(12)) : time;
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
