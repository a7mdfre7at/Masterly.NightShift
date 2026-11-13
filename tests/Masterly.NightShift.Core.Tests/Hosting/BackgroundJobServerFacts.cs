using Masterly.NightShift.Client;
using Masterly.NightShift.Server;
using LogLevel = Masterly.NightShift.Logging.LogLevel;

// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable ObjectCreationAsStatement

namespace Masterly.NightShift.Core.Tests;

public class BackgroundJobServerFacts
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<JobStorage> _jobStorage = new();
    private readonly Mock<IStorageConnection> _storageConnection = new();
    private readonly BackgroundJobServerOptions _serverOptions = new()
    {
        ServerName = "facts-server",
        WorkerCount = 2,
        Queues = ["critical", "default"],
        IsLightweightServer = true,
        ShutdownTimeout = TimeSpan.FromSeconds(15),
    };

    public BackgroundJobServerFacts()
    {
        _jobStorage.Setup(x => x.GetConnection()).Returns(_storageConnection.Object);

        // Behaves like a real storage: waits for a job until the server stops
        _storageConnection
            .Setup(x => x.FetchNextJob(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns<string[], CancellationToken>((_, token) =>
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return null;
            });
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobServer(_serverOptions, (JobStorage)null));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenOptionsAreNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobServer(null, _jobStorage.Object));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenAdditionalProcessesAreNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobServer(_serverOptions, _jobStorage.Object, null));

        Assert.Equal("additionalProcesses", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenOnlySomeCustomServicesAreGiven()
    {
        Mock<IBackgroundJobFactory> factory = new();
        Mock<IBackgroundJobPerformer> performer = new();
        Mock<IBackgroundJobStateChanger> stateChanger = new();

        Assert.Equal("performer", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
            _serverOptions, _jobStorage.Object, [], null, null, factory.Object, null, stateChanger.Object)).ParamName);
        Assert.Equal("factory", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
            _serverOptions, _jobStorage.Object, [], null, null, null, performer.Object, stateChanger.Object)).ParamName);
        Assert.Equal("stateChanger", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
            _serverOptions, _jobStorage.Object, [], null, null, factory.Object, performer.Object, null)).ParamName);
    }

    [Fact]
    public void Ctor_StartsTheServer_ThatAnnouncesItsQueuesAndWorkerCount_AndRemovesItselfOnShutdown()
    {
        ManualResetEventSlim announced = new();
        string serverId = null;
        ServerContext serverContext = null;
        _storageConnection
            .Setup(x => x.AnnounceServer(It.IsAny<string>(), It.IsAny<ServerContext>()))
            .Callback<string, ServerContext>((id, context) =>
            {
                serverId = id;
                serverContext = context;
                announced.Set();
            });

        using (BackgroundJobServer server = new(_serverOptions, _jobStorage.Object, []))
        {
            Assert.True(announced.Wait(WaitTimeout, TestContext.Current.CancellationToken));

            server.SendStop();
            Assert.True(server.WaitForShutdown(WaitTimeout));
        }

        Assert.StartsWith("facts-server:", serverId);
        Assert.Equal(new[] { "critical", "default" }, serverContext.Queues);
        Assert.Equal(2, serverContext.WorkerCount);
        _storageConnection.Verify(x => x.RemoveServer(serverId), Times.Once);
    }

    [Fact, StaticLock]
    public void Ctor_AddsTheJobMonitorProcess_EvenWithoutMonitors_SoMonitorsRegisteredLaterAreChecked()
    {
        JobMonitors.Clear();

        using BackgroundJobServer server = new(_serverOptions, _jobStorage.Object, []);

        Assert.Contains(server.Processes, static x => x.ToString() == nameof(JobMonitorProcess));
        server.SendStop();
        Assert.True(server.WaitForShutdown(WaitTimeout));
    }

    [Fact]
    public void Ctor_AddsTheChainMaintenanceProcess()
    {
        using BackgroundJobServer server = new(_serverOptions, _jobStorage.Object, []);

        Assert.Contains(server.Processes, static x => x.ToString() == nameof(JobChainMaintenanceProcess));
        server.SendStop();
        Assert.True(server.WaitForShutdown(WaitTimeout));
    }

    [Fact]
    public void Ctor_WithCustomServices_RunsTheAdditionalProcesses()
    {
        ManualResetEventSlim executed = new();
        Mock<IBackgroundProcess> process = new();
        process
            .Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>()))
            .Callback<BackgroundProcessContext>(context =>
            {
                executed.Set();
                context.StoppingToken.WaitHandle.WaitOne();
            });

        using (BackgroundJobServer server = new(
            _serverOptions,
            _jobStorage.Object,
            [process.Object],
            null,
            null,
            new Mock<IBackgroundJobFactory>().Object,
            new Mock<IBackgroundJobPerformer>().Object,
            new Mock<IBackgroundJobStateChanger>().Object))
        {
            Assert.True(executed.Wait(WaitTimeout, TestContext.Current.CancellationToken));

            server.SendStop();
            Assert.True(server.WaitForShutdown(WaitTimeout));
        }

        process.Verify(x => x.Execute(It.Is<BackgroundProcessContext>(context => context.ServerId.StartsWith("facts-server:"))));
    }

    [Fact]
    public async Task DisposeAsync_StopsTheServerAndDisposesIt()
    {
        BackgroundJobServer server = new(_serverOptions, _jobStorage.Object);

        await server.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => server.WaitForShutdown(TimeSpan.Zero));
    }

    [Fact]
    public async Task DisposeAsync_DoesNothing_WhenCalledAgain()
    {
        BackgroundJobServer server = new(_serverOptions, _jobStorage.Object);
        await server.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await server.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_DoesNothing_AfterDispose()
    {
        BackgroundJobServer server = new(_serverOptions, _jobStorage.Object);
        server.Dispose();

        Exception? exception = await Record.ExceptionAsync(async () => await server.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact, GlobalLock(Reason = "Sets the deployment of the process, uses the drain mode cache and the log provider")]
    public void Ctor_WithDeploymentFencing_FetchesNoJob_BeforeItsFirstCheckHasPausedTheFencedServer()
    {
        // The cluster runs the newer deployment v2, and a server of v1 starts: its first check will find it fenced
        FencedCluster cluster = new();
        RecordingLogProvider recording = new();
        List<string> workerThreads = [];
        BackgroundJobServerOptions options = FencedServerOptions();
        options.WorkerThreadConfigurationAction = thread => workerThreads.Add(thread.Name);
        DeploymentFencing.DeploymentId = "v1";
        DeploymentFencing.DeploymentOrder = 1;

        try
        {
            bool fetchedBeforeTheCheck;
            using (recording.Use())
            using (BackgroundJobServer server = new(options, cluster.Storage.Object, []))
            {
                // The first check is held at its lock. A worker that doesn't wait for it fetches meanwhile; one that
                // does says so, and then the test knows that the worker had its turn
                Assert.True(cluster.CheckStarted.Wait(WaitTimeout, TestContext.Current.CancellationToken));
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetched.IsSet || WaitsForItsFirstCheck(recording), WaitTimeout));
                fetchedBeforeTheCheck = cluster.Fetched.IsSet;

                cluster.ReleaseCheck.Set();
                Assert.True(cluster.Fetched.Wait(WaitTimeout, TestContext.Current.CancellationToken));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            Assert.False(fetchedBeforeTheCheck, "A worker fetched a job before the first deployment check had decided.");
            Assert.Equal(["pause", "fetch"], cluster.Events);
            KeyValuePair<string, string> paused = Assert.Single(DrainMode.Read(cluster.Connection.Object).PausedServers);
            Assert.StartsWith("fenced-server:", paused.Key, StringComparison.Ordinal);
            Assert.Equal("Fenced: deployment 'v1' (order 1) is older than 'v2' (order 2)", paused.Value);

            // Named as on a server without fencing
            Assert.Equal(["Worker #1"], workerThreads);
        }
        finally
        {
            cluster.ReleaseCheck.Set();
            ResetDeploymentFencing();
        }
    }

    [Fact, GlobalLock(Reason = "Sets the deployment of the process, uses the drain mode cache and the log provider")]
    public void Ctor_WithDeploymentFencing_FetchesNoJob_WhileItsFirstCheckFails_AndStartsOnceACheckSucceeds()
    {
        FencedCluster cluster = new() { ChecksFail = true };
        cluster.ReleaseCheck.Set();
        RecordingLogProvider recording = new();
        DeploymentFencing.DeploymentId = "v1";
        DeploymentFencing.DeploymentOrder = 1;

        try
        {
            bool fetchedBeforeTheCheck;
            using (recording.Use())
            using (BackgroundJobServer server = new(FencedServerOptions(), cluster.Storage.Object, []))
            {
                // The storage is down for the first check, which is tried again about a second later
                Assert.True(cluster.CheckFailed.Wait(WaitTimeout, TestContext.Current.CancellationToken));
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetched.IsSet || WaitsForItsFirstCheck(recording), WaitTimeout));
                fetchedBeforeTheCheck = cluster.Fetched.IsSet;

                cluster.ChecksFail = false;
                Assert.True(cluster.Fetched.Wait(WaitTimeout, TestContext.Current.CancellationToken));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            Assert.False(fetchedBeforeTheCheck, "A worker fetched a job although the first deployment check had failed.");
            Assert.Equal(["pause", "fetch"], cluster.Events);
            Assert.Contains(recording.Entries, static x =>
                x.Level == LogLevel.Warn &&
                x.Message.Contains("takes no jobs until a check succeeds", StringComparison.Ordinal));
        }
        finally
        {
            ResetDeploymentFencing();
        }
    }

    [Fact, GlobalLock(Reason = "Reads the deployment of the process")]
    public void Ctor_WithoutDeploymentFencing_StartsItsWorkersAsBefore_AndNeverReadsTheDeployment()
    {
        FencedCluster cluster = new();
        cluster.ReleaseCheck.Set();
        ResetDeploymentFencing();

        using (BackgroundJobServer server = new(FencedServerOptions(), cluster.Storage.Object, []))
        {
            Assert.True(cluster.Fetched.Wait(WaitTimeout, TestContext.Current.CancellationToken));

            Assert.Contains(server.Processes, static x => x.ToString() == nameof(Worker));
            Assert.DoesNotContain(server.Processes, static x => x.ToString() == nameof(DeploymentFencingProcess));
            server.SendStop();
            Assert.True(server.WaitForShutdown(WaitTimeout));
        }

        Assert.Equal(["fetch"], cluster.Events);
        cluster.Connection.Verify(x => x.GetAllEntriesFromHash(DeploymentFencing.HashKey), Times.Never);
        cluster.Connection.Verify(x => x.AcquireDistributedLock($"lock:{DeploymentFencing.HashKey}", It.IsAny<TimeSpan>()), Times.Never);
    }

    private static BackgroundJobServerOptions FencedServerOptions()
    {
        return new BackgroundJobServerOptions
        {
            ServerName = "fenced-server",
            WorkerCount = 1,
            IsLightweightServer = true,
            ShutdownTimeout = TimeSpan.FromSeconds(15),
        };
    }

    // What a worker logs when it waits for the first deployment check of its server
    private static bool WaitsForItsFirstCheck(RecordingLogProvider recording)
    {
        return recording.Entries.Any(static x => x.Message.Contains("takes no jobs until its first deployment check", StringComparison.Ordinal));
    }

    private static void ResetDeploymentFencing()
    {
        DeploymentFencing.DeploymentId = null;
        DeploymentFencing.DeploymentOrder = null;
        DrainMode.InvalidateCache();
    }

    /// <summary>
    /// A storage that runs deployment v2 (order 2), keeps its hashes, and lets a test hold or fail the deployment
    /// check of a server and see when a worker fetches. Servers call it from several threads.
    /// </summary>
    private sealed class FencedCluster
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = [];
        private readonly List<string> _events = [];

        public FencedCluster()
        {
            _hashes[DeploymentFencing.HashKey] = new Dictionary<string, string>
            {
                [DeploymentFencing.CurrentField] = "v2",
                [DeploymentFencing.SeenField("v2")] = DeploymentFencing.FormatSeen(DateTime.UtcNow, 7, TimeSpan.FromSeconds(15)),
                [DeploymentFencing.OrderField("v2")] = "2",
            };

            Storage.Setup(x => x.GetConnection()).Returns(Connection.Object);

            // Behaves like a real storage: waits for a job until the server stops
            Connection
                .Setup(x => x.FetchNextJob(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
                .Returns<string[], CancellationToken>((_, token) =>
                {
                    Record("fetch");
                    Fetched.Set();
                    token.WaitHandle.WaitOne();
                    token.ThrowIfCancellationRequested();
                    return null;
                });

            Connection
                .Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>()))
                .Returns((string key) =>
                {
                    lock (_sync)
                    {
                        return _hashes.TryGetValue(key, out Dictionary<string, string> hash) ? new Dictionary<string, string>(hash) : null;
                    }
                });

            Connection
                .Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Returns((string resource, TimeSpan _) =>
                {
                    if (resource == $"lock:{DeploymentFencing.HashKey}")
                    {
                        CheckStarted.Set();
                        ReleaseCheck.Wait();

                        if (ChecksFail)
                        {
                            CheckFailed.Set();
                            throw new InvalidOperationException("storage down");
                        }
                    }

                    return new Mock<IDisposable>().Object;
                });

            Connection.Setup(x => x.CreateWriteTransaction()).Returns(CreateTransaction);
        }

        public Mock<JobStorage> Storage { get; } = new();
        public Mock<IStorageConnection> Connection { get; } = new();

        /// <summary>Set when a deployment check has reached its lock.</summary>
        public ManualResetEventSlim CheckStarted { get; } = new();

        /// <summary>A deployment check waits at its lock until this is set.</summary>
        public ManualResetEventSlim ReleaseCheck { get; } = new();

        /// <summary>Set when a deployment check has failed.</summary>
        public ManualResetEventSlim CheckFailed { get; } = new();

        /// <summary>Set when a worker has asked for a job.</summary>
        public ManualResetEventSlim Fetched { get; } = new();

        /// <summary>Whether a deployment check fails at its lock, as when the storage is down.</summary>
        public bool ChecksFail
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>"pause" for each pause of a server that was committed, "fetch" for each job a worker asked for, in order.</summary>
        public string[] Events
        {
            get
            {
                lock (_sync)
                {
                    return [.. _events];
                }
            }
        }

        private void Record(string name)
        {
            lock (_sync)
            {
                _events.Add(name);
            }
        }

        private IWriteOnlyTransaction CreateTransaction()
        {
            List<(string Key, List<KeyValuePair<string, string>> Fields)> pending = [];
            Mock<IWriteOnlyTransaction> transaction = new();

            transaction
                .Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback((string key, IEnumerable<KeyValuePair<string, string>> fields) => pending.Add((key, [.. fields])));

            transaction.Setup(x => x.Commit()).Callback(() =>
            {
                lock (_sync)
                {
                    foreach ((string key, List<KeyValuePair<string, string>> fields) in pending)
                    {
                        if (!_hashes.TryGetValue(key, out Dictionary<string, string> hash))
                        {
                            _hashes[key] = hash = [];
                        }

                        foreach (KeyValuePair<string, string> field in fields)
                        {
                            hash[field.Key] = field.Value;
                        }

                        if (key == DrainMode.HashKey && fields.Exists(static x => x.Key.StartsWith(DrainMode.ServerFieldPrefix, StringComparison.Ordinal)))
                        {
                            _events.Add("pause");
                        }
                    }
                }
            });

            return transaction.Object;
        }
    }
}
