namespace Masterly.NightShift.Core.Tests.Server;

public class BackgroundJobServerOptionsFacts
{
    [Fact]
    public void Ctor_InitializeProperties_WithCorrectValues()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Equal(Math.Min(Environment.ProcessorCount * 5, 20), options.WorkerCount);
        Assert.Equal(EnqueuedState.DefaultQueue, options.Queues[0]);
        Assert.Equal(TimeSpan.FromSeconds(15), options.ShutdownTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), options.SchedulePollingInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ServerTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ServerCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatInterval);
    }

    [Fact]
    public void WorkerCount_ThrowsAnException_WhenValueIsEqualToZero()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.WorkerCount = 0);
    }

    [Fact]
    public void WorkerCount_ThrowsAnException_WhenValueIsNegative()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.WorkerCount = -1);
    }

    [Fact]
    public void Queues_ThrowsAnException_WhenValueIsNull()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentNullException>(
            () => options.Queues = null);
    }

    [Fact]
    public void Queues_ThrowsAnException_WhenGivenArrayIsEmpty()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentException>(
            () => options.Queues = []);
    }

    [Fact]
    public void ServerTimeout_ThrowsAnException_WhenValueIsTooLarge()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ServerTimeout = TimeSpan.FromHours(25));
    }

    [Fact]
    public void ServerTimeout_ThrowsAnException_WhenValueIsNegative()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ServerTimeout = TimeSpan.FromHours(-1));
    }

    [Fact]
    public void ServerTimeout_ThrowsAnException_WhenValueIsInfinite()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ServerTimeout = Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void ServerCheckInterval_ThrowsAnException_WhenValueIsTooLarge()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ServerCheckInterval = TimeSpan.FromHours(25));
    }

    [Fact]
    public void ServerCheckInterval_ThrowsAnException_WhenValueIsNegative()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ServerCheckInterval = TimeSpan.FromHours(-1));
    }

    [Fact]
    public void ServerCheckInterval_ThrowsAnException_WhenValueIsInfinite()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ServerCheckInterval = Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void HeartbeatInterval_ThrowsAnException_WhenValueIsTooLarge()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HeartbeatInterval = TimeSpan.FromHours(25));
    }

    [Fact]
    public void HeartbeatInterval_ThrowsAnException_WhenValueIsNegative()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HeartbeatInterval = TimeSpan.FromHours(-1));
    }

    [Fact]
    public void HeartbeatInterval_ThrowsAnException_WhenValueIsInfinite()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HeartbeatInterval = Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void ShutdownTimeout_ThrowsAnException_WhenValueIsTooLarge()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ShutdownTimeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1));
    }

    [Fact]
    public void ShutdownTimeout_ThrowsAnException_WhenValueIsNegative()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ShutdownTimeout = TimeSpan.FromHours(-1));
    }

    [Fact]
    public void ShutdownTimeout_DoesNotThrowAnException_WhenValueIsInfinite()
    {
        BackgroundJobServerOptions options = CreateOptions();

        options.ShutdownTimeout = Timeout.InfiniteTimeSpan;

        Assert.Equal(Timeout.InfiniteTimeSpan, options.ShutdownTimeout);
    }

    [Fact]
    public void SchedulePollingInterval_ThrowsAnException_WhenValueIsTooLarge()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.SchedulePollingInterval = TimeSpan.FromMilliseconds((double)int.MaxValue + 1));
    }

    [Fact]
    public void SchedulePollingInterval_ThrowsAnException_WhenValueIsNegative()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.SchedulePollingInterval = TimeSpan.FromHours(-1));
    }

    [Fact]
    public void SchedulePollingInterval_ThrowsAnException_WhenValueIsInfinite()
    {
        BackgroundJobServerOptions options = CreateOptions();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.SchedulePollingInterval = Timeout.InfiniteTimeSpan);
    }

    private static BackgroundJobServerOptions CreateOptions()
    {
        return new BackgroundJobServerOptions();
    }
}
