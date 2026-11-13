using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Deployment fencing and paused servers through real storage: a server that is fenced or paused takes no jobs, so
/// it neither slows the others down nor makes their jobs late, also after it got a new server id.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class DeploymentFencingFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static int _ranOnOlder;
    private static int _ran;

    /// <summary>
    /// A fenced server whose server row is removed (what another server's watchdog does after a pause or an
    /// outage) restarts with a new id. Nothing has paused the new id yet, so it must wait for its deployment check.
    /// </summary>
    [Fact, CleanDatabase]
    public async Task FencedServer_ThatRestartsWithANewId_RunsNoJob_WhileTheNewerDeploymentLives()
    {
        SqlServerStorage storage = CreateStorage();
        TimeSpan checkInterval = DeploymentFencing.CheckInterval;
        TimeSpan takeOverAfter = DeploymentFencing.TakeOverAfter;
        DeploymentFencing.CheckInterval = TimeSpan.FromSeconds(1);
        DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(3);
        Volatile.Write(ref _ranOnOlder, 0);

        BackgroundJobServer? newer = null;
        BackgroundJobServer? older = null;
        using CancellationTokenSource stopEnqueuing = new();

        try
        {
            GlobalConfiguration.Configuration.UseDeploymentFencing("v2", 2);
            newer = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = ["newer"] }, storage);
            await WaitUntilAsync(() => DeploymentFencing.GetCurrentDeployment(storage) == "v2");

            GlobalConfiguration.Configuration.UseDeploymentFencing("v1", 1);
            older = new BackgroundJobServer(
                new BackgroundJobServerOptions { WorkerCount = 4, Queues = ["older"], HeartbeatInterval = TimeSpan.FromMilliseconds(200) },
                storage);

            await WaitUntilAsync(() => PausedServers(storage).Count == 1);
            string olderId = PausedServers(storage).Keys.Single();

            // Jobs keep arriving for the queue only the older deployment listens to
            BackgroundJobClient client = new(storage);
            Task enqueuing = Task.Run(
                () =>
                {
                    while (!stopEnqueuing.IsCancellationRequested)
                    {
                        client.Enqueue("older", () => RanOnOlder());
                    }
                },
                TestContext.Current.CancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref _ranOnOlder));
            Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(storage));

            // Another server's watchdog takes it for dead (it was suspended, the storage was away): the heartbeat
            // finds the row gone and the server restarts with a new id
            using (IStorageConnection connection = storage.GetConnection())
            {
                connection.RemoveServer(olderId);
            }

            await WaitUntilAsync(() => PausedServers(storage).Keys.Any(x => x != olderId));
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await stopEnqueuing.CancelAsync();
            await enqueuing;

            Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(storage));
            Assert.True(
                Volatile.Read(ref _ranOnOlder) == 0,
                $"The fenced deployment v1 ran {Volatile.Read(ref _ranOnOlder)} job(s) after its server restarted, while v2 was the current deployment and alive.");

            // The pause of the id that is gone doesn't stay behind
            Assert.DoesNotContain(olderId, PausedServers(storage).Keys);
        }
        finally
        {
            await stopEnqueuing.CancelAsync();
            older?.Dispose();
            newer?.Dispose();
            DeploymentFencing.CheckInterval = checkInterval;
            DeploymentFencing.TakeOverAfter = takeOverAfter;
            ResetFencing();
        }
    }

    /// <summary>
    /// A fenced server that kept fetching from the queues it shares with the current deployment put every job it
    /// got a hold interval into the future, and with more workers than the current deployment it got most of them.
    /// </summary>
    [Fact, CleanDatabase]
    public async Task FencedServer_DoesNotSlowTheCurrentDeploymentDown_OnTheQueuesTheyShare()
    {
        const int jobs = 2000;
        SqlServerStorage storage = CreateStorage();
        TimeSpan checkInterval = DeploymentFencing.CheckInterval;
        TimeSpan takeOverAfter = DeploymentFencing.TakeOverAfter;
        DeploymentFencing.CheckInterval = TimeSpan.FromSeconds(1);
        DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(3);
        Volatile.Write(ref _ran, 0);

        BackgroundJobServer? newer = null;
        BackgroundJobServer? older = null;

        try
        {
            GlobalConfiguration.Configuration.UseDeploymentFencing("v2", 2);
            newer = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 20 }, storage);
            await WaitUntilAsync(() => DeploymentFencing.GetCurrentDeployment(storage) == "v2");

            // The current deployment alone
            BackgroundJobClient client = new(storage);
            Stopwatch alone = Stopwatch.StartNew();
            for (int i = 0; i < jobs; i++)
            {
                client.Enqueue(() => Ran());
            }

            await WaitUntilAsync(() => Volatile.Read(ref _ran) == jobs);
            alone.Stop();

            // A server of the older deployment is still around (a rolling deploy, a stray instance): fenced
            GlobalConfiguration.Configuration.UseDeploymentFencing("v1", 1);
            older = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 20 }, storage);
            await WaitUntilAsync(() => PausedServers(storage).Count == 1);

            Volatile.Write(ref _ran, 0);
            Stopwatch together = Stopwatch.StartNew();
            for (int i = 0; i < jobs; i++)
            {
                client.Enqueue(() => Ran());
            }

            // Three times what the current deployment needed alone, and at least ten seconds
            TimeSpan allowed = TimeSpan.FromSeconds(Math.Max(10, alone.Elapsed.TotalSeconds * 3));
            while (together.Elapsed < allowed && Volatile.Read(ref _ran) < jobs)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            int ran = Volatile.Read(ref _ran);
            long held = storage.GetMonitoringApi().ScheduledCount();

            Assert.True(
                ran == jobs,
                $"Alone, the current deployment ran {jobs} jobs in {alone.Elapsed.TotalSeconds:0.0} s. With one fenced server of the older deployment on the same queue it ran {ran} of {jobs} in {together.Elapsed.TotalSeconds:0.0} s; {held} are held in the Scheduled state.");
            Assert.Equal(0, held);
        }
        finally
        {
            older?.Dispose();
            newer?.Dispose();
            DeploymentFencing.CheckInterval = checkInterval;
            DeploymentFencing.TakeOverAfter = takeOverAfter;
            ResetFencing();
        }
    }

    /// <summary>
    /// A job with a late-start window that a fenced server picked up first was held, and then deleted as skipped
    /// when a server of the current deployment got it, although that deployment had idle workers all along.
    /// </summary>
    [Fact, CleanDatabase]
    public async Task FencedServer_DoesNotMakeTheCurrentDeploymentSkipJobsAsLate()
    {
        const int jobs = 200;
        SqlServerStorage storage = CreateStorage();
        TimeSpan checkInterval = DeploymentFencing.CheckInterval;
        TimeSpan takeOverAfter = DeploymentFencing.TakeOverAfter;
        DeploymentFencing.CheckInterval = TimeSpan.FromSeconds(1);
        DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(3);

        BackgroundJobServer? newer = null;
        BackgroundJobServer? older = null;

        try
        {
            GlobalConfiguration.Configuration.UseDeploymentFencing("v2", 2);
            newer = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 20 }, storage);
            await WaitUntilAsync(() => DeploymentFencing.GetCurrentDeployment(storage) == "v2");

            GlobalConfiguration.Configuration.UseDeploymentFencing("v1", 1);
            older = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 20 }, storage);
            await WaitUntilAsync(() => PausedServers(storage).Count == 1);

            BackgroundJobClient client = new(storage);
            for (int i = 0; i < jobs; i++)
            {
                client.Enqueue(() => RanInTime());
            }

            IMonitoringApi monitoring = storage.GetMonitoringApi();
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(150) && monitoring.SucceededListCount() + monitoring.DeletedListCount() < jobs)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }

            long succeeded = monitoring.SucceededListCount();
            long deleted = monitoring.DeletedListCount();
            string? reason = null;
            string? deletedId = monitoring.DeletedJobs(0, 1).Select(static x => x.Key).FirstOrDefault();
            if (deletedId != null)
            {
                using IStorageConnection connection = storage.GetConnection();
                reason = connection.GetStateData(deletedId)?.Reason;
            }

            Assert.True(
                deleted == 0,
                $"Of {jobs} jobs with [SkipIfLate(20)] enqueued while the current deployment had 20 idle workers, {succeeded} ran and {deleted} were deleted ({reason}) after {waited.Elapsed.TotalSeconds:0} s: a fenced server of the older deployment held them first.");
            Assert.Equal(jobs, succeeded);
        }
        finally
        {
            older?.Dispose();
            newer?.Dispose();
            DeploymentFencing.CheckInterval = checkInterval;
            DeploymentFencing.TakeOverAfter = takeOverAfter;
            ResetFencing();
        }
    }

    /// <summary>
    /// A server paused by hand stops fetching as a fenced one does. Its workers that were waiting for a job when it
    /// was paused each take one more, which is held; the others stay in the queue for the servers that may work.
    /// </summary>
    [Fact, CleanDatabase]
    public async Task ServerPausedByHand_LeavesTheJobsInTheQueue_AndRunsThemOnceItIsResumed()
    {
        const int jobs = 20;
        const int workers = 2;
        SqlServerStorage storage = CreateStorage();
        TimeSpan holdInterval = DrainMode.HoldInterval;
        DrainMode.HoldInterval = TimeSpan.FromSeconds(3);
        Volatile.Write(ref _ran, 0);

        try
        {
            using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = workers, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage);
            IMonitoringApi monitoring = storage.GetMonitoringApi();
            await WaitUntilAsync(() => monitoring.Servers().Count == 1);
            string serverId = monitoring.Servers().Single().Name;

            using (IStorageConnection connection = storage.GetConnection())
            {
                DrainMode.PauseServer(connection, serverId, "maintenance");
            }

            BackgroundJobClient client = new(storage);
            for (int i = 0; i < jobs; i++)
            {
                client.Enqueue(() => Ran());
            }

            // Long enough for a server that kept fetching to hold every job
            await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
            long held = monitoring.ScheduledCount();
            long waiting = monitoring.EnqueuedCount(EnqueuedState.DefaultQueue);
            int ranWhilePaused = Volatile.Read(ref _ran);

            using (IStorageConnection connection = storage.GetConnection())
            {
                DrainMode.ResumeServer(connection, serverId);
            }

            await WaitUntilAsync(() => Volatile.Read(ref _ran) == jobs);

            Assert.Equal(0, ranWhilePaused);
            Assert.InRange(held, 0, workers);
            Assert.InRange(waiting, jobs - workers, jobs);
        }
        finally
        {
            DrainMode.HoldInterval = holdInterval;
            DrainMode.InvalidateCache();
        }
    }

    [SkipIfLate(20)]
    public static void RanInTime()
    {
        Thread.Sleep(20);
    }

    public static void RanOnOlder()
    {
        Interlocked.Increment(ref _ranOnOlder);
    }

    public static void Ran()
    {
        Thread.Sleep(20);
        Interlocked.Increment(ref _ran);
    }

    private static IReadOnlyDictionary<string, string?> PausedServers(JobStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return DrainMode.Read(connection).PausedServers;
    }

    private static void ResetFencing()
    {
        // There is no public way to turn fencing off again: later tests' servers must not run it
        typeof(DeploymentFencing).GetProperty(nameof(DeploymentFencing.DeploymentId))!.GetSetMethod(nonPublic: true)!.Invoke(null, [null]);
        typeof(DeploymentFencing).GetProperty(nameof(DeploymentFencing.DeploymentOrder))!.GetSetMethod(nonPublic: true)!.Invoke(null, [null]);
        DrainMode.InvalidateCache();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition wasn't met in time.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }
}
