using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Conformance;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>Values longer than their columns are refused before anything is written, on every provider.</summary>
public sealed class EfLimitsFacts : IDisposable
{
    private readonly SqliteDatabase _database = new();
    private readonly EntityFrameworkCoreStorage _storage;

    public EfLimitsFacts()
    {
        _storage = _database.CreateStorage();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Transaction_RefusesALongKey_AndWritesNothing()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => Commit(x =>
        {
            x.AddToSet("fits", "a");
            x.AddToSet(new string('k', 101), "a");
        }));

        Assert.Contains("at most 100 characters", exception.Message, StringComparison.Ordinal);
        using IStorageConnection connection = _storage.GetConnection();
        Assert.Empty(connection.GetAllItemsFromSet("fits"));
    }

    [Fact]
    public void Transaction_AcceptsValuesThatFitExactly()
    {
        string key = new('k', 100);
        string job = CreateJob();

        Commit(x =>
        {
            x.AddToSet(key, new string('v', 256));
            x.InsertToList(key, "item");
            x.IncrementCounter(key);
            x.SetRangeInHash(key, [new(new string('f', 100), "value")]);
            x.AddToQueue(new string('q', 50), job);
            ((JobStorageTransaction)x).SetJobParameter(job, new string('p', 40), "value");
        });

        using IStorageConnection connection = _storage.GetConnection();
        Assert.Single(connection.GetAllItemsFromSet(key));
        Assert.Equal("value", connection.GetJobParameter(job, new string('p', 40)));
    }

    [Fact]
    public void Transaction_RefusesLongSetValues_HashFields_QueueNames_AndParameterNames()
    {
        string job = CreateJob();

        Assert.Throws<ArgumentException>(() => Commit(x => x.AddToSet("set", new string('v', 257))));
        Assert.Throws<ArgumentException>(() => Commit(x => ((JobStorageTransaction)x).AddRangeToSet("set", ["a", new string('v', 257)])));
        Assert.Throws<ArgumentException>(() => Commit(x => x.SetRangeInHash("hash", [new(new string('f', 101), "value")])));
        Assert.Throws<ArgumentException>(() => Commit(x => x.AddToQueue(new string('q', 51), job)));
        Assert.Throws<ArgumentException>(() => Commit(x => x.IncrementCounter(new string('c', 101))));
        Assert.Throws<ArgumentException>(() => Commit(x => x.InsertToList(new string('l', 101), "item")));

        using IStorageConnection connection = _storage.GetConnection();
        Assert.Throws<ArgumentException>(() => connection.SetJobParameter(job, new string('p', 41), "value"));
        Assert.Throws<ArgumentException>(() => connection.SetRangeInHash(new string('h', 101), [new("field", "value")]));
        Assert.Equal(0, _storage.GetMonitoringApi().EnqueuedCount(new string('q', 51)));
    }

    [Fact]
    public void Transaction_RefusesALongStateName()
    {
        string job = CreateJob();
        Mock<IState> state = new();
        state.SetupGet(x => x.Name).Returns(new string('s', 21));
        state.Setup(x => x.SerializeData()).Returns([]);

        Assert.Throws<ArgumentException>(() => Commit(x => x.SetJobState(job, state.Object)));

        using IStorageConnection connection = _storage.GetConnection();
        Assert.Null(connection.GetStateData(job));
    }

    [Fact]
    public void CreateExpiredJob_RefusesALongParameterName()
    {
        using IStorageConnection connection = _storage.GetConnection();

        Assert.Throws<ArgumentException>(() => connection.CreateExpiredJob(
            Job.FromExpression(() => ConformanceJobs.Record("x")),
            new Dictionary<string, string?> { [new string('p', 41)] = "value" },
            JobClock.UtcNow,
            TimeSpan.FromHours(1)));
    }

    [Fact]
    public void AnnounceServer_RefusesALongServerId()
    {
        using IStorageConnection connection = _storage.GetConnection();

        Assert.Throws<ArgumentException>(() => connection.AnnounceServer(new string('s', 201), new ServerContext { Queues = ["default"], WorkerCount = 1 }));
    }

    [Fact]
    public void LockResource_KeepsShortNames_AndShortensLongOnes_ToDistinctNamesThatFit()
    {
        string common = new('r', 300);

        string first = EfLimits.LockResource(common + "1");
        string second = EfLimits.LockResource(common + "2");

        Assert.Equal("short", EfLimits.LockResource("short"));
        Assert.Equal(EfLimits.LockResourceLength, first.Length);
        Assert.NotEqual(first, second);
        Assert.Equal(first, EfLimits.LockResource(common + "1"));
    }

    [Fact]
    public void Locks_OnLongResources_SharingTheirBeginning_AreDistinct()
    {
        string common = new('r', 300);
        using IStorageConnection first = _storage.GetConnection();
        using IStorageConnection second = _storage.GetConnection();

        using IDisposable one = first.AcquireDistributedLock(common + "1", TimeSpan.FromSeconds(5));
        using IDisposable two = second.AcquireDistributedLock(common + "2", TimeSpan.FromSeconds(1));

        Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock(common + "1", TimeSpan.FromMilliseconds(200)));
        using DbContext context = _storage.CreateContext();
        Assert.All(context.Set<LockRow>().AsNoTracking(), static x => Assert.Equal(EfLimits.LockResourceLength, x.Resource.Length));
    }

    private void Commit(Action<IWriteOnlyTransaction> write)
    {
        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    private string CreateJob()
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => ConformanceJobs.Record("x")), new Dictionary<string, string?>(), JobClock.UtcNow, TimeSpan.FromHours(1))!;
    }
}
