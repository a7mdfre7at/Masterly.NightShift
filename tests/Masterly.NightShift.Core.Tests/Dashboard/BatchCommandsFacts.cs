using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class BatchCommandsFacts
    {
        private const string BatchId = "batch-1";
        private const string DashboardReason = "Triggered via Dashboard UI (batch)";

        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();

        public BatchCommandsFacts()
        {
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(new Mock<IWriteOnlyTransaction>().Object);
            _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);
        }

        [Fact]
        public void RequeueFailed_ReturnsNull_AndChangesNothing_WhenTheBatchIsUnknown()
        {
            var context = Create();

            var result = BatchCommands.RequeueFailed(context, "missing");

            Assert.Null(result);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void DeletePending_ReturnsNull_AndChangesNothing_WhenTheBatchIsUnknown()
        {
            var context = Create();

            var result = BatchCommands.DeletePending(context, "missing");

            Assert.Null(result);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void RequeueFailed_RequeuesOnlyTheFailedJobs_ExpectingTheFailedState()
        {
            SetupBatch(BatchId, ("1", FailedState.StateName), ("2", SucceededState.StateName), ("3", ProcessingState.StateName), ("4", FailedState.StateName));
            var context = Create();

            var result = BatchCommands.RequeueFailed(context, BatchId);

            Assert.Equal(2, result.Processed);
            Assert.True(result.Complete);
            _client.Verify(x => x.ChangeState("1", It.Is<EnqueuedState>(s => s.Reason == DashboardReason), FailedState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState("4", It.Is<EnqueuedState>(s => s.Reason == DashboardReason), FailedState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Exactly(2));
        }

        [Fact]
        public void DeletePending_DeletesEveryJobNotInAFinalState_ExpectingTheStateItRead()
        {
            SetupBatch(BatchId,
                ("1", FailedState.StateName),
                ("2", SucceededState.StateName),
                ("3", DeletedState.StateName),
                ("4", ProcessingState.StateName),
                ("5", AwaitingState.StateName));
            var context = Create();

            var result = BatchCommands.DeletePending(context, BatchId);

            Assert.Equal(3, result.Processed);
            // A job that finishes between the read and the change must not be deleted
            _client.Verify(x => x.ChangeState("1", It.Is<DeletedState>(s => s.Reason == DashboardReason), FailedState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState("4", It.Is<DeletedState>(s => s.Reason == DashboardReason), ProcessingState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState("5", It.Is<DeletedState>(s => s.Reason == DashboardReason), AwaitingState.StateName), Times.Once);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Exactly(3));
        }

        [Fact]
        public void DeletePending_SkipsJobsWhoseDataExpired()
        {
            SetupBatch(BatchId, ("1", ProcessingState.StateName));
            _connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetJobsKey(BatchId))).Returns(new HashSet<string> { "1", "expired" });
            _connection.Setup(x => x.GetJobData("expired")).Returns((JobData)null);
            var context = Create();

            var result = BatchCommands.DeletePending(context, BatchId);

            Assert.Equal(1, result.Processed);
            _client.Verify(x => x.ChangeState("expired", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
            _client.Verify(x => x.ChangeState("1", It.IsAny<DeletedState>(), ProcessingState.StateName), Times.Once);
        }

        [Fact]
        public void RequeueFailed_ReturnsTrue_ForAnEmptyBatch()
        {
            SetupBatch(BatchId);
            var context = Create();

            var result = BatchCommands.RequeueFailed(context, BatchId);

            Assert.Equal(0, result.Processed);
            Assert.True(result.Complete);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void RequeueFailed_DecodesAnEncodedSlashInTheBatchId_AndReadsThroughTheReadOnlyConnection()
        {
            // ASP.NET Core decodes the path before routing, except "%2F"
            SetupBatch("nightly/import 100%", ("7", FailedState.StateName));
            var context = Create();

            var result = BatchCommands.RequeueFailed(context, "nightly%2Fimport 100%");

            Assert.Equal(1, result.Processed);
            _storage.Verify(x => x.GetReadOnlyConnection());
            _client.Verify(x => x.ChangeState("7", It.IsAny<EnqueuedState>(), FailedState.StateName), Times.Once);
        }

        [Fact]
        public async Task RequeueFailedRoute_AnswersWhatItDid_AndRequeuesTheFailedJobs()
        {
            SetupBatch(BatchId, ("1", FailedState.StateName));
            var context = Create("/batches/requeue-failed/" + BatchId);

            await Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal("{\"processed\":1,\"remaining\":0,\"complete\":true}", context.Response.Text);
            _client.Verify(x => x.ChangeState("1", It.IsAny<EnqueuedState>(), FailedState.StateName), Times.Once);
        }

        [Fact]
        public async Task DeleteRoute_Answers422_ForAnUnknownBatch()
        {
            var context = Create("/batches/delete/missing");

            await Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
        }

        [Fact]
        public async Task DeleteRoute_Answers401_ForReadOnlyDashboards()
        {
            SetupBatch(BatchId, ("1", ProcessingState.StateName));
            var context = Create("/batches/delete/" + BatchId, readOnly: true);

            await Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void DeletePending_StopsWhenItsTimeIsUp_AndSaysHowManyJobsAreLeft()
        {
            // A batch of 100,000 jobs was handled in one request, past any proxy timeout
            SetupBatch(BatchId, ("1", ProcessingState.StateName), ("2", ProcessingState.StateName), ("3", ProcessingState.StateName));
            var context = Create();
            var budget = JobListCommands.TimeBudget;
            JobListCommands.TimeBudget = TimeSpan.Zero;
            try
            {
                var result = BatchCommands.DeletePending(context, BatchId);

                Assert.Equal(1, result.Processed);
                Assert.False(result.Complete);
                Assert.Equal(2, result.Remaining);
                _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Once);
            }
            finally
            {
                JobListCommands.TimeBudget = budget;
            }
        }

        private void SetupBatch(string batchId, params (string Id, string State)[] jobs)
        {
            var ids = new HashSet<string>();
            foreach (var job in jobs)
            {
                ids.Add(job.Id);
                _connection.Setup(x => x.GetJobData(job.Id)).Returns(new JobData { State = job.State });
            }

            _connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(batchId))).Returns(new Dictionary<string, string> { { "Description", "test" } });
            _connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetJobsKey(batchId))).Returns(ids);
        }

        private static Task Dispatch(DashboardContextStub context)
        {
            var route = DashboardRoutes.Routes.FindDispatcher(context.Request.Path);
            context.UriMatch = route.Item2;
            return route.Item1.Dispatch(context);
        }

        private DashboardContextStub Create(string path = "/", bool readOnly = false)
        {
            return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, new DashboardRequestStub("POST", path), _storage.Object)
            {
                BackgroundJobClient = _client.Object,
            };
        }
    }
}
