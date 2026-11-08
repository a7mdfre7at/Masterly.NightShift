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
    /// steps, ranges (including ranges that wrap around, such as <c>22-2</c> or <c>FRI-MON</c>), lists, names,
    /// <c>L</c>, <c>L-n</c>, <c>W</c>, <c>L-nW</c>, <c>#</c> and the <c>@</c> presets. When both day fields
    /// are restricted, Cronos runs only on days matching both, so <c>0 12 1 * 1</c> is described as
    /// "At 12:00, on day 1 of the month if it is a Monday". A step is "every N" only when the runs are always N apart:
    /// <c>*/45 * * * *</c> is "At minutes 0 and 45 past every hour", and <c>0 6/4 * * *</c> is
    /// "At 06:00, 10:00, 14:00, 18:00 and 22:00".
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
                var every = DescribeEvery(second, "second", "minute");

                if (minute.IsEvery) return Join(every, hourSuffix);
                if (hour.IsSpecific && minute.IsSpecific && TryDescribeTimes(hour, minute, null) is { } times) return Join(every, $"at {times}");

                return Join(every, $"during minute{(minute.Values.Count > 1 ? "s" : "")} {minute.Describe()} of the hour", hourSuffix);
            }

            if (minute.IsEvery || minute.Step > 1)
            {
                var every = DescribeEvery(minute, "minute", "hour");
                var seconds = hasSeconds && second.IsSpecific && !second.IsZero
                    ? $"at second{(second.Values.Count > 1 ? "s" : "")} {second.Describe()}"
                    : null;

                return Join(every, seconds, hourSuffix);
            }

            // Specific minutes
            if (hour.IsSpecific)
            {
                var seconds = hasSeconds && !second.IsZero ? second : null;

                var times = TryDescribeTimes(hour, minute, seconds);
                if (times != null) return $"at {times}";

                if (!hour.IsRange)
                {
                    var minutes = minute.Values.Count == 1 ? $"minute {minute.Values[0]}" : $"minutes {minute.Describe()}";
                    return $"at {minutes} past hours {hour.Describe()}";
                }

                // "0 9-17 * * *": too many runs to list, but one run an hour reads best as a span
                if (minute.Values.Count == 1 && (seconds == null || seconds.Values.Count == 1))
                {
                    return $"every hour from {FormatTime(hour.RangeStart!.Value, minute.Values[0], seconds)} to {FormatTime(hour.RangeEnd!.Value, minute.Values[0], seconds)}";
                }
            }

            var atStart = minute.Values.Count == 1 && minute.Values[0] == 0;
            var past = atStart ? "at the start of"
                : minute.Values.Count == 1 ? $"at {minute.Values[0]} minute{(minute.Values[0] == 1 ? "" : "s")} past"
                : $"at minutes {minute.Describe()} past";

            if (hour.IsEvery) return $"{past} every hour";
            if (hour.Step > 1) return $"{past} every {hour.Step} hours{(hour.RangeStart.HasValue ? $" between {hour.RangeStart:00}:00 and {hour.RangeEnd:00}:59" : "")}";

            return Join(past + " the hour", hourSuffix);
        }

        // "every 15 minutes", or, for a step that starts again at the top of its field, "every 7 minutes from minute 0
        // through 56 of the hour"
        private static string DescribeEvery(Field field, string unit, string container)
        {
            if (field.IsEvery) return $"every {unit}";

            return field.IsPartialStep
                ? $"every {field.Step} {unit}s from {unit} {field.RangeStart} through {field.RangeEnd} of the {container}"
                : $"every {field.Step} {unit}s";
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

            if (hour.RangeStart.HasValue && hour.Values.Count == hour.RangeLength)
            {
                return $"between {hour.RangeStart:00}:00 and {hour.RangeEnd:00}:59";
            }

            return hour.Values.Count == 1
                ? $"during hour {hour.Values[0]:00}"
                : $"during hours {hour.Describe()}";
        }

        /// <summary>Lists the times of day ("09:30 and 17:30"), or returns <c>null</c> when there are too many to list.</summary>
        private static string? TryDescribeTimes(Field hour, Field minute, Field? second)
        {
            if (hour.Values.Count * minute.Values.Count > 6) return null;

            var times = new List<string>();
            foreach (var h in hour.Values)
            foreach (var m in minute.Values)
            {
                times.Add(FormatTime(h, m, second));
            }

            var text = JoinList(times);
            return second != null && second.Values.Count > 1 ? $"{text} (seconds {second.Describe()})" : text;
        }

        private static string FormatTime(int hour, int minute, Field? second)
        {
            return second != null && second.Values.Count == 1
                ? $"{hour:00}:{minute:00}:{second.Values[0]:00}"
                : $"{hour:00}:{minute:00}";
        }

        private static string DescribeDate(Field dayOfMonth, Field month, Field dayOfWeek)
        {
            var parts = new List<string>();

            var domText = DescribeDayOfMonth(dayOfMonth);
            var dowText = DescribeDayOfWeek(dayOfWeek);

            // Cronos runs only on days that match both fields (unlike Vixie cron, which takes either), so say "if"
            if (domText != null && dowText != null) parts.Add($"{domText} if it is {DescribeDayOfWeekCondition(dayOfWeek)}");
            else if (domText != null) parts.Add(domText);
            else if (dowText != null) parts.Add(dowText);

            if (!month.IsEvery)
            {
                if (month.Step > 1)
                {
                    parts.Add($"every {month.Step} months");
                }
                else if (month.RangeStart.HasValue && month.Values.Count == month.RangeLength)
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
                        if (field.Special.StartsWith("L-", StringComparison.OrdinalIgnoreCase) && field.Special.EndsWith("W", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"on the weekday nearest to {DaysBeforeTheEnd(field.Special.Substring(2, field.Special.Length - 3))}";
                        }

                        if (field.Special.EndsWith("W", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"on the weekday nearest to day {field.Special.TrimEnd('W', 'w')} of the month";
                        }

                        if (field.Special.StartsWith("L-", StringComparison.OrdinalIgnoreCase))
                        {
                            return DaysBeforeTheEnd(field.Special.Substring(2));
                        }

                        return $"on {field.Special}";
                }
            }

            if (field.Step > 1) return DescribeEvery(field, "day", "month");

            if (field.RangeStart.HasValue && field.Values.Count == field.RangeLength)
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

            // A step is a list here: no step fits the week evenly (see Field.NormalizeStep)
            var days = field.Values.Select(static x => x % 7).Distinct().ToList();

            if (days.Count == 5 && !days.Contains(0) && !days.Contains(6)) return "Monday to Friday";
            if (days.Count == 2 && days.Contains(0) && days.Contains(6)) return "on weekends";

            if (field.RangeStart.HasValue && days.Count == field.RangeLength)
            {
                return $"{DayNames[field.RangeStart.Value % 7]} to {DayNames[field.RangeEnd!.Value % 7]}";
            }

            return "on " + JoinList(days.OrderBy(static x => x).Select(static x => DayNames[x]).ToList());
        }

        /// <summary>The day of week as a condition on the day of month: "a Monday", "a weekday", "the last Friday of the month".</summary>
        private static string DescribeDayOfWeekCondition(Field field)
        {
            if (field.Special != null)
            {
                var text = DescribeDayOfWeek(field)!;
                return text.StartsWith("on ", StringComparison.Ordinal) ? text.Substring(3) : text;
            }

            var days = field.Values.Select(static x => x % 7).Distinct().OrderBy(static x => x).ToList();

            if (days.Count == 5 && !days.Contains(0) && !days.Contains(6)) return "a weekday";
            if (days.Count == 2 && days.Contains(0) && days.Contains(6)) return "a Saturday or Sunday";

            var names = days.Select(static x => DayNames[x]).ToList();
            return names.Count == 1
                ? $"a {names[0]}"
                : $"a {String.Join(", ", names.Take(names.Count - 1))} or {names[names.Count - 1]}";
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

        // "L-3" is the day three days before the last one
        private static string DaysBeforeTheEnd(string offset) => $"{offset} days before the end of the month";

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
            // L, LW, L-3, L-3W, 15W, 5L, FRIL, 1#2, MON#2
            private static readonly System.Text.RegularExpressions.Regex SpecialToken =
                new(@"^(L|LW|L-\d+W?|\d+W|\d+L|[A-Za-z]{3}L|\d+#[1-5]|[A-Za-z]{3}#[1-5])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // As many as TryDescribeTimes lists
            private const int MaxListedValues = 6;

            private int _min;
            private int _size;

            public bool IsEvery { get; private set; }
            public int Step { get; private set; } = 1;
            public int? RangeStart { get; private set; }
            public int? RangeEnd { get; private set; }
            public List<int> Values { get; } = new();
            public string? Special { get; private set; }
            public string[]? Names { get; private set; }

            public bool IsSpecific => !IsEvery && Step == 1 && Special == null;
            public bool IsZero => IsSpecific && Values.Count == 1 && Values[0] == 0;

            /// <summary>A single range without a step, such as <c>9-17</c>, or <c>22-2</c> across midnight.</summary>
            public bool IsRange => IsSpecific && RangeStart.HasValue && Values.Count == RangeLength;

            /// <summary>How many values the range spans, counting around the end for a range such as <c>22-2</c>.</summary>
            public int RangeLength => RangeStart.HasValue && RangeEnd.HasValue ? Distance(RangeStart.Value, RangeEnd.Value) + 1 : 0;

            public static Field Fixed(int value)
            {
                var field = new Field();
                field.Values.Add(value);
                return field;
            }

            public static Field Parse(string text, int min, int max, string[]? names, bool allowSpecial = false)
            {
                // The day of week wraps around as 0-6, where 7 is Sunday like 0
                var field = new Field { Names = names, _min = min, _size = max == 7 && min == 0 ? 7 : max - min + 1 };
                var token = text.Trim();
                var wraps = false;

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
                            // "5/15" and "5/1" run from 5 to the end of the field
                            start = ParseValue(rangePart, names);
                            end = stepIndex >= 0 ? max : start;
                        }

                        // A range of a list is not kept, so the list's values are sorted below
                        if (token.IndexOf(',') < 0 && (dash > 0 || stepIndex >= 0))
                        {
                            field.RangeStart = start;
                            field.RangeEnd = end;
                        }
                    }

                    if (step > 1)
                    {
                        field.Step = step;
                        if (rangePart != "*" && token.IndexOf(',') < 0 && rangePart.IndexOf('-') > 0)
                        {
                            field.RangeStart = start;
                            field.RangeEnd = end;
                        }
                    }

                    if (end < start)
                    {
                        // A range that ends before it starts wraps around, like Cronos: 22-2 is 22, 23, 0, 1 and 2,
                        // and the step carries on across the end (22-6/2 is 22, 0, 2, 4 and 6)
                        wraps = true;
                        for (var offset = 0; offset <= field.Distance(start, end); offset += step)
                        {
                            var value = field._min + ((start - field._min + offset) % field._size);
                            if (!field.Values.Contains(value)) field.Values.Add(value);
                        }

                        continue;
                    }

                    for (var value = start; value <= end; value += step)
                    {
                        if (!field.Values.Contains(value)) field.Values.Add(value);
                    }
                }

                // A single wrapped range keeps its order, so "22-2" lists 22:00 first
                if (!(wraps && field.RangeStart.HasValue)) field.Values.Sort();
                if (field.Step > 1) field.NormalizeStep();
                return field;
            }

            /// <summary>
            /// Keeps "every N" only where it is true. A step over the whole field that divides it (<c>*/15</c> minutes,
            /// <c>*/6</c> hours) stays an even step, with no range. Any other step (one with a start or an end, such as
            /// <c>0-29/5</c> or <c>6/4</c>, or one that doesn't divide the field, such as <c>*/45</c>, after which the
            /// next run comes sooner) becomes the list of its values when they are few or not evenly spaced, and
            /// otherwise keeps the step with <see cref="RangeStart"/> and <see cref="RangeEnd"/> set to its first and
            /// last value.
            /// </summary>
            private void NormalizeStep()
            {
                var evenlySpaced = true;
                for (var i = 1; i < Values.Count; i++)
                {
                    if (Distance(Values[i - 1], Values[i]) != Step) evenlySpaced = false;
                }

                if (evenlySpaced && Values.Count > 1 && Values[0] == _min && _size % Step == 0 && Values.Count == _size / Step)
                {
                    RangeStart = null;
                    RangeEnd = null;
                    return;
                }

                if (evenlySpaced && Values.Count > MaxListedValues)
                {
                    RangeStart = Values[0];
                    RangeEnd = Values[Values.Count - 1];
                    return;
                }

                Step = 1;
                RangeStart = null;
                RangeEnd = null;
            }

            /// <summary>An uneven step (see <see cref="NormalizeStep"/>) runs from <see cref="RangeStart"/> to <see cref="RangeEnd"/>.</summary>
            public bool IsPartialStep => Step > 1 && RangeStart.HasValue;

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

            // Steps from one value to another, going around the end when the second is smaller
            private int Distance(int from, int to)
            {
                var size = _size > 0 ? _size : 1;
                return (((to - from) % size) + size) % size;
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
