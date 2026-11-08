using System;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class TimeTextFacts
    {
        // Wednesday 2026-11-04 14:30 UTC
        private static readonly DateTime Now = new DateTime(2026, 11, 4, 14, 30, 0, DateTimeKind.Utc);
        private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

        private static DateTime Parse(string text) => TimeText.Parse(text, Now, Utc);

        [Theory]
        [InlineData("in 20 minutes", 0, 0, 20)]
        [InlineData("20 minutes", 0, 0, 20)]
        [InlineData("in 2 hours", 0, 2, 0)]
        [InlineData("in an hour", 0, 1, 0)]
        [InlineData("in 90 seconds", 0, 0, 1.5)]
        [InlineData("in 3 days", 3, 0, 0)]
        [InlineData("in 1 week", 7, 0, 0)]
        [InlineData("2h from now", 0, 2, 0)]
        public void Parse_UnderstandsRelativePhrases(string text, int days, int hours, double minutes)
        {
            Assert.Equal(Now.AddDays(days).AddHours(hours).AddMinutes(minutes), Parse(text));
        }

        [Theory]
        [InlineData("tomorrow at 9am", "2026-11-05 09:00")]
        [InlineData("tomorrow at 9:30", "2026-11-05 09:30")]
        [InlineData("tomorrow 17:45", "2026-11-05 17:45")]
        [InlineData("tomorrow", "2026-11-05 09:00")]
        [InlineData("today at 18:00", "2026-11-04 18:00")]
        [InlineData("at 15:00", "2026-11-04 15:00")]
        [InlineData("at 9am", "2026-11-05 09:00")]
        [InlineData("9 pm", "2026-11-04 21:00")]
        [InlineData("tonight", "2026-11-04 20:00")]
        [InlineData("noon", "2026-11-05 12:00")]
        [InlineData("midnight", "2026-11-05 00:00")]
        [InlineData("friday at 6pm", "2026-11-06 18:00")]
        [InlineData("next monday at 8", "2026-11-09 08:00")]
        [InlineData("monday", "2026-11-09 09:00")]
        [InlineData("wednesday", "2026-11-11 09:00")]
        [InlineData("next week", "2026-11-09 09:00")]
        [InlineData("end of day", "2026-11-04 23:59")]
        [InlineData("end of month", "2026-11-30 23:59")]
        [InlineData("2026-12-24 08:00", "2026-12-24 08:00")]
        [InlineData("2026-12-24", "2026-12-24 00:00")]
        [InlineData("now", "2026-11-04 14:30")]
        public void Parse_UnderstandsDatesAndTimes(string text, string expected)
        {
            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected), DateTimeKind.Utc), Parse(text));
        }

        [Theory]
        [InlineData("tomorrow morning", "2026-11-05 09:00")]
        [InlineData("tomorrow afternoon", "2026-11-05 14:00")]
        [InlineData("tomorrow evening", "2026-11-05 18:00")]
        [InlineData("tomorrow evening at 19:15", "2026-11-05 19:15")]
        [InlineData("this friday", "2026-11-06 09:00")]
        [InlineData("on the friday", "2026-11-06 09:00")]
        [InlineData("end of week", "2026-11-08 23:59")]
        [InlineData("end of today", "2026-11-04 23:59")]
        [InlineData("end of year", "2026-12-31 23:59")]
        [InlineData("next month", "2026-12-01 09:00")]
        [InlineData("next month at 7", "2026-12-01 07:00")]
        [InlineData("in 2 months", "2027-01-04 14:30")]
        [InlineData("in a month", "2026-12-04 14:30")]
        [InlineData("asap", "2026-11-04 14:30")]
        public void Parse_UnderstandsPartsOfTheDay_AndEndsOfPeriods(string text, string expected)
        {
            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), Parse(text));
        }

        [Theory]
        [InlineData("tonight at 9", "2026-11-04 21:00")]
        [InlineData("9 tonight", "2026-11-04 21:00")]
        [InlineData("tomorrow evening at 7", "2026-11-05 19:00")]
        [InlineData("at 7 this evening", "2026-11-04 19:00")]
        [InlineData("this evening at 7:30", "2026-11-04 19:30")]
        [InlineData("friday evening at 8", "2026-11-06 20:00")]
        [InlineData("tomorrow afternoon at 3", "2026-11-05 15:00")]
        [InlineData("this afternoon at 3", "2026-11-04 15:00")]
        [InlineData("tomorrow afternoon at 12", "2026-11-05 12:00")]
        [InlineData("tomorrow morning at 10", "2026-11-05 10:00")]
        [InlineData("tomorrow morning at 12", "2026-11-05 12:00")]
        public void Parse_ReadsAnHourWithoutAmOrPm_InThePartOfTheDayNamed(string text, string expected)
        {
            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), Parse(text));
        }

        [Theory]
        [InlineData("tonight at 12", "2026-11-05 00:00")]
        [InlineData("evening at 12", "2026-11-05 00:00")]
        [InlineData("tomorrow evening at 12", "2026-11-06 00:00")]
        [InlineData("tomorrow evening at 12:30", "2026-11-06 00:30")]
        [InlineData("wednesday evening at 12", "2026-11-05 00:00")]
        public void Parse_ReadsTwelveInTheEvening_AsTheMidnightThatEndsIt(string text, string expected)
        {
            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), Parse(text));
        }

        // The same moment as "12" with those words, not the midnight before the evening
        [Theory]
        [InlineData("tomorrow evening at midnight", "2026-11-06 00:00")]
        [InlineData("midnight tomorrow evening", "2026-11-06 00:00")]
        [InlineData("evening at midnight", "2026-11-05 00:00")]
        [InlineData("this evening at midnight", "2026-11-05 00:00")]
        [InlineData("wednesday evening at midnight", "2026-11-05 00:00")]
        [InlineData("friday evening at midnight", "2026-11-07 00:00")]
        [InlineData("tomorrow at midnight", "2026-11-05 00:00")]
        [InlineData("friday midnight", "2026-11-06 00:00")]
        public void Parse_ReadsMidnightInTheEvening_AsTheMidnightThatEndsIt_LikeTwelve(string text, string expected)
        {
            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), Parse(text));
        }

        [Theory]
        [InlineData("tomorrow evening")]
        [InlineData("friday evening")]
        [InlineData("tonight")]
        public void Parse_ReadsMidnightAndTwelve_TheSame_InTheEvening(string phrase)
        {
            Assert.Equal(Parse($"{phrase} at 12"), Parse($"{phrase} at midnight"));
        }

        [Theory]
        [InlineData("tonight at 10pm", "2026-11-04 22:00")]
        [InlineData("tomorrow evening at 7am", "2026-11-05 07:00")]
        [InlineData("tomorrow evening at 07:30", "2026-11-05 07:30")]
        [InlineData("tomorrow evening at noon", "2026-11-05 12:00")]
        [InlineData("tomorrow afternoon at 16:45", "2026-11-05 16:45")]
        public void Parse_KeepsAnExplicitAmOrPm_OrA24HourTime_InAPartOfTheDay(string text, string expected)
        {
            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), Parse(text));
        }

        // Now is a Wednesday at 14:30
        [Theory]
        [InlineData("wednesday at 18:00", "2026-11-04 18:00")]
        [InlineData("18:00 wednesday", "2026-11-04 18:00")]
        [InlineData("wed 6 pm", "2026-11-04 18:00")]
        [InlineData("wednesday evening", "2026-11-04 18:00")]
        [InlineData("wednesday at 9am", "2026-11-11 09:00")]
        [InlineData("9am wednesday", "2026-11-11 09:00")]
        [InlineData("wednesday at 14:30", "2026-11-11 14:30")]
        [InlineData("midnight wednesday", "2026-11-11 00:00")]
        [InlineData("next wednesday at 18:00", "2026-11-11 18:00")]
        [InlineData("18:00 next wednesday", "2026-11-11 18:00")]
        [InlineData("wednesday", "2026-11-11 09:00")]
        public void Parse_ReadsTodaysWeekdayName_AsToday_OnlyWhenATimeStillAheadIsGiven_WhereverTheTimeIs(string text, string expected)
        {
            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), Parse(text));
        }

        [Theory]
        [InlineData("end of decade")]
        [InlineData("end")]
        [InlineData("end of")]
        [InlineData("2026-13-45")]
        [InlineData("2026-12-24 99:00")]
        public void TryParse_ReturnsFalse_ForUnknownPeriodsAndMalformedDates(string text)
        {
            var parsed = TimeText.TryParse(text, Now, Utc, out var result);

            Assert.False(parsed);
            Assert.Equal(default(DateTime), result);
        }

        [Theory]
        [InlineData("in 99999999 days")]
        [InlineData("in 9000000 days")]
        [InlineData("in 3000000000 months")]
        [InlineData("in 121 months")]
        [InlineData("in 3654 days")]
        [InlineData("in 99999999999999999999 seconds")]
        public void TryParse_ReturnsFalse_ForRelativePhrasesMoreThanTenYearsAhead(string text)
        {
            Assert.False(TimeText.TryParse(text, Now, Utc, out var result));
            Assert.Equal(default, result);
            Assert.Throws<FormatException>(() => Parse(text));
        }

        [Fact]
        public void TryParse_AcceptsRelativePhrasesUpToTenYearsAhead()
        {
            Assert.True(TimeText.TryParse("in 3652 days", Now, Utc, out var days));
            Assert.Equal(Now.AddDays(3652), days);
            Assert.True(TimeText.TryParse("in 120 months", Now, Utc, out var months));
            Assert.Equal(Now.AddMonths(120), months);
        }

        [Fact]
        public void TryParse_ReturnsFalse_InsteadOfThrowing_WhenTheMomentIsBeyondTheCalendar()
        {
            var nearTheEnd = new DateTime(9999, 12, 30, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(TimeText.TryParse("in 3 days", nearTheEnd, Utc, out _));
            Assert.False(TimeText.TryParse("in 2 months", nearTheEnd, Utc, out _));
        }

        [Fact]
        public void TryParse_ThrowsArgumentNullException_WhenTextIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => TimeText.TryParse(null, Now, Utc, out _));
        }

        [Fact]
        public void Parse_MovesATimeInsideTheSpringForwardGap_AnHourLater()
        {
            var berlin = TimeZoneInfo.FindSystemTimeZoneById(PlatformHelper.IsRunningOnWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

            // 02:30 does not exist in Berlin on 2027-03-28: clocks jump from 02:00 to 03:00 (01:00 UTC)
            var result = TimeText.Parse("2027-03-28 02:30", Now, berlin);

            Assert.Equal(new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc), result);
        }

        [Fact]
        public void Parse_AddsCalendarMonths_InTheGivenTimeZone()
        {
            var berlin = TimeZoneInfo.FindSystemTimeZoneById(PlatformHelper.IsRunningOnWindows() ? "W. Europe Standard Time" : "Europe/Berlin");
            var september = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

            // 14:00 summer time on September 30th is 14:00 winter time on October 30th, an hour later in UTC
            var result = TimeText.Parse("in 1 month", september, berlin);

            Assert.Equal(new DateTime(2026, 10, 30, 13, 0, 0, DateTimeKind.Utc), result);
        }

        [Fact, StaticLock]
        public void Parse_WithoutANow_ReadsRelativeToTheCurrentTime()
        {
            var previous = TimeText.DefaultTimeZone;
            try
            {
                TimeText.DefaultTimeZone = TimeZoneInfo.Utc;
                var before = DateTime.UtcNow;

                var result = TimeText.Parse("in 10 minutes");

                Assert.InRange(result, before.AddMinutes(10), DateTime.UtcNow.AddMinutes(10));
                Assert.Equal(DateTimeKind.Utc, result.Kind);
            }
            finally
            {
                TimeText.DefaultTimeZone = previous;
            }
        }

        [Fact]
        public void Parse_ReadsPhrasesInTheGivenTimeZone()
        {
            var berlin = TimeZoneInfo.FindSystemTimeZoneById(PlatformHelper.IsRunningOnWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

            var result = TimeText.Parse("tomorrow at 9am", Now, berlin);

            Assert.Equal(new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc), result);
        }

        [Theory]
        [InlineData("")]
        [InlineData("yesterday")]
        [InlineData("soonish")]
        [InlineData("at 25:00")]
        [InlineData("2020-01-01")]
        [InlineData("today at 9am")]
        public void Parse_RejectsNonsenseAndThePast(string text)
        {
            Assert.Throws<FormatException>(() => Parse(text));
            Assert.False(TimeText.TryParse(text, Now, Utc, out _));
        }

        [Theory]
        [InlineData("2020-01-01")]
        [InlineData("today at 9am")]
        [InlineData("2026-11-04T08:00:00Z")]
        public void Parse_SaysTheMomentIsInThePast(string text)
        {
            var exception = Assert.Throws<FormatException>(() => Parse(text));

            Assert.Equal($"The time '{text}' is in the past.", exception.Message);
        }

        // 21:00, after "tonight" (20:00) and "this evening" (18:00)
        private static readonly DateTime LateEvening = new DateTime(2026, 11, 4, 21, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("tonight", "20:00")]
        [InlineData("this evening", "18:00")]
        [InlineData("this morning", "09:00")]
        public void Parse_RefusesTodaysPartOfTheDay_OnceItHasPassed_SayingSo(string text, string time)
        {
            var exception = Assert.Throws<FormatException>(() => TimeText.Parse(text, LateEvening, Utc));

            Assert.Equal(
                $"The time '{text}' is in the past: '{text}' is {time} today. Add a time still ahead today, or name another day, such as \"tomorrow evening\".",
                exception.Message);
            Assert.False(TimeText.TryParse(text, LateEvening, Utc, out _));
        }

        [Theory]
        [InlineData("tonight at 23:00", "2026-11-04 23:00")]
        [InlineData("this evening at 22:30", "2026-11-04 22:30")]
        [InlineData("tonight at midnight", "2026-11-05 00:00")]
        [InlineData("midnight tonight", "2026-11-05 00:00")]
        [InlineData("evening", "2026-11-05 18:00")]
        [InlineData("at 18:00", "2026-11-05 18:00")]
        public void Parse_ReadsTodaysPartOfTheDay_WithALaterTime_AndMovesATimeWithoutADayToTomorrow(string text, string expected)
        {
            var result = TimeText.Parse(text, LateEvening, Utc);

            Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), result);
        }

        [Fact]
        public void Parse_ReadsThisPartOfTheDay_AsToday()
        {
            Assert.Equal(new DateTime(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc), Parse("this evening"));
        }

        [Fact]
        public void Parse_RefusesThisPartOfTheDay_InsteadOfMovingItToTomorrow()
        {
            // At 14:30, "this afternoon" (14:00) has passed; "afternoon" alone moves to tomorrow
            Assert.Throws<FormatException>(() => Parse("this afternoon"));
            Assert.Equal(new DateTime(2026, 11, 5, 14, 0, 0, DateTimeKind.Utc), Parse("afternoon"));
        }

        [Theory]
        [InlineData("in 1.5 months")]
        [InlineData("in 0.5 month")]
        [InlineData("2.25 months from now")]
        public void Parse_RefusesPartsOfAMonth(string text)
        {
            var exception = Assert.Throws<FormatException>(() => Parse(text));

            Assert.Equal(
                $"Unable to use the time '{text}': months must be a whole number. Use days or weeks for a part of a month, for example \"in 45 days\".",
                exception.Message);
            Assert.False(TimeText.TryParse(text, Now, Utc, out var result));
            Assert.Equal(default, result);
        }

        [Fact]
        public void Parse_AcceptsAWholeNumberOfMonths_WrittenWithDecimals()
        {
            Assert.Equal(Now.AddMonths(2), Parse("in 2.0 months"));
        }

        [Fact]
        public void TryParse_UsesTheDefaultTimeZone_WhenNoneIsGiven()
        {
            var previous = TimeText.DefaultTimeZone;
            try
            {
                TimeText.DefaultTimeZone = TimeZoneInfo.Utc;
                Assert.True(TimeText.TryParse("in 5 minutes", Now, null, out var result));
                Assert.Equal(Now.AddMinutes(5), result);
            }
            finally
            {
                TimeText.DefaultTimeZone = previous;
            }
        }
    }
}
