using System;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class CronBuilderFacts
    {
        [Fact]
        public void Default_IsDailyAtMidnight()
        {
            Assert.Equal("0 0 * * *", Cron.Builder().ToString());
        }

        [Fact]
        public void EveryMinutes_Between_OnWeekdays()
        {
            Assert.Equal("*/15 9-17 * * 1-5", Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays().ToString());
        }

        [Fact]
        public void EverySeconds_ProducesSixFields()
        {
            Assert.Equal("*/30 * * * * *", Cron.Builder().EverySeconds(30).ToString());
            Assert.Equal("* * * * * *", Cron.Builder().EverySeconds().ToString());
        }

        [Fact]
        public void EveryHours_AtMinute()
        {
            Assert.Equal("15 */6 * * *", Cron.Builder().EveryHours(6).AtMinute(15).ToString());
            Assert.Equal("15 */6 * * *", Cron.Builder().AtMinute(15).EveryHours(6).ToString());
        }

        [Fact]
        public void At_SeveralTimes_ShareTheMinute()
        {
            Assert.Equal("30 9,17 * * *", Cron.Builder().At(9, 30).At(17, 30).ToString());
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().At(9, 30).At(17, 45));
        }

        [Fact]
        public void OnDays_SortsAndDeduplicates()
        {
            Assert.Equal("0 8 * * 1,5", Cron.Builder().At(8).OnDays(DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Friday).ToString());
        }

        [Fact]
        public void Monthly_Schedules()
        {
            Assert.Equal("0 23 L * *", Cron.Builder().At(23).OnLastDayOfMonth().ToString());
            Assert.Equal("0 3 15 */3 *", Cron.Builder().At(3).OnDayOfMonth(15).EveryMonths(3).ToString());
            Assert.Equal("0 0 1 1,7 *", Cron.Builder().InMonths(7, 1).ToString());
        }

        [Fact]
        public void EveryDays_WithTime()
        {
            Assert.Equal("0 4 */3 * *", Cron.Builder().EveryDays(3).At(4).ToString());
        }

        [Fact]
        public void OnWeekends()
        {
            Assert.Equal("0 10 * * 0,6", Cron.Builder().At(10).OnWeekends().ToString());
        }

        [Fact]
        public void ImplicitConversion_ReturnsTheExpression()
        {
            string cron = Cron.Builder().EveryMinutes(5);
            Assert.Equal("*/5 * * * *", cron);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(60)]
        public void EveryMinutes_ValidatesRange(int minutes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Cron.Builder().EveryMinutes(minutes));
        }

        [Fact]
        public void Between_ValidatesOrder()
        {
            Assert.Throws<ArgumentException>(() => Cron.Builder().Between(17, 9));
        }

        [Fact]
        public void Result_IsValidForTheScheduler()
        {
            var next = Cron.GetNextOccurrences(Cron.Builder().EveryMinutes(10).Between(9, 17).OnWeekdays(), TimeZoneInfo.Utc, new DateTime(2026, 11, 6, 16, 55, 0, DateTimeKind.Utc), 2);

            Assert.Equal(new DateTime(2026, 11, 6, 17, 0, 0, DateTimeKind.Utc), next[0]);
            Assert.Equal(new DateTime(2026, 11, 6, 17, 10, 0, DateTimeKind.Utc), next[1]); // hour 17 is inclusive
        }
    }
}
