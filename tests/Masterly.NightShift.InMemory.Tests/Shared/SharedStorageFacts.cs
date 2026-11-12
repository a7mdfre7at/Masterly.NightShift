using System.Diagnostics;
using Masterly.NightShift.Storage.Shared;

namespace Masterly.NightShift.InMemory.Tests.Shared;

/// <summary>The layer the storages share (src/Shared/Storage), compiled into the in-memory storage.</summary>
public class SharedStorageFacts
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMilliseconds(300);

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
        // A renewal that finds the lease released (by Dispose) would report it lost; Dispose waits for it instead
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
        private int _releaseCalls;
        private volatile bool _released;

        public bool Renews { get; init; } = true;

        public TimeSpan RenewDelay { get; init; }

        public Exception? ReleaseFailure { get; init; }

        public int RenewCalls => Volatile.Read(ref _renewCalls);

        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);

        public bool TryTake(string resource, string owner, DateTime expireAt) => true;

        public bool TryRenew(string resource, string owner, DateTime expireAt)
        {
            Interlocked.Increment(ref _renewCalls);
            Thread.Sleep(RenewDelay);
            return Renews && !_released;
        }

        public void Release(string resource, string owner)
        {
            Interlocked.Increment(ref _releaseCalls);
            _released = true;
            if (ReleaseFailure != null) throw ReleaseFailure;
        }
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
