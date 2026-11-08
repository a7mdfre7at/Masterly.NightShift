using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class QueueInsightsFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 10, 7, 30, DateTimeKind.Utc);
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, long> _counters = new Dictionary<string, long>();

        public QueueInsightsFacts()
        {
            QueueInsights.Clock = () => _now;
            _connection.Setup(x => x.GetCounter(It.IsAny<string>())).Returns<string>(key => _counters.TryGetValue(key, out var value) ? value : 0);
        }

        public void Dispose()
        {
            QueueInsights.Clock = () => DateTime.UtcNow;
        }

        [Fact]
        public void Record_WritesLatencyThroughputAndTheLastStart()
        {
            QueueInsights.Record(_transaction.Object, "emails", TimeSpan.FromSeconds(2.5), _now);

            _transaction.Verify(x => x.AddToSet(QueueInsights.IndexKey, "emails"));
            _transaction.Verify(x => x.InsertToList("queue:emails:latency", "2500"));
            _transaction.Verify(x => x.TrimList("queue:emails:latency", 0, 199));
            _transaction.Verify(x => x.IncrementCounter("queue:emails:started:202611051007", TimeSpan.FromHours(1)));
            _transaction.Verify(x => x.SetRangeInHash("queue:emails:insights", It.Is<IEnumerable<KeyValuePair<string, string>>>(p => p.Any(kv => kv.Key == "LastStartedAt"))));
        }

        [Fact]
        public void Read_ComputesPercentilesThroughputAndTheDrainEstimate()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash("queue:emails:insights")).Returns(new Dictionary<string, string> { { "LastStartedAt", JobHelper.SerializeDateTime(_now.AddSeconds(-10)) } });
            _connection.Setup(x => x.GetAllItemsFromList("queue:emails:latency")).Returns(Enumerable.Range(1, 100).Select(i => (i * 100).ToString()).ToList());
            _counters["queue:emails:started:202611051006"] = 10;
            _counters["queue:emails:started:202611051004"] = 5;
            _counters["queue:emails:started:202611051002"] = 1;
            _counters["queue:emails:started:202611051007"] = 7;  // the current, unfinished minute: not counted yet
            _counters["queue:emails:started:202611051001"] = 99; // six minutes ago: outside the window

            var health = QueueInsights.Read(_connection.Object, "emails");

            Assert.Equal(100, health.Samples);
            Assert.Equal(5100, health.MedianLatencyMs);
            Assert.Equal(9500, health.P95LatencyMs);
            Assert.Equal(16, health.StartedLast5Minutes);
            Assert.Equal(3.2, health.PerMinute);
            Assert.Equal(TimeSpan.FromMinutes(10), health.EstimateDrain(32));
            Assert.Equal(TimeSpan.Zero, health.EstimateDrain(0));
            Assert.False(health.IsStalled(32));
        }

        [Fact]
        public void Read_CountsOnlyTheFiveWholeMinutes_SoPerMinuteIsTheRealRate()
        {
            // 10:07:30: the window is 10:02:00-10:06:59, five whole minutes
            _connection.Setup(x => x.GetAllEntriesFromHash("queue:emails:insights")).Returns(new Dictionary<string, string> { { "LastStartedAt", JobHelper.SerializeDateTime(_now.AddSeconds(-1)) } });
            _counters["queue:emails:started:202611051002"] = 6;
            _counters["queue:emails:started:202611051003"] = 6;
            _counters["queue:emails:started:202611051004"] = 6;
            _counters["queue:emails:started:202611051005"] = 6;
            _counters["queue:emails:started:202611051006"] = 6;
            _counters["queue:emails:started:202611051007"] = 3;

            var health = QueueInsights.Read(_connection.Object, "emails");

            Assert.Equal(30, health.StartedLast5Minutes);
            Assert.Equal(6.0, health.PerMinute);
        }

        [Fact]
        public void Read_DoesNotReportAStall_WhenJobsWereTakenOnlyInTheCurrentMinute()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash("queue:emails:insights")).Returns(new Dictionary<string, string> { { "LastStartedAt", JobHelper.SerializeDateTime(_now.AddSeconds(-5)) } });
            _counters["queue:emails:started:202611051007"] = 4;

            var health = QueueInsights.Read(_connection.Object, "emails");

            Assert.Equal(0, health.StartedLast5Minutes);
            Assert.False(health.IsStalled(10));
        }

        [Fact]
        public void Read_ReportsStalledQueues_AndNullDrainTime()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash("queue:slow:insights")).Returns(new Dictionary<string, string> { { "LastStartedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-20)) } });
            _connection.Setup(x => x.GetAllItemsFromList("queue:slow:latency")).Returns(new List<string> { "100" });

            var health = QueueInsights.Read(_connection.Object, "slow");

            Assert.Equal(0, health.PerMinute);
            Assert.Null(health.EstimateDrain(5));
            Assert.True(health.IsStalled(5));
            Assert.False(health.IsStalled(0));
        }

        [Fact]
        public void Read_ReturnsNull_WithoutRecords_OrWithoutAStorageConnection()
        {
            Assert.Null(QueueInsights.Read(_connection.Object, "nothing"));
            Assert.Null(QueueInsights.Read(new Mock<IStorageConnection>().Object, "emails"));
        }

        private ApplyStateContextMock CreateStartContext(string stateQueue = "critical", string jobQueue = null)
        {
            var context = new ApplyStateContextMock { NewStateObject = new ProcessingState("srv", "w"), OldStateName = EnqueuedState.StateName };
            context.BackgroundJob.Id = "42";
            if (jobQueue != null) context.BackgroundJob.Job = new Job(typeof(QueueInsightsFacts), typeof(QueueInsightsFacts).GetMethod(nameof(Dispose)), Array.Empty<object>(), jobQueue);
            context.Connection.Setup(x => x.GetStateData("42")).Returns(new StateData
            {
                Name = EnqueuedState.StateName,
                Data = new Dictionary<string, string> { { "Queue", stateQueue }, { "EnqueuedAt", JobHelper.SerializeDateTime(_now.AddSeconds(-4)) } },
            });
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            return context;
        }

        [Fact]
        public void Filter_RecordsTheWait_WhenAJobStartsFromAQueue_InATransactionOfItsOwn()
        {
            var filter = new QueueInsightsFilter();
            var context = CreateStartContext();

            filter.OnStateApplied(context.Object, context.Transaction.Object);

            _transaction.Verify(x => x.InsertToList("queue:critical:latency", "4000"));
            _transaction.Verify(x => x.IncrementCounter("queue:critical:started:202611051007", TimeSpan.FromHours(1)));
            _transaction.Verify(x => x.Commit());
            context.Transaction.VerifyNoOtherCalls();
        }

        [Fact]
        public void Filter_RecordsTheWait_UnderTheJobsOwnQueue_WhenTheStateSaysDefault()
        {
            // Enqueue("emails", ...) stores "default" in the Enqueued state and the queue in the job, and the job
            // waits in "emails"; it was recorded under "default"
            var filter = new QueueInsightsFilter();
            var context = CreateStartContext(stateQueue: "default", jobQueue: "emails");

            filter.OnStateApplied(context.Object, context.Transaction.Object);

            _transaction.Verify(x => x.InsertToList("queue:emails:latency", "4000"));
            _transaction.Verify(x => x.InsertToList("queue:default:latency", It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Filter_RecordsTheWait_UnderTheStatesQueue_WhenItIsNotDefault()
        {
            // As EnqueuedState's handler does: an explicit queue in the state wins over the job's own
            var filter = new QueueInsightsFilter();
            var context = CreateStartContext(stateQueue: "critical", jobQueue: "emails");

            filter.OnStateApplied(context.Object, context.Transaction.Object);

            _transaction.Verify(x => x.InsertToList("queue:critical:latency", "4000"));
        }

        [Fact]
        public void Filter_WaitsForTheCommit_WhenTheStateChangeHasPostCommitActions()
        {
            var filter = new QueueInsightsFilter();
            var context = CreateStartContext();
            var afterCommit = new PostCommitActions();
            context.Object.AfterCommit = afterCommit;

            filter.OnStateApplied(context.Object, context.Transaction.Object);

            _transaction.Verify(x => x.Commit(), Times.Never);

            afterCommit.Run();

            _transaction.Verify(x => x.Commit());
        }

        [Fact, StaticLock]
        public void Filter_DoesNotFailTheStateChange_WhenTheWriteFails_AndLogsAWarning()
        {
            var provider = new RecordingLogProvider();
            var filter = new QueueInsightsFilter();
            var context = CreateStartContext();
            _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("deadlock victim"));

            using (provider.Use())
            {
                filter.OnStateApplied(context.Object, context.Transaction.Object);
            }

            Assert.Contains(provider.Entries, e => e.Level == Masterly.NightShift.Logging.LogLevel.Warn && e.Exception is InvalidOperationException);
        }

        [Fact]
        public void Read_ReturnsNull_WhenTheStorageCannotReadListsAndCounters()
        {
            var connection = new Mock<JobStorageConnection> { CallBase = true };
            connection.Setup(x => x.GetAllEntriesFromHash("queue:emails:insights")).Returns(new Dictionary<string, string>());

            Assert.Null(QueueInsights.Read(connection.Object, "emails"));
        }

        [Fact]
        public void Filter_IgnoresOtherTransitions()
        {
            var filter = new QueueInsightsFilter();
            var context = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 1, 1), OldStateName = ProcessingState.StateName };
            context.BackgroundJob.Id = "42";

            filter.OnStateApplied(context.Object, _transaction.Object);

            context.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
            _transaction.Verify(x => x.InsertToList(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Filter_RecordsNothing_WhenThePreviousStateCannotBeRead()
        {
            var filter = new QueueInsightsFilter();
            var context = new ApplyStateContextMock { NewStateObject = new ProcessingState("srv", "w"), OldStateName = EnqueuedState.StateName };
            context.BackgroundJob.Id = "42";
            context.Connection.Setup(x => x.GetStateData("42")).Throws(new InvalidOperationException("storage is down"));

            filter.OnStateApplied(context.Object, _transaction.Object);

            _transaction.Verify(x => x.InsertToList(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _transaction.Verify(x => x.IncrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void Filter_OnStateUnapplied_WritesNothing()
        {
            var context = new ApplyStateContextMock { NewStateObject = new ProcessingState("srv", "w"), OldStateName = EnqueuedState.StateName };

            new QueueInsightsFilter().OnStateUnapplied(context.Object, _transaction.Object);

            context.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
            _transaction.VerifyNoOtherCalls();
        }
    }
}
