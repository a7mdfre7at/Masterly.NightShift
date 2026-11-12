using System.Globalization;
using Masterly.NightShift.Server;
using static Masterly.NightShift.ContinuationsSupportAttribute;

namespace Masterly.NightShift.Core.Tests;

public class ContinuationsSupportAttributeFacts
{
    private const string _parentId = "parent-id";
    private const string _continuationId = "continuation-id";
    private readonly ElectStateContextMock _context;
    private readonly Mock<IState> _nextState;
    private readonly Mock<IBackgroundJobStateChanger> _stateChanger;

    public ContinuationsSupportAttributeFacts()
    {
        _context = new ElectStateContextMock();
        _nextState = new Mock<IState>();
        _nextState.SetupGet(x => x.Name).Returns("SomeState");
        _stateChanger = new Mock<IBackgroundJobStateChanger>();

        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_parentId)).Returns(new StateData());
        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_parentId)).Returns(new JobData());

        _context.ApplyContext.NewStateObject = new AwaitingState(_parentId, _nextState.Object);
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _continuationId };
    }

    [Fact]
    public void OnStateElection_AddsAContinuationForParentJob_IfCandidateStateIsAwaiting()
    {
        // Arrange
        ContinuationsSupportAttribute filter = CreateFilter();

        // Act
        filter.OnStateElection(_context.Object);

        // Assert
        _context.ApplyContext.Connection.Verify(x => x.AcquireDistributedLock("job:parent-id:state-lock", It.IsAny<TimeSpan>()));
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(
            _parentId,
            "Continuations",
            It.Is<string>(value => SerializationHelper.Deserialize<List<Continuation>>(value)
                .Contains(new Continuation { JobId = _continuationId, Options = JobContinuationOptions.OnAnyFinishedState }))));

        Assert.IsType<AwaitingState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ChangesCandidateToTheNextState_OnAwaitingCompletedParentJob()
    {
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_parentId)).Returns(new StateData { Name = SucceededState.StateName });
        ContinuationsSupportAttribute filter = CreateFilter();

        filter.OnStateElection(_context.Object);

        Assert.Equal(_nextState.Object.Name, _context.Object.CandidateState.Name);
    }

    [Fact]
    public void OnStateElection_AddsAnotherContinuationForParentJob_OnAwaitingState()
    {
        // Arrange
        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = "another-id" } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act
        filter.OnStateElection(_context.Object);

        // Assert
        string expectedList = SerializationHelper.Serialize(new List<Continuation>
        {
            new() { JobId = "another-id", Options = JobContinuationOptions.OnAnyFinishedState },
            new() { JobId = _continuationId, Options = JobContinuationOptions.OnAnyFinishedState }
        });

        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(_parentId, "Continuations", expectedList));
    }

    [Fact]
    public void OnStateElection_ThrowsAnException_WhenParentJobDoesNotExist()
    {
        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_parentId)).Returns((JobData)null);

        ContinuationsSupportAttribute filter = CreateFilter();

        Assert.Throws<InvalidOperationException>(() => filter.OnStateElection(_context.Object));
    }

    [Fact]
    public void OnStateElection_ExecuteContinuations_IfExist()
    {
        // Arrange
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_continuationId)).Returns(new JobData());
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_continuationId)).Returns(new StateData
        {
            Name = AwaitingState.StateName,
            Data = new Dictionary<string, string>
            {
                { "NextState", SerializationHelper.Serialize<IState>(new EnqueuedState(), SerializationOption.TypedInternal) }
            }
        });


        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = _continuationId } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act
        filter.OnStateElection(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(
            ctx =>
                ctx.BackgroundJobId == _continuationId &&
                ctx.ExpectedStates.SingleOrDefault(s => s == AwaitingState.StateName) != null &&
                ctx.NewState.Name == "Enqueued")));
    }

    [Fact]
    public void OnStateElection_ExecuteContinuations_InFailedState_OnNextStateDeserializationError()
    {
        // Arrange
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_continuationId)).Returns(new JobData());
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_continuationId)).Returns(new StateData
        {
            Name = AwaitingState.StateName,
            Data = new Dictionary<string, string> { { "NextState", "hello" } }
        });

        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = _continuationId } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act
        filter.OnStateElection(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.BackgroundJobId == _continuationId &&
            ctx.ExpectedStates.SingleOrDefault(s => s == AwaitingState.StateName) != null &&
            ctx.NewState.Name == FailedState.StateName)));
    }

    [Fact]
    public void OnStateElection_ExecuteContinuations_InFailedState_WhenNextStateDoesNotExist()
    {
        // Arrange
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_continuationId)).Returns(new JobData());
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_continuationId)).Returns(new StateData
        {
            Name = AwaitingState.StateName
        });

        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = _continuationId } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act
        filter.OnStateElection(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.BackgroundJobId == _continuationId &&
            ctx.ExpectedStates.SingleOrDefault(s => s == AwaitingState.StateName) != null &&
            ctx.NewState.Name == FailedState.StateName)));
    }

    [Fact]
    public void OnStateElection_SkipsContinuations_WhenTheirCurrentState_IsNotAwaiting()
    {
        // Arrange
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_continuationId)).Returns(new JobData());
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_continuationId)).Returns(new StateData());

        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = _continuationId } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act
        filter.OnStateElection(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        Assert.Equal("Succeeded", _context.Object.CandidateState.Name);
    }

    [Fact]
    public void OnStateElection_SkipsExpiredContinuations()
    {
        // Arrange
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_continuationId)).Returns((JobData)null);
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_continuationId)).Returns((StateData)null);

        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = _continuationId } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act & Assert
        filter.OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        Assert.Equal("Succeeded", _context.Object.CandidateState.Name);
    }

    [Fact(Timeout = 20 * 1000)]
    public async Task OnStateElection_DoesNotStuckForever_WhenContinuationHasNoCorrespondingStateEntry()
    {
        // Arrange
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_continuationId)).Returns(new JobData());
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_continuationId)).Returns((StateData)null);

        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = _continuationId } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act: awaiting with the test's token makes the timeout end the test even if the call hangs
        await Task.Run(() => filter.OnStateElection(_context.Object), TestContext.Current.CancellationToken)
            .WaitAsync(TestContext.Current.CancellationToken);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        Assert.Equal("Succeeded", _context.Object.CandidateState.Name);
    }

    [Fact]
    public void OnStateElection_SkipsContinuations_WithNullIds()
    {
        // Arrange
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _context.ApplyContext.Connection.Setup(x => x.GetStateData(null)).Throws<ArgumentNullException>();

        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = null } }));

        ContinuationsSupportAttribute filter = CreateFilter();

        // Act
        filter.OnStateElection(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        Assert.Equal("Succeeded", _context.Object.CandidateState.Name);
    }

    [Fact]
    public void OnStateApplied_RecordsWhenAContinuationBecameDue_WhenItLeavesTheAwaitingState()
    {
        ApplyStateContextMock context = new()
        {
            OldStateName = AwaitingState.StateName,
            NewStateObject = new EnqueuedState(),
        };
        long before = JobHelper.ToMillisecondTimestamp(DateTime.UtcNow);

        CreateFilter().OnStateApplied(context.Object, context.Transaction.Object);

        context.Connection.Verify(x => x.SetJobParameter(
            "JobId",
            PerformContext.DueAtParameter,
            It.Is<string>(value => long.Parse(value, CultureInfo.InvariantCulture) >= before)));
    }

    [Fact]
    public void OnStateApplied_RecordsTheScheduledTime_OfAContinuationThatLeavesAwaitingForTheScheduledState()
    {
        DateTime enqueueAt = DateTime.UtcNow.AddHours(1);
        ApplyStateContextMock context = new()
        {
            OldStateName = AwaitingState.StateName,
            NewStateObject = new ScheduledState(enqueueAt),
        };

        CreateFilter().OnStateApplied(context.Object, context.Transaction.Object);

        context.Connection.Verify(x => x.SetJobParameter(
            "JobId",
            PerformContext.DueAtParameter,
            JobHelper.ToMillisecondTimestamp(enqueueAt).ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void OnStateApplied_RecordsWhenAContinuationBecameDue_InTheTransaction_WhenTheStorageCan()
    {
        Mock<JobStorageTransaction> transaction = new();
        ApplyStateContextMock context = new()
        {
            OldStateName = AwaitingState.StateName,
            NewStateObject = new EnqueuedState(),
        };
        context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(true);

        CreateFilter().OnStateApplied(context.Object, transaction.Object);

        transaction.Verify(x => x.SetJobParameter("JobId", PerformContext.DueAtParameter, It.IsNotNull<string>()));
        context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_DoesNotFailTheStateChange_WhenTheDueTimeCannotBeRecorded()
    {
        ApplyStateContextMock context = new()
        {
            OldStateName = AwaitingState.StateName,
            NewStateObject = new EnqueuedState(),
        };
        context.Connection
            .Setup(x => x.SetJobParameter("JobId", PerformContext.DueAtParameter, It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage is down"));

        CreateFilter().OnStateApplied(context.Object, context.Transaction.Object);
    }

    [Theory]
    [InlineData("Enqueued", "Processing")]
    [InlineData(null, "Enqueued")]
    [InlineData("Awaiting", "Deleted")]
    public void OnStateApplied_RecordsNothing_ForOtherStateChanges(string oldState, string newState)
    {
        ApplyStateContextMock context = new()
        {
            OldStateName = oldState,
            NewStateObject = newState switch
            {
                "Processing" => new ProcessingState("server", "worker"),
                "Deleted" => new DeletedState(),
                _ => new EnqueuedState(),
            },
        };

        CreateFilter().OnStateApplied(context.Object, context.Transaction.Object);

        context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("Processing")]
    [InlineData("Failed")]
    [InlineData("Succeeded")]
    public void OnStateElection_ReadsNothingElse_WhenTheLoadedParametersHaveNoContinuations(string candidate)
    {
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId, ParametersSnapshot = new Dictionary<string, string>() };
        _context.ApplyContext.NewStateObject = candidate switch
        {
            "Processing" => new ProcessingState("server", "worker"),
            "Failed" => new FailedState(new InvalidOperationException()),
            _ => new SucceededState(null, 0, 0),
        };

        CreateFilter().OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetJobData(It.IsAny<string>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateUnapplied_DoesNotThrow()
    {
        IApplyStateFilter filter = (IApplyStateFilter)CreateFilter();
        // Does not throw
        filter.OnStateUnapplied(null, null);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void HandlesChangingProcessOfInternalDataSerialization()
    {
        SerializationHelper.SetUserSerializerSettings(SerializerSettingsHelper.DangerousSettings);

        string continuationsJson = SerializationHelper.Serialize(new List<Continuation>
        {
            new() {JobId = "1", Options = JobContinuationOptions.OnAnyFinishedState},
            new() {JobId = "3214324", Options = JobContinuationOptions.OnlyOnSucceededState}
        }, SerializationOption.User);

        List<Continuation>? continuations = SerializationHelper.Deserialize<List<Continuation>>(continuationsJson);

        Assert.NotNull(continuations);
        Assert.Equal(2, continuations.Count);
        Assert.Equal("1", continuations[0].JobId);
        Assert.Equal(JobContinuationOptions.OnAnyFinishedState, continuations[0].Options);
        Assert.Equal("3214324", continuations[1].JobId);
        Assert.Equal(JobContinuationOptions.OnlyOnSucceededState, continuations[1].Options);
    }

    [Theory]
    [InlineData("Failed", JobContinuationOptions.OnlyOnFailedState, true)]
    [InlineData("Failed", JobContinuationOptions.OnlyOnSucceededState, false)]
    [InlineData("Failed", JobContinuationOptions.OnAnyFinishedState, false)]
    [InlineData("Processing", JobContinuationOptions.OnlyOnProcessingState, true)]
    [InlineData("Processing", JobContinuationOptions.OnlyOnFailedState, false)]
    [InlineData("Succeeded", JobContinuationOptions.OnlyOnFailedState, false)]
    [InlineData("Succeeded", JobContinuationOptions.OnlyOnSucceededState | JobContinuationOptions.OnlyOnFailedState, true)]
    public void OnStateElection_StartsOnlyTheMatchingContinuations_OnAFailedOrProcessingParent(string parentState, JobContinuationOptions options, bool starts)
    {
        SetUpParentWithContinuation(parentState, options);

        CreateFilter().OnStateElection(_context.Object);

        _stateChanger.Verify(
            x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.BackgroundJobId == _continuationId && ctx.NewState.Name == "Enqueued")),
            starts ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Processing")]
    public void OnStateElection_LeavesTheOtherContinuationsWaiting_OnAFailedOrProcessingParent(string parentState)
    {
        SetUpParentWithContinuation(parentState, JobContinuationOptions.OnlyOnSucceededState);

        CreateFilter().OnStateElection(_context.Object);

        // Not deleted either: the parent may still succeed
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_DeletesAFailureContinuation_WhenTheParentSucceeds()
    {
        SetUpParentWithContinuation(SucceededState.StateName, JobContinuationOptions.OnlyOnFailedState);

        CreateFilter().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.BackgroundJobId == _continuationId && ctx.NewState is DeletedState)));
    }

    [Fact]
    public void OnStateElection_PassesTheFailedParentsException_WhenResultsArePushed()
    {
        SetUpParentWithContinuation(FailedState.StateName, JobContinuationOptions.OnlyOnFailedState);
        ContinuationsSupportAttribute filter = new(pushResults: true, KnownFinalStates, _stateChanger.Object);

        filter.OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(
            _continuationId,
            "AntecedentException",
            It.Is<string>(value => value.Contains("InvalidOperationException", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("Failed", JobContinuationOptions.OnlyOnFailedState, true)]
    [InlineData("Processing", JobContinuationOptions.OnlyOnProcessingState, true)]
    [InlineData("Succeeded", JobContinuationOptions.OnlyOnProcessingState, true)]
    [InlineData("Failed", JobContinuationOptions.OnlyOnProcessingState, true)]
    [InlineData("Deleted", JobContinuationOptions.OnlyOnProcessingState, false)]
    [InlineData("Failed", JobContinuationOptions.OnlyOnSucceededState, false)]
    [InlineData("Processing", JobContinuationOptions.OnAnyFinishedState, false)]
    public void OnStateElection_StartsAContinuationAddedLate_WhenTheParentIsInItsState(string parentState, JobContinuationOptions options, bool starts)
    {
        _context.ApplyContext.NewStateObject = new AwaitingState(_parentId, new EnqueuedState(), options);
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_parentId)).Returns(new StateData
        {
            Name = parentState,
            Data = new Dictionary<string, string>(),
        });

        CreateFilter().OnStateElection(_context.Object);

        if (starts)
        {
            Assert.IsType<EnqueuedState>(_context.Object.CandidateState);
        }
        else
        {
            Assert.IsNotType<EnqueuedState>(_context.Object.CandidateState);
        }
    }

    [Fact]
    public void OnStateElection_KeepsAContinuationWaiting_WhenItIsAddedToAFailedParent_ForAnotherOutcome()
    {
        _context.ApplyContext.NewStateObject = new AwaitingState(_parentId, new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState);
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_parentId)).Returns(new StateData { Name = FailedState.StateName, Data = new Dictionary<string, string>() });

        CreateFilter().OnStateElection(_context.Object);

        Assert.IsType<AwaitingState>(_context.Object.CandidateState);
    }

    private void SetUpParentWithContinuation(string parentState, JobContinuationOptions options)
    {
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = _parentId };
        _context.ApplyContext.NewStateObject = parentState switch
        {
            "Failed" => new FailedState(new InvalidOperationException("boom")),
            "Processing" => new ProcessingState("server", "worker"),
            _ => new SucceededState(null, 0, 0),
        };

        _context.ApplyContext.Connection.Setup(x => x.GetJobData(_continuationId)).Returns(new JobData());
        _context.ApplyContext.Connection.Setup(x => x.GetStateData(_continuationId)).Returns(new StateData
        {
            Name = AwaitingState.StateName,
            Data = new Dictionary<string, string>
            {
                { "NextState", SerializationHelper.Serialize<IState>(new EnqueuedState(), SerializationOption.TypedInternal) }
            }
        });
        _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
            .Returns(SerializationHelper.Serialize(new List<Continuation> { new() { JobId = _continuationId, Options = options } }));
    }

    private ContinuationsSupportAttribute CreateFilter()
    {
        return new ContinuationsSupportAttribute(
            pushResults: false,
            ContinuationsSupportAttribute.KnownFinalStates,
            _stateChanger.Object);
    }
}