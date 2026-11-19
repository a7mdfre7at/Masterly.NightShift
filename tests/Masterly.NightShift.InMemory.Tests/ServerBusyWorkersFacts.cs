using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// Each server reports its own busy workers after its heartbeats, in its field of the hash <c>servers:busy</c>
/// (<c>{busy};{taken at};{heartbeat interval}</c>), which the dashboard's Servers page reads; a server that stops
/// removes its field.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class ServerBusyWorkersFacts : IDisposable
{
    private const string Hash = "servers:busy";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ManualResetEventSlim Release = new();
    private static int _started;

    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobClient _client;

    public ServerBusyWorkersFacts()
    {
        _client = new BackgroundJobClient(_storage);
        Release.Reset();
        ResetStarted();
    }

    public void Dispose()
    {
        Release.Set();
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Hold()
    {
        Interlocked.Increment(ref _started);
        Release.Wait(Timeout);
    }

    [Fact]
    public void TwoServers_EachReportTheirOwnBusyWorkers_AndRemoveTheirReportWhenTheyStop()
    {
        _client.Create(() => Hold(), new EnqueuedState("alpha"));
        _client.Create(() => Hold(), new EnqueuedState("alpha"));
        _client.Create(() => Hold(), new EnqueuedState("beta"));

        using (new BackgroundJobServer(Options("alpha", workers: 3), _storage))
        using (new BackgroundJobServer(Options("beta", workers: 2), _storage))
        {
            WaitFor(() => Volatile.Read(ref _started) == 3);
            string alphaId = ServerOf("alpha");
            string betaId = ServerOf("beta");

            WaitFor(() => Busy(alphaId) == 2 && Busy(betaId) == 1);
            Dictionary<string, string> reports = _storage.GetConnection().GetAllEntriesFromHash(Hash)!; // Both reported above
            Assert.Equal(new[] { alphaId, betaId }.Order(), reports.Keys.Order());

            Release.Set();
            WaitFor(() => Busy(alphaId) == 0 && Busy(betaId) == 0);
        }

        Dictionary<string, string>? left = _storage.GetConnection().GetAllEntriesFromHash(Hash);
        Assert.True(left == null || left.Count == 0, "the stopped servers left their reports");
    }

    // _started is static because the job is a static method: written by the job and here only
    private static void ResetStarted() => _started = 0;

    private static BackgroundJobServerOptions Options(string queue, int workers) => new()
    {
        Queues = [queue],
        WorkerCount = workers,
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        SchedulePollingInterval = TimeSpan.FromMilliseconds(100),
    };

    private string ServerOf(string queue)
    {
        string? id = null;
        WaitFor(() => (id = _storage.GetMonitoringApi().Servers().SingleOrDefault(x => x.Queues.Contains(queue))?.Name) != null);
        return id!; // Set by the wait above
    }

    // The busy workers a server reported, or -1 without a report
    private int Busy(string serverId)
    {
        Dictionary<string, string>? reports = _storage.GetConnection().GetAllEntriesFromHash(Hash);
        return reports != null && reports.TryGetValue(serverId, out string? value)
            ? int.Parse(value.Split(';')[0], CultureInfo.InvariantCulture)
            : -1;
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
