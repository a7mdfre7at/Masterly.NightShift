extern alias ReferencedDapper;
using System.Data.Common;
using System.Globalization;
using Masterly.NightShift.Annotations;
using ReferencedDapper::Dapper;
// ReSharper disable ArgumentsStyleLiteral

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerJobQueueFacts : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] DefaultQueues = ["default"];

    private readonly List<CancellationTokenSource> _timeouts = [];

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerJobQueue(null, new SqlServerStorageOptions()));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenOptionsValueIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerJobQueue(new SqlServerStorage(ConnectionUtils.GetConnectionString()), null));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsNull()
    {
        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => queue.Dequeue(null, CreateTimingOutCancellationToken()));

            Assert.Equal("queues", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsEmpty()
    {
        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => queue.Dequeue([], CreateTimingOutCancellationToken()));

            Assert.Equal("queues", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ThrowsOperationCanceled_WhenCancellationTokenIsSetAtTheBeginning()
    {
        UseConnection(connection =>
        {
            using CancellationTokenSource cts = new();
            cts.Cancel();
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            Assert.Throws<OperationCanceledException>(
                () => queue.Dequeue(DefaultQueues, cts.Token));
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldWaitIndefinitely_WhenThereAreNoJobs()
    {
        UseConnection(connection =>
        {
            using CancellationTokenSource cts = new(200);
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            Assert.Throws<OperationCanceledException>(
                () => queue.Dequeue(DefaultQueues, cts.Token));
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldFetchAJob_FromTheSpecifiedQueue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (@jobId, @queue);
select scope_identity() as Id;";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new { jobId = 1, queue = "default" });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            // Act
            using SqlServerTransactionJob payload = (SqlServerTransactionJob)queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());
            // Assert
            Assert.Equal("1", payload.JobId);
            Assert.Equal("default", payload.Queue);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_FetchesAJob_WhenJobIdIsLongValue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (@jobId, @queue);
select scope_identity() as Id;";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new { jobId = int.MaxValue + 1L, queue = "default" });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            // Act
            using SqlServerTransactionJob payload = (SqlServerTransactionJob)queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());
            // Assert
            Assert.Equal((int.MaxValue + 1L).ToString(), payload.JobId);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldDeleteAJob()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new { invocationData = "", arguments = "", queue = "default" });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            // Act
            using IFetchedJob payload = queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());
            // Assert
            Assert.NotNull(payload);

            UseConnection(connection2 =>
            {
                dynamic? jobInQueue = connection2.Query($"select * from [{Constants.DefaultSchema}].JobQueue with (readpast)").SingleOrDefault();
                Assert.Null(jobInQueue);
            });
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldFetchTimedOutJobs_FromTheSpecifiedQueue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, dateadd(minute, -60, getutcdate()))
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue, FetchedAt)
values (scope_identity(), @queue, @fetchedAt)";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new
                {
                    queue = "default",
                    fetchedAt = DateTime.UtcNow.AddDays(-1),
                    invocationData = "",
                    arguments = ""
                });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            // Act
            using IFetchedJob payload = queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());
            // Assert
            Assert.NotEmpty(payload.JobId);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldSetFetchedAt_OnlyForTheFetchedJob()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new[]
                {
                    new { queue = "default", invocationData = "", arguments = "" },
                    new { queue = "default", invocationData = "", arguments = "" }
                });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            // Act
            using IFetchedJob payload = queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());
            // Assert
            UseConnection(connection2 =>
            {
                DateTime? otherJobFetchedAt = connection2.Query<DateTime?>(
                    $"select FetchedAt from [{Constants.DefaultSchema}].JobQueue with (readpast) where JobId != @id",
                    new { id = payload.JobId }).Single();

                Assert.Null(otherJobFetchedAt);
            });
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldFetchJobs_OnlyFromSpecifiedQueues()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            connection.Execute(
                arrangeSql,
                new { queue = "critical", invocationData = "", arguments = "" });

            Assert.Throws<OperationCanceledException>(
                () => queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken()));
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_ShouldFetchJobs_FromMultipleQueues()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new[]
                {
                    new { queue = "default", invocationData = "", arguments = "" },
                    new { queue = "critical", invocationData = "", arguments = "" }
                });

            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            using (SqlServerTransactionJob critical = (SqlServerTransactionJob)queue.Dequeue(
                ["critical", "default"],
                CreateTimingOutCancellationToken()))
            {
                Assert.NotNull(critical.JobId);
                Assert.Equal("critical", critical.Queue);
                critical.RemoveFromQueue();
            }

            using SqlServerTransactionJob @default = (SqlServerTransactionJob)queue.Dequeue(
                ["critical", "default"],
                CreateTimingOutCancellationToken());
            Assert.NotNull(@default.JobId);
            Assert.Equal("default", @default.Queue);
            @default.RemoveFromQueue();
        });
    }

    //---
    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldThrowAnException_WhenQueuesCollectionIsNull()
    {
        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => queue.Dequeue(null, CreateTimingOutCancellationToken()));

            Assert.Equal("queues", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldThrowAnException_WhenQueuesCollectionIsEmpty()
    {
        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => queue.Dequeue([], CreateTimingOutCancellationToken()));

            Assert.Equal("queues", exception.ParamName);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ThrowsOperationCanceled_WhenCancellationTokenIsSetAtTheBeginning()
    {
        UseConnection(connection =>
        {
            using CancellationTokenSource cts = new();
            cts.Cancel();
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            Assert.Throws<OperationCanceledException>(
                () => queue.Dequeue(DefaultQueues, cts.Token));
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldWaitIndefinitely_WhenThereAreNoJobs()
    {
        UseConnection(connection =>
        {
            using CancellationTokenSource cts = new(200);
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            Assert.Throws<OperationCanceledException>(
                () => queue.Dequeue(DefaultQueues, cts.Token));
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldFetchAJob_FromTheSpecifiedQueue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (@jobId, @queue);
select scope_identity() as Id;";

        // Arrange
        UseConnection(connection =>
        {
            int id = connection.QuerySingle<int>(
                arrangeSql,
                new { jobId = 1, queue = "default" });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            // Act
            SqlServerTimeoutJob payload = (SqlServerTimeoutJob)queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());

            // Assert
            Assert.Equal(id, payload.Id);
            Assert.Equal("1", payload.JobId);
            Assert.Equal("default", payload.Queue);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldFetchTheFirstJob_FromTheSpecifiedQueue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
output inserted.Id
values (@jobId1, @queue), (@jobId2, @queue);";

        // Arrange
        UseConnection(connection =>
        {
            int id = connection.QueryFirst<int>(
                arrangeSql,
                new { jobId1 = 1, jobId2 = 2, queue = "default" });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            // Act
            SqlServerTimeoutJob payload = (SqlServerTimeoutJob)queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());

            // Assert
            Assert.Equal(id, payload.Id);
            Assert.Equal("1", payload.JobId);
            Assert.Equal("default", payload.Queue);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldLeaveJobInTheQueue_ButSetItsFetchedAtValue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new { invocationData = "", arguments = "", queue = "default" });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            // Act
            IFetchedJob payload = queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());

            // Assert
            Assert.NotNull(payload);

            DateTime? fetchedAt = connection.Query<DateTime?>(
                $"select FetchedAt from [{Constants.DefaultSchema}].JobQueue where JobId = @id",
                new { id = payload.JobId }).Single();

            Assert.NotNull(fetchedAt);
            Assert.True(fetchedAt > DateTime.UtcNow.AddMinutes(-1));
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldFetchATimedOutJobs_FromTheSpecifiedQueue()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue, FetchedAt)
values (scope_identity(), @queue, @fetchedAt)";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new
                {
                    queue = "default",
                    fetchedAt = DateTime.UtcNow.AddDays(-1),
                    invocationData = "",
                    arguments = ""
                });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            // Act
            IFetchedJob payload = queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());

            // Assert
            Assert.NotEmpty(payload.JobId);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldSetFetchedAt_OnlyForTheFetchedJob()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        // Arrange
        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new[]
                {
                    new { queue = "default", invocationData = "", arguments = "" },
                    new { queue = "default", invocationData = "", arguments = "" }
                });
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            // Act
            IFetchedJob payload = queue.Dequeue(
                DefaultQueues,
                CreateTimingOutCancellationToken());

            // Assert
            DateTime? otherJobFetchedAt = connection.Query<DateTime?>(
                $"select FetchedAt from [{Constants.DefaultSchema}].JobQueue where JobId != @id",
                new { id = payload.JobId }).Single();

            Assert.Null(otherJobFetchedAt);
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldFetchJobs_OnlyFromSpecifiedQueues()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            connection.Execute(
                arrangeSql,
                new { queue = "critical", invocationData = "", arguments = "" });

            Assert.Throws<OperationCanceledException>(
                () => queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken()));
        });
    }

    [Fact, CleanDatabase]
    public void Dequeue_InvisibilityTimeout_ShouldFetchJobs_FromMultipleQueues()
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

        UseConnection(connection =>
        {
            connection.Execute(
                arrangeSql,
                new[]
                {
                    new { queue = "default", invocationData = "", arguments = "" },
                    new { queue = "critical", invocationData = "", arguments = "" }
                });

            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: DefaultTimeout);

            SqlServerTimeoutJob critical = (SqlServerTimeoutJob)queue.Dequeue(
                ["critical", "default"],
                CreateTimingOutCancellationToken());

            Assert.NotNull(critical.JobId);
            Assert.Equal("critical", critical.Queue);

            SqlServerTimeoutJob @default = (SqlServerTimeoutJob)queue.Dequeue(
                ["critical", "default"],
                CreateTimingOutCancellationToken());

            Assert.NotNull(@default.JobId);
            Assert.Equal("default", @default.Queue);
        });
    }

    // WaitHandle.WaitAny takes at most 64 handles. A wait handle per queue plus the cancellation event used to throw
    // NotSupportedException on every idle poll of a worker listening to 64 queues or more.
    [Theory, CleanDatabase]
    [InlineData(false, 64), InlineData(false, 100), InlineData(true, 64), InlineData(true, 100)]
    public void Dequeue_WaitsUntilCanceled_WhenListeningToMoreThan63Queues(bool slidingInvisibility, int queueCount)
    {
        SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: slidingInvisibility ? DefaultTimeout : (TimeSpan?)null);

        Assert.Throws<OperationCanceledException>(
            () => queue.Dequeue(CreateQueueNames(queueCount), CreateTimingOutCancellationToken()));
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public async Task Dequeue_FindsAJobInAQueueBeyondThe63rd_OnTheNextPoll(bool slidingInvisibility)
    {
        SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: slidingInvisibility ? DefaultTimeout : (TimeSpan?)null);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task<IFetchedJob> fetch = Task.Run(() => queue.Dequeue(CreateQueueNames(100), cts.Token));

        // Inserted while the worker waits, into a queue it has no wake-up signal for
        await Task.Delay(500, TestContext.Current.CancellationToken);
        UseConnection(
            connection => connection.Execute($"insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue) values (1, N'q99')"));

        using IFetchedJob fetchedJob = await fetch;
        Assert.Equal("1", fetchedJob.JobId);
        Assert.Equal("q99", GetQueue(fetchedJob));
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public async Task Dequeue_IsWokenAtOnce_ByAJobEnqueuedInOneOfTheFirst63Queues_WhenListeningToMore(bool slidingInvisibility)
    {
        SqlServerStorage storage = new(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions
        {
            PrepareSchemaIfNecessary = false,
            SlidingInvisibilityTimeout = slidingInvisibility ? DefaultTimeout : (TimeSpan?)null,
            // Longer than the test may take, so that only the wake-up signal can explain the fetch
            QueuePollInterval = TimeSpan.FromMinutes(5)
        });
        SqlServerJobQueue queue = new(storage, storage.Options);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task<IFetchedJob> fetch = Task.Run(() => queue.Dequeue(CreateQueueNames(100), cts.Token));

        // The signal is set only when the worker has registered it
        while (!SqlServerJobQueue.NewItemInQueueEvents.ContainsKey(Tuple.Create(storage, "q62")))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToQueue("q62", "1");
            transaction.Commit();
        }

        using IFetchedJob fetchedJob = await fetch;
        Assert.Equal("q62", GetQueue(fetchedJob));
    }

    [Fact, CleanDatabase]
    public void Enqueue_AddsAJobToTheQueue()
    {
        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            using (DbTransaction transaction = connection.BeginTransaction())
            {
                queue.Enqueue(connection, transaction, "default", "1");
                transaction.Commit();
            }

            dynamic record = connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Equal("1", record.JobId.ToString());
            Assert.Equal("default", record.Queue);
            Assert.Null(record.FetchedAt);
        });
    }

    [Fact, CleanDatabase]
    public void Enqueue_ThrowsAnException_WhenTheGivenQueueIsTooLong()
    {
        UseConnection(connection =>
        {
            string queueName = "some-really-long-queue-name-that-should-cause-an-exception-to-be-thrown-and-not-ignored";
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            {
                using DbTransaction transaction = connection.BeginTransaction();
                queue.Enqueue(connection, transaction, queueName, "1");
                transaction.Commit();
            });

            dynamic? record = connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue").SingleOrDefault();
            Assert.Null(record);
            Assert.Contains("at most 50 characters", exception.Message);
        });
    }

    [Fact, CleanDatabase]
    public void Enqueue_AddsAJob_WhenIdIsLongValue()
    {
        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            using (DbTransaction transaction = connection.BeginTransaction())
            {
                queue.Enqueue(connection, transaction, "default", (int.MaxValue + 1L).ToString());
                transaction.Commit();
            }

            dynamic record = connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Equal((int.MaxValue + 1L).ToString(), record.JobId.ToString());
        });
    }

    [Fact, CleanDatabase]
    public void ReadTimeoutJob_LeavesNoJobTracked_WhenTheReaderFailsAfterTheRow()
    {
        SqlServerStorage storage = new(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5) });
        Mock<DbDataReader> reader = CreateFetchReader();
        reader.Setup(x => x.NextResult()).Throws(new InvalidOperationException("The connection is broken."));

        Assert.Throws<InvalidOperationException>(() => SqlServerJobQueue.ReadTimeoutJob(storage, reader.Object));

        Assert.Equal(0, storage.HeartbeatProcess.TrackedCount);
    }

    [Fact, CleanDatabase]
    public void ReadTimeoutJob_ReturnsATrackedJob_WhenTheReaderSucceeds()
    {
        SqlServerStorage storage = new(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5) });
        Mock<DbDataReader> reader = CreateFetchReader();
        reader.Setup(x => x.NextResult()).Returns(false);

        using SqlServerTimeoutJob? job = SqlServerJobQueue.ReadTimeoutJob(storage, reader.Object);
        Assert.Equal("2", job.JobId);
        Assert.Equal("default", job.Queue);
        Assert.Equal(1, storage.HeartbeatProcess.TrackedCount);
    }

    [Fact, CleanDatabase]
    public void Enqueue_DoesNothing_WhenTheJobIdIsNotANumber()
    {
        UseConnection(connection =>
        {
            SqlServerJobQueue queue = CreateJobQueue(invisibilityTimeout: null);

            using (DbTransaction transaction = connection.BeginTransaction())
            {
                queue.Enqueue(connection, transaction, "default", "not-a-number");
                transaction.Commit();
            }

            Assert.Empty(connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue"));
        });
    }

    private static Mock<DbDataReader> CreateFetchReader()
    {
        Mock<DbDataReader> reader = new();
        reader.SetupSequence(x => x.Read()).Returns(true).Returns(false);
        reader.Setup(x => x.GetOrdinal("Id")).Returns(0);
        reader.Setup(x => x.GetOrdinal("JobId")).Returns(1);
        reader.Setup(x => x.GetOrdinal("Queue")).Returns(2);
        reader.Setup(x => x.GetOrdinal("FetchedAt")).Returns(3);
        reader.Setup(x => x.GetValue(0)).Returns(1L);
        reader.Setup(x => x.GetValue(1)).Returns(2L);
        reader.Setup(x => x.GetString(2)).Returns("default");
        reader.Setup(x => x.GetDateTime(3)).Returns(DateTime.UtcNow);
        return reader;
    }

    private static string[] CreateQueueNames(int count)
    {
        return [.. Enumerable.Range(0, count).Select(static i => "q" + i.ToString(CultureInfo.InvariantCulture))];
    }

    /// <summary>Disposes the sources of the tokens the test created.</summary>
    public void Dispose()
    {
        foreach (CancellationTokenSource timeout in _timeouts)
        {
            timeout.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A token canceled a second from now, whose source the test disposes when it ends.</summary>
    private CancellationToken CreateTimingOutCancellationToken()
    {
        CancellationTokenSource source = new(TimeSpan.FromSeconds(1));
        _timeouts.Add(source);
        return source.Token;
    }

    private static SqlServerJobQueue CreateJobQueue(TimeSpan? invisibilityTimeout)
    {
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection);
        return new SqlServerJobQueue(storage, new SqlServerStorageOptions { SlidingInvisibilityTimeout = invisibilityTimeout });
    }

    private static void UseConnection([InstantHandle] Action<DbConnection> action)
    {
        using DbConnection connection = ConnectionUtils.CreateConnection();
        action(connection);
    }

    private static string GetQueue(IFetchedJob fetchedJob)
    {
        return fetchedJob is SqlServerTimeoutJob timeoutJob
            ? timeoutJob.Queue
            : ((SqlServerTransactionJob)fetchedJob).Queue;
    }
}
