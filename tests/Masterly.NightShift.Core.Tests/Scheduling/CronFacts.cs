namespace Masterly.NightShift.Core.Tests;

public class CronFacts
{
    [Theory]
    [InlineData(1, "* * * * * *")]
    [InlineData(15, "*/15 * * * * *")]
    [InlineData(30, "*/30 * * * * *")]
    [InlineData(60, "* * * * *")]
    [InlineData(300, "*/5 * * * *")]
    [InlineData(1800, "*/30 * * * *")]
    [InlineData(3600, "0 * * * *")]
    [InlineData(7200, "0 */2 * * *")]
    [InlineData(43200, "0 */12 * * *")]
    [InlineData(86400, "0 0 * * *")]
    [InlineData(604800, "0 0 * * 1")]
    public void Every_ReturnsTheExpression_ForAnIntervalCronCanKeepExactly(int seconds, string expected)
    {
        string actual = Cron.Every(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(5, 40)]
    [InlineData(1800, 4)]
    [InlineData(21600, 4)]
    public void Every_ProducesEvenlySpacedRuns(int seconds, int runs)
    {
        TimeSpan interval = TimeSpan.FromSeconds(seconds);
        DateTime from = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        IReadOnlyList<DateTime> occurrences = Cron.GetNextOccurrences(Cron.Every(interval), TimeZoneInfo.Utc, from, runs);

        Assert.All(occurrences.Skip(1).Zip(occurrences), pair => Assert.Equal(interval, pair.First - pair.Second));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1500)]
    [InlineData(7000)]
    [InlineData(45000)]
    [InlineData(90000)]
    [InlineData(420000)]
    [InlineData(2700000)]
    [InlineData(5400000)]
    [InlineData(25200000)]
    [InlineData(172800000)]
    public void Every_ThrowsAnException_ForAnIntervalCronCanNotKeepExactly(int milliseconds)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => Cron.Every(TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("interval", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Every_ThrowsAnException_WhenTheIntervalIsNotPositive(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cron.Every(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Minutely_ReturnsFormattedString()
    {
        string expected = "* * * * *";
        string actual = Cron.Minutely();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hourly_WithoutMinute_ReturnsFormattedStringWithDefaults()
    {
        string expected = "0 * * * *";
        string actual = Cron.Hourly();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hourly_WithMinute_ReturnsFormattedStringWithMinute()
    {
        string expected = "5 * * * *";
        string actual = Cron.Hourly(5);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Daily_WithoutMinuteOrHour_ReturnsFormattedStringWithDefaults()
    {
        string expected = "0 0 * * *";
        string actual = Cron.Daily();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Daily_WithoutMinute_ReturnsFormattedStringWithHourAndZeroMinute()
    {
        string expected = "0 5 * * *";
        string actual = Cron.Daily(5);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Daily_WithMinuteAndHour_ReturnsFormattedStringWithHourAndMinute()
    {
        string expected = "5 5 * * *";
        string actual = Cron.Daily(5, 5);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Weekly_WithoutDayHourMinute_ReturnsFormattedStringWithDefaults()
    {
        string expected = "0 0 * * " + ((int)DayOfWeek.Monday).ToString();
        string actual = Cron.Weekly();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Weekly_WithDayWithoutHourMinute_ReturnsFormattedStringWithDay()
    {
        DayOfWeek day = DayOfWeek.Thursday;
        string expected = "0 0 * * " + ((int)day).ToString();
        string actual = Cron.Weekly(day);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Weekly_WithDayHourWithoutMinute_ReturnsFormattedStringWithDayHour()
    {
        DayOfWeek day = DayOfWeek.Thursday;
        int hour = 5;
        string expected = "0 " + hour.ToString() + " * * " + ((int)day).ToString();
        string actual = Cron.Weekly(day, hour);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Weekly_WithDayHourMinute_ReturnsFormattedStringWithDayHourMinute()
    {
        DayOfWeek day = DayOfWeek.Thursday;
        int hour = 5;
        int minute = 5;
        string expected = minute.ToString() + " " + hour.ToString() + " * * " + ((int)day).ToString();
        string actual = Cron.Weekly(day, hour, minute);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Monthly_WithoutDayHourMinute_ReturnsFormattedStringWithDefaults()
    {
        string expected = "0 0 1 * *";
        string actual = Cron.Monthly();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Monthly_WithoutHourMinuteWithDay_ReturnsFormattedStringWithDay()
    {
        int day = 6;
        string expected = "0 0 " + day.ToString() + " * *";
        string actual = Cron.Monthly(day);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Monthly_WithoutMinuteWithDayHour_ReturnsFormattedStringWithDayHour()
    {
        int day = 7;
        int hour = 4;
        string expected = "0 " + hour.ToString() + " " + day.ToString() + " * *";
        string actual = Cron.Monthly(day, hour);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Monthly_WithDayHourMinute_ReturnsFormattedStringWithDayHourMinute()
    {
        int day = 7;
        int hour = 4;
        int minute = 23;
        string expected = minute.ToString() + " " + hour.ToString() + " " + day.ToString() + " * *";
        string actual = Cron.Monthly(day, hour, minute);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Yearly_WithoutMonthDayHourMinute_ReturnsFormattedStringWithDefaults()
    {
        string expected = "0 0 1 1 *";
        string actual = Cron.Yearly();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Yearly_WithoutDayHourMinuteWithMonth_ReturnsFormattedStringWithMonth()
    {
        int month = 7;
        string expected = "0 0 1 " + month.ToString() + " *";
        string actual = Cron.Yearly(month);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Yearly_WithoutHourMinuteWithMonthDay_ReturnsFormattedStringWithMonthDay()
    {
        int month = 8;
        int day = 18;
        string expected = "0 0 " + day.ToString() + " " + month.ToString() + " *";
        string actual = Cron.Yearly(month, day);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Yearly_WithoutMinuteWithMonthDayHour_ReturnsFormattedStringWithMonthDayHour()
    {
        int month = 3;
        int day = 18;
        int hour = 14;
        string expected = "0 " + hour.ToString() + " " + day.ToString() + " " + month.ToString() + " *";
        string actual = Cron.Yearly(month, day, hour);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Yearly_WithMonthDayHourMinute_ReturnsFormattedStringWithMonthDayHourMinute()
    {
        int month = 4;
        int day = 17;
        int hour = 3;
        int minute = 45;
        string expected = minute.ToString() + " " + hour.ToString() + " " + day.ToString() + " " + month.ToString() + " *";
        string actual = Cron.Yearly(month, day, hour, minute);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Never_ReturnsFormattedString()
    {
        string expected = "0 0 31 2 *";
        string actual = Cron.Yearly(2, 31);
        Assert.Equal(expected, actual);
    }
}
