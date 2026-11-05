using System;
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
        [InlineData("0 4 */3 * *", "At 04:00, every 3 days")]
        [InlineData("0 12 1 * 1", "At 12:00, on day 1 of the month or on Monday")]
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
        [InlineData("0 9-17/2 * * *", "At the start of every 2 hours between 09:00 and 17:59")]
        [InlineData("10-40/10 * * * *", "Every 10 minutes")]
        [InlineData("*/1 * * * *", "Every minute")]
        [InlineData("0 0 LW * *", "At 00:00, on the last weekday of the month")]
        [InlineData("0 0 L-3 * *", "At 00:00, 3 days before the end of the month")]
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
