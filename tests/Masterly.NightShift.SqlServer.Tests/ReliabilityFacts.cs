extern alias ReferencedDapper;

using System.Diagnostics.CodeAnalysis;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>Argument compression, retention per state and the long-running pool, through real storage.</summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class ReliabilityFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static string _received;

    [Fact, CleanDatabase]
    public async Task CompressedArguments_AreStoredCompressed_AndReachTheJob()
    {
        SqlServerStorage storage = CreateStorage();
        string text = new('n', 20_000);
        GlobalConfiguration.Configuration.UseArgumentCompression(1024);

        // Arguments are compressed only at the newest data compatibility level, which older servers can't read
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180);

        try
        {
            string jobId = new BackgroundJobClient(storage).Enqueue(() => Receive(text));

            using (System.Data.Common.DbConnection sql = ConnectionUtils.CreateConnection())
            {
                string stored = sql.QuerySingle<string>("select Arguments from [HangFire].Job where Id = @id", new { id = long.Parse(jobId, System.Globalization.CultureInfo.InvariantCulture) });
                Assert.StartsWith("gz:", stored, StringComparison.Ordinal);
                Assert.True(stored.Length < text.Length / 10);
            }

            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
            {
                Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken)).Name);
            }

            Assert.Equal(text, _received);
        }
        finally
        {
            GlobalConfiguration.Configuration.UseArgumentCompression(null);
            GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_110);
        }
    }

    [Fact, CleanDatabase]
    public async Task JobRetention_SetsTheExpiration_OfSucceededJobs()
    {
        SqlServerStorage storage = CreateStorage();
        GlobalConfiguration.Configuration.UseJobRetention(o => o.Succeeded = TimeSpan.FromDays(10));

        try
        {
            string jobId = new BackgroundJobClient(storage).Enqueue(() => Receive("x"));
            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
            {
                await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken);
            }

            using System.Data.Common.DbConnection sql = ConnectionUtils.CreateConnection();
            DateTime expireAt = sql.QuerySingle<DateTime>("select ExpireAt from [HangFire].Job where Id = @id", new { id = long.Parse(jobId, System.Globalization.CultureInfo.InvariantCulture) });
            Assert.InRange(expireAt, DateTime.UtcNow.AddDays(9.9), DateTime.UtcNow.AddDays(10.1));
        }
        finally
        {
            GlobalConfiguration.Configuration.UseJobRetention(static _ => { });
        }
    }

    [Fact, CleanDatabase]
    public async Task LongRunningJobs_RunOnTheirOwnPool()
    {
        SqlServerStorage storage = CreateStorage();
        string jobId = new BackgroundJobClient(storage).Enqueue(() => Long());

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(LongRunningAttribute.Queue, connection.GetStateData(jobId).Data["Queue"]);

        // The server's own queues don't include it: only the long-running pool runs the job
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = ["other"], LongRunningWorkerCount = 1 }, storage))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken)).Name);
        }
    }

    [Fact, CleanDatabase]
    public async Task DeploymentOrder_KeepsTheOlderDeploymentFenced_UntilTheNewerOneIsGone()
    {
        SqlServerStorage storage = CreateStorage();
        TimeSpan checkInterval = DeploymentFencing.CheckInterval;
        TimeSpan takeOverAfter = DeploymentFencing.TakeOverAfter;
        TimeSpan holdInterval = DrainMode.HoldInterval;
        // A second between checks: a server whose checks are more than one and a half intervals apart starts
        // watching afresh, so a shorter interval would make the test depend on how busy the machine is
        DeploymentFencing.CheckInterval = TimeSpan.FromSeconds(1);
        DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(3);
        DrainMode.HoldInterval = TimeSpan.FromSeconds(1);

        try
        {
            // A server reads its deployment when it is created, so one process can host both deployments
            GlobalConfiguration.Configuration.UseDeploymentFencing("v2", 2);
            using BackgroundJobServer newer = new(new BackgroundJobServerOptions { WorkerCount = 1, Queues = ["newer"] }, storage);
            await WaitUntilAsync(() => DeploymentFencing.GetCurrentDeployment(storage) == "v2");

            GlobalConfiguration.Configuration.UseDeploymentFencing("v1", 1);
            BackgroundJobServerOptions olderOptions = new() { WorkerCount = 1, Queues = ["older"], SchedulePollingInterval = TimeSpan.FromMilliseconds(200) };
            using BackgroundJobServer older = new(olderOptions, storage);

            BackgroundJobClient client = new(storage);
            string forOlder = client.Enqueue("older", () => Receive("older"));
            string forNewer = client.Enqueue("newer", () => Receive("newer"));

            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(forNewer, Timeout, TestContext.Current.CancellationToken)).Name);

            // Longer than TakeOverAfter and several checks: the older deployment never takes over while the newer lives
            await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
            Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(storage));
            Assert.Equal(2, DeploymentFencing.GetCurrentDeploymentOrder(storage));
            using (IStorageConnection connection = storage.GetConnection())
            {
                Assert.Contains(connection.GetStateData(forOlder).Name, new[] { EnqueuedState.StateName, ScheduledState.StateName });
            }

            // The newer deployment goes away: once it has been silent long enough, the older one takes over and works
            // (disposing it again at the end of the block does nothing)
            newer.Dispose();

            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(forOlder, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).Name);
            Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(storage));
        }
        finally
        {
            DeploymentFencing.CheckInterval = checkInterval;
            DeploymentFencing.TakeOverAfter = takeOverAfter;
            DrainMode.HoldInterval = holdInterval;

            // There is no public way to turn fencing off again: later tests' servers must not run it
            typeof(DeploymentFencing).GetProperty(nameof(DeploymentFencing.DeploymentId))!.GetSetMethod(nonPublic: true)!.Invoke(null, [null]);
            typeof(DeploymentFencing).GetProperty(nameof(DeploymentFencing.DeploymentOrder))!.GetSetMethod(nonPublic: true)!.Invoke(null, [null]);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition wasn't met in time.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    public static void Receive(string text)
    {
        _received = text;
    }

    [LongRunning]
    public static void Long()
    {
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }
}
