using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// A job with more than one row in [JobQueue] runs once, with either way of fetching; and a job whose own row
/// comes back to its queue (put back by its worker) runs again.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class DuplicateQueueEntryFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<string> Started = new();
    private static int _running;
    private static int _mostRunning;

    [AutomaticRetry(Attempts = 0)]
    public static void Slow(string name)
    {
        Started.Enqueue(name);
        int running = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _mostRunning)) < running && Interlocked.CompareExchange(ref _mostRunning, running, seen) != seen)
        {
            // Another run changed the most seen in between: read it again
        }

        Thread.Sleep(TimeSpan.FromSeconds(1));
        Interlocked.Decrement(ref _running);
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Quick(string name) => Started.Enqueue(name);

    /// <summary>The first run lasts until its server stops; the next ones return at once.</summary>
    [AutomaticRetry(Attempts = 0)]
    public static void RunUntilStopped(string name, CancellationToken cancellationToken)
    {
        Started.Enqueue(name);
        if (Started.Count(x => x == name) == 1)
        {
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    // The dashboard's Delete on an enqueued job, then Requeue on the Deleted page, before a worker took the job
    [Theory, CleanDatabase]
    [InlineData(true)]
    [InlineData(false)]
    public void Job_DeletedWhileEnqueued_AndRequeued_RunsOnce(bool slidingInvisibility)
    {
        Reset();
        SqlServerStorage storage = CreateStorage(slidingInvisibility);
        BackgroundJobClient client = new(storage);

        string id = client.Enqueue(() => Slow("x"))!;
        Assert.True(client.ChangeState(id, new DeletedState(), EnqueuedState.StateName));
        Assert.True(client.ChangeState(id, new EnqueuedState(), DeletedState.StateName));
        Assert.Equal(2, storage.GetMonitoringApi().EnqueuedCount(EnqueuedState.DefaultQueue));

        RunUntilEveryRowIsTaken(storage, id, workers: 2);

        Assert.Equal((1, 1), (Started.Count, MostRunning()));
    }

    // The Requeue of the API, twice, on a job that is still Enqueued
    [Theory, CleanDatabase]
    [InlineData(true)]
    [InlineData(false)]
    public void Job_RequeuedTwiceWhileEnqueued_RunsOnce(bool slidingInvisibility)
    {
        Reset();
        SqlServerStorage storage = CreateStorage(slidingInvisibility);
        BackgroundJobClient client = new(storage);

        string id = client.Enqueue(() => Slow("y"))!;
        Assert.True(client.Requeue(id));
        Assert.True(client.Requeue(id));

        RunUntilEveryRowIsTaken(storage, id, workers: 3);

        Assert.Equal((1, 1), (Started.Count, MostRunning()));
    }

    // A step of a chain that waits in its queue is deleted by hand, and the chain is retried from it
    [Fact, CleanDatabase]
    public void ChainStep_DeletedWhileEnqueued_ThenRetried_RunsOnce()
    {
        Reset();
        SqlServerStorage storage = CreateStorage(slidingInvisibility: true);
        BackgroundJobClient client = new(storage);

        JobChainResult chain = JobChain.StartWith(() => Quick("a")).Then(() => Slow("b")).Enqueue(client)!;
        Assert.True(client.ChangeState(chain.RootId, new SucceededState(null, 0, 0), null));
        string step = chain.Steps["1.0"];
        Assert.True(client.ChangeState(step, new DeletedState(), EnqueuedState.StateName));
        storage.RetryChainFrom(step);

        RunUntilEveryRowIsTaken(storage, step, workers: 3);

        Assert.Equal((1, 1), (Started.Count(static x => x == "b"), MostRunning()));
    }

    // The row the worker took comes back when the worker puts the job back (its server stopped): rolled back with
    // the transaction that deleted it, or with its FetchedAt cleared. The run is over, so the job runs again.
    [Theory, CleanDatabase]
    [InlineData(true)]
    [InlineData(false)]
    public void Job_PutBackByItsWorker_RunsAgain_OnTheNextServer(bool slidingInvisibility)
    {
        Reset();
        SqlServerStorage storage = CreateStorage(slidingInvisibility);
        BackgroundJobClient client = new(storage);
        string id = client.Enqueue(() => RunUntilStopped("z", CancellationToken.None))!;

        using (CreateServer(storage, workers: 1))
        {
            WaitFor(() => Started.Count == 1);
        }

        using (IStorageConnection connection = storage.GetConnection())
        {
            Assert.Equal(ProcessingState.StateName, connection.GetStateData(id)?.Name);
        }

        RunUntilEveryRowIsTaken(storage, id, workers: 2);

        Assert.Equal(2, Started.Count);
    }

    /// <summary>The most runs of the job methods seen at the same time.</summary>
    private static int MostRunning() => Volatile.Read(ref _mostRunning);

    private static void Reset()
    {
        Started.Clear();
        _running = 0;
        _mostRunning = 0;
    }

    /// <summary>Runs a server until the job succeeded and every row of its queue was taken by a worker.</summary>
    private static void RunUntilEveryRowIsTaken(SqlServerStorage storage, string id, int workers)
    {
        using BackgroundJobServer server = CreateServer(storage, workers);

        WaitFor(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            return connection.GetStateData(id)?.Name == SucceededState.StateName;
        });
        WaitFor(() => storage.GetMonitoringApi() is { } monitoring
            && monitoring.EnqueuedCount(EnqueuedState.DefaultQueue) == 0
            && monitoring.FetchedCount(EnqueuedState.DefaultQueue) == 0);
    }

    private static BackgroundJobServer CreateServer(SqlServerStorage storage, int workers)
    {
        return new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = workers, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) }, storage);
    }

    private static SqlServerStorage CreateStorage(bool slidingInvisibility)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false, QueuePollInterval = TimeSpan.FromMilliseconds(100) };
        if (!slidingInvisibility)
        {
            options.SlidingInvisibilityTimeout = null;
        }

        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), options);
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
