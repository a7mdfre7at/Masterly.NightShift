// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests;

public class RecurringJobOptionsFacts
{
    [Fact]
    public void Ctor_SetTheDefaultValues_ForProperties()
    {
        RecurringJobOptions options = new();

        Assert.Equal(TimeZoneInfo.Utc, options.TimeZone);
        Assert.Equal("default", options.QueueName);
        Assert.Equal(MisfireHandlingMode.Relaxed, options.MisfireHandling);
    }

    [Fact]
    public void SetTimeZone_ThrowsAnException_WhenValueIsNull()
    {
        RecurringJobOptions options = new();

        Assert.Throws<ArgumentNullException>(() => options.TimeZone = null);
    }

    [Fact]
    public void SetQueueName_ThrowsAnException_WhenValueIsNull()
    {
        RecurringJobOptions options = new();

        Assert.Throws<ArgumentNullException>(() => options.QueueName = null);
    }

    [Fact]
    public void SetQueueName_ThrowsAnException_WhenQueueNameHasInvalidFormat()
    {
        RecurringJobOptions options = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => options.QueueName = "UPPER_CASE");

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void SkipAfter_IsNull_ByDefault()
    {
        Assert.Null(new RecurringJobOptions().SkipAfter);
    }

    [Fact, GlobalLock(Reason = "Changes the default time zone")]
    public void DefaultTimeZone_IsUtc_AndNewOptionsUseIt()
    {
        Assert.Same(TimeZoneInfo.Utc, RecurringJobOptions.DefaultTimeZone);

        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(3), "Test", "Test");

        try
        {
            GlobalConfiguration.Configuration.UseDefaultRecurringJobTimeZone(zone);

            Assert.Same(zone, RecurringJobOptions.DefaultTimeZone);
            Assert.Same(zone, new RecurringJobOptions().TimeZone);
        }
        finally
        {
            RecurringJobOptions.DefaultTimeZone = TimeZoneInfo.Utc;
        }
    }

    [Fact]
    public void DefaultTimeZone_ThrowsAnException_WhenValueIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => RecurringJobOptions.DefaultTimeZone = null);
        Assert.Throws<ArgumentNullException>(() => GlobalConfiguration.Configuration.UseDefaultRecurringJobTimeZone(null));
    }
}
