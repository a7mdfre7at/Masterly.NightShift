using System;
using System.Linq;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    /// <summary>
    /// End-to-end checks of the bulk operations and the state and result readers against a real SQL Server.
    /// No server runs, so the jobs stay in the states the tests put them in.
    /// </summary>
    public class BulkOperationsFacts
    {
        [Fact, CleanDatabase]
        public void BatchJob_RequeueFailed_RequeuesTheFailedJobs_AndCancel_DeletesTheUnfinishedOnes()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            string failed = null, succeeded = null, scheduled = null;

            var batchId = BatchJob.StartNew(client, storage, batch =>
            {
                failed = batch.Schedule(() => BulkJobs.Work(), TimeSpan.FromDays(1));
                succeeded = batch.Schedule(() => BulkJobs.Work(), TimeSpan.FromDays(1));
                scheduled = batch.Schedule(() => BulkJobs.Work(), TimeSpan.FromDays(1));
            });
            Assert.True(client.ChangeState(failed, new FailedState(new TimeoutException())));
            Assert.True(client.ChangeState(succeeded, new SucceededState(null, 0, 0)));

            var requeued = BatchJob.RequeueFailed(client, storage, batchId);

            Assert.Equal(1, requeued.Processed);
            Assert.True(requeued.Complete);
            Assert.Equal(EnqueuedState.StateName, storage.GetJobState(failed).Name);

            var canceled = BatchJob.Cancel(client, storage, batchId);

            Assert.Equal(2, canceled.Processed);
            Assert.True(canceled.Complete);
            Assert.Equal(DeletedState.StateName, storage.GetJobState(failed).Name);
            Assert.Equal(DeletedState.StateName, storage.GetJobState(scheduled).Name);
            Assert.Equal(SucceededState.StateName, storage.GetJobState(succeeded).Name);

            using (var connection = storage.GetConnection())
            {
                Assert.True(BatchJob.Get(connection, batchId).IsFinished);
            }
        }

        [Fact, CleanDatabase]
        public void BatchJob_Cancel_Throws_ForAnUnknownBatch()
        {
            var storage = CreateStorage();

            Assert.Throws<InvalidOperationException>(() => BatchJob.Cancel(new BackgroundJobClient(storage), storage, "missing"));
        }

        [Fact, CleanDatabase]
        public void RequeueFailedJobs_RequeuesTheSelectedFailedJobs_ThenTheOthers()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var timeouts = Enumerable.Range(0, 3).Select(_ => Fail(client, new TimeoutException())).ToArray();
            var others = Enumerable.Range(0, 2).Select(_ => Fail(client, new InvalidOperationException())).ToArray();

            var first = storage.RequeueFailedJobs(job => job.ExceptionType == typeof(TimeoutException).FullName);

            Assert.Equal(3, first.Processed);
            Assert.True(first.Complete);
            Assert.All(timeouts, id => Assert.Equal(EnqueuedState.StateName, storage.GetJobState(id).Name));
            Assert.All(others, id => Assert.Equal(FailedState.StateName, storage.GetJobState(id).Name));
            Assert.Equal(2, storage.GetMonitoringApi().FailedCount());

            var second = storage.RequeueFailedJobs();

            Assert.Equal(2, second.Processed);
            Assert.Equal(0, storage.GetMonitoringApi().FailedCount());
        }

        [Fact, CleanDatabase]
        public void GetJobState_And_TryGetResult_ReadASucceededJob()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var jobId = client.Schedule(() => BulkJobs.Work(), TimeSpan.FromDays(1));
            Assert.True(client.ChangeState(jobId, new SucceededState(new BulkResult { Count = 7, Name = "import" }, 0, 0)));

            var state = storage.GetJobState(jobId);

            Assert.Equal(SucceededState.StateName, state.Name);
            Assert.True(state.Data.ContainsKey("SucceededAt"));
            Assert.True(DateTime.UtcNow - state.JobCreatedAt < TimeSpan.FromMinutes(5));

            Assert.True(storage.TryGetResult<BulkResult>(jobId, out var result));
            Assert.Equal(7, result.Count);
            Assert.Equal("import", result.Name);
        }

        [Fact, CleanDatabase]
        public void TryGetResult_ReturnsFalse_ForAJobThatHasNotSucceeded_OrDoesNotExist()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var jobId = client.Schedule(() => BulkJobs.Work(), TimeSpan.FromDays(1));

            Assert.False(storage.TryGetResult<int>(jobId, out _));
            Assert.False(storage.TryGetResult<int>("123456789", out _));
            Assert.Null(storage.GetJobState("123456789"));
        }

        private static string Fail(IBackgroundJobClient client, Exception exception)
        {
            var jobId = client.Schedule(() => BulkJobs.Work(), TimeSpan.FromDays(1));
            Assert.True(client.ChangeState(jobId, new FailedState(exception)));
            return jobId;
        }

        private static SqlServerStorage CreateStorage()
        {
            return new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
        }
    }

    public sealed class BulkResult
    {
        public int Count { get; set; }
        public string Name { get; set; }
    }

    public static class BulkJobs
    {
        // Failed must stay failed: no retry turns it into Scheduled
        [AutomaticRetry(Attempts = 0)]
        public static void Work()
        {
        }
    }
}
