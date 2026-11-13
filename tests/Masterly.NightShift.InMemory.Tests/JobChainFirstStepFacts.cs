using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// What a server's workers do with the first step of a chain that is still being created, and what that costs the
/// other jobs: through a real storage and real servers, with clients that take their time to create a chain.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class JobChainFirstStepFacts : IDisposable
{
    private static readonly ConcurrentDictionary<string, int> Runs = new();

    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobClient _client;
    private readonly SlowCreation _slow = new();
    private object? _firstFilter;

    public JobChainFirstStepFacts()
    {
        _client = new BackgroundJobClient(_storage);
        GlobalJobFilters.Filters.Add(_slow);
        Runs.Clear();
    }

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        GlobalJobFilters.Filters.Remove(_slow);
        if (_firstFilter != null)
        {
            GlobalJobFilters.Filters.Remove(_firstFilter);
        }
    }

    public static void Mark(string name) => Runs.AddOrUpdate(name, 1, static (_, count) => count + 1);

    public static void First()
    {
    }

    /// <summary>A step whose creation takes a while (see <see cref="SlowCreation"/>).</summary>
    public static void Slow(string name) => _ = name;

    /// <summary>A step whose creation stops the client for good.</summary>
    public static void Crash(string name) => _ = name;

    [Theory]
    [InlineData(1, 100, "beta", false)]
    [InlineData(1, 100, "beta", true)]
    [InlineData(1, 100, "alpha", false)]
    [InlineData(2, 1000, "beta", false)]
    [InlineData(2, 1000, "beta", true)]
    [InlineData(4, 300, "beta", false)]
    [InlineData(4, 300, "beta", true)]
    public void Job_InAnyQueueOfTheServer_RunsWithinSeconds_WhileAsManyClientsAsWorkersCreateChainsBackToBack(int workers, int creationMs, string queue, bool holdAsBefore)
    {
        // With as many chains in creation as the server has workers, each worker finds the first step of a chain
        // that isn't recorded yet, whenever it looks. Waiting for each of them, the workers would never get to
        // the queue after the chains', for as long as chains keep being created.
        if (holdAsBefore)
        {
            // A filter ordered before the chain's gate: the gate holds the step in the Scheduled state, as it
            // did before workers waited
            _firstFilter = new SeesEveryElection();
            GlobalJobFilters.Filters.Add(_firstFilter, -2);
        }

        TimeSpan waited = WaitsOfJobs(queue, workers, TimeSpan.FromMilliseconds(creationMs), TimeSpan.FromSeconds(0.5)).Single();
        TestContext.Current.TestOutputHelper?.WriteLine($"The job waited {waited.TotalSeconds:0.00} s.");

        // The workers of a pool wait for two seconds in ten at most, all together
        Assert.InRange(waited, TimeSpan.Zero, TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Jobs_InAnotherQueueOfTheServer_RunWithinSeconds_EachTime_HoweverLongChainsKeepBeingCreated(int workers)
    {
        // Not only the first time: also when the workers may wait again, after ten seconds, and in between
        TimeSpan[] waits = WaitsOfJobs("beta", workers, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(11.5), TimeSpan.FromSeconds(13));
        TestContext.Current.TestOutputHelper?.WriteLine($"The jobs waited {string.Join(", ", waits.Select(static x => $"{x.TotalSeconds:0.00} s"))}.");

        Assert.All(waits, static waited => Assert.InRange(waited, TimeSpan.Zero, TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task Server_ThatStopsWhileAWorkerWaitsForAChainToBeRecorded_LeavesTheStepInItsQueue_AndItRunsOnce()
    {
        _slow.Delay = TimeSpan.FromSeconds(3);
        JobChainResult? chain = null;
        Task creation;

        using (BackgroundJobServer server = CreateServer(2))
        {
            creation = Task.Run(() => chain = JobChain.StartWith(() => Mark("first")).Then(() => Slow("slow")).Enqueue(_client), TestContext.Current.CancellationToken);
            Thread.Sleep(1200);
            server.SendStop();
            Assert.True(server.WaitForShutdown(TimeSpan.FromSeconds(10)));
        }

        await creation;

        // As it was: enqueued, in its queue, not fetched, and with nothing more in its history
        Assert.False(Runs.ContainsKey("first"));
        Assert.Equal(1, _storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Equal(0, _storage.GetMonitoringApi().FetchedCount("default"));
        Assert.Equal([EnqueuedState.StateName], _storage.GetMonitoringApi().JobDetails(chain!.RootId).History.Select(static x => x.StateName));

        using BackgroundJobServer second = CreateServer(2);
        Assert.True(SpinWait.SpinUntil(() => _storage.GetChain(chain.ChainId) is { IsSettled: true }, TimeSpan.FromSeconds(20)));
        Assert.Equal(1, Runs["first"]);
    }

    [Fact]
    public async Task FirstStep_OfAChainThatTakesAMomentToCreate_StaysWithTheWorkerThatTookIt_UntilItRuns()
    {
        // The worker keeps the step and looks again: the step doesn't go back and forth between the queue and
        // the workers, as a job does only when its worker failed or stopped
        _slow.Delay = TimeSpan.FromSeconds(1.5);
        using BackgroundJobServer server = CreateServer(2);
        List<(long Enqueued, long Fetched)> seen = [];

        Task<JobChainResult?> creation = Task.Run(() => JobChain.StartWith(() => Mark("first")).Then(() => Slow("slow")).Enqueue(_client), TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => _storage.GetMonitoringApi().FetchedCount("default") == 1, TimeSpan.FromSeconds(10)));

        Stopwatch watched = Stopwatch.StartNew();
        while (watched.Elapsed < TimeSpan.FromSeconds(1))
        {
            seen.Add((_storage.GetMonitoringApi().EnqueuedCount("default"), _storage.GetMonitoringApi().FetchedCount("default")));
        }

        JobChainResult chain = (await creation)!;
        Assert.True(SpinWait.SpinUntil(() => _storage.GetChain(chain.ChainId) is { IsSettled: true }, TimeSpan.FromSeconds(20)));

        Assert.All(seen, static x => Assert.Equal((0L, 1L), x));
        Assert.Equal(1, Runs["first"]);
        Assert.Equal(
            [EnqueuedState.StateName, ProcessingState.StateName, SucceededState.StateName],
            _storage.GetMonitoringApi().JobDetails(chain.RootId).History.Reverse().Select(static x => x.StateName));
    }

    [Fact]
    public void FirstStep_OfAChainWhoseClientStopped_IsHeldAfterSeconds_AlsoWhenTheClockTheServerGoesByStandsStill()
    {
        // The client stops for good while it creates the chain; the application's clock (UseTimeProvider) is
        // frozen, so by that clock the creation never takes long. A worker must not look at the step for ever.
        _slow.CrashOn = nameof(Crash);
        Assert.Throws<OutOfMemoryException>(() => JobChain.StartWith(() => Mark("first")).Then(() => Crash("stop")).Enqueue(_client));
        string first = _slow.Created.Single();

        JobClock.TimeProvider = new FrozenClock(DateTimeOffset.UtcNow);
        using BackgroundJobServer server = CreateServer(2);

        // By real time: a few seconds, then the step is held, as the step of a chain that takes long to create is
        Assert.True(
            SpinWait.SpinUntil(() => StateOf(first) == ScheduledState.StateName, TimeSpan.FromSeconds(12)),
            $"The step is still {StateOf(first)}: a worker goes on looking at it.");
        Assert.False(Runs.ContainsKey("first"));
    }

    // How long jobs enqueued at the given times wait to run, while as many clients as the server has workers
    // create chains back to back, each taking a while
    private TimeSpan[] WaitsOfJobs(string queue, int workers, TimeSpan creation, params TimeSpan[] enqueuedAfter)
    {
        _slow.Delay = creation;

        using BackgroundJobServer server = new(
            new BackgroundJobServerOptions { WorkerCount = workers, Queues = ["alpha", "beta"], SchedulePollingInterval = TimeSpan.FromMilliseconds(100) },
            _storage);
        using CancellationTokenSource stop = new();

        Task[] creators = [.. Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                JobChain.StartWith(() => First(), queue: "alpha").Then(() => Slow("slow"), queue: "alpha").Enqueue(_client);
            }
        }))];

        Stopwatch load = Stopwatch.StartNew();
        List<TimeSpan> waits = [];

        for (int i = 0; i < enqueuedAfter.Length; i++)
        {
            string name = $"other-{i}";
            SpinWait.SpinUntil(() => load.Elapsed >= enqueuedAfter[i]);

            Stopwatch waited = Stopwatch.StartNew();
            _client.Create(() => Mark(name), new EnqueuedState(queue));
            SpinWait.SpinUntil(() => Runs.ContainsKey(name), TimeSpan.FromSeconds(14));
            waits.Add(waited.Elapsed);
        }

        stop.Cancel();
        Task.WaitAll(creators);
        return [.. waits];
    }

    private BackgroundJobServer CreateServer(int workers)
    {
        return new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = workers, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) }, _storage);
    }

    private string? StateOf(string jobId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetStateData(jobId)?.Name;
    }

    /// <summary>A clock that stands still.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>An election filter that runs before the chain's gate, and does nothing.</summary>
    private sealed class SeesEveryElection : IElectStateFilter
    {
        public void OnStateElection(ElectStateContext context)
        {
        }
    }

    /// <summary>Makes the creation of a step take a while, or stop the client for good.</summary>
    private sealed class SlowCreation : IClientFilter
    {
        public TimeSpan Delay { get; set; }

        public string? CrashOn { get; set; }

        public ConcurrentQueue<string> Created { get; } = new();

        public void OnCreating(CreatingContext context)
        {
            if (context.Job.Type != typeof(JobChainFirstStepFacts))
            {
                return;
            }

            if (context.Job.Method.Name == CrashOn)
            {
                throw new OutOfMemoryException("The process stops here.");
            }

            if (context.Job.Method.Name == nameof(Slow))
            {
                Thread.Sleep(Delay);
            }
        }

        public void OnCreated(CreatedContext context)
        {
            if (context.Job.Type == typeof(JobChainFirstStepFacts) && context.Job.Method.Name == nameof(Mark) && context.BackgroundJob != null)
            {
                Created.Enqueue(context.BackgroundJob.Id);
            }
        }
    }
}
