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
}