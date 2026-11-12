using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Shared;

namespace Masterly.NightShift.InMemory.Tests;

public class InMemoryStorageFacts
{
    [Fact]
    public void Ctor_RefusesIntervalsThatAreNotPositive()
    {
        Assert.Throws<ArgumentNullException>(() => new InMemoryStorage(null!));
        Assert.Throws<ArgumentException>(() => new InMemoryStorage(new InMemoryStorageOptions { QueuePollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new InMemoryStorage(new InMemoryStorageOptions { InvisibilityTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => new InMemoryStorage(new InMemoryStorageOptions { ExpirationCheckInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new InMemoryStorage(new InMemoryStorageOptions { MaxExpirationTime = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new InMemoryStorage(new InMemoryStorageOptions { MaxExpirationTime = TimeSpan.FromMinutes(-1) }));
    }

    [Fact]
    public void Options_AreCopied_SoLaterChangesChangeNothing()
    {
        InMemoryStorageOptions options = new() { InvisibilityTimeout = TimeSpan.FromMinutes(5) };
        InMemoryStorage storage = new(options);

        options.InvisibilityTimeout = TimeSpan.Zero;
        storage.Options.InvisibilityTimeout = TimeSpan.FromMinutes(2);

        Assert.Equal(TimeSpan.FromMinutes(5), storage.Options.InvisibilityTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), storage.Settings.InvisibilityTimeout);
    }

    [Fact]
    public void RemoveExpired_RemovesTheEntries_OfAJobQueuedInSeveralQueues_AndKeepsTheOthers()
    {
        InMemoryStorage storage = new();
        string expired = CreateJob(storage, TimeSpan.FromHours(1));
        string kept = CreateJob(storage, TimeSpan.FromHours(1));
        Write(storage, x =>
        {
            x.AddToQueue("first", expired);
            x.AddToQueue("second", expired);
            x.AddToQueue("first", kept);
            x.ExpireJob(expired, TimeSpan.Zero);
        });

        storage.Database.RemoveExpired();

        IMonitoringApi monitoring = storage.GetMonitoringApi();
        Assert.Equal(1, monitoring.EnqueuedCount("first"));
        Assert.Equal(0, monitoring.EnqueuedCount("second"));
        Assert.DoesNotContain(monitoring.Queues(), static x => x.Name == "second");
        using IStorageConnection connection = storage.GetConnection();
        using IFetchedJob fetched = connection.FetchNextJob(["first", "second"], CancellationToken.None);
        Assert.Equal(kept, fetched.JobId);
        fetched.RemoveFromQueue();
    }

    [Fact]
    public void RemoveExpired_RemovesABoundedNumberOfJobsAtOnce()
    {
        InMemoryStorage storage = new();
        string[] ids = [.. Enumerable.Range(0, InMemoryDatabase.MaxJobsPerSweep + 5).Select(_ => CreateJob(storage, TimeSpan.FromHours(1)))];
        Write(storage, x =>
        {
            foreach (string id in ids)
            {
                x.SetJobState(id, new SucceededState(null, 1, 1));
                x.ExpireJob(id, TimeSpan.Zero);
            }
        });

        storage.Database.RemoveExpired();
        Assert.Equal(5, storage.GetMonitoringApi().SucceededListCount());

        storage.Database.RemoveExpired();
        Assert.Equal(0, storage.GetMonitoringApi().SucceededListCount());
    }

    [Fact]
    public void RemoveFromQueue_OfAJobAnotherStorageFetched_IsRefused_BeforeAnythingIsWritten()
    {
        InMemoryStorage storage = new();
        InMemoryStorage other = new();
        string id = CreateJob(other, TimeSpan.FromHours(1));
        Write(other, x => x.AddToQueue("default", id));
        using IStorageConnection otherConnection = other.GetConnection();
        using IFetchedJob theirs = otherConnection.FetchNextJob(["default"], CancellationToken.None);

        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        Assert.Throws<NotSupportedException>(() => ((JobStorageTransaction)transaction).RemoveFromQueue(theirs));
        Assert.Throws<NotSupportedException>(() => ((JobStorageTransaction)transaction).RemoveFromQueue(new ForeignFetchedJob()));

        // The database refuses them too, before applying the operations that come first
        Assert.Throws<NotSupportedException>(() => storage.Database.Apply([new AddToSetOperation("set", "a", 0), new RemoveFromQueueOperation(new ForeignFetchedJob())]));
        Assert.Equal(0, storage.Database.GetSetCount("set"));
    }

    [Fact]
    public void Ranges_ReachingThePositionsLimit_ReadToTheEnd()
    {
        InMemoryStorage storage = new();
        Write(storage, x =>
        {
            x.AddToSet("set", "a");
            x.InsertToList("list", "1");
        });

        Assert.Equal(["a"], storage.Database.GetRangeFromSet("set", 0, int.MaxValue));
        Assert.Equal(["1"], storage.Database.GetRangeFromList("list", int.MinValue, int.MaxValue));
    }

    [Fact]
    public void GetFirstByLowestScoreFromSet_FindsValuesThatSortAfterAnyFixedBound()
    {
        InMemoryStorage storage = new();
        string highest = new(char.MaxValue, 8);
        Write(storage, x =>
        {
            x.AddToSet("set", highest, 5);
            x.AddToSet("set", "a", 5);
            x.AddToSet("set", "later", 6);
        });

        Assert.Equal(["a", highest], storage.Database.GetFirstByLowestScoreFromSet("set", 5, 5, 10));
    }

    [Fact]
    public void ExpireAt_IsClamped_BothWays()
    {
        DateTime now = DateTime.UtcNow;

        Assert.Equal(DateTime.MaxValue, StorageValues.ExpireAt(now, TimeSpan.MaxValue));
        Assert.Equal(DateTime.MinValue, StorageValues.ExpireAt(now, TimeSpan.MinValue));
        Assert.Equal(DateTimeKind.Utc, StorageValues.ExpireAt(now, TimeSpan.MinValue).Kind);
        Assert.Equal(now.AddHours(-1), StorageValues.ExpireAt(now, TimeSpan.FromHours(-1)));
    }

    [Fact]
    public void Storage_HasLinearizableReads_AndEveryExtendedFeature()
    {
        InMemoryStorage storage = new();

        Assert.True(storage.LinearizableReads);
        Assert.True(storage.HasFeature(JobStorageFeatures.ExtendedApi));
        Assert.True(storage.HasFeature(JobStorageFeatures.Transaction.RemoveFromQueue(typeof(InMemoryFetchedJob))));
        Assert.False(storage.HasFeature(JobStorageFeatures.Transaction.CreateJob));
        Assert.False(storage.HasFeature("Unknown.Feature"));
    }

    [Fact]
    public void RemoveExpired_RemovesExpiredJobs_WithTheirQueueEntries_AndExpiredKeys()
    {
        InMemoryStorage storage = new();
        string expired = CreateJob(storage, TimeSpan.Zero);
        string kept = CreateJob(storage, TimeSpan.FromHours(1));
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToQueue("default", expired);
            transaction.AddToSet("set", "a");
            ((JobStorageTransaction)transaction).ExpireSet("set", TimeSpan.Zero);
            transaction.IncrementCounter("counter", TimeSpan.Zero);
            transaction.InsertToList("list", "a");
            transaction.Commit();
        }

        storage.Database.RemoveExpired();

        using IStorageConnection reader = storage.GetConnection();
        Assert.Null(reader.GetJobData(expired));
        Assert.NotNull(reader.GetJobData(kept));
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Empty(reader.GetAllItemsFromSet("set"));
        Assert.Equal(0, ((JobStorageConnection)reader).GetCounter("counter"));
        Assert.Single(((JobStorageConnection)reader).GetAllItemsFromList("list"));
    }

    [Fact]
    public void MaxExpirationTime_CapsLongExpirations()
    {
        InMemoryStorage storage = new(new InMemoryStorageOptions { MaxExpirationTime = TimeSpan.FromMinutes(5) });

        string id = CreateJob(storage, TimeSpan.FromDays(30));

        DateTime? expireAt = storage.GetMonitoringApi().JobDetails(id)?.ExpireAt;
        Assert.InRange(expireAt ?? DateTime.MaxValue, JobClock.UtcNow, JobClock.UtcNow.AddMinutes(6));
    }

    [Fact]
    public void FetchedEntry_ThatNobodyKeepsAlive_BecomesVisible_AfterTheInvisibilityTimeout()
    {
        InMemoryStorage storage = new(new InMemoryStorageOptions { InvisibilityTimeout = TimeSpan.FromMilliseconds(200) });
        string id = CreateJob(storage, TimeSpan.FromHours(1));
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToQueue("default", id);
            transaction.Commit();
        }

        // Taken straight from the database, as by a worker whose process stopped: nothing keeps it alive
        (long _, long jobId, string _, string firstToken) = storage.Database.Fetch(["default"], CancellationToken.None);
        Thread.Sleep(400);
        (long _, long againId, string _, string secondToken) = storage.Database.Fetch(["default"], CancellationToken.None);

        Assert.Equal(jobId, againId);
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public void Lease_RenewedAfterItsRelease_IsNotBroughtBack()
    {
        InMemoryStorage storage = new();
        ILeaseStore leases = storage.Database;
        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow.AddMinutes(1)));
        leases.Release("resource", "owner");

        // What a renewal still on its way does when it arrives after the release
        bool renewed = leases.TryRenew("resource", "owner", JobClock.UtcNow.AddMinutes(1));

        Assert.False(renewed);
        Assert.True(leases.TryTake("resource", "other-owner", JobClock.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void Lease_TakenByAnotherOwner_IsNeitherRenewedNorReleased_ByTheEarlierOne()
    {
        InMemoryStorage storage = new();
        ILeaseStore leases = storage.Database;
        Assert.True(leases.TryTake("resource", "owner", JobClock.UtcNow.AddMinutes(1)));
        leases.Release("resource", "owner");
        Assert.True(leases.TryTake("resource", "other-owner", JobClock.UtcNow.AddMinutes(1)));

        bool renewed = leases.TryRenew("resource", "owner", JobClock.UtcNow.AddMinutes(1));
        leases.Release("resource", "owner");

        Assert.False(renewed);
        Assert.True(leases.TryRenew("resource", "other-owner", JobClock.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void UseInMemoryStorage_MakesItTheCurrentStorage()
    {
        Mock<IGlobalConfiguration> configuration = new();

        IGlobalConfiguration<InMemoryStorage> result = configuration.Object.UseInMemoryStorage(new InMemoryStorageOptions { QueuePollInterval = TimeSpan.FromSeconds(2) });

        Assert.Equal(TimeSpan.FromSeconds(2), result.Entry.Options.QueuePollInterval);
        Assert.Same(result.Entry, JobStorage.Current);
    }

    private static string CreateJob(InMemoryStorage storage, TimeSpan expireIn)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, expireIn)!;
    }

    private static void Write(InMemoryStorage storage, Action<IWriteOnlyTransaction> write)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    /// <summary>A job fetched by some other storage built on the shared layer.</summary>
    private sealed class ForeignFetchedJob() : FetchedJobBase("1", "default", null)
    {
        protected override void Remove()
        {
        }

        protected override void ReturnToQueue()
        {
        }
    }
}
