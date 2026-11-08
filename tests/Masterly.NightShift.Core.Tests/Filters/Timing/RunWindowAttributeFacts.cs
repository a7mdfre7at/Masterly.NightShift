using System;
using System.Collections.Generic;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
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
            var attribute = new RunWindowAttribute("09:00-17:00", PlatformHelper.IsRunningOnWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

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

        [Theory]
        [InlineData("2026-11-04 10:00", false, "2026-11-04 17:00")] // Wednesday, inside the blackout
        [InlineData("2026-11-04 18:00", true, "2026-11-04 18:00")]  // Wednesday, outside it
        [InlineData("2026-11-07 10:00", true, "2026-11-07 10:00")]  // Saturday: no blackout
        [InlineData("2026-11-08 12:00", true, "2026-11-08 12:00")]  // Sunday: no blackout
        public void Blackout_AppliesOnWeekdaysOnly_WhenAsked(string now, bool allowed, string next)
        {
            var attribute = new BlackoutAttribute("09:00-17:00") { WeekdaysOnly = true };

            var result = attribute.GetNextStart(Utc(now), out var allowedNow);

            Assert.Equal(allowed, allowedNow);
            Assert.Equal(Utc(next), result);
        }

        [Theory]
        [InlineData("2026-11-06 23:00", false, "2026-11-07 00:00")] // Friday night: lifted when Saturday starts
        [InlineData("2026-11-07 03:00", true, "2026-11-07 03:00")]  // Saturday early morning
        [InlineData("2026-11-08 23:00", true, "2026-11-08 23:00")]  // Sunday night
        [InlineData("2026-11-09 03:00", false, "2026-11-09 06:00")] // Monday early morning
        public void Blackout_WithWeekdaysOnly_DecidesAnOvernightWindowByTheDayOfTheMoment(string now, bool allowed, string next)
        {
            var attribute = new BlackoutAttribute("22:00-06:00") { WeekdaysOnly = true };

            var result = attribute.GetNextStart(Utc(now), out var allowedNow);

            Assert.Equal(allowed, allowedNow);
            Assert.Equal(Utc(next), result);
        }

        [Fact]
        public void Blackout_WithoutWeekdaysOnly_AppliesAtTheWeekendToo()
        {
            var attribute = new BlackoutAttribute("09:00-17:00");

            var result = attribute.GetNextStart(Utc("2026-11-07 10:00"), out var allowedNow);

            Assert.False(allowedNow);
            Assert.Equal(Utc("2026-11-07 17:00"), result);
        }

        [Fact]
        public void GetNextStart_OpensAnOvernightWeekdayWindow_AtMidnightOnMonday()
        {
            var attribute = new RunWindowAttribute("22:00-06:00") { WeekdaysOnly = true };

            // Sunday 23:00 is inside the window but on a Sunday; Monday 00:00 is inside it and on a weekday
            var result = attribute.GetNextStart(Utc("2026-11-08 23:00"), out var allowedNow);

            Assert.False(allowedNow);
            Assert.Equal(Utc("2026-11-09 00:00"), result);
        }

        [Fact]
        public void OnStateElection_RemembersTheQueueGivenOnlyByTheEnqueuedState_WhenItReschedules()
        {
            RunWindowAttribute.Clock = () => Utc("2026-11-04 20:00");
            _context.ApplyContext.OldStateName = EnqueuedState.StateName;
            _context.ApplyContext.Connection
                .Setup(x => x.GetStateData("1"))
                .Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string> { { "Queue", "reports" } } });

            new RunWindowAttribute("09:00-17:00").OnStateElection(_context.Object);

            Assert.IsType<ScheduledState>(_context.Object.CandidateState);
            _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("1", DrainModeFilter.HeldQueueParameter, "\"reports\""));
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
            var timeZoneId = PlatformHelper.IsRunningOnWindows() ? "W. Europe Standard Time" : "Europe/Berlin";

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

        [Fact]
        public void GetNextStart_PicksTheFirstPass_WhenTheWindowOpensInTheRepeatedHour()
        {
            // On 2026-10-25 Berlin clocks go back from 03:00 CEST to 02:00 CET (01:00 UTC): 02:30 happens at 00:30 UTC
            // and again at 01:30 UTC. At 02:10 CEST the window opens twenty minutes later, not an hour and twenty.
            var attribute = new RunWindowAttribute("02:30-04:00", BerlinTimeZoneId);

            var next = attribute.GetNextStart(Utc("2026-10-25 00:10"), out var allowed);

            Assert.False(allowed);
            Assert.Equal(Utc("2026-10-25 00:30"), next);
        }

        [Fact]
        public void GetNextStart_PicksTheSecondPass_WhenTheFirstOneIsOver()
        {
            // 01:10 UTC is 02:10 CET, the second pass of that hour: the blackout ends at the second 02:30
            var attribute = new BlackoutAttribute("02:00-02:30", BerlinTimeZoneId);

            var next = attribute.GetNextStart(Utc("2026-10-25 01:10"), out var allowed);

            Assert.False(allowed);
            Assert.Equal(Utc("2026-10-25 01:30"), next);
        }

        [Fact]
        public void OnStateElection_SchedulesTheJob_ForTheFirstPassOfTheRepeatedHour()
        {
            RunWindowAttribute.Clock = () => Utc("2026-10-25 00:10");
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = "5";
            context.ApplyContext.NewStateObject = new ProcessingState("server", "worker");

            new RunWindowAttribute("02:30-04:00", BerlinTimeZoneId).OnStateElection(context.Object);

            var scheduled = Assert.IsType<ScheduledState>(context.Object.CandidateState);
            Assert.Equal(Utc("2026-10-25 00:30"), scheduled.EnqueueAt);
        }

        private static string BerlinTimeZoneId => PlatformHelper.IsRunningOnWindows() ? "W. Europe Standard Time" : "Europe/Berlin";
    }
}
