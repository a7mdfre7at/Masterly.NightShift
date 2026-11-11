using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>Job functions run by name, and pending jobs deleted by method, by function or by condition, through real storage.</summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class JobFunctionFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<string> Log = new();

    public JobFunctionFacts()
    {
        JobFunctions.Add<Exports>();
    }

    [Fact, CleanDatabase]
    public async Task EnqueueFunction_RunsTheMethod_WithThePayload()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();

        string jobId = new BackgroundJobClient(storage).EnqueueFunction("sql-tests.export", new { Format = "csv", Rows = 10 });

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken)).Name);
        }

        Assert.Equal(["csv:10"], Log);
    }

    [Fact, CleanDatabase]
    public void DeletePendingJobs_DeletesTheJobsOfAMethod_OrAFunction_OrACondition()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);

        string exportEnqueued = client.EnqueueFunction("sql-tests.export", new { Format = "csv", Rows = 1 });
        string exportScheduled = client.ScheduleFunction("sql-tests.export", new { Format = "xml", Rows = 2 }, TimeSpan.FromHours(1));
        string other = client.Enqueue(() => Other("keep"));
        string continuation = client.ContinueJobWith(other, () => Other("then"));
        string critical = client.Enqueue("critical", () => Other("critical"));

        Assert.Equal(4, storage.GetPendingJobs().Count(static x => x.State != AwaitingState.StateName));
        Assert.Equal(2, storage.DeletePendingFunctionJobs("SQL-TESTS.EXPORT"));
        Assert.Equal(0, storage.DeletePendingJobs<Exports>());
        Assert.Equal(1, storage.DeletePendingJobs(static x => x.Queue == "critical"));
        Assert.Equal(2, storage.DeletePendingJobs<JobFunctionFacts>(nameof(Other)));

        using IStorageConnection connection = storage.GetConnection();
        Assert.All([exportEnqueued, exportScheduled, other, continuation, critical], id => Assert.Equal(DeletedState.StateName, connection.GetStateData(id).Name));
    }

    public static void Other(string name)
    {
        Log.Enqueue(name);
    }

    public sealed class Exports
    {
        [JobFunction("sql-tests.export")]
        public void Export(string format, int rows)
        {
            Log.Enqueue($"{format}:{rows}");
        }
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }
}
