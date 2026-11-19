extern alias ReferencedDapper;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Transactions;
using Microsoft.Data.SqlClient;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// The storage writes through connections of its own, which never join a caller's ambient transaction
/// (<see cref="TransactionScope"/>): a job created inside one exists whether the caller's transaction commits
/// or rolls back, and a write the storage was told succeeded never depends on another caller's transaction.
/// </summary>
/// <remarks>
/// Callers that write at the same time are written as a group by one of them (one writer at a time here, so
/// that the callers that wait are grouped): the tests make a caller inside a scope lead a group.
/// </remarks>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class AmbientTransactionFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // The order in which callers join the queue of the writers is known only to the coalescer, so the test
    // waits this long between them
    private static readonly TimeSpan JoinTheQueue = TimeSpan.FromSeconds(1);

    [Fact, CleanDatabase]
    public void Commit_ThatReturned_IsStored_WhenTheTransactionScopeOfAnotherCallerRollsBack()
    {
        SqlServerStorage storage = CreateStorage(static x => x.MaxConcurrentWriters = 1);
        string blocked = CreateJob(storage);
        string scoped = CreateJob(storage);
        string plain = CreateJob(storage);
        ConcurrentQueue<Exception> failures = new();

        // A writer that waits for a row takes the only place, so the next two callers wait and form a group
        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        HoldJobRow(holder, held, blocked);

        Thread writer = new(() => CommitState(storage, blocked, null, failures));
        writer.Start();
        LockWaits.WaitForSessionsThatWait(1);

        // The first to wait leads the group: a caller inside a scope, which it later rolls back
        using ManualResetEventSlim scopedCommitted = new();
        using ManualResetEventSlim leaveScope = new();
        Thread scopedCaller = new(() =>
        {
            try
            {
                using TransactionScope scope = new();
                CommitState(storage, scoped, null, failures);
                scopedCommitted.Set();
                leaveScope.Wait(Timeout, TestContext.Current.CancellationToken);

                // No Complete(): the caller's own business transaction fails
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Enqueue(ex);
                scopedCommitted.Set();
            }
        });
        scopedCaller.Start();
        Thread.Sleep(JoinTheQueue);

        // The second to wait: a caller without a transaction of its own
        bool plainCommitted = false;
        Thread plainCaller = new(() =>
        {
            CommitState(storage, plain, "plain", failures);
            plainCommitted = failures.IsEmpty;
        });
        plainCaller.Start();
        Thread.Sleep(JoinTheQueue);

        held.Rollback();
        Assert.True(writer.Join(Timeout));
        Assert.True(plainCaller.Join(Timeout));
        Assert.True(scopedCommitted.Wait(Timeout, TestContext.Current.CancellationToken));
        Assert.Empty(failures);
        Assert.True(plainCommitted);

        leaveScope.Set();
        Assert.True(scopedCaller.Join(Timeout));
        Assert.Empty(failures);

        // The commit that returned is stored, and so is the scoped caller's: the storage's writes aren't part of
        // the caller's transaction
        Assert.Equal(EnqueuedState.StateName, StateName(plain));
        Assert.Equal("1", Parameter(plain, "DueAt"));
        Assert.Equal(1, Counter("plain"));
        Assert.Equal(EnqueuedState.StateName, StateName(scoped));
    }

    [Fact, CleanDatabase]
    public void CreateExpiredJob_ThatReturned_HasCreatedTheJob_WhenTheTransactionScopeOfAnotherCallerRollsBack()
    {
        SqlServerStorage storage = CreateStorage(static x => x.MaxConcurrentWriters = 1);
        ConcurrentQueue<Exception> failures = new();

        // Nobody inserts a job while this is held: the first caller takes the only place and waits
        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        holder.Execute($"select top (0) Id from [{Constants.DefaultSchema}].Job with (tablockx, holdlock)", null, held);

        Thread writer = new(() => Create(storage, failures));
        writer.Start();
        LockWaits.WaitForSessionsThatWait(1);

        using ManualResetEventSlim scopedCreated = new();
        using ManualResetEventSlim leaveScope = new();
        string scopedId = null;
        Thread scopedCaller = new(() =>
        {
            try
            {
                using TransactionScope scope = new();
                scopedId = Create(storage, failures);
                scopedCreated.Set();
                leaveScope.Wait(Timeout, TestContext.Current.CancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Enqueue(ex);
                scopedCreated.Set();
            }
        });
        scopedCaller.Start();
        Thread.Sleep(JoinTheQueue);

        string plainId = null;
        Thread plainCaller = new(() => plainId = Create(storage, failures));
        plainCaller.Start();
        Thread.Sleep(JoinTheQueue);

        held.Rollback();
        Assert.True(writer.Join(Timeout));
        Assert.True(plainCaller.Join(Timeout));
        Assert.True(scopedCreated.Wait(Timeout, TestContext.Current.CancellationToken));
        Assert.Empty(failures);
        Assert.NotNull(plainId);

        leaveScope.Set();
        Assert.True(scopedCaller.Join(Timeout));

        // Both jobs exist, the one created inside the scope that rolled back included
        Assert.True(JobExists(plainId), $"CreateExpiredJob returned job '{plainId}', and no such job exists.");
        Assert.True(JobExists(scopedId), $"CreateExpiredJob returned job '{scopedId}' inside a scope, and no such job exists.");
    }

    [Fact, CleanDatabase]
    public void TransactionScope_OfACaller_Survives_WhenTheCommitOfAnotherCallerFails()
    {
        SqlServerStorage storage = CreateStorage(static x => x.MaxConcurrentWriters = 1);
        string blocked = CreateJob(storage);
        string scoped = CreateJob(storage);
        ConcurrentQueue<Exception> failures = new();

        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        HoldJobRow(holder, held, blocked);

        Thread writer = new(() => CommitState(storage, blocked, null, failures));
        writer.Start();
        LockWaits.WaitForSessionsThatWait(1);

        // The first to wait leads the group: a caller inside a scope, with business data of its own
        Exception scopedFailure = null;
        Thread scopedCaller = new(() =>
        {
            try
            {
                using TransactionScope scope = new();
                WriteBusinessData("business-data");

                using (IStorageConnection connection = storage.GetConnection())
                using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
                {
                    transaction.SetJobState(scoped, new EnqueuedState());
                    transaction.Commit();
                }

                scope.Complete();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                scopedFailure = ex;
            }
        });
        scopedCaller.Start();
        Thread.Sleep(JoinTheQueue);

        // Another caller, whose own write is wrong: a state for a job that doesn't exist
        Exception otherFailure = null;
        Thread other = new(() =>
        {
            try
            {
                using IStorageConnection connection = storage.GetConnection();
                using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
                transaction.SetJobState("987654321", new EnqueuedState());
                transaction.Commit();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                otherFailure = ex;
            }
        });
        other.Start();
        Thread.Sleep(JoinTheQueue);

        held.Rollback();
        Assert.True(writer.Join(Timeout));
        Assert.True(other.Join(Timeout));
        Assert.True(scopedCaller.Join(Timeout));
        Assert.Empty(failures);

        // The wrong write fails its own caller only
        Assert.IsType<SqlException>(otherFailure);
        Assert.Null(scopedFailure);
        Assert.Equal(1, Counter("business-data"));
        Assert.Equal(EnqueuedState.StateName, StateName(scoped));
    }

    [Fact, CleanDatabase]
    public void Commit_DoesNotStall_WhenAGroupHoldsTheStateOfAJobCreatedInsideATransactionScope()
    {
        SqlServerStorage storage = CreateStorage(static x =>
        {
            x.MaxConcurrentWriters = 1;
            x.CommandTimeout = TimeSpan.FromSeconds(3);
        });
        string blocked = CreateJob(storage);
        string plain = CreateJob(storage);
        ConcurrentQueue<Exception> failures = new();

        using DbConnection holder = ConnectionUtils.CreateConnection();
        using DbTransaction held = holder.BeginTransaction();
        HoldJobRow(holder, held, blocked);

        Thread writer = new(() => CommitState(storage, blocked, null, failures));
        writer.Start();
        LockWaits.WaitForSessionsThatWait(1);

        // The first to wait, and so the one that commits the group: a caller without a scope
        TimeSpan plainTook = TimeSpan.Zero;
        Thread plainCaller = new(() =>
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CommitState(storage, plain, "plain", failures);
            plainTook = stopwatch.Elapsed;
        });

        // As BackgroundJob.Enqueue inside a scope: the job, then its first state, which the group commits
        using ManualResetEventSlim created = new();
        using ManualResetEventSlim commit = new();
        TimeSpan scopedTook = TimeSpan.Zero;
        Thread scopedCaller = new(() =>
        {
            try
            {
                using TransactionScope scope = new();
                string id;
                using (IStorageConnection connection = storage.GetConnection())
                {
                    id = connection.CreateExpiredJob(Job.FromExpression(() => Run()), new Dictionary<string, string>(), DateTime.UtcNow, TimeSpan.FromHours(1));
                }

                created.Set();
                commit.Wait(Timeout, TestContext.Current.CancellationToken);
                Stopwatch stopwatch = Stopwatch.StartNew();
                CommitState(storage, id, null, failures);
                scopedTook = stopwatch.Elapsed;
                scope.Complete();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Enqueue(ex);
                created.Set();
            }
        });

        // The job is created while the job inserts are free (they have a coalescer of their own)
        scopedCaller.Start();
        Assert.True(created.Wait(Timeout, TestContext.Current.CancellationToken));

        plainCaller.Start();
        Thread.Sleep(JoinTheQueue);
        commit.Set();
        Thread.Sleep(JoinTheQueue);

        held.Rollback();
        Assert.True(writer.Join(Timeout * 2));
        Assert.True(plainCaller.Join(Timeout * 2));
        Assert.True(scopedCaller.Join(Timeout * 2));
        Assert.Empty(failures);

        Assert.True(
            plainTook < TimeSpan.FromSeconds(5) && scopedTook < TimeSpan.FromSeconds(5),
            $"The commit of the caller without a scope took {plainTook.TotalSeconds:F1} s and the scoped caller's {scopedTook.TotalSeconds:F1} s, with a command timeout of 3 s.");
    }

    [Fact, CleanDatabase]
    public void Job_EnqueuedInsideATransactionScopeThatRollsBack_StillExists()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        string id;

        using (new TransactionScope())
        {
            WriteBusinessData("rolled-back");
            id = client.Enqueue(() => Run());

            // No Complete(): the business transaction rolls back
        }

        // The job doesn't take part in the caller's transaction (docs/getting-started/core-concepts.md)
        Assert.Equal(0, Counter("rolled-back"));
        Assert.Equal(EnqueuedState.StateName, StateName(id));
        Assert.Equal(1, Scalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobQueue where JobId = @id", id));
    }

    [Fact, CleanDatabase]
    public async Task Job_EnqueuedInsideATransactionScopeThatFlowsWithAsyncCalls_StillExists_WhenTheScopeRollsBack()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        string id;
        string onAnotherThread;

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Task.Yield();
            WriteBusinessData("rolled-back");
            id = client.Enqueue(() => Run());

            // The ambient transaction flows to the thread pool too
            onAnotherThread = await Task.Run(() =>
            {
                Assert.NotNull(Transaction.Current);
                return client.Enqueue(() => Run());
            }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, Counter("rolled-back"));
        Assert.Equal(EnqueuedState.StateName, StateName(id));
        Assert.Equal(EnqueuedState.StateName, StateName(onAnotherThread));
    }

    [Theory, CleanDatabase]
    [InlineData(TransactionalJobParameterFacts.CommitPath.OneRoundTrip)]
    [InlineData(TransactionalJobParameterFacts.CommitPath.CommandBatch)]
    [InlineData(TransactionalJobParameterFacts.CommitPath.CommandAfterCommand)]
    public void BusinessData_WrittenInATransactionScope_IsCommitted_WhenACommitOfTheStorageFailsInsideTheScope(TransactionalJobParameterFacts.CommitPath path)
    {
        // A caller on its own (nothing is grouped), whose own commit fails and is rolled back by the storage
        SqlServerStorage storage = CreateStorage(x =>
        {
            x.MaxConcurrentWriters = 0;
            x.CommandBatchMaxTimeout = path == TransactionalJobParameterFacts.CommitPath.CommandAfterCommand ? null : TimeSpan.FromMinutes(5);
        });
        string id = CreateJob(storage);

        using (TransactionScope scope = new())
        {
            WriteBusinessData("business-data");

            using (IStorageConnection connection = storage.GetConnection())
            using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
            {
                if (path == TransactionalJobParameterFacts.CommitPath.CommandBatch)
                {
                    // More commands than one command carries
                    for (int i = 0; i < 100; i++)
                    {
                        transaction.IncrementCounter("filler");
                    }
                }

                transaction.SetJobState(id, new EnqueuedState());

                // A state for a job that doesn't exist breaks the foreign key
                transaction.SetJobState("987654321", new EnqueuedState());

                Assert.Throws<SqlException>(transaction.Commit);
            }

            WriteBusinessData("after-the-failure");
            scope.Complete();
        }

        Assert.Equal(1, Counter("business-data"));
        Assert.Equal(1, Counter("after-the-failure"));
        Assert.Null(StateName(id));
        Assert.Equal(0, Counter("filler"));
    }

    public static IEnumerable<TheoryDataRow<string>> Constructions()
    {
        yield return new("connection string");
        yield return new("factory that opens the connection");
        yield return new("factory that leaves the connection closed");
        yield return new("factory without pooling");
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(Constructions))]
    public void Writes_InsideATransactionScope_AreCommittedAtOnce_AndKeptWhenItRollsBack(string construction)
    {
        SqlServerStorage storage = CreateStorage(construction);
        string id;

        using (new TransactionScope())
        {
            using (IStorageConnection connection = storage.GetConnection())
            {
                id = connection.CreateExpiredJob(Job.FromExpression(() => Run()), new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, DateTime.UtcNow, TimeSpan.FromHours(1));

                // On the connection that holds a distributed lock, as the state changer writes
                using (connection.AcquireDistributedLock($"job:{id}:state-lock", TimeSpan.FromSeconds(5)))
                using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
                {
                    transaction.SetJobState(id, new EnqueuedState());
                    transaction.AddToQueue("default", id);
                    transaction.SetJobParameter(id, "C", "3");
                    transaction.IncrementCounter("in-scope");
                    transaction.Commit();
                }

                connection.SetJobParameter(id, "D", "4");
            }

            // Committed already, while the scope is still open: a reader outside it doesn't wait for the scope
            Assert.Equal(EnqueuedState.StateName, ReadWithoutWaiting<string>($"select StateName from [{Constants.DefaultSchema}].Job where Id = @id", id));
            Assert.Equal(4, ReadWithoutWaiting<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter where JobId = @id", id));

            // No Complete()
        }

        Assert.Equal(EnqueuedState.StateName, StateName(id));
        Assert.Equal(1, Counter("in-scope"));
        Assert.Equal("4", Parameter(id, "D"));

        // And the pooled connections the storage used are none the worse: it writes again, outside any scope
        using (IStorageConnection connection = storage.GetConnection())
        using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            transaction.IncrementCounter("after-the-scope");
            transaction.Commit();
        }

        Assert.Equal(1, Counter("after-the-scope"));
    }

    public static void Run()
    {
    }

    private static void CommitState(SqlServerStorage storage, string jobId, string counter, ConcurrentQueue<Exception> failures)
    {
        try
        {
            using IStorageConnection connection = storage.GetConnection();
            using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
            transaction.SetJobState(jobId, new EnqueuedState());
            transaction.SetJobParameter(jobId, "DueAt", "1");
            if (counter != null)
            {
                transaction.IncrementCounter(counter);
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Enqueue(ex);
        }
    }

    private static string Create(SqlServerStorage storage, ConcurrentQueue<Exception> failures)
    {
        try
        {
            using IStorageConnection connection = storage.GetConnection();
            return connection.CreateExpiredJob(Job.FromExpression(() => Run()), new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, DateTime.UtcNow, TimeSpan.FromHours(1));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Enqueue(ex);
            return null;
        }
    }

    /// <summary>Takes the job's row in the given transaction, so a writer of the job waits for it.</summary>
    private static void HoldJobRow(DbConnection connection, DbTransaction transaction, string jobId)
    {
        connection.Execute(
            $"update [{Constants.DefaultSchema}].Job set ExpireAt = dateadd(second, 1, ExpireAt) where Id = @id",
            new { id = long.Parse(jobId) },
            transaction);
    }

    /// <summary>The caller's own data, written on its own connection, in its scope if it has one.</summary>
    private static void WriteBusinessData(string key)
    {
        using DbConnection own = ConnectionUtils.CreateConnection();
        own.Execute($"insert into [{Constants.DefaultSchema}].Counter ([Key], [Value]) values (@key, 1)", new { key });
    }

    private static string StateName(string jobId) =>
        Scalar<string>($"select StateName from [{Constants.DefaultSchema}].Job where Id = @id", jobId);

    private static string Parameter(string jobId, string name) =>
        Scalar<string>($"select Value from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = N'{name}'", jobId);

    private static bool JobExists(string jobId) =>
        jobId != null && Scalar<int>($"select count(*) from [{Constants.DefaultSchema}].Job where Id = @id", jobId) == 1;

    private static int Counter(string key)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        return sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Counter where [Key] = @key", new { key });
    }

    private static T Scalar<T>(string query, string jobId)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        return sql.ExecuteScalar<T>(query, new { id = long.Parse(jobId) });
    }

    /// <summary>
    /// Reads outside the caller's scope, giving up after two seconds on a row somebody holds: what it reads is
    /// committed.
    /// </summary>
    private static T ReadWithoutWaiting<T>(string query, string jobId)
    {
        using SqlConnection sql = new(new SqlConnectionStringBuilder(ConnectionUtils.GetConnectionString()) { Enlist = false }.ConnectionString);
        sql.Open();
        sql.Execute("set lock_timeout 2000");
        return sql.ExecuteScalar<T>(query, new { id = long.Parse(jobId) });
    }

    private static string CreateJob(SqlServerStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(Job.FromExpression(() => Run()), new Dictionary<string, string>(), DateTime.UtcNow, TimeSpan.FromHours(1));
    }

    private static SqlServerStorage CreateStorage(string construction)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };

        return construction switch
        {
            "connection string" => new SqlServerStorage(ConnectionUtils.GetConnectionString(), options),
            "factory that opens the connection" => new SqlServerStorage(static () => ConnectionUtils.CreateConnection(), options),
            "factory that leaves the connection closed" => new SqlServerStorage(static () => new SqlConnection(ConnectionUtils.GetConnectionString()), options),
            "factory without pooling" => new SqlServerStorage(
                static () => new SqlConnection(new SqlConnectionStringBuilder(ConnectionUtils.GetConnectionString()) { Pooling = false }.ConnectionString),
                options),
            _ => throw new ArgumentOutOfRangeException(nameof(construction), construction, null),
        };
    }

    private static SqlServerStorage CreateStorage(Action<SqlServerStorageOptions> configure = null)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };
        configure?.Invoke(options);
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), options);
    }
}
