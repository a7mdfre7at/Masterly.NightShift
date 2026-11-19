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
            () => new BackgroundJobServer(_serverOptions, null));

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
        using ManualResetEventSlim announced = new();
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
        using ManualResetEventSlim executed = new();
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
    public void Ctor_WithDeploymentFencing_FetchesNoJob_BeforeItsFirstCheckHasDecided()
    {
        // The server is of the current deployment: its first check will find that it may work
        FencedCluster cluster = new(currentDeployment: "v1");
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
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches > 0 || Logged(recording, "takes no jobs until its first deployment check"), WaitTimeout));
                fetchedBeforeTheCheck = cluster.Fetches > 0;

                cluster.ReleaseCheck.Set();
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches > 0, WaitTimeout));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            Assert.False(fetchedBeforeTheCheck, "A worker fetched a job before the first deployment check had decided.");
            Assert.Equal(["announce", "check", "fetch"], cluster.Events);
            Assert.Empty(DrainMode.Read(cluster.Connection.Object).PausedServers);

            // Named as before there was anything in front of the workers
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
        FencedCluster cluster = new(currentDeployment: "v1") { ChecksFail = true };
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
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches > 0 || Logged(recording, "takes no jobs until its first deployment check"), WaitTimeout));
                fetchedBeforeTheCheck = cluster.Fetches > 0;

                cluster.ChecksFail = false;
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches > 0, WaitTimeout));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            Assert.False(fetchedBeforeTheCheck, "A worker fetched a job although the first deployment check had failed.");
            Assert.Equal(["announce", "check", "fetch"], cluster.Events);
            Assert.Contains(recording.Entries, static x =>
                x.Level == LogLevel.Warn &&
                x.Message.Contains("takes no jobs until a check succeeds", StringComparison.Ordinal));
        }
        finally
        {
            ResetDeploymentFencing();
        }
    }

    [Fact, GlobalLock(Reason = "Sets the deployment of the process, uses the drain mode cache and the log provider")]
    public void Ctor_WithDeploymentFencing_AFencedServerFetchesNoJob()
    {
        // The cluster runs the newer deployment v2, and a server of v1 starts: it is fenced
        FencedCluster cluster = new(currentDeployment: "v2");
        cluster.ReleaseCheck.Set();
        RecordingLogProvider recording = new();
        DeploymentFencing.DeploymentId = "v1";
        DeploymentFencing.DeploymentOrder = 1;

        try
        {
            using (recording.Use())
            using (BackgroundJobServer server = new(FencedServerOptions(), cluster.Storage.Object, []))
            {
                // A worker that fetches although its server is paused does it right after the check; one that
                // doesn't says so, and then the test knows that the worker had its turn
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches > 0 || Logged(recording, "is fenced: it takes no jobs"), WaitTimeout));

                // The gate closes and that line is logged before the pause is written; a stop that came in between
                // would leave no pause to take back, so wait for it as the restart test does
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches > 0 || cluster.PausedServers.Length == 1, WaitTimeout));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            Assert.Equal(["announce", "check", "pause", "resume"], cluster.Events);
            Assert.Equal(0, cluster.Fetches);
        }
        finally
        {
            ResetDeploymentFencing();
        }
    }

    [Fact, GlobalLock(Reason = "Sets the deployment of the process, uses the drain mode cache and the log provider")]
    public void Ctor_WithDeploymentFencing_AServerThatRestartsWithANewId_FetchesNoJob_BeforeACheckHasDecidedForThatId()
    {
        FencedCluster cluster = new(currentDeployment: "v1");
        RecordingLogProvider recording = new();
        BackgroundJobServerOptions options = FencedServerOptions();
        options.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
        DeploymentFencing.DeploymentId = "v1";
        DeploymentFencing.DeploymentOrder = 1;

        try
        {
            int fetchesWhileTheCheckWasHeld;
            using (recording.Use())
            using (BackgroundJobServer server = new(options, cluster.Storage.Object, []))
            {
                cluster.ReleaseCheck.Set();
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches == 1, WaitTimeout));
                Assert.Equal(["announce", "check", "fetch"], cluster.Events);

                // The checks are slow from now on (another server holds the lock, the storage is slow), and another
                // server's watchdog removes this one: its heartbeat restarts it with a new id
                cluster.ReleaseCheck.Reset();
                cluster.CheckStarted.Reset();
                cluster.RemoveServerOnce = true;
                Assert.True(SpinWait.SpinUntil(() => cluster.Announced.Length == 2, WaitTimeout));
                Assert.True(cluster.CheckStarted.Wait(WaitTimeout, TestContext.Current.CancellationToken));
                string newId = cluster.Announced[1];

                // The worker of the new id has its turn while the check is held at its lock
                Assert.True(SpinWait.SpinUntil(
                    () => cluster.Fetches == 2 || Logged(recording, $"Server '{newId}' takes no jobs until its first deployment check"),
                    WaitTimeout));
                fetchesWhileTheCheckWasHeld = cluster.Fetches;

                cluster.ReleaseCheck.Set();
                Assert.True(SpinWait.SpinUntil(() => cluster.Fetches == 2, WaitTimeout));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            Assert.True(
                fetchesWhileTheCheckWasHeld == 1,
                $"The restarted server fetched a job before a deployment check had decided for its new id. Events: {string.Join(", ", cluster.Events)}");
            Assert.Equal(["announce", "check", "fetch", "announce", "check", "fetch"], cluster.Events);
            Assert.NotEqual(cluster.Announced[0], cluster.Announced[1]);
        }
        finally
        {
            cluster.ReleaseCheck.Set();
            ResetDeploymentFencing();
        }
    }

    [Fact, GlobalLock(Reason = "Sets the deployment of the process, uses the drain mode cache and the log provider")]
    public void Ctor_WithDeploymentFencing_AFencedServerThatRestarts_FetchesNoJob_AndLeavesNoPauseOfItsOldId()
    {
        FencedCluster cluster = new(currentDeployment: "v2");
        cluster.ReleaseCheck.Set();
        BackgroundJobServerOptions options = FencedServerOptions();
        options.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
        DeploymentFencing.DeploymentId = "v1";
        DeploymentFencing.DeploymentOrder = 1;

        try
        {
            using (BackgroundJobServer server = new(options, cluster.Storage.Object, []))
            {
                Assert.True(SpinWait.SpinUntil(() => cluster.PausedServers.Length == 1, WaitTimeout));
                string oldId = cluster.PausedServers[0];

                cluster.RemoveServerOnce = true;
                Assert.True(SpinWait.SpinUntil(() => cluster.PausedServers is [string paused] && paused != oldId, WaitTimeout));
                string newId = cluster.PausedServers[0];

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));

                Assert.Equal([oldId, newId], cluster.Announced);
            }

            // Paused, resumed when the id was given up, and the same for the new id when the server stopped
            Assert.Equal(["announce", "check", "pause", "resume", "announce", "check", "pause", "resume"], cluster.Events);
            Assert.Equal(0, cluster.Fetches);
            Assert.Empty(cluster.PausedServers);
        }
        finally
        {
            ResetDeploymentFencing();
        }
    }

    [Fact, GlobalLock(Reason = "Reads the deployment of the process, uses the drain mode cache")]
    public void Ctor_WithoutDeploymentFencing_StartsItsWorkers_AndNeverReadsTheDeployment()
    {
        FencedCluster cluster = new(currentDeployment: "v2");
        cluster.ReleaseCheck.Set();
        ResetDeploymentFencing();

        using (BackgroundJobServer server = new(FencedServerOptions(), cluster.Storage.Object, []))
        {
            Assert.True(SpinWait.SpinUntil(() => cluster.Fetches > 0, WaitTimeout));

            Assert.Contains(server.Processes, static x => x.ToString() == nameof(Worker));
            Assert.DoesNotContain(server.Processes, static x => x.ToString() == nameof(DeploymentFencingProcess));
            server.SendStop();
            Assert.True(server.WaitForShutdown(WaitTimeout));
        }

        Assert.Equal(["announce", "fetch"], cluster.Events);
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

    private static bool Logged(RecordingLogProvider recording, string text)
    {
        return recording.Entries.Any(x => x.Message.Contains(text, StringComparison.Ordinal));
    }

    private static void ResetDeploymentFencing()
    {
        DeploymentFencing.DeploymentId = null;
        DeploymentFencing.DeploymentOrder = null;
        DrainMode.InvalidateCache();
    }

    /// <summary>
    /// A storage that keeps its hashes, with a current deployment, and lets a test hold or fail the deployment check
    /// of a server, remove the server as another server's watchdog would, and see in which order the server was
    /// announced, its deployment check written, the server paused and resumed, and a job fetched. Servers call it
    /// from several threads.
    /// </summary>
    private sealed class FencedCluster
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = [];
        private readonly List<string> _events = [];
        private readonly List<string> _announced = [];
        private int _fetches;

        public FencedCluster(string currentDeployment)
        {
            _hashes[DeploymentFencing.HashKey] = new Dictionary<string, string>
            {
                [DeploymentFencing.CurrentField] = currentDeployment,
                [DeploymentFencing.SeenField(currentDeployment)] = DeploymentFencing.FormatSeen(DateTime.UtcNow, 7, TimeSpan.FromSeconds(15)),
                [DeploymentFencing.OrderField("v1")] = "1",
                [DeploymentFencing.OrderField("v2")] = "2",
            };

            Storage.Setup(x => x.GetConnection()).Returns(Connection.Object);

            Connection
                .Setup(x => x.AnnounceServer(It.IsAny<string>(), It.IsAny<ServerContext>()))
                .Callback((string serverId, ServerContext _) =>
                {
                    lock (_sync)
                    {
                        _announced.Add(serverId);
                        _events.Add("announce");
                    }
                });

            Connection
                .Setup(x => x.Heartbeat(It.IsAny<string>()))
                .Callback((string _) =>
                {
                    if (RemoveServerOnce)
                    {
                        RemoveServerOnce = false;
                        throw new BackgroundServerGoneException();
                    }
                });

            // Behaves like a real storage: waits for a job until the server stops
            Connection
                .Setup(x => x.FetchNextJob(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
                .Returns<string[], CancellationToken>((_, token) =>
                {
                    lock (_sync)
                    {
                        _events.Add("fetch");
                        _fetches++;
                    }

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

        /// <summary>Whether a deployment check fails at its lock, as when the storage is down.</summary>
        public bool ChecksFail
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>Makes the next heartbeat find the server removed, as after another server's watchdog took it for dead.</summary>
        public bool RemoveServerOnce
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>How many times a worker has asked for a job.</summary>
        public int Fetches
        {
            get
            {
                lock (_sync)
                {
                    return _fetches;
                }
            }
        }

        /// <summary>
        /// In order: "announce" for each server id announced, "check" for each deployment check written, "pause"
        /// and "resume" for each pause of a server committed or taken back, "fetch" for each job a worker asked for.
        /// </summary>
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

        /// <summary>The server ids announced, in order.</summary>
        public string[] Announced
        {
            get
            {
                lock (_sync)
                {
                    return [.. _announced];
                }
            }
        }

        /// <summary>The servers paused now.</summary>
        public string[] PausedServers => [.. DrainMode.Read(Connection.Object).PausedServers.Keys];

        // The pauses' fields of the drain-mode hash, called under _sync
        private Dictionary<string, string> ServerFields()
        {
            return _hashes.TryGetValue(DrainMode.HashKey, out Dictionary<string, string> hash)
                ? hash.Where(static x => x.Key.StartsWith(DrainMode.ServerFieldPrefix, StringComparison.Ordinal)).ToDictionary()
                : [];
        }

        private IWriteOnlyTransaction CreateTransaction()
        {
            // A null list removes the hash
            List<(string Key, List<KeyValuePair<string, string>> Fields)> pending = [];
            Mock<IWriteOnlyTransaction> transaction = new();

            transaction
                .Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback((string key, IEnumerable<KeyValuePair<string, string>> fields) => pending.Add((key, [.. fields])));
            transaction
                .Setup(x => x.RemoveHash(It.IsAny<string>()))
                .Callback((string key) => pending.Add((key, null)));

            transaction.Setup(x => x.Commit()).Callback(() =>
            {
                lock (_sync)
                {
                    Dictionary<string, string> pausesBefore = ServerFields();
                    foreach ((string key, List<KeyValuePair<string, string>> fields) in pending)
                    {
                        if (fields == null)
                        {
                            _hashes.Remove(key);
                            continue;
                        }

                        if (!_hashes.TryGetValue(key, out Dictionary<string, string> hash))
                        {
                            _hashes[key] = hash = [];
                        }

                        foreach (KeyValuePair<string, string> field in fields)
                        {
                            hash[field.Key] = field.Value;
                        }

                        if (key == DeploymentFencing.HashKey)
                        {
                            _events.Add("check");
                        }
                    }

                    // A resume removes the server's field, by writing the hash again without it
                    Dictionary<string, string> pausesAfter = ServerFields();
                    _events.AddRange(pausesBefore.Keys.Where(x => !pausesAfter.ContainsKey(x)).Select(static _ => "resume"));
                    _events.AddRange(pausesAfter.Where(x => !pausesBefore.TryGetValue(x.Key, out string before) || before != x.Value).Select(static _ => "pause"));
                }
            });

            return transaction.Object;
        }
    }
}
