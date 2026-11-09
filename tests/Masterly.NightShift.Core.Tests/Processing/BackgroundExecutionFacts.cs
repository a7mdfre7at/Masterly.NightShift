using System.Diagnostics;
using Masterly.NightShift.Processing;

namespace Masterly.NightShift.Core.Tests.Processing;

public class BackgroundExecutionFacts
{
    private static readonly TimeSpan LongDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromSeconds(10);

    [Fact]
    public void Run_DoesNotCallTheCallback_WhenAlreadyStopped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var execution = CreateExecution(LongDelay, cts.Token);
        var calls = 0;

        execution.Run((_, _) => calls++, null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Run_Stops_WhenStopIsRequestedDuringTheRetryDelay()
    {
        using var cts = new CancellationTokenSource();
        using var execution = CreateExecution(LongDelay, cts.Token);
        var calls = 0;
        var stopwatch = Stopwatch.StartNew();

        execution.Run((_, _) =>
        {
            calls++;
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            throw new InvalidOperationException("Fails, so the execution waits for the retry delay");
        }, null);

        Assert.Equal(1, calls);
        Assert.True(stopwatch.Elapsed < MaxRunTime);
    }

    [Fact]
    public void Run_Stops_OnOperationCanceledException_OfALinkedStopToken()
    {
        using var cts = new CancellationTokenSource();
        using var execution = CreateExecution(ShortDelay, cts.Token);
        var calls = 0;

        execution.Run((_, _) =>
        {
            calls++;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            cts.Cancel();
            linked.Token.ThrowIfCancellationRequested();
        }, null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Run_Retries_OnOperationCanceledException_OfAnUnrelatedToken()
    {
        using var cts = new CancellationTokenSource();
        using var execution = CreateExecution(ShortDelay, cts.Token);
        var calls = 0;

        execution.Run((_, _) =>
        {
            if (++calls == 2)
            {
                cts.Cancel();
                return;
            }

            throw new OperationCanceledException(new CancellationToken(true));
        }, null);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Run_CallsTheCallbackAgain_AfterASuccess()
    {
        using var cts = new CancellationTokenSource();
        using var execution = CreateExecution(LongDelay, cts.Token);
        var calls = 0;

        execution.Run((_, _) =>
        {
            if (++calls == 3) cts.Cancel();
        }, null);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RunAsync_DoesNotCallTheCallback_WhenAlreadyStopped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var execution = CreateExecution(LongDelay, cts.Token);
        var calls = 0;

        await execution.RunAsync((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunAsync_Stops_WhenStopIsRequestedDuringTheRetryDelay()
    {
        using var cts = new CancellationTokenSource();
        using var execution = CreateExecution(LongDelay, cts.Token);
        var calls = 0;
        var stopwatch = Stopwatch.StartNew();

        await execution.RunAsync(async (_, _) =>
        {
            calls++;
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Task.Yield();
            throw new InvalidOperationException("Fails, so the execution waits for the retry delay");
        }, null);

        Assert.Equal(1, calls);
        Assert.True(stopwatch.Elapsed < MaxRunTime);
    }

    [Fact]
    public async Task RunAsync_Stops_OnOperationCanceledException_OfALinkedStopToken()
    {
        using var cts = new CancellationTokenSource();
        using var execution = CreateExecution(ShortDelay, cts.Token);
        var calls = 0;

        await execution.RunAsync(async (_, _) =>
        {
            calls++;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, linked.Token);
        }, null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Retries_OnOperationCanceledException_OfAnUnrelatedToken()
    {
        using var cts = new CancellationTokenSource();
        using var execution = CreateExecution(ShortDelay, cts.Token);
        var calls = 0;

        await execution.RunAsync((_, _) =>
        {
            if (++calls == 2)
            {
                cts.Cancel();
                return Task.CompletedTask;
            }

            return Task.FromCanceled(new CancellationToken(true));
        }, null);

        Assert.Equal(2, calls);
    }

    private static BackgroundExecution CreateExecution(TimeSpan retryDelay, CancellationToken stopToken)
    {
        var options = new BackgroundExecutionOptions
        {
            Name = "facts",
            RetryDelay = _ => retryDelay
        };

        return new BackgroundExecution(options, stopToken);
    }
}
