using Masterly.NightShift.Storage.Conformance;

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
}
