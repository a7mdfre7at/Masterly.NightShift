namespace Masterly.NightShift.Core.Tests;

public class SchedulePolicyFacts
{
    [Fact]
    public void Ctor_LowerCasesTheName_AndStartsWithoutRules()
    {
        SchedulePolicy policy = new(" Business-Hours ");

        Assert.Equal("business-hours", policy.Name);
        Assert.Equal(TimeZoneInfo.Utc.Id, policy.TimeZoneId);
        Assert.Empty(policy.Days);
        Assert.Empty(policy.Windows);
        Assert.Empty(policy.ExcludedDates);
        Assert.Empty(policy.ExcludedAnnualDates);
        Assert.Empty(policy.DateProviders);
        Assert.Empty(policy.Required);
        Assert.Empty(policy.Excepted);
        Assert.Equal(0, policy.Revision);
        Assert.Null(policy.UpdatedAt);
        Assert.Null(policy.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-starts-with-dash")]
    [InlineData("has space")]
    [InlineData("has,comma")]
    [InlineData("has:colon")]
    [InlineData("ünicode")]
    public void Ctor_ThrowsAnException_WhenTheNameIsInvalid(string name)
    {
        Assert.Throws<ArgumentException>("name", () => new SchedulePolicy(name));
    }

    [Fact]
    public void Ctor_AcceptsTheLongestName_AndRefusesALongerOne()
    {
        Assert.Equal(64, new SchedulePolicy(new string('a', 64)).Name.Length);
        Assert.Throws<ArgumentException>("name", () => new SchedulePolicy(new string('a', 65)));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenTheNameIsNull()
    {
        Assert.Throws<ArgumentNullException>("name", () => new SchedulePolicy(null));
    }

    [Fact]
    public void NormalizeName_ReturnsTheLowerCasedName()
    {
        Assert.Equal("public.holidays_de-2", SchedulePolicy.NormalizeName("Public.Holidays_DE-2"));
        Assert.Throws<ArgumentException>("name", () => SchedulePolicy.NormalizeName("no way"));
    }

    [Fact]
    public void AllowWeekdays_AddsTheDays_AndIgnoresRepeats()
    {
        SchedulePolicy policy = new SchedulePolicy("p")
            .AllowWeekdays(DayOfWeek.Friday, DayOfWeek.Monday)
            .AllowWeekdays(DayOfWeek.Monday);

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], policy.Days);
    }

    [Fact]
    public void AllowWeekdays_ThrowsAnException_ForNoDay_OrAnUndefinedOne()
    {
        SchedulePolicy policy = new("p");

        Assert.Throws<ArgumentNullException>("days", () => policy.AllowWeekdays(null));
        Assert.Throws<ArgumentException>("days", () => policy.AllowWeekdays());
        Assert.Throws<ArgumentOutOfRangeException>("days", () => policy.AllowWeekdays((DayOfWeek)7));
    }

    [Fact]
    public void AllowBetween_ParsesTheTimes_AndKeepsEachWindowOnce()
    {
        SchedulePolicy policy = new SchedulePolicy("p")
            .AllowBetween("09:00", "12:30")
            .AllowBetween("9:00", "12:30:00")
            .AllowBetween(TimeSpan.FromHours(22), TimeSpan.FromHours(6));

        Assert.Equal(
            [new ScheduleWindow(TimeSpan.FromHours(9), new TimeSpan(12, 30, 0)), new ScheduleWindow(TimeSpan.FromHours(22), TimeSpan.FromHours(6))],
            policy.Windows);
    }

    [Theory]
    [InlineData("24:00", "10:00")]
    [InlineData("nine", "10:00")]
    [InlineData("09:00", "")]
    public void AllowBetween_ThrowsAnException_WhenATimeIsInvalid(string start, string end)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SchedulePolicy("p").AllowBetween(start, end));
    }

    [Fact]
    public void AllowBetween_ThrowsAnException_ForAnEmptyWindow()
    {
        Assert.Throws<ArgumentException>("end", () => new SchedulePolicy("p").AllowBetween("10:00", "10:00"));
    }

    [Fact]
    public void ExcludeDate_AndExcludeAnnualDate_KeepEachDateOnce_InOrder()
    {
        SchedulePolicy policy = new SchedulePolicy("p")
            .ExcludeDate(new DateOnly(2026, 12, 31))
            .ExcludeDate(new DateOnly(2026, 12, 24))
            .ExcludeDate(new DateOnly(2026, 12, 31))
            .ExcludeAnnualDate(12, 25)
            .ExcludeAnnualDate(1, 1)
            .ExcludeAnnualDate(12, 25);

        Assert.Equal([new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 31)], policy.ExcludedDates);
        Assert.Equal([new AnnualDate(1, 1), new AnnualDate(12, 25)], policy.ExcludedAnnualDates);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(13, 1)]
    [InlineData(2, 30)]
    [InlineData(4, 31)]
    [InlineData(1, 0)]
    public void ExcludeAnnualDate_ThrowsAnException_ForADayThatDoesNotExist(int month, int day)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchedulePolicy("p").ExcludeAnnualDate(month, day));
    }

    [Fact]
    public void ExcludeAnnualDate_AcceptsTheLeapDay()
    {
        Assert.Equal([new AnnualDate(2, 29)], new SchedulePolicy("p").ExcludeAnnualDate(2, 29).ExcludedAnnualDates);
    }

    [Fact]
    public void ExcludeDatesFrom_Require_AndExceptWhen_LowerCaseTheNames_AndKeepEachOnce()
    {
        SchedulePolicy policy = new SchedulePolicy("p")
            .ExcludeDatesFrom("Holidays")
            .ExcludeDatesFrom("holidays")
            .Require("Trading")
            .Require("trading")
            .ExceptWhen("Maintenance");

        Assert.Equal(["holidays"], policy.DateProviders);
        Assert.Equal(["trading"], policy.Required);
        Assert.Equal(["maintenance"], policy.Excepted);
    }

    [Fact]
    public void Require_AndExceptWhen_ThrowAnException_ForThePolicyItself()
    {
        SchedulePolicy policy = new("p");

        Assert.Throws<ArgumentException>("policyName", () => policy.Require("P"));
        Assert.Throws<ArgumentException>("policyName", () => policy.ExceptWhen("p"));
    }

    [Fact]
    public void Require_AndExceptWhen_ThrowAnException_ForAPolicyAlreadyOnTheOtherSide()
    {
        Assert.Throws<ArgumentException>("policyName", () => new SchedulePolicy("p").Require("x").ExceptWhen("x"));
        Assert.Throws<ArgumentException>("policyName", () => new SchedulePolicy("p").ExceptWhen("x").Require("x"));
    }

    [Fact]
    public void ReferenceMethods_ThrowAnException_ForInvalidNames()
    {
        SchedulePolicy policy = new("p");

        Assert.Throws<ArgumentNullException>("providerName", () => policy.ExcludeDatesFrom(null));
        Assert.Throws<ArgumentException>("providerName", () => policy.ExcludeDatesFrom("a b"));
        Assert.Throws<ArgumentNullException>("policyName", () => policy.Require(null));
        Assert.Throws<ArgumentException>("policyName", () => policy.ExceptWhen("a b"));
    }

    [Fact]
    public void InTimeZone_ResolvesTheId_OrTakesTheTimeZone()
    {
        Assert.Equal(BerlinTimeZoneId, new SchedulePolicy("p").InTimeZone(BerlinTimeZoneId).TimeZoneId);
        Assert.Equal(TimeZoneInfo.Local.Id, new SchedulePolicy("p").InTimeZone(TimeZoneInfo.Local).TimeZoneId);
    }

    [Fact]
    public void InTimeZone_ThrowsAnException_ForAnUnknownId_OrNull()
    {
        Assert.ThrowsAny<Exception>(() => new SchedulePolicy("p").InTimeZone("Nowhere/Never"));
        Assert.Throws<ArgumentNullException>("timeZoneId", () => new SchedulePolicy("p").InTimeZone((string)null));
        Assert.Throws<ArgumentNullException>("timeZone", () => new SchedulePolicy("p").InTimeZone((TimeZoneInfo)null));
    }

    [Fact]
    public void WithDescription_TrimsIt_ClearsABlankOne_AndRefusesALongOne()
    {
        Assert.Equal("Office hours", new SchedulePolicy("p").WithDescription("  Office hours ").Description);
        Assert.Null(new SchedulePolicy("p").WithDescription("Office").WithDescription(" ").Description);
        Assert.Throws<ArgumentOutOfRangeException>("description", () => new SchedulePolicy("p").WithDescription(new string('x', 501)));
    }

    [Fact]
    public void ToFields_AndFromFields_RoundTripEveryRule()
    {
        SchedulePolicy policy = new SchedulePolicy("business-hours")
            .WithDescription("Office hours")
            .InTimeZone(BerlinTimeZoneId)
            .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Friday)
            .AllowBetween("09:00", "12:00")
            .AllowBetween("13:00:30", "17:00")
            .ExcludeDate(new DateOnly(2026, 12, 24))
            .ExcludeAnnualDate(12, 25)
            .ExcludeDatesFrom("holidays")
            .Require("trading")
            .ExceptWhen("maintenance");

        Dictionary<string, string> fields = policy.ToFields();
        fields["Revision"] = "4";
        fields["UpdatedAt"] = JobHelper.SerializeDateTime(new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));

        SchedulePolicy read = SchedulePolicy.FromFields("business-hours", fields);

        Assert.Equal("Monday,Friday", fields["Days"]);
        Assert.Equal("09:00-12:00,13:00:30-17:00", fields["Windows"]);
        Assert.Equal("2026-12-24", fields["ExcludedDates"]);
        Assert.Equal("12-25", fields["AnnualDates"]);
        Assert.True(read.HasSameRules(policy));
        Assert.Equal(4, read.Revision);
        Assert.Equal(new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc), read.UpdatedAt);
        Assert.Equal("Office hours", read.Description);
        Assert.Equal(policy.Windows, read.Windows);
        Assert.Equal(["trading"], read.Required);
        Assert.Equal(["maintenance"], read.Excepted);
    }

    [Fact]
    public void ToFields_WritesEveryField_SoAClearedRuleOverwritesTheStoredOne()
    {
        Dictionary<string, string> fields = new SchedulePolicy("p").ToFields();

        Assert.Equal(string.Empty, fields["Days"]);
        Assert.Equal(string.Empty, fields["Windows"]);
        Assert.Equal(string.Empty, fields["Description"]);
        Assert.Equal(TimeZoneInfo.Utc.Id, fields["TimeZone"]);
    }

    [Fact]
    public void FromFields_ReadsMissingFields_AsNoRule()
    {
        SchedulePolicy policy = SchedulePolicy.FromFields("p", new Dictionary<string, string> { ["Revision"] = "2" });

        Assert.True(policy.HasSameRules(new SchedulePolicy("p")));
        Assert.Equal(2, policy.Revision);
    }

    [Theory]
    [InlineData("Days", "Someday")]
    [InlineData("Windows", "9-")]
    [InlineData("ExcludedDates", "2026-13-01")]
    [InlineData("AnnualDates", "02-30")]
    [InlineData("Require", "a b")]
    [InlineData("Revision", "x")]
    public void FromFields_ThrowsAFormatException_ForAFieldItCannotRead(string field, string value)
    {
        Assert.Throws<FormatException>(() => SchedulePolicy.FromFields("p", new Dictionary<string, string> { [field] = value }));
    }

    [Fact]
    public void HasSameRules_ComparesTheRules_NotTheRevisions()
    {
        SchedulePolicy first = new SchedulePolicy("p").AllowBetween("09:00", "17:00");
        SchedulePolicy second = new SchedulePolicy("p").AllowBetween("09:00", "17:00");
        second.Revision = 9;

        Assert.True(first.HasSameRules(second));
        Assert.False(first.HasSameRules(new SchedulePolicy("p").AllowBetween("09:00", "18:00")));
    }

    [Fact]
    public void ScheduleWindow_Default_ContainsNothing()
    {
        ScheduleWindow empty = default;

        Assert.False(empty.Contains(TimeSpan.Zero));
        Assert.False(empty.Contains(TimeSpan.FromHours(12)));
    }

    [Fact]
    public void ScheduleWindow_Contains_HandlesDayAndOvernightWindows()
    {
        ScheduleWindow day = new(TimeSpan.FromHours(9), TimeSpan.FromHours(17));
        ScheduleWindow night = new(TimeSpan.FromHours(22), TimeSpan.FromHours(6));

        Assert.True(day.Contains(TimeSpan.FromHours(9)));
        Assert.False(day.Contains(TimeSpan.FromHours(17)));
        Assert.True(night.Contains(TimeSpan.FromHours(23)));
        Assert.True(night.Contains(TimeSpan.FromHours(5.5)));
        Assert.False(night.Contains(TimeSpan.FromHours(6)));
        Assert.False(night.Contains(TimeSpan.FromHours(12)));
    }

    [Fact]
    public void ScheduleWindow_ParseAndToString_RoundTrip()
    {
        Assert.Equal("22:00-06:15", ScheduleWindow.Parse(" 22:00 - 6:15 ").ToString());
        Assert.Throws<FormatException>(() => ScheduleWindow.Parse("22:00"));
        Assert.Throws<FormatException>(() => ScheduleWindow.Parse("10:00-10:00"));
        Assert.Throws<ArgumentNullException>("text", () => ScheduleWindow.Parse(null));
    }

    [Fact]
    public void ScheduleWindow_Ctor_ThrowsAnException_ForATimeOutsideADay()
    {
        Assert.Throws<ArgumentOutOfRangeException>("start", () => new ScheduleWindow(TimeSpan.FromHours(-1), TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>("end", () => new ScheduleWindow(TimeSpan.Zero, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void AnnualDate_ParsesComparesAndMatches()
    {
        AnnualDate christmas = AnnualDate.Parse("12-25");

        Assert.Equal("12-25", christmas.ToString());
        Assert.True(christmas.Matches(new DateOnly(2030, 12, 25)));
        Assert.False(christmas.Matches(new DateOnly(2030, 12, 26)));
        Assert.True(new AnnualDate(1, 1) < christmas);
        Assert.True(christmas >= new AnnualDate(12, 25));
        Assert.True(christmas > new AnnualDate(12, 24));
        Assert.True(christmas <= new AnnualDate(12, 31));
        Assert.Throws<FormatException>(() => AnnualDate.Parse("2-30"));
        Assert.Throws<FormatException>(() => AnnualDate.Parse("christmas"));
    }

    internal static string BerlinTimeZoneId => PlatformHelper.IsRunningOnWindows() ? "W. Europe Standard Time" : "Europe/Berlin";
}
