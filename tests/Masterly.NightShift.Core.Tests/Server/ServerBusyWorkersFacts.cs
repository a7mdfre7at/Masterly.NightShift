using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class ServerBusyWorkersFacts
{
    private static readonly DateTime Now = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Mock<IDisposable> _lock = new();

    public ServerBusyWorkersFacts()
    {
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(_lock.Object);
    }

    [Fact]
    public void Counter_RisesAndFalls_AndIsNeverNegative()
    {
        BusyWorkerCounter counter = new();

        counter.Enter();
        counter.Enter();
        int two = counter.Current;
        counter.Leave();
        counter.Leave();
        counter.Leave();

        Assert.Equal((2, 0), (two, counter.Current));
    }

    [Fact]
    public void TheCountOfAServer_IsSharedByAllItsPools_ThroughTheProcessContexts()
    {
        // The normal and the long-running pools (threads of their own), and an async process, from one server
        BusyWorkerCounter counter = new();
        using CancellationTokenSource stop = new();
        BackgroundServerContext server = new("server", new Mock<JobStorage>().Object, new Dictionary<string, object>(), stop.Token, stop.Token, stop.Token)
        {
            BusyWorkers = counter
        };
        CapturingProcess normal = new();
        CapturingProcess longRunning = new();
        CapturingAsyncProcess asynchronous = new();

        using (normal.UseBackgroundPool(2).Create(server, new BackgroundProcessingServerOptions()))
        using (longRunning.UseBackgroundPool(1).Create(server, new BackgroundProcessingServerOptions()))
        using (asynchronous.UseBackgroundPool(1).Create(server, new BackgroundProcessingServerOptions()))
        {
            Assert.True(normal.Ran.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(longRunning.Ran.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(asynchronous.Ran.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            stop.Cancel();
        }

        Assert.Same(counter, normal.Counter);
        Assert.Same(counter, longRunning.Counter);
        Assert.Same(counter, asynchronous.Counter);
    }

    [Fact]
    public void FormatValue_IsReadBack_ByTryParse()
    {
        string value = ServerBusyWorkers.FormatValue(7, Now, TimeSpan.FromSeconds(30));

        Assert.True(ServerBusyWorkers.TryParse(value, out int busy, out DateTime takenAt, out TimeSpan interval));
        Assert.Equal((7, Now, TimeSpan.FromSeconds(30)), (busy, takenAt, interval));
        Assert.Equal(DateTimeKind.Utc, takenAt.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("3;x;30000")]
    [InlineData("-1;1;30000")]
    public void TryParse_RefusesWhatIsNotAReport(string value)
    {
        Assert.False(ServerBusyWorkers.TryParse(value, out int busy, out _, out _));
        Assert.Equal(0, busy);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-59, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(-61, false)]
    public void IsFresh_AllowsTwoHeartbeatIntervals_AroundTheServersLastHeartbeat(int secondsAfterReport, bool fresh)
    {
        Assert.Equal(fresh, ServerBusyWorkers.IsFresh(Now, TimeSpan.FromSeconds(30), Now.AddSeconds(secondsAfterReport)));
    }

    [Fact]
    public void IsFresh_IsFalse_WithoutAHeartbeat()
    {
        Assert.False(ServerBusyWorkers.IsFresh(Now, TimeSpan.FromSeconds(30), null));
    }

    [Fact]
    public void GetField_KeepsAShortServerId_AndShortensALongOne_ToTheFieldLimit()
    {
        string longId = new string('s', 150) + ":42:" + Guid.NewGuid();

        Assert.Equal("web-1:42:abc", ServerBusyWorkers.GetField("web-1:42:abc"));
        Assert.Equal(ServerBusyWorkers.MaxFieldLength, ServerBusyWorkers.GetField(longId).Length);
        Assert.Equal(ServerBusyWorkers.GetField(longId), ServerBusyWorkers.GetField(longId));
        Assert.NotEqual(ServerBusyWorkers.GetField(longId), ServerBusyWorkers.GetField(longId + "x"));
    }

    [Fact]
    public void Write_SetsTheServersOwnFieldOnly()
    {
        ServerBusyWorkers.Write(_connection.Object, "web-1", "2;1;30000");

        _connection.Verify(x => x.SetRangeInHash("servers:busy", It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Single().Key == "web-1" && pairs.Single().Value == "2;1;30000")));
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void RemoveServer_WritesTheHashAgainWithoutTheServersField_UnderTheLock()
    {
        SetupHash(new Dictionary<string, string> { { "a", Report(1, Now) }, { "b", Report(2, Now) } });

        ServerBusyWorkers.RemoveServer(_connection.Object, "a");

        _connection.Verify(x => x.AcquireDistributedLock("lock:servers:busy", It.IsAny<TimeSpan>()));
        _transaction.Verify(x => x.RemoveHash("servers:busy"));
        _transaction.Verify(x => x.SetRangeInHash("servers:busy", It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Select(static p => p.Key).SequenceEqual(new[] { "b" }))));
        _transaction.Verify(x => x.Commit());
        _lock.Verify(x => x.Dispose());
    }

    [Fact]
    public void RemoveServer_RemovesTheWholeHash_WhenTheServerWasTheLastOne()
    {
        SetupHash(new Dictionary<string, string> { { "a", Report(1, Now) } });

        ServerBusyWorkers.RemoveServer(_connection.Object, "a");

        _transaction.Verify(x => x.RemoveHash("servers:busy"));
        _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void RemoveServer_WritesNothing_WhenTheServerHasNoField()
    {
        SetupHash(new Dictionary<string, string> { { "b", Report(2, Now) } });

        ServerBusyWorkers.RemoveServer(_connection.Object, "a");

        _connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void RemoveStale_RemovesTheReportsOlderThanTheServerTimeout_AndTheOnesThatAreNotReports()
    {
        SetupHash(new Dictionary<string, string>
        {
            { "fresh", Report(1, Now.AddMinutes(-1)) },
            { "gone", Report(2, Now.AddMinutes(-6)) },
            { "garbage", "x" },
        });

        ServerBusyWorkers.RemoveStale(_connection.Object, Now, TimeSpan.FromMinutes(5));

        _transaction.Verify(x => x.SetRangeInHash("servers:busy", It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Select(static p => p.Key).SequenceEqual(new[] { "fresh" }))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void RemoveStale_WritesNothing_WhenEveryReportIsFresh_OrThereIsNoHash()
    {
        SetupHash(new Dictionary<string, string> { { "fresh", Report(1, Now) } });
        ServerBusyWorkers.RemoveStale(_connection.Object, Now, TimeSpan.FromMinutes(5));

        SetupHash(null);
        ServerBusyWorkers.RemoveStale(_connection.Object, Now, TimeSpan.FromMinutes(5));

        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void RemoveStale_GivesUp_WhenAnotherServerHoldsTheLock()
    {
        SetupHash(new Dictionary<string, string> { { "gone", Report(2, Now.AddHours(-1)) } });
        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new DistributedLockTimeoutException("lock:servers:busy"));

        ServerBusyWorkers.RemoveStale(_connection.Object, Now, TimeSpan.FromMinutes(5));

        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    private void SetupHash(Dictionary<string, string> hash)
    {
        _connection.Setup(x => x.GetAllEntriesFromHash("servers:busy")).Returns(hash);
    }

    private static string Report(int busy, DateTime takenAt) => ServerBusyWorkers.FormatValue(busy, takenAt, TimeSpan.FromSeconds(30));

    private sealed class CapturingProcess : IBackgroundProcess
    {
        public ManualResetEventSlim Ran { get; } = new();
        public BusyWorkerCounter Counter { get; private set; }

        public void Execute(BackgroundProcessContext context)
        {
            Counter = context.BusyWorkers;
            Ran.Set();
            context.StoppingToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class CapturingAsyncProcess : IBackgroundProcessAsync
    {
        public ManualResetEventSlim Ran { get; } = new();
        public BusyWorkerCounter Counter { get; private set; }

        public async Task ExecuteAsync(BackgroundProcessContext context)
        {
            Counter = context.BusyWorkers;
            Ran.Set();
            await Task.Delay(TimeSpan.FromSeconds(10), context.StoppingToken).ContinueWith(static _ => { }, TaskScheduler.Default);
        }
    }
}
