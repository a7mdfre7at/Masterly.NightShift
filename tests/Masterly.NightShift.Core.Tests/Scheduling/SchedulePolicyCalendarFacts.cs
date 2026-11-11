using System.Globalization;

namespace Masterly.NightShift.Core.Tests;

public sealed class SchedulePolicyCalendarFacts : IDisposable
{
    private static readonly SchedulePolicy BusinessHours = new SchedulePolicy("business-hours")
        .InTimeZone(SchedulePolicyFacts.BerlinTimeZoneId)
        .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
        .AllowBetween("09:00", "17:00");

    public void Dispose()
    {
        ScheduleDateProviders.Clear();
    }

    [Fact]
    public void Evaluate_AllowsAnInstantInsideTheRules()
    {
        // Wednesday 2026-11-18, 10:00 in Berlin (UTC+1)
        SchedulePolicyDecision decision = Calendar(BusinessHours).Evaluate(Utc("2026-11-18 09:00"));

        Assert.True(decision.Allowed);
        Assert.Null(decision.Reason);
        Assert.Equal("business-hours", decision.Policy);
        Assert.Equal(DateTimeKind.Utc, decision.Instant.Kind);
    }

    [Fact]
    public void Evaluate_BlocksADayThatIsNotAllowed()
    {
        SchedulePolicyDecision decision = Calendar(BusinessHours).Evaluate(Utc("2026-11-21 10:00"));

        Assert.False(decision.Allowed);
        Assert.Equal("policy 'business-hours': Saturday isn't an allowed day", decision.Reason);
    }

