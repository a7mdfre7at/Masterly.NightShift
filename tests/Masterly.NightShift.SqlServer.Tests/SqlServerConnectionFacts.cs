extern alias ReferencedDapper;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Server;
using ReferencedDapper::Dapper;
// ReSharper disable PossibleNullReferenceException

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerConnectionFacts
{
    private readonly Mock<IPersistentJobQueue> _queue;
    private readonly PersistentJobQueueProviderCollection _providers;

    public SqlServerConnectionFacts()
    {
        _queue = new Mock<IPersistentJobQueue>();

        Mock<IPersistentJobQueueProvider> provider = new();
        provider.Setup(x => x.GetJobQueue())
            .Returns(_queue.Object);

        _providers = new PersistentJobQueueProviderCollection(provider.Object);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerConnection(null));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void FetchNextJob_DelegatesItsExecution_ToTheQueue()
    {
        UseConnection(connection =>
        {
            CancellationToken token = new();
            string[] queues = ["default"];

            connection.FetchNextJob(queues, token);

            _queue.Verify(x => x.Dequeue(queues, token));
        });
    }

    [Fact, CleanDatabase]
    public void FetchNextJob_Throws_IfMultipleProvidersResolved()
    {
        UseConnection(connection =>
        {
            CancellationToken token = new();
            Mock<IPersistentJobQueueProvider> anotherProvider = new();
            _providers.Add(anotherProvider.Object, ["critical"]);

            Assert.Throws<InvalidOperationException>(
                () => connection.FetchNextJob(["critical", "default"], token));
        });
    }

    [Fact, CleanDatabase]
    public void CreateWriteTransaction_ReturnsNonNullInstance()
    {
        UseConnection(connection =>
        {
            IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            Assert.NotNull(transaction);
        });
    }

    [Fact, CleanDatabase]
    public void AcquireLock_ReturnsNonNullInstance()
    {
        UseConnection(connection =>
        {
            using IDisposable @lock = connection.AcquireDistributedLock("1", TimeSpan.FromSeconds(1));
            Assert.NotNull(@lock);
        });
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_ThrowsAnException_WhenResourceIsNullOrEmpty()
    {
        UseConnection(connection =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
            () => connection.AcquireDistributedLock("", TimeSpan.FromMinutes(5)));

            Assert.Equal("resource", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_AcquiresExclusiveApplicationLock_OnSession()
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        SqlServerStorage storage = new(sql);

        using (SqlServerConnection connection = new(storage))
        using (connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5)))
        {
            string lockMode = sql.Query<string>(
                $"select applock_mode('public', '{Constants.DefaultSchema}:hello', 'session')").Single();

            Assert.Equal("Exclusive", lockMode);
        }
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_ThrowsAnException_IfLockCanNotBeGranted()
    {
        using ManualResetEventSlim releaseLock = new(false);
        using ManualResetEventSlim lockAcquired = new(false);

        Thread thread = new(
            () => UseConnection(connection1 =>
            {
                using (connection1.AcquireDistributedLock("exclusive", TimeSpan.Zero))
                {
                    lockAcquired.Set();
                    releaseLock.Wait();
                }
            }));
        thread.Start();

        lockAcquired.Wait(TestContext.Current.CancellationToken);

        UseConnection(connection2 =>
        {
            Assert.Throws<DistributedLockTimeoutException>(
                () =>
                {
                    using (connection2.AcquireDistributedLock("exclusive", TimeSpan.FromSeconds(1)))
                    {
                    }
                });
        });

        releaseLock.Set();
        thread.Join();
    }

    // A pooled connection goes back to its pool with its session, and SQL Server resets the session only when the
    // pool hands the connection out again: a session lock still held stayed held until then
    [Fact, CleanDatabase]
    public void Dispose_LetsGoOfTheSessionLocks_WhenTheConnectionIsDisposedWhileHoldingThem()
    {
        SqlServerStorage storage = new(ConnectionUtils.GetConnectionString());
        using (SqlServerConnection connection = (SqlServerConnection)storage.GetConnection())
        {
            // The lock's handle is left undisposed: disposing the connection must let go of it
            connection.AcquireDistributedLock("bk:pooled", TimeSpan.FromSeconds(1));
        }

        Assert.True(CanTakeLockElsewhere($"{Constants.DefaultSchema}:bk:pooled"));
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_LetsGoOfTheLock_WhenItsReleaseFails()
    {
        SqlServerStorage storage = new(ConnectionUtils.GetConnectionString());
        using SqlServerConnection connection = (SqlServerConnection)storage.GetConnection();
        using IDisposable distributedLock = connection.AcquireDistributedLock("bk:release", TimeSpan.FromSeconds(1));

        // The release command can't run on a connection with a transaction it doesn't take part in
        using DbTransaction pending = connection.DedicatedConnection!.BeginTransaction(); // Set by the lock

        // Released here, while the transaction is pending; disposing it again at the end does nothing
        distributedLock.Dispose();

        Assert.True(CanTakeLockElsewhere($"{Constants.DefaultSchema}:bk:release"));
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_ClosesTheSession_WhenTheAcquireFailsOtherwiseThanByATimeout()
    {
        (int SessionId, DateTime ConnectTime) session = default;

        // SqlClient holds a transaction begun on a connection only weakly: once a collection reclaims it,
        // sp_getapplock runs again, so the test keeps it alive
        DbTransaction? pending = null;
        SqlServerStorage storage = new(() =>
        {
            DbConnection opened = ConnectionUtils.CreateConnection();
            session = opened.QuerySingle<(int, DateTime)>("select session_id, connect_time from sys.dm_exec_connections where session_id = @@spid");

            // sp_getapplock can't run on a connection with a transaction it doesn't take part in
            pending = opened.BeginTransaction();
            return opened;
        }, new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, TryAutoDetectSchemaDependentOptions = false });
        using SqlServerConnection connection = (SqlServerConnection)storage.GetConnection();

        Assert.Throws<InvalidOperationException>(() => connection.AcquireDistributedLock("bk:acquire", TimeSpan.FromSeconds(1)));

        Assert.True(PhysicalConnectionCloses(session.SessionId, session.ConnectTime));
        GC.KeepAlive(pending);
    }

    // Keeping a connection out of its pool clears the whole pool, so the common failure, a timeout, never does it
    [Fact, CleanDatabase]
    public void AcquireDistributedLock_KeepsTheSessionPooled_WhenTheAcquireTimesOut()
    {
        (int SessionId, DateTime ConnectTime) session = default;
        SqlServerStorage storage = new(() =>
        {
            DbConnection opened = ConnectionUtils.CreateConnection();
            session = opened.QuerySingle<(int, DateTime)>("select session_id, connect_time from sys.dm_exec_connections where session_id = @@spid");
            return opened;
        }, new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, TryAutoDetectSchemaDependentOptions = false });
        using DbConnection holder = OpenInAnotherPool();
        SqlServerDistributedLock.Acquire(holder, $"{Constants.DefaultSchema}:bk:timeout", TimeSpan.Zero);

        using (SqlServerConnection connection = (SqlServerConnection)storage.GetConnection())
        {
            Assert.Throws<DistributedLockTimeoutException>(() => connection.AcquireDistributedLock("bk:timeout", TimeSpan.FromSeconds(1)));
        }

        Assert.False(PhysicalConnectionCloses(session.SessionId, session.ConnectTime));
    }

    // Logging out takes the server a moment. A new connection may get the same session id, and a pooled one keeps
    // its connection when it is reused, so it is asked from another pool.
    private static bool PhysicalConnectionCloses(int sessionId, DateTime connectTime)
    {
        using DbConnection sql = OpenInAnotherPool();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (sql.ExecuteScalar<int>(
            "select count(*) from sys.dm_exec_connections where session_id = @sessionId and connect_time = @connectTime",
            new { sessionId, connectTime }) > 0)
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    // From a connection of another pool, without waiting
    private static bool CanTakeLockElsewhere(string resource)
    {
        using DbConnection other = OpenInAnotherPool();

        try
        {
            SqlServerDistributedLock.Acquire(other, resource, TimeSpan.Zero);
            SqlServerDistributedLock.Release(other, resource);
            return true;
        }
        catch (DistributedLockTimeoutException)
        {
            return false;
        }
    }

    private static DbConnection OpenInAnotherPool()
    {
        DbConnectionStringBuilder connectionString = new() { ConnectionString = ConnectionUtils.GetConnectionString() };
        connectionString["Application Name"] = "NightShift tests, another pool";
        Microsoft.Data.SqlClient.SqlConnection other = new(connectionString.ConnectionString);
        other.Open();
        return other;
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_KeepsLongResourcesApart_WhenTheyShareTheirFirst255Characters()
    {
        string sharedPrefix = new('r', 300);

        UseConnection(connection1 => UseConnection(connection2 =>
        {
            using (connection1.AcquireDistributedLock(sharedPrefix + "1", TimeSpan.FromSeconds(1)))
            using (connection2.AcquireDistributedLock(sharedPrefix + "2", TimeSpan.FromSeconds(1)))
            {
                Assert.Throws<DistributedLockTimeoutException>(
                    () => connection2.AcquireDistributedLock(sharedPrefix + "1", TimeSpan.FromSeconds(1)));
            }
        }));
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_ReleasesALongResource_OnDispose()
    {
        string resource = new('r', 300);

        UseConnection(connection1 => UseConnection(connection2 =>
        {
            connection1.AcquireDistributedLock(resource, TimeSpan.FromSeconds(1)).Dispose();

            using (connection2.AcquireDistributedLock(resource, TimeSpan.FromSeconds(1)))
            {
                Assert.True(true);
            }
        }));
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_Dispose_ReleasesExclusiveApplicationLock()
    {
        UseConnections((sql, connection) =>
        {
            IDisposable distributedLock = connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5));
            distributedLock.Dispose();

            string lockMode = sql.Query<string>(
                $"select applock_mode('public', '{Constants.DefaultSchema}:hello', 'session')").Single();

            Assert.Equal("NoLock", lockMode);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_IsReentrant_FromTheSameConnection_OnTheSameResource()
    {
        UseConnection(connection =>
        {
            using (connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5)))
            using (connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5)))
            {
                Assert.True(true);
            }
        });
    }

    [Fact, CleanDatabase]
    public void CreateExpiredJob_ThrowsAnException_WhenJobIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.CreateExpiredJob(
                    null,
                    new Dictionary<string, string>(),
                    DateTime.UtcNow,
                    TimeSpan.Zero));

            Assert.Equal("job", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void CreateExpiredJob_ThrowsAnException_WhenParametersCollectionIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("hello")),
                    null,
                    DateTime.UtcNow,
                    TimeSpan.Zero));

            Assert.Equal("parameters", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [InlineData(1), InlineData(5)]
    public void CreateExpiredJob_ThrowsArgumentException_InsteadOfTruncating_WhenAParameterNameIsTooLong(int parameterCount)
    {
        UseConnections((sql, connection) =>
        {
            Dictionary<string, string> parameters = Enumerable.Range(1, parameterCount - 1).ToDictionary(i => $"Key{i}", i => "Value");
            parameters.Add(new string('n', 41), "Value");

            ArgumentException exception = Assert.Throws<ArgumentException>(() => connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("hello")),
                parameters,
                DateTime.UtcNow,
                TimeSpan.FromDays(1)));

            Assert.Equal("name", exception.ParamName);
            Assert.Contains("at most 40 characters", exception.Message);
            Assert.Empty(sql.Query($"select * from [{Constants.DefaultSchema}].Job"));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void CreateExpiredJob_KeepsTheExpirationOnTheLastDayTheColumnHolds_WhenExpireInIsTooLong()
    {
        UseConnections((sql, connection) =>
        {
            connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string>(),
                DateTime.UtcNow,
                TimeSpan.MaxValue);

            dynamic sqlJob = sql.Query($"select * from [{Constants.DefaultSchema}].Job").Single();
            Assert.Equal(new DateTime(9999, 12, 31), sqlJob.ExpireAt);
        }, useBatching: false);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateParametersWithNonNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", "Value1" } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Equal("Value1", parameters["Key1"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateTwoParametersWithNonNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", "Value1" }, { "Key2", "Value2" } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Equal("Value1", parameters["Key1"]);
            Assert.Equal("Value2", parameters["Key2"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateThreeParametersWithNonNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", "Value1" }, { "Key2", "Value2" }, { "Key3", "Value3" } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Equal("Value1", parameters["Key1"]);
            Assert.Equal("Value2", parameters["Key2"]);
            Assert.Equal("Value3", parameters["Key3"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateFourParametersWithNonNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", "Value1" }, { "Key2", "Value2" }, { "Key3", "Value3" }, { "Key4", "Value4" } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Equal("Value1", parameters["Key1"]);
            Assert.Equal("Value2", parameters["Key2"]);
            Assert.Equal("Value3", parameters["Key3"]);
            Assert.Equal("Value4", parameters["Key4"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateManyParametersWithNonNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", "Value1" }, { "Key2", "Value2" }, { "Key3", "Value3" }, { "Key4", "Value4" }, { "Key5", "Value5" } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Equal("Value1", parameters["Key1"]);
            Assert.Equal("Value2", parameters["Key2"]);
            Assert.Equal("Value3", parameters["Key3"]);
            Assert.Equal("Value4", parameters["Key4"]);
            Assert.Equal("Value5", parameters["Key5"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateParametersWithNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", null } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Null(parameters["Key1"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateTwoParametersWithNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", null }, { "Key2", null } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Null(parameters["Key1"]);
            Assert.Null(parameters["Key2"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateThreeParametersWithNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", null }, { "Key2", null }, { "Key3", null } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Null(parameters["Key1"]);
            Assert.Null(parameters["Key2"]);
            Assert.Null(parameters["Key3"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateFourParametersWithNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", null }, { "Key2", null }, { "Key3", null }, { "Key4", null } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Null(parameters["Key1"]);
            Assert.Null(parameters["Key2"]);
            Assert.Null(parameters["Key3"]);
            Assert.Null(parameters["Key4"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateManyParametersWithNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", null }, { "Key2", null }, { "Key3", null }, { "Key4", null }, { "Key5", null } },
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Null(parameters["Key1"]);
            Assert.Null(parameters["Key2"]);
            Assert.Null(parameters["Key3"]);
            Assert.Null(parameters["Key4"]);
            Assert.Null(parameters["Key5"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CanCreateJobWithoutParameters(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string>(), 
                createdAt,
                TimeSpan.FromDays(1));

            Dictionary<string, string> parameters = sql
                .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                .ToDictionary(x => (string)x.Name, x => (string)x.Value);

            Assert.Empty(parameters);
        }, useBatching);
    }

    [Fact, CleanDatabase]
    public void GetJobData_ThrowsAnException_WhenJobIdIsNull()
    {
        UseConnection(
            connection => Assert.Throws<ArgumentNullException>(() => connection.GetJobData(null)));
    }

    [Fact, CleanDatabase]
    public void GetJobData_ReturnsNull_WhenThereIsNoSuchJob()
    {
        UseConnection(connection =>
        {
            JobData? result = connection.GetJobData("1");
            Assert.Null(result);
        });
    }
    
    [Fact, CleanDatabase]
    public void GetJobData_ReturnsNull_WhenIdentifierCanNotBeParsedAsLong()
    {
        UseConnection(connection =>
        {
            JobData? result = connection.GetJobData("some-non-long-id");
            Assert.Null(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetStateData_ThrowsAnException_WhenJobIdIsNull()
    {
        UseConnection(
            connection => Assert.Throws<ArgumentNullException>(() => connection.GetStateData(null)));
    }

    [Fact, CleanDatabase]
    public void GetStateData_ReturnsNull_IfThereIsNoSuchState()
    {
        UseConnection(connection =>
        {
            StateData? result = connection.GetStateData("1");
            Assert.Null(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetStateData_ReturnsNull_WhenIdentifierCanNotBeParsedAsLong()
    {
        UseConnection(connection =>
        {
            StateData? result = connection.GetStateData("some-non-long-id");
            Assert.Null(result);
        });
    }

    [Fact, CleanDatabase]
    public void SetParameter_ThrowsAnException_WhenJobIdIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.SetJobParameter(null, "name", "value"));

            Assert.Equal("id", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void SetParameter_ThrowsAnException_WhenNameIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.SetJobParameter("1", null, "value"));

            Assert.Equal("name", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void SetParameter_ThrowsArgumentException_WhenNameIsTooLong()
    {
        UseConnection(connection =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => connection.SetJobParameter("1", new string('n', 41), "value"));

            Assert.Equal("name", exception.ParamName);
            Assert.Contains("at most 40 characters", exception.Message);
        });
    }

    [Fact, CleanDatabase]
    public void SetParameter_StoresAName_OfExactly40Characters()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnections((sql, connection) =>
        {
            string jobId = sql.Query(arrangeSql).Single().Id.ToString();
            string name = new('n', 40);

            connection.SetJobParameter(jobId, name, "Value");

            Assert.Equal("Value", connection.GetJobParameter(jobId, name));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void SetParameters_CreatesNewParameter_WhenParameterWithTheGivenNameDoesNotExists()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnections((sql, connection) =>
        {
            dynamic job = sql.Query(arrangeSql).Single();
            string jobId = job.Id.ToString();

            connection.SetJobParameter(jobId, "Name", "Value");

            dynamic parameter = sql.Query(
                $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                new { id = jobId, name = "Name" }).Single();

            Assert.Equal("Value", parameter.Value);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void SetParameter_UpdatesValue_WhenParameterWithTheGivenName_AlreadyExists()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnections((sql, connection) =>
        {
            dynamic job = sql.Query(arrangeSql).Single();
            string jobId = job.Id.ToString();

            connection.SetJobParameter(jobId, "Name", "Value");
            connection.SetJobParameter(jobId, "Name", "AnotherValue");

            dynamic parameter = sql.Query(
                $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                new { id = jobId, name = "Name" }).Single();

            Assert.Equal("AnotherValue", parameter.Value);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void SetParameter_CanAcceptNulls_AsValues()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnections((sql, connection) =>
        {
            dynamic job = sql.Query(arrangeSql).Single();
            string jobId = job.Id.ToString();

            connection.SetJobParameter(jobId, "Name", null);

            dynamic parameter = sql.Query(
                $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                new { id = jobId, name = "Name" }).Single();

            string? value = parameter.Value;
            Assert.Null(value);
        }, useBatching: false);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void SetJobParameter_WithIgnoreDupKeyOption_InsertsNonExistingValue(bool useBatching)
    {
        try
        {
            UseConnections((sql, connection) =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = ON)");

                string jobId = sql.Query($@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt) values ('', '', getutcdate())
select scope_identity() as Id").Single().Id.ToString();

                connection.SetJobParameter(jobId, "Name", "Value");

                dynamic parameter = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @jobId and Name = N'Name'",
                    new { jobId }).Single();

                Assert.Equal("Value", parameter.Value);
            }, useBatching);
        }
        finally
        {
            UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = ON)"), useBatching);
        }
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void SetJobParameter_WithIgnoreDupKeyOption_UpdatesExistingValue_WhenIgnoreDupKeyOptionIsSet(bool setIgnoreDupKey)
    {
        try
        {
            UseConnections((sql, connection) =>
            {
                sql.Execute("SET XACT_ABORT ON");
                string onOrOff = setIgnoreDupKey ? "ON" : "OFF";
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = {onOrOff})");

                string jobId1 = sql.Query($@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt) values ('', '', getutcdate())
select scope_identity() as Id").Single().Id.ToString();

                string jobId2 = sql.Query($@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt) values ('', '', getutcdate())
select scope_identity() as Id").Single().Id.ToString();

                sql.Execute(
                    $@"insert into [{Constants.DefaultSchema}].[JobParameter] (JobId, Name, Value) values
(@jobId1, N'Name1', N'Value1'),
(@jobId1, N'Name2', N'Value1'),
(@jobId2, N'Name1', N'Value1')", new { jobId1, jobId2 });

                connection.SetJobParameter(jobId1, "Name1", "Value2");

                Dictionary<string, string> parameters1 = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @jobId1",
                    new { jobId1 }).ToDictionary(x => (string)x.Name, x => (string)x.Value);

                Assert.Equal("Value2", parameters1["Name1"]);
                Assert.Equal("Value1", parameters1["Name2"]);

                Dictionary<string, string> parameters2 = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @jobId2",
                    new { jobId2 }).ToDictionary(x => (string)x.Name, x => (string)x.Value);

                Assert.Equal("Value1", parameters2["Name1"]);
            }, useBatching: false);
        }
        finally
        {
            UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = ON)"), useBatching: false);
        }
    }

    [Fact, CleanDatabase]
    public void GetParameter_ThrowsAnException_WhenJobIdIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetJobParameter(null, "hello"));

            Assert.Equal("id", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetParameter_ThrowsAnException_WhenNameIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetJobParameter("1", null));

            Assert.Equal("name", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetParameter_ReturnsNull_WhenParameterDoesNotExists()
    {
        UseConnection(connection =>
        {
            string? value = connection.GetJobParameter("1", "hello");
            Assert.Null(value);
        });
    }

    [Fact, CleanDatabase]
    public void GetParameter_ReturnsNull_WhenJobIdCanNotBeParsedAsLong()
    {
        UseConnection(connection =>
        {
            string? result = connection.GetJobParameter("some-non-long-id", "name");
            Assert.Null(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetParameter_ReturnsParameterValue_WhenJobExists()
    {
        string arrangeSql = $@"
declare @id bigint
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
set @id = scope_identity()
insert into [{Constants.DefaultSchema}].JobParameter (JobId, Name, Value)
values (@id, @name, @value)
select @id";

        UseConnections((sql, connection) =>
        {
            long id = sql.Query<long>(
                arrangeSql,
                new { name = "name", value = "value" }).Single();

            string? value = connection.GetJobParameter(id.ToString(), "name");

            Assert.Equal("value", value);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetFirstByLowestScoreFromSet(null, 0, 1));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ThrowsAnException_ToScoreIsLowerThanFromScore()
    {
        UseConnection(connection =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => connection.GetFirstByLowestScoreFromSet("key", 0, -1));

            Assert.Equal("toScore", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ReturnsNull_WhenTheKeyDoesNotExist()
    {
        UseConnection(connection =>
        {
            string? result = connection.GetFirstByLowestScoreFromSet(
                "key", 0, 1);

            Assert.Null(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ThrowsArgException_WhenRequestingLessThanZero()
    {
        UseConnection(connection =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => connection.GetFirstByLowestScoreFromSet("key", 0, 1, -1));

            Assert.Equal("count", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ReturnsEmpty_WhenNoneExist()
    {
        UseConnection(connection =>
        {
            List<string> result = connection.GetFirstByLowestScoreFromSet("key", 0, 1, 10);
            Assert.Empty(result);
        });
    }
    
    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ReturnsN_WhenMoreThanNExist()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values 
('key', 1.0, '1234'),
('key', -1.0, '567'),
('key', -5.0, '890'),
('another-key', -2.0, 'abcd')";
        
        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql);

            List<string> result = connection.GetFirstByLowestScoreFromSet("key", -10.0, 10.0, 2);
            
            Assert.Equal(2, result.Count);
            Assert.Equal("890", result.ElementAt(0));
            Assert.Equal("567", result.ElementAt(1));
        }, useBatching: false);
    }
    
    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ReturnsN_WhenMoreThanNExist_And_RequestedCountIsGreaterThanN()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values 
('key', 1.0, '1234'),
('key', -1.0, '567'),
('key', -5.0, '890'),
('another-key', -2.0, 'abcd')";
        
        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql);

            List<string> result = connection.GetFirstByLowestScoreFromSet("another-key", -10.0, 10.0, 5);
            
            Assert.Single(result);
            Assert.Equal("abcd", result.First());
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetFirstByLowestScoreFromSet_ReturnsTheValueWithTheLowestScore()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values 
('key', 1.0, '1.0'),
('key', -1.0, '-1.0'),
('key', -5.0, '-5.0'),
('another-key', -2.0, '-2.0')";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql);

            string? result = connection.GetFirstByLowestScoreFromSet("key", -1.0, 3.0);
            
            Assert.Equal("-1.0", result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void AnnounceServer_ThrowsAnException_WhenServerIdIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.AnnounceServer(null, new ServerContext()));

            Assert.Equal("serverId", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void AnnounceServer_ThrowsAnException_WhenContextIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.AnnounceServer("server", null));

            Assert.Equal("context", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void AnnounceServer_CreatesOrUpdatesARecord()
    {
        UseConnections((sql, connection) =>
        {
            ServerContext context1 = new()
            {
                Queues = ["critical", "default"],
                WorkerCount = 4
            };
            connection.AnnounceServer("server", context1);

            dynamic server = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
            Assert.Equal("server", server.Id);
            string data = server.Data;
            Assert.True(data.StartsWith(
                "{\"WorkerCount\":4,\"Queues\":[\"critical\",\"default\"],\"StartedAt\":"),
                data);
            Assert.NotNull(server.LastHeartbeat);
            Assert.True(DateTime.UtcNow.AddHours(-1) < server.LastHeartbeat &&
                        server.LastHeartbeat < DateTime.UtcNow.AddHours(1));

            ServerContext context2 = new()
            {
                Queues = ["default"],
                WorkerCount = 1000 
            };
            connection.AnnounceServer("server", context2);
            dynamic sameServer = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
            Assert.Equal("server", sameServer.Id);
            Assert.Contains("1000", sameServer.Data);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void AnnounceServer_ThrowsAnException_WhenTheServerIdIsLongerThan200Characters()
    {
        UseConnections((sql, connection) =>
        {
            // A server name of 158 characters, the process id and a GUID: 201 characters
            string serverId = new string('s', 158) + ":12345:" + Guid.NewGuid();

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => connection.AnnounceServer(serverId, new ServerContext { Queues = ["default"], WorkerCount = 1 }));

            Assert.Equal("serverId", exception.ParamName);
            Assert.Contains("at most 200 characters", exception.Message);
            Assert.Contains("ServerName", exception.Message);
            Assert.Equal(0, sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].Server").Single());
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void AnnounceServer_AcceptsAServerIdOf200Characters()
    {
        UseConnections((sql, connection) =>
        {
            string serverId = new string('s', 157) + ":12345:" + Guid.NewGuid();
            Assert.Equal(200, serverId.Length);

            connection.AnnounceServer(serverId, new ServerContext { Queues = ["default"], WorkerCount = 1 });
            connection.Heartbeat(serverId);

            Assert.Equal(serverId, sql.Query<string>($"select Id from [{Constants.DefaultSchema}].Server").Single());
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void RemoveServer_ThrowsAnException_WhenServerIdIsNull()
    {
        UseConnection(
            connection => Assert.Throws<ArgumentNullException>(() => connection.RemoveServer(null)));
    }

    [Fact, CleanDatabase]
    public void RemoveServer_RemovesAServerRecord()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Server (Id, Data, LastHeartbeat)
values 
('Server1', '', getutcdate()),
('Server2', '', getutcdate())";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql);

            connection.RemoveServer("Server1");

            dynamic server = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
            Assert.NotEqual("Server1", server.Id, StringComparer.OrdinalIgnoreCase);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void Heartbeat_ThrowsAnException_WhenServerIdIsNull()
    {
        UseConnection(
            connection => Assert.Throws<ArgumentNullException>(() => connection.Heartbeat(null)));
    }

    [Fact, CleanDatabase]
    public void Heartbeat_UpdatesLastHeartbeat_OfTheServerWithGivenId()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Server (Id, Data, LastHeartbeat)
values
('server1', '', '2012-12-12 12:12:12'),
('server2', '', '2012-12-12 12:12:12')";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql);

            connection.Heartbeat("server1");

            Dictionary<string, DateTime> servers = sql.Query($"select * from [{Constants.DefaultSchema}].Server")
                .ToDictionary(x => (string)x.Id, x => (DateTime)x.LastHeartbeat);

            Assert.NotEqual(2012, servers["server1"].Year);
            Assert.Equal(2012, servers["server2"].Year);

            Assert.True(DateTime.UtcNow.AddHours(-1) < servers["server1"] &&
                        servers["server1"] < DateTime.UtcNow.AddHours(1));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void RemoveTimedOutServers_ThrowsAnException_WhenTimeOutIsNegative()
    {
        UseConnection(
            connection => Assert.Throws<ArgumentException>(() => connection.RemoveTimedOutServers(TimeSpan.FromMinutes(-5))));
    }

    [Fact, CleanDatabase]
    public void RemoveTimedOutServers_DoItsWorkPerfectly()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Server (Id, Data, LastHeartbeat)
values (@id, '', @heartbeat)";

        UseConnections((sql, connection) =>
        {
            sql.Execute(
                arrangeSql,
                new[]
                {
                    new { id = "server1", heartbeat = DateTime.UtcNow.AddDays(-1) },
                    new { id = "server2", heartbeat = DateTime.UtcNow.AddHours(-12) }
                });

            connection.RemoveTimedOutServers(TimeSpan.FromHours(15));

            dynamic liveServer = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
            Assert.Equal("server2", liveServer.Id);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetAllItemsFromSet_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(
            connection => Assert.Throws<ArgumentNullException>(() => connection.GetAllItemsFromSet(null)));
    }

    [Fact, CleanDatabase]
    public void GetAllItemsFromSet_ReturnsEmptyCollection_WhenKeyDoesNotExist()
    {
        UseConnection(connection =>
        {
            HashSet<string> result = connection.GetAllItemsFromSet("some-set");

            Assert.NotNull(result);
            Assert.Empty(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetAllItemsFromSet_ReturnsAllItems()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values (@key, 0.0, @value)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "some-set", value = "1" },
                new { key = "some-set", value = "2" },
                new { key = "another-set", value = "3" }
            });

            // Act
            HashSet<string> result = connection.GetAllItemsFromSet("some-set");

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Contains("1", result);
            Assert.Contains("2", result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void SetRangeInHash_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.SetRangeInHash(null, []));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.SetRangeInHash("some-hash", null));

            Assert.Equal("keyValuePairs", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void SetRangeInHash_ThrowsArgumentException_WhenKeyIsTooLong()
    {
        UseConnection(connection =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => connection.SetRangeInHash(
                "123456789_123456789_123456789_123456789_123456789_123456789_123456789_123456789_123456789_123456789_12345",
                new Dictionary<string, string> { { "field", "value" } }));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Fact, CleanDatabase]
    public void SetRangeInHash_ThrowsArgumentException_InsteadOfTruncating_WhenAFieldIsTooLong()
    {
        UseConnections((sql, connection) =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => connection.SetRangeInHash(
                    "some-hash",
                    new Dictionary<string, string> { { "short", "value" }, { new string('f', 101), "value" } }));

            Assert.Equal("field", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
            Assert.Empty(sql.Query($"select * from [{Constants.DefaultSchema}].Hash"));
        }, useBatching: false);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void SetRangeInHash_MergesAllRecords(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            connection.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                { "Key1", "Value1" },
                { "Key2", "Value2" }
            });

            Dictionary<string, string> result = sql.Query(
                $"select * from [{Constants.DefaultSchema}].Hash where [Key] = @key",
                new { key = "some-hash" })
                .ToDictionary(x => (string)x.Field, x => (string)x.Value);

            Assert.Equal("Value1", result["Key1"]);
            Assert.Equal("Value2", result["Key2"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void SetRangeInHash_CanCreateFieldsWithNullValues(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            connection.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                { "Key1", null }
            });

            Dictionary<string, string> result = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].Hash where [Key] = @key",
                    new { key = "some-hash" })
                .ToDictionary(x => (string)x.Field, x => (string)x.Value);

            Assert.Null(result["Key1"]);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void SetRangeInHash_ReleasesTheAcquiredLock(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            connection.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                { "Key", "Value" }
            });

            string result = sql.QuerySingle<string>($"select APPLOCK_MODE( 'public' , 'HangFire:Hash:Lock' , 'Session' )");
            Assert.Equal("NoLock", result);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void SetRangeInHash_WithIgnoreDupKeyOption_InsertsNonExistingValue(bool useBatching)
    {
        try
        {
            UseConnections((sql, connection) =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)");

                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "key", "value" }
                });

                Dictionary<string, string> result = sql
                    .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'some-hash'")
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal("value", result["key"]);
            }, useBatching);
        }
        finally
        {
            UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)"), useBatching);
        }
    }

    [Theory, CleanDatabase]
    [InlineData(false, false), InlineData(false, true)]
    [InlineData(true, false), InlineData(true, true)]
    public void SetRangeInHash_WithIgnoreDupKeyOption_UpdatesExistingValue_WhenIgnoreDupKeyOptionIsSet(bool setIgnoreDupKey, bool useBatching)
    {
        try
        {
            UseConnections((sql, connection) =>
            {
                string onOrOff = setIgnoreDupKey ? "ON" : "OFF";
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = {onOrOff})");
                sql.Execute($@"insert into [{Constants.DefaultSchema}].Hash([Key], Field, Value) VALUES
(N'some-hash', N'key1', N'value1'),
(N'some-hash', N'key2', N'value1'),
(N'othr-hash', N'key1', N'value1')");

                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "key1", "value2" }
                });

                Dictionary<string, string> someResult = sql
                    .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'some-hash'")
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal("value2", someResult["key1"]);
                Assert.Equal("value1", someResult["key2"]);

                Dictionary<string, string> othrResult = sql
                    .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'othr-hash'")
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal("value1", othrResult["key1"]);
            }, useBatching);
        }
        finally
        {
            UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)"), useBatching);
        }
    }

    [Fact, CleanDatabase]
    public void GetAllEntriesFromHash_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(
            connection => Assert.Throws<ArgumentNullException>(() => connection.GetAllEntriesFromHash(null)));
    }

    [Fact, CleanDatabase]
    public void GetAllEntriesFromHash_ReturnsNull_IfHashDoesNotExist()
    {
        UseConnection(connection =>
        {
            Dictionary<string, string>? result = connection.GetAllEntriesFromHash("some-hash");
            Assert.Null(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetAllEntriesFromHash_ReturnsAllKeysAndTheirValues()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [Value])
values (@key, @field, @value)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "some-hash", field = "Key1", value = "Value1" },
                new { key = "some-hash", field = "Key2", value = "Value2" },
                new { key = "another-hash", field = "Key3", value = "Value3" }
            });

            // Act
            Dictionary<string, string>? result = connection.GetAllEntriesFromHash("some-hash");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal("Value1", result["Key1"]);
            Assert.Equal("Value2", result["Key2"]);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetSetCount_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(
                () => connection.GetSetCount(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetSetCount_ReturnsZero_WhenSetDoesNotExist()
    {
        UseConnection(connection =>
        {
            long result = connection.GetSetCount("my-set");
            Assert.Equal(0, result);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetCount_ReturnsNumberOfElements_InASet()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@key, @value, 0.0)";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql, new List<dynamic>
            {
                new { key = "set-1", value = "value-1" },
                new { key = "set-2", value = "value-1" },
                new { key = "set-1", value = "value-2" }
            });

            long result = connection.GetSetCount("set-1");

            Assert.Equal(2, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetSetCount_Limited_ThrowsAnException_WhenKeysArgumentIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetSetCount(keys: null, 10));

            Assert.Equal("keys", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetCount_Limited_ThrowsAnException_WhenLimitIsNegative()
    {
        UseConnection(connection =>
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => connection.GetSetCount([], -10));

            Assert.Equal("limit", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetCount_Limited_ReturnsZero_WhenSetsArgumentIsEmpty()
    {
        UseConnection(connection =>
        {
            long result = connection.GetSetCount([], 10);
            Assert.Equal(0, result);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetCount_Limited_ReturnsZero_WhenGivenSetsDoNotExist()
    {
        UseConnection(connection =>
        {
            long result = connection.GetSetCount(new [] { "set-1", "set-2" }.AsEnumerable(), 10);
            Assert.Equal(0, result);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetCount_Limited_ReturnsTheSum_OfGivenSetCardinalities()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@Key, @Value, 0.0)";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql, new List<dynamic>
            {
                new { Key = "set-1", Value = "1" },
                new { Key = "set-1", Value = "2" },
                new { Key = "set-2", Value = "2" },
                new { Key = "set-2", Value = "3" },
                new { Key = "set-3", Value = "1" }
            });

            long result = connection.GetSetCount(new [] { "set-1", "set-2" }.AsEnumerable(), 10);
            Assert.Equal(4, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetSetCount_Limited_LimitValue_IsConsidered()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@Key, @Value, 0.0)";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql, new List<dynamic>
            {
                new { Key = "set-1", Value = "1" },
                new { Key = "set-1", Value = "2" },
                new { Key = "set-2", Value = "2" },
                new { Key = "set-2", Value = "3" },
                new { Key = "set-3", Value = "1" }
            });

            long result = connection.GetSetCount(new [] { "set-1", "set-2", "set-4" }.AsEnumerable(), 2);
            Assert.Equal(2, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetRangeFromSet_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(() => connection.GetRangeFromSet(null, 0, 1));
        });
    }

    [Fact, CleanDatabase]
    public void GetRangeFromSet_ReturnsPagedElements_SortedByScore()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@Key, @Value, @Score)";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql, new List<dynamic>
            {
                new { Key = "set-1", Value = "4", Score = 4.0D },
                new { Key = "set-2", Value = "4", Score = 4.0D },
                new { Key = "set-1", Value = "6", Score = 6.0D },
                new { Key = "set-1", Value = "2", Score = 2.0D },
                new { Key = "set-1", Value = "3", Score = 3.0D },
                new { Key = "set-1", Value = "5", Score = 5.0D },
                new { Key = "set-1", Value = "1", Score = 1.0D },
            });

            List<string> result = connection.GetRangeFromSet("set-1", 2, 4);

            Assert.Equal(new [] { "3", "4", "5" }, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetRangeFromSet_ReturnsTheSameOrder_ForARangeNearTheEndOfALongSet()
    {
        // Pairs of members share a score, and their values break the tie; ranges near the end are read from the end
        string arrangeSql = $@"
;with n as (select top (1500) row_number() over (order by (select null)) - 1 as i from sys.all_objects a cross join sys.all_objects b)
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
select N'long-set', right(N'000' + cast(i as nvarchar(10)), 4), i / 2 from n;";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql);

            Assert.Equal(new[] { "1495", "1496", "1497", "1498", "1499" }, connection.GetRangeFromSet("long-set", 1495, 1499));
            Assert.Equal(new[] { "1498", "1499" }, connection.GetRangeFromSet("long-set", 1498, 1510));
            Assert.Equal(new[] { "1100", "1101", "1102" }, connection.GetRangeFromSet("long-set", 1100, 1102));
            Assert.Equal(new[] { "0001", "0002", "0003" }, connection.GetRangeFromSet("long-set", 1, 3));
            Assert.Empty(connection.GetRangeFromSet("long-set", 1500, 2000));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetRangeFromSet_ReturnsTheSameOrder_ForARangeNearTheStartOfALongSet()
    {
        // The ranges end past the counting threshold, but the set is more than twice as long: the count stops
        // early and the rows are numbered from the start
        string arrangeSql = $@"
;with n as (select top (5000) row_number() over (order by (select null)) - 1 as i from sys.all_objects a cross join sys.all_objects b)
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
select N'longer-set', right(N'000' + cast(i as nvarchar(10)), 4), i / 2 from n;";

        UseConnections((sql, connection) =>
        {
            sql.Execute(arrangeSql);

            Assert.Equal(new[] { "1001", "1002", "1003" }, connection.GetRangeFromSet("longer-set", 1001, 1003));
            Assert.Equal(new[] { "2497", "2498", "2499" }, connection.GetRangeFromSet("longer-set", 2497, 2499));
            Assert.Equal(new[] { "2500", "2501" }, connection.GetRangeFromSet("longer-set", 2500, 2501));
            Assert.Equal(new[] { "4998", "4999" }, connection.GetRangeFromSet("longer-set", 4998, 4999));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetCounter_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(
                () => connection.GetCounter(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetSetContains_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetSetContains(null, "value"));
            
            Assert.Equal("key", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetContains_ThrowsAnException_WhenValueIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetSetContains("key", null));

            Assert.Equal("value", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetContains_ReturnsFalse_WhenGivenSetDoesNotExist()
    {
        UseConnection(connection =>
        {
            bool result = connection.GetSetContains("non-existing-set", "some-value");
            Assert.False(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetContains_ReturnsTrue_WhenGivenSetExists_AndContainsTheValue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@Key, @Value, 0.0)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new List<dynamic>
            {
                new { Key = "my-set", Value = "1" },
                new { Key = "my-set", Value = "2" },
            });

            // Act
            bool result = connection.GetSetContains("my-set", "2");

            // Assert
            Assert.True(result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetSetContains_ReturnsFalse_WhenGivenSetExists_ButContainsOtherValues()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@Key, @Value, 0.0)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new List<dynamic>
            {
                new { Key = "my-set", Value = "1" },
                new { Key = "my-set", Value = "2" },
            });

            // Act
            bool result = connection.GetSetContains("my-set", "3");

            // Assert
            Assert.False(result);
        }, useBatching: false);
    }
    
    [Fact, CleanDatabase]
    public void GetSetContains_ReturnsFalse_WhenAnotherSetContainsTheValue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@Key, @Value, 0.0)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new List<dynamic>
            {
                new { Key = "my-set", Value = "1" },
                new { Key = "another-set", Value = "2" },
            });

            // Act
            bool result = connection.GetSetContains("my-set", "2");

            // Assert
            Assert.False(result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetCounter_ReturnsZero_WhenKeyDoesNotExist()
    {
        UseConnection(connection =>
        {
            long result = connection.GetCounter("my-counter");
            Assert.Equal(0, result);
        });
    }

    [Fact, CleanDatabase]
    public void GetCounter_ReturnsSumOfValues_InCounterTable()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Counter ([Key], [Value])
values (@key, @value)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "counter-1", value = 1 },
                new { key = "counter-2", value = 1 },
                new { key = "counter-1", value = 1 }
            });

            // Act
            long result = connection.GetCounter("counter-1");

            // Assert
            Assert.Equal(2, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetCounter_IncludesValues_FromCounterAggregateTable()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].AggregatedCounter ([Key], [Value])
values (@key, @value)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "counter-1", value = 12 },
                new { key = "counter-2", value = 15 }
            });

            // Act
            long result = connection.GetCounter("counter-1");

            Assert.Equal(12, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetHashCount_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(() => connection.GetHashCount(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetHashCount_ReturnsZero_WhenKeyDoesNotExist()
    {
        UseConnection(connection =>
        {
            long result = connection.GetHashCount("my-hash");
            Assert.Equal(0, result);
        });
    }

    [Fact, CleanDatabase]
    public void GetHashCount_ReturnsNumber_OfHashFields()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field])
values (@key, @field)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "hash-1", field = "field-1" },
                new { key = "hash-1", field = "field-2" },
                new { key = "hash-2", field = "field-1" }
            });

            // Act
            long result = connection.GetHashCount("hash-1");

            // Assert
            Assert.Equal(2, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetHashTtl_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(
                () => connection.GetHashTtl(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetHashTtl_ReturnsNegativeValue_WhenHashDoesNotExist()
    {
        UseConnection(connection =>
        {
            TimeSpan result = connection.GetHashTtl("my-hash");
            Assert.True(result < TimeSpan.Zero);
        });
    }

    [Fact, CleanDatabase]
    public void GetHashTtl_ReturnsExpirationTimeForHash()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [ExpireAt])
values (@key, @field, @expireAt)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "hash-1", field = "field", expireAt = new DateTime?(DateTime.UtcNow.AddHours(1)) },
                new { key = "hash-2", field = "field", expireAt = default(DateTime?) }
            });

            // Act
            TimeSpan result = connection.GetHashTtl("hash-1");

            // Assert
            Assert.True(TimeSpan.FromMinutes(59) < result);
            Assert.True(result < TimeSpan.FromMinutes(61));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetListCount_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(
                () => connection.GetListCount(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetListCount_ReturnsZero_WhenListDoesNotExist()
    {
        UseConnection(connection =>
        {
            long result = connection.GetListCount("my-list");
            Assert.Equal(0, result);
        });
    }

    [Fact, CleanDatabase]
    public void GetListCount_ReturnsTheNumberOfListElements()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key])
values (@key)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "list-1" },
                new { key = "list-1" },
                new { key = "list-2" }
            });

            // Act
            long result = connection.GetListCount("list-1");

            // Assert
            Assert.Equal(2, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetListTtl_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(
                () => connection.GetListTtl(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetListTtl_ReturnsNegativeValue_WhenListDoesNotExist()
    {
        UseConnection(connection =>
        {
            TimeSpan result = connection.GetListTtl("my-list");
            Assert.True(result < TimeSpan.Zero);
        });
    }

    [Fact, CleanDatabase]
    public void GetListTtl_ReturnsExpirationTimeForList()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key], [ExpireAt])
values (@key, @expireAt)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "list-1", expireAt = new DateTime?(DateTime.UtcNow.AddHours(1)) },
                new { key = "list-2", expireAt = default(DateTime?) }
            });

            // Act
            TimeSpan result = connection.GetListTtl("list-1");

            // Assert
            Assert.True(TimeSpan.FromMinutes(59) < result);
            Assert.True(result < TimeSpan.FromMinutes(61));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetValueFromHash_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetValueFromHash(null, "name"));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetValueFromHash_ThrowsAnException_WhenNameIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetValueFromHash("key", null));

            Assert.Equal("name", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetValueFromHash_ReturnsNull_WhenHashDoesNotExist()
    {
        UseConnection(connection =>
        {
            string? result = connection.GetValueFromHash("my-hash", "name");
            Assert.Null(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetValueFromHash_ReturnsValue_OfAGivenField()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [Value])
values (@key, @field, @value)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "hash-1", field = "field-1", value = "1" },
                new { key = "hash-1", field = "field-2", value = "2" },
                new { key = "hash-2", field = "field-1", value = "3" }
            });

            // Act
            string? result = connection.GetValueFromHash("hash-1", "field-1");

            // Assert
            Assert.Equal("1", result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetRangeFromList_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => connection.GetRangeFromList(null, 0, 1));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void GetRangeFromList_ReturnsAnEmptyList_WhenListDoesNotExist()
    {
        UseConnection(connection =>
        {
            List<string> result = connection.GetRangeFromList("my-list", 0, 1);
            Assert.Empty(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetRangeFromList_ReturnsAllEntries_WithinGivenBounds()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key], [Value])
values (@key, @value)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "list-1", value = "1" },
                new { key = "list-2", value = "2" },
                new { key = "list-1", value = "3" },
                new { key = "list-1", value = "4" },
                new { key = "list-1", value = "5" }
            });

            // Act
            List<string> result = connection.GetRangeFromList("list-1", 1, 2);
            
            // Assert
            Assert.Equal(new [] { "4", "3" }, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetAllItemsFromList_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(
                () => connection.GetAllItemsFromList(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetAllItemsFromList_ReturnsAnEmptyList_WhenListDoesNotExist()
    {
        UseConnection(connection =>
        {
            List<string> result = connection.GetAllItemsFromList("my-list");
            Assert.Empty(result);
        });
    }

    [Fact, CleanDatabase]
    public void GetAllItemsFromList_ReturnsAllItems_FromAGivenList()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key], Value)
values (@key, @value)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "list-1", value = "1" },
                new { key = "list-2", value = "2" },
                new { key = "list-1", value = "3" }
            });

            // Act
            List<string> result = connection.GetAllItemsFromList("list-1");

            // Assert
            Assert.Equal(new [] { "3", "1" }, result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetSetTtl_ThrowsAnException_WhenKeyIsNull()
    {
        UseConnection(connection =>
        {
            Assert.Throws<ArgumentNullException>(() => connection.GetSetTtl(null));
        });
    }

    [Fact, CleanDatabase]
    public void GetSetTtl_ReturnsNegativeValue_WhenSetDoesNotExist()
    {
        UseConnection(connection =>
        {
            TimeSpan result = connection.GetSetTtl("my-set");
            Assert.True(result < TimeSpan.Zero);
        });
    }

    [Fact, CleanDatabase]
    public void GetSetTtl_ReturnsExpirationTime_OfAGivenSet()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [ExpireAt], [Score])
values (@key, @value, @expireAt, 0.0)";

        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "set-1", value = "1", expireAt = new DateTime?(DateTime.UtcNow.AddMinutes(60)) },
                new { key = "set-2", value = "2", expireAt = default(DateTime?) }
            });

            // Act
            TimeSpan result = connection.GetSetTtl("set-1");

            // Assert
            Assert.True(TimeSpan.FromMinutes(59) < result);
            Assert.True(result < TimeSpan.FromMinutes(61));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void CreateExpiredJob_HandlesJobIdCanExceedInt32Max()
    {
        UseConnections((sql, connection) =>
        {
            // Arrange
            sql.Query($"DBCC CHECKIDENT('[{Constants.DefaultSchema}].Job', RESEED, {int.MaxValue + 1L});");

            // Act
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string>(),
                createdAt,
                TimeSpan.FromDays(1));

            // Assert
            Assert.True(int.MaxValue < long.Parse(jobId));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void SetJobParameter_CreatesNewParameter_WhenJobIdIsLongValue()
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt)
values (@jobId, '', '', getutcdate())";

        UseConnections((sql, connection) =>
        {
            sql.Query(
                arrangeSql,
                new { jobId =  int.MaxValue + 1L});

            connection.SetJobParameter((int.MaxValue + 1L).ToString(), "Name", "Value");

            dynamic parameter = sql.Query(
                $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                new { id = int.MaxValue + 1L, name = "Name" }).Single();

            Assert.NotNull(parameter);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void SetJobParameter_DoesNothing_WhenTheJobIdIsNotANumber()
    {
        UseConnections((sql, connection) =>
        {
            connection.SetJobParameter("not-a-number", "Name", "Value");

            Assert.Empty(sql.Query($"select * from [{Constants.DefaultSchema}].JobParameter"));
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetJobParameter_ReturnsParameterValue_WhenJobIdIsLong()
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt)
values (@jobId, '', '', getutcdate())
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job OFF
insert into [{Constants.DefaultSchema}].JobParameter (JobId, Name, Value)
values (@jobId, @name, @value)";

        UseConnections((sql, connection) =>
        {
            sql.Query(
                arrangeSql, 
                new
                {
                    jobId = int.MaxValue + 1L,
                    name = "name", value = "value"
                });

            string? value = connection.GetJobParameter((int.MaxValue + 1L).ToString(), "name");

            Assert.Equal("value", value);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetUtcDateTime_ReturnsCurrentUtcDateTime()
    {
        UseConnection(connection =>
        {
            DateTime dateTime = connection.GetUtcDateTime();
            DateTime currentDateTime = DateTime.UtcNow;

            Assert.Equal(DateTimeKind.Utc, dateTime.Kind);
            Assert.True(currentDateTime.AddMinutes(-1) < dateTime, dateTime.ToString(CultureInfo.CurrentCulture));
            Assert.True(dateTime < currentDateTime.AddMinutes(1), dateTime.ToString(CultureInfo.CurrentCulture));
        });
    }

    [Fact, CleanSerializerSettings]
    public void HandlesChangingProcessOfStateDataSerialization()
    {
        GlobalConfiguration.Configuration.UseSerializerSettings(SerializerSettingsHelper.DangerousSettings);
        Dictionary<string, string> stateData = new()
        {
            { "key1", "value1" },
            { "key2", null }
        };
        string serializedData = SerializationHelper.Serialize(stateData, SerializationOption.User);

        Dictionary<string, string>? deserializedStateData = SerializationHelper.Deserialize<Dictionary<string, string>>(serializedData);

        Assert.NotNull(deserializedStateData);
        Assert.Equal(2, deserializedStateData.Count);

        Assert.Equal("value1", deserializedStateData["key1"]);
        Assert.Null(deserializedStateData["key2"]);
    }

    private void UseConnections(Action<DbConnection, SqlServerConnection> action, bool useBatching)
    {
        using DbConnection sqlConnection = ConnectionUtils.CreateConnection();
        SqlServerStorage storage = new(
            ConnectionUtils.CreateConnection,
            new SqlServerStorageOptions { CommandBatchMaxTimeout = useBatching ? TimeSpan.FromMinutes(1) : (TimeSpan?)null });

        using SqlServerConnection connection = new(storage);
        action(sqlConnection, connection);
    }

    private void UseConnection(Action<SqlServerConnection> action)
    {
        Mock<SqlServerStorage> storage = new((Func<DbConnection>)(() => ConnectionUtils.CreateConnection()));
        storage.Setup(x => x.QueueProviders).Returns(_providers);

        using SqlServerConnection connection = new(storage.Object);
        action(connection);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void SampleMethod(string arg){ }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void CreateExpiredJob_CreatesAJobInTheStorage_AndSetsItsParameters(bool useBatching)
    {
        UseConnections((sql, connection) =>
        {
            DateTime createdAt = new(2012, 12, 12);
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => SampleMethod("Hello")),
                new Dictionary<string, string> { { "Key1", "Value1" }, { "Key2", "Value2" }, { "Key3", "Value3" } },
                createdAt,
                TimeSpan.FromDays(1));

            Assert.NotNull(jobId);
            Assert.NotEmpty(jobId);

            dynamic sqlJob = sql.Query($"select * from [{Constants.DefaultSchema}].Job").Single();
            Assert.Equal(jobId, sqlJob.Id.ToString());
            Assert.Equal(createdAt, sqlJob.CreatedAt);
            Assert.Null((int?) sqlJob.StateId);
            string? stateName = sqlJob.StateName;
            Assert.Null(stateName);

            string payload = sqlJob.InvocationData;
            InvocationData invocationData = InvocationData.DeserializePayload(payload);
            invocationData.Arguments = sqlJob.Arguments;

            Job job = invocationData.DeserializeJob();
            Assert.Equal(typeof(SqlServerConnectionFacts), job.Type);
            Assert.Equal("SampleMethod", job.Method.Name);
            Assert.Equal("Hello", job.Args[0]);

            Assert.True(createdAt.AddDays(1).AddMinutes(-1) < sqlJob.ExpireAt);
            Assert.True(sqlJob.ExpireAt < createdAt.AddDays(1).AddMinutes(1));

            Dictionary<string, string> parameters = sql.Query(
                $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id",
                new { id = jobId })
                .ToDictionary(x => (string) x.Name, x => (string) x.Value);

            Assert.Equal("Value1", parameters["Key1"]);
            Assert.Equal("Value2", parameters["Key2"]);
            Assert.Equal("Value3", parameters["Key3"]);
        }, useBatching);
    }

    [Fact, CleanDatabase]
    public void GetJobData_ReturnsJobLoadException_IfThereWasADeserializationException()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values (@invocationData, @arguments, @stateName, getutcdate())
select scope_identity() as Id";

        UseConnections((sql, connection) =>
        {
            long jobId = sql.QuerySingle<long>(
                arrangeSql,
                new
                {
                    invocationData = TestJson.ToJson(new InvocationData(null, null, null, null)),
                    stateName = "Succeeded",
                    arguments = "[\"Arguments\"]"
                });

            JobData? result = connection.GetJobData(jobId.ToString());

            Assert.NotNull(result.LoadException);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetJobData_ReturnsResultWithParameters_WhenJobAndParametersExist()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values (@invocationData, @arguments, @stateName, getutcdate())
declare @id bigint = scope_identity();
insert into [{Constants.DefaultSchema}].JobParameter ([JobId], [Name], [Value])
values (@id, N'Param1', N'Value1'), (@id, N'Param2', 'Value2')
select @id as Id";

        UseConnections((sql, connection) =>
        {
            Job job = Job.FromExpression(() => SampleMethod("wrong"));

            long jobId = sql.QuerySingle<long>(
                arrangeSql,
                new
                {
                    invocationData = TestJson.ToJson(InvocationData.SerializeJob(job)),
                    stateName = "Succeeded",
                    arguments = "[\"Arguments\"]"
                });

            JobData? result = connection.GetJobData(jobId.ToString());

            Assert.NotNull(result);
            Assert.Equal("SampleMethod", result.Job.Method.Name);
            Assert.Equal(2, result.ParametersSnapshot.Count);
            Assert.Equal("Value1", result.ParametersSnapshot["Param1"]);
            Assert.Equal("Value2", result.ParametersSnapshot["Param2"]);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetJobData_ReturnsResult_WhenJobExists()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values (@invocationData, @arguments, @stateName, getutcdate())
select scope_identity() as Id";

        UseConnections((sql, connection) =>
        {
            Job job = Job.FromExpression(() => SampleMethod("wrong"));

            long jobId = sql.QuerySingle<long>(
                arrangeSql,
                new
                {
                    invocationData = TestJson.ToJson(InvocationData.SerializeJob(job)),
                    stateName = "Succeeded",
                    arguments = "[\"Arguments\"]"
                });

            JobData? result = connection.GetJobData(jobId.ToString());

            Assert.NotNull(result);
            Assert.NotNull(result.Job);
            Assert.Equal("Succeeded", result.State);
            Assert.Equal("Arguments", result.Job.Args[0]);
            Assert.Null(result.LoadException);
            Assert.True(DateTime.UtcNow.AddMinutes(-1) < result.CreatedAt);
            Assert.True(result.CreatedAt < DateTime.UtcNow.AddMinutes(1));
            Assert.Empty(result.ParametersSnapshot);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetJobData_ReturnsResult_WhenJobIdIsLongValue()
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON;
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, StateName, CreatedAt)
values (@jobId, @invocationData, '[""Arguments""]', 'Succeeded', getutcdate());";

        UseConnections((sql, connection) =>
        {
            Job job = Job.FromExpression(() => SampleMethod("hello"));

            sql.Query(
                arrangeSql,
                new
                {
                    jobId = int.MaxValue + 1L,
                    invocationData = TestJson.ToJson(InvocationData.SerializeJob(job)),
                });

            JobData? result = connection.GetJobData((int.MaxValue + 1L).ToString());

            Assert.NotNull(result);
            Assert.NotNull(result.Job);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetStateData_ReturnsCorrectData()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values ('', '', '', getutcdate());
declare @JobId bigint;
set @JobId = scope_identity();
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@JobId, 'old-state', getutcdate());
insert into [{Constants.DefaultSchema}].State (JobId, Name, Reason, Data, CreatedAt)
values (@JobId, @name, @reason, @data, getutcdate());
declare @StateId bigint;
set @StateId = scope_identity();
update [{Constants.DefaultSchema}].Job set StateId = @StateId;
select @JobId as Id;";

        UseConnections((sql, connection) =>
        {
            Dictionary<string, string> data = new()
            {
                { "Key", "Value" }
            };

            long jobId = sql.QuerySingle<long>(
                arrangeSql,
                new { name = "Name", reason = "Reason", @data = TestJson.ToJson(data) });

            StateData? result = connection.GetStateData(jobId.ToString());

            Assert.NotNull(result);

            Assert.Equal("Name", result.Name);
            Assert.Equal("Reason", result.Reason);
            Assert.Equal("Value", result.Data["Key"]);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetStateData_ReturnsCorrectData_WhenJobIdAndStateIdAreLongValues()
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, StateName, CreatedAt)
values (@jobId, '', '', '', getutcdate());
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@jobId, 'old-state', getutcdate());
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job OFF
SET IDENTITY_INSERT [{Constants.DefaultSchema}].State ON
insert into [{Constants.DefaultSchema}].State (Id, JobId, Name, Data, CreatedAt)
values (@stateId, @jobId, 'Name', @data, getutcdate());
update [{Constants.DefaultSchema}].Job set StateId = @stateId;";

        UseConnections((sql, connection) =>
        {
            Dictionary<string, string> data = new()
            {
                { "Key", "Value" }
            };

            sql.Query(
                arrangeSql,
                new
                {
                    jobId = int.MaxValue + 1L,
                    stateId = int.MaxValue + 1L,
                    data = TestJson.ToJson(data)
                });

            StateData? result = connection.GetStateData((int.MaxValue + 1L).ToString());

            Assert.NotNull(result);
        }, useBatching: false);
    }

    [Fact, CleanDatabase]
    public void GetStateData_ReturnsCorrectData_WhenPropertiesAreCamelcased()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values ('', '', '', getutcdate());
declare @JobId bigint;
set @JobId = scope_identity();
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@JobId, 'old-state', getutcdate());
insert into [{Constants.DefaultSchema}].State (JobId, Name, Reason, Data, CreatedAt)
values (@JobId, @name, @reason, @data, getutcdate());
declare @StateId bigint;
set @StateId = scope_identity();
update [{Constants.DefaultSchema}].Job set StateId = @StateId;
select @JobId as Id;";

        UseConnections((sql, connection) =>
        {
            Dictionary<string, string> data = new()
            {
                { "key", "Value" }
            };

            long jobId = sql.QuerySingle<long>(
                arrangeSql,
                new { name = "Name", reason = "Reason", @data = TestJson.ToJson(data) });

            StateData? result = connection.GetStateData(jobId.ToString());
            Assert.NotNull(result);

            Assert.Equal("Value", result.Data["Key"]);
        }, useBatching: false);
    }

    [Fact, CleanSerializerSettings]
    public void HandlesChangingProcessOfInvocationDataSerialization()
    {
        GlobalConfiguration.Configuration.UseSerializerSettings(SerializerSettingsHelper.DangerousSettings);

        Job initialJob = Job.FromExpression(() => Console.WriteLine());
        InvocationData invocationData = InvocationData.SerializeJob(initialJob);

        string serializedInvocationData = SerializationHelper.Serialize(invocationData, SerializationOption.User);

        InvocationData? deserializedStateData = SerializationHelper.Deserialize<InvocationData>(serializedInvocationData);
        Job deserializedJob = deserializedStateData.DeserializeJob();

        Assert.Equal(initialJob.Args, deserializedJob.Args);
        Assert.Equal(initialJob.Method, deserializedJob.Method);
        Assert.Equal(initialJob.Type, deserializedJob.Type);
    }
}
