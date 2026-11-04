using System;
using Xunit;

namespace Hangfire.Core.Tests
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
