extern alias ReferencedDapper;

using System;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Storage;
using Moq;
using ReferencedDapper::Dapper;
using Xunit;
// ReSharper disable ArgumentsStyleLiteral

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.SqlServer.Tests
{
    public class SqlServerJobQueueFacts
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
        private static readonly string[] DefaultQueues = { "default" };

        [Fact]
        public void Ctor_ThrowsAnException_WhenStorageIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerJobQueue(null, new SqlServerStorageOptions()));

            Assert.Equal("storage", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenOptionsValueIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerJobQueue(new SqlServerStorage(ConnectionUtils.GetConnectionString()), null));

            Assert.Equal("options", exception.ParamName);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsNull(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                var exception = Assert.Throws<ArgumentNullException>(
                    () => queue.Dequeue(null, CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsEmpty(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                var exception = Assert.Throws<ArgumentException>(
                    () => queue.Dequeue(new string[0], CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ThrowsOperationCanceled_WhenCancellationTokenIsSetAtTheBeginning(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(DefaultQueues, cts.Token));
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldWaitIndefinitely_WhenThereAreNoJobs(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var cts = new CancellationTokenSource(200);
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(DefaultQueues, cts.Token));
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldFetchAJob_FromTheSpecifiedQueue(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (@jobId, @queue);
select scope_identity() as Id;";

            // Arrange
            UseConnection(connection =>
            {
                // ReSharper disable once UnusedVariable
                var id = (int)connection.Query(
                    arrangeSql,
                    new { jobId = 1, queue = "default" }).Single().Id;
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                // Act
                using (var payload = (SqlServerTransactionJob) queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken()))
                {
                    // Assert
                    Assert.Equal("1", payload.JobId);
                    Assert.Equal("default", payload.Queue);
                }
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_FetchesAJob_WhenJobIdIsLongValue(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (@jobId, @queue);
select scope_identity() as Id;";

            // Arrange
            UseConnection(connection =>
            {
                // ReSharper disable once UnusedVariable
                var id = (int)connection.Query(
                    arrangeSql,
                    new { jobId = int.MaxValue + 1L, queue = "default" }).Single().Id;
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                // Act
                using (var payload = (SqlServerTransactionJob) queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken()))
                {
                    // Assert
                    Assert.Equal((int.MaxValue + 1L).ToString(), payload.JobId);
                }
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldDeleteAJob(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                // Act
                using (var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken()))
                {
                    // Assert
                    Assert.NotNull(payload);

                    UseConnection(connection2 =>
                    {
                        var jobInQueue = connection2.Query($"select * from [{Constants.DefaultSchema}].JobQueue with (readpast)").SingleOrDefault();
                        Assert.Null(jobInQueue);
                    }, useMicrosoftDataSqlClient);
                }
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldFetchTimedOutJobs_FromTheSpecifiedQueue(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                // Act
                using (var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken()))
                {
                    // Assert
                    Assert.NotEmpty(payload.JobId);
                }
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldSetFetchedAt_OnlyForTheFetchedJob(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                // Act
                using (var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken()))
                {
                    // Assert
                    UseConnection(connection2 =>
                    {
                        var otherJobFetchedAt = connection2.Query<DateTime?>(
                            $"select FetchedAt from [{Constants.DefaultSchema}].JobQueue with (readpast) where JobId != @id",
                            new { id = payload.JobId }).Single();

                        Assert.Null(otherJobFetchedAt);
                    }, useMicrosoftDataSqlClient);
                }
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldFetchJobs_OnlyFromSpecifiedQueues(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                connection.Execute(
                    arrangeSql,
                    new { queue = "critical", invocationData = "", arguments = "" });
                
                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(
                        DefaultQueues,
                        CreateTimingOutCancellationToken()));
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_ShouldFetchJobs_FromMultipleQueues(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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

                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                using (var critical = (SqlServerTransactionJob) queue.Dequeue(
                    new[] { "critical", "default" },
                    CreateTimingOutCancellationToken()))
                {
                    Assert.NotNull(critical.JobId);
                    Assert.Equal("critical", critical.Queue);
                    critical.RemoveFromQueue();
                }

                using (var @default = (SqlServerTransactionJob) queue.Dequeue(
                    new[] { "critical", "default" },
                    CreateTimingOutCancellationToken()))
                {
                    Assert.NotNull(@default.JobId);
                    Assert.Equal("default", @default.Queue);
                    @default.RemoveFromQueue();
                }
            }, useMicrosoftDataSqlClient);
        }

        //---
        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldThrowAnException_WhenQueuesCollectionIsNull(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                var exception = Assert.Throws<ArgumentNullException>(
                    () => queue.Dequeue(null, CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldThrowAnException_WhenQueuesCollectionIsEmpty(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                var exception = Assert.Throws<ArgumentException>(
                    () => queue.Dequeue(new string[0], CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ThrowsOperationCanceled_WhenCancellationTokenIsSetAtTheBeginning(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(DefaultQueues, cts.Token));
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldWaitIndefinitely_WhenThereAreNoJobs(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var cts = new CancellationTokenSource(200);
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(DefaultQueues, cts.Token));
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldFetchAJob_FromTheSpecifiedQueue(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (@jobId, @queue);
select scope_identity() as Id;";

            // Arrange
            UseConnection(connection =>
            {
                var id = (int)connection.Query(
                    arrangeSql,
                    new { jobId = 1, queue = "default" }).Single().Id;
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                // Act
                var payload = (SqlServerTimeoutJob)queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                Assert.Equal(id, payload.Id);
                Assert.Equal("1", payload.JobId);
                Assert.Equal("default", payload.Queue);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldFetchTheFirstJob_FromTheSpecifiedQueue(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
output inserted.Id
values (@jobId1, @queue), (@jobId2, @queue);";

            // Arrange
            UseConnection(connection =>
            {
                var id = (int)connection.Query(
                    arrangeSql,
                    new { jobId1 = 1, jobId2 = 2, queue = "default" }).First().Id;
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                // Act
                var payload = (SqlServerTimeoutJob)queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                Assert.Equal(id, payload.Id);
                Assert.Equal("1", payload.JobId);
                Assert.Equal("default", payload.Queue);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldLeaveJobInTheQueue_ButSetItsFetchedAtValue(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                // Act
                var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                Assert.NotNull(payload);

                var fetchedAt = connection.Query<DateTime?>(
                    $"select FetchedAt from [{Constants.DefaultSchema}].JobQueue where JobId = @id",
                    new { id = payload.JobId }).Single();

                Assert.NotNull(fetchedAt);
                Assert.True(fetchedAt > DateTime.UtcNow.AddMinutes(-1));
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldFetchATimedOutJobs_FromTheSpecifiedQueue(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                // Act
                var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                Assert.NotEmpty(payload.JobId);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldSetFetchedAt_OnlyForTheFetchedJob(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                // Act
                var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                var otherJobFetchedAt = connection.Query<DateTime?>(
                    $"select FetchedAt from [{Constants.DefaultSchema}].JobQueue where JobId != @id",
                    new { id = payload.JobId }).Single();

                Assert.Null(otherJobFetchedAt);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldFetchJobs_OnlyFromSpecifiedQueues(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values (@invocationData, @arguments, getutcdate())
insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue)
values (scope_identity(), @queue)";

            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                connection.Execute(
                    arrangeSql,
                    new { queue = "critical", invocationData = "", arguments = "" });

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(
                        DefaultQueues,
                        CreateTimingOutCancellationToken()));
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Dequeue_InvisibilityTimeout_ShouldFetchJobs_FromMultipleQueues(bool useMicrosoftDataSqlClient)
        {
            var arrangeSql = $@"
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

                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: DefaultTimeout);

                var critical = (SqlServerTimeoutJob)queue.Dequeue(
                    new[] { "critical", "default" },
                    CreateTimingOutCancellationToken());

                Assert.NotNull(critical.JobId);
                Assert.Equal("critical", critical.Queue);

                var @default = (SqlServerTimeoutJob)queue.Dequeue(
                    new[] { "critical", "default" },
                    CreateTimingOutCancellationToken());

                Assert.NotNull(@default.JobId);
                Assert.Equal("default", @default.Queue);
            }, useMicrosoftDataSqlClient);
        }

        // WaitHandle.WaitAny takes at most 64 handles. A wait handle per queue plus the cancellation event used to throw
        // NotSupportedException on every idle poll of a worker listening to 64 queues or more.
        [Theory, CleanDatabase]
        [InlineData(false, 64), InlineData(false, 100), InlineData(true, 64), InlineData(true, 100)]
        public void Dequeue_WaitsUntilCanceled_WhenListeningToMoreThan63Queues(bool slidingInvisibility, int queueCount)
        {
            var queue = CreateJobQueue(useMicrosoftDataSqlClient: true, invisibilityTimeout: slidingInvisibility ? DefaultTimeout : (TimeSpan?)null);

            Assert.Throws<OperationCanceledException>(
                () => queue.Dequeue(CreateQueueNames(queueCount), CreateTimingOutCancellationToken()));
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public async Task Dequeue_FindsAJobInAQueueBeyondThe63rd_OnTheNextPoll(bool slidingInvisibility)
        {
            var queue = CreateJobQueue(useMicrosoftDataSqlClient: true, invisibilityTimeout: slidingInvisibility ? DefaultTimeout : (TimeSpan?)null);

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var fetch = Task.Run(() => queue.Dequeue(CreateQueueNames(100), cts.Token));

                // Inserted while the worker waits, into a queue it has no wake-up signal for
                await Task.Delay(500, TestContext.Current.CancellationToken);
                UseConnection(
                    connection => connection.Execute($"insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue) values (1, N'q99')"),
                    useMicrosoftDataSqlClient: true);

                using (var fetchedJob = await fetch)
                {
                    Assert.Equal("1", fetchedJob.JobId);
                    Assert.Equal("q99", GetQueue(fetchedJob));
                }
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public async Task Dequeue_IsWokenAtOnce_ByAJobEnqueuedInOneOfTheFirst63Queues_WhenListeningToMore(bool slidingInvisibility)
        {
            var storage = new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions
            {
                PrepareSchemaIfNecessary = false,
                SlidingInvisibilityTimeout = slidingInvisibility ? DefaultTimeout : (TimeSpan?)null,
                // Longer than the test may take, so that only the wake-up signal can explain the fetch
                QueuePollInterval = TimeSpan.FromMinutes(5)
            });
            var queue = new SqlServerJobQueue(storage, storage.Options);

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var fetch = Task.Run(() => queue.Dequeue(CreateQueueNames(100), cts.Token));

                // The signal is set only when the worker has registered it
                while (!SqlServerJobQueue.NewItemInQueueEvents.ContainsKey(Tuple.Create(storage, "q62")))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }

                using (var connection = storage.GetConnection())
                using (var transaction = connection.CreateWriteTransaction())
                {
                    transaction.AddToQueue("q62", "1");
                    transaction.Commit();
                }

                using (var fetchedJob = await fetch)
                {
                    Assert.Equal("q62", GetQueue(fetchedJob));
                }
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Enqueue_AddsAJobToTheQueue(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                using (var transaction = connection.BeginTransaction())
                {
                    queue.Enqueue(connection, transaction, "default", "1");
                    transaction.Commit();
                }

                var record = connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
                Assert.Equal("1", record.JobId.ToString());
                Assert.Equal("default", record.Queue);
                Assert.Null(record.FetchedAt);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Enqueue_ThrowsAnException_WhenTheGivenQueueIsTooLong(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queueName = "some-really-long-queue-name-that-should-cause-an-exception-to-be-thrown-and-not-ignored";
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                var exception = Assert.Throws<ArgumentException>(() =>
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        queue.Enqueue(connection, transaction, queueName, "1");
                        transaction.Commit();
                    }
                });

                var record = connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue").SingleOrDefault();
                Assert.Null(record);
                Assert.Contains("at most 50 characters", exception.Message);
            }, useMicrosoftDataSqlClient);
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Enqueue_AddsAJob_WhenIdIsLongValue(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);
                
                using (var transaction = connection.BeginTransaction())
                {
                    queue.Enqueue(connection, transaction, "default", (int.MaxValue + 1L).ToString());
                    transaction.Commit();
                }

                var record = connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
                Assert.Equal((int.MaxValue + 1L).ToString(), record.JobId.ToString());
            }, useMicrosoftDataSqlClient);
        }

        [Fact, CleanDatabase]
        public void ReadTimeoutJob_LeavesNoJobTracked_WhenTheReaderFailsAfterTheRow()
        {
            var storage = new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5) });
            var reader = CreateFetchReader();
            reader.Setup(x => x.NextResult()).Throws(new InvalidOperationException("The connection is broken."));

            Assert.Throws<InvalidOperationException>(() => SqlServerJobQueue.ReadTimeoutJob(storage, reader.Object));

            Assert.Equal(0, storage.HeartbeatProcess.TrackedCount);
        }

        [Fact, CleanDatabase]
        public void ReadTimeoutJob_ReturnsATrackedJob_WhenTheReaderSucceeds()
        {
            var storage = new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5) });
            var reader = CreateFetchReader();
            reader.Setup(x => x.NextResult()).Returns(false);

            using (var job = SqlServerJobQueue.ReadTimeoutJob(storage, reader.Object))
            {
                Assert.Equal("2", job.JobId);
                Assert.Equal("default", job.Queue);
                Assert.Equal(1, storage.HeartbeatProcess.TrackedCount);
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void Enqueue_DoesNothing_WhenTheJobIdIsNotANumber(bool useMicrosoftDataSqlClient)
        {
            UseConnection(connection =>
            {
                var queue = CreateJobQueue(useMicrosoftDataSqlClient, invisibilityTimeout: null);

                using (var transaction = connection.BeginTransaction())
                {
                    queue.Enqueue(connection, transaction, "default", "not-a-number");
                    transaction.Commit();
                }

                Assert.Empty(connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue"));
            }, useMicrosoftDataSqlClient);
        }

        private static Mock<DbDataReader> CreateFetchReader()
        {
            var reader = new Mock<DbDataReader>();
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
            return Enumerable.Range(0, count).Select(static i => "q" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        }

        private static CancellationToken CreateTimingOutCancellationToken()
        {
            var source = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            return source.Token;
        }

        private static SqlServerJobQueue CreateJobQueue(bool useMicrosoftDataSqlClient, TimeSpan? invisibilityTimeout)
        {
            var storage = new SqlServerStorage(() => ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient));
            return new SqlServerJobQueue(storage, new SqlServerStorageOptions { SlidingInvisibilityTimeout = invisibilityTimeout });
        }

        private static void UseConnection([InstantHandle] Action<DbConnection> action, bool useMicrosoftDataSqlClient)
        {
            using (var connection = ConnectionUtils.CreateConnection(useMicrosoftDataSqlClient))
            {
                action(connection);
            }
        }

        private static string GetQueue(IFetchedJob fetchedJob)
        {
            return fetchedJob is SqlServerTimeoutJob timeoutJob
                ? timeoutJob.Queue
                : ((SqlServerTransactionJob)fetchedJob).Queue;
        }
    }
}
