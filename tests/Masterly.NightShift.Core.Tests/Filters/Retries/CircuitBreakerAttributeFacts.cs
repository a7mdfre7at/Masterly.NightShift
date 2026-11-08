using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Profiling;
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

            _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.AddToSet(CircuitBreakerAttribute.CircuitsSetKey, "resource"));
            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs => pairs.Any(p => p.Key == "State"))), Times.Never);
            _transaction.Verify(x => x.Commit());
            _electContext.ApplyContext.Transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_OpensTheCircuit_AtTheThreshold()
        {
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
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

            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState))));
        }

        public static IEnumerable<object[]> FailuresWithoutCallingTheDependency => new[]
        {
            new object[] { new ConcurrencyLimitException("resource", "All slots stayed in use") },
            new object[] { new DistributedLockTimeoutException("lock") },
            new object[] { new RetryLaterException(TimeSpan.FromMinutes(1)) },
        };

        [Theory, StaticLock]
        [MemberData(nameof(FailuresWithoutCallingTheDependency))]
        public void OnStateElection_DoesNotCount_FailuresInWhichTheDependencyWasNeverCalled(Exception exception)
        {
            // Six jobs waiting for DisableConcurrentExecution's lock must not open the circuit of a healthy service
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(exception);
            var context = _electContext.Object;
            context.CandidateState = new ScheduledState(TimeSpan.FromMinutes(1));

            attribute.OnStateElection(context);

            _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
            _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
            _electContext.ApplyContext.Connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNotReopenTheCircuit_WhenTheProbeFailsWithoutCallingTheDependency()
        {
            var attribute = Create();
            HalfOpen(probe: "JobId");
            _electContext.ApplyContext.NewStateObject = new FailedState(new DistributedLockTimeoutException("lock"));

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
            _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_CountsTheExcludedExceptions_WhenExceptOnIsEmpty()
        {
            var attribute = new CircuitBreakerAttribute("resource", 3, 60, 120) { ExceptOn = Type.EmptyTypes };
            _electContext.ApplyContext.NewStateObject = new FailedState(new DistributedLockTimeoutException("lock"));

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
        }

        [Fact, StaticLock]
        public void OnStateElection_CountsOnlyTheListedExceptions_WhenOnlyOnIsGiven()
        {
            var attribute = new CircuitBreakerAttribute("resource", 3, 60, 120) { OnlyOn = new[] { typeof(TimeoutException) } };

            _electContext.ApplyContext.NewStateObject = new FailedState(new InvalidOperationException());
            attribute.OnStateElection(_electContext.Object);
            _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);

            var timedOut = new ElectStateContextMock();
            timedOut.ApplyContext.Connection = _electContext.ApplyContext.Connection;
            timedOut.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Sample());
            timedOut.ApplyContext.NewStateObject = new FailedState(new TimeoutException());
            attribute.OnStateElection(timedOut.Object);
            _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
        }

        [Fact]
        public void ExceptOn_ByDefault_ListsTheFailuresInWhichTheDependencyWasNeverCalled()
        {
            var attribute = Create();

            Assert.Empty(attribute.OnlyOn);
            Assert.Equal(new[] { typeof(ConcurrencyLimitException), typeof(DistributedLockTimeoutException), typeof(RetryLaterException) }, attribute.ExceptOn);
        }

        [Fact, StaticLock]
        public void OnStateElection_PutsAHeldJobBackInItsQueue_WhenItIsEnqueuedAgain()
        {
            var attribute = Create();
            _electContext.ApplyContext.OldStateName = ScheduledState.StateName;
            _electContext.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { DrainModeFilter.HeldQueueParameter, "\"emails\"" } };
            _electContext.ApplyContext.NewStateObject = new EnqueuedState();

            attribute.OnStateElection(_electContext.Object);

            Assert.Equal("emails", Assert.IsType<EnqueuedState>(_electContext.Object.CandidateState).Queue);
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

            _transaction.Verify(x => x.RemoveFromSet(FailuresKey, old));
            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs => pairs.Any(p => p.Key == "State"))), Times.Never);
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
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.HalfOpenState)
                      && pairs.Any(p => p.Key == "Probe" && p.Value == "JobId"))));
            _transaction.Verify(x => x.Commit());
        }

        [Fact, StaticLock]
        public void OnPerforming_WritesTheProbeAgain_AfterATransientStorageError()
        {
            var attribute = Create();
            Open(openedSecondsAgo: 121);
            var commits = 0;
            _transaction.Setup(x => x.Commit()).Callback(() =>
            {
                if (++commits == 1) throw new InvalidOperationException("deadlock victim");
            });
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            Assert.Equal(2, commits);
            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "Probe" && p.Value == "JobId"))), Times.Exactly(2));
        }

        [Fact, StaticLock]
        public void OnPerforming_ReschedulesOtherJobs_WhileTheProbeRuns()
        {
            var attribute = Create();
            HalfOpen(probe: "probe-job");
            ProbeState(ProcessingState.StateName);
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.True(performing.Canceled);
            _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
                c.NewState is ScheduledState
                && ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(12)
                && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(13)
                && c.NewState.Reason.Contains("probe-job"))));
            _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_DoesNotOverwriteTheProbesVerdict_CommittedWhileItChecks()
        {
            var attribute = Create();
            HalfOpen(probe: "probe-job");

            // The probe succeeds and closes the circuit right after this job read the circuit
            _performContext.Connection
                .Setup(x => x.GetStateData("probe-job"))
                .Returns(() =>
                {
                    _circuit = new Dictionary<string, string> { { "State", CircuitBreakerAttribute.ClosedState } };
                    return new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() };
                });
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_LetsTheProbeRunAgain_WithoutWritingAnything()
        {
            var attribute = Create();
            HalfOpen(probe: "JobId");
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
            _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Theory, StaticLock]
        [InlineData(null)]
        [InlineData("Deleted")]
        [InlineData("Scheduled")]
        public void OnPerforming_TakesOverAsTheProbe_WhenTheProbeIsNoLongerRunning(string probeState)
        {
            var attribute = Create();
            HalfOpen(probe: "probe-job");
            ProbeState(probeState);
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.HalfOpenState)
                      && pairs.Any(p => p.Key == "Probe" && p.Value == "JobId"))));
        }

        [Fact, StaticLock]
        public void OnPerforming_ChoosesTheProbe_OnFreshDataUnderTheLock()
        {
            // Another server made its job the probe between the first read and the lock
            var attribute = Create();
            Open(openedSecondsAgo: 121);
            ProbeState(ProcessingState.StateName);
            _performContext.Connection
                .Setup(x => x.AcquireDistributedLock("circuit:resource:lock", It.IsAny<TimeSpan>()))
                .Callback(() => HalfOpen(probe: "probe-job"))
                .Returns(new Mock<IDisposable>().Object);
            var performing = _performContext.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.True(performing.Canceled);
            _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void ReadCircuit_ReturnsTheProbe_OfAHalfOpenCircuit()
        {
            HalfOpen(probe: "probe-job");

            var state = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

            Assert.Equal(CircuitBreakerAttribute.HalfOpenState, state.State);
            Assert.Equal("probe-job", state.ProbeJobId);
        }

        [Fact, StaticLock]
        public void OnStateElection_IgnoresTheSuccessOfAnotherJob_WhileTheProbeRuns()
        {
            var attribute = Create();
            HalfOpen(probe: "probe-job");
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_OnlyRecordsTheFailureOfAnotherJob_WhileTheProbeRuns()
        {
            var attribute = Create();
            HalfOpen(probe: "probe-job");
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs => pairs.Any(p => p.Key == "State"))), Times.Never);
            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_ClosesTheCircuit_WhenTheRecordedProbeSucceeds()
        {
            var attribute = Create();
            HalfOpen(probe: "JobId");
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.ClosedState)
                      && pairs.Any(p => p.Key == "Probe" && p.Value == ""))));
        }

        [Fact, StaticLock]
        public void OnStateElection_ReopensTheCircuit_WhenTheRecordedProbeFails()
        {
            var attribute = Create();
            HalfOpen(probe: "JobId");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState))));
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
        public void Reset_ClosesTheCircuit_ForgetsFailures_AndRemovesItFromTheCircuitsSet()
        {
            Open(openedSecondsAgo: 5);
            _failures.Add($"{_timestamp}:a");

            CircuitBreakerAttribute.Reset(_performContext.Connection.Object, "resource");

            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.ClosedState)
                      && pairs.Any(p => p.Key == "OpenUntil" && p.Value == ""))));
            _transaction.Verify(x => x.RemoveFromSet(CircuitBreakerAttribute.CircuitsSetKey, "resource"));
            _transaction.Verify(x => x.RemoveFromSet(FailuresKey, $"{_timestamp}:a"));
            _transaction.Verify(x => x.Commit());
            _performContext.Connection.Verify(x => x.AcquireDistributedLock("circuit:resource:lock", It.IsAny<TimeSpan>()));
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
        public void OnStateElection_UsesTheFullTypeName_MethodAndParameterTypes_AsTheDefaultResource()
        {
            const string defaultFailuresKey = "circuit:masterly.nightshift.core.tests.circuitbreakerattributefacts.sample():failures";
            var attribute = new CircuitBreakerAttribute(3, 60, 120);
            _electContext.ApplyContext.Connection.Setup(x => x.GetAllItemsFromSet(defaultFailuresKey)).Returns(new HashSet<string>());
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.AddToSet(defaultFailuresKey, $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.AddToSet(CircuitBreakerAttribute.CircuitsSetKey, "masterly.nightshift.core.tests.circuitbreakerattributefacts.sample()"));
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

        [Fact, StaticLock]
        public void OnStateElection_RaisesTheAlert_OnlyAfterTheOpenStateIsCommitted()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _transaction.Setup(x => x.Commit()).Callback(() => Assert.Empty(sink.Received));
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.Commit());
            Assert.Single(sink.Received);
        }

        [Fact, StaticLock]
        public void OnStateElection_OpensTheCircuitAndAlertsOnce_WhenTheStateChangeIsRetried()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            StoreHashWrites();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            // The failure is committed at once; a retried state change finds the circuit open
            attribute.OnStateElection(_electContext.Object);
            attribute.OnStateElection(new ElectStateContext(_electContext.ApplyContext.Object));

            Assert.Single(sink.Received);
            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState))), Times.Once);
        }

        [Fact, StaticLock]
        public void OnStateElection_CountsTheFailure_UnderTheCircuitLock()
        {
            var attribute = Create();
            var locked = false;
            _electContext.ApplyContext.Connection
                .Setup(x => x.AcquireDistributedLock("circuit:resource:lock", It.IsAny<TimeSpan>()))
                .Returns(() =>
                {
                    locked = true;
                    var handle = new Mock<IDisposable>();
                    handle.Setup(x => x.Dispose()).Callback(() => locked = false);
                    return handle.Object;
                });
            _transaction.Setup(x => x.Commit()).Callback(() => Assert.True(locked));
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.Commit());
            Assert.False(locked);
        }

        [Fact, StaticLock]
        public void OnStateElection_StoresWhenTheOpenPeriodEnds()
        {
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
                pairs => pairs.Any(p => p.Key == "OpenUntil" && p.Value == (_timestamp + 120).ToString()))));
        }

        [Fact, StaticLock]
        public void ReadCircuit_ReturnsWhenTheOpenPeriodEnds()
        {
            Open(openedSecondsAgo: 5);
            _circuit["OpenUntil"] = (_timestamp + 115).ToString();

            var circuit = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

            Assert.Equal(_now.AddSeconds(115), circuit.OpenUntil);
        }

        [Fact, StaticLock]
        public void ReadCircuit_CountsOnlyTheFailuresWithinTheStoredWindow()
        {
            _circuit = new Dictionary<string, string> { { "WindowSeconds", "60" } };
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 100}:b");

            var circuit = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

            Assert.Equal(1, circuit.Failures);
            Assert.Equal(CircuitBreakerAttribute.ClosedState, circuit.State);
            Assert.Null(circuit.OpenUntil);
        }

        [Fact, StaticLock]
        public void ReadCircuit_CountsEveryFailure_OfACircuitWrittenWithoutAWindow()
        {
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 100}:b");

            Assert.Equal(2, CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource").Failures);
        }

        [Fact, StaticLock]
        public void OnStateElection_ForgetsAQuietClosedCircuit_WhenAJobSucceeds()
        {
            var attribute = Create();
            var stale = $"{_timestamp - 500}:a";
            _failures.Add(stale);
            _circuit = new Dictionary<string, string>
            {
                { "State", CircuitBreakerAttribute.ClosedState },
                { "OpenedAt", (_timestamp - 121).ToString() },
                { "WindowSeconds", "60" },
            };
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.RemoveFromSet(CircuitBreakerAttribute.CircuitsSetKey, "resource"));
            _transaction.Verify(x => x.RemoveHash(CircuitKey));
            _transaction.Verify(x => x.RemoveFromSet(FailuresKey, stale));
            _transaction.Verify(x => x.Commit());
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNotFailTheSuccess_WhenAQuietCircuitCannotBeForgotten()
        {
            var attribute = Create();
            _circuit = new Dictionary<string, string>
            {
                { "State", CircuitBreakerAttribute.ClosedState },
                { "OpenedAt", (_timestamp - 121).ToString() },
                { "WindowSeconds", "60" },
            };
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
            _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("storage is down"));

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.RemoveFromSet(CircuitBreakerAttribute.CircuitsSetKey, "resource"));
            Assert.IsType<SucceededState>(_electContext.Object.CandidateState);
        }

        [Fact, StaticLock]
        public void OnStateElection_KeepsListingACircuit_ClosedLessThanItsRetentionAgo()
        {
            var attribute = Create();
            _circuit = new Dictionary<string, string>
            {
                { "State", CircuitBreakerAttribute.ClosedState },
                { "OpenedAt", (_timestamp - 100).ToString() },
                { "WindowSeconds", "60" },
            };
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_KeepsListingACircuit_WithFailuresInTheWindow()
        {
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _circuit = new Dictionary<string, string> { { "WindowSeconds", "60" } };
            _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_ExpiresTheFailures_WithTheWindow_WhenTheStorageSupportsIt()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _electContext.ApplyContext.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            _electContext.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            var attribute = Create();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            storageTransaction.Verify(x => x.ExpireSet(FailuresKey, TimeSpan.FromSeconds(60) + TimeSpan.FromMinutes(1)));
            // A closed circuit's hash expires a day after max(window, open period)
            storageTransaction.Verify(x => x.ExpireHash(CircuitKey, TimeSpan.FromSeconds(120) + TimeSpan.FromDays(1)));
        }

        [Fact, StaticLock]
        public void OnStateElection_KeepsTheHashOfAnOpenCircuit_WhenTheStorageSupportsExpiration()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _electContext.ApplyContext.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            _electContext.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            storageTransaction.Verify(x => x.PersistHash(CircuitKey));
            storageTransaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNotExpireAnything_WhenTheStorageDoesNotSupportIt()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _electContext.ApplyContext.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            var attribute = Create();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            storageTransaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
            storageTransaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
            storageTransaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateElection_ShortensTheKeys_OfALongResource()
        {
            var attribute = new CircuitBreakerAttribute("partner-{0}", 3, 60, 120);
            var partner = new string('p', 95);
            _electContext.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => ForPartner(partner));
            _electContext.ApplyContext.Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns(new HashSet<string>());
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            attribute.OnStateElection(_electContext.Object);

            _transaction.Verify(x => x.AddToSet(
                It.Is<string>(key => key.StartsWith("circuit:partner-ppp", StringComparison.Ordinal) && key.EndsWith(":failures", StringComparison.Ordinal) && key.Length <= 100),
                $"{_timestamp}:JobId",
                _timestamp));
        }

        [Fact, StaticLock]
        public void OnStateElection_WaitsForTheCommit_BeforeRaisingTheAlertOfAFailedProbe()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            HalfOpen(probe: "JobId");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            // Last, because reading Object builds the context from the setup above
            var afterCommit = new PostCommitActions();
            _electContext.ApplyContext.Object.AfterCommit = afterCommit;

            attribute.OnStateElection(_electContext.Object);
            Assert.Empty(sink.Received);

            afterCommit.Run();
            Assert.Equal(AlertKind.CircuitOpened, Assert.Single(sink.Received).Kind);
        }

        [Fact, StaticLock]
        public void OnStateElection_RaisesTheAlertOfAnOpenedCircuit_OnlyWhenTheStateChangeIsOver()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            // Last, because reading Object builds the context from the setup above
            var afterCommit = new PostCommitActions();
            _electContext.ApplyContext.Object.AfterCommit = afterCommit;

            attribute.OnStateElection(_electContext.Object);
            Assert.Empty(sink.Received);

            afterCommit.Run();
            Assert.Equal(AlertKind.CircuitOpened, Assert.Single(sink.Received).Kind);
        }

        [Fact, StaticLock]
        public void OnStateElection_StillRaisesTheAlertOfAnOpenedCircuit_WhenTheJobsStateChangeFails()
        {
            var sink = new RecordingSink();
            Alerts.AddSink(sink);
            var attribute = Create();
            _failures.Add($"{_timestamp - 10}:a");
            _failures.Add($"{_timestamp - 20}:b");
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());
            var afterCommit = new PostCommitActions();
            _electContext.ApplyContext.Object.AfterCommit = afterCommit;

            attribute.OnStateElection(_electContext.Object);

            // The circuit is open already, so a retried state change wouldn't alert
            afterCommit.RunAfterFailure();
            Assert.Equal(AlertKind.CircuitOpened, Assert.Single(sink.Received).Kind);
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNotCountAFailureTwice_WhenTheStateChangeIsRetriedInALaterSecond()
        {
            var attribute = Create();
            StoreFailureWrites();
            var failed = new FailedState(new Exception());
            _electContext.ApplyContext.NewStateObject = failed;

            attribute.OnStateElection(_electContext.Object);
            ConcurrencyControl.Clock = () => _now.AddSeconds(2);
            attribute.OnStateElection(new ElectStateContext(_electContext.ApplyContext.Object));

            Assert.Equal(new[] { $"{_timestamp}:JobId" }, _failures);
            _transaction.Verify(x => x.AddToSet(FailuresKey, It.IsAny<string>(), It.IsAny<double>()), Times.Once);
        }

        [Fact, StaticLock]
        public void OnStateElection_DoesNotCountAFailureTwice_WhenAnotherFilterTurnedItIntoARetry()
        {
            var attribute = Create();
            StoreFailureWrites();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            var first = new ElectStateContext(_electContext.ApplyContext.Object);
            first.CandidateState = new ScheduledState(TimeSpan.FromMinutes(1));
            attribute.OnStateElection(first);

            ConcurrencyControl.Clock = () => _now.AddSeconds(1);
            var retried = new ElectStateContext(_electContext.ApplyContext.Object);
            retried.CandidateState = new ScheduledState(TimeSpan.FromMinutes(1));
            attribute.OnStateElection(retried);

            Assert.Single(_failures);
        }

        [Fact, StaticLock]
        public void OnStateElection_CountsTheNextFailureOfTheSameJob_AsANewFailure()
        {
            var attribute = Create();
            StoreFailureWrites();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());
            attribute.OnStateElection(_electContext.Object);

            ConcurrencyControl.Clock = () => _now.AddSeconds(30);
            var first = _electContext.ApplyContext.Object;
            var next = new ApplyStateContext(first.Storage, first.Connection, first.Transaction, first.BackgroundJob, new FailedState(new Exception()), null, EmptyProfiler.Instance, first.StateMachine);
            attribute.OnStateElection(new ElectStateContext(next));

            Assert.Equal(new[] { $"{_timestamp}:JobId", $"{_timestamp + 30}:JobId" }, _failures.OrderBy(x => x));
        }

        [Fact, StaticLock]
        public void OnStateElection_CountsTheFailureAgain_WhenItsFirstCommitFailed()
        {
            var attribute = Create();
            _transaction.SetupSequence(x => x.Commit()).Throws(new TimeoutException()).Pass();
            _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

            Assert.Throws<TimeoutException>(() => attribute.OnStateElection(_electContext.Object));
            attribute.OnStateElection(new ElectStateContext(_electContext.ApplyContext.Object));

            _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp), Times.Exactly(2));
            _transaction.Verify(x => x.Commit(), Times.Exactly(2));
        }

        private static CircuitBreakerAttribute Create()
        {
            return new CircuitBreakerAttribute("resource", failureThreshold: 3, windowSeconds: 60, openSeconds: 120);
        }

        // Failures added through the connection's transactions become visible to later reads
        private void StoreFailureWrites()
        {
            _transaction
                .Setup(x => x.AddToSet(FailuresKey, It.IsAny<string>(), It.IsAny<double>()))
                .Callback<string, string, double>((_, member, _) => _failures.Add(member));
        }

        // Hash writes made through the connection's transactions become visible to later reads
        private void StoreHashWrites()
        {
            _transaction
                .Setup(x => x.SetRangeInHash(CircuitKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) =>
                {
                    _circuit ??= new Dictionary<string, string>();
                    foreach (var pair in pairs) _circuit[pair.Key] = pair.Value;
                });
        }

        private void Open(int openedSecondsAgo)
        {
            _circuit = new Dictionary<string, string>
            {
                { "State", CircuitBreakerAttribute.OpenState },
                { "OpenedAt", (_timestamp - openedSecondsAgo).ToString() }
            };
        }

        // Without a probe: a circuit written before probes were recorded
        private void HalfOpen(string probe = null)
        {
            _circuit = new Dictionary<string, string>
            {
                { "State", CircuitBreakerAttribute.HalfOpenState },
                { "OpenedAt", (_timestamp - 200).ToString() }
            };

            if (probe != null) _circuit["Probe"] = probe;
        }

        private void ProbeState(string stateName)
        {
            _performContext.Connection
                .Setup(x => x.GetStateData("probe-job"))
                .Returns(stateName != null ? new StateData { Name = stateName, Data = new Dictionary<string, string>() } : null);
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

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void ForPartner(string partner)
        {
        }
    }
}
