using System.Diagnostics;
using TaskExtensions = Masterly.NightShift.Processing.TaskExtensions;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Processing;

public class TaskExtensionsFacts
{
    private readonly ManualResetEvent _mre;
    private readonly CancellationTokenSource _cts;

    public TaskExtensionsFacts()
    {
        _mre = new ManualResetEvent(false);
        _cts = new CancellationTokenSource();
    }

    [Fact]
    public async Task WaitOneAsync_ThrowsArgNullException_WhenWaitHandleIsNull()
    {
        ArgumentNullException exception = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await TaskExtensions.WaitOneAsync(null, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("waitHandle", exception.ParamName);
    }

    [Fact]
    public async Task WaitOneAsync_ThrowsOpCanceledException_WhenCancellationTokenIsCanceled()
    {
        _cts.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await TaskExtensions.WaitOneAsync(_mre, TimeSpan.Zero, _cts.Token));

        Assert.Equal(_cts.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task WaitOneAsync_ThrowsOpCanceledException_EvenWhenWaitHandleIsSignaled()
    {
        _cts.Cancel();
        _mre.Set();

        OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await TaskExtensions.WaitOneAsync(_mre, Timeout.InfiniteTimeSpan, _cts.Token));

        Assert.Equal(_cts.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task WaitOneAsync_ReturnsTrue_WhenWaitHandleIsSignaled()
    {
        _mre.Set();

        bool result = await TaskExtensions.WaitOneAsync(_mre, Timeout.InfiniteTimeSpan, _cts.Token);

        Assert.True(result);
    }

    [Fact]
    public async Task WaitOneAsync_ReturnsTrue_WhenWaitHandleIsSignaled_AndTimeoutIsZero()
    {
        _mre.Set();

        bool result = await TaskExtensions.WaitOneAsync(_mre, TimeSpan.Zero, _cts.Token);

        Assert.True(result);
    }

    [Fact]
    public async Task WaitOneAsync_ReturnsFalseImmediately_WhenNotSignaled_AndTimeoutIsZero()
    {
        bool result = await TaskExtensions.WaitOneAsync(_mre, TimeSpan.Zero, _cts.Token);

        Assert.False(result);
    }

    [Fact]
    public async Task WaitOneAsync_WaitsAndReturnsFalse_WhenNotSignaled_AndNonNullTimeout()
    {
        using ManualResetEvent mre = new(false);
        Stopwatch sw = Stopwatch.StartNew();
        bool result = await TaskExtensions.WaitOneAsync(mre, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        sw.Stop();

        Assert.False(result, "result != false");
        Assert.False(mre.WaitOne(TimeSpan.Zero), "mre is signaled");
        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(450), $"Elapsed: {sw.Elapsed.TotalMilliseconds} ms, Expected: 450 ms");
    }

    [Fact]
    public async Task WaitOneAsync_WaitsAndThrowsTaskCanceled_WhenNotSignaled_AndCancellationTokenIsCanceled()
    {
        Stopwatch sw = Stopwatch.StartNew();
        _cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await TaskExtensions.WaitOneAsync(_mre, Timeout.InfiniteTimeSpan, _cts.Token));
        sw.Stop();

        Assert.Equal(_cts.Token, exception.CancellationToken);
        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(450), $"Elapsed: {sw.Elapsed.TotalMilliseconds} ms, Expected: 450 ms");
    }

    [Fact]
    public void WaitOne_ThrowsArgNullException_WhenWaitHandleIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => TaskExtensions.WaitOne(null, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("waitHandle", exception.ParamName);
    }

    [Fact]
    public void WaitOne_ThrowsOpCanceledException_WhenCancellationTokenIsCanceled()
    {
        _cts.Cancel();

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => TaskExtensions.WaitOne(_mre, TimeSpan.Zero, _cts.Token));

        Assert.Equal(_cts.Token, exception.CancellationToken);
    }

    [Fact]
    public void WaitOne_ThrowsOpCanceledException_EvenWhenWaitHandleIsSignaled()
    {
        _cts.Cancel();
        _mre.Set();

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => TaskExtensions.WaitOne(_mre, Timeout.InfiniteTimeSpan, _cts.Token));

        Assert.Equal(_cts.Token, exception.CancellationToken);
    }

    [Fact]
    public void WaitOne_ReturnsTrue_WhenWaitHandleIsSignaled()
    {
        _mre.Set();

        bool result = TaskExtensions.WaitOne(_mre, Timeout.InfiniteTimeSpan, _cts.Token);

        Assert.True(result);
    }

    [Fact]
    public void WaitOne_ReturnsTrue_WhenWaitHandleIsSignaled_AndTimeoutIsZero()
    {
        _mre.Set();

        bool result = TaskExtensions.WaitOne(_mre, TimeSpan.Zero, _cts.Token);

        Assert.True(result);
    }

    [Fact]
    public void WaitOne_ReturnsFalseImmediately_WhenNotSignaled_AndTimeoutIsZero()
    {
        bool result = TaskExtensions.WaitOne(_mre, TimeSpan.Zero, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public void WaitOne_WaitsAndReturnsFalse_WhenNotSignaled_AndNonNullTimeout()
    {
        using ManualResetEvent mre = new(false);
        Stopwatch sw = Stopwatch.StartNew();
        bool result = TaskExtensions.WaitOne(mre, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        sw.Stop();

        Assert.False(result, "result != false");
        Assert.False(mre.WaitOne(TimeSpan.Zero), "mre is signaled");
        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(450), $"Elapsed: {sw.Elapsed.TotalMilliseconds} ms, Expected: 450 ms");
    }

    [Fact]
    public void WaitOne_WaitsAndThrowsTaskCanceled_WhenNotSignaled_AndCancellationTokenIsCanceled()
    {
        Stopwatch sw = Stopwatch.StartNew();
        _cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        OperationCanceledException exception = Assert.ThrowsAny<OperationCanceledException>(
            () => TaskExtensions.WaitOne(_mre, Timeout.InfiniteTimeSpan, _cts.Token));
        sw.Stop();

        Assert.Equal(_cts.Token, exception.CancellationToken);
        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(450), $"Elapsed: {sw.Elapsed.TotalMilliseconds} ms, Expected: 450 ms");
    }
}
