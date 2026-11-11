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
