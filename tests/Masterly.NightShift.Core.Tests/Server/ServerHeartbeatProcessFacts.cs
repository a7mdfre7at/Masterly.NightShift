using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class ServerHeartbeatProcessFacts
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1);
    private static readonly DateTime StorageNow = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly BackgroundProcessContextMock _context = new() { ServerId = "web-1:42:abc" };
    private readonly Mock<JobStorageConnection> _connection = new();
    private int _restarts;

    public ServerHeartbeatProcessFacts()
    {
        _context.Storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _context.BusyWorkers = new BusyWorkerCounter();
    }

    [Fact]
    public void Execute_ReportsTheBusyWorkers_InTheServersOwnField_ByTheStoragesClock_AfterTheHeartbeat()
    {
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);
        _connection.Setup(x => x.GetUtcDateTime()).Returns(StorageNow);
        List<string> calls = [];
        _connection.Setup(x => x.Heartbeat(It.IsAny<string>())).Callback(() => calls.Add("heartbeat"));
        _connection.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback(() => calls.Add("report"));
        _context.BusyWorkers.Enter();
        _context.BusyWorkers.Enter();

        CreateProcess().Execute(_context.Object);

        Assert.Equal(["heartbeat", "report"], calls);
        _connection.Verify(x => x.SetRangeInHash("servers:busy", It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs =>
            pairs.Single().Key == "web-1:42:abc" &&
            pairs.Single().Value == ServerBusyWorkers.FormatValue(2, StorageNow, Interval))));
    }

    [Fact]
    public void Execute_ReportsTheTime_OfTheProcessClock_WhenTheStorageHasNoClock()
    {
        string value = null;
        _connection.Setup(x => x.SetRangeInHash("servers:busy", It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => value = pairs.Single().Value);
        DateTime before = JobClock.UtcNow;

        CreateProcess().Execute(_context.Object);

        Assert.True(ServerBusyWorkers.TryParse(value, out int busy, out DateTime takenAt, out _));
        Assert.Equal(0, busy);
        Assert.InRange(takenAt, before.AddMilliseconds(-1), JobClock.UtcNow.AddMilliseconds(1));
        _connection.Verify(x => x.GetUtcDateTime(), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Uses the log provider")]
    public void Execute_KeepsTheHeartbeatsGoing_WhenTheReportFails_AndWarnsOnce()
    {
        RecordingLogProvider recording = new();
        _connection.Setup(x => x.SetRangeInHash("servers:busy", It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Throws(new InvalidOperationException("no hashes here"));
        ServerHeartbeatProcess process = CreateProcess();

        using (recording.Use())
        {
            for (int i = 0; i < 3; i++)
            {
                process.Execute(_context.Object);
            }
        }

        _connection.Verify(x => x.Heartbeat("web-1:42:abc"), Times.Exactly(3));
        Assert.Equal(0, _restarts);
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains("could not report its busy workers", warning.Message, StringComparison.Ordinal);
        Assert.Equal(2, recording.Entries.Count(static x => x.Level == LogLevel.Debug && x.Exception is InvalidOperationException));
        // The heartbeat never counted as faulted: each one was a plain success
        Assert.Equal(3, recording.Entries.Count(static x => x.Message.EndsWith("heartbeat successfully sent", StringComparison.Ordinal)));
        Assert.DoesNotContain(recording.Entries, static x => x.Message.Contains("is now able to continue", StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_DoesNotReport_WhenTheHeartbeatFails_AndStillRestartsAfterTheTimeout()
    {
        _connection.Setup(x => x.Heartbeat(It.IsAny<string>())).Throws(new InvalidOperationException("down"));

        CreateProcess(serverTimeout: TimeSpan.Zero).Execute(_context.Object);

        _connection.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public void Execute_DoesNotReport_WhenTheServerIsGone()
    {
        _connection.Setup(x => x.Heartbeat(It.IsAny<string>())).Throws(new BackgroundServerGoneException());

        CreateProcess().Execute(_context.Object);

        _connection.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public void Execute_DoesNotReport_WithoutACount()
    {
        _context.BusyWorkers = null;

        CreateProcess().Execute(_context.Object);

        _connection.Verify(x => x.Heartbeat("web-1:42:abc"));
        _connection.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
    }

    private ServerHeartbeatProcess CreateProcess(TimeSpan? serverTimeout = null)
    {
        return new ServerHeartbeatProcess(Interval, serverTimeout ?? TimeSpan.FromMinutes(5), () => _restarts++);
    }
}
