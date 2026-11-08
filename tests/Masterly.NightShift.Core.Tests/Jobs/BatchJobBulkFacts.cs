using System;
using System.Collections.Generic;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class BatchJobBulkFacts
    {
        private const string BatchId = "batch-1";

        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();

        public BatchJobBulkFacts()
        {
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);
        }

        [Fact]
        public void Cancel_DeletesEveryJobNotInAFinalState_ExpectingTheStateItRead()
        {
            SetupBatch(
                ("1", FailedState.StateName),
                ("2", SucceededState.StateName),
                ("3", DeletedState.StateName),
                ("4", ProcessingState.StateName),
                ("5", EnqueuedState.StateName));

            var result = BatchJob.Cancel(_client.Object, _storage.Object, BatchId);

            Assert.Equal(3, result.Processed);
            Assert.Equal(0, result.Remaining);
            Assert.True(result.Complete);
            _client.Verify(x => x.ChangeState("1", It.Is<DeletedState>(s => s.Reason == "Canceled with BatchJob.Cancel"), FailedState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState("4", It.IsAny<DeletedState>(), ProcessingState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState("5", It.IsAny<DeletedState>(), EnqueuedState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Exactly(3));
        }

        [Fact]
        public void Cancel_DoesNotCountJobsThatChangedStateMeanwhile()
        {
            SetupBatch(("1", ProcessingState.StateName), ("2", ProcessingState.StateName));
            _client.Setup(x => x.ChangeState("2", It.IsAny<IState>(), ProcessingState.StateName)).Returns(false);

            var result = BatchJob.Cancel(_client.Object, _storage.Object, BatchId);

            Assert.Equal(1, result.Processed);
            Assert.True(result.Complete);
        }

        [Fact]
        public void RequeueFailed_RequeuesOnlyTheFailedJobs_ExpectingTheFailedState()
        {
            SetupBatch(("1", FailedState.StateName), ("2", SucceededState.StateName), ("3", FailedState.StateName));

            var result = BatchJob.RequeueFailed(_client.Object, _storage.Object, BatchId);

            Assert.Equal(2, result.Processed);
            Assert.True(result.Complete);
            _client.Verify(x => x.ChangeState("1", It.Is<EnqueuedState>(s => s.Reason == "Requeued with BatchJob.RequeueFailed"), FailedState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState("3", It.IsAny<EnqueuedState>(), FailedState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Exactly(2));
        }

        [Fact]
        public void Cancel_StopsAtItsTimeBudget_AndSaysHowManyJobsAreLeft()
        {
            SetupBatch(("1", ProcessingState.StateName), ("2", ProcessingState.StateName), ("3", ProcessingState.StateName));

            var result = BatchJob.Cancel(_client.Object, _storage.Object, BatchId, TimeSpan.Zero);

            Assert.Equal(1, result.Processed);
            Assert.Equal(2, result.Remaining);
            Assert.False(result.Complete);
        }

        [Fact]
        public void RequeueFailed_HasNoTimeLimit_ByDefault()
        {
            var jobs = new List<(string, string)>();
            for (var i = 0; i < 1200; i++) jobs.Add((i.ToString(), FailedState.StateName));
            SetupBatch(jobs.ToArray());

            var result = BatchJob.RequeueFailed(_client.Object, _storage.Object, BatchId);

            Assert.Equal(1200, result.Processed);
            Assert.True(result.Complete);
        }

        [Fact]
        public void CancelAndRequeueFailed_Throw_WhenTheBatchDoesNotExist()
        {
            var cancel = Assert.Throws<InvalidOperationException>(() => BatchJob.Cancel(_client.Object, _storage.Object, "missing"));
            var requeue = Assert.Throws<InvalidOperationException>(() => BatchJob.RequeueFailed(_client.Object, _storage.Object, "missing"));

            Assert.Contains("missing", cancel.Message);
            Assert.Contains("missing", requeue.Message);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Cancel_ThrowsOnInvalidArguments()
        {
            Assert.Throws<ArgumentNullException>("client", () => BatchJob.Cancel(null, _storage.Object, BatchId));
            Assert.Throws<ArgumentNullException>("storage", () => BatchJob.Cancel(_client.Object, null, BatchId));
            Assert.Throws<ArgumentNullException>("batchId", () => BatchJob.Cancel(_client.Object, _storage.Object, null));
            Assert.Throws<ArgumentOutOfRangeException>("timeBudget", () => BatchJob.Cancel(_client.Object, _storage.Object, BatchId, TimeSpan.FromSeconds(-1)));
        }

        [Fact, GlobalLock(Reason = "Uses JobStorage.Current instance")]
        public void CancelAndRequeueFailed_ReadTheBatchFromTheCurrentStorage()
        {
            // Only finished jobs, so the client created for the current storage changes nothing
            SetupBatch(("1", SucceededState.StateName));
            JobStorage.Current = _storage.Object;

            var canceled = BatchJob.Cancel(BatchId);
            var requeued = BatchJob.RequeueFailed(BatchId);

            Assert.True(canceled.Complete);
            Assert.True(requeued.Complete);
            Assert.Equal(0, canceled.Processed + requeued.Processed);
            _connection.Verify(x => x.GetJobData("1"), Times.Exactly(2));
        }

        private void SetupBatch(params (string Id, string State)[] jobs)
        {
            var ids = new HashSet<string>();
            foreach (var job in jobs)
            {
                ids.Add(job.Id);
                _connection.Setup(x => x.GetJobData(job.Id)).Returns(new JobData { State = job.State });
            }

            _connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(BatchId))).Returns(new Dictionary<string, string> { { "Description", "test" } });
            _connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetJobsKey(BatchId))).Returns(ids);
        }
    }
}
