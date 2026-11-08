extern alias ReferencedDapper;

using Moq;
using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using ReferencedDapper::Dapper;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    public class SqlServerStorageFacts
    {
        private readonly SqlServerStorageOptions _options;

        public SqlServerStorageFacts()
        {
            _options = new SqlServerStorageOptions { PrepareSchemaIfNecessary = false };
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenConnectionStringIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerStorage((string)null));

            Assert.Equal("nameOrConnectionString", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenOptionsValueIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerStorage("hello", null));

            Assert.Equal("options", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAClearException_WhenGivenANameThatIsNotConfigured()
        {
            var exception = Assert.Throws<ArgumentException>(
                () => new SqlServerStorage("NoSuchConnectionStringName", _options));

            Assert.Equal("nameOrConnectionString", exception.ParamName);
            Assert.Contains("'NoSuchConnectionStringName' looks like a connection string name", exception.Message);
        }

        [Fact]
        public void ResolveConnectionString_ReturnsAConnectionString_AsItIs()
        {
            var connectionString = "Server=.;Database=NightShift;Integrated Security=True;";

            Assert.Same(connectionString, SqlServerStorage.ResolveConnectionString(connectionString));
        }

#if NETFRAMEWORK
        [Fact]
        public void ResolveConnectionString_ReadsANamedConnectionString_FromTheConfigurationFile()
        {
            // App.config of this project holds it
            var connectionString = SqlServerStorage.ResolveConnectionString("NightShiftNamedConnection");

            Assert.Equal(@"Server=(localdb)\NamedConnectionTest;Database=NightShiftNamed;Integrated Security=True;", connectionString);
        }
#endif

        [Fact]
        public void JobExpirationTimeout_AcceptsTheLongestRetention()
        {
            var storage = new SqlServerStorage(() => new Mock<DbConnection>().Object, _options);

            storage.JobExpirationTimeout = TimeSpan.FromDays(ExpireAfterAttribute.MaxDays);

            Assert.Equal(TimeSpan.FromDays(ExpireAfterAttribute.MaxDays), storage.JobExpirationTimeout);
        }

        [Fact]
        public void JobExpirationTimeout_ThrowsAnException_WhenLongerThanTheLongestRetention()
        {
            var storage = new SqlServerStorage(() => new Mock<DbConnection>().Object, _options);

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => storage.JobExpirationTimeout = TimeSpan.MaxValue);

            Assert.Equal("value", exception.ParamName);
            Assert.Equal(TimeSpan.FromDays(1), storage.JobExpirationTimeout);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Ctor_CanCreateSqlServerStorage_WithExistingConnection(bool useMicrosoftDataSqlClient)
        {
            using (var connection = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                var storage = new SqlServerStorage(connection);
                Assert.NotNull(storage);
            }
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenConnectionFactoryIsNull()
        {
            Func<DbConnection> connectionFactory = null;

            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerStorage(connectionFactory));

            Assert.Equal("connectionFactory", exception.ParamName);
        }

        [Theory]
        [InlineData(false), InlineData(true)]
        public void Ctor_ThrowsAnException_WhenOptionsValueIsNull_WithConnectionFactory(bool useMicrosoftDataSqlClient)
        {
            Func<DbConnection> connectionFactory = () => ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient);
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerStorage(connectionFactory, null));

            Assert.Equal("options", exception.ParamName);
        }

        [Fact]
        public void CreateAndOpenConnection_UsesConnectionFactory()
        {
            var connection = new Mock<DbConnection>();
            var storage = new SqlServerStorage(() => connection.Object, _options);

            Assert.Same(connection.Object, storage.CreateAndOpenConnection());
        }

        [Fact, CleanDatabase]
        public void GetMonitoringApi_ReturnsNonNullInstance()
        {
            var storage = CreateStorage();
            var api = storage.GetMonitoringApi();
            Assert.NotNull(api);
        }

        [Fact, CleanDatabase]
        public void GetConnection_ReturnsNonNullInstance()
        {
            var storage = CreateStorage();
            using (var connection = (SqlServerConnection)storage.GetConnection())
            {
                Assert.NotNull(connection);
            }
        }

        [Fact, CleanDatabase]
        public void UseConnection_UsesMicrosoftDataSqlClient_ByDefault()
        {
            var storage = CreateStorage();
            storage.UseConnection(null, (_, connection) =>
            {
                Assert.IsType<Microsoft.Data.SqlClient.SqlConnection>(connection);
            });
        }

        [Fact, CleanDatabase]
        public void UseConnection_UsesSystemDataSqlClient_WhenSqlClientFactoryIsSet()
        {
            _options.SqlClientFactory = System.Data.SqlClient.SqlClientFactory.Instance;
            var storage = CreateStorage();
            storage.UseConnection(null, (_, connection) =>
            {
                Assert.IsType<System.Data.SqlClient.SqlConnection>(connection);
            });
        }

        [Fact, CleanDatabase]
        public void GetComponents_ReturnsAllNeededComponents()
        {
            var storage = CreateStorage();

            var components = storage.GetComponents();

            var componentTypes = components.Select(x => x.GetType()).ToArray();
            Assert.Contains(typeof(ExpirationManager), componentTypes);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Connection_GetUtcDateTime_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Connection.GetUtcDateTime");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Connection_GetSetContains_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Connection.GetSetContains");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Connection_GetSetCount_Limited_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Connection.GetSetCount.Limited");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Connection_BatchedGetFirstByLowestScoreFromSet_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Connection.BatchedGetFirstByLowestScoreFromSet");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Job_Queue_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Job.Queue");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Storage_ExtendedApi_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Storage.ExtendedApi");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Transaction_AcquireDistributedLock_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Transaction.AcquireDistributedLock");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Monitoring_AwaitingJobs_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Monitoring.AwaitingJobs");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void HasFeature_Monitoring_DeletedStateGraphs_ReturnsTrue()
        {
            var storage = CreateStorage();

            var result = storage.HasFeature("Monitoring.DeletedStateGraphs");

            Assert.True(result);
        }

        [Fact, CleanDatabase]
        public void Ctor_AutoDetectsTheSchemaDependentOptions_ThatWereNotSet()
        {
            // The test database is at the latest schema version
            var storage = CreateStorage();

            Assert.True(storage.DisableGlobalLocks);
            Assert.True(storage.UseIgnoreDupKeyOption);
            Assert.Equal(10000, storage.DeleteExpiredBatchSize);
            Assert.True(storage.Options.UseRecommendedIsolationLevel);
        }

        [Fact, CleanDatabase]
        public void Ctor_DoesNotWriteTheAutoDetectedValues_IntoTheOptionsObject()
        {
            CreateStorage();

            Assert.False(_options.DisableGlobalLocks);
            Assert.False(_options.UseIgnoreDupKeyOption);
            Assert.Equal(-1, _options.DeleteExpiredBatchSize);
        }

        [Fact, CleanDatabase]
        public void Ctor_KeepsTheSchemaDependentOptions_ThatWereSetExplicitly()
        {
            _options.DisableGlobalLocks = false;
            _options.UseIgnoreDupKeyOption = false;
            _options.DeleteExpiredBatchSize = 500;
            _options.UseRecommendedIsolationLevel = false;

            var storage = CreateStorage();

            Assert.False(storage.DisableGlobalLocks);
            Assert.False(storage.UseIgnoreDupKeyOption);
            Assert.Equal(500, storage.DeleteExpiredBatchSize);
            Assert.False(_options.DisableGlobalLocks);
            Assert.False(_options.UseIgnoreDupKeyOption);
            Assert.False(_options.UseRecommendedIsolationLevel);
        }

        [Fact, CleanDatabase]
        public void Ctor_UsesTheDefaults_WhenAutoDetectionIsTurnedOff()
        {
            _options.TryAutoDetectSchemaDependentOptions = false;

            var storage = CreateStorage();

            Assert.False(storage.DisableGlobalLocks);
            Assert.False(storage.UseIgnoreDupKeyOption);
            Assert.Equal(-1, storage.DeleteExpiredBatchSize);
        }

        [Fact, CleanDatabase]
        public void Ctor_UsesTheExplicitValues_WhenAutoDetectionIsTurnedOff()
        {
            _options.TryAutoDetectSchemaDependentOptions = false;
            _options.DisableGlobalLocks = true;
            _options.UseIgnoreDupKeyOption = true;

            var storage = CreateStorage();

            Assert.True(storage.DisableGlobalLocks);
            Assert.True(storage.UseIgnoreDupKeyOption);
        }

        [Fact, CleanDatabase]
        public void UseIgnoreDupKeyOption_StaysOff_OnASchemaOlderThan8_EvenWhenSet()
        {
            _options.UseIgnoreDupKeyOption = true;
            var storage = CreateStorage();

            // The test database is at the latest schema; pretend detection found version 7
            typeof(SqlServerStorage)
                .GetField("_detectedSchemaVersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(storage, 7);

            Assert.False(storage.UseIgnoreDupKeyOption);
        }

        [Fact, CleanDatabase]
        public void UseTransaction_UsesTheConfiguredIsolationLevel_WhenTheRecommendedOneIsTurnedOff()
        {
            _options.UseRecommendedIsolationLevel = false;
            _options.TransactionIsolationLevel = IsolationLevel.Serializable;
            var storage = CreateStorage();

            var level = storage.UseTransaction(null, static (_, connection, transaction, _) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "select transaction_isolation_level from sys.dm_exec_sessions where session_id = @@SPID";
                    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }, (object)null, null);

            // 4 is SERIALIZABLE in sys.dm_exec_sessions
            Assert.Equal(4, level);
        }

        [Fact, CleanDatabase]
        public void Ctor_WaitsBetweenInstallAttempts_WhenTheDatabaseIsNotReachableYet()
        {
            _options.PrepareSchemaIfNecessary = true;
            _options.TryAutoDetectSchemaDependentOptions = false;
            var attempts = 0;

            var stopwatch = Stopwatch.StartNew();
            var storage = new SqlServerStorage(() => Interlocked.Increment(ref attempts) <= 2
                ? throw new DatabaseStartingException()
                : ConnectionUtils.CreateConnection(true), _options);
            stopwatch.Stop();

            // The third attempt succeeds, after waiting 1 s and 3 s
            Assert.NotNull(storage);
            Assert.Equal(3, attempts);
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(3.9), $"Took {stopwatch.Elapsed}.");
        }

        // A factory can fail in its own way while the database is starting, for example while the token service it
        // asks for an access token isn't ready yet
        [Fact, CleanDatabase]
        public void Ctor_RetriesTheInstall_WhenTheConnectionFactoryThrowsANonDatabaseException()
        {
            _options.PrepareSchemaIfNecessary = true;
            _options.TryAutoDetectSchemaDependentOptions = false;
            var attempts = 0;

            var storage = new SqlServerStorage(() => Interlocked.Increment(ref attempts) <= 2
                ? throw new InvalidOperationException("The token service isn't ready yet.")
                : ConnectionUtils.CreateConnection(true), _options);

            Assert.False(storage.SchemaInstallPending);
            Assert.Equal(3, attempts);
        }

        [Fact, CleanDatabase]
        public void Ctor_DoesNotStop_WhenTheConnectionFactoryKeepsThrowingANonDatabaseException()
        {
            _options.PrepareSchemaIfNecessary = true;
            _options.TryAutoDetectSchemaDependentOptions = false;

            var storage = new SqlServerStorage(() => throw new InvalidOperationException("The token service isn't ready yet."), _options);

            Assert.True(storage.SchemaInstallPending);
        }

        [Fact]
        public void Ctor_ThrowsAtOnce_WhenNoSqlClientIsAvailable_EvenThoughTheInstallToleratesErrors()
        {
            var options = new SqlServerStorageOptions();

            // As when neither Microsoft.Data.SqlClient nor System.Data.SqlClient can be loaded
            typeof(SqlServerStorageOptions)
                .GetField("_sqlClientFactory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(options, null);

            var stopwatch = Stopwatch.StartNew();
            var exception = Assert.Throws<InvalidOperationException>(
                () => new SqlServerStorage(ConnectionUtils.GetConnectionString(), options));

            Assert.Contains("Microsoft.Data.SqlClient", exception.Message);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Took {stopwatch.Elapsed}.");
        }

        // After a failed open, the SQL client fails every open of a pooled connection string at once, with the same
        // error, for 5 seconds or more (its pool blocking period), so the retries 1 and 3 seconds later never reached
        // the server
        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Ctor_InstallRetriesReachTheServer_AfterAPooledConnectionFailedToOpen(bool useMicrosoftDataSqlClient)
        {
            // The database doesn't exist at the first attempt, and is created before the second one opens its connection
            var databaseName = ConnectionUtils.GetDatabaseName() + "_starting";
            var connectionString = new DbConnectionStringBuilder { ConnectionString = ConnectionUtils.GetConnectionString(databaseName) };
            connectionString["Pooling"] = true;
            // Otherwise the client retries the failed login itself, 10 seconds later
            connectionString["ConnectRetryCount"] = 0;

            var attempts = 0;
            var options = new SqlServerStorageOptions
            {
                TryAutoDetectSchemaDependentOptions = false,
                SqlClientFactory = useMicrosoftDataSqlClient
                    ? Microsoft.Data.SqlClient.SqlClientFactory.Instance
                    : System.Data.SqlClient.SqlClientFactory.Instance,
                // Called before each connection is opened
                ImpersonationFunc = () =>
                {
                    if (Interlocked.Increment(ref attempts) == 2) CreateDatabase(databaseName);
                    return null;
                }
            };

            DropDatabase(databaseName);

            try
            {
                var storage = new SqlServerStorage(connectionString.ConnectionString, options);

                Assert.False(storage.SchemaInstallPending);
                Assert.Equal(2, attempts);
            }
            finally
            {
                DropDatabase(databaseName);
            }
        }

        [Fact, CleanDatabase]
        public void SchemaDependentOptions_AreDetectedOnFirstUse_WhenTheDatabaseWasNotReachableAtStartup()
        {
            var attempts = 0;
            var storage = new SqlServerStorage(() => Interlocked.Increment(ref attempts) == 1
                ? throw new DatabaseStartingException()
                : ConnectionUtils.CreateConnection(true), _options);

            Assert.True(storage.DisableGlobalLocks);
            Assert.True(storage.UseIgnoreDupKeyOption);
            Assert.Equal(10000, storage.DeleteExpiredBatchSize);
            Assert.Equal(SqlServerObjectsInstaller.LatestSchemaVersion, storage.DetectedSchemaVersion);
            Assert.Equal(2, attempts);
        }

        [Fact, CleanDatabase]
        public void SchemaInstall_IsTriedAgainWhenTheStorageIsUsed_WhenTheDatabaseWasNotReachableAtStartup()
        {
            _options.PrepareSchemaIfNecessary = true;
            var reachable = 0;
            var attempts = 0;
            var storage = new SqlServerStorage(() =>
            {
                Interlocked.Increment(ref attempts);
                return Volatile.Read(ref reachable) == 1 ? ConnectionUtils.CreateConnection(true) : throw new DatabaseStartingException();
            }, _options);

            // The three startup attempts and the version detection failed
            Assert.True(storage.SchemaInstallPending);
            Assert.Equal(4, attempts);

            Volatile.Write(ref reachable, 1);

            // The first use reads the version again at once, but the install isn't due yet
            storage.GetConnection().Dispose();
            Assert.True(storage.SchemaInstallPending);
            Assert.Equal(5, attempts);

            MakeSchemaRetriesDue(storage);
            storage.GetConnection().Dispose();

            Assert.False(storage.SchemaInstallPending);
            Assert.Equal(SqlServerObjectsInstaller.LatestSchemaVersion, storage.DetectedSchemaVersion);
        }

        [Fact, CleanDatabase]
        public void SchemaInstall_WaitsLongerAfterEachFailedRetry()
        {
            _options.PrepareSchemaIfNecessary = true;
            _options.TryAutoDetectSchemaDependentOptions = false;
            var attempts = 0;
            var storage = new SqlServerStorage(() =>
            {
                Interlocked.Increment(ref attempts);
                throw new DatabaseStartingException();
            }, _options);
            Assert.Equal(3, attempts);

            MakeSchemaRetriesDue(storage);
            storage.GetConnection().Dispose();
            storage.GetConnection().Dispose();

            // One retry, which failed; the next one waits twice the first interval
            Assert.True(storage.SchemaInstallPending);
            Assert.Equal(4, attempts);
            Assert.Equal(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromTicks((long)typeof(SqlServerStorage)
                    .GetField("_schemaInstallRetryIntervalTicks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .GetValue(storage)));
        }

        [Fact, CleanDatabase]
        public void SchemaInstall_IsNotTriedAgain_WhenItSucceededAtStartup()
        {
            _options.PrepareSchemaIfNecessary = true;
            var storage = CreateStorage();

            Assert.False(storage.SchemaInstallPending);
        }

        private static void MakeSchemaRetriesDue(SqlServerStorage storage)
        {
            foreach (var field in new[] { "_nextSchemaInstallRetryTicks", "_nextSchemaVersionRetryTicks" })
            {
                typeof(SqlServerStorage)
                    .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(storage, 0L);
            }
        }

        [Fact, CleanDatabase]
        public void SchemaVersionDetection_IsNotRetriedOnEveryUse_WhileItKeepsFailing()
        {
            var attempts = 0;
            var storage = new SqlServerStorage(() => Interlocked.Increment(ref attempts) <= 2
                ? throw new DatabaseStartingException()
                : ConnectionUtils.CreateConnection(true), _options);

            // The first use tries again and fails; the next ones wait for the retry interval
            Assert.False(storage.DisableGlobalLocks);
            Assert.False(storage.UseIgnoreDupKeyOption);
            Assert.Equal(-1, storage.DeleteExpiredBatchSize);
            Assert.Null(storage.DetectedSchemaVersion);
            Assert.Equal(2, attempts);
        }

        [Fact, CleanDatabase]
        public void Ctor_InstallsAndUsesASchema_WhoseNameHasSpacesDashesAndBrackets()
        {
            const string schemaName = "Night Shift-[test";

            try
            {
                var storage = new SqlServerStorage(
                    ConnectionUtils.GetConnectionString(),
                    new SqlServerStorageOptions { SchemaName = schemaName });

                Assert.Equal(SqlServerObjectsInstaller.LatestSchemaVersion, storage.DetectedSchemaVersion);

                using (var connection = storage.GetConnection())
                {
                    using (var transaction = connection.CreateWriteTransaction())
                    {
                        transaction.AddToSet("my-set", "value");
                        transaction.Commit();
                    }

                    Assert.Contains("value", connection.GetAllItemsFromSet("my-set"));
                }
            }
            finally
            {
                DropSchema(schemaName);
            }
        }

        // The install script compared the name with non-Unicode literals, where characters outside the database's code
        // page become question marks: the install failed and rolled back on every attempt, and a schema created
        // beforehand failed it at CREATE SCHEMA
        [Fact, CleanDatabase]
        public void Ctor_InstallsAndUsesASchema_WhoseNameHasCharactersOutsideTheDatabaseCodePage()
        {
            const string schemaName = "Задания";

            using (var connection = ConnectionUtils.CreateConnection(true))
            {
                // The test database's collation (SQL_Latin1_General_CP1_CS_AS) has no Cyrillic in its code page
                var lostInNonUnicodeLiteral = connection.ExecuteScalar<int>(
                    $"select case when '{schemaName}' = N'{schemaName}' then 0 else 1 end");
                Assert.SkipUnless(lostInNonUnicodeLiteral == 1, "The database's code page can hold the schema name, so this test proves nothing here.");
            }

            try
            {
                // The second time, the schema is already there
                for (var i = 0; i < 2; i++)
                {
                    var storage = new SqlServerStorage(
                        ConnectionUtils.GetConnectionString(),
                        new SqlServerStorageOptions { SchemaName = schemaName });

                    Assert.False(storage.SchemaInstallPending);
                    Assert.Equal(SqlServerObjectsInstaller.LatestSchemaVersion, storage.DetectedSchemaVersion);

                    using (var connection = storage.GetConnection())
                    {
                        using (var transaction = connection.CreateWriteTransaction())
                        {
                            transaction.AddToSet("my-set", "value" + i.ToString(CultureInfo.InvariantCulture));
                            transaction.Commit();
                        }

                        Assert.Contains("value" + i.ToString(CultureInfo.InvariantCulture), connection.GetAllItemsFromSet("my-set"));
                    }
                }
            }
            finally
            {
                DropSchema(schemaName);
            }
        }

        private static void CreateDatabase(string databaseName)
        {
            using (var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionUtils.GetMasterConnectionString()))
            {
                connection.Execute(@"
declare @sql nvarchar(max) = N'create database ' + quotename(@name);
exec sp_executesql @sql;", new { name = databaseName });
            }
        }

        private static void DropDatabase(string databaseName)
        {
            using (var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionUtils.GetMasterConnectionString()))
            {
                connection.Execute(@"
declare @sql nvarchar(max) = N'alter database ' + quotename(@name) + N' set single_user with rollback immediate; drop database ' + quotename(@name);
if db_id(@name) is not null exec sp_executesql @sql;", new { name = databaseName });
            }
        }

        private static void DropSchema(string schemaName)
        {
            using (var connection = ConnectionUtils.CreateConnection(true))
            {
                // State and JobParameter first, because of their foreign keys to Job
                connection.Execute(@"
declare @sql nvarchar(max) = N'';
select @sql += N'drop table ' + quotename(s.name) + N'.' + quotename(t.name) + N';'
from sys.tables t join sys.schemas s on s.schema_id = t.schema_id
where s.name = @schema and t.name in (N'State', N'JobParameter');
exec sp_executesql @sql;
set @sql = N'';
select @sql += N'drop table ' + quotename(s.name) + N'.' + quotename(t.name) + N';'
from sys.tables t join sys.schemas s on s.schema_id = t.schema_id
where s.name = @schema;
exec sp_executesql @sql;
set @sql = N'drop schema ' + quotename(@schema);
if schema_id(@schema) is not null exec sp_executesql @sql;", new { schema = schemaName });
            }
        }

        private sealed class DatabaseStartingException : DbException
        {
            public DatabaseStartingException() : base("The database is starting.")
            {
            }
        }

        private SqlServerStorage CreateStorage()
        {
            return new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                _options);
        }
    }
}
