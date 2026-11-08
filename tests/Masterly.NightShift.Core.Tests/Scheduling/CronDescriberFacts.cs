extern alias ReferencedCronos;

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ReferencedCronos::Cronos;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class CronDescriberFacts
    {
        [Theory]
        [InlineData("* * * * *", "Every minute")]
        [InlineData("*/15 * * * *", "Every 15 minutes")]
        [InlineData("*/15 9-17 * * 1-5", "Every 15 minutes, between 09:00 and 17:59, Monday to Friday")]
        [InlineData("0 * * * *", "At the start of every hour")]
        [InlineData("30 * * * *", "At 30 minutes past every hour")]
        [InlineData("0 */6 * * *", "At the start of every 6 hours")]
        [InlineData("0 0 * * *", "At 00:00")]
        [InlineData("30 9 * * *", "At 09:30")]
        [InlineData("30 9,17 * * *", "At 09:30 and 17:30")]
        [InlineData("0 9 * * 1-5", "At 09:00, Monday to Friday")]
        [InlineData("0 10 * * 0,6", "At 10:00, on weekends")]
        [InlineData("0 8 * * 1,5", "At 08:00, on Monday and Friday")]
        [InlineData("0 8 * * MON,WED,FRI", "At 08:00, on Monday, Wednesday and Friday")]
        [InlineData("0 3 15 * *", "At 03:00, on day 15 of the month")]
        [InlineData("0 23 L * *", "At 23:00, on the last day of the month")]
        [InlineData("0 0 1 1 *", "At 00:00, on day 1 of the month, in January")]
        [InlineData("0 0 1 */3 *", "At 00:00, on day 1 of the month, every 3 months")]
        [InlineData("0 6 1 3-5 *", "At 06:00, on day 1 of the month, March to May")]
        [InlineData("0 4 */3 * *", "At 04:00, every 3 days from day 1 through 31 of the month")]
        [InlineData("0 12 1 * 1", "At 12:00, on day 1 of the month if it is a Monday")]
        [InlineData("0 9-17 * * *", "Every hour from 09:00 to 17:00")]
        [InlineData("0 9 * * 5L", "At 09:00, on the last Friday of the month")]
        [InlineData("0 9 * * 1#2", "At 09:00, on the second Monday of the month")]
        [InlineData("0 9 15W * *", "At 09:00, on the weekday nearest to day 15 of the month")]
        [InlineData("* * * * * *", "Every second")]
        [InlineData("*/30 * * * * *", "Every 30 seconds")]
        [InlineData("*/15 1 2 * * *", "Every 15 seconds, at 02:01")]
        [InlineData("30 0 9 * * *", "At 09:00:30")]
        [InlineData("0 */5 * * * *", "Every 5 minutes")]
        [InlineData("@daily", "At 00:00")]
        [InlineData("@hourly", "At the start of every hour")]
        [InlineData("@weekly", "At 00:00, on Sunday")]
        public void Describe_ProducesReadableText(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Theory]
        [InlineData("*/10 5 * * * *", "Every 10 seconds, during minute 5 of the hour")]
        [InlineData("*/10 5,10 */2 * * *", "Every 10 seconds, during minutes 5 and 10 of the hour, every 2 hours")]
        [InlineData("0 0,10,20,30 9,17 * * *", "At minutes 0, 10, 20 and 30 past hours 9 and 17")]
        [InlineData("0 9-17/2 * * *", "At 09:00, 11:00, 13:00, 15:00 and 17:00")]
        [InlineData("10-40/10 * * * *", "At minutes 10, 20, 30 and 40 past every hour")]
        [InlineData("*/1 * * * *", "Every minute")]
        [InlineData("0 0 LW * *", "At 00:00, on the last weekday of the month")]
        [InlineData("0 0 L-3 * *", "At 00:00, 3 days before the end of the month")]
        [InlineData("0 0 L-3W * *", "At 00:00, on the weekday nearest to 3 days before the end of the month")]
        [InlineData("0 0 1-15 * *", "At 00:00, on days 1 to 15 of the month")]
        [InlineData("0 0 * * 1-3", "At 00:00, Monday to Wednesday")]
        [InlineData("0 0 * * MON-WED", "At 00:00, Monday to Wednesday")]
        [InlineData("0 0 1 JAN *", "At 00:00, on day 1 of the month, in January")]
        [InlineData("0 0 1 1,7 *", "At 00:00, on day 1 of the month, in January and July")]
        [InlineData("0 9 * * FRIL", "At 09:00, on the last Friday of the month")]
        [InlineData("0 9 * * MON#2", "At 09:00, on the second Monday of the month")]
        public void Describe_ProducesReadableText_ForLessCommonShapes(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Theory]
        [InlineData("0-29/5 * * * *", "At minutes 0, 5, 10, 15, 20 and 25 past every hour")]
        [InlineData("5-55/10 * * * *", "At minutes 5, 15, 25, 35, 45 and 55 past every hour")]
        [InlineData("*/45 * * * *", "At minutes 0 and 45 past every hour")]
        [InlineData("*/7 * * * *", "Every 7 minutes from minute 0 through 56 of the hour")]
        [InlineData("*/7 9-17 * * *", "Every 7 minutes from minute 0 through 56 of the hour, between 09:00 and 17:59")]
        [InlineData("0 6/4 * * *", "At 06:00, 10:00, 14:00, 18:00 and 22:00")]
        [InlineData("0 */5 * * *", "At 00:00, 05:00, 10:00, 15:00 and 20:00")]
        [InlineData("15 9-17/4 * * *", "At 09:15, 13:15 and 17:15")]
        [InlineData("0 1/2 * * *", "At the start of every 2 hours between 01:00 and 23:59")]
        [InlineData("0 20-10/2 * * *", "At the start of every 2 hours between 20:00 and 10:59")]
        [InlineData("0 0 10-20/5 * *", "At 00:00, on days 10, 15 and 20 of the month")]
        [InlineData("0 0 1-31/2 * *", "At 00:00, every 2 days from day 1 through 31 of the month")]
        [InlineData("0 0 * * 1-5/2", "At 00:00, on Monday, Wednesday and Friday")]
        [InlineData("0 0 * * */2", "At 00:00, on Sunday, Tuesday, Thursday and Saturday")]
        [InlineData("0 0 1 2-11/3 *", "At 00:00, on day 1 of the month, in February, May, August and November")]
        [InlineData("0 0 1 */5 *", "At 00:00, on day 1 of the month, in January, June and November")]
        [InlineData("0-30/10 * * * * *", "Every minute, at seconds 0, 10, 20 and 30")]
        [InlineData("*/7 * * * * *", "Every 7 seconds from second 0 through 56 of the minute")]
        [InlineData("0-10/5,30 * * * *", "At minutes 0, 5, 10 and 30 past every hour")]
        public void Describe_CallsAStepEveryN_OnlyWhenTheRunsAreAlwaysThatFarApart(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Theory]
        [InlineData("*/15 * * * *", 15)]
        [InlineData("*/20 * * * *", 20)]
        [InlineData("0/30 * * * *", 30)]
        [InlineData("0-59/12 * * * *", 12)]
        public void Describe_EveryNMinutes_MatchesTheOccurrences(string cron, int minutes)
        {
            var from = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
            var occurrences = Cron.GetNextOccurrences(cron, TimeZoneInfo.Utc, from, 200);

            Assert.Equal($"Every {minutes} minutes", Cron.Describe(cron));
            Assert.All(occurrences.Zip(occurrences.Skip(1), static (a, b) => b - a), gap => Assert.Equal(TimeSpan.FromMinutes(minutes), gap));
        }

        [Fact]
        public void Describe_IsTruthful_ForAStepFromTextThatDoesNotDivideTheHour()
        {
            var cron = Cron.FromText("every 45 minutes");

            Assert.Equal("*/45 * * * *", cron);
            Assert.Equal("At minutes 0 and 45 past every hour", Cron.Describe(cron));
        }

        [Fact]
        public void BothDayFields_MustMatch_ForTheSchedulerToRun()
        {
            // The describer's "if it is" wording relies on this: Cronos ANDs the day fields, Vixie cron ORs them
            var expression = CronExpression.Parse("0 12 1 * 1");
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var occurrences = expression.GetOccurrences(from, from.AddYears(2), TimeZoneInfo.Utc).ToList();
            var viaCron = Cron.GetNextOccurrences("0 12 1 * 1", TimeZoneInfo.Utc, from, 3);

            Assert.All(occurrences, x => Assert.True(x.Day == 1 && x.DayOfWeek == DayOfWeek.Monday, $"{x:u}"));
            Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), occurrences[0]);
            Assert.Equal(occurrences.Take(3), viaCron);
        }

        [Theory]
        [InlineData("0 0 13 * 5", "At 00:00, on day 13 of the month if it is a Friday")]
        [InlineData("0 0 1-7 * 1", "At 00:00, on days 1 to 7 of the month if it is a Monday")]
        [InlineData("0 0 L * FRI", "At 00:00, on the last day of the month if it is a Friday")]
        [InlineData("0 0 1 * 1-5", "At 00:00, on day 1 of the month if it is a weekday")]
        [InlineData("0 0 1 * 0,6", "At 00:00, on day 1 of the month if it is a Saturday or Sunday")]
        [InlineData("0 0 1 * MON,WED,FRI", "At 00:00, on day 1 of the month if it is a Monday, Wednesday or Friday")]
        [InlineData("0 0 1 * 1,5", "At 00:00, on day 1 of the month if it is a Monday or Friday")]
        [InlineData("0 0 1 6 1", "At 00:00, on day 1 of the month if it is a Monday, in June")]
        public void Describe_SaysBothDayFieldsMustMatch(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Fact]
        public void Describe_WordsASpecialDayOfWeek_AsACondition()
        {
            Assert.Equal("At 00:00, on the last day of the month if it is the last Friday of the month", CronDescriber.Describe("0 0 L * 5L"));
        }

        [Theory]
        [InlineData("30 9-17 * * 1-5", "Every hour from 09:30 to 17:30, Monday to Friday")]
        [InlineData("0 0-23 * * *", "Every hour from 00:00 to 23:00")]
        [InlineData("30 0 9-17 * * *", "Every hour from 09:00:30 to 17:00:30")]
        [InlineData("0 9-11 * * *", "At 09:00, 10:00 and 11:00")]
        [InlineData("0,30 9-17 * * *", "At minutes 0 and 30 past the hour, between 09:00 and 17:59")]
        [InlineData("0 1,3,5,7,9,11,13 * * *", "At minute 0 past hours 1, 3, 5, 7, 9, 11 and 13")]
        [InlineData("15 9-12,14-17 * * *", "At minute 15 past hours 9, 10, 11, 12, 14, 15, 16 and 17")]
        [InlineData("*/15 0 9-17 * * *", "Every 15 seconds, during minute 0 of the hour, between 09:00 and 17:59")]
        public void Describe_KeepsHourRangesShort(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Theory]
        [InlineData("0 22-2 * * *", "At 22:00, 23:00, 00:00, 01:00 and 02:00")]
        [InlineData("30 22-6 * * *", "Every hour from 22:30 to 06:30")]
        [InlineData("*/10 22-6 * * *", "Every 10 minutes, between 22:00 and 06:59")]
        [InlineData("0 22-6/2 * * *", "At 22:00, 00:00, 02:00, 04:00 and 06:00")]
        [InlineData("0 0 * * FRI-MON", "At 00:00, Friday to Monday")]
        [InlineData("0 0 * * 5-1", "At 00:00, Friday to Monday")]
        [InlineData("0 0 * * 6-0", "At 00:00, on weekends")]
        [InlineData("0 0 1 NOV-FEB *", "At 00:00, on day 1 of the month, November to February")]
        [InlineData("0 0 28-3 * *", "At 00:00, on days 28 to 3 of the month")]
        public void Describe_UnderstandsRangesThatWrapAround(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Theory]
        [InlineData("0 0 * 1/1 *", "At 00:00, January to December")]
        [InlineData("0 9/1 * * *", "Every hour from 09:00 to 23:00")]
        [InlineData("55/1 * * * *", "At minutes 55, 56, 57, 58 and 59 past every hour")]
        [InlineData("0 55/1 * * * *", "At minutes 55, 56, 57, 58 and 59 past every hour")]
        [InlineData("0 0 5/1 * *", "At 00:00, on days 5 to 31 of the month")]
        [InlineData("0 0 * * FRI/1", "At 00:00, Friday to Sunday")]
        public void Describe_ReadsAStartWithAStepOfOne_AsARangeToTheEndOfTheField(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Theory]
        [InlineData("0/1 * * * *")]
        [InlineData("30/1 * * * *")]
        [InlineData("10,5/1 * * * *")]
        [InlineData("15/1,52 * * * *")]
        public void Describe_ListsTheMinutesTheSchedulerRuns_ForAStartWithAStepOfOne(string cron)
        {
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var expected = CronExpression.Parse(cron).GetOccurrences(from, from.AddHours(1), TimeZoneInfo.Utc)
                .Select(static x => x.Minute.ToString(CultureInfo.InvariantCulture));

            var described = Regex.Matches(Cron.Describe(cron), @"\d+").Cast<Match>().Select(static x => x.Value);

            Assert.Equal(expected, described);
        }

        [Theory]
        [InlineData("52,48-15/29,19 * * * *", "At minutes 19, 48 and 52 past every hour")]
        [InlineData("0 22-6/2,3 * * *", "At 00:00, 02:00, 03:00, 04:00, 06:00 and 22:00")]
        public void Describe_SortsAList_ThatHoldsASteppedRange(string cron, string expected)
        {
            Assert.Equal(expected, Cron.Describe(cron));
        }

        [Theory]
        [InlineData("0 0 * * XYZ#2", "At 00:00, on the second XYZ of the month")]
        [InlineData("0 0 * * XYZL", "At 00:00, on the last XYZ of the month")]
        [InlineData("0 0 * * LW", "At 00:00, on LW")]
        [InlineData("0 0 1#2 * *", "At 00:00, on 1#2")]
        public void Describe_EchoesSpecialTokens_ItHasNoWordsFor(string cron, string expected)
        {
            // Cronos rejects these, so only the describer itself sees them; it must not throw
            Assert.Equal(expected, CronDescriber.Describe(cron));
        }

        [Theory]
        [InlineData("1 2 3")]
        [InlineData("1 2 3 4 5 6 7")]
        public void Describe_ThrowsFormatException_WhenTheFieldCountIsWrong(string cron)
        {
            var exception = Assert.Throws<FormatException>(() => CronDescriber.Describe(cron));

            Assert.Equal("A CRON expression has 5 or 6 fields.", exception.Message);
        }

        [Theory]
        [InlineData("0 5L * * *", "5L")]
        [InlineData("L * * * *", "L")]
        [InlineData("0 0 * 3W *", "3W")]
        [InlineData("1#2 0 0 * * *", "1#2")]
        public void Describe_ThrowsFormatException_ForDayTokensOutsideTheDayFields(string cron, string token)
        {
            var exception = Assert.Throws<FormatException>(() => CronDescriber.Describe(cron));

            Assert.Equal($"'{token}' is not a valid CRON field value.", exception.Message);
        }

        [Fact]
        public void Describe_ThrowsFormatException_ForAnUnknownName()
        {
            var exception = Assert.Throws<FormatException>(() => CronDescriber.Describe("0 0 1 FOO *"));

            Assert.Equal("'FOO' is not a valid CRON field value.", exception.Message);
        }

        [Fact]
        public void Describe_ThrowsAnException_ForInvalidExpressions()
        {
            Assert.ThrowsAny<FormatException>(() => Cron.Describe("not cron"));
            Assert.Throws<ArgumentNullException>(() => Cron.Describe(null));
        }

        [Fact]
        public void TryDescribe_ReturnsNull_ForInvalidExpressions()
        {
            Assert.Null(Cron.TryDescribe("not cron"));
            Assert.Null(Cron.TryDescribe(null));
            Assert.Equal("Every minute", Cron.TryDescribe("* * * * *"));
        }
    }
}
