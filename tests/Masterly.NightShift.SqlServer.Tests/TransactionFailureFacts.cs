extern alias ReferencedDapper;

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using ReferencedDapper::Dapper;
using CommitPath = Masterly.NightShift.SqlServer.Tests.TransactionalJobParameterFacts.CommitPath;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// A write that fails half-way (a deadlock victim, a command timeout, a duplicate key, a job deleted while a
/// parameter waits for it) commits all of the transaction or none of it, on every commit path, and leaves the
/// connection usable; and writes of servers and clients to the same jobs never deadlock each other.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class TransactionFailureFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    public void Transaction_ChosenAsDeadlockVictim_WhileAStateWaits_CommitsNothing(CommitPath path)
    {
        SqlServerStorage storage = CreateLowPriorityStorage(path);
        string first = CreateJob(storage, new Dictionary<string, string> { ["A"] = "old" });
        string second = CreateJob(storage, new Dictionary<string, string> { ["Held"] = "old" });

        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute($"update [{Constants.DefaultSchema}].Job set ExpireAt = ExpireAt where Id = @id", new { id = long.Parse(second) }, held);

        Exception thrown = null;
        Thread thread = new(() => thrown = Record.Exception(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
            Fill(transaction, path, first);
            transaction.SetJobState(first, new EnqueuedState());
            transaction.SetJobState(second, new EnqueuedState());
            transaction.IncrementCounter("after");
            transaction.Commit();
        }));
        thread.Start();
        LockWaits.WaitForSessionsThatWait(1);

        // The holder now wants the first job, which the transaction holds: the transaction is the victim
        holder.Execute($"update [{Constants.DefaultSchema}].Job set ExpireAt = ExpireAt where Id = @id", new { id = long.Parse(first) }, held);
        Assert.True(thread.Join(Timeout));
        held.Rollback();

        Assert.Equal(1205, Assert.IsType<SqlException>(thrown).Number);

        using DbConnection reader = ConnectionUtils.CreateConnection();
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter"));
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].State"));
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter where Name like N'Filler%'"));
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Job where StateName is not null"));
    }

    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    public void Transaction_ChosenAsDeadlockVictim_WhileAParameterUpsertWaits_CommitsNothing(CommitPath path)
    {
        SqlServerStorage storage = CreateLowPriorityStorage(path);
        string first = CreateJob(storage, new Dictionary<string, string> { ["A"] = "old" });
        string second = CreateJob(storage, new Dictionary<string, string> { ["Held"] = "old" });

        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute($"update [{Constants.DefaultSchema}].JobParameter set Value = N'held' where JobId = @id and Name = N'Held'", new { id = long.Parse(second) }, held);

        Exception thrown = null;
        Thread thread = new(() => thrown = Record.Exception(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
            Fill(transaction, path, first);
            transaction.SetJobParameter(first, "A", "new");
            transaction.SetJobParameter(second, "Held", "new");
            transaction.SetJobState(first, new EnqueuedState());
            transaction.SetJobState(second, new EnqueuedState());
            transaction.IncrementCounter("after");
            transaction.Commit();
        }));
        thread.Start();
        LockWaits.WaitForSessionsThatWait(1);

        // The holder now wants the first job's parameter, which the transaction holds: the transaction is the victim
        holder.Execute($"update [{Constants.DefaultSchema}].JobParameter set Value = N'held' where JobId = @id and Name = N'A'", new { id = long.Parse(first) }, held);
        Assert.True(thread.Join(Timeout));
        held.Rollback();

        Assert.Equal(1205, Assert.IsType<SqlException>(thrown).Number);

        using DbConnection reader = ConnectionUtils.CreateConnection();
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter"));
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].State"));
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter where Name like N'Filler%'"));
        Assert.Equal("old", reader.ExecuteScalar<string>($"select Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = N'A'", new { id = long.Parse(first) }));
        Assert.Equal("old", reader.ExecuteScalar<string>($"select Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = N'Held'", new { id = long.Parse(second) }));
    }

    // On the connection that holds a distributed lock, as the state changer's does, or on one of its own
    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip, true)]
    [InlineData(CommitPath.OneRoundTrip, false)]
    [InlineData(CommitPath.CommandBatch, true)]
    [InlineData(CommitPath.CommandAfterCommand, true)]
    public void CommandTimeout_WhileAParameterUpsertWaits_LeavesNothingOpen_AndTheConnectionWritesAgain(CommitPath path, bool lockedConnection)
    {
        SqlServerStorage storage = CreateStorage(x =>
        {
            x.CommandTimeout = TimeSpan.FromSeconds(2);
            x.CommandBatchMaxTimeout = path == CommitPath.CommandAfterCommand ? null : TimeSpan.FromSeconds(3);
        });
        string first = CreateJob(storage, new Dictionary<string, string> { ["A"] = "old" });
        string second = CreateJob(storage, new Dictionary<string, string> { ["Held"] = "old" });

        using DbConnection holder = ConnectionUtils.CreateConnection();
        int holderSession = holder.ExecuteScalar<int>("select @@spid");
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute($"update [{Constants.DefaultSchema}].JobParameter set Value = N'held' where JobId = @id and Name = N'Held'", new { id = long.Parse(second) }, held);

        using (IStorageConnection connection = storage.GetConnection())
        {
            using IDisposable? jobLock = lockedConnection ? connection.AcquireDistributedLock($"job:{first}:state-lock", TimeSpan.FromSeconds(5)) : null;

            using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
            {
                Fill(transaction, path, first);
                transaction.SetJobParameter(first, "A", "new");
                transaction.SetJobParameter(second, "Held", "new");
                transaction.SetJobState(first, new EnqueuedState());
                transaction.IncrementCounter("after");

                Assert.NotNull(Record.Exception(transaction.Commit));
            }

            // While the holder still holds its row: no other session has a transaction open, or holds a row
            using (DbConnection probe = ConnectionUtils.CreateConnection())
            {
                Assert.Equal(0, probe.ExecuteScalar<int>(
                    """
                    select count(*) from sys.dm_exec_sessions
                    where database_id = db_id() and open_transaction_count > 0 and session_id not in (@@spid, @holder)
                    """,
                    new { holder = holderSession }));
                Assert.Equal(0, probe.ExecuteScalar<int>(
                    """
                    select count(*) from sys.dm_tran_locks
                    where resource_database_id = db_id() and resource_type in (N'KEY', N'RID', N'PAGE')
                      and request_session_id not in (@@spid, @holder)
                    """,
                    new { holder = holderSession }));
            }

            held.Rollback();

            using JobStorageTransaction again = (JobStorageTransaction)connection.CreateWriteTransaction();
            again.SetJobParameter(first, "A", "again");
            again.IncrementCounter("again");
            again.Commit();
        }

        // Committed for good: resetting every pooled connection would roll back what one had left open
        SqlConnection.ClearAllPools();
        using DbConnection reader = ConnectionUtils.CreateConnection();
        Assert.Equal("again", reader.ExecuteScalar<string>($"select Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = N'A'", new { id = long.Parse(first) }));
        Assert.Equal(1, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter where [Key] = N'again'"));
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter where [Key] = N'after'"));
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].State"));
    }

    // "A" and "A " are two names for .NET and one for SQL Server, which ignores trailing spaces: a duplicate key
    // (2627) after the command has already sent the new job's identifier
    [Theory, CleanDatabase]
    [InlineData(0, 2, false)]
    [InlineData(0, 3, false)]
    [InlineData(0, 4, false)]
    [InlineData(0, 5, false)]
    [InlineData(8, 2, false)]
    [InlineData(0, 2, true)]
    public void CreateExpiredJob_WhoseParameterInsertFails_Throws_AndLeavesNoJob(int maxConcurrentWriters, int parameters, bool delayedDurability)
    {
        SqlServerStorage storage = CreateStorage(x =>
        {
            x.MaxConcurrentWriters = maxConcurrentWriters;
            x.AllowDelayedDurability = delayedDurability;
        });

        bool ignoredDuplicates = RejectDuplicateParameters();

        try
        {
            Dictionary<string, string> jobParameters = new() { ["A"] = "1", ["A "] = "2" };
            for (int i = 2; i < parameters; i++)
            {
                jobParameters[$"P{i}"] = "x";
            }

            Exception thrown = Record.Exception(() =>
            {
                using IStorageConnection connection = storage.GetConnection();
                connection.CreateExpiredJob(Job.FromExpression(() => Run()), jobParameters, DateTime.UtcNow, TimeSpan.FromHours(1));
            });

            Assert.Equal(2627, Assert.IsType<SqlException>(thrown).Number);

            using DbConnection reader = ConnectionUtils.CreateConnection();
            Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Job"));
            Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter"));
        }
        finally
        {
            RestoreDuplicateParameters(ignoredDuplicates);

            if (delayedDurability)
            {
                // The other tests run with the setting a new database has
                using DbConnection sql = ConnectionUtils.CreateConnection();
                sql.Execute("alter database current set delayed_durability = disabled");
            }
        }
    }

    // The upsert's insert finds the parameter inserted meanwhile (2627) and updates it in its CATCH block, where
    // XACT_ABORT is off: an error there must not let the command go on to its commit
    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    public void Commit_ThatThrows_HasWrittenNothing_WhenTheSecondUpdateOfAnUpsertFails(CommitPath path)
    {
        bool ignoredDuplicates = RejectDuplicateParameters();

        try
        {
            CommitThatFailsInTheSecondUpdateOfAnUpsert(path);
        }
        finally
        {
            RestoreDuplicateParameters(ignoredDuplicates);
        }
    }

    private static void CommitThatFailsInTheSecondUpdateOfAnUpsert(CommitPath path)
    {
        SqlServerStorage storage = CreateLockTimeoutStorage(path, 4000);

        // The job has a parameter already, so the page its new parameter goes to exists
        string id = CreateJob(storage, new Dictionary<string, string> { ["Existing"] = "x" });
        string filler = CreateJob(storage, []);
        long jobId = long.Parse(id);

        // A key-range lock where the new parameter goes: the upsert's update passes, its insert waits
        using DbConnection first = ConnectionUtils.CreateConnection();
        using DbTransaction firstTransaction = first.BeginTransaction();
        first.Execute($"select Value from [{Constants.DefaultSchema}].JobParameter with (serializable) where JobId = @id and Name = N'N'", new { id = jobId }, firstTransaction);

        Exception thrown = null;
        Thread thread = new(() => thrown = Record.Exception(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
            Fill(transaction, path, filler);
            transaction.SetJobParameter(id, "N", "from the transaction");
            transaction.SetJobState(id, new EnqueuedState());
            transaction.IncrementCounter("after");
            transaction.Commit();
        }));
        thread.Start();
        LockWaits.WaitForSessionsThatWait(1);

        // Meanwhile the parameter is inserted by somebody else, and a REPEATABLE READ reader waits for it: the
        // upsert's second update then waits for that reader until its lock timeout
        first.Execute($"insert into [{Constants.DefaultSchema}].JobParameter (JobId, Name, Value) values (@id, N'N', N'from somebody else')", new { id = jobId }, firstTransaction);

        using DbConnection second = ConnectionUtils.CreateConnection();
        using DbTransaction secondTransaction = second.BeginTransaction(IsolationLevel.RepeatableRead);
        Task<string> read = Task.Run(
            () => second.ExecuteScalar<string>($"select Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = N'N'", new { id = jobId }, secondTransaction),
            TestContext.Current.CancellationToken);
        LockWaits.WaitForSessionsThatWait(2);

        firstTransaction.Commit();
        Assert.True(read.Wait(Timeout, TestContext.Current.CancellationToken));
        Assert.True(thread.Join(Timeout));
        secondTransaction.Rollback();

        using DbConnection reader = ConnectionUtils.CreateConnection();
        int counters = reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter where [Key] = N'after'");
        int states = reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].State where JobId = @id", new { id = jobId });
        string value = reader.ExecuteScalar<string>($"select Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = N'N'", new { id = jobId });

        // Either the commit fails and nothing is stored, or it succeeds and everything is
        if (thrown != null)
        {
            Assert.Equal((0, 0), (counters, states));
        }
        else
        {
            Assert.Equal((1, 1, "from the transaction"), (counters, states, value));
        }
    }

    // The job is deleted while the upsert's insert waits for its row: the foreign key's error (547) is skipped
    [Theory, CleanDatabase]
    [InlineData(CommitPath.OneRoundTrip)]
    [InlineData(CommitPath.CommandBatch)]
    [InlineData(CommitPath.CommandAfterCommand)]
    public void SetJobParameter_OfAJobDeletedWhileTheInsertWaits_IsIgnored_AndTheRestIsCommitted(CommitPath path)
    {
        SqlServerStorage storage = CreateLockTimeoutStorage(path, 20000);
        string kept = CreateJob(storage, []);
        string filler = CreateJob(storage, []);
        string deleted = CreateJob(storage, new Dictionary<string, string> { ["Existing"] = "x" });

        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute($"delete from [{Constants.DefaultSchema}].Job where Id = @id", new { id = long.Parse(deleted) }, held);

        Exception thrown = null;
        Thread thread = new(() => thrown = Record.Exception(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
            Fill(transaction, path, filler);
            transaction.SetJobParameter(kept, "Kept", "value");
            transaction.SetJobParameter(deleted, "New", "value");
            transaction.SetJobState(kept, new EnqueuedState());
            transaction.IncrementCounter("after");
            transaction.Commit();
        }));
        thread.Start();
        LockWaits.WaitForSessionsThatWait(1);
        held.Commit();
        Assert.True(thread.Join(Timeout));

        Assert.Null(thrown);

        using DbConnection reader = ConnectionUtils.CreateConnection();
        Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = long.Parse(deleted) }));
        Assert.Equal(1, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter where [Key] = N'after'"));
        Assert.Equal("value", reader.ExecuteScalar<string>($"select Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = N'Kept'", new { id = long.Parse(kept) }));
        Assert.Equal(EnqueuedState.StateName, reader.ExecuteScalar<string>($"select StateName from [{Constants.DefaultSchema}].Job where Id = @id", new { id = long.Parse(kept) }));
    }

    // A pooled session keeps the isolation level its last user set, and the application may share the pool. Under
    // repeatable read, the check that a parameter's job exists would keep a shared lock on the job's row until the
    // commit, and a transaction that writes the parameter and then the job's state would deadlock with it
    [Theory, CleanDatabase]
    [InlineData("one round trip")]
    [InlineData("command batch")]
    [InlineData("command after command")]
    [InlineData("committed together")]
    public void Commit_KeepsNoSharedLockOnTheJobsItReads_WhenItsSessionWasLeftAtRepeatableRead(string path)
    {
        SqlServerStorage storage = CreateStorageOnSessionsWith(
            path == "command after command" ? CommitPath.CommandAfterCommand : CommitPath.OneRoundTrip,
            "set transaction isolation level repeatable read");
        string read = CreateJob(storage, []);
        string blocked = CreateJob(storage, []);

        using DbConnection holder = ConnectionUtils.CreateConnection();
        int holderSession = holder.ExecuteScalar<int>("select @@spid");
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute($"update [{Constants.DefaultSchema}].Job set ExpireAt = ExpireAt where Id = @id", new { id = long.Parse(blocked) }, held);

        // A new parameter of one job (its insert reads the job's row), then the state of another, which waits
        Exception thrown = null;
        Thread thread = new(() => thrown = Record.Exception(() =>
        {
            using SqlServerConnection connection = (SqlServerConnection)storage.GetConnection();
            using SqlServerWriteOnlyTransaction transaction = new(connection);
            if (path == "command batch")
            {
                Fill(transaction, CommitPath.CommandBatch, read);
            }

            transaction.SetJobParameter(read, "New", "value");
            transaction.SetJobState(blocked, new EnqueuedState());

            if (path == "committed together")
            {
                SqlServerWriteOnlyTransaction.CommitTogether(storage, [transaction]);
            }
            else
            {
                transaction.Commit();
            }
        }));
        thread.Start();
        LockWaits.WaitForSessionsThatWait(1);

        using (DbConnection probe = ConnectionUtils.CreateConnection())
        {
            int shared = probe.ExecuteScalar<int>(
                $"""
                select count(*) from sys.dm_tran_locks
                where resource_database_id = db_id() and resource_type = N'KEY' and request_status = N'GRANT'
                  and request_mode in (N'S', N'RangeS-S', N'U')
                  and resource_associated_entity_id = (select hobt_id from sys.partitions where object_id = object_id(N'[{Constants.DefaultSchema}].[Job]') and index_id = 1)
                  and request_session_id not in (@@spid, @holder)
                """,
                new { holder = holderSession });

            held.Rollback();
            Assert.True(thread.Join(Timeout));
            Assert.Equal(0, shared);
        }

        Assert.Null(thrown);
    }

    // The same for a read of a job: under repeatable read it kept a shared lock on each parameter it had read while
    // it waited for the next, which a transaction writing the job's parameters in their order could hold while it
    // waited for one the read had passed (MixedWrites_ToTheSameJobs_NeverDeadlock, after a test left such a session)
    [Fact, CleanDatabase]
    public void GetJobData_KeepsNoSharedLockOnTheParametersItReads_WhenItsSessionWasLeftAtRepeatableRead()
    {
        SqlServerStorage storage = CreateStorageOnSessionsWith(CommitPath.OneRoundTrip, "set transaction isolation level repeatable read");
        string jobId = CreateJob(storage, new Dictionary<string, string> { ["A"] = "read", ["B"] = "held" });

        using DbConnection holder = ConnectionUtils.CreateConnection();
        int holderSession = holder.ExecuteScalar<int>("select @@spid");
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute($"update [{Constants.DefaultSchema}].JobParameter set Value = Value where JobId = @id and Name = N'B'", new { id = long.Parse(jobId) }, held);

        JobData data = null;
        Exception thrown = null;
        Thread thread = new(() => thrown = Record.Exception(() =>
        {
            using IStorageConnection connection = storage.GetConnection();
            data = connection.GetJobData(jobId);
        }));
        thread.Start();
        LockWaits.WaitForSessionsThatWait(1);

        using (DbConnection probe = ConnectionUtils.CreateConnection())
        {
            int shared = probe.ExecuteScalar<int>(
                $"""
                select count(*) from sys.dm_tran_locks
                where resource_database_id = db_id() and resource_type = N'KEY' and request_status = N'GRANT'
                  and request_mode in (N'S', N'RangeS-S', N'U')
                  and resource_associated_entity_id = (select hobt_id from sys.partitions where object_id = object_id(N'[{Constants.DefaultSchema}].[JobParameter]') and index_id = 1)
                  and request_session_id not in (@@spid, @holder)
                """,
                new { holder = holderSession });

            held.Rollback();
            Assert.True(thread.Join(Timeout));
            Assert.Equal(0, shared);
        }

        Assert.Null(thrown);
        Assert.Equal("held", data.ParametersSnapshot["B"]);
    }

    // What servers and clients do to the same few jobs at once. Operations: A a state with a parameter (and a
    // counter), B a parameter outside a transaction, C parameters of several jobs without a state (a chain's
    // marks), D a parameter with the job's expiration, E reads
    [Theory, CleanDatabase]
    [InlineData(8, false, "ABCE")]
    [InlineData(1, true, "ABCDE")]
    [InlineData(1, true, "C")]
    public void MixedWrites_ToTheSameJobs_NeverDeadlock(int maxConcurrentWriters, bool disableGlobalLocks, string operations)
    {
        SqlServerStorage storage = CreateStorage(x =>
        {
            x.MaxConcurrentWriters = maxConcurrentWriters;
            x.DisableGlobalLocks = disableGlobalLocks;
        });
        ConcurrentQueue<Exception> failures = new();
        const int callers = 12;
        string[] names = ["DueAt", "ChainRetry", "ChainRecorded", "Other"];

        for (int round = 0; round < 6; round++)
        {
            string[] jobs = [.. Enumerable.Range(0, 5).Select(_ => CreateJob(storage, []))];
            using Barrier barrier = new(callers);

            Thread[] threads = [.. Enumerable.Range(0, callers).Select(caller => new Thread(() =>
            {
                Random random = new((round * 100) + caller);
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));

                for (int i = 0; i < 40; i++)
                {
                    char operation = operations[random.Next(operations.Length)];
                    string job = jobs[random.Next(jobs.Length)];
                    string other = jobs[random.Next(jobs.Length)];
                    string name = names[random.Next(names.Length)];

                    try
                    {
                        Write(storage, random, operation, job, other, jobs, names, name, i);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        failures.Enqueue(new InvalidOperationException($"operation {operation}", ex));
                    }
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
        }

        Assert.True(failures.IsEmpty, $"{failures.Count} failures, first: {failures.FirstOrDefault()}");
    }

    public static void Run()
    {
    }

    private static void Write(SqlServerStorage storage, Random random, char operation, string job, string other, string[] jobs, string[] names, string name, int i)
    {
        using IStorageConnection connection = storage.GetConnection();

        switch (operation)
        {
            case 'A':
                using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
                {
                    transaction.SetJobState(job, new ScheduledState(TimeSpan.FromHours(1)));
                    transaction.SetJobParameter(job, name, i.ToString());
                    if (random.Next(2) == 0)
                    {
                        transaction.IncrementCounter("stats");
                    }

                    transaction.Commit();
                }

                break;

            case 'B':
                connection.SetJobParameter(job, name, i.ToString());
                break;

            case 'C':
                using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
                {
                    transaction.SetJobParameter(other, names[random.Next(names.Length)], i.ToString());
                    transaction.SetJobParameter(job, name, i.ToString());
                    transaction.SetJobParameter(jobs[random.Next(jobs.Length)], names[random.Next(names.Length)], null);
                    transaction.Commit();
                }

                break;

            case 'D':
                using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
                {
                    transaction.SetJobParameter(job, name, i.ToString());
                    transaction.ExpireJob(job, TimeSpan.FromHours(2));
                    transaction.Commit();
                }

                break;

            default:
                connection.GetJobData(job);
                connection.GetJobParameter(job, name);
                break;
        }
    }

    /// <summary>
    /// Makes the key of the parameters reject a duplicate (2627), as the schema the storage installs does, and
    /// returns whether it ignored duplicates before: some tests of the test database leave IGNORE_DUP_KEY on.
    /// </summary>
    private static bool RejectDuplicateParameters()
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        bool ignored = sql.ExecuteScalar<bool>(
            $"select ignore_dup_key from sys.indexes where object_id = object_id(N'[{Constants.DefaultSchema}].[JobParameter]') and index_id = 1");

        if (ignored)
        {
            sql.Execute($"alter table [{Constants.DefaultSchema}].[JobParameter] rebuild with (ignore_dup_key = off)");
        }

        return ignored;
    }

    private static void RestoreDuplicateParameters(bool ignored)
    {
        if (ignored)
        {
            using DbConnection sql = ConnectionUtils.CreateConnection();
            sql.Execute($"alter table [{Constants.DefaultSchema}].[JobParameter] rebuild with (ignore_dup_key = on)");
        }
    }

    /// <summary>Adds what makes a transaction take <paramref name="path"/> when it is committed.</summary>
    private static void Fill(JobStorageTransaction transaction, CommitPath path, string jobId)
    {
        if (path == CommitPath.CommandBatch)
        {
            // More commands than one command carries
            for (int i = 0; i < 100; i++)
            {
                transaction.SetJobParameter(jobId, $"Filler{i:D3}", null);
            }
        }
    }

    /// <summary>A storage whose sessions are the deadlock victims, on connections out of the pool.</summary>
    private static SqlServerStorage CreateLowPriorityStorage(CommitPath path)
    {
        return CreateStorageOnSessionsWith(path, "set deadlock_priority low");
    }

    /// <summary>A storage whose sessions give up on a lock after that many milliseconds.</summary>
    private static SqlServerStorage CreateLockTimeoutStorage(CommitPath path, int milliseconds)
    {
        return CreateStorageOnSessionsWith(path, $"set lock_timeout {milliseconds}");
    }

    // Connections that stay out of the pool, so no other test is handed a session with these settings
    private static SqlServerStorage CreateStorageOnSessionsWith(CommitPath path, string setting)
    {
        return new SqlServerStorage(
            () =>
            {
                SqlConnection connection = new(new SqlConnectionStringBuilder(ConnectionUtils.GetConnectionString()) { Pooling = false }.ConnectionString);
                connection.Open();
                connection.Execute(setting);
                return connection;
            },
            new SqlServerStorageOptions
            {
                PrepareSchemaIfNecessary = false,
                CommandBatchMaxTimeout = path == CommitPath.CommandAfterCommand ? null : TimeSpan.FromMinutes(5),
            });
    }

    private static SqlServerStorage CreateStorage(Action<SqlServerStorageOptions> configure = null)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };
        configure?.Invoke(options);
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), options);
    }

    private static string CreateJob(SqlServerStorage storage, Dictionary<string, string> parameters)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => Run()), parameters, DateTime.UtcNow, TimeSpan.FromHours(1));
    }
}
