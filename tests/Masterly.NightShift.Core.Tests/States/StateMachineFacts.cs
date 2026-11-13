using Moq.Sequences;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.States;

public class StateMachineFacts
{
    private const string OldStateName = "OldState";
    private const string JobId = "job";

    private readonly List<object> _filters = [];

    private readonly Mock<IWriteOnlyTransaction> _transaction;
    private readonly ApplyStateContextMock _context;
    private readonly Mock<IJobFilterProvider> _filterProvider;

    private readonly Mock<IStateMachine> _innerMachine;

    public StateMachineFacts()
    {
        Mock<IStorageConnection> connection = new();
        _transaction = new Mock<IWriteOnlyTransaction>();
        connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);

        BackgroundJobMock backgroundJob = new() { Id = JobId };
        _context = new ApplyStateContextMock
        {
            BackgroundJob = backgroundJob,
            OldStateName = OldStateName,
            Transaction = _transaction
        };

        _filterProvider = new Mock<IJobFilterProvider>();
        _filterProvider.Setup(x => x.GetFilters(It.IsNotNull<Job>())).Returns(
            _filters.Select(f => new JobFilter(f, JobFilterScope.Type, null)));

        _innerMachine = new Mock<IStateMachine>();
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenFilterProviderIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new StateMachine(null, _innerMachine.Object));

        Assert.Equal("filterProvider", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenInnerStateMachineIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new StateMachine(_filterProvider.Object, null));

        Assert.Equal("innerStateMachine", exception.ParamName);
    }

    [Fact]
    public void ApplyState_CallsElectionFilterWithCorrectProperties()
    {
        // Arrange
        Mock<IElectStateFilter> filter = CreateFilter<IElectStateFilter>();

        StateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_context.Object);

        filter.Verify(x => x.OnStateElection(It.Is<ElectStateContext>(context =>
            context.Storage == _context.Storage.Object &&
            context.Connection == _context.Connection.Object &&
            context.BackgroundJob == _context.BackgroundJob.Object &&
            context.CandidateState == _context.NewState.Object &&
            context.CurrentState == _context.OldStateName)));
    }

    [Fact, SequenceAttribute]
    public void ApplyState_CallsElectionFilters()
    {
        // Arrange
        Mock<IElectStateFilter> filter1 = CreateFilter<IElectStateFilter>();
        Mock<IElectStateFilter> filter2 = CreateFilter<IElectStateFilter>();

        filter1.Setup(x => x.OnStateElection(It.IsAny<ElectStateContext>()))
            .InSequence();
        filter2.Setup(x => x.OnStateElection(It.IsAny<ElectStateContext>()))
            .InSequence();

        StateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_context.Object);

        // Assert - Sequence
    }

    [Fact]
    public void ApplyState_AddsJobHistory_ForTraversedStates()
    {
        // Arrange
        Mock<IState> anotherState = new();
        Mock<IElectStateFilter> filter = CreateFilter<IElectStateFilter>();
        filter.Setup(x => x.OnStateElection(It.IsNotNull<ElectStateContext>()))
            .Callback<ElectStateContext>(context => context.CandidateState = anotherState.Object);

        StateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_context.Object);

        // Assert
        _context.Transaction.Verify(x => x.AddJobState(JobId, _context.NewState.Object));
    }

    [Fact, Sequence]
    public void ApplyState_CallsStateUnappliedFilters_BeforeCallingInnerStateMachine()
    {
        // Arrange
        Mock<IApplyStateFilter> filter1 = CreateFilter<IApplyStateFilter>();
        Mock<IApplyStateFilter> filter2 = CreateFilter<IApplyStateFilter>();

        filter1.Setup(x => x.OnStateUnapplied(It.IsNotNull<ApplyStateContext>(), _transaction.Object))
            .InSequence();
        filter2.Setup(x => x.OnStateUnapplied(It.IsNotNull<ApplyStateContext>(), _transaction.Object))
            .InSequence();
        _innerMachine
            .Setup(x => x.ApplyState(It.IsAny<ApplyStateContext>()))
            .InSequence();

        StateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_context.Object);

        // Assert - Sequence
    }

    [Fact, Sequence]
    public void ApplyState_CallsStateAppliedFilters_AfterSettingTheState()
    {
        // Arrange
        Mock<IApplyStateFilter> filter1 = CreateFilter<IApplyStateFilter>();
        Mock<IApplyStateFilter> filter2 = CreateFilter<IApplyStateFilter>();

        filter1.Setup(x => x.OnStateApplied(It.IsNotNull<ApplyStateContext>(), _transaction.Object))
            .InSequence();
        filter2.Setup(x => x.OnStateApplied(It.IsNotNull<ApplyStateContext>(), _transaction.Object))
            .InSequence();

        StateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_context.Object);

        // Assert - Sequence
    }

    [Fact]
    public void ApplyState_StopsAtTheFilterThatAsksTheWorkerToLookAgain_AndWritesNothing()
    {
        // What a worker offers for the job it fetched
        _context.LookAgain = new JobLookAgain();

        Mock<IElectStateFilter> first = CreateFilter<IElectStateFilter>();
        first.Setup(x => x.OnStateElection(It.IsNotNull<ElectStateContext>()))
            .Callback<ElectStateContext>(static context =>
            {
                Assert.True(context.CanLookAgain);
                Assert.True(context.TryLookAgain(TimeSpan.FromMilliseconds(20)));
            });
        Mock<IElectStateFilter> second = CreateFilter<IElectStateFilter>();
        Mock<IElectStateFilter> third = CreateFilter<IElectStateFilter>();
        Mock<IApplyStateFilter> apply = CreateFilter<IApplyStateFilter>();

        CreateStateMachine().ApplyState(_context.Object);

        // The filters after it don't run, and neither the states traversed nor a new state are written
        first.Verify(x => x.OnStateElection(It.IsAny<ElectStateContext>()), Times.Once);
        second.Verify(x => x.OnStateElection(It.IsAny<ElectStateContext>()), Times.Never);
        third.Verify(x => x.OnStateElection(It.IsAny<ElectStateContext>()), Times.Never);
        apply.Verify(x => x.OnStateUnapplied(It.IsAny<ApplyStateContext>(), It.IsAny<IWriteOnlyTransaction>()), Times.Never);
        apply.Verify(x => x.OnStateApplied(It.IsAny<ApplyStateContext>(), It.IsAny<IWriteOnlyTransaction>()), Times.Never);
        _innerMachine.Verify(x => x.ApplyState(It.IsAny<ApplyStateContext>()), Times.Never);
        Assert.Empty(_transaction.Invocations);
        Assert.True(_context.LookAgain.Requested);
        Assert.Equal(TimeSpan.FromMilliseconds(20), _context.LookAgain.Pause);
    }

    [Fact]
    public void ApplyState_OffersToLookAgain_ToTheFirstElectionFilterOnly()
    {
        // Once a filter has seen the job about to change state, the worker doesn't wait any more: nothing tells
        // that filter that the state change didn't happen
        _context.LookAgain = new JobLookAgain();
        bool? offered = null;
        bool? given = null;

        Mock<IElectStateFilter> first = CreateFilter<IElectStateFilter>();
        Mock<IElectStateFilter> second = CreateFilter<IElectStateFilter>();
        second.Setup(x => x.OnStateElection(It.IsNotNull<ElectStateContext>()))
            .Callback<ElectStateContext>(context =>
            {
                offered = context.CanLookAgain;
                given = context.TryLookAgain(TimeSpan.FromMilliseconds(20));
            });

        CreateStateMachine().ApplyState(_context.Object);

        Assert.False(offered);
        Assert.False(given);
        Assert.False(_context.LookAgain.Requested);
        _innerMachine.Verify(x => x.ApplyState(It.IsAny<ApplyStateContext>()), Times.Once);
    }

    [Fact]
    public void ApplyState_TellsAFilterTheWorkerWillNotLookAgain_WhenNoWorkerOfferedIt()
    {
        bool? given = null;
        bool? offered = null;
        Mock<IElectStateFilter> filter = CreateFilter<IElectStateFilter>();
        filter.Setup(x => x.OnStateElection(It.IsNotNull<ElectStateContext>()))
            .Callback<ElectStateContext>(context =>
            {
                offered = context.CanLookAgain;
                given = context.TryLookAgain(TimeSpan.FromMilliseconds(20));
            });

        CreateStateMachine().ApplyState(_context.Object);

        // The state change goes on as if nothing had been asked
        Assert.False(offered);
        Assert.False(given);
        _innerMachine.Verify(x => x.ApplyState(It.IsAny<ApplyStateContext>()), Times.Once);
    }

    private StateMachine CreateStateMachine()
    {
        return new StateMachine(_filterProvider.Object, _innerMachine.Object);
    }

    private Mock<T> CreateFilter<T>() where T : class
    {
        Mock<T> filter = new();
        _filters.Add(filter.Object);

        return filter;
    }
}