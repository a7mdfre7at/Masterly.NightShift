extern alias ReferencedDapper;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerHeartbeatProcessFacts
{
    private static readonly string[] Queues = ["bk"];

    [Fact, CleanDatabase]
    public void Execute_KeepsRenewingTheOtherJobs_WhileTheRowOfOneIsLocked()
    {
        SqlServerStorage storage = CreateStorage();
        InsertQueueEntry(1);
        InsertQueueEntry(2);

        using IStorageConnection connection = storage.GetConnection();
        using IFetchedJob first = connection.FetchNextJob(Queues, CancellationToken.None);
        using IFetchedJob second = connection.FetchNextJob(Queues, CancellationToken.None);

        using DbConnection blocker = ConnectionUtils.CreateConnection();
        using DbTransaction blocking = blocker.BeginTransaction();
        blocker.Execute(
            $"update [{Constants.DefaultSchema}].JobQueue set Queue = Queue where JobId = @jobId",
            new { jobId = long.Parse(first.JobId, CultureInfo.InvariantCulture) },
            blocking);

        using CancellationTokenSource stop = new();
        Thread heartbeat = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                storage.HeartbeatProcess.Execute(stop.Token);
            }
        });
        heartbeat.Start();

        // Longer than the invisibility timeout: only renewals keep the second job from other workers
        Thread.Sleep(TimeSpan.FromSeconds(9));
        SqlServerStorage other = CreateStorage();
        using IStorageConnection otherConnection = other.GetConnection();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
        IFetchedJob? stolen = null;
        try
        {
            stolen = otherConnection.FetchNextJob(Queues, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }

        stop.Cancel();
        blocking.Rollback();
        heartbeat.Join();
        Assert.Null(stolen?.JobId);
    }

    [Fact, CleanDatabase]
    public void Execute_RenewsALockedRow_OnceItIsReleased()
    {
        SqlServerStorage storage = CreateStorage();
        InsertQueueEntry(1);
        using IStorageConnection connection = storage.GetConnection();
        using SqlServerTimeoutJob fetchedJob = (SqlServerTimeoutJob)connection.FetchNextJob(Queues, CancellationToken.None);
        DateTime? fetchedAt = fetchedJob.FetchedAt;

        using (DbConnection blocker = ConnectionUtils.CreateConnection())
        using (DbTransaction blocking = blocker.BeginTransaction())
        {
            blocker.Execute(
                $"update [{Constants.DefaultSchema}].JobQueue set Queue = Queue where JobId = 1",
                transaction: blocking);
            Thread.Sleep(TimeSpan.FromSeconds(1.5));

            storage.HeartbeatProcess.KeepAlive([fetchedJob]);

            Assert.Equal(fetchedAt, fetchedJob.FetchedAt);
            blocking.Rollback();
        }

        storage.HeartbeatProcess.KeepAlive([fetchedJob]);

        Assert.NotEqual(fetchedAt, fetchedJob.FetchedAt);
        Assert.Equal(fetchedJob.FetchedAt, GetFetchedAt(1));
    }

    [Fact, CleanDatabase]
    public void Execute_StopsTheKeepAlive_OfAJobFetchedByAnotherWorker()
    {
        SqlServerStorage storage = CreateStorage();
        InsertQueueEntry(1);
        using IStorageConnection connection = storage.GetConnection();
        using SqlServerTimeoutJob fetchedJob = (SqlServerTimeoutJob)connection.FetchNextJob(Queues, CancellationToken.None);
        using (DbConnection sql = ConnectionUtils.CreateConnection())
        {
            sql.Execute($"update [{Constants.DefaultSchema}].JobQueue set FetchedAt = dateadd(second, 1, FetchedAt) where JobId = 1");
        }

        DateTime? renewedByTheOther = GetFetchedAt(1);
        Thread.Sleep(TimeSpan.FromSeconds(1.5));

        storage.HeartbeatProcess.KeepAlive([fetchedJob]);

        Assert.Null(fetchedJob.FetchedAt);
        Assert.Equal(renewedByTheOther, GetFetchedAt(1));
    }

    [Fact, CleanDatabase]
    public void KeepAlive_WaitingForAPooledConnection_DoesNotHoldUpTheCommitsOfTheDueJobs()
    {
        string connectionString = $"{ConnectionUtils.GetConnectionString()};Max Pool Size=2;Connect Timeout=4;Application Name=keep-alive-pool";
        SqlServerStorage storage = new(connectionString, new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromSeconds(5) });
        InsertQueueEntry(1);
        InsertQueueEntry(2);
        SqlServerTimeoutJob first;
        SqlServerTimeoutJob second;
        using (IStorageConnection fetcher = storage.GetConnection())
        {
            first = (SqlServerTimeoutJob)fetcher.FetchNextJob(Queues, CancellationToken.None);
            second = (SqlServerTimeoutJob)fetcher.FetchNextJob(Queues, CancellationToken.None);
        }

        Thread.Sleep(TimeSpan.FromSeconds(1.2)); // Both keep-alives are due
        Thread heartbeat;
        TimeSpan[] commits = new TimeSpan[2];
        using (IStorageConnection worker1 = storage.GetConnection())
        using (IStorageConnection worker2 = storage.GetConnection())
        {
            // Each worker holds a pooled connection, as a state change does with the job's lock: the pool is used up
            using IDisposable lock1 = worker1.AcquireDistributedLock("worker1", TimeSpan.FromSeconds(1));
            using IDisposable lock2 = worker2.AcquireDistributedLock("worker2", TimeSpan.FromSeconds(1));

            heartbeat = new Thread(() => storage.HeartbeatProcess.KeepAlive([first, second]));
            heartbeat.Start();
            Thread.Sleep(TimeSpan.FromMilliseconds(300));

            commits[0] = TimeCommit(worker1, first);
            commits[1] = TimeCommit(worker2, second);
        }

        heartbeat.Join();
        first.Dispose();
        second.Dispose();
        Assert.All(commits, static x => Assert.True(x < TimeSpan.FromSeconds(2), $"A commit waited {x}."));
    }

    [Fact, CleanDatabase]
    public void KeepAlive_BlockedByATableLock_GivesUpSoon_AndReleasesTheJob()
    {
        SqlServerStorage storage = CreateStorage();
        InsertQueueEntry(1);
        using IStorageConnection connection = storage.GetConnection();
        using SqlServerTimeoutJob fetchedJob = (SqlServerTimeoutJob)connection.FetchNextJob(Queues, CancellationToken.None);
        Thread.Sleep(TimeSpan.FromSeconds(1.2));

        Stopwatch elapsed;
        using (DbConnection blocker = ConnectionUtils.CreateConnection())
        using (DbTransaction blocking = blocker.BeginTransaction())
        {
            blocker.Execute(
                $"select top (0) Id from [{Constants.DefaultSchema}].JobQueue with (tablockx, holdlock)",
                transaction: blocking);

            elapsed = Stopwatch.StartNew();
            storage.HeartbeatProcess.KeepAlive([fetchedJob]);
            elapsed.Stop();
            blocking.Rollback();
        }

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"The keep-alive waited {elapsed.Elapsed}.");
        storage.HeartbeatProcess.KeepAlive([fetchedJob]);
        Assert.Equal(fetchedJob.FetchedAt, GetFetchedAt(1));
    }

    [Fact, CleanDatabase]
    public void KeepAliveCommands_HaveTheSameText_ForThreeAndForSevenJobs()
    {
        SqlServerStorage storage = CreateStorage();
        using DbConnection sql = ConnectionUtils.CreateConnection();
        List<SqlServerTimeoutJob> jobs = [.. Enumerable.Range(1, 7).Select(i => new SqlServerTimeoutJob(storage, i, i.ToString(CultureInfo.InvariantCulture), i % 2 == 0 ? "default" : "a-much-longer-queue-name", DateTime.UtcNow))];
        List<KeyValuePair<SqlServerTimeoutJob, DateTime>> renewed = [.. jobs.Select(static x => new KeyValuePair<SqlServerTimeoutJob, DateTime>(x, x.FetchedAt!.Value))]; // Just created with one

        using DbCommand renewThree = SqlServerHeartbeatProcess.CreateRenewCommand(storage, sql, renewed.GetRange(0, 3));
        using DbCommand renewSeven = SqlServerHeartbeatProcess.CreateRenewCommand(storage, sql, renewed);
        using DbCommand readThree = SqlServerHeartbeatProcess.CreateReadCommand(storage, sql, renewed.GetRange(0, 3));
        using DbCommand readSeven = SqlServerHeartbeatProcess.CreateReadCommand(storage, sql, renewed);

        Assert.Equal(renewThree.CommandText, renewSeven.CommandText);
        Assert.Equal(readThree.CommandText, readSeven.CommandText);
        Assert.All(
            renewSeven.Parameters.Cast<DbParameter>().Concat(readSeven.Parameters.Cast<DbParameter>()).Where(static x => x.DbType == DbType.String),
            static x => Assert.Equal(50, x.Size));
        jobs.ForEach(static x => x.DisposeTimer());
    }

    [Fact, CleanDatabase]
    public void KeepAlive_RenewsEveryDueJob_OfABatchThatIsPadded()
    {
        SqlServerStorage storage = CreateStorage();
        for (int i = 1; i <= 7; i++)
        {
            InsertQueueEntry(i);
        }

        using IStorageConnection connection = storage.GetConnection();
        List<SqlServerTimeoutJob> jobs = [.. Enumerable.Range(0, 7).Select(_ => (SqlServerTimeoutJob)connection.FetchNextJob(Queues, CancellationToken.None))];
        List<DateTime?> fetched = [.. jobs.Select(static x => x.FetchedAt)];
        Thread.Sleep(TimeSpan.FromSeconds(1.2));

        storage.HeartbeatProcess.KeepAlive(jobs);

        Assert.All(Enumerable.Range(0, 7), i =>
        {
            Assert.NotEqual(fetched[i], jobs[i].FetchedAt);
            Assert.Equal(jobs[i].FetchedAt, GetFetchedAt(long.Parse(jobs[i].JobId, CultureInfo.InvariantCulture)));
        });
        jobs.ForEach(static x => x.Dispose());
    }

    private static TimeSpan TimeCommit(IStorageConnection connection, SqlServerTimeoutJob job)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        ((JobStorageTransaction)transaction).RemoveFromQueue(job);
        transaction.Commit();
        return elapsed.Elapsed;
    }

    private static DateTime? GetFetchedAt(long jobId)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        return sql.ExecuteScalar<DateTime?>(
            $"select FetchedAt from [{Constants.DefaultSchema}].JobQueue where JobId = @jobId",
            new { jobId });
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromSeconds(5) });
    }

    private static void InsertQueueEntry(long jobId)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        sql.Execute(
            $"insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue) values (@jobId, @queue)",
            new { jobId, queue = Queues[0] });
    }
}
