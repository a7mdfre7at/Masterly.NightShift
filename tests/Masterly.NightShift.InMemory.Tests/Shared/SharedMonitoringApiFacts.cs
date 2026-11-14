using Masterly.NightShift.Storage.Conformance;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.InMemory.Tests.Shared;

/// <summary>The dashboard's lists of the shared layer (src/Shared/Storage/SharedMonitoringApi.cs) over state data it can't read.</summary>
public class SharedMonitoringApiFacts
{
    private readonly InMemoryStorage _storage = new();

    [Fact]
    public void SucceededJobs_ListsARowWhoseDurationIsNotANumber_WithoutItsTotalDuration()
    {
        string bad = CreateJob(new RawState("Succeeded", new() { ["PerformanceDuration"] = "abc", ["Latency"] = "10" }));
        string good = CreateJob(new SucceededState(null, 10, 20));

        JobList<SucceededJobDto?> jobs = _storage.GetMonitoringApi().SucceededJobs(0, 10);

        Assert.Null(Single(jobs, bad).TotalDuration);
        Assert.Equal(30, Single(jobs, good).TotalDuration);
    }

    [Fact]
    public void ScheduledJobs_ListsARowWhoseEnqueueAtIsNotADate_WithTheMinimumDate()
    {
        string bad = CreateJob(new RawState("Scheduled", new() { ["EnqueueAt"] = "nonsense", ["ScheduledAt"] = "nonsense" }));
        Write(x => x.AddToSet("schedule", bad, 0));
        DateTime enqueueAt = new(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string good = CreateJob(new ScheduledState(enqueueAt));

        JobList<ScheduledJobDto?> jobs = _storage.GetMonitoringApi().ScheduledJobs(0, 10);

        Assert.Equal(DateTime.MinValue, Single(jobs, bad).EnqueueAt);
        Assert.Equal(enqueueAt, Single(jobs, good).EnqueueAt);
    }

    [Fact]
    public void ProcessingJobs_ListsARowWithoutAServer_WithANullServerId()
    {
        string withoutServer = CreateJob(new RawState("Processing", new() { ["WorkerId"] = "worker-1" }));
        string legacy = CreateJob(new RawState("Processing", new() { ["ServerName"] = "old-server", ["WorkerId"] = "worker-1" }));

        JobList<ProcessingJobDto?> jobs = _storage.GetMonitoringApi().ProcessingJobs(0, 10);

        Assert.Null(Single(jobs, withoutServer).ServerId);
        Assert.Equal("old-server", Single(jobs, legacy).ServerId);
    }

    private string CreateJob(IState state)
    {
        string id;
        using (IStorageConnection connection = _storage.GetConnection())
        {
            id = connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromHours(1))!;
        }

        Write(x => x.SetJobState(id, state));
        return id;
    }

    private void Write(Action<IWriteOnlyTransaction> write)
    {
        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    private static T Single<T>(JobList<T?> jobs, string id)
        where T : class
    {
        T? job = Assert.Single(jobs, x => x.Key == id).Value;
        Assert.NotNull(job);
        return job;
    }

    /// <summary>A state written as another writer would: any name, any data.</summary>
    private sealed class RawState(string name, Dictionary<string, string> data) : IState
    {
        public string Name { get; } = name;

        public string? Reason => null;

        public bool IsFinal => false;

        public bool IgnoreJobLoadException => false;

        public Dictionary<string, string> SerializeData() => data;
    }
}
