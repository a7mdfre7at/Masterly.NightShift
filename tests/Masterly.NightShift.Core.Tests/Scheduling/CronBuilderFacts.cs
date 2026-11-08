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
        public void Between_WrapsPastMidnight_WhenTheEndIsBeforeTheStart()
        {
            Assert.Equal("*/10 22-6 * * *", Cron.Builder().EveryMinutes(10).Between(22, 6).ToString());
            Assert.Equal("0 22-6/2 * * *", Cron.Builder().EveryHours(2).Between(22, 6).ToString());
            Assert.Equal("0 22-2 * * *", Cron.Builder().Between(22, 2).ToString());
        }

        [Fact]
        public void Between_ValidatesTheHours()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Cron.Builder().Between(-1, 9));
            Assert.Throws<ArgumentOutOfRangeException>(() => Cron.Builder().Between(9, 24));
        }

        [Fact]
        public void Between_KeepsAMinuteOrSecondInterval_InEitherOrder()
        {
            Assert.Equal("*/15 9-17 * * *", Cron.Builder().Between(9, 17).EveryMinutes(15).ToString());
            Assert.Equal("*/15 9-17 * * *", Cron.Builder().EveryMinutes(15).Between(9, 17).ToString());
            Assert.Equal("*/30 * 9-17 * * *", Cron.Builder().Between(9, 17).EverySeconds(30).ToString());
            Assert.Equal("*/30 * 9-17 * * *", Cron.Builder().EverySeconds(30).Between(9, 17).ToString());
        }

        [Fact]
        public void EveryHours_Between_KeepsTheStep_InEitherOrder()
        {
            Assert.Equal("0 8-18/2 * * *", Cron.Builder().EveryHours(2).Between(8, 18).ToString());
            Assert.Equal("0 8-18/2 * * *", Cron.Builder().Between(8, 18).EveryHours(2).ToString());
            Assert.Equal("30 8-18/2 * * 1-5", Cron.Builder().OnWeekdays().Between(8, 18).AtMinute(30).EveryHours(2).ToString());
            Assert.Equal("0 8-18 * * *", Cron.Builder().EveryHours().Between(8, 18).ToString());
            Assert.Equal("0 9 * * *", Cron.Builder().EveryHours(2).Between(9, 9).ToString());
        }

        [Fact]
        public void Between_AloneRunsEveryHourOfTheRange()
        {
            Assert.Equal("0 9-17 * * *", Cron.Builder().Between(9, 17).ToString());
        }

        [Fact]
        public void At_ThrowsWhenCombinedWithAnHourIntervalOrRange_InEitherOrder()
        {
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().At(9).EveryHours(2));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().EveryHours(2).At(9));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().At(9).Between(8, 18));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().Between(8, 18).At(9));
        }

        [Fact]
        public void At_ExplainsHowToRunEveryFewHoursWithinAWindow()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Cron.Builder().At(9).EveryHours(2));

            Assert.Equal(
                "At(9, 0) can't be combined with EveryHours(2): At sets fixed times of day. For runs every few hours within a window, " +
                "use EveryHours with AtMinute and Between, for example EveryHours(2).AtMinute(0).Between(9, 17).",
                exception.Message);
        }

        [Fact]
        public void CallsSettingTheSameFieldDifferently_Throw_InEitherOrder()
        {
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().EveryMinutes(15).At(9));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().At(9).EveryMinutes(15));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().At(9).AtMinute(30));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().AtMinute(30).At(9));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().EveryMinutes(5).EveryMinutes(10));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().OnWeekdays().OnWeekends());
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().OnDays(DayOfWeek.Monday).OnWeekdays());
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().EveryDays(2).OnDayOfMonth(15));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().OnLastDayOfMonth().OnDayOfMonth(15));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().EveryMonths(3).InMonths(1));
            Assert.Throws<InvalidOperationException>(() => Cron.Builder().Between(8, 12).Between(13, 17));
        }

        [Fact]
        public void Conflict_NamesBothCalls()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Cron.Builder().EveryMinutes(15).AtMinute(30));

            Assert.Equal("AtMinute(30) conflicts with EveryMinutes(15): both set the minute.", exception.Message);
        }

        [Fact]
        public void RepeatingACallWithTheSameValue_IsAllowed()
        {
            Assert.Equal("30 9 * * 1-5", Cron.Builder().AtMinute(30).At(9, 30).OnWeekdays().OnWeekdays().ToString());
            Assert.Equal("*/5 * * * *", Cron.Builder().EveryMinutes(5).EveryMinutes(5).ToString());
        }

        [Fact]
        public void Intervals_Combine_WhatCronCanExpress()
        {
            Assert.Equal("*/15 */2 * * *", Cron.Builder().EveryHours(2).EveryMinutes(15).ToString());
            Assert.Equal("*/15 */2 * * *", Cron.Builder().EveryMinutes(15).EveryHours(2).ToString());
            Assert.Equal("0 0 * */3 *", Cron.Builder().EveryDays().EveryMonths(3).ToString());
            Assert.Equal("0 0 * */3 *", Cron.Builder().EveryMonths(3).EveryDays().ToString());
        }

        [Fact]
        public void Months_RunOnTheFirst_UnlessDaysOfWeekAreGiven()
        {
            Assert.Equal("0 0 1 */3 *", Cron.Builder().EveryMonths(3).ToString());
            Assert.Equal("0 9 * 1 1-5", Cron.Builder().At(9).InMonths(1).OnWeekdays().ToString());
            Assert.Equal("0 9 * 1 1-5", Cron.Builder().OnWeekdays().At(9).InMonths(1).ToString());
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
