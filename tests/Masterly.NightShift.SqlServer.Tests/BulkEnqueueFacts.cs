using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// <c>EnqueueMany</c> against real storage: what it writes in a few round trips is what <c>Enqueue</c> writes
/// job by job, and a server runs it.
/// </summary>
public class BulkEnqueueFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static int _ran;

    [Fact, CleanDatabase]
    public void EnqueueMany_CreatesEveryJob_InTheOrderGiven_WithItsArguments()
    {
        SqlServerStorage storage = CreateStorage();

        IReadOnlyList<string> ids = new BackgroundJobClient(storage)
            .EnqueueMany<BulkEnqueueFacts, int>(Enumerable.Range(0, 20), static (x, number) => x.Run(number));

        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());

        using IStorageConnection connection = storage.GetConnection();
        for (int i = 0; i < ids.Count; i++)
        {
            JobData data = connection.GetJobData(ids[i]);

            Assert.Equal(EnqueuedState.StateName, data.State);
            Assert.Equal(i, data.Job.Args[0]);
        }
    }

    [Fact, CleanDatabase]
    public void EnqueueMany_PutsEveryJobIntoItsQueue()
    {
        SqlServerStorage storage = CreateStorage();

        new BackgroundJobClient(storage).EnqueueMany<BulkEnqueueFacts, int>(Enumerable.Range(0, 12), static (x, number) => x.Run(number));

        Assert.Equal(12, storage.GetMonitoringApi().EnqueuedCount("default"));
    }

    [Fact, CleanDatabase]
    public void EnqueueMany_StoresTheParametersFiltersSet_ForEveryJob()
    {
        SqlServerStorage storage = CreateStorage();
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

        try
        {
            // The default client's filters include CaptureCulture, which sets two parameters per job
            IReadOnlyList<string> ids = new BackgroundJobClient(storage)
                .EnqueueMany<BulkEnqueueFacts, int>(Enumerable.Range(0, 3), static (x, number) => x.Run(number));

            using IStorageConnection connection = storage.GetConnection();
            Assert.All(ids, id => Assert.Contains("fr-FR", connection.GetJobParameter(id, "CurrentCulture")));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact, CleanDatabase]
    public void EnqueueMany_CreatesMoreJobsThanFitIntoOneCommand()
    {
        SqlServerStorage storage = CreateStorage();

        // Above BulkSize, and above what one command can carry in parameters
        IReadOnlyList<string> ids = new BackgroundJobClient(storage)
            .EnqueueMany<BulkEnqueueFacts, int>(Enumerable.Range(0, 900), static (x, number) => x.Run(number));

        Assert.Equal(900, ids.Distinct().Count());
        Assert.Equal(900, storage.GetMonitoringApi().EnqueuedCount("default"));

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(899, connection.GetJobData(ids[899]).Job.Args[0]);
    }

    [Fact, CleanDatabase]
    public void CreateExpiredJobs_ReturnsNothing_ForNoJobs()
    {
        SqlServerStorage storage = CreateStorage();
        using JobStorageConnection connection = (JobStorageConnection)storage.GetConnection();

        Assert.Empty(connection.CreateExpiredJobs([], [], DateTime.UtcNow, TimeSpan.FromHours(1)));
    }

    [Fact, CleanDatabase]
    public void CreateExpiredJobs_ThrowsAnException_ForAParameterNameThatIsTooLong()
    {
        SqlServerStorage storage = CreateStorage();
        using JobStorageConnection connection = (JobStorageConnection)storage.GetConnection();
        Dictionary<string, string> parameters = new() { [new string('x', 41)] = "value" };

        Assert.Throws<ArgumentException>(() => connection.CreateExpiredJobs(
            [Job.FromExpression<BulkEnqueueFacts>(static x => x.Run(1))], [parameters], DateTime.UtcNow, TimeSpan.FromHours(1)));
    }

    [Fact, CleanDatabase]
    public void EnqueueMany_CreatesJobsAServerRuns()
    {
        SqlServerStorage storage = CreateStorage();
        Interlocked.Exchange(ref _ran, 0);

        IReadOnlyList<string> ids = new BackgroundJobClient(storage)
            .EnqueueMany<BulkEnqueueFacts, int>(Enumerable.Range(0, 30), static (x, number) => x.Run(number));

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 4 }, storage))
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _ran) == 30, Timeout), "The jobs weren't run in time.");
            Assert.True(SpinWait.SpinUntil(() => HasSucceeded(storage, ids[^1]), Timeout), "The last job didn't succeed in time.");
        }
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }

    private static bool HasSucceeded(SqlServerStorage storage, string jobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetStateData(jobId)?.Name == "Succeeded";
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public void Run(int number)
    {
        Interlocked.Increment(ref _ran);
    }
}
