namespace Masterly.NightShift.Core.Tests.Configuration;

public sealed class JobClockFacts : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void UtcNow_ReturnsTheTimeOfTheConfiguredProvider()
    {
        JobClock.TimeProvider = new FixedTimeProvider(FixedTime);

        Assert.Equal(FixedTime.UtcDateTime, JobClock.UtcNow);
    }

    [Fact, GlobalLock(Reason = "Reads the clock")]
    public void TimeProvider_DefaultsToTheSystemClock()
    {
        Assert.Same(TimeProvider.System, JobClock.TimeProvider);
    }

    [Fact]
    public void TimeProvider_ThrowsAnException_WhenValueIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => JobClock.TimeProvider = null);

        Assert.Equal("value", exception.ParamName);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void UseTimeProvider_SetsTheClock()
    {
        var provider = new FixedTimeProvider(FixedTime);

        GlobalConfiguration.Configuration.UseTimeProvider(provider);

        Assert.Same(provider, JobClock.TimeProvider);
    }

    [Fact]
    public void UseTimeProvider_ThrowsAnException_WhenProviderIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => GlobalConfiguration.Configuration.UseTimeProvider(null));

        Assert.Equal("timeProvider", exception.ParamName);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void NewState_TakesItsTimestampFromTheClock()
    {
        JobClock.TimeProvider = new FixedTimeProvider(FixedTime);

        var state = new EnqueuedState();

        Assert.Equal(FixedTime.UtcDateTime, state.EnqueuedAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
