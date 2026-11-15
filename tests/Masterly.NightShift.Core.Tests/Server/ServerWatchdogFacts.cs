using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class ServerWatchdogFacts
{
    private readonly Mock<IStorageConnection> _connection;
    private readonly BackgroundProcessContextMock _context;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _serverTimeout;

    public ServerWatchdogFacts()
    {
        _checkInterval = Timeout.InfiniteTimeSpan;
        _serverTimeout = TimeSpan.FromSeconds(5);

        _context = new BackgroundProcessContextMock();
        _context.StoppingTokenSource.Cancel();

        _connection = new Mock<IStorageConnection>();
        _context.Storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
    }

    [Fact]
    public void Execute_DelegatesRemovalToStorageConnection()
    {
        _connection.Setup(x => x.RemoveTimedOutServers(It.IsAny<TimeSpan>())).Returns(1);
        ServerWatchdog watchdog = new(_checkInterval, _serverTimeout);

        Assert.Throws<OperationCanceledException>(() => watchdog.Execute(_context.Object));

        _connection.Verify(x => x.RemoveTimedOutServers(_serverTimeout));
        _connection.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void Execute_PrunesTheBusyWorkerReports_OlderThanTheServerTimeout()
    {
        DateTime now = JobClock.UtcNow;
        Mock<IWriteOnlyTransaction> transaction = new();
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        _connection.Setup(x => x.GetAllEntriesFromHash(ServerBusyWorkers.HashKey)).Returns(new Dictionary<string, string>
        {
            { "alive", ServerBusyWorkers.FormatValue(1, now, TimeSpan.FromSeconds(1)) },
            { "gone", ServerBusyWorkers.FormatValue(3, now.AddSeconds(-30), TimeSpan.FromSeconds(1)) },
        });
        ServerWatchdog watchdog = new(_checkInterval, _serverTimeout);

        Assert.Throws<OperationCanceledException>(() => watchdog.Execute(_context.Object));

        transaction.Verify(x => x.RemoveHash(ServerBusyWorkers.HashKey));
        transaction.Verify(x => x.SetRangeInHash(ServerBusyWorkers.HashKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Select(static p => p.Key).SequenceEqual(new[] { "alive" }))));
        transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void Execute_KeepsWatching_WhenTheReportsCanNotBeRead()
    {
        _connection.Setup(x => x.RemoveTimedOutServers(It.IsAny<TimeSpan>())).Returns(1);
        _connection.Setup(x => x.GetAllEntriesFromHash(ServerBusyWorkers.HashKey)).Throws(new NotSupportedException());
        ServerWatchdog watchdog = new(_checkInterval, _serverTimeout);

        Assert.Throws<OperationCanceledException>(() => watchdog.Execute(_context.Object));

        _connection.Verify(x => x.RemoveTimedOutServers(_serverTimeout));
        _connection.Verify(x => x.Dispose(), Times.Once);
    }
}
