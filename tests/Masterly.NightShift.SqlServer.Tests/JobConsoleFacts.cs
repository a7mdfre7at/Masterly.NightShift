using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
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
            var storage = CreateStorage();
            var jobId = CreateJob(storage);
            var errors = new ConcurrentQueue<Exception>();

            using (var connection = storage.GetConnection())
            using (connection.AcquireDistributedLock($"console-test:{jobId}", TimeSpan.FromSeconds(5)))
            {
                var console = CreateContext(storage, connection, jobId).Console;

                Parallel.For(0, 100, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    try
                    {
                        if (i % 2 == 0) console.WriteLine($"line {i}");
                        else console.SetProgress(i, $"step {i}");
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                });
            }

            Assert.Empty(errors);
            using (var connection = storage.GetConnection())
            {
                Assert.Equal(50, JobConsole.ReadEntries(connection, jobId).Count);
                Assert.NotNull(JobConsole.ReadProgress(connection, jobId));
            }
        }

        [Fact, CleanDatabase]
        public void Checkpoints_SurviveTheConsoleBeingTrimmed()
        {
            var storage = CreateStorage();
            var jobId = CreateJob(storage);
            var ran = 0;

            Perform(storage, jobId, console =>
            {
                console.Resume("download", () => ran++);
                for (var i = 0; i < JobConsole.DefaultMaxLines + 10; i++) console.WriteLine($"row {i}");
            });
            Perform(storage, jobId, console => console.Resume("download", () => ran++));

            Assert.Equal(1, ran);
        }

        [Fact, CleanDatabase]
        public void RequeuingASucceededJob_RunsItsResumeStepsAgain()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var jobId = CreateJob(storage);
            var ran = 0;

            Perform(storage, jobId, console => console.Resume("download", () => ran++));
            Assert.True(client.ChangeState(jobId, new SucceededState(null, 0, 0)));

            using (var connection = (JobStorageConnection)storage.GetConnection())
            {
                Assert.True(connection.GetListTtl(JobConsole.GetConsoleKey(jobId)) > TimeSpan.Zero);
                Assert.True(connection.GetListTtl(JobConsole.GetCheckpointsKey(jobId)) > TimeSpan.Zero);
            }

            Assert.True(client.Requeue(jobId));
            Perform(storage, jobId, console => console.Resume("download", () => ran++));

            Assert.Equal(2, ran);
            using (var connection = (JobStorageConnection)storage.GetConnection())
            {
                Assert.True(connection.GetListTtl(JobConsole.GetConsoleKey(jobId)) < TimeSpan.Zero);
                Assert.True(connection.GetListTtl(JobConsole.GetCheckpointsKey(jobId)) < TimeSpan.Zero);
            }
        }

        [Fact, CleanDatabase]
        public void RequeuingAFailedJob_SkipsTheStepsThatFinished()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var jobId = CreateJob(storage);
            var ran = 0;

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
            using (var connection = storage.GetConnection())
            {
                body(CreateContext(storage, connection, jobId).Console);
            }
        }

        private static PerformContext CreateContext(JobStorage storage, IStorageConnection connection, string jobId)
        {
            var data = connection.GetJobData(jobId);
            var job = new BackgroundJob(jobId, data.Job, data.CreatedAt, data.ParametersSnapshot);
            return new PerformContext(storage, connection, job, new JobCancellationToken(false));
        }

        public static class ConsoleTestJob
        {
            public static void Noop()
            {
            }
        }
    }
}
