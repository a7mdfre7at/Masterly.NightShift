using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class CircuitBreakerAttributeFacts : IDisposable
{
    private const string CircuitKey = "circuit:resource";
    private const string FailuresKey = "circuit:resource:failures";

    private readonly PerformContextMock _performContext = new();
    private readonly ElectStateContextMock _electContext = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new();
    private readonly HashSet<string> _failures = [];
    private readonly DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly long _timestamp;
    private Dictionary<string, string> _circuit;

    public CircuitBreakerAttributeFacts()
    {
        _timestamp = JobHelper.ToTimestamp(_now);

        foreach (Mock<IStorageConnection>? connection in new[] { _performContext.Connection, _electContext.ApplyContext.Connection })
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
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
        _failures.Add($"{_timestamp - 10}:a");
        _failures.Add($"{_timestamp - 20}:b");
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());
        ElectStateContext context = _electContext.Object;
        context.CandidateState = new ScheduledState(TimeSpan.FromMinutes(1));

        attribute.OnStateElection(context);

        _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState))));
    }

    public static IEnumerable<object[]> FailuresWithoutCallingTheDependency =>
    [
        [new ConcurrencyLimitException("resource", "All slots stayed in use")],
        [new DistributedLockTimeoutException("lock")],
        [new RetryLaterException(TimeSpan.FromMinutes(1))],
        // A job that skipped or canceled itself didn't fail
        [new TerminateJobException("nothing to do")],
        [new TerminateJobException("stopped", TerminateJobOutcome.Canceled)],
        [new JobPerformanceException("wrapped", new TerminateJobException("nothing to do"))],
    ];

    [Fact, StaticLock]
    public void OnStateElection_Counts_AJobTerminatedAsFailed()
    {
        CircuitBreakerAttribute attribute = Create();
        _electContext.ApplyContext.NewStateObject = new FailedState(new TerminateJobException("Invalid input", TerminateJobOutcome.Failed));

        attribute.OnStateElection(_electContext.Object);

        _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
    }

    [Theory, StaticLock]
    [MemberData(nameof(FailuresWithoutCallingTheDependency))]
    public void OnStateElection_DoesNotCount_FailuresInWhichTheDependencyWasNeverCalled(Exception exception)
    {
        // Six jobs waiting for DisableConcurrentExecution's lock must not open the circuit of a healthy service
        CircuitBreakerAttribute attribute = Create();
        _failures.Add($"{_timestamp - 10}:a");
        _failures.Add($"{_timestamp - 20}:b");
        _electContext.ApplyContext.NewStateObject = new FailedState(exception);
        ElectStateContext context = _electContext.Object;
        context.CandidateState = new ScheduledState(TimeSpan.FromMinutes(1));

        attribute.OnStateElection(context);

        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        _electContext.ApplyContext.Connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnStateElection_DoesNotReopenTheCircuit_WhenTheProbeFailsWithoutCallingTheDependency()
    {
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "JobId");
        _electContext.ApplyContext.NewStateObject = new FailedState(new DistributedLockTimeoutException("lock"));

        attribute.OnStateElection(_electContext.Object);

        _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnStateElection_CountsTheExcludedExceptions_WhenExceptOnIsEmpty()
    {
        CircuitBreakerAttribute attribute = new("resource", 3, 60, 120) { ExceptOn = Type.EmptyTypes };
        _electContext.ApplyContext.NewStateObject = new FailedState(new DistributedLockTimeoutException("lock"));

        attribute.OnStateElection(_electContext.Object);

        _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnStateElection_CountsOnlyTheListedExceptions_WhenOnlyOnIsGiven()
    {
        CircuitBreakerAttribute attribute = new("resource", 3, 60, 120) { OnlyOn = [typeof(TimeoutException)] };

        _electContext.ApplyContext.NewStateObject = new FailedState(new InvalidOperationException());
        attribute.OnStateElection(_electContext.Object);
        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);

        ElectStateContextMock timedOut = new();
        timedOut.ApplyContext.Connection = _electContext.ApplyContext.Connection;
        timedOut.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Sample());
        timedOut.ApplyContext.NewStateObject = new FailedState(new TimeoutException());
        attribute.OnStateElection(timedOut.Object);
        _transaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact]
    public void ExceptOn_ByDefault_ListsTheFailuresInWhichTheDependencyWasNeverCalled()
    {
        CircuitBreakerAttribute attribute = Create();

        Assert.Empty(attribute.OnlyOn);
        Assert.Equal([typeof(ConcurrencyLimitException), typeof(DistributedLockTimeoutException), typeof(RetryLaterException)], attribute.ExceptOn);
    }

    [Fact, StaticLock]
    public void OnStateElection_PutsAHeldJobBackInItsQueue_WhenItIsEnqueuedAgain()
    {
        CircuitBreakerAttribute attribute = Create();
        _electContext.ApplyContext.OldStateName = ScheduledState.StateName;
        _electContext.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { DrainModeFilter.HeldQueueParameter, "\"emails\"" } };
        _electContext.ApplyContext.NewStateObject = new EnqueuedState();

        attribute.OnStateElection(_electContext.Object);

        Assert.Equal("emails", Assert.IsType<EnqueuedState>(_electContext.Object.CandidateState).Queue);
    }

    [Fact, StaticLock]
    public void OnStateElection_IgnoresFailuresOutsideTheWindow()
    {
        CircuitBreakerAttribute attribute = Create();
        string old = $"{_timestamp - 61}:a";
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
        CircuitBreakerAttribute attribute = Create();
        _electContext.ApplyContext.NewStateObject = new EnqueuedState();

        attribute.OnStateElection(_electContext.Object);

        _electContext.ApplyContext.Transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_LetsJobsThrough_WhenTheCircuitIsClosed()
    {
        CircuitBreakerAttribute attribute = Create();
        PerformingContext performing = _performContext.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ReschedulesJobs_WhileTheCircuitIsOpen()
    {
        CircuitBreakerAttribute attribute = Create();
        Open(openedSecondsAgo: 30);
        PerformingContext performing = _performContext.GetPerformingContext();

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
        CircuitBreakerAttribute attribute = Create();
        Open(openedSecondsAgo: 121);
        PerformingContext performing = _performContext.GetPerformingContext();

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
        CircuitBreakerAttribute attribute = Create();
        Open(openedSecondsAgo: 121);
        int commits = 0;
        _transaction.Setup(x => x.Commit()).Callback(() =>
        {
            if (++commits == 1)
            {
                throw new InvalidOperationException("deadlock victim");
            }
        });
        PerformingContext performing = _performContext.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        Assert.Equal(2, commits);
        _transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Any(p => p.Key == "Probe" && p.Value == "JobId"))), Times.Exactly(2));
    }

    [Fact, StaticLock]
    public void OnPerforming_ReschedulesOtherJobs_WhileTheProbeRuns()
    {
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "probe-job");
        ProbeState(ProcessingState.StateName);
        PerformingContext performing = _performContext.GetPerformingContext();

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
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "probe-job");

        // The probe succeeds and closes the circuit right after this job read the circuit
        _performContext.Connection
            .Setup(x => x.GetStateData("probe-job"))
            .Returns(() =>
            {
                _circuit = new Dictionary<string, string> { { "State", CircuitBreakerAttribute.ClosedState } };
                return new StateData { Name = SucceededState.StateName, Data = new Dictionary<string, string>() };
            });
        PerformingContext performing = _performContext.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_LetsTheProbeRunAgain_WithoutWritingAnything()
    {
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "JobId");
        PerformingContext performing = _performContext.GetPerformingContext();

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
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "probe-job");
        ProbeState(probeState);
        PerformingContext performing = _performContext.GetPerformingContext();

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
        CircuitBreakerAttribute attribute = Create();
        Open(openedSecondsAgo: 121);
        ProbeState(ProcessingState.StateName);
        _performContext.Connection
            .Setup(x => x.AcquireDistributedLock("circuit:resource:lock", It.IsAny<TimeSpan>()))
            .Callback(() => HalfOpen(probe: "probe-job"))
            .Returns(new Mock<IDisposable>().Object);
        PerformingContext performing = _performContext.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
    }

    [Fact, StaticLock]
    public void ReadCircuit_ReturnsTheProbe_OfAHalfOpenCircuit()
    {
        HalfOpen(probe: "probe-job");

        CircuitBreakerAttribute.CircuitState state = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

        Assert.Equal(CircuitBreakerAttribute.HalfOpenState, state.State);
        Assert.Equal("probe-job", state.ProbeJobId);
    }

    [Fact, StaticLock]
    public void OnStateElection_IgnoresTheSuccessOfAnotherJob_WhileTheProbeRuns()
    {
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "probe-job");
        _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        attribute.OnStateElection(_electContext.Object);

        _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnStateElection_OnlyRecordsTheFailureOfAnotherJob_WhileTheProbeRuns()
    {
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "JobId");
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        attribute.OnStateElection(_electContext.Object);

        _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.OpenState))));
    }

    [Fact, StaticLock]
    public void OnStateElection_ClosesTheCircuit_WhenTheProbeSucceeds()
    {
        CircuitBreakerAttribute attribute = Create();
        HalfOpen();
        _failures.Add($"{_timestamp - 10}:a");
        _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        attribute.OnStateElection(_electContext.Object);

        Mock<IWriteOnlyTransaction> transaction = _electContext.ApplyContext.Transaction;
        transaction.Verify(x => x.SetRangeInHash(CircuitKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
            pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.ClosedState))));
        transaction.Verify(x => x.RemoveFromSet(FailuresKey, $"{_timestamp - 10}:a"));
    }

    [Fact, StaticLock]
    public void OnStateElection_ReopensTheCircuit_WhenTheProbeFails()
    {
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
        _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        attribute.OnStateElection(_electContext.Object);

        _electContext.ApplyContext.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
    }

    [Fact, StaticLock]
    public void ReadCircuit_ReturnsClosed_WhenNothingIsStored()
    {
        CircuitBreakerAttribute.CircuitState state = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

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
        CircuitBreakerAttribute attribute = new(3, 60, 120);

        Assert.Null(attribute.Resource);
        Assert.Equal(3, attribute.FailureThreshold);
        Assert.Equal(60, attribute.WindowSeconds);
        Assert.Equal(120, attribute.OpenSeconds);
    }

    [Fact, StaticLock]
    public void OnStateElection_UsesTheFullTypeName_MethodAndParameterTypes_AsTheDefaultResource()
    {
        const string defaultFailuresKey = "circuit:masterly.nightshift.core.tests.circuitbreakerattributefacts.sample():failures";
        CircuitBreakerAttribute attribute = new(3, 60, 120);
        _electContext.ApplyContext.Connection.Setup(x => x.GetAllItemsFromSet(defaultFailuresKey)).Returns([]);
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        attribute.OnStateElection(_electContext.Object);

        _transaction.Verify(x => x.AddToSet(defaultFailuresKey, $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.AddToSet(CircuitBreakerAttribute.CircuitsSetKey, "masterly.nightshift.core.tests.circuitbreakerattributefacts.sample()"));
    }

    [Fact, StaticLock]
    public void OnPerformed_DoesNotTouchTheStorage()
    {
        CircuitBreakerAttribute attribute = Create();

        attribute.OnPerformed(_performContext.GetPerformedContext());

        _performContext.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        _performContext.Connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnStateElection_RaisesACircuitOpenedAlert_WhenTheThresholdIsReached()
    {
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
        _failures.Add($"{_timestamp - 10}:a");
        _failures.Add($"{_timestamp - 20}:b");
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        attribute.OnStateElection(_electContext.Object);

        Alert alert = Assert.Single(sink.Received);
        Assert.Equal(AlertKind.CircuitOpened, alert.Kind);
        Assert.Equal("Circuit 'resource' is open", alert.Title);
        Assert.Equal("3 failure(s) within 60 seconds; jobs are held for 120 seconds.", alert.Message);
        Assert.Equal("resource", alert.Resource);
        Assert.Equal("JobId", alert.JobId);
    }

    [Fact, StaticLock]
    public void OnStateElection_RaisesACircuitOpenedAlert_WhenTheProbeFails()
    {
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
        HalfOpen();
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        attribute.OnStateElection(_electContext.Object);

        Alert alert = Assert.Single(sink.Received);
        Assert.Equal(AlertKind.CircuitOpened, alert.Kind);
        Assert.Equal("3 failure(s) within 60 seconds; jobs are held for 120 seconds.", alert.Message);
    }

    [Fact, StaticLock]
    public void OnStateElection_DoesNotRaiseAnAlert_BelowTheThreshold()
    {
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        attribute.OnStateElection(_electContext.Object);

        Assert.Empty(sink.Received);
    }

    [Fact, StaticLock]
    public void OnStateElection_RaisesTheAlert_OnlyAfterTheOpenStateIsCommitted()
    {
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
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
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
        bool locked = false;
        _electContext.ApplyContext.Connection
            .Setup(x => x.AcquireDistributedLock("circuit:resource:lock", It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                locked = true;
                Mock<IDisposable> handle = new();
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
        CircuitBreakerAttribute attribute = Create();
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

        CircuitBreakerAttribute.CircuitState circuit = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

        Assert.Equal(_now.AddSeconds(115), circuit.OpenUntil);
    }

    [Fact, StaticLock]
    public void ReadCircuit_CountsOnlyTheFailuresWithinTheStoredWindow()
    {
        _circuit = new Dictionary<string, string> { { "WindowSeconds", "60" } };
        _failures.Add($"{_timestamp - 10}:a");
        _failures.Add($"{_timestamp - 100}:b");

        CircuitBreakerAttribute.CircuitState circuit = CircuitBreakerAttribute.ReadCircuit(_performContext.Connection.Object, "resource");

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
        CircuitBreakerAttribute attribute = Create();
        string stale = $"{_timestamp - 500}:a";
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
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
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
        CircuitBreakerAttribute attribute = Create();
        _failures.Add($"{_timestamp - 10}:a");
        _circuit = new Dictionary<string, string> { { "WindowSeconds", "60" } };
        _electContext.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        attribute.OnStateElection(_electContext.Object);

        _transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnStateElection_ExpiresTheFailures_WithTheWindow_WhenTheStorageSupportsIt()
    {
        Mock<JobStorageTransaction> storageTransaction = new();
        _electContext.ApplyContext.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
        _electContext.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        CircuitBreakerAttribute attribute = Create();
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        attribute.OnStateElection(_electContext.Object);

        storageTransaction.Verify(x => x.ExpireSet(FailuresKey, TimeSpan.FromSeconds(60) + TimeSpan.FromMinutes(1)));
        // A closed circuit's hash expires a day after max(window, open period)
        storageTransaction.Verify(x => x.ExpireHash(CircuitKey, TimeSpan.FromSeconds(120) + TimeSpan.FromDays(1)));
    }

    [Fact, StaticLock]
    public void OnStateElection_KeepsTheHashOfAnOpenCircuit_WhenTheStorageSupportsExpiration()
    {
        Mock<JobStorageTransaction> storageTransaction = new();
        _electContext.ApplyContext.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
        _electContext.ApplyContext.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        CircuitBreakerAttribute attribute = Create();
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
        Mock<JobStorageTransaction> storageTransaction = new();
        _electContext.ApplyContext.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
        CircuitBreakerAttribute attribute = Create();
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        attribute.OnStateElection(_electContext.Object);

        storageTransaction.Verify(x => x.AddToSet(FailuresKey, $"{_timestamp}:JobId", _timestamp));
        storageTransaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        storageTransaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnStateElection_ShortensTheKeys_OfALongResource()
    {
        CircuitBreakerAttribute attribute = new("partner-{0}", 3, 60, 120);
        string partner = new('p', 95);
        _electContext.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => ForPartner(partner));
        _electContext.ApplyContext.Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
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
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
        HalfOpen(probe: "JobId");
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        // Last, because reading Object builds the context from the setup above
        PostCommitActions afterCommit = new();
        _electContext.ApplyContext.Object.AfterCommit = afterCommit;

        attribute.OnStateElection(_electContext.Object);
        Assert.Empty(sink.Received);

        afterCommit.Run();
        Assert.Equal(AlertKind.CircuitOpened, Assert.Single(sink.Received).Kind);
    }

    [Fact, StaticLock]
    public void OnStateElection_RaisesTheAlertOfAnOpenedCircuit_OnlyWhenTheStateChangeIsOver()
    {
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
        _failures.Add($"{_timestamp - 10}:a");
        _failures.Add($"{_timestamp - 20}:b");
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        // Last, because reading Object builds the context from the setup above
        PostCommitActions afterCommit = new();
        _electContext.ApplyContext.Object.AfterCommit = afterCommit;

        attribute.OnStateElection(_electContext.Object);
        Assert.Empty(sink.Received);

        afterCommit.Run();
        Assert.Equal(AlertKind.CircuitOpened, Assert.Single(sink.Received).Kind);
    }

    [Fact, StaticLock]
    public void OnStateElection_StillRaisesTheAlertOfAnOpenedCircuit_WhenTheJobsStateChangeFails()
    {
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        CircuitBreakerAttribute attribute = Create();
        _failures.Add($"{_timestamp - 10}:a");
        _failures.Add($"{_timestamp - 20}:b");
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());
        PostCommitActions afterCommit = new();
        _electContext.ApplyContext.Object.AfterCommit = afterCommit;

        attribute.OnStateElection(_electContext.Object);

        // The circuit is open already, so a retried state change wouldn't alert
        afterCommit.RunAfterFailure();
        Assert.Equal(AlertKind.CircuitOpened, Assert.Single(sink.Received).Kind);
    }

    [Fact, StaticLock]
    public void OnStateElection_DoesNotCountAFailureTwice_WhenTheStateChangeIsRetriedInALaterSecond()
    {
        CircuitBreakerAttribute attribute = Create();
        StoreFailureWrites();
        FailedState failed = new(new Exception());
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
        CircuitBreakerAttribute attribute = Create();
        StoreFailureWrites();
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());

        ElectStateContext first = new(_electContext.ApplyContext.Object)
        {
            CandidateState = new ScheduledState(TimeSpan.FromMinutes(1))
        };
        attribute.OnStateElection(first);

        ConcurrencyControl.Clock = () => _now.AddSeconds(1);
        ElectStateContext retried = new(_electContext.ApplyContext.Object)
        {
            CandidateState = new ScheduledState(TimeSpan.FromMinutes(1))
        };
        attribute.OnStateElection(retried);

        Assert.Single(_failures);
    }

    [Fact, StaticLock]
    public void OnStateElection_CountsTheNextFailureOfTheSameJob_AsANewFailure()
    {
        CircuitBreakerAttribute attribute = Create();
        StoreFailureWrites();
        _electContext.ApplyContext.NewStateObject = new FailedState(new Exception());
        attribute.OnStateElection(_electContext.Object);

        ConcurrencyControl.Clock = () => _now.AddSeconds(30);
        ApplyStateContext first = _electContext.ApplyContext.Object;
        ApplyStateContext next = new(first.Storage, first.Connection, first.Transaction, first.BackgroundJob, new FailedState(new Exception()), null, EmptyProfiler.Instance, first.StateMachine);
        attribute.OnStateElection(new ElectStateContext(next));

        Assert.Equal([$"{_timestamp}:JobId", $"{_timestamp + 30}:JobId"], _failures.OrderBy(x => x));
    }

    [Fact, StaticLock]
    public void OnStateElection_CountsTheFailureAgain_WhenItsFirstCommitFailed()
    {
        CircuitBreakerAttribute attribute = Create();
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
                _circuit ??= [];
                foreach (KeyValuePair<string, string> pair in pairs)
                {
                    _circuit[pair.Key] = pair.Value;
                }
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

        if (probe != null)
        {
            _circuit["Probe"] = probe;
        }
    }

    private void ProbeState(string stateName)
    {
        _performContext.Connection
            .Setup(x => x.GetStateData("probe-job"))
            .Returns(stateName != null ? new StateData { Name = stateName, Data = new Dictionary<string, string>() } : null);
    }

    private sealed class RecordingSink : IAlertSink
    {
        public List<Alert> Received { get; } = [];

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
