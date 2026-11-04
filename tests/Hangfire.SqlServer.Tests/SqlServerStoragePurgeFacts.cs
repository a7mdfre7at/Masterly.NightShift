using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Hangfire.Common;
using Hangfire.Storage;
using Xunit;

namespace Hangfire.SqlServer.Tests
{
    public class SqlServerStoragePurgeFacts
    {
        [Fact]
        public void Storage_ReportsThePurgeFeature()
        {
            using (var connection = ConnectionUtils.CreateConnection(false))
            {
                var storage = new SqlServerStorage(connection);
                Assert.True(storage.HasFeature(JobStorageFeatures.Purge));
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_ThrowsAnException_WhenOptionsAreNull(bool useMicrosoftDataSqlClient)
        {
            using (var connection = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                var storage = new SqlServerStorage(connection);
                Assert.Throws<ArgumentNullException>(() => storage.Purge(null));
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_RemovesJobs_AndKeepsRecurringJobsByDefault(bool useMicrosoftDataSqlClient)
        {
            using (var sqlConnection = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                var storage = new SqlServerStorage(sqlConnection);
                string jobId;

                using (var connection = storage.GetConnection())
                {
                    jobId = connection.CreateExpiredJob(Job.FromExpression(() => Sample()), new Dictionary<string, string> { { "p", "1" } }, DateTime.UtcNow, TimeSpan.FromDays(1));

                    using (var transaction = connection.CreateWriteTransaction())
                    {
                        transaction.AddToQueue("default", jobId);
                        transaction.AddToSet("my-set", "value");
                        transaction.AddToSet("recurring-jobs", "nightly", 1);
                        transaction.SetRangeInHash("recurring-job:nightly", new[] { new KeyValuePair<string, string>("Cron", "0 1 * * *") });
                        transaction.SetRangeInHash("other-hash", new[] { new KeyValuePair<string, string>("a", "b") });
                        transaction.InsertToList("my-list", "line");
                        transaction.IncrementCounter("my-counter");
                        transaction.Commit();
                    }

                    connection.AnnounceServer("server-1", new Server.ServerContext());
                }

                storage.Purge(new StoragePurgeOptions());

                using (var connection = (JobStorageConnection)storage.GetConnection())
                {
                    Assert.Null(connection.GetJobData(jobId));
                    Assert.Empty(connection.GetAllItemsFromSet("my-set"));
                    Assert.Null(connection.GetAllEntriesFromHash("other-hash"));
                    Assert.Empty(connection.GetAllItemsFromList("my-list"));
                    Assert.Equal(0, connection.GetCounter("my-counter"));

                    Assert.Contains("nightly", connection.GetAllItemsFromSet("recurring-jobs"));
                    Assert.Equal("0 1 * * *", connection.GetAllEntriesFromHash("recurring-job:nightly")["Cron"]);
                }

                Assert.Contains(storage.GetMonitoringApi().Servers(), server => server.Name == "server-1");
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_RemovesEverything_WhenNothingIsKept(bool useMicrosoftDataSqlClient)
        {
            using (var sqlConnection = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                var storage = new SqlServerStorage(sqlConnection);

                using (var connection = storage.GetConnection())
                {
                    using (var transaction = connection.CreateWriteTransaction())
                    {
                        transaction.AddToSet("recurring-jobs", "nightly", 1);
                        transaction.SetRangeInHash("recurring-job:nightly", new[] { new KeyValuePair<string, string>("Cron", "0 1 * * *") });
                        transaction.Commit();
                    }

                    connection.AnnounceServer("server-1", new Server.ServerContext());
                }

                storage.Purge(new StoragePurgeOptions { KeepRecurringJobs = false, KeepServers = false });

                using (var connection = (JobStorageConnection)storage.GetConnection())
                {
                    Assert.Empty(connection.GetAllItemsFromSet("recurring-jobs"));
                    Assert.Null(connection.GetAllEntriesFromHash("recurring-job:nightly"));
                }

                Assert.Empty(storage.GetMonitoringApi().Servers());
            }
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
