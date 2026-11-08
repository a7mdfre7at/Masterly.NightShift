extern alias ReferencedDapper;

using System;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using ReferencedDapper::Dapper;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    /// <summary>
    /// <see cref="ExpireAfterAttribute.IncludeFailed"/> against real storage: real state changes, then the
    /// <see cref="ExpirationManager"/>, then the monitoring API the dashboard reads.
    /// </summary>
    public class FailedJobRetentionFacts
    {
        [Fact, CleanDatabase]
        public void AFailedJob_ExpiresAfterItsRetries_AndLeavesNoGhostInTheFailedListOrCount()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var jobId = client.Create(() => RetentionTestJob.Flaky(), new ScheduledState(TimeSpan.FromDays(1)));
            WriteToConsole(storage, jobId);

            // The first failure is retried (ChangeState returns false: the job ended up Scheduled, not Failed)
            Assert.False(client.ChangeState(jobId, new FailedState(new InvalidOperationException("boom"))));
            Assert.Equal(ScheduledState.StateName, GetStateName(jobId));
            Assert.Null(GetExpireAt(jobId));

            // The retry is used up: the job stays failed, with an expiration set in the same transaction
            Assert.True(client.ChangeState(jobId, new FailedState(new InvalidOperationException("boom"))));
            Assert.Equal(FailedState.StateName, GetStateName(jobId));
            var expireAt = GetExpireAt(jobId);
            Assert.NotNull(expireAt);
            Assert.InRange(expireAt.Value, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
            using (var connection = (JobStorageConnection)storage.GetConnection())
            {
                Assert.True(connection.GetListTtl(JobConsole.GetConsoleKey(jobId)) > TimeSpan.Zero);
            }

            var monitoring = storage.GetMonitoringApi();
            Assert.Equal(1, monitoring.FailedCount());
            Assert.Equal(1, monitoring.GetStatistics().Failed);
            Assert.Contains(monitoring.FailedJobs(0, 10), x => x.Key == jobId);

            // A day later
            Backdate(jobId);
            new ExpirationManager(storage, TimeSpan.Zero, TimeSpan.FromTicks(1)).Execute(CancellationToken.None);

            Assert.Equal(0, monitoring.FailedCount());
            Assert.Equal(0, monitoring.GetStatistics().Failed);
            Assert.Empty(monitoring.FailedJobs(0, 10));
            using (var connection = storage.GetConnection())
            {
                Assert.Null(connection.GetJobData(jobId));
            }

            using (var sql = ConnectionUtils.CreateConnection(microsoftDataSqlClient: false))
            {
                var id = long.Parse(jobId);
                Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].State where JobId = @id", new { id }));
                Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id }));
                Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].List where [Key] = @key", new { key = JobConsole.GetConsoleKey(jobId) }));
            }
        }

        [Fact, CleanDatabase]
        public void AFailedJob_RequeuedBeforeItExpires_IsKept()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var jobId = client.Create(() => RetentionTestJob.FailsOnce(), new ScheduledState(TimeSpan.FromDays(1)));
            WriteToConsole(storage, jobId);

            Assert.True(client.ChangeState(jobId, new FailedState(new InvalidOperationException("boom"))));
            Assert.Equal(FailedState.StateName, GetStateName(jobId));
            Assert.NotNull(GetExpireAt(jobId));

            Assert.True(client.Requeue(jobId));

            Assert.Equal(EnqueuedState.StateName, GetStateName(jobId));
            Assert.Null(GetExpireAt(jobId));
            using (var connection = (JobStorageConnection)storage.GetConnection())
            {
                Assert.True(connection.GetListTtl(JobConsole.GetConsoleKey(jobId)) < TimeSpan.Zero);
            }

            new ExpirationManager(storage, TimeSpan.Zero, TimeSpan.FromTicks(1)).Execute(CancellationToken.None);

            using (var connection = storage.GetConnection())
            {
                Assert.Equal(EnqueuedState.StateName, connection.GetJobData(jobId)?.State);
            }

            Assert.Equal(0, storage.GetMonitoringApi().FailedCount());
        }

        private static SqlServerStorage CreateStorage()
        {
            return new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
        }

        private static void WriteToConsole(JobStorage storage, string jobId)
        {
            using (var connection = storage.GetConnection())
            {
                var data = connection.GetJobData(jobId);
                var job = new BackgroundJob(jobId, data.Job, data.CreatedAt, data.ParametersSnapshot);
                new PerformContext(storage, connection, job, new JobCancellationToken(false)).Console.WriteLine("calling the partner API");
            }
        }

        private static string GetStateName(string jobId)
        {
            using (var sql = ConnectionUtils.CreateConnection(microsoftDataSqlClient: false))
            {
                return sql.ExecuteScalar<string>($"select StateName from [{Constants.DefaultSchema}].Job where Id = @id", new { id = long.Parse(jobId) });
            }
        }

        private static DateTime? GetExpireAt(string jobId)
        {
            using (var sql = ConnectionUtils.CreateConnection(microsoftDataSqlClient: false))
            {
                return sql.ExecuteScalar<DateTime?>($"select ExpireAt from [{Constants.DefaultSchema}].Job where Id = @id", new { id = long.Parse(jobId) });
            }
        }

        private static void Backdate(string jobId)
        {
            using (var sql = ConnectionUtils.CreateConnection(microsoftDataSqlClient: false))
            {
                sql.Execute(
                    $@"update [{Constants.DefaultSchema}].Job set ExpireAt = dateadd(minute, -1, getutcdate()) where Id = @id;
update [{Constants.DefaultSchema}].List set ExpireAt = dateadd(minute, -1, getutcdate()) where [Key] = @key;",
                    new { id = long.Parse(jobId), key = JobConsole.GetConsoleKey(jobId) });
            }
        }

        public static class RetentionTestJob
        {
            [AutomaticRetry(Attempts = 1)]
            [ExpireAfter(days: 1, IncludeFailed = true)]
            public static void Flaky()
            {
            }

            [AutomaticRetry(Attempts = 0)]
            [ExpireAfter(days: 1, IncludeFailed = true)]
            public static void FailsOnce()
            {
            }
        }
    }
}
