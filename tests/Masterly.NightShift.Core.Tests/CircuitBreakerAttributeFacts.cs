using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class CircuitBreakerAttributeFacts : IDisposable
    {
        private const string CircuitKey = "circuit:resource";
        private const string FailuresKey = "circuit:resource:failures";

        private readonly PerformContextMock _performContext = new PerformContextMock();
        private readonly ElectStateContextMock _electContext = new ElectStateContextMock();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new Mock<IBackgroundJobStateChanger>();
        private readonly HashSet<string> _failures = new HashSet<string>();
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private readonly long _timestamp;
        private Dictionary<string, string> _circuit;

        public CircuitBreakerAttributeFacts()
        {
            _timestamp = JobHelper.ToTimestamp(_now);

            foreach (var connection in new[] { _performContext.Connection, _electContext.ApplyContext.Connection })
            {
                connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
                connection.Setup(x => x.GetAllItemsFromSet(FailuresKey)).Returns(_failures);
                connection.Setup(x => x.GetAllEntriesFromHash(CircuitKey)).Returns(() => _circuit);
                connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            }

            _performContext.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
            _performContext.BackgroundJob.Job = Job.FromExpression(() => Sample());
            _electContext.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Sample());

            ConcurrencyControl.StateChanger = _stateChanger.Object;
            ConcurrencyControl.Clock = () => _now;
        }

        public void Dispose()
        {
            ConcurrencyControl.StateChanger = new BackgroundJobStateChanger();
            ConcurrencyControl.Clock = () => DateTime.UtcNow;
            Alerts.Clear();
        }

        [Fact]
        public void Ctor_ValidatesArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreakerAttribute(0, 60, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreakerAttribute(3, 0, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreakerAttribute(3, 60, 0));
        }

        [Fact, StaticLock]
        public void OnStateElection_RecordsAFailure_WithoutOpening_BelowTheThreshold()
        {
            var attribute = Create();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            var transaction = _electContext.ApplyContext.Transaction;
            transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
            transaction.Verify(x => x.AddToSet(CircuitBreakerAttribute.CircuitsSetKey, "resource"));
            transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_OpensTheCircuit_AtTheThreshold()
        {
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState))));
        }

        [Fact, StaticLock]
        public void OnStateElection_CountsFailures_ThatAnotherFilterTurnedIntoRetries()
        {
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());
            var context = _electContext.Object;
            context.CandidateState = new ScheduledState(TimeSpan.FromMinutes(1));

            attribute.OnStateElection(context);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState))));
        }

        [Fact, StaticLock]
        public void OnStateElection_IgnoresFailuresOutsideTheWindow()
        {
            var attribute = Create();
            var old = $"{_timestamp - 61}:a";
            _failures.Add(old);
            _failures.Add($"{_timestamp - 62}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            var transaction = _electContext.ApplyContext.Transaction;
            transaction.Verify(x => x.RemoveFromSet(FailuresKey, old));
            transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNothing_ForOtherStates()
        {
            var attribute = Create();
            _electContext.ApplyContext.NewStateObject = new EnqueuedState();

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_LetsJobsThrough_WhenTheCircuitIsClosed()
        {
            var attribute = Create();
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_ReschedulesJobs_WhileTheCircuitIsOpen()
        {
            var attribute = Create();
            Open(openedSecondsAgo: 30);
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.True(performing.Canceled);
            _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
                c.NewState is ScheduledState
                && ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(90)
                && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(91))));
        }

        [Fact, StaticLock]
        public void OnPerforming_HalfOpensTheCircuit_AfterTheOpenPeriod_AndLetsTheJobThrough()
        {
            var attribute = Create();
            Open(openedSecondsAgo: 121);
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.HalfOpenState))));
            _transaction.Verify(x => x.Commit());
        }

        [Fact, StaticLock]
        public void OnStateElection_ClosesTheCircuit_WhenTheProbeSucceeds()
        {
            var attribute = Create();
            HalfOpen();
            _failures.Add($"{_timestamp - 10}:a");
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            attribute.OnStateElection(_electContext.Object);

            var transaction = _electContext.ApplyContext.Transaction;
            transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.ClosedState))));
            transaction.Verify(x => x.RemoveFromSet(FailuresKey, $"{_timestamp - 10}:a"));
        }

        [Fact, StaticLock]
        public void OnStateElection_ReopensTheCircuit_WhenTheProbeFails()
        {
            var attribute = Create();
            HalfOpen();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState)
                      && pairs.Any(p => p.Key == "OpenedAt" && p.Value == _timestamp.ToString()))));
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNotRecordFailures_ForSuccessfulJobs_OnClosedCircuits()
        {
            var attribute = Create();
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void ReadCircuit_ReturnsClosed_WhenNothingIsStored()
        {
            var state = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

            Assert.Equal(CircuitBreakerAttribute.ClosedState, state.State);
            Assert.Equal(0, state.Failures);
        }

        [Fact, StaticLock]
        public void Reset_ClosesTheCircuit_AndForgetsFailures()
        {
            Open(openedSecondsAgo: 5);
            _failures.Add($"{_timestamp}:a");

            CircuitBreakerAttribute.Reset(_performContext.Connection.Object, "resource");

            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.ClosedState))));
            _transaction.Verify(x => x.RemoveFromSet(FailuresKey, $"{_timestamp}:a"));
            _transaction.Verify(x => x.Commit());
        }

        [Fact]
        public void Ctor_WithoutResource_LeavesTheResourceToTheJobTypeAndMethod()
        {
            var attribute = new CircuitBreakerAttribute(3, 60, 120);

            Assert.Null(attribute.Resource);
            Assert.Equal(3, attribute.FailureThreshold);
            Assert.Equal(60, attribute.WindowSeconds);
            Assert.Equal(120, attribute.OpenSeconds);
        }

        [Fact, StaticLock]
        public void OnStateElection_UsesTheJobTypeAndMethod_AsTheDefaultResource()
        {
            const string defaultFailuresKey = "circuit:circuitbreakerattributefacts.sample:failures";
            var attribute = new CircuitBreakerAttribute(3, 60, 120);
            _electContext.ApplyContext.Connection.Setup(x => x.GetAllItemsFromSet(defaultFailuresKey)).Returns(new HashSet<string>());
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            var transaction = _electContext.ApplyContext.Transaction;
            transaction.Verify(x => x.AddToSet(defaultFailuresKey, $"{_timestamp}:JobId", _timestamp));
            transaction.Verify(x => x.AddToSet(CircuitBreakerAttribute.CircuitsSetKey, "circuitbreakerattributefacts.sample"));
        }

        [Fact, StaticLock]
        public void OnPerformed_DoesNotTouchTheStorage()
        {
            var attribute = Create();

            attribute.OnPerformed(_performContext.GetPerformedContext());

            _performContext.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
            _performContext.Connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_RaisesACircuitOpenedAlert_WhenTheThresholdIsReached()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            var alert = Assert.Single(sink.Received);
            Assert.Equal(AlertKind.CircuitOpened, alert.Kind);
            Assert.Equal("Circuit 'resource' is open", alert.Title);
            Assert.Equal("3 failure(s) within 60 seconds; jobs are held for 120 seconds.", alert.Message);
            Assert.Equal("resource", alert.Resource);
            Assert.Equal("JobId", alert.JobId);
        }

        [Fact, StaticLock]
        public void OnStateElection_RaisesACircuitOpenedAlert_WhenTheProbeFails()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            HalfOpen();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            var alert = Assert.Single(sink.Received);
            Assert.Equal(AlertKind.CircuitOpened, alert.Kind);
            Assert.Equal("3 failure(s) within 60 seconds; jobs are held for 120 seconds.", alert.Message);
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNotRaiseAnAlert_BelowTheThreshold()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            Assert.Empty(sink.Received);
        }

        private static CircuitBreakerAttribute Create()
        {
            return new CircuitBreakerAttribute("resource", failureThreshold: 3, windowSeconds: 60, openSeconds: 120);
        }

        private void Open(int openedSecondsAgo)
        {
            _circuit = new Dictionary<string, string>
            {
                { "State", CircuitBreakerAttribute.OpenState },
                { "OpenedAt", (_timestamp - openedSecondsAgo).ToString() }
            };
        }

        private void HalfOpen()
        {
            _circuit = new Dictionary<string, string>
            {
                { "State", CircuitBreakerAttribute.HalfOpenState },
                { "OpenedAt", (_timestamp - 200).ToString() }
            };
        }

        private sealed class RecordingSink : IAlertSink
        {
            public List<Alert> Received { get; } = new List<Alert>();

            public void Publish(Alert alert) => Received.Add(alert);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
