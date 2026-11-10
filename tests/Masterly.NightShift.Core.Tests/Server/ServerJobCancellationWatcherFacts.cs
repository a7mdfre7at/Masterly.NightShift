using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class ServerJobCancellationWatcherFacts : IDisposable
{
    private readonly Mock<IStorageConnection> _connection;
    private readonly BackgroundProcessContextMock _context;
    private readonly TimeSpan _checkInterval;

    public ServerJobCancellationWatcherFacts()
    {
        _checkInterval = Timeout.InfiniteTimeSpan;

        _context = new BackgroundProcessContextMock();
        _context.StoppingTokenSource.Cancel();

        _connection = new Mock<IStorageConnection>();
        _context.Storage.Setup(x => x.GetConnection()).Returns(_connection.Object);

        ServerJobCancellationToken.AddServer(_context.ServerId);
    }

    public void Dispose()
    {
        ServerJobCancellationToken.RemoveServer(_context.ServerId);
    }

    [Fact]
    public void Execute_DelegatesCancellationToServerJobCancellationToken()
    {
        ServerJobCancellationToken token = new(_connection.Object, "job-id", _context.ServerId, "1", _context.StoppedTokenSource.Token);
        Assert.False(token.ShutdownToken.IsCancellationRequested);

        _connection.Setup(x => x.GetStateData(It.IsAny<string>())).Returns((StateData)null);
        ServerJobCancellationWatcher watchdog = new(_checkInterval);

        Assert.Throws<OperationCanceledException>(() => watchdog.Execute(_context.Object));

        _connection.Verify(x => x.GetStateData("job-id"));
        _connection.Verify(x => x.Dispose(), Times.Once);
        Assert.True(token.IsAborted);
    }
}
