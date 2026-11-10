namespace NightShiftBenchmarks;

/// <summary>What the throughput run needs from each library.</summary>
internal interface ILibrary
{
    string Name { get; }

    string Driver { get; }

    void OpenDatabase();

    void Reset();

    string? Enqueue();

    string? EnqueueCounted();

    /// <summary>Enqueues the given number of counted jobs, in the fastest way the library offers.</summary>
    void EnqueueAllCounted(int count);

    /// <summary>Enqueues the given number of counted jobs one by one, from several threads at once.</summary>
    void EnqueueCountedInParallel(int count, int threads);

    IDisposable StartServer(int workerCount);

    long SucceededCount();
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
