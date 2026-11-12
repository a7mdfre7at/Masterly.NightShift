using System.Diagnostics;
using Masterly.NightShift.Client;
using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>
/// A chain that is being created is protected by a lease, written by the client and compared by the servers. On
/// this storage both tell the time with the database's clock, so their own clocks needn't agree.
/// </summary>
public sealed class EfChainLeaseFacts : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly SqliteDatabase _database = new();
    private readonly PausingFilter _filter = new();

    public EfChainLeaseFacts()
    {
        GlobalJobFilters.Filters.Add(_filter);
    }

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        GlobalJobFilters.Filters.Remove(_filter);
        _database.Dispose();
    }

    [Fact]
    public void ChainBeingCreated_IsNotTakenForAbandoned_ByAServerWhoseClockIsAhead_OnSqlite()
    {
        AssertTheChainIsCreated(_database.CreateStorage(Options()));
    }

    [Fact]
    public void ChainBeingCreated_IsNotTakenForAbandoned_ByAServerWhoseClockIsAhead_OnSqlServer()
    {
        AssertTheChainIsCreated(SqlServerDatabase.CreateStorage(Options()));
    }

    // The test moves this process's clock: locks and fetched jobs, which go by it, mustn't look abandoned for it
    private static EntityFrameworkCoreStorageOptions Options() => new()
    {
        DistributedLockLifetime = TimeSpan.FromDays(1),
        InvisibilityTimeout = TimeSpan.FromDays(1),
        QueuePollInterval = TimeSpan.FromMilliseconds(100),
    };

    private void AssertTheChainIsCreated(EntityFrameworkCoreStorage storage)
    {
        Assert.True(storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime));
        BackgroundJobClient client = new(storage);
        string prefix = Guid.NewGuid().ToString("N");
        string? stateOfTheFirstStep = null;

        _filter.PauseOn = $"{prefix}-c";
        _filter.WhilePaused = () =>
        {
            // While the client creates the third step, a server whose clock is ten minutes ahead of the client's
            // takes the first: by its own clock, the client's two-minute lease lapsed long ago
            string first = _filter.Created.Single(x => x.Name == $"{prefix}-a").Id;
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(10));

            using BackgroundJobServer server = new(
                new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) },
                storage);

            Stopwatch stopwatch = Stopwatch.StartNew();
            while ((stateOfTheFirstStep = StateOf(storage, first)) != ScheduledState.StateName && stateOfTheFirstStep != DeletedState.StateName)
            {
                Assert.True(stopwatch.Elapsed < Timeout, $"The server didn't look at the first step; it is {stateOfTheFirstStep}.");
                Thread.Sleep(50);
            }
        };

        JobChainResult? chain = JobChain
            .StartWith(() => ConformanceJobs.Record($"{prefix}-a"))
            .Then(() => ConformanceJobs.Record($"{prefix}-b"))
            .Then(() => ConformanceJobs.Record($"{prefix}-c"))
            .Enqueue(client);

        // The server made the step wait for the chain to be recorded, instead of deleting it
        Assert.NotNull(chain);
        Assert.Equal(ScheduledState.StateName, stateOfTheFirstStep);
        Assert.Equal(0, ConformanceJobs.Runs($"{prefix}-a"));

        // Once recorded, the chain runs to its end
        using BackgroundJobServer runner = new(
            new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) },
            storage);

        Stopwatch waiting = Stopwatch.StartNew();
        while (ConformanceJobs.Runs($"{prefix}-c") == 0)
        {
            Assert.True(waiting.Elapsed < Timeout, $"The chain didn't run to its end; its first step is {StateOf(storage, chain.RootId)}.");
            Thread.Sleep(50);
        }

        Assert.Equal([1, 1, 1], new[] { "a", "b", "c" }.Select(x => ConformanceJobs.Runs($"{prefix}-{x}")));
    }

    private static string? StateOf(EntityFrameworkCoreStorage storage, string jobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetStateData(jobId)?.Name;
    }

    /// <summary>The system clock, moved by a fixed time.</summary>
    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    /// <summary>Stops the creation of the step with a given argument for as long as an action takes.</summary>
    private sealed class PausingFilter : IClientFilter
    {
        public string? PauseOn { get; set; }

        public Action? WhilePaused { get; set; }

        public List<(string Name, string Id)> Created { get; } = [];

        public void OnCreating(CreatingContext context)
        {
            if (PauseOn != null && context.Job.Type == typeof(ConformanceJobs) && context.Job.Args is [string name, ..] && name == PauseOn)
            {
                WhilePaused?.Invoke();
            }
        }

        public void OnCreated(CreatedContext context)
        {
            if (context.Job.Type == typeof(ConformanceJobs) && context.BackgroundJob != null && context.Job.Args is [string name, ..])
            {
                Created.Add((name, context.BackgroundJob.Id));
            }
        }
    }
}
