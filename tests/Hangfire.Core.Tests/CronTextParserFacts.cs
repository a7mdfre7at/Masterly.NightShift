using System;
using Xunit;

namespace Hangfire.Core.Tests
{
    public class CronTextParserFacts
    {
        [Theory]
        [InlineData("every minute", "* * * * *")]
        [InlineData("every 15 minutes", "*/15 * * * *")]
        [InlineData("Every 5 Minutes", "*/5 * * * *")]
        [InlineData("every two minutes", "*/2 * * * *")]
        [InlineData("every other minute", "*/2 * * * *")]
        [InlineData("every second", "* * * * * *")]
        [InlineData("every 30 seconds", "*/30 * * * * *")]
        [InlineData("every hour", "0 * * * *")]
        [InlineData("hourly", "0 * * * *")]
        [InlineData("every 6 hours", "0 */6 * * *")]
        [InlineData("every day", "0 0 * * *")]
        [InlineData("daily", "0 0 * * *")]
        [InlineData("daily at 3", "0 3 * * *")]
        [InlineData("daily at 03:30", "30 3 * * *")]
        [InlineData("daily at 3:30 pm", "30 15 * * *")]
        [InlineData("daily at 3:30pm", "30 15 * * *")]
        [InlineData("every day at 12 am", "0 0 * * *")]
        [InlineData("every day at midnight", "0 0 * * *")]
        [InlineData("every day at noon", "0 12 * * *")]
        [InlineData("at 7:15", "15 7 * * *")]
        [InlineData("daily at 9 and 17", "0 9,17 * * *")]
        [InlineData("daily at 9:30 and 17:30", "30 9,17 * * *")]
        [InlineData("every 3 days at 4", "0 4 */3 * *")]
        [InlineData("weekdays at 9", "0 9 * * 1-5")]
        [InlineData("every weekday at 09:00", "0 9 * * 1-5")]
        [InlineData("weekends at 10", "0 10 * * 0,6")]
        [InlineData("every monday", "0 0 * * 1")]
        [InlineData("every monday at 9:30", "30 9 * * 1")]
        [InlineData("every monday and friday at 8am", "0 8 * * 1,5")]
        [InlineData("every mon, wed, fri at 18:00", "0 18 * * 1,3,5")]
        [InlineData("every monday to friday at 6", "0 6 * * 1,2,3,4,5")]
        [InlineData("weekly", "0 0 * * 1")]
        [InlineData("weekly on sunday at 2", "0 2 * * 0")]
        [InlineData("every 10 minutes between 9 and 17", "*/10 9-17 * * *")]
        [InlineData("every 10 minutes from 9am to 5pm on weekdays", "*/10 9-17 * * 1-5")]
        [InlineData("every hour on weekdays", "0 * * * 1-5")]
        [InlineData("monthly", "0 0 1 * *")]
        [InlineData("every month on the 15th at 3", "0 3 15 * *")]
        [InlineData("monthly on day 1 at midnight", "0 0 1 * *")]
        [InlineData("first day of every month at 6", "0 6 1 * *")]
        [InlineData("last day of month at 23:00", "0 23 L * *")]
        [InlineData("every 3 months on the 1st", "0 0 1 */3 *")]
        [InlineData("yearly", "0 0 1 1 *")]
        [InlineData("every year on january 1st at 0:00", "0 0 1 1 *")]
        [InlineData("every year in march on the 15th at 9", "0 9 15 3 *")]
        [InlineData("* * * * *", "* * * * *")]
        [InlineData("0 */2 * * 1-5", "0 */2 * * 1-5")]
        [InlineData("@daily", "@daily")]
        public void FromText_ProducesTheExpectedCronExpression(string text, string expected)
        {
            Assert.Equal(expected, Cron.FromText(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("whenever")]
        [InlineData("every")]
        [InlineData("every 5 fortnights")]
        [InlineData("daily at 25:00")]
        [InlineData("daily at 9:15 and 17:45")]
        [InlineData("every 2 weeks")]
        [InlineData("between 9 and 17 and also")]
        [InlineData("* * *")]
        public void FromText_ThrowsFormatException_ForTextItDoesNotUnderstand(string text)
        {
            Assert.Throws<FormatException>(() => Cron.FromText(text));
        }

        [Fact]
        public void FromText_ThrowsAnException_WhenTextIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => Cron.FromText(null));
        }

        [Fact]
        public void TryFromText_ReturnsFalse_WithoutThrowing()
        {
            Assert.False(Cron.TryFromText("nonsense words", out var cron));
            Assert.Null(cron);

            Assert.True(Cron.TryFromText("every 5 minutes", out cron));
            Assert.Equal("*/5 * * * *", cron);
        }

        [Fact]
        public void GetNextOccurrences_ReturnsTheRequestedNumberOfUtcInstants()
        {
            var from = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

            var next = Cron.GetNextOccurrences("0 */6 * * *", TimeZoneInfo.Utc, from, 3);

            Assert.Equal(new[]
            {
                new DateTime(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc),
            }, next);
        }

        [Fact]
        public void GetNextOccurrences_EvaluatesInTheGivenTimeZone()
        {
            var from = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus two", "plus two");

            var next = Cron.GetNextOccurrences("0 9 * * *", plusTwo, from, 1);

            Assert.Equal(new DateTime(2026, 11, 5, 7, 0, 0, DateTimeKind.Utc), next[0]);
        }

        [Fact]
        public void GetNextOccurrences_ReturnsFewer_WhenTheExpressionStopsFiring()
        {
            var from = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

            // February 31st never happens
            var next = Cron.GetNextOccurrences("0 0 31 2 *", TimeZoneInfo.Utc, from, 2);

            Assert.Empty(next);
        }
    }
}
