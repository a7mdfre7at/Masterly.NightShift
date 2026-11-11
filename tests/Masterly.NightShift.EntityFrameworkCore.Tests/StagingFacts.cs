using System.Diagnostics;
using Masterly.NightShift.Storage.Conformance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>Jobs staged in an application's context commit or roll back with it.</summary>
public sealed class StagingFacts : IDisposable
{
    private readonly SqliteDatabase _database = new();
    private readonly EntityFrameworkCoreStorage _storage;

    public StagingFacts()
    {
        using (ShopContext context = CreateContext())
        {
            context.Database.EnsureCreated();
        }

        _storage = new EntityFrameworkCoreStorage(CreateContext);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void StagedJob_IsEnqueued_WhenTheContextSaves()
    {
        string? id;
        using (ShopContext context = CreateContext())
        {
            context.Orders.Add(new Order { Name = "Book" });
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("staged"));

            Assert.NotNull(id);
            Assert.Null(State(id));
            Assert.Equal(0, _storage.GetMonitoringApi().EnqueuedCount("default"));

            context.SaveChanges();
        }

        Assert.Equal(EnqueuedState.StateName, State(id));
        Assert.Equal(id, Assert.Single(_storage.GetMonitoringApi().EnqueuedJobs("default", 0, 10)).Key);
    }

    [Fact]
    public void StagedJob_IsNeverEnqueued_WhenTheContextIsNotSaved()
    {
        string? id;
        using (ShopContext context = CreateContext())
        {
            context.Orders.Add(new Order { Name = "Book" });
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record("discarded"));
        }

        Assert.Null(State(id!));
        Assert.Equal(0, _storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.NotNull(_storage.GetMonitoringApi().JobDetails(id!)?.ExpireAt);
    }

    [Fact]
    public void StagedJob_RollsBack_WithTheApplicationsTransaction()
    {
        // On SQL Server: SQLite has one writer at a time, so the id can't be reserved while the application's
        // transaction holds the write lock
        string connectionString = SqlServerDatabase.ConnectionString("Staging");
        using (ShopContext setup = CreateSqlServerContext(connectionString))
        {
            setup.Database.EnsureDeleted();
            setup.Database.EnsureCreated();
        }

        EntityFrameworkCoreStorage storage = new(() => CreateSqlServerContext(connectionString));
        string? id;
        using (ShopContext context = CreateSqlServerContext(connectionString))
        {
            using IDbContextTransaction transaction = context.Database.BeginTransaction();
            context.Orders.Add(new Order { Name = "Book" });
            id = context.NightShift(storage).Enqueue(() => ConformanceJobs.Record("rolled-back"));
            context.SaveChanges();
            Assert.Equal(EnqueuedState.StateName, context.Set<JobRow>().Single(x => x.Id == long.Parse(id!)).StateName);
            transaction.Rollback();
        }

        using ShopContext reader = CreateSqlServerContext(connectionString);
        Assert.Empty(reader.Orders);
        using IStorageConnection connection = storage.GetConnection();
        Assert.Null(connection.GetStateData(id!));
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("default"));
    }

    [Fact]
    public void StagedSchedule_IsScheduled_WhenTheContextSaves()
    {
        using ShopContext context = CreateContext();

        string? id = context.NightShift(_storage).Schedule(() => ConformanceJobs.Record("later"), TimeSpan.FromHours(1));
        context.SaveChanges();

        Assert.Equal(ScheduledState.StateName, State(id!));
        Assert.Equal(1, _storage.GetMonitoringApi().ScheduledCount());
    }

    [Fact]
    public async Task StagedJob_RunsOnAServer_OnceSaved()
    {
        string key = Guid.NewGuid().ToString("N");
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, _storage);

        string? id;
        using (ShopContext context = CreateContext())
        {
            id = context.NightShift(_storage).Enqueue(() => ConformanceJobs.Record(key));
            context.SaveChanges();
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (State(id!) != SucceededState.StateName)
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Job {id} didn't succeed.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, ConformanceJobs.Runs(key));
    }

    [Fact]
    public void NightShift_Refuses_AContextWithoutTheStoragesTables()
    {
        using EmptyContext context = new(new DbContextOptionsBuilder<EmptyContext>().UseSqlite(_database.ConnectionString).Options);

        Assert.Throws<InvalidOperationException>(() => context.NightShift(_storage));
    }

    [Fact]
    public void Storage_Refuses_AContextFactoryWhoseModelLacksTheTables()
    {
        Assert.Throws<InvalidOperationException>(() => new EntityFrameworkCoreStorage(() => new EmptyContext(new DbContextOptionsBuilder<EmptyContext>().UseSqlite(_database.ConnectionString).Options)));
    }

    private string? State(string id)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetStateData(id)?.Name;
    }

    private ShopContext CreateContext() => new(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_database.ConnectionString).Options);

    private static ShopContext CreateSqlServerContext(string connectionString)
    {
        return new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlServer(connectionString).Options, NightShiftModelBuilderExtensions.DefaultSchema);
    }

    private sealed class ShopContext(DbContextOptions<ShopContext> options, string? schema = null) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyNightShiftModel(schema);
        }
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);

    private sealed class Order
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
