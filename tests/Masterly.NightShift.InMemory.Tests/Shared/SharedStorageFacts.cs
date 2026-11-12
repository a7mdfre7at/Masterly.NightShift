using System.Collections.Concurrent;
using System.Diagnostics;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Shared;

namespace Masterly.NightShift.InMemory.Tests.Shared;

/// <summary>The layer the storages share (src/Shared/Storage), compiled into the in-memory storage.</summary>
public class SharedStorageFacts
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMilliseconds(300);

    // Far below the time a blocked store call takes (the deadline), far above what Dispose needs
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);

    [Fact]
    public void LeaseLock_ThatFindsItsLeaseTaken_IsLost_StopsRenewing_AndDoesNotReleaseTheNewOwners()
    {
        FakeLeaseStore store = new() { Renews = false };
        LeaseLock taken = LeaseLock.Acquire(store, "resource", TimeSpan.FromSeconds(1), TimeToLive);

        WaitUntil(() => taken.Lost);
        int renewals = store.RenewCalls;
        Thread.Sleep(TimeToLive);
        taken.Dispose();

        Assert.Equal(renewals, store.RenewCalls);
        Assert.Equal(0, store.ReleaseCalls);
    }

    [Fact]
    public void LeaseLock_RenewsWhileHeld_AndNeverAfterDispose()
    {
        FakeLeaseStore store = new();
        LeaseLock held = LeaseLock.Acquire(store, "resource", TimeSpan.FromSeconds(1), TimeToLive);

        WaitUntil(() => store.RenewCalls >= 3);
        held.Dispose();
        int renewals = store.RenewCalls;
        Thread.Sleep(TimeToLive);

        Assert.Equal(renewals, store.RenewCalls);
        Assert.False(held.Lost);
        Assert.Equal(1, store.ReleaseCalls);
    }

    [Fact]
    public void LeaseLock_DisposedWhileARenewalRuns_IsNeverReportedLost()
    {
        // A renewal that finds the lease released (by Dispose) would report it lost; it sees the lock disposed instead
        for (int i = 0; i < 20; i++)
        {
            FakeLeaseStore store = new() { RenewDelay = TimeSpan.FromMilliseconds(5) };
            LeaseLock held = LeaseLock.Acquire(store, "resource", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300));
            Thread.Sleep(100 + i);

            held.Dispose();

            Assert.False(held.Lost);
        }
    }

    [Fact]
    public void LeaseLock_ThatFindsItsLeaseTaken_LogsThatItWasLost()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        FakeLeaseStore store = new() { Renews = false };
        using LeaseLock taken = LeaseLock.Acquire(store, "lost-resource", TimeSpan.FromSeconds(1), TimeToLive);

        WaitUntil(() => taken.Lost);
        WaitUntil(() => logs.About("lost-resource").Count > 0);

        Assert.Single(logs.About("lost-resource"));
    }

    [Fact]
    public void LeaseLock_Dispose_DoesNotWaitForARenewalInProgress()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        using ManualResetEventSlim gate = new();
        FakeLeaseStore store = new() { RenewGate = gate };
        LeaseLock held = LeaseLock.Acquire(store, "slow-resource", TimeSpan.FromSeconds(1), TimeToLive);
        WaitUntil(() => store.RenewCalls == 1);

        Stopwatch stopwatch = Stopwatch.StartNew();
        held.Dispose();
        stopwatch.Stop();
        int finishedAtDispose = store.RenewsFinished;
        int releasedAtDispose = store.ReleaseCalls;

        // The renewal comes back after the release and finds the lease gone, as it would in a real store
        gate.Set();
        WaitUntil(() => store.RenewsFinished == 1);
        Thread.Sleep(TimeToLive);

        Assert.True(stopwatch.Elapsed < Prompt, $"Dispose took {stopwatch.Elapsed}.");
        Assert.Equal(0, finishedAtDispose);
        Assert.Equal(1, releasedAtDispose);
        Assert.Equal(1, store.ReleaseCalls);
        Assert.Equal(1, store.RenewCalls);
        Assert.False(held.Lost);
        Assert.Empty(logs.About("slow-resource"));
    }

    [Fact]
    public void LeaseLock_DisposedWhileARenewalFails_LogsNothing()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        using ManualResetEventSlim gate = new();
        FakeLeaseStore store = new() { RenewGate = gate, RenewFailure = new TimeoutException("The store timed out.") };
        LeaseLock held = LeaseLock.Acquire(store, "failing-resource", TimeSpan.FromSeconds(1), TimeToLive);
        WaitUntil(() => store.RenewCalls == 1);

        held.Dispose();
        gate.Set();
        WaitUntil(() => store.RenewsFinished == 1);
        Thread.Sleep(TimeToLive);

        Assert.Equal(1, store.RenewCalls);
        Assert.Equal(1, store.ReleaseCalls);
        Assert.False(held.Lost);
        Assert.Empty(logs.About("failing-resource"));
    }

    [Fact]
    public void LeaseLock_DoesNotStartARenewal_WhileOneIsInProgress()
    {
        using ManualResetEventSlim gate = new();
        FakeLeaseStore store = new() { RenewGate = gate };
        using LeaseLock held = LeaseLock.Acquire(store, "resource", TimeSpan.FromSeconds(1), TimeToLive);
        WaitUntil(() => store.RenewCalls == 1);

        // Three renewal periods pass while the first renewal is still in the store
        Thread.Sleep(TimeToLive);
        int renewalsWhileBlocked = store.RenewCalls;
        gate.Set();
        WaitUntil(() => store.RenewCalls >= 2);

        Assert.Equal(1, renewalsWhileBlocked);
        Assert.False(held.Lost);
    }

    [Fact]
    public void LeaseLock_DisposedByManyThreadsAtOnce_ReleasesOnce()
    {
        FakeLeaseStore store = new();
        LeaseLock held = LeaseLock.Acquire(store, "resource", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ => held.Dispose());

        Assert.Equal(1, store.ReleaseCalls);
        Assert.False(held.Lost);
    }

    [Fact]
    public void LeaseLock_Dispose_DoesNotThrow_WhenTheStoreFailsToRelease()
    {
        FakeLeaseStore store = new() { ReleaseFailure = new TimeoutException("The store timed out.") };
        LeaseLock held = LeaseLock.Acquire(store, "resource", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        held.Dispose();
        held.Dispose();

        Assert.Equal(1, store.ReleaseCalls);
    }

    [Fact]
    public void Transaction_Dispose_ReleasesEveryLock_EvenWhenOneFails()
    {
        CountingTransaction transaction = new();
        transaction.AcquireDistributedLock("first", TimeSpan.FromSeconds(1));
        transaction.AcquireDistributedLock("failing", TimeSpan.FromSeconds(1));
        transaction.AcquireDistributedLock("last", TimeSpan.FromSeconds(1));

        transaction.Dispose();

        Assert.Equal(["last", "failing", "first"], transaction.Released);
    }

    [Fact]
    public void Transaction_RefusesAFetchedJob_ItsStorageDoesNotOwn_WhenRecorded()
    {
        CountingTransaction transaction = new() { OwnsFetchedJobs = false };
        using TestFetchedJob fetched = new();

        Assert.Throws<NotSupportedException>(() => transaction.RemoveFromQueue(fetched));
    }

    [Fact]
    public void FetchedJob_Dispose_PutsTheJobBackOnce_AndDoesNotThrow_WhenTheStorageFails()
    {
        TestFetchedJob fetched = new() { Failure = new TimeoutException("The store timed out.") };

        fetched.Dispose();
        fetched.Dispose();
        fetched.Requeue();

        Assert.Equal(1, fetched.Returns);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < Deadline, "The condition didn't hold before the deadline.");
            Thread.Sleep(10);
        }
    }

    private sealed class FakeLeaseStore : ILeaseStore
    {
        private int _renewCalls;
        private int _renewsFinished;
        private int _releaseCalls;
        private volatile bool _released;

        public bool Renews { get; init; } = true;

        public TimeSpan RenewDelay { get; init; }

        /// <summary>When set, a renewal stays in the store until the gate opens (or the deadline passes).</summary>
        public ManualResetEventSlim? RenewGate { get; init; }

        public Exception? RenewFailure { get; init; }

        public Exception? ReleaseFailure { get; init; }

        public int RenewCalls => Volatile.Read(ref _renewCalls);

        public int RenewsFinished => Volatile.Read(ref _renewsFinished);

        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);

        public bool TryTake(string resource, string owner, DateTime expireAt) => true;

        public bool TryRenew(string resource, string owner, DateTime expireAt)
        {
            Interlocked.Increment(ref _renewCalls);

            try
            {
                Thread.Sleep(RenewDelay);
                RenewGate?.Wait(Deadline);
                if (RenewFailure != null) throw RenewFailure;
                return Renews && !_released;
            }
            finally
            {
                Interlocked.Increment(ref _renewsFinished);
            }
        }

        public void Release(string resource, string owner)
        {
            Interlocked.Increment(ref _releaseCalls);
            _released = true;
            if (ReleaseFailure != null) throw ReleaseFailure;
        }
    }

    /// <summary>Keeps what Masterly.NightShift logs while in use. Test assemblies run one test at a time.</summary>
    private sealed class RecordingLogs : ILogProvider, ILog, IDisposable
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public static RecordingLogs Use()
        {
            RecordingLogs logs = new();
            GlobalConfiguration.Configuration.UseLogProvider(logs);
            return logs;
        }

        /// <summary>The messages that name <paramref name="resource"/>: another test's lock doesn't count.</summary>
        public IReadOnlyList<string> About(string resource)
        {
            return [.. _messages.Where(x => x.Contains($"'{resource}'", StringComparison.Ordinal))];
        }

        public ILog GetLogger(string name) => this;

        public bool Log(LogLevel logLevel, Func<string> messageFunc, Exception exception = null)
        {
            // Without a message, the call only asks whether the level is enabled
            if (messageFunc != null) _messages.Enqueue(messageFunc());
            return true;
        }

        // No logging library is referenced here, so this is what the loggers wrote to before
        public void Dispose() => GlobalConfiguration.Configuration.UseNoOpLogProvider();
    }

    private sealed class CountingTransaction : RecordingTransaction
    {
        public List<string> Released { get; } = [];

        public bool OwnsFetchedJobs { get; init; } = true;

        protected override void Apply(IReadOnlyList<StorageOperation> operations)
        {
        }

        protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => new TestLock(this, resource);

        protected override bool IsOwnFetchedJob(FetchedJobBase fetchedJob) => OwnsFetchedJobs;

        private sealed class TestLock(CountingTransaction owner, string resource) : IDisposable
        {
            public void Dispose()
            {
                owner.Released.Add(resource);
                if (resource == "failing") throw new TimeoutException("The store timed out.");
            }
        }
    }

    private sealed class TestFetchedJob() : FetchedJobBase("1", "default", null)
    {
        public Exception? Failure { get; init; }

        public int Returns { get; private set; }

        protected override void Remove()
        {
        }

        protected override void ReturnToQueue()
        {
            Returns++;
            if (Failure != null) throw Failure;
        }
    }
}
