namespace Masterly.NightShift.Core.Tests;

public class RetryAttributeFacts
{
    private const string JobId = "id";

    private readonly FailedState _failedState;
    private readonly Mock<IStorageConnection> _connection;
    private readonly ElectStateContextMock _context;
    private readonly Mock<IWriteOnlyTransaction> _transaction;

    public RetryAttributeFacts()
    {
        _failedState = new FailedState(new InvalidOperationException());
        _connection = new Mock<IStorageConnection>();
        _transaction = new Mock<IWriteOnlyTransaction>();

        _context = new ElectStateContextMock();
        _context.ApplyContext.BackgroundJob.Id = JobId;
        _context.ApplyContext.Connection = _connection;
        _context.ApplyContext.NewStateObject = _failedState;
    }

    [Fact]
    public void Ctor_SetsPositiveRetryAttemptsNumber_ByDefault()
    {
        AutomaticRetryAttribute filter = new();
        Assert.Equal(10, filter.Attempts);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenAttemptsValueIsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AutomaticRetryAttribute { Attempts = -1 });
    }

    [Fact]
    public void Ctor_ThrowsAnArgumentException_WhenDelaysInSecondsIsEmpty()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new AutomaticRetryAttribute { DelaysInSeconds = [] });

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenDelaysInSecondsContainsNegativeNumbers()
    {
        Assert.Throws<ArgumentException>(
            () => new AutomaticRetryAttribute { DelaysInSeconds = [1, -5] });
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenDelayByAttemptIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AutomaticRetryAttribute { DelayInSecondsByAttemptFunc = null });
    }

    [Fact]
    public void Ctor_SetsOnAttemptsExceededAction_ByDefault()
    {
        AutomaticRetryAttribute filter = new();
        Assert.Equal(AttemptsExceededAction.Fail, filter.OnAttemptsExceeded);
    }

    [Fact]
    public void Ctor_DelayByAttemptIsNotNull_ByDefault()
    {
        AutomaticRetryAttribute filter = new();
        Assert.NotNull(filter.DelayInSecondsByAttemptFunc);
    }

    [Fact]
    public void DelaysInSeconds_SetsValueCorrectly()
    {
        AutomaticRetryAttribute filter = new() { DelaysInSeconds = [5, 8] };

        Assert.Equal(2, filter.DelaysInSeconds.Length);
        Assert.Equal(5, filter.DelaysInSeconds[0]);
        Assert.Equal(8, filter.DelaysInSeconds[1]);
    }

    [Fact]
    public void DelaysInSeconds_CanBeSetToNull()
    {
        AutomaticRetryAttribute filter = new() { DelaysInSeconds = null };
        Assert.NotNull(filter);
    }

    [Fact]
    public void OnStateElection_UsesTheDelayFunction_AfterDelaysInSecondsIsResetToNull()
    {
        AutomaticRetryAttribute filter = new()
        {
            DelaysInSeconds = [0],
            DelayInSecondsByAttemptFunc = attempt => 60,
        };
        filter.DelaysInSeconds = null;

        filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }

    [Fact]
    public void DelayInSecondsByAttemptFunc_ReturnCorrectValue_WhenCustomFunctionIsSet()
    {
        AutomaticRetryAttribute filter = new() { DelayInSecondsByAttemptFunc = attempt => (int)attempt % 3 };

        Assert.Equal(1, filter.DelayInSecondsByAttemptFunc(1));
        Assert.Equal(2, filter.DelayInSecondsByAttemptFunc(2));
        Assert.Equal(0, filter.DelayInSecondsByAttemptFunc(3));
        Assert.Equal(1, filter.DelayInSecondsByAttemptFunc(4));
        Assert.Equal(2, filter.DelayInSecondsByAttemptFunc(5));
        Assert.Equal(1, filter.DelayInSecondsByAttemptFunc(100));
    }

    [Fact]
    public void OnStateElection_ThrowsAnException_WhenDelayInSecondsByAttemptFuncThrowsAnException()
    {
        Exception exception = new();

        AutomaticRetryAttribute filter = new()
        {
            DelayInSecondsByAttemptFunc = attempt =>
            {
                throw exception;
            }
        };

        Exception thrownException = Assert.Throws<Exception>(() => filter.OnStateElection(_context.Object));

        Assert.Equal(exception, thrownException);
    }

    [Fact]
    public void OnStateElection_UsesDelaysInSeconds_WhenBothDelaysInSecondsAndDelayInSecondsByAttemptFuncAreSpecified()
    {
        AutomaticRetryAttribute filter = new()
        {
            DelayInSecondsByAttemptFunc = attempt => 1,
            DelaysInSeconds = [0]
        };

        filter.OnStateElection(_context.Object);

        Assert.IsType<EnqueuedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DoesNotChangeState_IfRetryAttemptsIsSetToZero()
    {
        AutomaticRetryAttribute filter = new() { Attempts = 0 };
        filter.OnStateElection(_context.Object);

        Assert.Same(_failedState, _context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ChangeStateToScheduled_IfRetryAttemptsWereNotExceeded()
    {
        AutomaticRetryAttribute filter = CreateFilter();
        filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        Assert.True(((ScheduledState)_context.Object.CandidateState).EnqueueAt > DateTime.UtcNow);

        Assert.NotNull(_context.Object.CandidateState.Reason);
        Assert.Contains("1 of 1", _context.Object.CandidateState.Reason);

        _connection.Verify(x => x.SetJobParameter(JobId, "RetryCount", "1"));
    }

    [Fact]
    public void OnStateElection_ChangeStateToEnqueued_IfDelayIsZero()
    {
        AutomaticRetryAttribute filter = new()
        {
            Attempts = 1,
            DelaysInSeconds = [0]
        };

        filter.OnStateElection(_context.Object);

        Assert.IsType<EnqueuedState>(_context.Object.CandidateState);
        Assert.NotNull(_context.Object.CandidateState.Reason);
        Assert.Contains("1 of 1", _context.Object.CandidateState.Reason);

        _connection.Verify(x => x.SetJobParameter(JobId, "RetryCount", "1"));
    }

    [Fact]
    public void OnStateElection_DoesNotChangeAnything_IfCandidateStateIsNotFailedState()
    {
        AutomaticRetryAttribute filter = CreateFilter();
        Mock<IState> state = new();
        _context.ApplyContext.NewStateObject = null;
        _context.ApplyContext.NewState = state;

        filter.OnStateElection(_context.Object);

        Assert.Same(state.Object, _context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DoesNotChangeState_IfRetryAttemptsNumberExceeded()
    {
        _connection.Setup(x => x.GetJobParameter(JobId, "RetryCount")).Returns("1");
        AutomaticRetryAttribute filter = CreateFilter();

        filter.OnStateElection(_context.Object);

        Assert.Same(_failedState, _context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ChangesStateToDeleted_IfRetryAttemptsNumberExceededAndOnAttemptsExceededIsSetToDelete()
    {
        _connection.Setup(x => x.GetJobParameter(JobId, "RetryCount")).Returns("1");
        AutomaticRetryAttribute filter = new() { Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Delete };

        filter.OnStateElection(_context.Object);

        Assert.IsType<DeletedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ChangesStateToFailed_IfRetryAttemptsNumberExceededAndOnAttemptsExceedIsSetToFail()
    {
        _connection.Setup(x => x.GetJobParameter(JobId, "RetryCount")).Returns("1");
        AutomaticRetryAttribute filter = new() { Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail };

        filter.OnStateElection(_context.Object);

        Assert.IsType<FailedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ChangesStateToDeleted_IfRetryAttemptsNumberIsZeroAndOnAttemptsExceedIsSetToDelete()
    {
        _connection.Setup(x => x.GetJobParameter(JobId, "RetryCount")).Returns("0");
        AutomaticRetryAttribute filter = new() { Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete };

        filter.OnStateElection(_context.Object);

        Assert.IsType<DeletedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_DoesNotDoAnything_WhenOnlyOnIsSpecified_AndIneligibleExceptionIsPassed()
    {
        AutomaticRetryAttribute filter = new() { OnlyOn = [typeof(FormatException), typeof(NullReferenceException)] };
        _context.ApplyContext.NewStateObject = new FailedState(new ArgumentException());

        filter.OnStateElection(_context.Object);

        Assert.IsType<FailedState>(_context.Object.CandidateState);
        _connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_TriggersRetryAnyway_WhenEmptyOnlyOnIsSpecified()
    {
        AutomaticRetryAttribute filter = new() { OnlyOn = Type.EmptyTypes };
        _context.ApplyContext.NewStateObject = new FailedState(new ArgumentException());

        filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter(JobId, "RetryCount", "1"));
    }

    [Fact]
    public void OnStateElection_WorksAsExpected_WhenOnlyOnIsSpecified_AndAnEligibleExceptionIsPassed()
    {
        AutomaticRetryAttribute filter = new() { OnlyOn = [typeof(NullReferenceException), typeof(InvalidOperationException)] };

        filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter(JobId, "RetryCount", "1"));
    }

    [Fact]
    public void OnStateElection_WorksAsExpected_WhenOnlyOnIsSpecified_AndADerivedClassOfAnEligibleExceptionIsPassed()
    {
        AutomaticRetryAttribute filter = new() { OnlyOn = [typeof(ArgumentException)] };
        _context.ApplyContext.NewStateObject = new FailedState(new ArgumentNullException());

        filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter(JobId, "RetryCount", "1"));
    }

    [Fact]
    public void OnStateElection_WorksAsExpected_WhenExceptOnIsSpecified_AndIneligibleExceptionIsPassed()
    {
        AutomaticRetryAttribute filter = new() { ExceptOn = [typeof(FormatException), typeof(NullReferenceException)] };
        _context.ApplyContext.NewStateObject = new FailedState(new ArgumentException());

        filter.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
        _connection.Verify(x => x.SetJobParameter(JobId, "RetryCount", "1"));
    }

    [Fact]
    public void OnStateElection_DoesNotDoAnything_WhenExceptOnIsSpecified_AndAnEligibleExceptionIsPassed()
    {
        AutomaticRetryAttribute filter = new() { ExceptOn = [typeof(NullReferenceException), typeof(InvalidOperationException)] };

        filter.OnStateElection(_context.Object);

        Assert.IsType<FailedState>(_context.Object.CandidateState);
        _connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_DoesNotDoAnything_WhenExceptOnIsSpecified_AndADerivedClassOfAnEligibleExceptionIsPassed()
    {
        AutomaticRetryAttribute filter = new() { ExceptOn = [typeof(ArgumentException)] };
        _context.ApplyContext.NewStateObject = new FailedState(new ArgumentNullException());

        filter.OnStateElection(_context.Object);

        Assert.IsType<FailedState>(_context.Object.CandidateState);
        _connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_AddsJobToRetriesSet_IfNewStateIsScheduled()
    {
        // Arrange
        AutomaticRetryAttribute filter = CreateFilter();

        ScheduledState newState = new(DateTime.UtcNow) { Reason = "Retry attempt ..." };
        ApplyStateContext applyStateContext = CreatApplyStateContext(newState);

        // Act
        filter.OnStateApplied(applyStateContext, _transaction.Object);

        // Assert
        _transaction.Verify(t => t.AddToSet("retries", JobId));
    }

    [Fact]
    public void OnStateApplied_DoesNotAddJobToRetriesSet_IfNewStateIsScheduledAndReasonIsNull()
    {
        // Arrange
        AutomaticRetryAttribute filter = CreateFilter();

        ScheduledState newState = new(DateTime.UtcNow) { Reason = null };
        ApplyStateContext applyStateContext = CreatApplyStateContext(newState);

        // Act
        filter.OnStateApplied(applyStateContext, _transaction.Object);

        // Assert
        _transaction.Verify(t => t.AddToSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_DoesNotAddJobToRetriesSet_IfNewStateIsScheduledAndReasonDoesNotMatch()
    {
        // Arrange
        AutomaticRetryAttribute filter = CreateFilter();

        ScheduledState newState = new(DateTime.UtcNow) { Reason = "Some reason." };
        ApplyStateContext applyStateContext = CreatApplyStateContext(newState);

        // Act
        filter.OnStateApplied(applyStateContext, _transaction.Object);

        // Assert
        _transaction.Verify(t => t.AddToSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_DoesNotAddJobToRetriesSet_IfNewStateIsEnqueued()
    {
        // Arrange
        AutomaticRetryAttribute filter = CreateFilter();

        EnqueuedState newState = new() { Reason = "Retry attempt ..." };
        ApplyStateContext applyStateContext = CreatApplyStateContext(newState);

        // Act
        filter.OnStateApplied(applyStateContext, _transaction.Object);

        // Assert
        _transaction.Verify(t => t.AddToSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static AutomaticRetryAttribute CreateFilter()
    {
        return new AutomaticRetryAttribute { Attempts = 1 };
    }

    private ApplyStateContext CreatApplyStateContext(IState newState)
    {
        ApplyStateContextMock context = new();
        context.BackgroundJob.Id = JobId;
        context.Transaction = _transaction;
        context.NewStateObject = newState;
        return context.Object;
    }
}
