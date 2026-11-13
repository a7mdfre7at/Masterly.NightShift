using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>
/// On a database with a clock of its own, a lock's lease and a fetched job's invisibility are stamped and
/// judged with that clock, so servers whose clocks differ agree on which lock is held and which job is being
/// worked on. The clock of this process is moved to play a server whose clock is off. SQLite's clock is the
/// process's own: there it is asked with a statement, as the database of any provider is with DatabaseClockSql.
/// </summary>
public sealed class EfLeaseClockFacts : IDisposable
{
    private const string SqliteNow = "select strftime('%Y-%m-%d %H:%M:%f', 'now')";

    private readonly SqliteDatabase _database = new();

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        _database.Dispose();
    }

    [Theory]
    [InlineData(-45)]
    [InlineData(45)]
    [InlineData(-1200)]
    public void LockHeldByAServerWhoseClockIsOff_IsNotTakenByAnother_OnADatabaseAskedForItsClock(int secondsOff)
    {
        AssertTheLockStaysHeld(_database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow }), TimeSpan.FromSeconds(secondsOff));
    }

    [Theory]
    [InlineData(-45)]
    [InlineData(45)]
    [InlineData(-3600)]
    public void LockHeldByAServerWhoseClockIsOff_IsNotTakenByAnother_OnSqlServer(int secondsOff)
    {
        AssertTheLockStaysHeld(SqlServerDatabase.CreateStorage(), TimeSpan.FromSeconds(secondsOff));
    }

    [Fact]
    public void Lease_IsStampedWithTheDatabasesClock_WhenItIsTakenAndRenewed()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        using IStorageConnection connection = storage.GetConnection();
        ILeaseStore leases = (ILeaseStore)connection;
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(10));

        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow.AddSeconds(30)));
        DateTime taken = ExpireAt(storage, "resource");
        Assert.True(leases.TryRenew("resource", "owner", JobClock.UtcNow.AddSeconds(30)));
        DateTime renewed = ExpireAt(storage, "resource");

        // The lock lifetime of the options (30 seconds) from the database's time, not from this process's
        Assert.InRange(taken, DateTime.UtcNow.AddSeconds(20), DateTime.UtcNow.AddSeconds(40));
        Assert.InRange(renewed, taken, DateTime.UtcNow.AddSeconds(40));
    }

    [Theory]
    [InlineData(-45)]
    [InlineData(45)]
    public void Lease_OfAHolderThatStopped_IsTakenOver_OnceItRanOutByTheDatabasesClock(int secondsOff)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = SqliteNow,
            DistributedLockLifetime = TimeSpan.FromSeconds(1),
        });
        using IStorageConnection connection = storage.GetConnection();
        ILeaseStore leases = (ILeaseStore)connection;

        // Taken straight from the store, as by a holder whose process stopped: nothing renews it
        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow.AddSeconds(1)));
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromSeconds(secondsOff));
        Assert.False(leases.TryTake("resource", "other-owner", JobClock.UtcNow.AddSeconds(1)));

        Thread.Sleep(1500);

        Assert.True(leases.TryTake("resource", "other-owner", JobClock.UtcNow.AddSeconds(1)));
        Assert.False(leases.TryRenew("resource", "owner", JobClock.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public void Lease_OnSqlite_GoesByTheProcessClock()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        using IStorageConnection connection = storage.GetConnection();
        ILeaseStore leases = (ILeaseStore)connection;
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(3));

        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow.AddSeconds(30)));

        Assert.InRange(ExpireAt(storage, "resource"), DateTime.UtcNow.AddDays(3).AddSeconds(20), DateTime.UtcNow.AddDays(3).AddSeconds(40));
        Assert.False(leases.TryTake("resource", "other-owner", JobClock.UtcNow.AddSeconds(30)));
    }

    [Fact]
    public void Lease_HeldOrReleasedInBetween_IsToldApart_FromAnInsertThatFails()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        using IStorageConnection connection = storage.GetConnection();
        ILeaseStore leases = (ILeaseStore)connection;

        Assert.True(leases.TryTake("resource", "first", JobClock.UtcNow.AddSeconds(30)));
        Assert.False(leases.TryTake("resource", "second", JobClock.UtcNow.AddSeconds(30)));
        leases.Release("resource", "first");
        Assert.True(leases.TryTake("resource", "second", JobClock.UtcNow.AddSeconds(30)));

        // The owner can't be null: the insert fails for another reason than a held lease
        Assert.Throws<DbUpdateException>(() => leases.TryTake("other", null!, JobClock.UtcNow.AddSeconds(30)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void JobBeingWorkedOn_IsNotFetchedAgain_ByAServerWhoseClockIsADayOff(int daysOff)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = SqliteNow,
            QueuePollInterval = TimeSpan.FromMilliseconds(50),
        });
        Enqueue(storage);
        using IStorageConnection worker = storage.GetConnection();
        using IStorageConnection other = storage.GetConnection();

        using IFetchedJob fetched = worker.FetchNextJob(["default"], CancellationToken.None);
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(daysOff));

        using CancellationTokenSource giveUp = new(TimeSpan.FromMilliseconds(500));
        Assert.ThrowsAny<OperationCanceledException>(() => other.FetchNextJob(["default"], giveUp.Token));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void JobWhoseWorkerStopped_IsFetchedAgain_OnceTheInvisibilityTimeoutPassedByTheDatabasesClock(int daysOff)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = SqliteNow,
            QueuePollInterval = TimeSpan.FromMilliseconds(50),
        });
        string id = Enqueue(storage);

        // The statement from the options is checked against this process's clock: read once while it is right
        using (IStorageConnection first = storage.GetConnection())
        {
            ((JobStorageConnection)first).GetUtcDateTime();
        }

        // Fetched 31 minutes ago by the database's clock, by a worker whose process stopped: nothing keeps it alive
        DateTime fetchedAt = DateTime.UtcNow.AddMinutes(-31);
        using (DbContext context = storage.CreateContext())
        {
            context.Set<JobQueueRow>().ExecuteUpdate(s => s.SetProperty(x => x.FetchedAt, fetchedAt).SetProperty(x => x.FetchToken, "gone"));
        }

        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(daysOff));
        using IStorageConnection connection = storage.GetConnection();
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10));

        using IFetchedJob fetched = connection.FetchNextJob(["default"], limit.Token);

        Assert.Equal(id, fetched.JobId);
        using DbContext reader = storage.CreateContext();
        Assert.InRange(reader.Set<JobQueueRow>().Single().FetchedAt ?? DateTime.MinValue, DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow.AddSeconds(30));
    }

    private static void AssertTheLockStaysHeld(EntityFrameworkCoreStorage storage, TimeSpan off)
    {
        using IStorageConnection first = storage.GetConnection();
        using IStorageConnection second = storage.GetConnection();

        // One server, whose clock is off, holds the lock, with a 30 second lease
        JobClock.TimeProvider = new ShiftedClock(off);
        using IDisposable held = first.AcquireDistributedLock("lock:chains:maintenance", TimeSpan.Zero);

        // Another, whose clock is right, tries a moment later
        JobClock.TimeProvider = TimeProvider.System;
        Assert.Throws<DistributedLockTimeoutException>(() =>
        {
            using IDisposable taken = second.AcquireDistributedLock("lock:chains:maintenance", TimeSpan.Zero);
        });

        // And the first, whose clock is off, doesn't take what the other holds
        using IDisposable other = second.AcquireDistributedLock("lock:other", TimeSpan.Zero);
        JobClock.TimeProvider = new ShiftedClock(off);
        Assert.Throws<DistributedLockTimeoutException>(() =>
        {
            using IDisposable taken = first.AcquireDistributedLock("lock:other", TimeSpan.Zero);
        });
    }

    private static DateTime ExpireAt(EntityFrameworkCoreStorage storage, string resource)
    {
        using DbContext context = storage.CreateContext();
        return context.Set<LockRow>().AsNoTracking().Single(x => x.Resource == resource).ExpireAt;
    }

    private static string Enqueue(EntityFrameworkCoreStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        string id = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromDays(30))!;
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToQueue("default", id);
        transaction.Commit();
        return id;
    }

    /// <summary>The system clock, moved by a fixed time.</summary>
    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }
}
