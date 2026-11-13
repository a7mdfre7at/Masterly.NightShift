extern alias ReferencedDapper;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Job parameters written in a transaction (<see cref="JobStorageTransaction.SetJobParameter"/>): on every way a
/// transaction is committed, next to the writes of the same parameter made outside a transaction, and with the
/// durability a parameter has there.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class TransactionalJobParameterFacts
{
    /// <summary>The ways a transaction reaches the database.</summary>
    public enum CommitPath
    {
        /// <summary>One command: <c>begin tran</c>, the commands with their parameters renamed, <c>commit tran</c>.</summary>
        OneRoundTrip,

        /// <summary>Too many commands for one: SqlClient's transaction and a command batch.</summary>
        CommandBatch,

        /// <summary>Batching turned off: SqlClient's transaction, command after command.</summary>
        CommandAfterCommand,

        /// <summary>A queue of another provider, which is given SqlClient's transaction.</summary>
        CustomQueue,
    }

    [Fact, CleanDatabase]
    public void Storage_SaysItSetsJobParametersInTransactions()
    {
        Assert.True(CreateStorage().HasFeature(JobStorageFeatures.Transaction.SetJobParameter));
    }

    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    [InlineData(CommitPath.CustomQueue)]
    public void SetJobParameter_AddsOverwritesAndClears_WithTheStateOfTheJob(CommitPath path)
    {
        SqlServerStorage storage = CreateStorage(path, out Mock<IPersistentJobQueue> customQueue);
        string id = CreateJob(storage, new Dictionary<string, string> { ["Existing"] = "old", ["Cleared"] = "value" });
        string other = CreateJob(storage, []);

        Commit(storage, path, transaction =>
        {
            // The state before the parameters, and the jobs out of order: the storage sorts them
            transaction.SetJobState(id, new ScheduledState(TimeSpan.FromHours(1)));
            transaction.SetJobParameter(other, "New", "of the other job");
            transaction.SetJobParameter(id, "New", "value");
            transaction.SetJobParameter(id, "Existing", "new");
            transaction.SetJobParameter(id, "Twice", "first");
            transaction.SetJobParameter(id, "Twice", "second");
            transaction.SetJobParameter(id, "Cleared", null);
            transaction.SetJobParameter(id, "Empty", null);
            transaction.SetJobParameter(id, new string('n', 40), new string('v', 5000));
        });

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Cleared"] = null,
                ["Empty"] = null,
                ["Existing"] = "new",
                ["New"] = "value",
                ["Twice"] = "second",
                [new string('n', 40)] = new string('v', 5000),
            },
            Parameters(id));
        Assert.Equal(new Dictionary<string, string> { ["New"] = "of the other job" }, Parameters(other));

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(ScheduledState.StateName, connection.GetStateData(id).Name);

        if (path == CommitPath.CustomQueue)
        {
            customQueue.Verify(x => x.Enqueue(It.IsNotNull<DbConnection>(), It.IsNotNull<DbTransaction>(), "custom", id), Times.Once);
        }
    }

    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    [InlineData(CommitPath.CustomQueue)]
    public void SetJobParameter_IsNotWritten_WhenALaterCommandOfTheTransactionFails(CommitPath path)
    {
        SqlServerStorage storage = CreateStorage(path, out _);
        string id = CreateJob(storage, new Dictionary<string, string> { ["Existing"] = "old" });

        using (IStorageConnection connection = storage.GetConnection())
        using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            Fill(transaction, path, id);
            transaction.SetJobParameter(id, "New", "value");
            transaction.SetJobParameter(id, "Existing", "new");

            // A state for a job that doesn't exist breaks the foreign key, after the parameters are written
            transaction.SetJobState("987654321", new EnqueuedState());

            Assert.Throws<SqlException>(transaction.Commit);
        }

        Assert.Equal(new Dictionary<string, string> { ["Existing"] = "old" }, Parameters(id));
    }

    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    [InlineData(CommitPath.CustomQueue)]
    public void SetJobParameter_OfAJobThatDoesNotExist_IsIgnored_AndTheRestOfTheTransactionIsCommitted(CommitPath path)
    {
        SqlServerStorage storage = CreateStorage(path, out _);
        string id = CreateJob(storage, []);

        // A job that never existed, and one that is gone with its parameters
        string gone = CreateJob(storage, new Dictionary<string, string> { ["Existing"] = "old" });
        using (DbConnection sql = ConnectionUtils.CreateConnection())
        {
            sql.Execute($"delete from [{Constants.DefaultSchema}].Job where Id = @id", new { id = long.Parse(gone) });
        }

        Commit(storage, path, transaction =>
        {
            transaction.SetJobParameter(gone, "Existing", "new");
            transaction.SetJobParameter(gone, "New", "value");
            transaction.SetJobParameter("987654321", "New", "value");
            transaction.SetJobParameter(id, "New", "value");
            transaction.SetJobState(id, new EnqueuedState());
            transaction.IncrementCounter("after-the-parameters");
        });

        Assert.Equal(new Dictionary<string, string> { ["New"] = "value" }, Parameters(id));

        using DbConnection reader = ConnectionUtils.CreateConnection();
        Assert.Equal(1, reader.ExecuteScalar<int>($"select count(distinct JobId) from [{Constants.DefaultSchema}].JobParameter"));
        Assert.Equal(1, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter"));
        Assert.Equal(1, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Job where StateName = N'Enqueued'"));
    }

    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    public void SetJobParameter_ThatFailsForAnotherReason_FailsTheCommit_WithTheErrorOfSqlServer(CommitPath path)
    {
        // Connections that give up after half a second on a row another session holds. They stay out of the
        // pool, so no other test is handed one
        SqlServerStorage storage = new(
            () =>
            {
                SqlConnection connection = new(new SqlConnectionStringBuilder(ConnectionUtils.GetConnectionString()) { Pooling = false }.ConnectionString);
                connection.Open();
                connection.Execute("set lock_timeout 500");
                return connection;
            },
            new SqlServerStorageOptions
            {
                PrepareSchemaIfNecessary = false,
                CommandBatchMaxTimeout = path == CommitPath.CommandAfterCommand ? null : TimeSpan.FromMinutes(5),
            });

        string first = CreateJob(storage, []);
        string second = CreateJob(storage, new Dictionary<string, string> { ["Held"] = "old" });

        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute(
            $"update [{Constants.DefaultSchema}].JobParameter set Value = N'held' where JobId = @id and Name = N'Held'",
            new { id = long.Parse(second) },
            held);

        using (IStorageConnection connection = storage.GetConnection())
        using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            Fill(transaction, path, first);
            transaction.SetJobParameter(first, "New", "value");
            transaction.SetJobParameter(second, "Held", "new");
            transaction.IncrementCounter("after-the-parameters");

            // The lock request timed out: thrown again with its number, and the commit never runs
            Assert.Equal(1222, Assert.Throws<SqlException>(transaction.Commit).Number);
        }

        held.Rollback();

        Assert.Empty(Parameters(first));
        Assert.Equal(new Dictionary<string, string> { ["Held"] = "old" }, Parameters(second));
        using DbConnection reader = ConnectionUtils.CreateConnection();
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter"));

        // The storage goes on, with no transaction left open on a connection
        Commit(storage, path, transaction => transaction.SetJobParameter(first, "New", "again"));
        Assert.Equal(new Dictionary<string, string> { ["New"] = "again" }, Parameters(first));
    }

    [Fact, CleanDatabase]
    public void SetJobParameter_ThrowsTheExceptionsOfTheConnection_ForItsArguments()
    {
        SqlServerStorage storage = CreateStorage();
        string id = CreateJob(storage, []);
        string tooLong = new('n', 41);

        using IStorageConnection connection = storage.GetConnection();
        using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();

        Assert.Equal("jobId", Assert.Throws<ArgumentNullException>(() => transaction.SetJobParameter(null, "Name", "value")).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => transaction.SetJobParameter(id, null, "value")).ParamName);

        ArgumentException fromConnection = Assert.Throws<ArgumentException>(() => connection.SetJobParameter(id, tooLong, "value"));
        ArgumentException fromTransaction = Assert.Throws<ArgumentException>(() => transaction.SetJobParameter(id, tooLong, "value"));
        Assert.Equal("name", fromTransaction.ParamName);
        Assert.Equal(fromConnection.Message, fromTransaction.Message);

        // Nothing was queued by the refused calls
        transaction.Commit();
        Assert.Empty(Parameters(id));
    }

    [Fact, CleanDatabase]
    public void SetJobParameter_DoesNothing_WhenTheJobIdIsNotANumber()
    {
        SqlServerStorage storage = CreateStorage();

        Commit(storage, CommitPath.OneRoundTrip, static transaction => transaction.SetJobParameter("not-a-number", "Name", "value"));

        using DbConnection sql = ConnectionUtils.CreateConnection();
        Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter"));
    }

    [Fact, CleanDatabase]
    public void CommitTogether_WritesTheParametersOfEveryTransaction()
    {
        SqlServerStorage storage = CreateStorage();
        string first = CreateJob(storage, new Dictionary<string, string> { ["Existing"] = "old" });
        string second = CreateJob(storage, []);

        using SqlServerConnection connection = (SqlServerConnection)storage.GetConnection();
        using SqlServerWriteOnlyTransaction one = new(connection);
        using SqlServerWriteOnlyTransaction other = new(connection);

        // Each transaction writes a state of one job and a parameter of the other
        one.SetJobState(first, new EnqueuedState());
        one.SetJobParameter(second, "New", "value");
        one.SetJobParameter(first, "Shared", "from one");
        other.SetJobState(second, new ScheduledState(TimeSpan.FromHours(1)));
        other.SetJobParameter(first, "Existing", "new");
        other.SetJobParameter(first, "Shared", "from the other");

        SqlServerWriteOnlyTransaction.CommitTogether(storage, [one, other]);

        Assert.Equal(new Dictionary<string, string> { ["Existing"] = "new", ["Shared"] = "from the other" }, Parameters(first));
        Assert.Equal(new Dictionary<string, string> { ["New"] = "value" }, Parameters(second));
        Assert.Equal(EnqueuedState.StateName, connection.GetStateData(first).Name);
        Assert.Equal(ScheduledState.StateName, connection.GetStateData(second).Name);
    }

    [Fact, CleanDatabase]
    public void CommitTogether_WritesNothing_WhenACommandOfOneTransactionFails()
    {
        SqlServerStorage storage = CreateStorage();
        string id = CreateJob(storage, []);

        using SqlServerConnection connection = (SqlServerConnection)storage.GetConnection();
        using SqlServerWriteOnlyTransaction one = new(connection);
        using SqlServerWriteOnlyTransaction other = new(connection);

        one.SetJobParameter(id, "New", "value");
        one.SetJobState(id, new EnqueuedState());
        // A state for a job that doesn't exist breaks the foreign key, after the parameters are written
        other.SetJobState("987654321", new EnqueuedState());

        Assert.Throws<SqlException>(() => SqlServerWriteOnlyTransaction.CommitTogether(storage, [one, other]));

        Assert.Empty(Parameters(id));
        Assert.Null(connection.GetStateData(id));
    }

    [Theory, CleanDatabase]
    [InlineData(1)]
    [InlineData(8)]
    public void SetJobParameter_FromManyCallersAtOnce_WritesEveryParameter(int maxConcurrentWriters)
    {
        // With one writer at a time, the callers that wait are committed together
        SqlServerStorage storage = CreateStorage(x => x.MaxConcurrentWriters = maxConcurrentWriters);
        string[][] ids = new string[16][];

        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = ids.Length }, caller =>
        {
            ids[caller] = new string[25];
            for (int i = 0; i < ids[caller].Length; i++)
            {
                string id = ids[caller][i] = CreateJob(storage, []);

                Commit(storage, CommitPath.OneRoundTrip, transaction =>
                {
                    transaction.SetJobState(id, new EnqueuedState());
                    transaction.SetJobParameter(id, "Caller", caller.ToString());
                    transaction.SetJobParameter(id, "Number", i.ToString());
                });
            }
        });

        using DbConnection sql = ConnectionUtils.CreateConnection();
        Assert.Equal(800, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter"));
        Assert.Equal(400, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Job where StateName = N'Enqueued'"));

        for (int caller = 0; caller < ids.Length; caller++)
        {
            Assert.Equal(
                new Dictionary<string, string> { ["Caller"] = caller.ToString(), ["Number"] = "24" },
                Parameters(ids[caller][24]));
        }
    }

    [Fact, CleanDatabase]
    public void SetJobParameter_OfANewParameter_InTransactionsAndOutsideThemAtOnce_NeverFails()
    {
        // Every caller finds no parameter to update and inserts it: all but one find it inserted meanwhile
        SqlServerStorage storage = CreateStorage();
        const int callers = 8;
        ConcurrentQueue<Exception> failures = new();

        for (int round = 0; round < 40; round++)
        {
            string id = CreateJob(storage, []);
            using Barrier barrier = new(callers);

            // Threads of their own: every caller is at the barrier before any of them writes
            Thread[] threads = [.. Enumerable.Range(0, callers).Select(caller => new Thread(() =>
            {
                try
                {
                    using IStorageConnection connection = storage.GetConnection();
                    using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
                    transaction.SetJobParameter(id, "Shared", caller.ToString());
                    barrier.SignalAndWait(TimeSpan.FromSeconds(30));

                    if (caller % 2 == 0)
                    {
                        transaction.Commit();
                    }
                    else
                    {
                        connection.SetJobParameter(id, "Shared", caller.ToString());
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }))];

            foreach (Thread thread in threads)
            {
                thread.Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }

            Assert.Empty(failures);
            Assert.Contains(Assert.Single(Parameters(id)).Value, Enumerable.Range(0, callers).Select(static x => x.ToString()));
        }
    }

    [Fact, CleanDatabase]
    public async Task SetJobParameter_WithAStateOfItsJob_DoesNotDeadlock_WithTheSameNewParameterWrittenOutsideATransaction()
    {
        // Outside a transaction a new parameter locks its row, then reads the row of its job (the foreign key). A
        // transaction that wrote the state first, locking the row of the job, and the parameter second would wait
        // for the parameter's row while the other session waits for the job's: a deadlock. So a transaction
        // writes its parameters first.
        SqlServerStorage storage = CreateStorage();
        string first = CreateJob(storage, []);
        string second = CreateJob(storage, []);

        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();

        // The transaction stops in the middle, at the second job, whose row another session holds: after the
        // state of the first job, and its parameter if parameters come first
        holder.Execute($"update [{Constants.DefaultSchema}].Job set ExpireAt = ExpireAt where Id = @id", new { id = long.Parse(second) }, held);

        Task inTransaction = Task.Run(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
            transaction.SetJobState(first, new EnqueuedState());
            transaction.SetJobState(second, new EnqueuedState());
            transaction.SetJobParameter(first, "Shared", "transaction");
            transaction.Commit();
        }, TestContext.Current.CancellationToken);
        WaitForSessionsThatWait(1);

        // Meanwhile the same parameter is written outside a transaction: it waits for the transaction
        Task outside = Task.Run(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            connection.SetJobParameter(first, "Shared", "connection");
        }, TestContext.Current.CancellationToken);
        WaitForSessionsThatWait(2);

        held.Rollback();

        // Neither is chosen as the victim of a deadlock
        await Task.WhenAll(inTransaction, outside).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(new Dictionary<string, string> { ["Shared"] = "connection" }, Parameters(first));

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal(EnqueuedState.StateName, reader.GetStateData(first).Name);
        Assert.Equal(EnqueuedState.StateName, reader.GetStateData(second).Name);
    }

    [Fact, CleanDatabase]
    public void Transaction_ThatOnlyMovesJobsToProcessing_WaitsForTheDisk_OnceItWritesAJobParameter()
    {
        SqlServerStorage storage = CreateStorage();
        string id = CreateJob(storage, []);

        using SqlServerConnection connection = (SqlServerConnection)storage.GetConnection();
        using SqlServerWriteOnlyTransaction processing = new(connection);
        using SqlServerWriteOnlyTransaction withParameter = new(connection);
        using SqlServerWriteOnlyTransaction parameterOnly = new(connection);

        processing.SetJobState(id, Processing());
        withParameter.SetJobState(id, Processing());
        withParameter.SetJobParameter(id, "Lease", "worker");
        parameterOnly.SetJobParameter(id, "Lease", "worker");

        // A lost Processing state is written again when the job is fetched again; a parameter is not
        Assert.True(processing.CommitsWithoutWaitingForTheDisk);
        Assert.False(withParameter.CommitsWithoutWaitingForTheDisk);
        Assert.False(parameterOnly.CommitsWithoutWaitingForTheDisk);
    }

    [Fact, CleanDatabase]
    public void SetJobParameter_IsWritten_WhenDelayedDurabilityIsAllowed()
    {
        SqlServerStorage storage = CreateStorage(static x => x.AllowDelayedDurability = true);

        try
        {
            Assert.True(storage.UseDelayedDurability);
            string id = CreateJob(storage, []);

            Commit(storage, CommitPath.OneRoundTrip, transaction =>
            {
                transaction.SetJobState(id, Processing());
                transaction.SetJobParameter(id, "Lease", "worker");
            });

            Assert.Equal(new Dictionary<string, string> { ["Lease"] = "worker" }, Parameters(id));
            using IStorageConnection connection = storage.GetConnection();
            Assert.Equal(ProcessingState.StateName, connection.GetStateData(id).Name);
        }
        finally
        {
            // The other tests run with the setting a new database has
            using DbConnection sql = ConnectionUtils.CreateConnection();
            sql.Execute("alter database current set delayed_durability = disabled");
        }
    }

    /// <summary>
    /// Waits until that many sessions wait for a lock in the test database, so a test goes on once the sessions
    /// it started are where it wants them. Where the locks can't be listed, it waits a moment instead.
    /// </summary>
    private static void WaitForSessionsThatWait(int count)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            while (sql.ExecuteScalar<int>(
                "select count(distinct request_session_id) from sys.dm_tran_locks where request_status = N'WAIT' and resource_database_id = db_id()") < count)
            {
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"{count} sessions should be waiting for a lock.");
                Thread.Sleep(20);
            }
        }
        catch (SqlException)
        {
            // No permission to see the locks
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A Processing state: only workers create the real one.</summary>
    private static IState Processing()
    {
        Mock<IState> state = new();
        state.Setup(x => x.Name).Returns(ProcessingState.StateName);
        state.Setup(x => x.SerializeData()).Returns(new Dictionary<string, string> { ["ServerId"] = "server", ["WorkerId"] = "worker" });
        return state.Object;
    }

    public static void Run()
    {
    }

    /// <summary>Writes with the transaction, with what makes it take <paramref name="path"/>, and commits it.</summary>
    private static void Commit(SqlServerStorage storage, CommitPath path, Action<JobStorageTransaction> write)
    {
        using IStorageConnection connection = storage.GetConnection();
        using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
        write(transaction);

        if (path is CommitPath.CommandBatch or CommitPath.CustomQueue)
        {
            // The job the fillers are written to, or a custom queue is given: the first job of the test
            using DbConnection sql = ConnectionUtils.CreateConnection();
            Fill(transaction, path, sql.ExecuteScalar<long>($"select min(Id) from [{Constants.DefaultSchema}].Job").ToString());
        }

        transaction.Commit();
    }

    /// <summary>Adds what makes a transaction take <paramref name="path"/> when it is committed.</summary>
    private static void Fill(JobStorageTransaction transaction, CommitPath path, string jobId)
    {
        switch (path)
        {
            case CommitPath.CommandBatch:
                // More commands than one command carries
                for (int i = 0; i < 100; i++)
                {
                    transaction.SetJobParameter(jobId, $"Filler{i:D3}", null);
                }

                break;

            case CommitPath.CustomQueue:
                transaction.AddToQueue("custom", jobId);
                break;
        }
    }

    /// <summary>The parameters of a job as the table has them, without the ones <see cref="Fill"/> wrote.</summary>
    private static Dictionary<string, string> Parameters(string jobId)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        return sql
            .Query<(string Name, string Value)>(
                $"select Name, Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name not like N'Filler%'",
                new { id = long.Parse(jobId) })
            .ToDictionary(static x => x.Name, static x => x.Value);
    }

    private static string CreateJob(SqlServerStorage storage, Dictionary<string, string> parameters)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => Run()), parameters, DateTime.UtcNow, TimeSpan.FromHours(1));
    }

    private static SqlServerStorage CreateStorage(CommitPath path, out Mock<IPersistentJobQueue> customQueue)
    {
        SqlServerStorage storage = CreateStorage(x => x.CommandBatchMaxTimeout = path == CommitPath.CommandAfterCommand ? null : TimeSpan.FromMinutes(5));

        customQueue = new Mock<IPersistentJobQueue>();
        Mock<IPersistentJobQueueProvider> provider = new();
        provider.Setup(x => x.GetJobQueue()).Returns(customQueue.Object);
        storage.QueueProviders.Add(provider.Object, ["custom"]);

        return storage;
    }

    private static SqlServerStorage CreateStorage(Action<SqlServerStorageOptions> configure = null)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };
        configure?.Invoke(options);
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), options);
    }
}
