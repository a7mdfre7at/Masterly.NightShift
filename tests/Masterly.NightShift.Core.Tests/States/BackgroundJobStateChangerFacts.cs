// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.States;

public class BackgroundJobStateChangerFacts
{
    private const string StateName = "State";
    private const string JobId = "1";
    private const string OldStateName = "Old";
    private static readonly string[] FromOldState = [OldStateName];

    private readonly Mock<IStorageConnection> _connection;
    private readonly Job _job;
    private readonly Mock<IState> _state;
    private readonly Mock<IJobFilterProvider> _filterProvider;
    private readonly Mock<IStateMachine> _stateMachine;
    private readonly Mock<IDisposable> _distributedLock;
    private readonly Mock<IWriteOnlyTransaction> _transaction;
    private readonly CancellationTokenSource _cts;
    private readonly StateChangeContextMock _context;

    public BackgroundJobStateChangerFacts()
    {
        _stateMachine = new Mock<IStateMachine>();
        _filterProvider = new Mock<IJobFilterProvider>();
        _filterProvider.Setup(x => x.GetFilters(It.IsAny<Job>())).Returns([]);

        _job = Job.FromExpression(() => Console.WriteLine());
        _state = new Mock<IState>();
        _state.Setup(x => x.Name).Returns(StateName);

        _connection = new Mock<IStorageConnection>();
        _transaction = new Mock<IWriteOnlyTransaction>();

        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);

