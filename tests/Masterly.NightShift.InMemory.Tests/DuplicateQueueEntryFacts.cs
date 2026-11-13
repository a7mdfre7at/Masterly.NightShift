using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// A job with more than one entry in its queues runs once. An entry stays in its queue when the job leaves the
/// Enqueued state by hand, and every requeue adds one; whichever a worker takes first runs the job, and a worker
/// that takes another while the job is Processing leaves it to that run.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class DuplicateQueueEntryFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<string> Started = new();
    private static int _running;
    private static int _mostRunning;

    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobClient _client;

    public DuplicateQueueEntryFacts()
    {
        _client = new BackgroundJobClient(_storage);
        Started.Clear();
        _running = 0;
        _mostRunning = 0;
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Slow(string name)
    {
        Started.Enqueue(name);
        int running = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _mostRunning)) < running && Interlocked.CompareExchange(ref _mostRunning, running, seen) != seen)
        {
        }

        Thread.Sleep(TimeSpan.FromSeconds(1));
        Interlocked.Decrement(ref _running);
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Quick(string name) => Started.Enqueue(name);

    // The dashboard's Delete on an enqueued job, then Requeue on the Deleted page, before a worker took the job
    [Fact]
    public void Job_DeletedWhileEnqueued_AndRequeued_RunsOnce()
    {
        string id = _client.Enqueue(() => Slow("x"))!;
        Assert.True(_client.ChangeState(id, new DeletedState(), EnqueuedState.StateName));
        Assert.True(_client.ChangeState(id, new EnqueuedState(), DeletedState.StateName));

        RunUntilEveryEntryIsTaken(id, workers: 2);

        Assert.Equal((1, 1), (Started.Count, Volatile.Read(ref _mostRunning)));
    }

    // Scheduled by hand while enqueued, then enqueued by the scheduler when due: two entries
    [Fact]
    public void Job_ScheduledWhileEnqueued_AndEnqueuedAgain_RunsOnce()
    {
        string id = _client.Enqueue(() => Slow("x"))!;
        Assert.True(_client.ChangeState(id, new ScheduledState(TimeSpan.FromHours(1)), EnqueuedState.StateName));
        Assert.True(_client.ChangeState(id, new EnqueuedState(), ScheduledState.StateName));

        RunUntilEveryEntryIsTaken(id, workers: 2);

        Assert.Equal((1, 1), (Started.Count, Volatile.Read(ref _mostRunning)));
    }

    // The dashboard's Requeue on the page of a job that is Enqueued, as the API's Requeue: no expected state
    [Fact]
    public void Job_RequeuedWhileEnqueued_RunsOnce()
    {
        string id = _client.Enqueue(() => Slow("y"))!;
        Assert.True(_client.Requeue(id));

        RunUntilEveryEntryIsTaken(id, workers: 2);

        Assert.Equal((1, 1), (Started.Count, Volatile.Read(ref _mostRunning)));
    }

    // A step of a chain that waits in its queue is deleted by hand, and the chain is retried from it
    [Fact]
    public void ChainStep_DeletedWhileEnqueued_ThenRetried_RunsOnce()
    {
        JobChainResult chain = JobChain.StartWith(() => Quick("a")).Then(() => Slow("b")).Enqueue(_client)!;
        Assert.True(_client.ChangeState(chain.RootId, new SucceededState(null, 0, 0), null));
        string step = chain.Steps["1.0"];
        Assert.Equal(EnqueuedState.StateName, _storage.GetConnection().GetStateData(step)?.Name);

        Assert.True(_client.ChangeState(step, new DeletedState(), null));
        _storage.RetryChainFrom(step);

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 3, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) }, _storage))
        {
            WaitFor(() => _storage.GetConnection().GetStateData(step)?.Name == SucceededState.StateName);
            WaitFor(() => _storage.GetMonitoringApi() is { } monitoring
                && monitoring.EnqueuedCount(EnqueuedState.DefaultQueue) == 0
                && monitoring.FetchedCount(EnqueuedState.DefaultQueue) == 0);
        }

        Assert.Equal((1, 1), (Started.Count(static x => x == "b"), Volatile.Read(ref _mostRunning)));
    }

    /// <summary>Runs a server until the job succeeded and every entry of its queue was taken by a worker.</summary>
    private void RunUntilEveryEntryIsTaken(string id, int workers)
    {
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = workers }, _storage);
        WaitFor(() => _storage.GetConnection().GetStateData(id)?.Name == SucceededState.StateName);
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
}
