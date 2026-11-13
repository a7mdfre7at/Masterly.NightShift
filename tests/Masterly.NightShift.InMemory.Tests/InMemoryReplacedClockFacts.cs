using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// A test may replace the clock (<c>UseTimeProvider</c>) and move it. The timers that renew a lock's lease and
/// keep a fetched job invisible run in real time, so whether either has lapsed is judged in real time too: a
/// clock that jumps ahead gives neither to a second owner, and a clock that stands still keeps neither for good.
/// What is stored and shown, and what expires or comes due, still goes by the clock the test moves.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class InMemoryReplacedClockFacts : IDisposable
{
    private static int _started;

    private readonly TimeProvider _before = JobClock.TimeProvider;
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    public InMemoryReplacedClockFacts()
    {
        GlobalConfiguration.Configuration.UseTimeProvider(_clock);
    }

    public void Dispose() => GlobalConfiguration.Configuration.UseTimeProvider(_before);

    /// <summary>A job that says it started, and takes a moment.</summary>
    [AutomaticRetry(Attempts = 0)]
    public static void Slow()
    {
        Interlocked.Increment(ref _started);
        Thread.Sleep(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void LockHeldByOneConnection_IsNotGivenToAnother_WhenTheClockMovesPastItsLifetime()
    {
        InMemoryStorage storage = new();
        using IStorageConnection first = storage.GetConnection();
        using IStorageConnection second = storage.GetConnection();

        using (first.AcquireDistributedLock("resource", TimeSpan.Zero))
        {
            Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock("resource", TimeSpan.Zero));

            // The test moves on to tomorrow, for a delayed or a recurring job
            _clock.Advance(TimeSpan.FromDays(1));

            // Still held by the first connection, which is alive and inside its using block
            Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock("resource", TimeSpan.Zero));
        }

        using IDisposable released = second.AcquireDistributedLock("resource", TimeSpan.Zero);
    }

    [Fact]
    public async Task RunningJob_IsNotStartedASecondTime_WhenTheClockMovesPastTheInvisibilityTimeout()
    {
        Volatile.Write(ref _started, 0);
        InMemoryStorage storage = new(new InMemoryStorageOptions { QueuePollInterval = TimeSpan.FromMilliseconds(100) });
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = 2 }, storage);
        new BackgroundJobClient(storage).Enqueue(() => Slow());

        long waitingSince = Environment.TickCount64;
        while (Volatile.Read(ref _started) == 0)
        {
            Assert.True(Environment.TickCount64 - waitingSince < 30_000, "The job didn't start.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        // The job runs; the test moves on to tomorrow. The second worker looks at the queue several times meanwhile.
        _clock.Advance(TimeSpan.FromDays(1));
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref _started));
    }

    [Fact]
    public void FetchedEntry_IsNotFetchedAgain_WhenTheClockMovesPastTheInvisibilityTimeout()
    {
        InMemoryStorage storage = new(new InMemoryStorageOptions { QueuePollInterval = TimeSpan.FromMilliseconds(50) });
        Enqueue(storage);
        storage.Database.Fetch(["default"], CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(1));

        using CancellationTokenSource giveUp = new(TimeSpan.FromMilliseconds(300));
        Assert.ThrowsAny<OperationCanceledException>(() => storage.Database.Fetch(["default"], giveUp.Token));
    }

    [Fact]
    public void FetchedEntry_ThatNobodyKeepsAlive_BecomesVisible_AfterTheInvisibilityTimeout_WhileTheClockStandsStill()
    {
        InMemoryStorage storage = new(new InMemoryStorageOptions { InvisibilityTimeout = TimeSpan.FromMilliseconds(200) });
        Enqueue(storage);

        // Taken straight from the database, as by a worker whose process stopped: nothing keeps it alive
        (long _, long jobId, string _, string firstToken) = storage.Database.Fetch(["default"], CancellationToken.None);
        Thread.Sleep(400);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10));
        (long _, long againId, string _, string secondToken) = storage.Database.Fetch(["default"], limit.Token);

        Assert.Equal(jobId, againId);
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public void Lease_ThatNobodyRenews_IsTakenOver_AfterItsLifetime_WhileTheClockStandsStill()
    {
        InMemoryStorage storage = new(new InMemoryStorageOptions { DistributedLockLifetime = TimeSpan.FromMilliseconds(200) });
        ILeaseStore leases = storage.Database;

        // Taken straight from the database, as by a holder whose process stopped: nothing renews it
        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow.AddMilliseconds(200)));
        Assert.False(leases.TryTake("resource", "other-owner", JobClock.UtcNow.AddMilliseconds(200)));
        Thread.Sleep(400);

        Assert.True(leases.TryTake("resource", "other-owner", JobClock.UtcNow.AddMilliseconds(200)));
    }

    [Fact]
    public void Lease_Renewed_LastsItsLifetimeAgain_WhateverTheClockSays()
    {
        InMemoryStorage storage = new(new InMemoryStorageOptions { DistributedLockLifetime = TimeSpan.FromMilliseconds(600) });
        ILeaseStore leases = storage.Database;
        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow));

        Thread.Sleep(400);
        _clock.Advance(TimeSpan.FromDays(-1));
        Assert.True(leases.TryRenew("resource", "owner", JobClock.UtcNow));
        Thread.Sleep(400);

        // 800 ms after it was taken, 400 ms after it was renewed
        Assert.False(leases.TryTake("resource", "other-owner", JobClock.UtcNow));
    }

    [Fact]
    public void MovingTheClock_StillExpiresJobs_AndTimesOutServers()
    {
        InMemoryStorage storage = new();
        string expiring;
        string kept;
        using (IStorageConnection connection = storage.GetConnection())
        {
            expiring = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromHours(1))!;
            kept = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("y")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromDays(7))!;
            connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
            Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromMinutes(5)));
        }

        _clock.Advance(TimeSpan.FromHours(2));
        storage.Database.RemoveExpired();

        using IStorageConnection reader = storage.GetConnection();
        Assert.Null(reader.GetJobData(expiring));
        Assert.NotNull(reader.GetJobData(kept));
        Assert.InRange(((JobStorageConnection)reader).GetUtcDateTime() - DateTime.UtcNow, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        Assert.Equal(1, reader.RemoveTimedOutServers(TimeSpan.FromMinutes(5)));
    }

    private static void Enqueue(InMemoryStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        // Kept for longer than the clock is moved: an expired job leaves its queue
        string id = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromDays(30))!;
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToQueue("default", id);
        transaction.Commit();
    }

    /// <summary>A clock that moves only when the test moves it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
