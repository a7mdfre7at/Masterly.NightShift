using Moq.Sequences;

namespace Masterly.NightShift.Core.Tests.States;

public class CoreStateMachineFacts
{
    private const string OldStateName = "OldState";
    private const string StateName = "State";
    private const string JobId = "job";

    private readonly Dictionary<string, List<IStateHandler>> _handlers = [];
    private readonly Func<JobStorage, string, CoreStateMachine.StateHandlersCollection> _stateHandlersThunk;

    private readonly ApplyStateContextMock _applyContext;

    public CoreStateMachineFacts()
    {
        _stateHandlersThunk = (storage, stateName) => new CoreStateMachine.StateHandlersCollection(
            _handlers.TryGetValue(stateName, out List<IStateHandler>? handlers) ? [.. handlers] : [],
            [],
            stateName);

        BackgroundJobMock backgroundJob = new() { Id = JobId };
        _applyContext = new ApplyStateContextMock
        {
            BackgroundJob = backgroundJob,
            OldStateName = OldStateName
        };

        _applyContext.NewState.Setup(x => x.Name).Returns(StateName);
    }

    [Fact]
    public void ApplyState_SetsTheNewState_ForABackgroundJob()
    {
        CoreStateMachine stateMachine = CreateStateMachine();

        stateMachine.ApplyState(_applyContext.Object);

        _applyContext.Transaction.Verify(x => x.SetJobState(JobId, _applyContext.NewState.Object));
    }

    [Fact]
    public void ApplyState_SetsJobExpiration_IfStateIsFinal()
    {
        _applyContext.NewState.Setup(x => x.IsFinal).Returns(true);
        CoreStateMachine stateMachine = CreateStateMachine();

        stateMachine.ApplyState(_applyContext.Object);

        _applyContext.Transaction.Verify(x => x.ExpireJob(JobId, _applyContext.JobExpirationTimeout));
    }

    [Fact]
    public void ApplyState_PersistTheJob_IfStateIsNotFinal()
    {
        _applyContext.NewState.Setup(x => x.IsFinal).Returns(false);
        CoreStateMachine stateMachine = CreateStateMachine();

        stateMachine.ApplyState(_applyContext.Object);

        _applyContext.Transaction.Verify(x => x.PersistJob(JobId));
    }

    [Fact]
    public void ApplyState_ExpiresTheJob_InsteadOfPersistingIt_WhenAFilterAskedToExpireANonFinalState()
    {
        _applyContext.NewState.Setup(x => x.IsFinal).Returns(false);
        _applyContext.Object.JobExpirationTimeout = TimeSpan.FromDays(30);
        _applyContext.Object.ExpireInNonFinalState = true;
        CoreStateMachine stateMachine = CreateStateMachine();

        stateMachine.ApplyState(_applyContext.Object);

        _applyContext.Transaction.Verify(x => x.ExpireJob(JobId, TimeSpan.FromDays(30)));
        _applyContext.Transaction.Verify(x => x.PersistJob(It.IsAny<string>()), Times.Never);
    }

    [Fact, Sequence]
    public void ApplyState_CallsUnapplyHandlers_BeforeSettingTheState()
    {
        // Arrange
        Mock<IStateHandler> handler1 = CreateStateHandler(OldStateName);
        Mock<IStateHandler> handler2 = CreateStateHandler(OldStateName);

        handler1
            .Setup(x => x.Unapply(_applyContext.Object, _applyContext.Transaction.Object))
            .InSequence();

        handler2
            .Setup(x => x.Unapply(_applyContext.Object, _applyContext.Transaction.Object))
            .InSequence();

        CoreStateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_applyContext.Object);

        // Assert - Sequence
    }

    [Fact]
    public void ApplyState_DoesNotCallUnapplyHandlers_ForDifferentStates()
    {
        // Arrange
        Mock<IStateHandler> handler = CreateStateHandler(StateName);
        CoreStateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_applyContext.Object);

        // Assert
        handler.Verify(
            x => x.Unapply(It.IsAny<ApplyStateContext>(), It.IsAny<IWriteOnlyTransaction>()),
            Times.Never);
    }

    [Fact, Sequence]
    public void ApplyState_ShouldCallApplyHandlers_AfterSettingTheState()
    {
        // Arrange
        Mock<IStateHandler> handler1 = CreateStateHandler(StateName);
        Mock<IStateHandler> handler2 = CreateStateHandler(StateName);

        _applyContext.Transaction
            .Setup(x => x.SetJobState(It.IsAny<string>(), It.IsAny<IState>()))
            .InSequence();

        handler1.Setup(x => x.Apply(_applyContext.Object, _applyContext.Transaction.Object))
            .InSequence();
        handler2.Setup(x => x.Apply(_applyContext.Object, _applyContext.Transaction.Object))
            .InSequence();

        CoreStateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_applyContext.Object);

        // Assert - Sequence
    }

    [Fact]
    public void ApplyState_DoesNotCallApplyHandlers_ForDifferentStates()
    {
        // Arrange
        Mock<IStateHandler> handler = CreateStateHandler(OldStateName);
        CoreStateMachine stateMachine = CreateStateMachine();

        // Act
        stateMachine.ApplyState(_applyContext.Object);

        // Assert
        handler.Verify(
            x => x.Apply(It.IsAny<ApplyStateContext>(), It.IsAny<IWriteOnlyTransaction>()),
            Times.Never);
    }

    private CoreStateMachine CreateStateMachine()
    {
        return new CoreStateMachine(_stateHandlersThunk);
    }

    private Mock<IStateHandler> CreateStateHandler(string stateName)
    {
        Mock<IStateHandler> handler = new();
        handler.Setup(x => x.StateName).Returns(stateName);

        if (!_handlers.ContainsKey(stateName))
        {
            _handlers.Add(stateName, []);
        }

        _handlers[stateName].Add(handler.Object);
        return handler;
    }
}