        _connection.Setup(x => x.CreateExpiredJob(
            It.IsAny<Job>(),
            It.IsAny<IDictionary<string, string>>(),
            It.IsAny<DateTime>(),
            It.IsAny<TimeSpan>())).Returns(JobId);

        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData
            {
                State = OldStateName,
                Job = _job
            });

        _distributedLock = new Mock<IDisposable>();
        _connection
            .Setup(x => x.AcquireDistributedLock($"job:{JobId}:state-lock", It.IsAny<TimeSpan>()))
            .Returns(_distributedLock.Object);

        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        _context = new StateChangeContextMock
        {
            BackgroundJobId = JobId,
            Connection = _connection,
            CancellationToken = _cts.Token,
            NewState = _state,
            ExpectedStates = FromOldState,
            CustomData = new Dictionary<string, object>
            {
                { "Key", "Value" }
            }
        };

        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Returns(_context.NewState.Object);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenFilterProviderIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobStateChanger((IJobFilterProvider)null));

        Assert.Equal("filterProvider", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStateMachineNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobStateChanger(_filterProvider.Object, null));

        Assert.Equal("stateMachine", exception.ParamName);
    }

    [Fact]
    public void ChangeState_WorksWithinAJobLock()
    {
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        _distributedLock.Verify(x => x.Dispose());
    }

    [Fact]
    public void TryToChangeState_ChangesTheStateOfTheJob()
    {
        // Arrange
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(
            It.Is<ApplyStateContext>(sc => sc.BackgroundJob.Id == JobId && sc.BackgroundJob.Job.Type.Name.Equals("Console")
                && ReferenceEquals(sc.NewState, _state.Object) && sc.OldStateName == OldStateName)));

        Assert.NotNull(result);
        Assert.Equal(_state.Object.Name, result.Name);
    }

    [Fact]
    public void ChangeState_PassesCustomData_ToApplyStateContext()
    {
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        _stateMachine.Verify(x => x.ApplyState(It.Is<ApplyStateContext>(
            sc => sc.CustomData["Key"].Equals("Value"))));
    }

    [Fact]
    public void ChangeState_ChangesTheStateOfTheJob_WhenFromStatesIsNull()
    {
        // Arrange
        BackgroundJobStateChanger stateChanger = CreateStateChanger();
        _context.ExpectedStates = null;

        // Act
        stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(
            It.Is<ApplyStateContext>(ctx => ReferenceEquals(ctx.NewState, _state.Object) && ctx.OldStateName == OldStateName)));
    }

    [Fact]
    public void ChangeState_ReturnsNull_WhenJobIsNotFound()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(It.IsAny<string>()))
            .Returns(default(JobData));

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        Assert.Null(result);
        _connection.Verify(x => x.GetJobData(JobId));

        _stateMachine.Verify(
            x => x.ApplyState(It.IsAny<ApplyStateContext>()),
            Times.Never);
    }

    [Fact]
    public void ChangeState_DoesNotDoAnything_WhenStateIsNull_AndCancellationTokenIsCancelled()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(It.IsAny<string>())).Returns(new JobData
        {
            Job = _job,
            State = null
        });

        BackgroundJobStateChanger stateChanger = CreateStateChanger();
        _cts.Cancel();

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        Assert.Null(result);

        _stateMachine.Verify(
            x => x.ApplyState(It.IsAny<ApplyStateContext>()),
            Times.Never);
    }

    [Fact]
    public void ChangeState_WaitsFor_NonNullJobDataAndStateValue()
    {
        // Arrange
        Queue<JobData> results = new();
        results.Enqueue(null);
        results.Enqueue(new JobData { Job = _job, State = null });
        results.Enqueue(new JobData { Job = _job, State = OldStateName });

        _connection.Setup(x => x.GetJobData(It.IsAny<string>()))
            .Returns(results.Dequeue);

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        Assert.Empty(results);
        Assert.NotNull(result);
        Assert.Equal(_state.Object.Name, result.Name);
    }

    [Fact]
    public void ChangeState_ReturnsNull_WhenFromStatesArgumentDoesNotContainCurrentState()
    {
        // Arrange
        BackgroundJobStateChanger stateChanger = CreateStateChanger();
        _context.ExpectedStates = ["AnotherState"];

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        Assert.Null(result);

        _stateMachine.Verify(
            x => x.ApplyState(It.IsAny<ApplyStateContext>()),
            Times.Never);
    }

    [Fact]
    public void ChangeState_ThrowsAnException_WhenApplyStateThrowsException()
    {
        // Arrange
        _stateMachine.Setup(x => x.ApplyState(It.IsAny<ApplyStateContext>()))
            .Throws(new FieldAccessException());

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act & Assert
        Assert.Throws<FieldAccessException>(
            () => stateChanger.ChangeState(_context.Object));
    }

    [Fact]
    public void ChangeState_MoveJobToTheFailedState_IfMethodDataCouldNotBeResolved()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData
            {
                State = OldStateName,
                Job = null,
                LoadException = new JobLoadException("asd", new InvalidOperationException())
            });

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(
            It.Is<ApplyStateContext>(ctx => ctx.BackgroundJob.Id == JobId &&
            ctx.BackgroundJob.Job == null && ctx.NewState is FailedState)));
    }

    [Fact]
    public void ChangeState_FailedStateReason_SaysTheArgumentsCouldNotBeDeserialized_WhenTheyCouldNot()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData
            {
                State = OldStateName,
                Job = null,
                LoadException = new JobLoadException("asd", new InvalidOperationException("Bad argument.")) { Failure = JobLoadFailure.Arguments }
            });

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(It.Is<ApplyStateContext>(ctx =>
            (ctx.NewState as FailedState).Reason == "Can not change the state to 'State': the job's arguments could not be deserialized." &&
            (ctx.NewState as FailedState).Exception.Message == "Bad argument.")));
    }

    [Fact]
    public void ChangeState_FailedStateReason_SaysTheTypeOrMethodWasNotFound_WhenItWasNot()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData
            {
                State = OldStateName,
                Job = null,
                LoadException = new JobLoadException("asd", new InvalidOperationException("No method.")) { Failure = JobLoadFailure.TypeOrMethod }
            });

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(It.Is<ApplyStateContext>(ctx =>
            (ctx.NewState as FailedState).Reason == "Can not change the state to 'State': the job's type or method was not found.")));
    }

    [Fact]
    public void ChangeState_FailedStateReason_SaysTheJobCouldNotBeLoaded_WhenTheCauseIsUnknown()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData
            {
                State = OldStateName,
                Job = null,
                LoadException = new JobLoadException("asd", new InvalidOperationException("Storage error."))
            });

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(It.Is<ApplyStateContext>(ctx =>
            (ctx.NewState as FailedState).Reason == "Can not change the state to 'State': the job could not be loaded.")));
    }

    [Fact]
    public void ChangeState_MoveJobToTheGivenState_IfStateIgnoresThisException_AndMethodDataCouldNotBeResolved()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData
            {
                State = OldStateName,
                Job = null,
                LoadException = new JobLoadException("asd", new Exception())
            });

        _state.Setup(x => x.IgnoreJobLoadException).Returns(true);

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(
            It.Is<ApplyStateContext>(ctx => ReferenceEquals(ctx.NewState, _state.Object))));

        Assert.NotNull(result);
        Assert.Equal(_state.Object.Name, result.Name);
    }

    [Fact]
    public void ChangeState_CommitsTheNewState_AndReturnsAppliedState()
    {
        // Arrange
        BackgroundJobStateChanger stateChanger = CreateStateChanger();
        _context.ExpectedStates = [OldStateName];

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(
            It.Is<ApplyStateContext>(ctx => ReferenceEquals(ctx.NewState, _state.Object) && ctx.OldStateName == OldStateName
                && ctx.BackgroundJob.Job == _job && ctx.BackgroundJob.Id == JobId)));

        _transaction.Verify(x => x.Commit());

        Assert.NotNull(result);
        Assert.Equal(_state.Object.Name, result.Name);
    }

    [Fact]
    public void ChangeState_ReturnsState_ReturnedByAStateMachine()
    {
        // Arrange
        Mock<IState> anotherState = new();

        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Returns(anotherState.Object);

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        IState? result = stateChanger.ChangeState(_context.Object);

        // Assert
        Assert.Same(result, anotherState.Object);
    }

    [Fact]
    public void ChangeState_RethrowsFilterException_AndDoesNotCommitAnything_WhenNoCancellationToken()
    {
        // Arrange
        _stateMachine
            .Setup(x => x.ApplyState(It.Is<ApplyStateContext>(context => ReferenceEquals(context.NewState, _state.Object))))
            .Throws<Exception>();

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act & Assert
        Assert.Throws<Exception>(() => stateChanger.ChangeState(_context.Object));

        _transaction.Verify(x => x.Commit(), Times.Never);

        _stateMachine.Verify(
            x => x.ApplyState(It.Is<ApplyStateContext>(context => ReferenceEquals(context.NewState, _state.Object))),
            Times.Once);
    }

    [Fact]
    public void ChangeState_SimplyRethrowsAnException_WithoutRetriesAndFailedState()
    {
        // Arrange
        _stateMachine
            .Setup(x => x.ApplyState(It.Is<ApplyStateContext>(context => ReferenceEquals(context.NewState, _state.Object))))
            .Throws<Exception>();

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act & Assert
        Assert.Throws<Exception>(() => stateChanger.ChangeState(_context.Object));

        _connection.Verify(x => x.GetJobData(JobId), Times.Once);
        _connection.Verify(x => x.AcquireDistributedLock($"job:{JobId}:state-lock", It.IsAny<TimeSpan>()), Times.Once);

        _transaction.Verify(x => x.Commit(), Times.Never);

        _stateMachine.Verify(
            x => x.ApplyState(It.Is<ApplyStateContext>(context => ReferenceEquals(context.NewState, _state.Object))),
            Times.Once);
    }

    [Fact]
    public void ChangeState_DoesNotApplyAnything_AndRethrowsExceptions_ThrownByElectStateFilters()
    {
        // Arrange
        Mock<IElectStateFilter> electStateFilter = new();

        electStateFilter
            .Setup(x => x.OnStateElection(It.IsAny<ElectStateContext>()))
            .Throws<NotSupportedException>();

        _filterProvider
            .Setup(x => x.GetFilters(It.IsAny<Job>()))
            .Returns([new JobFilter(electStateFilter.Object, JobFilterScope.Method, 0)]);

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        Assert.Throws<NotSupportedException>(() => stateChanger.ChangeState(_context.Object));

        // Assert
        electStateFilter.Verify(x => x.OnStateElection(It.IsNotNull<ElectStateContext>()), Times.Once);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void ChangeState_DoesNotApplyAnything_AndRethrowsAndException_ThrownByApplyStateFilters()
    {
        // Arrange
        Mock<IApplyStateFilter> applyStateFilter = new();

        applyStateFilter
            .Setup(x => x.OnStateApplied(It.IsAny<ApplyStateContext>(), It.IsAny<IWriteOnlyTransaction>()))
            .Throws<NotSupportedException>();

        _filterProvider
            .Setup(x => x.GetFilters(It.IsAny<Job>()))
            .Returns([new JobFilter(applyStateFilter.Object, JobFilterScope.Method, 0)]);

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        Assert.Throws<NotSupportedException>(() => stateChanger.ChangeState(_context.Object));

        // Assert
        applyStateFilter.Verify(
            x => x.OnStateApplied(It.IsNotNull<ApplyStateContext>(), It.IsNotNull<IWriteOnlyTransaction>()),
            Times.Once);

        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void ChangeState_DoesNotInvokeElectStateFilters_WhenFiltersDisabled()
    {
        // Arrange
        Mock<IElectStateFilter> electStateFilter = new();

        electStateFilter
            .Setup(x => x.OnStateElection(It.IsAny<ElectStateContext>()))
            .Throws<NotSupportedException>();

        _filterProvider
            .Setup(x => x.GetFilters(It.IsAny<Job>()))
            .Returns([new JobFilter(electStateFilter.Object, JobFilterScope.Method, 0)]);

        _context.DisableFilters = true;

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        stateChanger.ChangeState(_context.Object);

        // Assert
        electStateFilter.Verify(
            x => x.OnStateElection(It.IsAny<ElectStateContext>()),
            Times.Never);

        _stateMachine.Verify(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void ChangeState_DoesNotInvokeApplyStateFilters_WhenFiltersDisabled()
    {
        // Arrange
        Mock<IApplyStateFilter> applyStateFilter = new();

        applyStateFilter
            .Setup(x => x.OnStateApplied(It.IsAny<ApplyStateContext>(), It.IsAny<IWriteOnlyTransaction>()))
            .Throws<NotSupportedException>();

        _filterProvider
            .Setup(x => x.GetFilters(It.IsAny<Job>()))
            .Returns([new JobFilter(applyStateFilter.Object, JobFilterScope.Method, 0)]);

        _context.DisableFilters = true;

        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        // Act
        stateChanger.ChangeState(_context.Object);

        // Assert
        applyStateFilter.Verify(
            x => x.OnStateApplied(It.IsAny<ApplyStateContext>(), It.IsAny<IWriteOnlyTransaction>()),
            Times.Never);

        _stateMachine.Verify(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void ChangeState_WithTransaction_PassesTheGivenTransaction_AndDoesNotCommitTheImplicitOne()
    {
        _context.Transaction = new Mock<JobStorageTransaction>();
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        _stateMachine.Verify(x => x.ApplyState(It.Is<ApplyStateContext>(
            ctx => ReferenceEquals(ctx.Transaction, _context.Transaction.Object))));

        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void ChangeState_WithTransaction_DoesNotCommitAndDoesNotDisposeTheExplicitTransaction()
    {
        _context.Transaction = new Mock<JobStorageTransaction>();
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        _context.Transaction.Verify(x => x.Commit(), Times.Never);
        _context.Transaction.Verify(x => x.Dispose(), Times.Never);
    }

    [Fact]
    public void ChangeState_WithTransaction_AcquiresATransactionLevelLockInstead()
    {
        _context.Transaction = new Mock<JobStorageTransaction>();
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        _context.Transaction.Verify(x => x.AcquireDistributedLock($"job:{JobId}:state-lock", It.IsAny<TimeSpan>()));
        _connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public void ChangeState_RunsPostCommitActions_AfterTheCommit()
    {
        List<string> calls = [];
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx => ctx.RunAfterCommit(() => calls.Add("action")))
            .Returns(_state.Object);
        _transaction.Setup(x => x.Commit()).Callback(() => calls.Add("commit"));
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        Assert.Equal(new[] { "commit", "action" }, calls);
    }

    [Fact]
    public void ChangeState_RunsPostCommitActions_AfterTheJobLockIsReleased()
    {
        List<string> calls = [];
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx => ctx.RunAfterCommit(() => calls.Add("action")))
            .Returns(_state.Object);
        _transaction.Setup(x => x.Dispose()).Callback(() => calls.Add("transaction disposed"));
        _distributedLock.Setup(x => x.Dispose()).Callback(() => calls.Add("lock released"));
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        Assert.Equal(new[] { "transaction disposed", "lock released", "action" }, calls);
    }

    [Fact]
    public void ChangeState_DropsPostCommitActions_WhenTheCommitFails()
    {
        List<string> calls = [];
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx => ctx.RunAfterCommit(() => calls.Add("action")))
            .Returns(_state.Object);
        _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("commit failed"));
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        Assert.Throws<TimeoutException>(() => stateChanger.ChangeState(_context.Object));

        Assert.Empty(calls);
    }

    [Fact]
    public void ChangeState_RunsOnlyTheActionsQueuedRegardless_WhenTheCommitFails_AfterTheJobLockIsReleased()
    {
        List<string> calls = [];
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx =>
            {
                ctx.RunAfterCommit(() => calls.Add("action"));
                ctx.AfterCommit.AddRegardless(() => calls.Add("regardless"));
            })
            .Returns(_state.Object);
        _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("commit failed"));
        _distributedLock.Setup(x => x.Dispose()).Callback(() => calls.Add("lock released"));
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        Assert.Throws<TimeoutException>(() => stateChanger.ChangeState(_context.Object));

        Assert.Equal(new[] { "lock released", "regardless" }, calls);
    }

    [Fact]
    public void ChangeState_RunsTheActionsQueuedRegardless_AfterASuccessfulCommit()
    {
        List<string> calls = [];
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx => ctx.AfterCommit.AddRegardless(() => calls.Add("regardless")))
            .Returns(_state.Object);
        _transaction.Setup(x => x.Commit()).Callback(() => calls.Add("commit"));
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        Assert.Equal(new[] { "commit", "regardless" }, calls);
    }

    [Fact]
    public void ChangeState_RethrowsTheOriginalException_WhenAnActionQueuedRegardlessThrows()
    {
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx => ctx.AfterCommit.AddRegardless(() => throw new InvalidOperationException("sink failed")))
            .Returns(_state.Object);
        _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("commit failed"));
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        TimeoutException exception = Assert.Throws<TimeoutException>(() => stateChanger.ChangeState(_context.Object));

        Assert.Equal("commit failed", exception.Message);
    }

    [Fact]
    public void ChangeState_ReturnsTheState_WhenAPostCommitActionThrows()
    {
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx => ctx.RunAfterCommit(() => throw new InvalidOperationException("sink failed")))
            .Returns(_state.Object);
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        IState? result = stateChanger.ChangeState(_context.Object);

        Assert.Same(_state.Object, result);
        _transaction.Verify(x => x.Commit(), Times.Once);
    }

    [Fact]
    public void ChangeState_WithTransaction_RunsActionsRightAway_BecauseItDoesNotCommit()
    {
        _context.Transaction = new Mock<JobStorageTransaction>();
        bool ran = false;
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx =>
            {
                Assert.Null(ctx.AfterCommit);
                ctx.RunAfterCommit(() => ran = true);
            })
            .Returns(_state.Object);
        BackgroundJobStateChanger stateChanger = CreateStateChanger();

        stateChanger.ChangeState(_context.Object);

        Assert.True(ran);
    }

    [Fact]
    public void ChangeState_RefusesTheChange_WhenAnotherWorkerHoldsTheLease()
    {
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData
        {
            State = OldStateName,
            Job = _job,
            ParametersSnapshot = new Dictionary<string, string> { [StaleJobFilter.LeaseParameter] = "\"worker-b\"" },
        });
        _context.LeaseHolder = "worker-a";

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.Null(result);
        _stateMachine.Verify(x => x.ApplyState(It.IsAny<ApplyStateContext>()), Times.Never);
    }

    [Theory]
    [InlineData("\"worker-a\"")]
    [InlineData(null)]
    public void ChangeState_AppliesTheChange_WhenTheWorkerHoldsTheLease_OrThereIsNone(string lease)
    {
        Dictionary<string, string> snapshot = [];
        if (lease != null)
        {
            snapshot[StaleJobFilter.LeaseParameter] = lease;
        }

        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { State = OldStateName, Job = _job, ParametersSnapshot = snapshot });
        _context.LeaseHolder = "worker-a";

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.NotNull(result);
    }

    [Fact]
    public void ChangeState_IgnoresTheLease_WhenTheStorageLoadsNoParametersWithTheJob()
    {
        // The lease is checked from the loaded parameters only, never with an extra read
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { State = OldStateName, Job = _job, ParametersSnapshot = null });
        _context.LeaseHolder = "worker-a";

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.NotNull(result);
        _connection.Verify(x => x.GetJobParameter(JobId, StaleJobFilter.LeaseParameter), Times.Never);
    }

    [Fact]
    public void ChangeState_AppliesTheChange_WhenTheLeaseCannotBeRead()
    {
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData
        {
            State = OldStateName,
            Job = _job,
            ParametersSnapshot = new Dictionary<string, string> { [StaleJobFilter.LeaseParameter] = "{broken" },
        });
        _context.LeaseHolder = "worker-a";

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.NotNull(result);
    }

    [Fact]
    public void ChangeState_RefusesTheChange_WhenTheJobIsProcessingUnderAnotherQueueEntry()
    {
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { State = ProcessingState.StateName, Job = _job });
        _connection.Setup(x => x.GetStateData(JobId)).Returns(ProcessingUnder("12"));
        _context.ExpectedStates = [EnqueuedState.StateName, ProcessingState.StateName];
        _context.QueueEntry = "13";

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.Null(result);
        _stateMachine.Verify(x => x.ApplyState(It.IsAny<ApplyStateContext>()), Times.Never);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Theory]
    [InlineData("12")]
    [InlineData(null)]
    [InlineData("")]
    public void ChangeState_AppliesTheChange_WhenTheJobIsProcessingUnderTheSameQueueEntry_OrNoneIsKnown(string running)
    {
        // The worker put the entry back, or the storage handed it out again after the invisibility timeout: a
        // restart. A Processing state that names no entry was written before workers recorded it.
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { State = ProcessingState.StateName, Job = _job });
        _connection.Setup(x => x.GetStateData(JobId)).Returns(ProcessingUnder(running));
        _context.ExpectedStates = [EnqueuedState.StateName, ProcessingState.StateName];
        _context.QueueEntry = "12";

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(false, "12")]
    [InlineData(true, null)]
    public void ChangeState_DoesNotReadTheState_WhenTheJobIsNotProcessing_OrTheWorkerKnowsNoEntry(bool processing, string entry)
    {
        if (processing)
        {
            _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { State = ProcessingState.StateName, Job = _job });
            _context.ExpectedStates = [ProcessingState.StateName];
        }

        _context.QueueEntry = entry;

        Assert.NotNull(CreateStateChanger().ChangeState(_context.Object));
        _connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
    }

    private static StateData ProcessingUnder(string queueEntry)
    {
        Dictionary<string, string> data = new() { ["ServerId"] = "server", ["WorkerId"] = "worker" };
        if (queueEntry != null)
        {
            data[ProcessingState.QueueEntryKey] = queueEntry;
        }

        return new StateData { Name = ProcessingState.StateName, Data = data };
    }

    [Fact]
    public void ChangeState_StoresNothing_AndDoesNotAcknowledgeTheJob_WhenAFilterAsksTheWorkerToLookAgain()
    {
        JobLookAgain lookAgain = new();
        Mock<IFetchedJob> fetchedJob = new();
        Mock<JobStorageTransaction> transaction = new();
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
        _context.LookAgain = lookAgain;
        _context.CompleteJob = fetchedJob;

        bool afterCommit = false;
        bool afterAttempt = false;
        bool lockHeld = true;
        _distributedLock.Setup(x => x.Dispose()).Callback(() => lockHeld = false);

        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Returns<ApplyStateContext>(context =>
            {
                Assert.Same(lookAgain, context.LookAgain);
                context.RunAfterCommit(() => afterCommit = true);
                PostCommitActions.RunAfterCommitAttempt(context.AfterCommit, () => afterAttempt = !lockHeld);
                context.LookAgain.Request(TimeSpan.FromMilliseconds(30));
                return context.NewState;
            });

        IState result = CreateStateChanger().ChangeState(_context.Object);

        // No state for the caller, nothing stored, the fetched job not acknowledged, and the job's lock released
        Assert.Null(result);
        Assert.True(lookAgain.Requested);
        Assert.Equal(TimeSpan.FromMilliseconds(30), lookAgain.Pause);
        Assert.Null(_context.Object.ProcessedJob);
        transaction.Verify(x => x.Commit(), Times.Never);
        transaction.Verify(x => x.RemoveFromQueue(It.IsAny<IFetchedJob>()), Times.Never);
        _distributedLock.Verify(x => x.Dispose(), Times.Once);

        // What was to follow the commit doesn't happen; what is about something stored already does, after the lock
        Assert.False(afterCommit);
        Assert.True(afterAttempt);
    }

    [Fact]
    public void ChangeState_ForgetsWhatAnEarlierAttemptAsked_WhenTheStateChangeIsTriedAgain()
    {
        JobLookAgain lookAgain = new();
        lookAgain.Request(TimeSpan.FromMilliseconds(30));
        _context.LookAgain = lookAgain;

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.NotNull(result);
        Assert.False(lookAgain.Requested);
        _transaction.Verify(x => x.Commit(), Times.Once);
    }

    [Fact]
    public void ChangeState_DoesNotOfferToLookAgain_WhenTheTransactionIsTheCallers()
    {
        // Nothing could be dropped: what the filters wrote would be committed by the caller
        _context.LookAgain = new JobLookAgain();
        _context.Transaction = new Mock<JobStorageTransaction>();
        JobLookAgain offered = new();
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Returns<ApplyStateContext>(context =>
            {
                offered = context.LookAgain;
                return context.NewState;
            });

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.Null(offered);
        Assert.NotNull(result);
    }

    [Fact]
    public void ChangeState_DoesNotOfferToLookAgain_WhenTheFiltersAreDisabled()
    {
        _context.LookAgain = new JobLookAgain();
        _context.DisableFilters = true;
        JobLookAgain offered = new();
        _stateMachine.Setup(x => x.ApplyState(It.IsNotNull<ApplyStateContext>()))
            .Returns<ApplyStateContext>(context =>
            {
                offered = context.LookAgain;
                return context.NewState;
            });

        IState result = CreateStateChanger().ChangeState(_context.Object);

        Assert.Null(offered);
        Assert.NotNull(result);
    }

    private BackgroundJobStateChanger CreateStateChanger()
    {
        return new BackgroundJobStateChanger(_filterProvider.Object, _stateMachine.Object);
    }
}
