extern alias ReferencedDapper;
using System.Data;
using System.Data.Common;
using ReferencedDapper::Dapper;
// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerTimeoutJobFacts
{
    private const string JobId = "id";
    private const string Queue = "queue";
    private static readonly DateTime FetchedAt = DateTime.UtcNow;

    [Fact]
    public void Ctor_ThrowsAnException_WhenConnectionIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerTimeoutJob(null, 1, JobId, Queue, FetchedAt));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void Ctor_ThrowsAnException_WhenJobIdIsNull()
    {
        UseConnection((sql, storage) =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerTimeoutJob(storage, 1, null, Queue, FetchedAt));

            Assert.Equal("jobId", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void Ctor_ThrowsAnException_WhenQueueIsNull()
    {
        UseConnection((sql, storage) =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerTimeoutJob(storage, 1, JobId, null, FetchedAt));

            Assert.Equal("queue", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void Ctor_CorrectlySets_AllInstanceProperties()
    {
        UseConnection((sql, storage) =>
        {
            using SqlServerTimeoutJob fetchedJob = new(storage, 1, JobId, Queue, FetchedAt);
            Assert.Equal(1, fetchedJob.Id);
            Assert.Equal(JobId, fetchedJob.JobId);
            Assert.Equal(Queue, fetchedJob.Queue);
            Assert.Equal(FetchedAt, fetchedJob.FetchedAt);
        });
    }

    [Fact, CleanDatabase]
    public void RemoveFromQueue_ReallyDeletesTheJobFromTheQueue()
    {
        UseConnection((sql, storage) =>
        {
            // Arrange
            int id = CreateJobQueueRecord(sql, "1", "default", FetchedAt);
            using SqlServerTimeoutJob processingJob = new(storage, id, "1", "default", FetchedAt);
            processingJob.DisposeTimer();

            // Act
            processingJob.RemoveFromQueue();

            // Assert
            int count = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Equal(0, count);
        });
    }

    [Fact, CleanDatabase]
    public void RemoveFromQueue_DoesNotDelete_UnrelatedJobs()
    {
        UseConnection((sql, storage) =>
        {
            // Arrange
            CreateJobQueueRecord(sql, "1", "default", FetchedAt);
            CreateJobQueueRecord(sql, "1", "critical", FetchedAt);
            CreateJobQueueRecord(sql, "2", "default", FetchedAt);

            using SqlServerTimeoutJob fetchedJob = new(storage, 999, "1", "default", FetchedAt);
            fetchedJob.DisposeTimer();

            // Act
            fetchedJob.RemoveFromQueue();

            // Assert
            int count = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Equal(3, count);
        });
    }

    [Fact, CleanDatabase]
    public void Requeue_SetsFetchedAtValueToNull()
    {
        UseConnection((sql, storage) =>
        {
            // Arrange
            int id = CreateJobQueueRecord(sql, "1", "default", FetchedAt);
            using SqlServerTimeoutJob processingJob = new(storage, id, "1", "default", FetchedAt);
            processingJob.DisposeTimer();

            // Act
            processingJob.Requeue();

            // Assert
            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Null(record.FetchedAt);
        });
    }

    [Fact, CleanDatabase]
    public void Timer_UpdatesFetchedAtColumn()
    {
        UseConnection((sql, storage) =>
        {
            // Arrange
            int id = CreateJobQueueRecord(sql, "1", "default", FetchedAt);
            using SqlServerTimeoutJob processingJob = new(storage, id, "1", "default", FetchedAt);
            processingJob.DisposeTimer();
            Thread.Sleep(TimeSpan.FromSeconds(2));
            processingJob.ExecuteKeepAliveQueryIfRequired();

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();

            Assert.NotNull(processingJob.FetchedAt);
            Assert.Equal<DateTime?>(processingJob.FetchedAt, record.FetchedAt);
            DateTime now = DateTime.UtcNow;
            Assert.True(now.AddSeconds(-5) < record.FetchedAt, (now - record.FetchedAt).ToString());
        });
    }

    [Fact, CleanDatabase]
    public void RemoveFromQueue_AfterTimer_RemovesJobFromTheQueue()
    {
        UseConnection((sql, storage) =>
        {
            // Arrange
            int id = CreateJobQueueRecord(sql, "1", "default", FetchedAt);
            using SqlServerTimeoutJob processingJob = new(storage, id, "1", "default", FetchedAt);
            Thread.Sleep(TimeSpan.FromSeconds(1));
            processingJob.DisposeTimer();

            // Act
            processingJob.RemoveFromQueue();

            // Assert
            int count = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Equal(0, count);
        });
    }

    [Fact, CleanDatabase]
    public void RequeueQueue_AfterTimer_SetsFetchedAtValueToNull()
    {
        UseConnection((sql, storage) =>
        {
            // Arrange
            int id = CreateJobQueueRecord(sql, "1", "default", FetchedAt);
            using SqlServerTimeoutJob processingJob = new(storage, id, "1", "default", FetchedAt);
            Thread.Sleep(TimeSpan.FromSeconds(1));
            processingJob.DisposeTimer();

            // Act
            processingJob.Requeue();

            // Assert
            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Null(record.FetchedAt);
        });
    }

    [Fact, CleanDatabase]
    public void Dispose_SetsFetchedAtValueToNull_IfThereWereNoCallsToComplete()
    {
        UseConnection((sql, storage) =>
        {
            // Arrange
            int id = CreateJobQueueRecord(sql, "1", "default", FetchedAt);
            using SqlServerTimeoutJob processingJob = new(storage, id, "1", "default", FetchedAt);
            // Act
            processingJob.Dispose();

            // Assert
            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Null(record.FetchedAt);
        });
    }

    private static int CreateJobQueueRecord(IDbConnection connection, string jobId, string queue, DateTime? fetchedAt)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue, FetchedAt)
values (@id, @queue, @fetchedAt);
select scope_identity() as Id";

        return connection.QuerySingle<int>(arrangeSql, new { id = jobId, queue, fetchedAt });
    }

    private static void UseConnection(Action<IDbConnection, SqlServerStorage> action)
    {
        SqlServerStorage storage = new(
            ConnectionUtils.CreateConnection,
            new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromSeconds(5) });

        using DbConnection connection = ConnectionUtils.CreateConnection();
        action(connection, storage);
    }
}