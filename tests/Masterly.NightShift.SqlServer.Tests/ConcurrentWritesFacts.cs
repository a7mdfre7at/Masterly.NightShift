using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Jobs created by many callers at once are written in groups, and transactions are committed in one round
/// trip: what ends up in the database is what one caller after another would have written.
/// </summary>
public class ConcurrentWritesFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static int _ran;

    [Theory, CleanDatabase]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public void Enqueue_FromManyCallersAtOnce_CreatesEveryJobOnce(int maxConcurrentWriters)
    {
        SqlServerStorage storage = CreateStorage(x => x.MaxConcurrentWriters = maxConcurrentWriters);
        BackgroundJobClient client = new(storage);
        string[][] ids = new string[16][];

        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = ids.Length }, caller =>
        {
            ids[caller] = new string[25];
            for (int i = 0; i < ids[caller].Length; i++)
            {
                int number = (caller * 100) + i;
                ids[caller][i] = client.Enqueue<ConcurrentWritesFacts>(x => x.Run(number));
            }
        });

        Assert.Equal(400, ids.SelectMany(static x => x).Distinct().Count());
        Assert.Equal(400, storage.GetMonitoringApi().EnqueuedCount("default"));

        // Every caller got the identifier of its own job
        using IStorageConnection connection = storage.GetConnection();
        for (int caller = 0; caller < ids.Length; caller++)
        {
            JobData data = connection.GetJobData(ids[caller][24]);

            Assert.Equal((caller * 100) + 24, data.Job.Args[0]);
            Assert.Equal(EnqueuedState.StateName, data.State);
        }
    }

    [Fact, CleanDatabase]
    public void Enqueue_FromManyCallersAtOnce_CreatesJobsAServerRuns()
    {
        SqlServerStorage storage = CreateStorage(static x => x.MaxConcurrentWriters = 1);
        BackgroundJobClient client = new(storage);
        ResetRan();

        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, number => client.Enqueue<ConcurrentWritesFacts>(x => x.Run(number)));

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 4 }, storage))
        {
            Assert.True(SpinWait.SpinUntil(static () => RanCount() == 40, Timeout), "The jobs weren't run in time.");
            Assert.True(SpinWait.SpinUntil(() => storage.GetMonitoringApi().SucceededListCount() == 40, Timeout), "The jobs didn't succeed in time.");
        }

        // Finished jobs leave their queue in the transaction of their final state
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Equal(0, storage.GetMonitoringApi().FetchedCount("default"));
    }

    [Fact, CleanDatabase]
    public void Commit_StoresEverythingOrNothing_WhenACommandFails()
    {
        SqlServerStorage storage = CreateStorage();
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();

        transaction.IncrementCounter("commit-test");
        // A state for a job that doesn't exist breaks the foreign key
        transaction.SetJobState("987654321", new EnqueuedState());

        Assert.Throws<SqlException>(transaction.Commit);
        Assert.Equal(0, ((JobStorageConnection)connection).GetCounter("commit-test"));
    }

    [Fact, CleanDatabase]
    public void Commit_StoresTheCommandsOfATransaction_WithTheSameParameterNames()
    {
        SqlServerStorage storage = CreateStorage();
        using JobStorageConnection connection = (JobStorageConnection)storage.GetConnection();

        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            // Every one of these commands has a @key and a @value parameter
            transaction.IncrementCounter("first");
            transaction.IncrementCounter("first");
            transaction.IncrementCounter("second");
            transaction.SetRangeInHash("hash", [new KeyValuePair<string, string>("field", "value")]);
            transaction.AddToSet("set", "member", 2.5);
            transaction.Commit();
        }

        Assert.Equal(2, connection.GetCounter("first"));
        Assert.Equal(1, connection.GetCounter("second"));
        Assert.Equal("value", connection.GetAllEntriesFromHash("hash")["field"]);
        Assert.Equal(["member"], connection.GetAllItemsFromSet("set"));
    }

    [Fact, CleanDatabase]
    public void Commit_StoresATransactionTooLargeForOneCommand()
    {
        SqlServerStorage storage = CreateStorage();
        using JobStorageConnection connection = (JobStorageConnection)storage.GetConnection();

        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                transaction.AddToSet("large-set", i.ToString());
            }

            transaction.Commit();
        }

        Assert.Equal(200, connection.GetSetCount("large-set"));
    }

    [Fact, CleanDatabase]
    public void AllowDelayedDurability_AllowsItForTheDatabase_AndJobsStillRun()
    {
        SqlServerStorage storage = CreateStorage(static x => x.AllowDelayedDurability = true);
        ResetRan();

        try
        {
            Assert.True(storage.UseDelayedDurability);

            BackgroundJobClient client = new(storage);
            string[] ids = [.. Enumerable.Range(0, 20).Select(number => client.Enqueue<ConcurrentWritesFacts>(x => x.Run(number)))];
            IReadOnlyList<string> bulk = client.EnqueueMany<ConcurrentWritesFacts, int>(Enumerable.Range(20, 20), static (x, number) => x.Run(number));

            Assert.Equal(40, ids.Concat(bulk).Distinct().Count());

            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 4 }, storage))
            {
                Assert.True(SpinWait.SpinUntil(static () => RanCount() == 40, Timeout), "The jobs weren't run in time.");
                Assert.True(SpinWait.SpinUntil(() => storage.GetMonitoringApi().SucceededListCount() == 40, Timeout), "The jobs didn't succeed in time.");
            }
        }
        finally
        {
            // The other tests run with the setting a new database has
            using DbConnection connection = ConnectionUtils.CreateConnection();
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "alter database current set delayed_durability = disabled";
            command.ExecuteNonQuery();
        }
    }

    [Fact, CleanDatabase]
    public void UseDelayedDurability_IsOff_WhenTheDatabaseDoesNotAllowIt()
    {
        SqlServerStorage storage = CreateStorage();

        Assert.False(storage.UseDelayedDurability);
    }

    [Fact]
    public void MaxConcurrentWriters_ThrowsAnException_WhenValueIsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(static () => new SqlServerStorageOptions { MaxConcurrentWriters = -1 });
    }

    [Fact]
    public void Options_HaveTheDefaultsThatSaveRoundTrips()
    {
        SqlServerStorageOptions options = new();

        Assert.True(options.UseTransactionalAcknowledge);
        Assert.Equal(8, options.MaxConcurrentWriters);
        Assert.False(options.AllowDelayedDurability);
    }

    private static SqlServerStorage CreateStorage(Action<SqlServerStorageOptions> configure = null)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };
        configure?.Invoke(options);
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), options);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public void Run(int number)
    {
        CountRun();
    }

    // The job runs on an instance the server creates, so the count is shared by every instance
    private static void CountRun() => Interlocked.Increment(ref _ran);

    private static void ResetRan() => Interlocked.Exchange(ref _ran, 0);

    private static int RanCount() => Volatile.Read(ref _ran);
}
