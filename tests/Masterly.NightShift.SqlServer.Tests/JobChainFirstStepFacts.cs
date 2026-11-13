using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// What the other jobs of a server pay for the first steps of chains that are still being created, on a real
/// database: clients that take their time to create chains, back to back, and a job in another queue.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class JobChainFirstStepFacts
{
    private static readonly ConcurrentDictionary<string, int> Runs = new();

    public static void Mark(string name) => Runs.AddOrUpdate(name, 1, static (_, count) => count + 1);

    public static void First()
    {
    }

    /// <summary>A step whose creation takes a while (see <see cref="SlowCreation"/>).</summary>
    public static void Slow(string name) => _ = name;

    [Theory, CleanDatabase]
    [InlineData(false, 2, 1000, true)]
    [InlineData(true, 2, 1000, true)]
    [InlineData(false, 1, 100, true)]
    [InlineData(true, 1, 100, true)]
    [InlineData(false, 2, 100, true)]
    [InlineData(false, 2, 100, false)]
    [InlineData(false, 1, 100, false)]
    public async Task Job_InAnotherQueueOfTheServer_RunsWithinSeconds_WhileAsManyClientsAsWorkersCreateChainsBackToBack(
        bool holdAsBefore,
        int workers,
        int creationMs,
        bool slidingInvisibilityTimeout)
    {
        // With as many chains in creation as the server has workers, each worker finds the first step of a chain
        // that isn't recorded yet, whenever it looks. Waiting for each of them, the workers would never get to
        // the other queue, for as long as chains keep being created. Both ways a worker holds a fetched job.
        TimeSpan[] waits = await WaitsOfJobs(holdAsBefore, workers, creationMs, slidingInvisibilityTimeout, TimeSpan.FromSeconds(0.5));

        // The workers of a pool wait for two seconds in ten at most, all together
        Assert.InRange(waits.Single(), TimeSpan.Zero, TimeSpan.FromSeconds(4));
    }

    [Fact, CleanDatabase]
    public async Task Jobs_InAnotherQueueOfTheServer_RunWithinSeconds_EachTime_HoweverLongChainsKeepBeingCreated()
    {
        // Not only the first time: also when the workers may wait again, after ten seconds, and in between
        TimeSpan[] waits = await WaitsOfJobs(false, 1, 100, true, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(11.5), TimeSpan.FromSeconds(13));

        Assert.All(waits, static waited => Assert.InRange(waited, TimeSpan.Zero, TimeSpan.FromSeconds(4)));
    }

    // How long jobs enqueued to another queue at the given times wait to run, while as many clients as the server
    // has workers create chains back to back, each taking a while
    private static async Task<TimeSpan[]> WaitsOfJobs(bool holdAsBefore, int workers, int creationMs, bool slidingInvisibilityTimeout, params TimeSpan[] enqueuedAfter)
    {
        Runs.Clear();
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };
        if (!slidingInvisibilityTimeout)
        {
            options.SlidingInvisibilityTimeout = null;
        }

        SqlServerStorage storage = new(ConnectionUtils.GetConnectionString(), options);
        BackgroundJobClient client = new(storage);
        SlowCreation slow = new(TimeSpan.FromMilliseconds(creationMs));
        SeesEveryElection first = new();
        GlobalJobFilters.Filters.Add(slow);

        if (holdAsBefore)
        {
            // A filter ordered before the chain's gate: the gate holds the step in the Scheduled state, as it
            // did before workers waited
            GlobalJobFilters.Filters.Add(first, -2);
        }

        try
        {
            using BackgroundJobServer server = new(
                new BackgroundJobServerOptions { WorkerCount = workers, Queues = ["alpha", "beta"], SchedulePollingInterval = TimeSpan.FromMilliseconds(200) },
                storage);
            using CancellationTokenSource stop = new();

            Task[] creators = [.. Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    JobChain.StartWith(() => First(), queue: "alpha").Then(() => Slow("slow"), queue: "alpha").Enqueue(client);
                }
            }))];

            Stopwatch load = Stopwatch.StartNew();
            List<TimeSpan> waits = [];

            for (int i = 0; i < enqueuedAfter.Length; i++)
            {
                string name = $"other-{i}";
                SpinWait.SpinUntil(() => load.Elapsed >= enqueuedAfter[i]);

                Stopwatch waited = Stopwatch.StartNew();
                client.Create(() => Mark(name), new EnqueuedState("beta"));
                SpinWait.SpinUntil(() => Runs.ContainsKey(name), TimeSpan.FromSeconds(14));
                waits.Add(waited.Elapsed);
            }

            stop.Cancel();
            await Task.WhenAll(creators);
            TestContext.Current.TestOutputHelper?.WriteLine($"The jobs waited {string.Join(", ", waits.Select(static x => $"{x.TotalSeconds:0.00} s"))}.");

            return [.. waits];
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(slow);
            GlobalJobFilters.Filters.Remove(first);
        }
    }

    /// <summary>An election filter that runs before the chain's gate, and does nothing.</summary>
    private sealed class SeesEveryElection : IElectStateFilter
    {
        public void OnStateElection(ElectStateContext context)
        {
        }
    }

    /// <summary>Makes the creation of a step take a while.</summary>
    private sealed class SlowCreation(TimeSpan delay) : IClientFilter
    {
        public void OnCreating(CreatingContext context)
        {
            if (context.Job.Type == typeof(JobChainFirstStepFacts) && context.Job.Method.Name == nameof(Slow))
            {
                Thread.Sleep(delay);
            }
        }

        public void OnCreated(CreatedContext context)
        {
        }
    }
}
