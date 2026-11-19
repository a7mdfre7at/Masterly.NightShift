using System.Collections.Concurrent;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// The job console (<see cref="IJobConsole"/>) against real storage, with <see cref="JobConsoleStateFilter"/>
/// applied by real state changes.
/// </summary>
public class JobConsoleFacts
{
    [Fact, CleanDatabase]
    public void Writes_FromSeveralThreads_Succeed_WhileTheWorkerConnectionHoldsADistributedLock()
    {
        // [DisableConcurrentExecution] holds a lock on the worker's connection for the whole run
        SqlServerStorage storage = CreateStorage();
        string jobId = CreateJob(storage);
        ConcurrentQueue<Exception> errors = new();

        using (IStorageConnection connection = storage.GetConnection())
        using (connection.AcquireDistributedLock($"console-test:{jobId}", TimeSpan.FromSeconds(5)))
        {
            IJobConsole console = CreateContext(storage, connection, jobId).Console;

            Parallel.For(0, 100, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    if (i % 2 == 0)
                    {
                        console.WriteLine($"line {i}");
                    }
                    else
                    {
                        console.SetProgress(i, $"step {i}");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    errors.Enqueue(ex);
                }
            });
        }

        Assert.Empty(errors);
        using (IStorageConnection connection = storage.GetConnection())
        {
            Assert.Equal(50, JobConsole.ReadEntries(connection, jobId).Count);
            Assert.NotNull(JobConsole.ReadProgress(connection, jobId));
        }
    }

    [Fact, CleanDatabase]
    public void Checkpoints_SurviveTheConsoleBeingTrimmed()
    {
        SqlServerStorage storage = CreateStorage();
        string jobId = CreateJob(storage);
        int ran = 0;

        Perform(storage, jobId, console =>
        {
            console.Resume("download", () => ran++);
            for (int i = 0; i < JobConsole.DefaultMaxLines + 10; i++)
            {
                console.WriteLine($"row {i}");
            }
        });
        Perform(storage, jobId, console => console.Resume("download", () => ran++));

        Assert.Equal(1, ran);
    }

    [Fact, CleanDatabase]
    public void RequeuingASucceededJob_RunsItsResumeStepsAgain()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        string jobId = CreateJob(storage);
        int ran = 0;

        Perform(storage, jobId, console => console.Resume("download", () => ran++));
        Assert.True(client.ChangeState(jobId, new SucceededState(null, 0, 0)));

        using (JobStorageConnection connection = (JobStorageConnection)storage.GetConnection())
        {
            Assert.True(connection.GetListTtl(JobConsole.GetConsoleKey(jobId)) > TimeSpan.Zero);
            Assert.True(connection.GetListTtl(JobConsole.GetCheckpointsKey(jobId)) > TimeSpan.Zero);
        }

        Assert.True(client.Requeue(jobId));
        Perform(storage, jobId, console => console.Resume("download", () => ran++));

        Assert.Equal(2, ran);
        using (JobStorageConnection connection = (JobStorageConnection)storage.GetConnection())
        {
            Assert.True(connection.GetListTtl(JobConsole.GetConsoleKey(jobId)) < TimeSpan.Zero);
            Assert.True(connection.GetListTtl(JobConsole.GetCheckpointsKey(jobId)) < TimeSpan.Zero);
        }
    }

    [Fact, CleanDatabase]
    public void RequeuingAFailedJob_SkipsTheStepsThatFinished()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        string jobId = CreateJob(storage);
        int ran = 0;

        Perform(storage, jobId, console => console.Resume("download", () => ran++));
        // The global AutomaticRetry filter may turn Failed into Scheduled; neither is final
        client.ChangeState(jobId, new FailedState(new InvalidOperationException("boom")));
        Assert.True(client.Requeue(jobId));
        Perform(storage, jobId, console => console.Resume("download", () => ran++));

        Assert.Equal(1, ran);
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }

    private static string CreateJob(JobStorage storage)
    {
        return new BackgroundJobClient(storage).Create(() => ConsoleTestJob.Noop(), new ScheduledState(TimeSpan.FromDays(1)));
    }

    /// <summary>One attempt, with the job data a worker would load.</summary>
    private static void Perform(JobStorage storage, string jobId, Action<IJobConsole> body)
    {
        using IStorageConnection connection = storage.GetConnection();
        body(CreateContext(storage, connection, jobId).Console);
    }

    private static PerformContext CreateContext(JobStorage storage, IStorageConnection connection, string jobId)
    {
        JobData? data = connection.GetJobData(jobId);
        BackgroundJob job = new(jobId, data.Job, data.CreatedAt, data.ParametersSnapshot);
        return new PerformContext(storage, connection, job, new JobCancellationToken(false));
    }

    public static class ConsoleTestJob
    {
        public static void Noop()
        {
        }
    }
}
