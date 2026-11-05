using System;
using Masterly.NightShift.States;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class RunWindowAttributeFacts : IDisposable
    {
        private readonly ElectStateContextMock _context = new ElectStateContextMock();

        public RunWindowAttributeFacts()
        {
            _context.ApplyContext.BackgroundJob.Id = "1";
            _context.ApplyContext.NewStateObject = new ProcessingState("srv", "w");
        }

        public void Dispose()
        {
            RunWindowAttribute.Clock = () => DateTime.UtcNow;
        }

        private static DateTime Utc(string text) => DateTime.SpecifyKind(DateTime.Parse(text), DateTimeKind.Utc);

        [Fact]
        public void Ctor_RejectsBadWindows()
        {
            Assert.Throws<ArgumentException>(() => new RunWindowAttribute("9-"));
            Assert.Throws<ArgumentException>(() => new RunWindowAttribute("09:00-09:00"));
            Assert.Throws<ArgumentException>(() => new RunWindowAttribute("25:00-26:00"));
            Assert.Equal(TimeSpan.FromHours(9), new RunWindowAttribute("9-17").Start);
            Assert.Equal(TimeSpan.FromHours(17), new RunWindowAttribute("9-17").End);
        }

        [Theory]
        [InlineData("2026-11-04 10:00", true, "2026-11-04 10:00")]
        [InlineData("2026-11-04 08:59", false, "2026-11-04 09:00")]
        [InlineData("2026-11-04 17:00", false, "2026-11-05 09:00")]
        [InlineData("2026-11-04 23:30", false, "2026-11-05 09:00")]
        public void GetNextStart_ForADaytimeWindow(string now, bool allowed, string next)
        {
            var attribute = new RunWindowAttribute("09:00-17:00");

            var result = attribute.GetNextStart(Utc(now), out var allowedNow);

            Assert.Equal(allowed, allowedNow);
            Assert.Equal(Utc(next), result);
        }

        [Theory]
        [InlineData("2026-11-04 23:00", true, "2026-11-04 23:00")]
        [InlineData("2026-11-05 03:00", true, "2026-11-05 03:00")]
        [InlineData("2026-11-05 06:00", false, "2026-11-05 22:00")]
        [InlineData("2026-11-05 12:00", false, "2026-11-05 22:00")]
        public void GetNextStart_ForAnOvernightWindow(string now, bool allowed, string next)
        {
            var attribute = new RunWindowAttribute("22:00-06:00");

            var result = attribute.GetNextStart(Utc(now), out var allowedNow);

            Assert.Equal(allowed, allowedNow);
            Assert.Equal(Utc(next), result);
        }

        [Fact]
        public void GetNextStart_SkipsWeekends_WhenAsked()
        {
            var attribute = new RunWindowAttribute("09:00-17:00") { WeekdaysOnly = true };

            // Saturday 2026-11-07 10:00 -> Monday 09:00
            var result = attribute.GetNextStart(Utc("2026-11-07 10:00"), out var allowedNow);

            Assert.False(allowedNow);
            Assert.Equal(Utc("2026-11-09 09:00"), result);
        }

        [Fact]
        public void GetNextStart_HonoursTheTimeZone()
        {
            var attribute = new RunWindowAttribute("09:00-17:00", OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

            // 08:30 UTC is 09:30 in Berlin in November: allowed
            attribute.GetNextStart(Utc("2026-11-04 08:30"), out var allowedNow);
            Assert.True(allowedNow);

            // 07:30 UTC is 08:30 in Berlin: next start is 09:00 Berlin = 08:00 UTC
            var next = attribute.GetNextStart(Utc("2026-11-04 07:30"), out allowedNow);
            Assert.False(allowedNow);
            Assert.Equal(Utc("2026-11-04 08:00"), next);
        }

        [Theory]
        [InlineData("2026-11-04 03:00", false, "2026-11-04 04:00")]
        [InlineData("2026-11-04 12:00", true, "2026-11-04 12:00")]
        [InlineData("2026-11-04 01:59", true, "2026-11-04 01:59")]
        public void Blackout_InvertsTheWindow(string now, bool allowed, string next)
        {
            var attribute = new BlackoutAttribute("02:00-04:00");

            var result = attribute.GetNextStart(Utc(now), out var allowedNow);

            Assert.Equal(allowed, allowedNow);
            Assert.Equal(Utc(next), result);
        }

        [Fact]
        public void OnStateElection_ReschedulesJobsOutsideTheWindow_AndLeavesOthersAlone()
        {
            RunWindowAttribute.Clock = () => Utc("2026-11-04 20:00");
            var attribute = new RunWindowAttribute("09:00-17:00");

            attribute.OnStateElection(_context.Object);

            var scheduled = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
            Assert.Equal(Utc("2026-11-05 09:00"), scheduled.EnqueueAt);
            Assert.Contains("09:00-17:00", scheduled.Reason);

            RunWindowAttribute.Clock = () => Utc("2026-11-04 10:00");
            var inside = new ElectStateContextMock();
            inside.ApplyContext.BackgroundJob.Id = "2";
            inside.ApplyContext.NewStateObject = new ProcessingState("srv", "w");

            attribute.OnStateElection(inside.Object);

            Assert.IsType<ProcessingState>(inside.Object.CandidateState);
        }

        [Fact]
        public void TimeZoneId_IsUtc_WhenNoTimeZoneIsGiven()
        {
            Assert.Equal(TimeZoneInfo.Utc.Id, new RunWindowAttribute("09:00-17:00").TimeZoneId);
        }

        [Fact]
        public void TimeZoneId_IsTheGivenTimeZone()
        {
            var timeZoneId = OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin";

            Assert.Equal(timeZoneId, new RunWindowAttribute("09:00-17:00", timeZoneId).TimeZoneId);
        }

        [Fact]
        public void OnStateElection_IgnoresOtherStates()
        {
            RunWindowAttribute.Clock = () => Utc("2026-11-04 20:00");
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = "3";
            context.ApplyContext.NewStateObject = new EnqueuedState();

            new RunWindowAttribute("09:00-17:00").OnStateElection(context.Object);

            Assert.IsType<EnqueuedState>(context.Object.CandidateState);
        }

        [Fact]
        public void GetNextStart_ReturnsTheEndOfTheGap_WhenTheWindowOpensInTheSkippedHour()
        {
            // On 2026-03-29 Berlin clocks jump from 02:00 to 03:00 (01:00 UTC), so 02:00 doesn't exist that night
            var attribute = new RunWindowAttribute("02:00-04:00", BerlinTimeZoneId);

            var next = attribute.GetNextStart(Utc("2026-03-29 00:30"), out var allowed);

            Assert.False(allowed);
            Assert.Equal(Utc("2026-03-29 01:00"), next);
        }

        [Fact]
        public void GetNextStart_ReturnsTheEndOfTheGap_WhenABlackoutEndsInTheSkippedHour()
        {
            var attribute = new BlackoutAttribute("00:00-02:30", BerlinTimeZoneId);

            var next = attribute.GetNextStart(Utc("2026-03-29 00:30"), out var allowed);

            Assert.False(allowed);
            Assert.Equal(Utc("2026-03-29 01:00"), next);
        }

        [Fact]
        public void OnStateElection_SchedulesTheJob_WhenTheWindowOpensInTheSkippedHour()
        {
            RunWindowAttribute.Clock = () => Utc("2026-03-29 00:30");
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = "4";
            context.ApplyContext.NewStateObject = new ProcessingState("server", "worker");

            new RunWindowAttribute("02:00-04:00", BerlinTimeZoneId).OnStateElection(context.Object);

            var scheduled = Assert.IsType<ScheduledState>(context.Object.CandidateState);
            Assert.Equal(Utc("2026-03-29 01:00"), scheduled.EnqueueAt);
        }

        private static string BerlinTimeZoneId => OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin";
    }
}
