using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public sealed class LongRunningAttributeFacts : IDisposable
{
    private readonly RecordingLogProvider _log = new();
    private readonly IDisposable _logScope;

    public LongRunningAttributeFacts()
    {
        LongRunningAttribute.ResetLocalWorkers();
        _logScope = _log.Use();
    }

    public void Dispose()
    {
        _logScope.Dispose();
        LongRunningAttribute.ResetLocalWorkers();
    }

    [Fact, GlobalLock(Reason = "Changes the log provider and the workers noted")]
    public void OnStateElection_WarnsOnce_WhenThisProcessRunsWorkers_ButNoneServesTheQueue()
    {
        _ = new Worker("default");

        Route();
        Route();

        RecordingLogProvider.LogEntry warning = Assert.Single(_log.Entries, x => x.Message.Contains("[LongRunning]", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Contains("LongRunningWorkerCount", warning.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Changes the log provider and the workers noted")]
    public void OnStateElection_DoesNotWarn_WhenAWorkerOfThisProcessServesTheQueue()
    {
        _ = new Worker("default");
        _ = new Worker(LongRunningAttribute.Queue);

        Route();

        Assert.DoesNotContain(_log.Entries, x => x.Message.Contains("[LongRunning]", StringComparison.Ordinal));
    }

    [Fact, GlobalLock(Reason = "Changes the log provider and the workers noted")]
    public void OnStateElection_DoesNotWarn_InAProcessWithoutWorkers()
    {
        // A process that only creates jobs can't know about the servers of other processes
        Route();

        Assert.DoesNotContain(_log.Entries, x => x.Message.Contains("[LongRunning]", StringComparison.Ordinal));
    }

    private static void Route()
    {
        ElectStateContextMock context = new();
        EnqueuedState enqueued = new();
        context.ApplyContext.NewStateObject = enqueued;

        new LongRunningAttribute().OnStateElection(context.Object);

        Assert.Equal(LongRunningAttribute.Queue, enqueued.Queue);
    }
}
