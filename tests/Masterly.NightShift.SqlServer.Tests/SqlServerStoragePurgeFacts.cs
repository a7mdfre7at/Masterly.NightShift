using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
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

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_KeepsTheAuditLogAndDrainMode_ByDefault(bool useMicrosoftDataSqlClient)
        {
            var storage = CreateStorage(useMicrosoftDataSqlClient);
            WriteAuditLogDrainModeAndOtherData(storage);

            storage.Purge(new StoragePurgeOptions());

            using (var connection = (JobStorageConnection)storage.GetConnection())
            {
                Assert.Equal(new[] { "who-did-what" }, connection.GetAllItemsFromList(AuditLog.ListKey));
                Assert.Equal("maintenance", connection.GetAllEntriesFromHash(DrainMode.HashKey)["Queue:critical"]);
                Assert.Empty(connection.GetAllItemsFromList("activity"));
                Assert.Null(connection.GetAllEntriesFromHash("other-hash"));
                Assert.Equal("0 1 * * *", connection.GetAllEntriesFromHash("recurring-job:nightly")["Cron"]);
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_RemovesTheAuditLogAndDrainMode_WhenTheyAreNotKept(bool useMicrosoftDataSqlClient)
        {
            var storage = CreateStorage(useMicrosoftDataSqlClient);
            WriteAuditLogDrainModeAndOtherData(storage);

            storage.Purge(new StoragePurgeOptions { KeepAuditLog = false, KeepDrainMode = false });

            using (var connection = (JobStorageConnection)storage.GetConnection())
            {
                Assert.Empty(connection.GetAllItemsFromList(AuditLog.ListKey));
                Assert.Null(connection.GetAllEntriesFromHash(DrainMode.HashKey));
                Assert.Null(connection.GetAllEntriesFromHash("other-hash"));
                Assert.Equal("0 1 * * *", connection.GetAllEntriesFromHash("recurring-job:nightly")["Cron"]);
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_KeepsDrainMode_WithoutRecurringJobs(bool useMicrosoftDataSqlClient)
        {
            var storage = CreateStorage(useMicrosoftDataSqlClient);
            WriteAuditLogDrainModeAndOtherData(storage);

            storage.Purge(new StoragePurgeOptions { KeepRecurringJobs = false, KeepDrainMode = true, KeepAuditLog = false });

            using (var connection = (JobStorageConnection)storage.GetConnection())
            {
                Assert.Equal("maintenance", connection.GetAllEntriesFromHash(DrainMode.HashKey)["Queue:critical"]);
                Assert.Null(connection.GetAllEntriesFromHash("recurring-job:nightly"));
                Assert.Empty(connection.GetAllItemsFromSet("recurring-jobs"));
                Assert.Empty(connection.GetAllItemsFromList(AuditLog.ListKey));
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_DeletesEveryRow_WhenThereAreMoreRowsThanFitInOneBatch(bool useMicrosoftDataSqlClient)
        {
            var storage = CreateStorage(useMicrosoftDataSqlClient, options => options.DeleteExpiredBatchSize = 10);

            using (var sql = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                InsertJobs(sql, 25);
                Assert.Equal(25 + 75 + 50, CountJobRows(sql));

                storage.Purge(new StoragePurgeOptions());

                Assert.Equal(0, CountJobRows(sql));
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_DeletesEveryRow_WhenTheExistingConnectionHasNocountOn(bool useMicrosoftDataSqlClient)
        {
            using (var sql = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                // Storage transactions turn NOCOUNT on, and an existing connection keeps it
                using (var command = sql.CreateCommand())
                {
                    command.CommandText = "set nocount on;";
                    command.ExecuteNonQuery();
                }

                var storage = new SqlServerStorage(sql, new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, DeleteExpiredBatchSize = 10 });
                InsertJobs(sql, 25);

                storage.Purge(new StoragePurgeOptions());

                Assert.Equal(0, CountJobRows(sql));
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_KeepsWhatItAlreadyDeleted_WhenALaterBatchFails(bool useMicrosoftDataSqlClient)
        {
            var storage = CreateStorage(useMicrosoftDataSqlClient, options => options.CommandTimeout = TimeSpan.FromSeconds(1));

            using (var sql = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            using (var blocker = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                InsertJobs(sql, 5);

                using (var connection = storage.GetConnection())
                using (var transaction = connection.CreateWriteTransaction())
                {
                    transaction.SetRangeInHash("other-hash", new[] { new KeyValuePair<string, string>("a", "b") });
                    transaction.Commit();
                }

                // Another session holds a row of the [Hash] table, which Purge deletes after the jobs, so
                // the purge times out there
                using (var blockingTransaction = blocker.BeginTransaction())
                {
                    try
                    {
                        using (var command = blocker.CreateCommand())
                        {
                            command.Transaction = blockingTransaction;
                            command.CommandText = $"update [{Constants.DefaultSchema}].[Hash] set [Value] = N'locked' where [Key] = N'other-hash'";
                            command.ExecuteNonQuery();
                        }

                        Assert.ThrowsAny<DbException>(() => storage.Purge(new StoragePurgeOptions()));
                    }
                    finally
                    {
                        blockingTransaction.Rollback();
                    }
                }

                // The batches that ran before the failure aren't rolled back
                Assert.Equal(0, CountJobRows(sql));
            }
        }

        private static SqlServerStorage CreateStorage(bool useMicrosoftDataSqlClient, Action<SqlServerStorageOptions> configure = null)
        {
            var options = new SqlServerStorageOptions { PrepareSchemaIfNecessary = false };
            configure?.Invoke(options);

            return new SqlServerStorage(() => ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient), options);
        }

        private static void WriteAuditLogDrainModeAndOtherData(SqlServerStorage storage)
        {
            using (var connection = storage.GetConnection())
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.InsertToList(AuditLog.ListKey, "who-did-what");
                transaction.InsertToList("activity", "something-happened");
                transaction.SetRangeInHash(DrainMode.HashKey, new[] { new KeyValuePair<string, string>("Queue:critical", "maintenance") });
                transaction.SetRangeInHash("other-hash", new[] { new KeyValuePair<string, string>("a", "b") });
                transaction.AddToSet("recurring-jobs", "nightly", 1);
                transaction.SetRangeInHash("recurring-job:nightly", new[] { new KeyValuePair<string, string>("Cron", "0 1 * * *") });
                transaction.Commit();
            }
        }

        private static void InsertJobs(DbConnection connection, int count)
        {
            using (var command = connection.CreateCommand())
            {
                // Every job gets three states and two parameters
                command.CommandText = $@"
declare @i int = 0;
while @i < {count}
begin
    insert into [{Constants.DefaultSchema}].Job (StateName, InvocationData, Arguments, CreatedAt) values (N'Succeeded', N'', N'[]', getutcdate());
    declare @id bigint = scope_identity();
    insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt) values (@id, N'Enqueued', getutcdate()), (@id, N'Processing', getutcdate()), (@id, N'Succeeded', getutcdate());
    insert into [{Constants.DefaultSchema}].JobParameter (JobId, Name, Value) values (@id, N'CurrentCulture', N'en-US'), (@id, N'CurrentUICulture', N'en-US');
    set @i += 1;
end";
                command.ExecuteNonQuery();
            }
        }

        private static long CountJobRows(DbConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"select
    (select count_big(*) from [{Constants.DefaultSchema}].Job) +
    (select count_big(*) from [{Constants.DefaultSchema}].State) +
    (select count_big(*) from [{Constants.DefaultSchema}].JobParameter)";
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
