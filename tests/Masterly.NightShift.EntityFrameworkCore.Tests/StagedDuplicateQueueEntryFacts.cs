using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>
/// A staged job that gets more than one queue entry runs once: enqueued and requeued in the same unit of work,
/// or staged and requeued after the application saved.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class StagedDuplicateQueueEntryFacts : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, int> Runs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> Running = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> MostRunning = new(StringComparer.Ordinal);

    private readonly SqliteDatabase _database = new();
    private readonly EntityFrameworkCoreStorage _storage;

    public StagedDuplicateQueueEntryFacts()
    {
        using (ShopContext context = CreateContext())
        {
            context.Database.EnsureCreated();
        }

        _storage = new EntityFrameworkCoreStorage(CreateContext);
    }

    public void Dispose() => _database.Dispose();

    [AutomaticRetry(Attempts = 0)]
    public static void Slow(string key)
    {
        Runs.AddOrUpdate(key, 1, static (_, count) => count + 1);
        int running = Running.AddOrUpdate(key, 1, static (_, count) => count + 1);
        MostRunning.AddOrUpdate(key, running, (_, most) => Math.Max(most, running));
        Thread.Sleep(TimeSpan.FromSeconds(1));
        Running.AddOrUpdate(key, 0, static (_, count) => count - 1);
    }

    [Fact]
    public void StagedJob_RequeuedBeforeTheContextSaves_GetsNoSecondEntry_AndRunsOnce()
    {
        string key = Guid.NewGuid().ToString("N");
        string id;

        using (ShopContext context = CreateContext())
        {
            context.Orders.Add(new Order { Name = "Book" });
            IBackgroundJobClient staged = context.NightShift(_storage);
            id = staged.Enqueue(() => Slow(key))!;

            // The job has no state until the context saves: nothing to requeue it from
            Assert.False(staged.Requeue(id));
            context.SaveChanges();
        }

        Assert.Equal(1, _storage.GetMonitoringApi().EnqueuedCount(EnqueuedState.DefaultQueue));

        RunUntilEveryEntryIsTaken(id, workers: 2);

        Assert.Equal((1, 1), (Runs.GetValueOrDefault(key), MostRunning.GetValueOrDefault(key)));
    }

    [Fact]
    public void StagedJob_RequeuedThroughTheSameContext_AfterItSaved_RunsOnce()
    {
        string key = Guid.NewGuid().ToString("N");
        string id;

        using (ShopContext context = CreateContext())
        {
            IBackgroundJobClient staged = context.NightShift(_storage);
            id = staged.Enqueue(() => Slow(key))!;
            context.SaveChanges();

            // Staged again, with more of the application's data: a second entry, saved with it
            context.Orders.Add(new Order { Name = "Book" });
            Assert.True(staged.Requeue(id));
            context.SaveChanges();
        }

        Assert.Equal(2, _storage.GetMonitoringApi().EnqueuedCount(EnqueuedState.DefaultQueue));

        RunUntilEveryEntryIsTaken(id, workers: 2);

        Assert.Equal((1, 1), (Runs.GetValueOrDefault(key), MostRunning.GetValueOrDefault(key)));
    }

    [Fact]
    public void StagedJob_RequeuedAfterTheApplicationSaved_RunsOnce()
    {
        string key = Guid.NewGuid().ToString("N");
        string id;

        using (ShopContext context = CreateContext())
        {
            id = context.NightShift(_storage).Enqueue(() => Slow(key))!;
            context.SaveChanges();
        }

        // The dashboard's Delete, then Requeue on the Deleted page, before a worker took the job
        BackgroundJobClient client = new(_storage);
        Assert.True(client.ChangeState(id, new DeletedState(), EnqueuedState.StateName));
        Assert.True(client.ChangeState(id, new EnqueuedState(), DeletedState.StateName));

        RunUntilEveryEntryIsTaken(id, workers: 2);

        Assert.Equal((1, 1), (Runs.GetValueOrDefault(key), MostRunning.GetValueOrDefault(key)));
    }

    /// <summary>Runs a server until the job succeeded and every entry of its queue was taken by a worker.</summary>
    private void RunUntilEveryEntryIsTaken(string id, int workers)
    {
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = workers }, _storage);

        WaitFor(() =>
        {
            using IStorageConnection connection = _storage.GetConnection();
            return connection.GetStateData(id)?.Name == SucceededState.StateName;
        });
        WaitFor(() => _storage.GetMonitoringApi() is { } monitoring
            && monitoring.EnqueuedCount(EnqueuedState.DefaultQueue) == 0
            && monitoring.FetchedCount(EnqueuedState.DefaultQueue) == 0);
    }

    private static void WaitFor(Func<bool> condition)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Timeout, "The condition wasn't met in time.");
            Thread.Sleep(25);
        }
    }

    private ShopContext CreateContext() => new(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_database.ConnectionString).Options);

    private sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyNightShiftModel();
        }
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
