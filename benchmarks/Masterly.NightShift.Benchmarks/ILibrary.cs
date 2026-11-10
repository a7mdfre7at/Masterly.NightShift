namespace NightShiftBenchmarks;

/// <summary>What the throughput run needs from each library.</summary>
internal interface ILibrary
{
    string Name { get; }

    string Driver { get; }

    void OpenDatabase();

    void Reset();

    string? Enqueue();

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
