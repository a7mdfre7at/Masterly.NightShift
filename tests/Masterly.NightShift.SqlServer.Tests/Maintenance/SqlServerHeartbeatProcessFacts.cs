extern alias ReferencedDapper;
using System.Data.Common;
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
