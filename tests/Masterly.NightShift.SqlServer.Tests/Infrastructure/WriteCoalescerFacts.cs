using System.Collections.Concurrent;

namespace Masterly.NightShift.SqlServer.Tests;

public class WriteCoalescerFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<string> _alone = new();
    private readonly ConcurrentQueue<string[]> _groups = new();

    [Fact]
    public void Write_WritesAlone_WhenNobodyElseIsWriting()
    {
        WriteCoalescer<string> coalescer = Create(maxWriters: 1);

        coalescer.Write("a", _alone.Enqueue);
        coalescer.Write("b", _alone.Enqueue);

        Assert.Equal(["a", "b"], _alone);
        Assert.Empty(_groups);
    }

    [Fact]
    public void Write_ThrowsWhatWritingAloneThrows_AndStaysUsable()
    {
        WriteCoalescer<string> coalescer = Create(maxWriters: 1);

        Assert.Throws<InvalidOperationException>(() => coalescer.Write("a", static _ => throw new InvalidOperationException()));
        coalescer.Write("b", _alone.Enqueue);

        Assert.Equal(["b"], _alone);
    }

    [Fact]
    public void Write_WritesTheCallersThatWaited_AsOneGroup()
    {
        WriteCoalescer<string> coalescer = Create(maxWriters: 1);
        using ManualResetEventSlim firstIsWriting = new();
        using ManualResetEventSlim releaseFirst = new();

        Caller first = Run(() => coalescer.Write("first", request =>
        {
            firstIsWriting.Set();
            releaseFirst.Wait(Timeout, CancellationToken.None);
            _alone.Enqueue(request);
        }));
        Assert.True(firstIsWriting.Wait(Timeout, TestContext.Current.CancellationToken));

        Caller[] waiting = [.. new[] { "a", "b", "c" }.Select(x => Run(() => coalescer.Write(x, _alone.Enqueue)))];
        WaitUntilBlocked(waiting);
        releaseFirst.Set();

        JoinAll([first, .. waiting]);
        Assert.Equal(["first"], _alone);
        Assert.Equal(["a", "b", "c"], Assert.Single(_groups).Order());
    }

    [Fact]
    public void Write_LetsUpToMaxWritersWriteAloneAtOnce()
    {
        WriteCoalescer<string> coalescer = Create(maxWriters: 2);
        using CountdownEvent bothAreWriting = new(2);
        using ManualResetEventSlim release = new();

        Caller[] writers = [.. new[] { "a", "b" }.Select(x => Run(() => coalescer.Write(x, request =>
        {
            bothAreWriting.Signal();
            release.Wait(Timeout, CancellationToken.None);
            _alone.Enqueue(request);
        })))];

        Assert.True(bothAreWriting.Wait(Timeout, TestContext.Current.CancellationToken), "Two callers should write at the same time.");
        release.Set();

        JoinAll(writers);
        Assert.Empty(_groups);
    }

    [Fact]
    public void Write_SplitsTheCallersThatWaited_IntoGroupsOfMaxGroupSize()
    {
        WriteCoalescer<string> coalescer = Create(maxWriters: 1, maxGroupSize: 2);
        using ManualResetEventSlim firstIsWriting = new();
        using ManualResetEventSlim releaseFirst = new();

        Caller first = Run(() => coalescer.Write("first", _ =>
        {
            firstIsWriting.Set();
            releaseFirst.Wait(Timeout, CancellationToken.None);
        }));
        Assert.True(firstIsWriting.Wait(Timeout, TestContext.Current.CancellationToken));

        Caller[] waiting = [.. new[] { "a", "b", "c", "d", "e" }.Select(x => Run(() => coalescer.Write(x, _alone.Enqueue)))];
        WaitUntilBlocked(waiting);
        releaseFirst.Set();

        JoinAll([first, .. waiting]);

        // Two groups of two, and the last caller on its own
        Assert.Equal(2, _groups.Count);
        Assert.All(_groups, static x => Assert.Equal(2, x.Length));
        Assert.Single(_alone);
        Assert.Equal(["a", "b", "c", "d", "e"], _groups.SelectMany(static x => x).Concat(_alone).Order());
    }

    [Fact]
    public void Write_WritesEveryMemberAlone_WhenWritingTheGroupFails()
    {
        WriteCoalescer<string> coalescer = new(static _ => throw new InvalidOperationException("group"), maxGroupSize: 10, maxWriters: 1);
        using ManualResetEventSlim firstIsWriting = new();
        using ManualResetEventSlim releaseFirst = new();

        Caller first = Run(() => coalescer.Write("first", _ =>
        {
            firstIsWriting.Set();
            releaseFirst.Wait(Timeout, CancellationToken.None);
        }));
        Assert.True(firstIsWriting.Wait(Timeout, TestContext.Current.CancellationToken));

        Caller[] waiting = [.. new[] { "a", "b", "c" }.Select(x => Run(() => coalescer.Write(x, _alone.Enqueue)))];
        WaitUntilBlocked(waiting);
        releaseFirst.Set();

        JoinAll([first, .. waiting]);
        Assert.Equal(["a", "b", "c"], _alone.Order());
    }

    [Fact]
    public void Write_OnlyFailsTheCallerWhoseOwnWriteFails_WhenWritingTheGroupFails()
    {
        WriteCoalescer<string> coalescer = new(static _ => throw new InvalidOperationException("group"), maxGroupSize: 10, maxWriters: 1);
        using ManualResetEventSlim firstIsWriting = new();
        using ManualResetEventSlim releaseFirst = new();

        Caller first = Run(() => coalescer.Write("first", _ =>
        {
            firstIsWriting.Set();
            releaseFirst.Wait(Timeout, CancellationToken.None);
        }));
        Assert.True(firstIsWriting.Wait(Timeout, TestContext.Current.CancellationToken));

        Caller good = Run(() => coalescer.Write("good", _alone.Enqueue));
        Caller bad = Run(() => coalescer.Write("bad", static _ => throw new TimeoutException()));
        WaitUntilBlocked([good, bad]);
        releaseFirst.Set();

        JoinAll([first, good]);
        Assert.True(bad.Thread.Join(Timeout));
        Assert.IsType<TimeoutException>(bad.Exception);
        Assert.Equal(["good"], _alone);
    }

    [Fact]
    public void Write_WritesEveryRequestExactlyOnce_UnderManyCallers()
    {
        ConcurrentBag<int> written = [];
        WriteCoalescer<Request> coalescer = new(
            group =>
            {
                foreach (Request request in group)
                {
                    written.Add(request.Number);
                }
            },
            maxGroupSize: 8,
            maxWriters: 2);

        Caller[] callers = [.. Enumerable.Range(0, 16).Select(caller => Run(() =>
        {
            for (int i = 0; i < 250; i++)
            {
                coalescer.Write(new Request((caller * 1000) + i), request => written.Add(request.Number));
            }
        }))];

        JoinAll(callers);
        Assert.Equal(4000, written.Count);
        Assert.Equal(4000, written.Distinct().Count());
    }

    private WriteCoalescer<string> Create(int maxWriters, int maxGroupSize = 10)
    {
        return new WriteCoalescer<string>(group => _groups.Enqueue([.. group]), maxGroupSize, maxWriters);
    }

    private static Caller Run(Action action)
    {
        Caller caller = new(action);
        caller.Thread.Start();
        return caller;
    }

    private static void JoinAll(Caller[] callers)
    {
        foreach (Caller caller in callers)
        {
            Assert.True(caller.Thread.Join(Timeout), "A caller didn't return in time.");
            Assert.Null(caller.Exception);
        }
    }

    // The callers have to be waiting inside the coalescer before the writer is released
    private static void WaitUntilBlocked(Caller[] callers)
    {
        Assert.True(
            SpinWait.SpinUntil(() => callers.All(static x => x.Thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin)), Timeout),
            "The callers didn't start waiting in time.");
    }

    // A caller on a thread of its own, with what it threw
    private sealed class Caller
    {
        public Caller(Action action)
        {
            Thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Exception = ex;
                }
            })
            {
                IsBackground = true,
            };
        }

        public Thread Thread { get; }
        public Exception Exception { get; private set; }
    }

    private sealed record Request(int Number);
}
