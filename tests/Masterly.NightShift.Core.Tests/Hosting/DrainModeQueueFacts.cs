using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
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

        [Fact, StaticLock]
        public void Filter_HoldsAJobWithAQueueOfItsOwn_WhenItsEnqueuedStateNamesTheDefaultQueue()
        {
            // BackgroundJob.Enqueue("emails", ...) keeps the queue in the job; the Enqueued state says "default"
            DrainMode.PauseQueue(_connection.Object, "emails");
            var context = ElectWithJobQueue("7", "emails");
            SetupState("7", "default");

            new DrainModeFilter().OnStateElection(context.Object);

            var scheduled = Assert.IsType<ScheduledState>(context.Object.CandidateState);
            Assert.Equal("Held: queue 'emails' is paused", scheduled.Reason);
        }

        [Fact, StaticLock]
        public void Filter_UsesTheQueueOfTheEnqueuedState_WhenItIsNotTheDefaultOne()
        {
            // As the Enqueued state handler does: an explicit queue in the state wins over the job's own
            DrainMode.PauseQueue(_connection.Object, "emails");
            var context = ElectWithJobQueue("8", "emails");
            SetupState("8", "critical");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ProcessingState>(context.Object.CandidateState);
        }

        [Fact, StaticLock]
        public void Filter_RemembersTheQueueOfTheEnqueuedState_WhenItHoldsAJobWithoutAQueueOfItsOwn()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var context = Elect("9", "emails");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ScheduledState>(context.Object.CandidateState);
            _connection.Verify(x => x.SetJobParameter("9", DrainModeFilter.HeldQueueParameter, "\"emails\""));
        }

        [Fact, StaticLock]
        public void Filter_RemembersTheQueue_WhenTheGlobalSwitchHoldsTheJob()
        {
            DrainMode.Enable(_connection.Object);
            var context = Elect("10", "emails");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ScheduledState>(context.Object.CandidateState);
            _connection.Verify(x => x.SetJobParameter("10", DrainModeFilter.HeldQueueParameter, "\"emails\""));
        }

        [Fact, StaticLock]
        public void Filter_DoesNotRememberTheQueue_OfAJobWithAQueueOfItsOwn_OrOfTheDefaultQueue()
        {
            DrainMode.Enable(_connection.Object);
            var ownQueue = ElectWithJobQueue("11", "emails");
            SetupState("11", "default");
            var defaultQueue = Elect("12", "default");

            new DrainModeFilter().OnStateElection(ownQueue.Object);
            new DrainModeFilter().OnStateElection(defaultQueue.Object);

            Assert.IsType<ScheduledState>(ownQueue.Object.CandidateState);
            Assert.IsType<ScheduledState>(defaultQueue.Object.CandidateState);
            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_DoesNotWriteTheQueueAgain_WhenTheJobAlreadyRemembersIt()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var context = Elect("13", "emails");
            context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { DrainModeFilter.HeldQueueParameter, "\"emails\"" } };

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ScheduledState>(context.Object.CandidateState);
            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_PutsAHeldJobBackInItsQueue_WhenTheScheduleEnqueuesItAgain()
        {
            // The delayed job scheduler enqueues the held job with a new EnqueuedState, which names the default queue
            var context = ElectEnqueued("14", ScheduledState.StateName, "\"emails\"");

            new DrainModeFilter().OnStateElection(context.Object);

            var enqueued = Assert.IsType<EnqueuedState>(context.Object.CandidateState);
            Assert.Equal("emails", enqueued.Queue);
            _connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_ReadsTheHeldQueueFromTheStorage_WhenThereIsNoParametersSnapshot()
        {
            var context = ElectEnqueued("15", ScheduledState.StateName, null);
            context.ApplyContext.BackgroundJob.ParametersSnapshot = null;
            _connection.Setup(x => x.GetJobParameter("15", DrainModeFilter.HeldQueueParameter)).Returns("\"emails\"");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.Equal("emails", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
        }

        [Theory, StaticLock]
        [InlineData("Awaiting", "default", false)]
        [InlineData("Scheduled", "critical", false)]
        [InlineData("Scheduled", "default", true)]
        public void Filter_LeavesTheEnqueuedState_WhenTheJobDoesNotComeBackFromAHold(string currentState, string candidateQueue, bool jobHasItsOwnQueue)
        {
            var context = ElectEnqueued("16", currentState, "\"emails\"", candidateQueue);
            if (jobHasItsOwnQueue) context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Sample(), "reports");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.Equal(candidateQueue, Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
        }

        [Fact, StaticLock]
        public void Filter_ForgetsTheHeldQueue_OnceItPutTheJobBackInIt()
        {
            var context = ElectEnqueued("18", ScheduledState.StateName, "\"emails\"");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.Equal("emails", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
            _connection.Verify(x => x.SetJobParameter("18", DrainModeFilter.HeldQueueParameter, null));
        }

        [Fact, StaticLock]
        public void Filter_ForgetsTheHeldQueue_InTheStateChangesTransaction_WhenTheStorageCan()
        {
            var context = ElectEnqueued("19", ScheduledState.StateName, "\"emails\"");
            var transaction = new Mock<JobStorageTransaction>();
            context.ApplyContext.Transaction = transaction.As<IWriteOnlyTransaction>();
            context.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(true);

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.Equal("emails", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
            transaction.Verify(x => x.SetJobParameter("19", DrainModeFilter.HeldQueueParameter, null));
            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_ForgetsTheHeldQueue_WhenTheJobIsEnqueuedToAnotherQueueAfterTheHold()
        {
            var context = ElectEnqueued("20", ScheduledState.StateName, "\"emails\"", "critical");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.Equal("critical", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
            _connection.Verify(x => x.SetJobParameter("20", DrainModeFilter.HeldQueueParameter, null));
        }

        [Fact, StaticLock]
        public void Filter_LeavesARetryInTheDefaultQueue_AfterTheHeldQueueWasForgotten()
        {
            // The parameter was cleared when the hold ended (an empty value), and the job then failed and was retried
            var context = ElectEnqueued("21", ScheduledState.StateName, null);
            context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { DrainModeFilter.HeldQueueParameter, null } };

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.Equal("default", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_HoldsAJobRequeuedWhileRunning_FromAPausedQueue_FoundInItsHistory()
        {
            // A [Queue("emails")] or new EnqueuedState("emails") job interrupted by a shutdown comes back still in
            // the Processing state, which names no queue
            DrainMode.PauseQueue(_connection.Object, "emails");
            var context = ElectRequeuedWhileProcessing("22", "emails");

            new DrainModeFilter().OnStateElection(context.Object);

            var scheduled = Assert.IsType<ScheduledState>(context.Object.CandidateState);
            Assert.Equal("Held: queue 'emails' is paused", scheduled.Reason);
            _connection.Verify(x => x.SetJobParameter("22", DrainModeFilter.HeldQueueParameter, "\"emails\""));
        }

        [Fact, StaticLock]
        public void Filter_RemembersTheQueueFromTheHistory_WhenTheGlobalSwitchHoldsAJobRequeuedWhileRunning()
        {
            DrainMode.Enable(_connection.Object);
            var context = ElectRequeuedWhileProcessing("23", "emails");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ScheduledState>(context.Object.CandidateState);
            _connection.Verify(x => x.SetJobParameter("23", DrainModeFilter.HeldQueueParameter, "\"emails\""));
        }

        [Fact, StaticLock]
        public void Filter_DoesNotReadTheHistory_OfAJobFetchedFromItsEnqueuedState()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var context = Elect("24", "emails");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ScheduledState>(context.Object.CandidateState);
            context.ApplyContext.Storage.Verify(x => x.GetMonitoringApi(), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_LetsAJobRequeuedWhileRunning_Run_WhenItsHistoryCannotBeRead()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            var context = ElectRequeuedWhileProcessing("25", "emails");
            context.ApplyContext.Storage.Setup(x => x.GetMonitoringApi()).Throws(new InvalidOperationException("storage hiccup"));

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ProcessingState>(context.Object.CandidateState);
        }

        [Fact, StaticLock]
        public void HoldingFilters_PutAHeldJobBackInItsQueue_WithoutDrainModeFilter()
        {
            var filters = new IElectStateFilter[]
            {
                new ThrottleAttribute("resource"),
                new RateLimitAttribute("resource", 1, 60),
                new CircuitBreakerAttribute(1, 60, 60),
                new RunWindowAttribute("00:00-23:59"),
            };

            foreach (var filter in filters)
            {
                var context = ElectEnqueued("26", ScheduledState.StateName, "\"emails\"");
                context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Sample());

                filter.OnStateElection(context.Object);

                Assert.Equal("emails", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
            }

            _connection.Verify(x => x.SetJobParameter("26", DrainModeFilter.HeldQueueParameter, null), Times.Exactly(filters.Length));
        }

        [Fact, StaticLock]
        public void HoldingFilters_AndDrainModeFilter_ForgetTheHeldQueue_OnceInOneElection()
        {
            var context = ElectEnqueued("27", ScheduledState.StateName, "\"emails\"");
            context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Sample());

            new DrainModeFilter().OnStateElection(context.Object);
            new ThrottleAttribute("resource").OnStateElection(context.Object);
            new CircuitBreakerAttribute(1, 60, 60).OnStateElection(context.Object);

            Assert.Equal("emails", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
            _connection.Verify(x => x.SetJobParameter("27", DrainModeFilter.HeldQueueParameter, null), Times.Once);
        }

        [Fact, StaticLock]
        public void Filter_IgnoresAnInvalidHeldQueue()
        {
            var context = ElectEnqueued("17", ScheduledState.StateName, "\"Not A Queue\"");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.Equal("default", Assert.IsType<EnqueuedState>(context.Object.CandidateState).Queue);
        }

        private ElectStateContextMock ElectEnqueued(string jobId, string currentState, string heldQueue, string candidateQueue = "default")
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = jobId;
            context.ApplyContext.BackgroundJob.ParametersSnapshot = heldQueue == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { { DrainModeFilter.HeldQueueParameter, heldQueue } };
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.OldStateName = currentState;
            context.ApplyContext.NewStateObject = new EnqueuedState(candidateQueue);
            return context;
        }

        // A job requeued while it was running: still Processing, with its queue in the Enqueued state before that
        private ElectStateContextMock ElectRequeuedWhileProcessing(string jobId, string queue)
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = jobId;
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.OldStateName = ProcessingState.StateName;
            context.ApplyContext.NewStateObject = new ProcessingState("srv", "w");
            _connection.Setup(x => x.GetStateData(jobId)).Returns(new StateData
            {
                Name = ProcessingState.StateName,
                Data = new Dictionary<string, string> { { "ServerId", "old" }, { "WorkerId", "w" } },
            });

            var now = DateTime.UtcNow;
            var monitoring = new Mock<IMonitoringApi>();
            monitoring.Setup(x => x.JobDetails(jobId)).Returns(new JobDetailsDto
            {
                Properties = new Dictionary<string, string>(),
                History = new List<StateHistoryDto>
                {
                    new StateHistoryDto { StateName = ProcessingState.StateName, CreatedAt = now.AddMinutes(-1), Data = new Dictionary<string, string>() },
                    new StateHistoryDto { StateName = EnqueuedState.StateName, CreatedAt = now.AddMinutes(-2), Data = new Dictionary<string, string> { { "Queue", queue } } },
                    new StateHistoryDto { StateName = ScheduledState.StateName, CreatedAt = now.AddMinutes(-3), Data = new Dictionary<string, string>() },
                    new StateHistoryDto { StateName = EnqueuedState.StateName, CreatedAt = now.AddMinutes(-4), Data = new Dictionary<string, string> { { "Queue", "default" } } },
                },
            });
            context.ApplyContext.Storage.Setup(x => x.GetMonitoringApi()).Returns(monitoring.Object);
            return context;
        }

        private void SetupState(string jobId, string queue)
        {
            _connection.Setup(x => x.GetStateData(jobId)).Returns(new StateData { Name = EnqueuedState.StateName, Data = new Dictionary<string, string> { { "Queue", queue } } });
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
