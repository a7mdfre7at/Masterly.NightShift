using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class DrainModeQueueFacts : IDisposable
    {
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, string> _hash = new Dictionary<string, string>();

        public DrainModeQueueFacts()
        {
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
            _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => { foreach (var pair in pairs) _hash[pair.Key] = pair.Value; });
            DrainMode.InvalidateCache();
        }

        public void Dispose()
        {
            DrainMode.InvalidateCache();
        }

        [Fact, StaticLock]
        public void PauseQueue_AndResumeQueue_RoundTrip()
        {
            DrainMode.PauseQueue(_connection.Object, "Emails", "smtp outage");

            var status = DrainMode.Read(_connection.Object);
            Assert.False(status.Active);
            Assert.Equal("smtp outage", status.PausedQueues["emails"]);
            Assert.True(status.IsQueuePaused("emails"));
            Assert.True(status.IsQueuePaused("EMAILS"));
            Assert.False(status.IsQueuePaused("default"));

            DrainMode.ResumeQueue(_connection.Object, "emails");

            status = DrainMode.Read(_connection.Object);
            Assert.Empty(status.PausedQueues);
            Assert.Same(DrainModeStatus.Inactive, status);
        }

        [Fact, StaticLock]
        public void GlobalDrain_HoldsEveryQueue()
        {
            DrainMode.Enable(_connection.Object, "deploy");

            var status = DrainMode.Read(_connection.Object);
            Assert.True(status.Active);
            Assert.True(status.IsQueuePaused("anything"));
        }

        [Fact, StaticLock]
        public void Filter_HoldsJobsFromPausedQueues_AndLetsOthersRun()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var filter = new DrainModeFilter();

            var held = Elect("1", "emails");
            filter.OnStateElection(held.Object);
            var scheduled = Assert.IsType<ScheduledState>(held.Object.CandidateState);
            Assert.Equal("Held: queue 'emails' is paused", scheduled.Reason);

            var running = Elect("2", "default");
            filter.OnStateElection(running.Object);
            Assert.IsType<ProcessingState>(running.Object.CandidateState);
        }

        [Fact, StaticLock]
        public void Filter_DoesNotReadTheState_WhenNoQueueIsPaused()
        {
            var filter = new DrainModeFilter();
            var context = Elect("3", "emails");

            filter.OnStateElection(context.Object);

            Assert.IsType<ProcessingState>(context.Object.CandidateState);
            _connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_FallsBackToTheJobQueue_WhenTheStateCannotBeRead()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var filter = new DrainModeFilter();
            var context = ElectWithJobQueue("4", "emails");
            _connection.Setup(x => x.GetStateData("4")).Throws(new InvalidOperationException("storage hiccup"));

            filter.OnStateElection(context.Object);

            var scheduled = Assert.IsType<ScheduledState>(context.Object.CandidateState);
            Assert.Equal("Held: queue 'emails' is paused", scheduled.Reason);
        }

        [Fact, StaticLock]
        public void Filter_FallsBackToTheJobQueue_WhenTheStateHasNoQueue()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var filter = new DrainModeFilter();
            var context = ElectWithJobQueue("5", "emails");
            _connection.Setup(x => x.GetStateData("5")).Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string>() });

            filter.OnStateElection(context.Object);

            Assert.IsType<ScheduledState>(context.Object.CandidateState);
        }

        [Fact, StaticLock]
        public void Filter_LetsTheJobRun_WhenNeitherTheStateNorTheJobNamesAQueue()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var filter = new DrainModeFilter();
            var context = ElectWithJobQueue("6", null);
            _connection.Setup(x => x.GetStateData("6")).Returns((StateData)null);

            filter.OnStateElection(context.Object);

            Assert.IsType<ProcessingState>(context.Object.CandidateState);
        }

        private ElectStateContextMock ElectWithJobQueue(string jobId, string queue)
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = jobId;
            context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Sample(), queue);
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.NewStateObject = new ProcessingState("srv", "w");
            return context;
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }

        private ElectStateContextMock Elect(string jobId, string queue)
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = jobId;
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.NewStateObject = new ProcessingState("srv", "w");
            _connection.Setup(x => x.GetStateData(jobId)).Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string> { { "Queue", queue } } });
            return context;
        }
    }
}
