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
    /// Describes CRON expressions in English: <c>*/15 9-17 * * 1-5</c> becomes
    /// "Every 15 minutes, between 09:00 and 17:59, Monday to Friday". Covers the 5- and 6-field forms,
    /// steps, ranges, lists, names, <c>L</c>, <c>W</c>, <c>#</c> and the <c>@</c> presets.
    /// </summary>
    internal static class CronDescriber
    {
        private static readonly char[] FieldSeparators = { ' ', '\t' };

        private static readonly string[] DayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        private static readonly string[] MonthNames = { "", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
        private static readonly string[] Ordinals = { "", "first", "second", "third", "fourth", "fifth" };

        private static readonly Dictionary<string, string> Presets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["@yearly"] = "0 0 1 1 *", ["@annually"] = "0 0 1 1 *", ["@monthly"] = "0 0 1 * *", ["@weekly"] = "0 0 * * 0",
            ["@daily"] = "0 0 * * *", ["@midnight"] = "0 0 * * *", ["@hourly"] = "0 * * * *", ["@every_minute"] = "* * * * *",
            ["@every_second"] = "* * * * * *",
        };

        public static string Describe(string expression)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            var text = expression.Trim();
            if (Presets.TryGetValue(text, out var preset)) text = preset;

            var parts = text.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 && parts.Length != 6)
            {
                throw new FormatException("A CRON expression has 5 or 6 fields.");
            }

            var offset = parts.Length == 6 ? 1 : 0;
            var second = parts.Length == 6 ? Field.Parse(parts[0], 0, 59, null) : Field.Fixed(0);
            var minute = Field.Parse(parts[offset], 0, 59, null);
            var hour = Field.Parse(parts[offset + 1], 0, 23, null);
            var dayOfMonth = Field.Parse(parts[offset + 2], 1, 31, null, allowSpecial: true);
            var month = Field.Parse(parts[offset + 3], 1, 12, MonthNames.Skip(1).Select(static x => x.Substring(0, 3)).ToArray());
            var dayOfWeek = Field.Parse(parts[offset + 4], 0, 7, DayNames.Select(static x => x.Substring(0, 3)).ToArray(), allowSpecial: true);

            var time = DescribeTime(second, minute, hour, parts.Length == 6);
            var date = DescribeDate(dayOfMonth, month, dayOfWeek);

            var result = date.Length > 0 ? $"{time}, {date}" : time;
            return Char.ToUpperInvariant(result[0]) + result.Substring(1);
        }

        private static string DescribeTime(Field second, Field minute, Field hour, bool hasSeconds)
        {
            var hourSuffix = DescribeHourQualifier(hour);

            if (hasSeconds && (second.IsEvery || second.Step > 1))
            {
                var every = second.IsEvery ? "every second" : $"every {second.Step} seconds";

                if (minute.IsEvery) return Join(every, hourSuffix);
                if (hour.IsSpecific && minute.IsSpecific) return Join(every, $"at {DescribeTimes(hour, minute, null)}");

                return Join(every, $"during minute{(minute.Values.Count > 1 ? "s" : "")} {minute.Describe()} of the hour", hourSuffix);
            }

            if (minute.IsEvery || minute.Step > 1)
            {
                var every = minute.IsEvery ? "every minute" : $"every {minute.Step} minutes";
                return Join(every, hasSeconds && second.IsSpecific && !second.IsZero ? $"at second {second.Describe()}" : null, hourSuffix);
            }

            // Specific minutes
            if (hour.IsSpecific)
            {
                return $"at {DescribeTimes(hour, minute, hasSeconds && !second.IsZero ? second : null)}";
            }

            var atStart = minute.Values.Count == 1 && minute.Values[0] == 0;
            var past = atStart ? "at the start of"
                : minute.Values.Count == 1 ? $"at {minute.Values[0]} minute{(minute.Values[0] == 1 ? "" : "s")} past"
                : $"at minutes {minute.Describe()} past";

            if (hour.IsEvery) return $"{past} every hour";
            if (hour.Step > 1) return $"{past} every {hour.Step} hours{(hour.RangeStart.HasValue ? $" between {hour.RangeStart:00}:00 and {hour.RangeEnd:00}:59" : "")}";

            return Join(past + " the hour", hourSuffix);
        }

        private static string? DescribeHourQualifier(Field hour)
        {
            if (hour.IsEvery) return null;

            if (hour.Step > 1)
            {
                return hour.RangeStart.HasValue
                    ? $"every {hour.Step} hours between {hour.RangeStart:00}:00 and {hour.RangeEnd:00}:59"
                    : $"every {hour.Step} hours";
            }

            if (hour.RangeStart.HasValue && hour.Values.Count == hour.RangeEnd - hour.RangeStart + 1)
            {
                return $"between {hour.RangeStart:00}:00 and {hour.RangeEnd:00}:59";
            }

            return hour.Values.Count == 1
                ? $"during hour {hour.Values[0]:00}"
                : $"during hours {hour.Describe()}";
        }

        private static string DescribeTimes(Field hour, Field minute, Field? second)
        {
            if (hour.Values.Count * minute.Values.Count <= 6)
            {
                var times = new List<string>();
                foreach (var h in hour.Values)
                foreach (var m in minute.Values)
                {
                    times.Add(second != null && second.Values.Count == 1
                        ? $"{h:00}:{m:00}:{second.Values[0]:00}"
                        : $"{h:00}:{m:00}");
                }

                var text = JoinList(times);
                return second != null && second.Values.Count > 1 ? $"{text} (seconds {second.Describe()})" : text;
            }

            return $"minutes {minute.Describe()} past hours {hour.Describe()}";
        }

        private static string DescribeDate(Field dayOfMonth, Field month, Field dayOfWeek)
        {
            var parts = new List<string>();

            var domText = DescribeDayOfMonth(dayOfMonth);
            var dowText = DescribeDayOfWeek(dayOfWeek);

            if (domText != null && dowText != null) parts.Add($"{domText} or {dowText}");
            else if (domText != null) parts.Add(domText);
            else if (dowText != null) parts.Add(dowText);

            if (!month.IsEvery)
            {
                if (month.Step > 1)
                {
                    parts.Add($"every {month.Step} months");
                }
                else if (month.RangeStart.HasValue && month.Values.Count == month.RangeEnd - month.RangeStart + 1)
                {
                    parts.Add($"{MonthNames[month.RangeStart.Value]} to {MonthNames[month.RangeEnd!.Value]}");
                }
                else
                {
                    parts.Add("in " + JoinList(month.Values.Select(static x => MonthNames[x]).ToList()));
                }
            }

            return String.Join(", ", parts);
        }

        private static string? DescribeDayOfMonth(Field field)
        {
            if (field.IsEvery) return null;

            if (field.Special != null)
            {
                switch (field.Special)
                {
                    case "L": return "on the last day of the month";
                    case "LW": return "on the last weekday of the month";
                    default:
                        if (field.Special.EndsWith("W", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"on the weekday nearest to day {field.Special.TrimEnd('W', 'w')} of the month";
                        }

                        if (field.Special.StartsWith("L-", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"{field.Special.Substring(2)} days before the end of the month";
                        }

                        return $"on {field.Special}";
                }
            }

            if (field.Step > 1) return $"every {field.Step} days";

            if (field.RangeStart.HasValue && field.Values.Count == field.RangeEnd - field.RangeStart + 1)
            {
                return $"on days {field.RangeStart} to {field.RangeEnd} of the month";
            }

            return field.Values.Count == 1
                ? $"on day {field.Values[0]} of the month"
                : $"on days {field.Describe()} of the month";
        }

        private static string? DescribeDayOfWeek(Field field)
        {
            if (field.IsEvery) return null;

            if (field.Special != null)
            {
                var hash = field.Special.IndexOf('#');
                if (hash > 0 && Int32.TryParse(field.Special.Substring(hash + 1), out var nth) && nth >= 1 && nth <= 5)
                {
                    return $"on the {Ordinals[nth]} {DayName(field.Special.Substring(0, hash), field.Names)} of the month";
                }

                if (field.Special.EndsWith("L", StringComparison.OrdinalIgnoreCase))
                {
                    return $"on the last {DayName(field.Special.Substring(0, field.Special.Length - 1), field.Names)} of the month";
                }

                return $"on {field.Special}";
            }

            var days = field.Values.Select(static x => x % 7).Distinct().ToList();

            if (field.Step > 1) return $"every {field.Step} days of the week";

            if (days.Count == 5 && !days.Contains(0) && !days.Contains(6)) return "Monday to Friday";
            if (days.Count == 2 && days.Contains(0) && days.Contains(6)) return "on weekends";

            if (field.RangeStart.HasValue && days.Count == field.RangeEnd - field.RangeStart + 1)
            {
                return $"{DayNames[field.RangeStart.Value % 7]} to {DayNames[field.RangeEnd!.Value % 7]}";
            }

            return "on " + JoinList(days.OrderBy(static x => x).Select(static x => DayNames[x]).ToList());
        }

        private static string DayName(string token, string[]? names)
        {
            if (Int32.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return DayNames[number % 7];

            if (names != null)
            {
                var index = Array.FindIndex(names, x => x.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) return DayNames[index];
            }

            return token;
        }

        private static string Join(params string?[] parts)
        {
            return String.Join(", ", parts.Where(static x => !String.IsNullOrEmpty(x)));
        }

        private static string JoinList(IList<string> items)
        {
            if (items.Count == 1) return items[0];
            if (items.Count == 2) return $"{items[0]} and {items[1]}";
            return String.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];
        }

        /// <summary>One parsed CRON field: either a wildcard, a step, a set of values, or a special token (L, W, #).</summary>
        private sealed class Field
        {
            // L, LW, L-3, 15W, 5L, FRIL, 1#2, MON#2
            private static readonly System.Text.RegularExpressions.Regex SpecialToken =
                new(@"^(L|LW|L-\d+|\d+W|\d+L|[A-Za-z]{3}L|\d+#[1-5]|[A-Za-z]{3}#[1-5])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            public bool IsEvery { get; private set; }
            public int Step { get; private set; } = 1;
            public int? RangeStart { get; private set; }
            public int? RangeEnd { get; private set; }
            public List<int> Values { get; } = new();
            public string? Special { get; private set; }
            public string[]? Names { get; private set; }

            public bool IsSpecific => !IsEvery && Step == 1 && Special == null;
            public bool IsZero => IsSpecific && Values.Count == 1 && Values[0] == 0;

            public static Field Fixed(int value)
            {
                var field = new Field();
                field.Values.Add(value);
                return field;
            }

            public static Field Parse(string text, int min, int max, string[]? names, bool allowSpecial = false)
            {
                var field = new Field { Names = names };
                var token = text.Trim();

                if (token == "*" || token == "?")
                {
                    field.IsEvery = true;
                    return field;
                }

                // L, W and # only mean something in the day fields; elsewhere they fail as invalid values below
                if (allowSpecial && SpecialToken.IsMatch(token))
                {
                    field.Special = token.ToUpperInvariant();
                    return field;
                }

                foreach (var item in token.Split(','))
                {
                    var stepIndex = item.IndexOf('/');
                    var rangePart = stepIndex >= 0 ? item.Substring(0, stepIndex) : item;
                    var step = stepIndex >= 0 ? Int32.Parse(item.Substring(stepIndex + 1), CultureInfo.InvariantCulture) : 1;

                    int start, end;

                    if (rangePart == "*")
                    {
                        start = min;
                        end = max;
                        if (step == 1)
                        {
                            field.IsEvery = true;
                            return field;
                        }
                    }
                    else
                    {
                        var dash = rangePart.IndexOf('-');
                        if (dash > 0)
                        {
                            start = ParseValue(rangePart.Substring(0, dash), names);
                            end = ParseValue(rangePart.Substring(dash + 1), names);
                        }
                        else
                        {
                            start = ParseValue(rangePart, names);
                            end = step > 1 ? max : start;
                        }

                        if (token.IndexOf(',') < 0 && dash > 0)
                        {
                            field.RangeStart = start;
                            field.RangeEnd = end;
                        }
                    }

                    if (step > 1)
                    {
                        field.Step = step;
                        if (rangePart != "*" && token.IndexOf('-') > 0)
                        {
                            field.RangeStart = start;
                            field.RangeEnd = end;
                        }
                    }

                    for (var value = start; value <= end; value += step)
                    {
                        if (!field.Values.Contains(value)) field.Values.Add(value);
                    }
                }

                field.Values.Sort();
                return field;
            }

            public string Describe()
            {
                return Values.Count switch
                {
                    0 => String.Empty,
                    1 => Values[0].ToString(CultureInfo.InvariantCulture),
                    2 => $"{Values[0]} and {Values[1]}",
                    _ => String.Join(", ", Values.Take(Values.Count - 1)) + " and " + Values[Values.Count - 1],
                };
            }

            private static int ParseValue(string text, string[]? names)
            {
                if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;

                if (names != null)
                {
                    var index = Array.FindIndex(names, x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) return index + (names.Length == 12 ? 1 : 0);
                }

                throw new FormatException($"'{text}' is not a valid CRON field value.");
            }
        }
    }
}
