#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Masterly.NightShift
{
    /// <summary>
    /// Turns English schedule phrases into CRON expressions: "every 15 minutes", "daily at 3:30 pm",
    /// "weekdays at 9", "every monday and friday at 08:00", "every 10 minutes between 9 and 17 on weekdays",
    /// "monthly on the 1st at midnight", "last day of month at 23:00", "every 30 seconds".
    /// </summary>
    /// <remarks>
    /// The grammar is: an optional interval (<c>every [N] second|minute|hour|day|week|month|year</c>,
    /// <c>hourly|daily|weekly|monthly|yearly</c>, <c>weekdays|weekends</c>, a list of day names), then any of
    /// <c>at TIME[, TIME]</c>, <c>on DAYS</c>, <c>on the Nth</c> / <c>on day N</c>, <c>between H and H</c>
    /// or <c>from H to H</c>, in any order. A phrase that already is a CRON expression is returned unchanged.
    /// For hourly schedules a time gives the minute (<c>hourly at 15</c> is minute 15, <c>every 2 hours at 9:30</c>
    /// minute 30). An hour range that ends before it starts runs overnight: <c>between 22 and 6</c> is <c>22-6</c>. Second and minute intervals take no time, and day schedules no hour range with their times:
    /// use <c>between</c> to limit the hours of an interval.
    /// </remarks>
    internal static class CronTextParser
    {
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex Ordinal = new(@"^(\d{1,2})(st|nd|rd|th)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Time = new(@"^(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, int> Days = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sunday"] = 0, ["sun"] = 0, ["sundays"] = 0,
            ["monday"] = 1, ["mon"] = 1, ["mondays"] = 1,
            ["tuesday"] = 2, ["tue"] = 2, ["tues"] = 2, ["tuesdays"] = 2,
            ["wednesday"] = 3, ["wed"] = 3, ["wednesdays"] = 3,
            ["thursday"] = 4, ["thu"] = 4, ["thur"] = 4, ["thurs"] = 4, ["thursdays"] = 4,
            ["friday"] = 5, ["fri"] = 5, ["fridays"] = 5,
            ["saturday"] = 6, ["sat"] = 6, ["saturdays"] = 6,
        };

        private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
        {
            ["january"] = 1, ["jan"] = 1, ["february"] = 2, ["feb"] = 2, ["march"] = 3, ["mar"] = 3,
            ["april"] = 4, ["apr"] = 4, ["may"] = 5, ["june"] = 6, ["jun"] = 6, ["july"] = 7, ["jul"] = 7,
            ["august"] = 8, ["aug"] = 8, ["september"] = 9, ["sep"] = 9, ["sept"] = 9, ["october"] = 10, ["oct"] = 10,
            ["november"] = 11, ["nov"] = 11, ["december"] = 12, ["dec"] = 12,
        };

        private static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
            ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30,
            ["other"] = 2,
        };

        public static string Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var normalized = Whitespace.Replace(text.Trim(), " ");
            if (normalized.Length == 0) throw new FormatException("The schedule text is empty.");

            if (LooksLikeCron(normalized))
            {
                RecurringJobEntity.ParseCronExpression(normalized);
                return normalized;
            }

            var tokens = Tokenize(normalized);
            var schedule = new Schedule();
            var position = 0;

            ParseInterval(tokens, ref position, schedule);

            while (position < tokens.Count)
            {
                if (!ParseModifier(tokens, ref position, schedule))
                {
                    throw new FormatException($"Unable to understand '{tokens[position]}' in the schedule '{text}'.");
                }
            }

            var cron = schedule.ToCron();
            RecurringJobEntity.ParseCronExpression(cron);
            return cron;
        }

        private static bool LooksLikeCron(string text)
        {
            if (text.StartsWith("@", StringComparison.Ordinal)) return true;

            var parts = text.Split(' ');
            if (parts.Length != 5 && parts.Length != 6) return false;

            // Every field must be made of CRON atoms: numbers, * ? L W #, ranges, steps, lists, or
            // three-letter day and month names. English words like "daily" or "at" are not.
            return parts.All(static part => part.Split(',', '-', '/').All(IsCronAtom));
        }

        private static bool IsCronAtom(string atom)
        {
            if (atom.Length == 0) return false;
            if (atom == "*" || atom == "?" || atom == "L" || atom == "LW") return true;
            if (atom.All(static ch => Char.IsDigit(ch))) return true;
            if (atom.Length >= 2 && atom.Substring(0, atom.Length - 1).All(static ch => Char.IsDigit(ch)) && (atom.EndsWith("W", StringComparison.Ordinal) || atom.EndsWith("L", StringComparison.Ordinal))) return true;
            if (atom.Contains("#") && atom.Replace("#", String.Empty).All(static ch => Char.IsDigit(ch))) return true;

            return atom.Length == 3 && (Days.ContainsKey(atom) || Months.ContainsKey(atom)) && atom.All(static ch => Char.IsLetter(ch));
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var words = text.Split(' ');

            for (var i = 0; i < words.Length; i++)
            {
                var word = words[i].Trim(',', ';').ToLowerInvariant();
                if (word.Length == 0) continue;

                // "9:30pm" and "9:30 pm" are the same time, and so are "9a.m." and "9 a.m."
                if ((word == "am" || word == "pm" || word == "a.m." || word == "p.m.") && tokens.Count > 0 && Time.IsMatch(tokens[tokens.Count - 1] + " " + word))
                {
                    tokens[tokens.Count - 1] = tokens[tokens.Count - 1] + word;
                    continue;
                }

                if (word == "each") { tokens.Add("every"); continue; }
                if (word == "a" || word == "an") { tokens.Add("1"); continue; }

                tokens.Add(word);
            }

            return tokens;
        }

        private static void ParseInterval(List<string> tokens, ref int position, Schedule schedule)
        {
            if (position >= tokens.Count) return;

            var token = tokens[position];

            switch (token)
            {
                case "hourly": schedule.Unit = Unit.Hour; position++; return;
                case "daily": case "everyday": schedule.Unit = Unit.Day; position++; return;
                case "weekly": schedule.Unit = Unit.Week; position++; return;
                case "monthly": schedule.Unit = Unit.Month; position++; return;
                case "yearly": case "annually": schedule.Unit = Unit.Year; position++; return;
                case "weekdays": schedule.Unit = Unit.Day; schedule.DaysOfWeek = "1-5"; position++; return;
                case "weekends": schedule.Unit = Unit.Day; schedule.DaysOfWeek = "0,6"; position++; return;
                case "midnight": case "noon": schedule.Unit = Unit.Day; return; // "midnight" alone: parsed as a time below
                case "at": case "on": case "between": case "from": schedule.Unit = Unit.Day; return;
            }

            if (token == "last" && Peek(tokens, position + 1) == "day")
            {
                // "last day of [every|the] month [at ...]"
                position += 2;
                if (Peek(tokens, position) == "of") position++;
                if (Peek(tokens, position) == "every" || Peek(tokens, position) == "the") position++;
                if (Peek(tokens, position) == "month") position++;
                schedule.Unit = Unit.Month;
                schedule.DayOfMonth = "L";
                return;
            }

            if (token == "first" && Peek(tokens, position + 1) == "day")
            {
                position += 2;
                if (Peek(tokens, position) == "of") position++;
                if (Peek(tokens, position) == "every" || Peek(tokens, position) == "the") position++;
                if (Peek(tokens, position) == "month") position++;
                schedule.Unit = Unit.Month;
                schedule.DayOfMonth = "1";
                return;
            }

            if (Time.IsMatch(token))
            {
                // "9:30 on weekends": the time itself is parsed by the modifiers
                schedule.Unit = Unit.Day;
                return;
            }

            if (Days.ContainsKey(token))
            {
                // "monday and friday at 9"
                schedule.Unit = Unit.Day;
                schedule.DaysOfWeek = ParseDayList(tokens, ref position);
                return;
            }

            if (token != "every")
            {
                throw new FormatException($"A schedule should start with 'every', 'daily', 'weekly', a day of week or a time, not '{token}'.");
            }

            position++;
            if (position >= tokens.Count) throw new FormatException("'every' needs a unit, for example 'every 5 minutes'.");

            var step = 1;
            if (TryParseNumber(tokens[position], out var number))
            {
                step = number;
                position++;
                if (position >= tokens.Count) throw new FormatException("'every N' needs a unit, for example 'every 5 minutes'.");
            }

            if (step <= 0) throw new FormatException("The interval should be greater than zero.");

            token = tokens[position];

            if (Days.ContainsKey(token))
            {
                schedule.Unit = Unit.Day;
                schedule.DaysOfWeek = ParseDayList(tokens, ref position);
                return;
            }

            position++;

            switch (TrimPlural(token))
            {
                case "second": case "sec": schedule.Unit = Unit.Second; break;
                case "minute": case "min": schedule.Unit = Unit.Minute; break;
                case "hour": case "hr": case "hrs": schedule.Unit = Unit.Hour; break;
                case "day": schedule.Unit = Unit.Day; break;
                case "weekday": schedule.Unit = Unit.Day; schedule.DaysOfWeek = "1-5"; break;
                case "weekend": schedule.Unit = Unit.Day; schedule.DaysOfWeek = "0,6"; break;
                case "week": schedule.Unit = Unit.Week; break;
                case "month": schedule.Unit = Unit.Month; break;
                case "year": schedule.Unit = Unit.Year; break;
                default: throw new FormatException($"Unknown unit '{token}'. Use seconds, minutes, hours, days, weeks, months or years.");
            }

            schedule.Step = step;
        }

        private static bool ParseModifier(List<string> tokens, ref int position, Schedule schedule)
        {
            var token = tokens[position];

            if (token == "at")
            {
                position++;
                schedule.Times.AddRange(ParseTimeList(tokens, ref position));
                return true;
            }

            if (token == "midnight" || token == "noon" || Time.IsMatch(token))
            {
                schedule.Times.AddRange(ParseTimeList(tokens, ref position));
                return true;
            }

            if (token == "on" || token == "in")
            {
                position++;
                if (position >= tokens.Count) throw new FormatException("'on' needs days, for example 'on weekdays' or 'on the 15th'.");

                token = tokens[position];

                if (token == "weekdays") { schedule.DaysOfWeek = "1-5"; position++; return true; }
                if (token == "weekends") { schedule.DaysOfWeek = "0,6"; position++; return true; }
                if (Days.ContainsKey(token)) { schedule.DaysOfWeek = ParseDayList(tokens, ref position); return true; }
                if (Months.TryGetValue(token, out var month))
                {
                    schedule.Month = month.ToString(CultureInfo.InvariantCulture);
                    position++;

                    if (position < tokens.Count && Ordinal.IsMatch(tokens[position]))
                    {
                        schedule.DayOfMonth = ParseDayOfMonth(tokens[position]);
                        position++;
                    }

                    return true;
                }

                if (token == "the") { position++; token = Peek(tokens, position) ?? String.Empty; }
                if (token == "day") { position++; token = Peek(tokens, position) ?? String.Empty; }

                if (token == "last") { schedule.DayOfMonth = "L"; position++; if (Peek(tokens, position) == "day") position++; return true; }

                if (Ordinal.IsMatch(token))
                {
                    schedule.DayOfMonth = ParseDayOfMonth(token);
                    position++;
                    if (Peek(tokens, position) == "day") position++;
                    return true;
                }

                throw new FormatException($"Unable to understand the days '{token}'.");
            }

            if (token == "between" || token == "from")
            {
                position++;
                var start = ParseHour(tokens, ref position);
                var separator = Peek(tokens, position);
                if (separator != "to" && separator != "until" && separator != "till" && separator != "and")
                {
                    throw new FormatException("'between' needs two hours, for example 'between 9 and 17'.");
                }

                position++;
                var end = ParseHour(tokens, ref position);

                // An end before the start wraps past midnight: "between 22 and 6" is 22-6, which CRON runs overnight
                schedule.Hours = start == end ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{end}";
                return true;
            }

            if (token == "of" && Peek(tokens, position + 1) is { } next && (next == "every" || next == "the" || next == "month"))
            {
                position++;
                if (Peek(tokens, position) == "every" || Peek(tokens, position) == "the") position++;
                if (Peek(tokens, position) == "month") position++;
                return true;
            }

            return false;
        }

        private static string ParseDayList(List<string> tokens, ref int position)
        {
            var days = new SortedSet<int>();

            while (position < tokens.Count && Days.TryGetValue(tokens[position], out var day))
            {
                days.Add(day);
                position++;

                if (Peek(tokens, position) == "and" && position + 1 < tokens.Count && Days.ContainsKey(tokens[position + 1]))
                {
                    position++;
                }

                if (Peek(tokens, position) == "to" && position + 1 < tokens.Count && Days.TryGetValue(tokens[position + 1], out var end))
                {
                    for (var d = day; d != end; d = (d + 1) % 7) days.Add(d);
                    days.Add(end);
                    position += 2;
                }
            }

            return String.Join(",", days.Select(static x => x.ToString(CultureInfo.InvariantCulture)));
        }

        private static List<TimeOfDay> ParseTimeList(List<string> tokens, ref int position)
        {
            var times = new List<TimeOfDay>();

            while (position < tokens.Count)
            {
                var token = tokens[position];

                if (token == "and" && times.Count > 0 && position + 1 < tokens.Count
                    && (tokens[position + 1] == "midnight" || tokens[position + 1] == "noon" || Time.IsMatch(tokens[position + 1])))
                {
                    position++;
                    continue;
                }

                if (token == "midnight") { times.Add(new TimeOfDay(0, 0, token)); position++; continue; }
                if (token == "noon") { times.Add(new TimeOfDay(12, 0, token)); position++; continue; }

                var match = Time.Match(token);
                if (!match.Success) break;

                var hour = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var minute = match.Groups[2].Success ? Int32.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                var meridiem = match.Groups[3].Value.Replace(".", String.Empty).ToLowerInvariant();

                if (meridiem == "pm" && hour < 12) hour += 12;
                if (meridiem == "am" && hour == 12) hour = 0;

                // A bare number is an hour of the day, except for hourly schedules, where "hourly at 15" means minute 15
                var isBareNumber = !match.Groups[2].Success && meridiem.Length == 0;
                if (hour > (isBareNumber ? 59 : 23) || minute > 59) throw new FormatException($"'{token}' is not a valid time.");

                times.Add(new TimeOfDay(hour, minute, token, isBareNumber));
                position++;
            }

            if (times.Count == 0) throw new FormatException("'at' needs a time, for example 'at 9:30' or 'at 5 pm'.");

            return times;
        }

        private static int ParseHour(List<string> tokens, ref int position)
        {
            if (position >= tokens.Count) throw new FormatException("An hour is missing.");

            var token = tokens[position];
            (int Hour, int Minute) time;

            if (token == "midnight") time = (0, 0);
            else if (token == "noon") time = (12, 0);
            else
            {
                var match = Time.Match(token);
                if (!match.Success) throw new FormatException($"'{token}' is not an hour.");

                var hour = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var minute = match.Groups[2].Success ? Int32.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                var meridiem = match.Groups[3].Value.Replace(".", String.Empty).ToLowerInvariant();

                if (meridiem == "pm" && hour < 12) hour += 12;
                if (meridiem == "am" && hour == 12) hour = 0;

                time = (hour, minute);
            }

            if (time.Minute != 0 || time.Hour > 23) throw new FormatException("Hour ranges should use whole hours, for example 'between 9 and 17'.");

            position++;
            return time.Hour;
        }

        private static string ParseDayOfMonth(string token)
        {
            var match = Ordinal.Match(token);
            var day = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (day < 1 || day > 31) throw new FormatException($"'{token}' is not a day of month.");

            return day.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryParseNumber(string token, out int value)
        {
            if (Int32.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            return Numbers.TryGetValue(token, out value);
        }

        private static string TrimPlural(string token)
        {
            return token.EndsWith("s", StringComparison.Ordinal) && token.Length > 3 ? token.Substring(0, token.Length - 1) : token;
        }

        private static string? Peek(List<string> tokens, int position)
        {
            return position < tokens.Count ? tokens[position] : null;
        }

        private enum Unit
        {
            Second,
            Minute,
            Hour,
            Day,
            Week,
            Month,
            Year,
        }

        /// <summary>A time from the phrase, with the text it came from.</summary>
        private readonly struct TimeOfDay
        {
            public TimeOfDay(int hour, int minute, string text, bool isBareNumber = false)
            {
                Hour = hour;
                Minute = minute;
                Text = text;
                IsBareNumber = isBareNumber;
            }

            public int Hour { get; }
            public int Minute { get; }
            public string Text { get; }

            /// <summary>Written as a plain number ("at 15"), with no minutes or am/pm.</summary>
            public bool IsBareNumber { get; }
        }

        private sealed class Schedule
        {
            public Unit Unit { get; set; } = Unit.Day;
            public int Step { get; set; } = 1;
            public List<TimeOfDay> Times { get; } = new();
            public string? Hours { get; set; }
            public string? DaysOfWeek { get; set; }
            public string? DayOfMonth { get; set; }
            public string? Month { get; set; }

            public string ToCron()
            {
                var minute = "0";
                var hour = "0";
                var dayOfMonth = DayOfMonth ?? "*";
                var month = Month ?? "*";
                var dayOfWeek = DaysOfWeek ?? "*";
                string? second = null;

                if ((Unit == Unit.Second || Unit == Unit.Minute) && Times.Count > 0)
                {
                    // "every 15 minutes at 9" could mean during 09:00-09:59 or from 09:00 on; 'between' says which
                    var unit = Unit == Unit.Second ? "seconds" : "minutes";
                    throw new FormatException($"'every N {unit}' can't take a time like '{Times[0].Text}'. Use 'between' to limit the hours, for example 'every 15 {unit} between 9 and 17'.");
                }

                switch (Unit)
                {
                    case Unit.Second:
                        second = Step == 1 ? "*" : $"*/{Step}";
                        minute = "*";
                        hour = Hours ?? "*";
                        break;

                    case Unit.Minute:
                        minute = Step == 1 ? "*" : $"*/{Step}";
                        hour = Hours ?? "*";
                        break;

                    case Unit.Hour:
                        // "hourly at 15" is minute 15; a full time such as "9:30" or "9am" gives its minute
                        minute = Times.Count > 0 ? JoinDistinct(Times.Select(static t => t.IsBareNumber ? t.Hour : t.Minute)) : "0";
                        hour = Step == 1 || (Hours != null && Hours.IndexOf('-') < 0)
                            ? Hours ?? "*"
                            : $"{Hours ?? "*"}/{Step}";
                        break;

                    case Unit.Day:
                        ApplyTimes(ref minute, ref hour);
                        if (Step > 1) dayOfMonth = $"*/{Step}";
                        break;

                    case Unit.Week:
                        ApplyTimes(ref minute, ref hour);
                        if (dayOfWeek == "*") dayOfWeek = "1";
                        if (Step > 1) throw new FormatException("CRON can't express 'every N weeks'; schedule a weekly job and skip runs in the job instead.");
                        break;

                    case Unit.Month:
                        ApplyTimes(ref minute, ref hour);
                        if (DayOfMonth == null) dayOfMonth = "1";
                        if (Step > 1) month = $"*/{Step}";
                        break;

                    case Unit.Year:
                        ApplyTimes(ref minute, ref hour);
                        if (DayOfMonth == null) dayOfMonth = "1";
                        if (Month == null) month = "1";
                        if (Step > 1) throw new FormatException("CRON can't express 'every N years'.");
                        break;
                }

                var cron = $"{minute} {hour} {dayOfMonth} {month} {dayOfWeek}";
                return second != null ? $"{second} {cron}" : cron;
            }

            private void ApplyTimes(ref string minute, ref string hour)
            {
                if (Times.Count == 0)
                {
                    if (Hours != null) hour = Hours;
                    return;
                }

                if (Hours != null)
                {
                    throw new FormatException("A time of day and an hour range can't be combined; 'between' is for intervals such as 'every 15 minutes between 9 and 17'.");
                }

                foreach (var time in Times)
                {
                    if (time.Hour > 23) throw new FormatException($"'{time.Text}' is not a valid time.");
                }

                var minutes = Times.Select(static t => t.Minute).Distinct().ToList();
                if (minutes.Count > 1)
                {
                    throw new FormatException("Several times a day are supported only when they share the same minute, for example 'at 9:30 and 17:30'.");
                }

                minute = minutes[0].ToString(CultureInfo.InvariantCulture);
                hour = JoinDistinct(Times.Select(static t => t.Hour));
            }

            private static string JoinDistinct(IEnumerable<int> values)
            {
                return String.Join(",", values.Distinct().OrderBy(static x => x).Select(static x => x.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }
}
