namespace NightShiftBenchmarks;

/// <summary>What the throughput run needs from each library.</summary>
internal interface ILibrary
{
    string Name { get; }

    string Driver { get; }

    void OpenDatabase();

    void Reset();

    /// <summary>Enqueues the given number of counted jobs, in the fastest way the library offers.</summary>
    void EnqueueAllCounted(int count);

    /// <summary>Enqueues the given number of counted jobs one by one, from several threads at once.</summary>
    void EnqueueCountedInParallel(int count, int threads);

    IDisposable StartServer(int workerCount);

    /// <summary>Whether the storage shows all of the given number of jobs as finished.</summary>
    bool HasFinished(int jobCount);
}

/// <summary>Runs an action a number of times, spread over several threads.</summary>
internal static class Callers
{
    public static void Run(int count, int threads, Action action)
    {
        int next = 0;
        Thread[] workers = new Thread[threads];

        for (int i = 0; i < threads; i++)
        {
            workers[i] = new Thread(() =>
            {
                while (Interlocked.Increment(ref next) <= count)
                {
                    action();
                }
            });
            workers[i].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }
    }
}

internal static class Samples
{
    public static Order Order { get; } = new()
    {
        Id = 42,
        Customer = "Contoso",
        Total = 1299.50m,
        Items = ["keyboard", "monitor", "dock"],
    };
}
