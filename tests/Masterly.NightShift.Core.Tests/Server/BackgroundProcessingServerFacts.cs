using Masterly.NightShift.Server;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Server;

public class BackgroundProcessingServerFacts
{
    private readonly string[] _queues = ["queue"];
    private readonly Mock<JobStorage> _storage;
    private readonly List<IBackgroundProcess> _processes;
    private readonly Dictionary<string, object> _properties;
    private readonly Mock<IStorageConnection> _connection;

    public BackgroundProcessingServerFacts()
    {
        _storage = new Mock<JobStorage>();
        _processes = [];
        _properties = new Dictionary<string, object> { { "Queues", _queues } };

        _connection = new Mock<IStorageConnection>();
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessingServer(null, _processes, _properties));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenProcessesArgumentIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessingServer(_storage.Object, null, _properties));

        Assert.Equal("processes", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenPropertiesArgumentIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessingServer(_storage.Object, _processes, null));

        Assert.Equal("properties", exception.ParamName);
    }

    [Fact]
    public void Ctor_AnnouncesTheServer_AndRemovesIt()
    {
        using (CreateServer()) { Thread.Sleep(50); }

        _connection.Verify(x => x.AnnounceServer(
            It.IsNotNull<string>(),
            It.Is<ServerContext>(y => y.Queues == _queues)));

        _connection.Verify(x => x.RemoveServer(It.IsNotNull<string>()));
    }

    [Fact]
    public void Execute_StartsAllTheProcesses_InLoop_AndWaitsForThem()
    {
        // Arrange
        CountdownEvent component1Countdown = new(5);
        CountdownEvent component2Countdown = new(5);

        Mock<IBackgroundProcess> component1 = CreateProcessMock<IBackgroundProcess>();
        component1.Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>())).Callback(() =>
        {
            component1Countdown.Signal();
        });

        Mock<IBackgroundProcess> component2 = CreateProcessMock<IBackgroundProcess>();
        component2.Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>())).Callback(() =>
        {
            component2Countdown.Signal();
        });

        // Act
        using (CreateServer())
        {
            WaitHandle.WaitAll([component1Countdown.WaitHandle, component2Countdown.WaitHandle]);
        }

        // Assert
        component1.Verify(x => x.Execute(It.IsAny<BackgroundProcessContext>()), Times.AtLeast(5));
        component2.Verify(x => x.Execute(It.IsNotNull<BackgroundProcessContext>()), Times.AtLeast(5));
    }

    [Fact]
    public async Task Dispose_AfterAWaitThatWasCanceled_StillGivesTheProcessesTheShutdownSignal()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim finished = new();
        bool sawShutdown = false;
        Mock<IBackgroundProcess> process = CreateProcessMock<IBackgroundProcess>();
        process.Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>())).Callback<BackgroundProcessContext>(context =>
        {
            if (finished.IsSet)
            {
                context.Wait(TimeSpan.FromMilliseconds(50));
                return;
            }

            entered.Set();
            sawShutdown = context.ShutdownToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(8));
            finished.Set();
        });

        BackgroundProcessingServer server = new(_storage.Object, _processes, _properties, new BackgroundProcessingServerOptions
        {
            StopTimeout = TimeSpan.FromSeconds(1),
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        server.SendStop();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.WaitForShutdownAsync(cts.Token));
        server.Dispose();

        Assert.True(finished.IsSet);
        Assert.True(sawShutdown);
    }

    [Fact]
    public void Execute_StopsTheDispatchersItStarted_WhenAnotherFailsToStart()
    {
        int executions = 0;
        Mock<IBackgroundProcess> process = new();
        process.Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>())).Callback<BackgroundProcessContext>(context =>
        {
            Interlocked.Increment(ref executions);
            context.Wait(TimeSpan.FromMilliseconds(20));
        });
        Mock<IBackgroundProcessDispatcherBuilder> failing = new();
        failing.Setup(x => x.Create(It.IsAny<BackgroundServerContext>(), It.IsAny<BackgroundProcessingServerOptions>()))
            .Throws(new InvalidOperationException("can't start"));

        // Every start fails, and the server tries again after RestartDelay
        using (new BackgroundProcessingServer(
                   _storage.Object,
                   [process.Object.UseBackgroundPool(threadCount: 1), failing.Object],
                   _properties,
                   new BackgroundProcessingServerOptions { RestartDelay = TimeSpan.FromMilliseconds(50) }))
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref executions) > 0, TimeSpan.FromSeconds(10)));
            Thread.Sleep(500);
        }

        Thread.Sleep(200);
        int afterDispose = Volatile.Read(ref executions);
        Thread.Sleep(300);

        // Nothing started by a failed start goes on running
        Assert.Equal(afterDispose, Volatile.Read(ref executions));
    }

    private BackgroundProcessingServer CreateServer()
    {
        return new BackgroundProcessingServer(_storage.Object, _processes, _properties);
    }

    private Mock<T> CreateProcessMock<T>()
        where T : class, IBackgroundProcess
    {
        Mock<T> mock = new();
        _processes.Add(mock.Object);

        return mock;
    }
}