    [Fact]
    public void Evaluate_BlocksATimeOutsideTheWindows_InThePolicysTimeZone()
    {
        // 16:30 UTC is 17:30 in Berlin
        SchedulePolicyDecision decision = Calendar(BusinessHours).Evaluate(Utc("2026-11-18 16:30"));

        Assert.False(decision.Allowed);
        Assert.StartsWith("policy 'business-hours': 17:30 is outside 09:00-17:00", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AllowsAnyTime_OfAPolicyWithoutRules()
    {
        Assert.True(Calendar(new SchedulePolicy("any")).IsAllowed(Utc("2026-11-21 03:00")));
    }

    [Fact]
    public void Evaluate_AllowsTheBothEndsOfAnOvernightWindow()
    {
        SchedulePolicy night = new SchedulePolicy("night").AllowBetween("22:00", "06:00");

        Assert.True(Calendar(night).IsAllowed(Utc("2026-11-18 23:00")));
        Assert.True(Calendar(night).IsAllowed(Utc("2026-11-19 05:59")));
        Assert.False(Calendar(night).IsAllowed(Utc("2026-11-19 06:00")));
        Assert.False(Calendar(night).IsAllowed(Utc("2026-11-19 21:59")));
    }

    [Fact]
    public void Evaluate_BlocksExcludedDates_AndAnnualDates()
    {
        SchedulePolicy policy = new SchedulePolicy("calendar").ExcludeDate(new DateOnly(2026, 12, 24)).ExcludeAnnualDate(12, 25);

        Assert.Equal("policy 'calendar': 2026-12-24 is excluded", Calendar(policy).Evaluate(Utc("2026-12-24 12:00")).Reason);
        Assert.Equal("policy 'calendar': 12-25 is excluded every year", Calendar(policy).Evaluate(Utc("2031-12-25 12:00")).Reason);
        Assert.True(Calendar(policy).IsAllowed(Utc("2027-12-24 12:00")));
    }

    [Fact]
    public void Evaluate_ReadsDatesInThePolicysTimeZone()
    {
        // 23:30 UTC on the 24th is already the 25th in Berlin
        SchedulePolicy policy = new SchedulePolicy("christmas").InTimeZone(SchedulePolicyFacts.BerlinTimeZoneId).ExcludeAnnualDate(12, 25);

        Assert.False(Calendar(policy).IsAllowed(Utc("2026-12-24 23:30")));
        Assert.True(Calendar(policy).IsAllowed(Utc("2026-12-24 22:30")));
    }

    [Fact]
    public void Evaluate_BlocksTheDatesOfADateProvider()
    {
        ScheduleDateProviders.Set("holidays", new Dates(new DateOnly(2026, 11, 18)));
        SchedulePolicy policy = new SchedulePolicy("p").ExcludeDatesFrom("holidays");

        Assert.Equal("policy 'p': 2026-11-18 is excluded by date provider 'holidays'", Calendar(policy).Evaluate(Utc("2026-11-18 10:00")).Reason);
        Assert.True(Calendar(policy).IsAllowed(Utc("2026-11-19 10:00")));
    }

    [Fact]
    public void Evaluate_BlocksEverything_WhenTheDateProviderIsNotRegistered()
    {
        SchedulePolicy policy = new SchedulePolicy("p").ExcludeDatesFrom("holidays");

        SchedulePolicyDecision decision = Calendar(policy).Evaluate(Utc("2026-11-18 10:00"));

        Assert.False(decision.Allowed);
        Assert.Equal("policy 'p': date provider 'holidays' isn't registered on this server", decision.Reason);
        Assert.Null(Calendar(policy).FindNextAllowed(Utc("2026-11-18 10:00")));
    }

    [Fact]
    public void Evaluate_BlocksTheDate_WhenTheDateProviderThrows()
    {
        ScheduleDateProviders.Set("holidays", new ThrowingDates());
        SchedulePolicy policy = new SchedulePolicy("p").ExcludeDatesFrom("holidays");

        SchedulePolicyDecision decision = Calendar(policy).Evaluate(Utc("2026-11-18 10:00"));

        Assert.False(decision.Allowed);
        Assert.Equal("policy 'p': date provider 'holidays' failed: database down", decision.Reason);
    }

    [Fact]
    public void Evaluate_BlocksWhatARequiredPolicyBlocks()
    {
        SchedulePolicy trading = new SchedulePolicy("trading").ExcludeAnnualDate(12, 31);
        SchedulePolicy policy = new SchedulePolicy("p").Require("trading");

        SchedulePolicyDecision decision = Calendar(policy, trading).Evaluate(Utc("2026-12-31 10:00"));

        Assert.False(decision.Allowed);
        Assert.Equal("policy 'p' requires 'trading', and policy 'trading': 12-31 is excluded every year", decision.Reason);
        Assert.True(Calendar(policy, trading).IsAllowed(Utc("2026-12-30 10:00")));
    }

    [Fact]
    public void Evaluate_BlocksWhatAnExceptedPolicyAllows()
    {
        SchedulePolicy maintenance = new SchedulePolicy("maintenance").AllowWeekdays(DayOfWeek.Sunday).AllowBetween("02:00", "04:00");
        SchedulePolicy policy = new SchedulePolicy("p").ExceptWhen("maintenance");

        Assert.Equal("policy 'p' blocks the times policy 'maintenance' allows", Calendar(policy, maintenance).Evaluate(Utc("2026-11-22 03:00")).Reason);
        Assert.True(Calendar(policy, maintenance).IsAllowed(Utc("2026-11-22 05:00")));
        Assert.True(Calendar(policy, maintenance).IsAllowed(Utc("2026-11-23 03:00")));
    }

    [Fact]
    public void Evaluate_IgnoresReferencesToPoliciesThatDoNotExist()
    {
        SchedulePolicy policy = new SchedulePolicy("p").Require("gone").ExceptWhen("also-gone");

        Assert.True(Calendar(policy).IsAllowed(Utc("2026-11-18 10:00")));
    }

    [Fact]
    public void Evaluate_BlocksEverything_WhenPoliciesReferToEachOther()
    {
        SchedulePolicy first = new SchedulePolicy("a").Require("b");
        SchedulePolicy second = new SchedulePolicy("b").Require("a");

        SchedulePolicyDecision decision = SchedulePolicyCalendar.FromPolicies("a", [first, second], GlobalTimeZoneResolver.Current).Evaluate(Utc("2026-11-18 10:00"));

        Assert.False(decision.Allowed);
        Assert.Contains("refers back to itself", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FindNextAllowed_ReturnsTheInstant_WhenItIsAllowed()
    {
        DateTime instant = Utc("2026-11-18 09:30:15");

        Assert.Equal(instant, Calendar(BusinessHours).FindNextAllowed(instant));
    }

    [Fact]
    public void FindNextAllowed_JumpsOverTheWeekend_ToTheWindowsStart()
    {
        // Friday 18:00 in Berlin; Monday 09:00 in Berlin is 08:00 UTC
        Assert.Equal(Utc("2026-11-23 08:00"), Calendar(BusinessHours).FindNextAllowed(Utc("2026-11-20 17:00")));
    }

    [Fact]
    public void FindNextAllowed_SkipsExcludedDates()
    {
        SchedulePolicy policy = new SchedulePolicy("p").ExcludeDate(new DateOnly(2026, 11, 18)).ExcludeDate(new DateOnly(2026, 11, 19));

        Assert.Equal(Utc("2026-11-20 00:00"), Calendar(policy).FindNextAllowed(Utc("2026-11-18 10:00")));
    }

    [Fact]
    public void FindNextAllowed_HonoursRequiredAndExceptedPolicies()
    {
        SchedulePolicy maintenance = new SchedulePolicy("maintenance").AllowBetween("09:00", "10:00");
        SchedulePolicy policy = new SchedulePolicy("p").AllowBetween("08:00", "12:00").ExceptWhen("maintenance");

        Assert.Equal(Utc("2026-11-18 10:00"), Calendar(policy, maintenance).FindNextAllowed(Utc("2026-11-18 09:15")));
    }

    [Fact]
    public void FindNextAllowed_ReturnsNull_WhenNothingIsAllowedWithinTenYears()
    {
        SchedulePolicy never = new SchedulePolicy("never").AllowWeekdays(DayOfWeek.Monday).ExceptWhen("mondays");
        SchedulePolicy mondays = new SchedulePolicy("mondays").AllowWeekdays(DayOfWeek.Monday);

        Assert.Null(Calendar(never, mondays).FindNextAllowed(Utc("2026-11-18 10:00")));
    }

    [Fact]
    public void FindNextAllowed_FindsAnInstantMonthsAhead_AcrossManyExcludedDates()
    {
        // Allows only leap days until the end of 2029: the next one is 29 February 2028
        SchedulePolicy onlyLeapDays = new("only-leap-days");
        for (DateOnly day = new(2026, 1, 1); day.Year < 2030; day = day.AddDays(1))
        {
            if (day is not { Month: 2, Day: 29 }) onlyLeapDays.ExcludeDate(day);
        }

        SchedulePolicy policy = new SchedulePolicy("p").Require("only-leap-days");

        Assert.Equal(Utc("2028-02-29 00:00"), Calendar(policy, onlyLeapDays).FindNextAllowed(Utc("2026-11-18 10:00")));
    }

    [Fact]
    public void FindNextAllowed_LandsAfterTheSkippedHour_WhenClocksGoForward()
    {
        // In Berlin, 2026-03-29 02:00 jumps to 03:00 (00:30 UTC is 01:30 CET)
        SchedulePolicy policy = new SchedulePolicy("night").InTimeZone(SchedulePolicyFacts.BerlinTimeZoneId).AllowBetween("02:30", "05:00");

        // 02:30 doesn't exist that night: the window opens at 03:00 CEST, 01:00 UTC
        Assert.Equal(Utc("2026-03-29 01:00"), Calendar(policy).FindNextAllowed(Utc("2026-03-29 00:30")));
    }

    [Fact]
    public void FindNextAllowed_TakesTheFirstOfARepeatedTime_WhenClocksGoBack()
    {
        // In Berlin, 2026-10-25 03:00 CEST goes back to 02:00 CET: 02:30 happens at 00:30 and 01:30 UTC
        SchedulePolicy policy = new SchedulePolicy("night").InTimeZone(SchedulePolicyFacts.BerlinTimeZoneId).AllowBetween("02:30", "02:45");

        Assert.Equal(Utc("2026-10-25 00:30"), Calendar(policy).FindNextAllowed(Utc("2026-10-25 00:10")));
    }

    [Fact]
    public void Load_ReadsThePolicyAndItsReferences_FromStorage()
    {
        PolicyStorage storage = new();
        storage.Add(new SchedulePolicy("trading").ExcludeAnnualDate(12, 31));
        storage.Add(new SchedulePolicy("p").Require("trading").ExceptWhen("gone"));

        SchedulePolicyCalendar calendar = SchedulePolicyCalendar.Load(storage.Connection.Object, "p", GlobalTimeZoneResolver.Current);

        Assert.NotNull(calendar);
        Assert.Equal("p", calendar.Name);
        Assert.Equal(["trading"], calendar.Policy?.Required);
        Assert.False(calendar.IsAllowed(Utc("2026-12-31 10:00")));
        Assert.True(calendar.IsAllowed(Utc("2026-12-30 10:00")));
    }

    [Fact]
    public void Load_ReturnsNull_WhenThePolicyDoesNotExist()
    {
        Assert.Null(SchedulePolicyCalendar.Load(new PolicyStorage().Connection.Object, "nope", GlobalTimeZoneResolver.Current));
    }

    [Fact]
    public void Load_BlocksEverything_WhenThePolicyCannotBeRead()
    {
        PolicyStorage storage = new();
        storage.Hashes[SchedulePolicies.GetKey("broken")] = new Dictionary<string, string> { ["Days"] = "Someday" };

        SchedulePolicyCalendar calendar = SchedulePolicyCalendar.Load(storage.Connection.Object, "broken", GlobalTimeZoneResolver.Current);
        SchedulePolicyDecision decision = calendar!.Evaluate(Utc("2026-11-18 10:00"));

        Assert.False(decision.Allowed);
        Assert.StartsWith("policy 'broken' can't be read", decision.Reason, StringComparison.Ordinal);
        Assert.Null(calendar.FindNextAllowed(Utc("2026-11-18 10:00")));
    }

    [Fact]
    public void Load_BlocksEverything_WhenTheTimeZoneIsUnknownOnThisServer()
    {
        PolicyStorage storage = new();
        storage.Hashes[SchedulePolicies.GetKey("far")] = new Dictionary<string, string> { ["TimeZone"] = "Nowhere/Never" };

        SchedulePolicyDecision decision = SchedulePolicyCalendar.Load(storage.Connection.Object, "far", GlobalTimeZoneResolver.Current)!.Evaluate(Utc("2026-11-18 10:00"));

        Assert.False(decision.Allowed);
        Assert.Contains("time zone 'Nowhere/Never'", decision.Reason, StringComparison.Ordinal);
    }

    private static SchedulePolicyCalendar Calendar(SchedulePolicy policy, params SchedulePolicy[] others)
    {
        return SchedulePolicyCalendar.FromPolicies(policy.Name, others.Prepend(policy), GlobalTimeZoneResolver.Current);
    }

    private static DateTime Utc(string value)
    {
        return DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }

    private sealed class Dates(params DateOnly[] dates) : IScheduleDateProvider
    {
        public bool Contains(DateOnly day) => dates.Contains(day);
    }

    private sealed class ThrowingDates : IScheduleDateProvider
    {
        public bool Contains(DateOnly day) => throw new InvalidOperationException("database down");
    }
}
