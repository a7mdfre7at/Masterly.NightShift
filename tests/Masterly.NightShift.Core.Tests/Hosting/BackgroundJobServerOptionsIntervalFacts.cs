namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// The intervals of the server's background processes must be positive: a process waits that long between two
/// passes, so zero makes it poll the storage without pause.
/// </summary>
public class BackgroundJobServerOptionsIntervalFacts
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HeartbeatInterval_ThrowsAnException_WhenNotPositive(int milliseconds)
    {
        BackgroundJobServerOptions options = new();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.HeartbeatInterval = TimeSpan.FromMilliseconds(milliseconds));

        Assert.Contains("HeartbeatInterval must be greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ServerCheckInterval_ThrowsAnException_WhenNotPositive(int milliseconds)
    {
        BackgroundJobServerOptions options = new();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.ServerCheckInterval = TimeSpan.FromMilliseconds(milliseconds));

        Assert.Contains("ServerCheckInterval must be greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ServerTimeout_ThrowsAnException_WhenNotPositive(int milliseconds)
    {
        BackgroundJobServerOptions options = new();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.ServerTimeout = TimeSpan.FromMilliseconds(milliseconds));

        Assert.Contains("ServerTimeout must be greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5000)]
    public void CancellationCheckInterval_ThrowsAnException_WhenNotPositive(int milliseconds)
    {
        BackgroundJobServerOptions options = new();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.CancellationCheckInterval = TimeSpan.FromMilliseconds(milliseconds));

        Assert.Contains("CancellationCheckInterval must be greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationCheckInterval_ThrowsAnException_WhenLongerThanAWaitCanBe()
    {
        BackgroundJobServerOptions options = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.CancellationCheckInterval = TimeSpan.FromMilliseconds((double)int.MaxValue + 1));
    }

    [Fact]
    public void CancellationCheckInterval_ThrowsAnException_WhenInfinite()
    {
        BackgroundJobServerOptions options = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.CancellationCheckInterval = System.Threading.Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void Intervals_AcceptTheSmallestPositiveValue_AndKeepTheirDefaults()
    {
        BackgroundJobServerOptions options = new();

        Assert.Equal(TimeSpan.FromSeconds(5), options.CancellationCheckInterval);

        options.HeartbeatInterval = TimeSpan.FromMilliseconds(1);
        options.ServerCheckInterval = TimeSpan.FromMilliseconds(1);
        options.ServerTimeout = TimeSpan.FromMilliseconds(1);
        options.CancellationCheckInterval = TimeSpan.FromMilliseconds(int.MaxValue);

        Assert.Equal(TimeSpan.FromMilliseconds(1), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(1), options.ServerCheckInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(1), options.ServerTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(int.MaxValue), options.CancellationCheckInterval);
    }
}
