using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.Core.Tests;

public class IdempotentCompletionAttributeFacts
{
    private const string JobId = "JobId";

    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Dictionary<string, string> _parameters = [];

    public IdempotentCompletionAttributeFacts()
    {
        _connection
            .Setup(x => x.GetJobParameter(JobId, It.IsAny<string>()))
            .Returns<string, string>((_, name) => _parameters.TryGetValue(name, out string? value) ? value : null);

        _connection
            .Setup(x => x.SetJobParameter(JobId, It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((_, name, value) => _parameters[name] = value);
    }

    [Fact]
    public void Ctor_SetsTheOrderToZero()
    {
        Assert.Equal(0, new IdempotentCompletionAttribute().Order);
    }

    [Fact]
    public void OnStateElection_ThrowsAnException_WhenContextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotentCompletionAttribute().OnStateElection(null));
    }

    [Fact]
    public void OnStateElection_DoesNothing_WhenTheJobIsCreated()
    {
        ElectStateContext context = Elect(currentState: null, new SucceededState(null, 0, 0));

        new IdempotentCompletionAttribute().OnStateElection(context);

        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_RecordsTheFirstFinalState()
    {
        IdempotentCompletionAttribute filter = new();
        ElectStateContext context = Elect(ProcessingState.StateName, new SucceededState(null, 0, 0));

        filter.OnStateElection(context);
        Assert.Null(ReadCompletion());

        filter.OnStateApplied(Apply(ProcessingState.StateName, context.CandidateState), new Mock<IWriteOnlyTransaction>().Object);

        Assert.IsType<SucceededState>(ReadCompletion());
    }

    [Fact]
    public void OnStateApplied_DoesNothing_WhenTheJobIsCreated()
    {
        new IdempotentCompletionAttribute().OnStateApplied(Apply(null, new DeletedState()), new Mock<IWriteOnlyTransaction>().Object);

        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_KeepsTheFirstRecordedState()
    {
        Record(new SucceededState(null, 0, 0));

        new IdempotentCompletionAttribute().OnStateApplied(Apply(SucceededState.StateName, new DeletedState()), new Mock<IWriteOnlyTransaction>().Object);

        Assert.IsType<SucceededState>(ReadCompletion());
    }

    [Fact]
    public void OnStateApplied_RecordsASucceededState_WithoutItsResult_EvenWhenTheResultCantBeSerialized()
    {
        Node node = new();
        node.Next = node;

        new IdempotentCompletionAttribute().OnStateApplied(
            Apply(ProcessingState.StateName, new SucceededState(node, 12, 34) { Reason = "done" }),
            new Mock<IWriteOnlyTransaction>().Object);

        SucceededState recorded = Assert.IsType<SucceededState>(ReadCompletion());
        Assert.Null(recorded.Result);
        Assert.Equal(12, recorded.Latency);
        Assert.Equal(34, recorded.PerformanceDuration);
        Assert.Equal("done", recorded.Reason);
    }

    [Fact]
    public void OnStateApplied_DoesNotThrow_WhenTheCompletionCantBeRecorded()
    {
        _connection
            .Setup(x => x.SetJobParameter(JobId, IdempotentCompletionAttribute.CompletionParameter, It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage is down"));

        new IdempotentCompletionAttribute().OnStateApplied(Apply(ProcessingState.StateName, new SucceededState(null, 0, 0)), new Mock<IWriteOnlyTransaction>().Object);
    }

    [Fact]
    public void StateMachine_RecordsAnUnserializableResult_AndTheJobStillSucceeds()
    {
        Node node = new();
        node.Next = node;

        IState applied = ApplyWithFilters(Job.FromExpression(() => Charge()), ProcessingState.StateName, new SucceededState(node, 1, 1));

        Assert.IsType<SucceededState>(applied);
        Assert.IsType<SucceededState>(ReadCompletion());
    }

    [Fact]
    public void StateMachine_RestoresTheRecordedState_WhenAnEarlierElectionFilterReplacedProcessing()
    {
        // LatencyTimeout (order -1) turns the late Processing state into Deleted before IdempotentCompletion runs
        Record(new SucceededState(null, 1, 1));
        Job job = Job.FromExpression(() => ChargeWithLatencyTimeout());

        IState applied = ApplyWithFilters(job, ProcessingState.StateName, new ProcessingState("server", "worker"), createdAt: DateTime.UtcNow.AddHours(-2));

        Assert.IsType<SucceededState>(applied);
    }

    [Fact]
    public void StateMachine_RecordsTheDeletedState_ThatALaterElectionFilterChose()
    {
        // AutomaticRetry (order 20) turns the Failed state into Deleted after IdempotentCompletion ran
        Job job = Job.FromExpression(() => ChargeAndDeleteOnFailure());

        IState applied = ApplyWithFilters(job, ProcessingState.StateName, new FailedState(new InvalidOperationException()));

        Assert.IsType<DeletedState>(applied);
        Assert.IsType<DeletedState>(ReadCompletion());
    }

    [Fact]
    public void OnStateElection_RestoresTheRecordedState_InsteadOfRunningTheJobAgain()
    {
        Record(new SucceededState(null, 0, 0));
        ElectStateContext context = Elect(ProcessingState.StateName, new ProcessingState("server", "worker"));

        new IdempotentCompletionAttribute().OnStateElection(context);

        Assert.IsType<SucceededState>(context.CandidateState);
    }

    [Fact]
    public void OnStateElection_LetsTheJobRun_WhenNothingIsRecorded()
    {
        ElectStateContext context = Elect(EnqueuedState.StateName, new ProcessingState("server", "worker"));

        new IdempotentCompletionAttribute().OnStateElection(context);

        Assert.IsType<ProcessingState>(context.CandidateState);
    }

    [Fact]
    public void OnStateElection_HonoursAnExplicitDelete_OfASucceededJob()
    {
        Record(new SucceededState(null, 0, 0));
        ElectStateContext context = Elect(SucceededState.StateName, new DeletedState());

        new IdempotentCompletionAttribute().OnStateElection(context);

        Assert.IsType<DeletedState>(context.CandidateState);
        Assert.IsType<SucceededState>(ReadCompletion());
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Deleted")]
    public void OnStateElection_ForgetsTheCompletion_WhenAFinishedJobIsRequeued(string currentState)
    {
        Record(new SucceededState(null, 0, 0));
        ElectStateContext context = Elect(currentState, new EnqueuedState());

        new IdempotentCompletionAttribute().OnStateElection(context);

        Assert.IsType<EnqueuedState>(context.CandidateState);
        _connection.Verify(x => x.SetJobParameter(JobId, IdempotentCompletionAttribute.CompletionParameter, null));
    }

    [Fact]
    public void OnStateElection_KeepsTheCompletion_WhenAJobWhoseFinalStateWasLostIsRequeued()
    {
        // The Succeeded state was never saved, so the job is still Processing in storage
        Record(new SucceededState(null, 0, 0));
        ElectStateContext context = Elect(ProcessingState.StateName, new EnqueuedState());

        new IdempotentCompletionAttribute().OnStateElection(context);

        Assert.IsType<EnqueuedState>(context.CandidateState);
        Assert.IsType<SucceededState>(ReadCompletion());
    }

    [Fact]
    public void OnStateElection_RunsARequeuedJobAgain_AndRecordsItsNextCompletion()
    {
        IdempotentCompletionAttribute filter = new();
        filter.OnStateApplied(Apply(ProcessingState.StateName, new SucceededState(null, 0, 0)), new Mock<IWriteOnlyTransaction>().Object);
        filter.OnStateElection(Elect(SucceededState.StateName, new EnqueuedState()));

        ElectStateContext processing = Elect(EnqueuedState.StateName, new ProcessingState("server", "worker"));
        filter.OnStateElection(processing);
        ElectStateContext deleted = Elect(ProcessingState.StateName, new DeletedState());
        filter.OnStateElection(deleted);
        filter.OnStateApplied(Apply(ProcessingState.StateName, deleted.CandidateState), new Mock<IWriteOnlyTransaction>().Object);

        Assert.IsType<ProcessingState>(processing.CandidateState);
        Assert.IsType<DeletedState>(ReadCompletion());
    }

    private ElectStateContext Elect(string currentState, IState candidate)
    {
        ElectStateContextMock mock = new();
        mock.ApplyContext.Connection = _connection;
        mock.ApplyContext.BackgroundJob.Id = JobId;
        mock.ApplyContext.OldStateName = currentState;
        mock.ApplyContext.NewStateObject = candidate;
        return mock.Object;
    }

    private ApplyStateContext Apply(string currentState, IState newState)
    {
        ApplyStateContextMock mock = new()
        {
            Connection = _connection
        };
        mock.BackgroundJob.Id = JobId;
        mock.OldStateName = currentState;
        mock.NewStateObject = newState;
        return mock.Object;
    }

    // Runs the real state machine with the job's filters, the way a worker's state change does
    private IState ApplyWithFilters(Job job, string currentState, IState candidate, DateTime? createdAt = null)
    {
        Dictionary<string, string> snapshot = new(_parameters);
        BackgroundJob backgroundJob = new(JobId, job, createdAt ?? DateTime.UtcNow, snapshot);
        StateMachine machine = new(new JobFilterProviderCollection(new JobFilterAttributeFilterProvider()));

        return machine.ApplyState(new ApplyStateContext(
            new Mock<JobStorage>().Object,
            _connection.Object,
            new Mock<IWriteOnlyTransaction>().Object,
            backgroundJob,
            candidate,
            currentState));
    }

    private void Record(IState state)
    {
        _parameters[IdempotentCompletionAttribute.CompletionParameter] = SerializationHelper.Serialize(
            SerializationHelper.Serialize(state, SerializationOption.TypedInternal),
            SerializationOption.User);
    }

    private IState ReadCompletion()
    {
        if (!_parameters.TryGetValue(IdempotentCompletionAttribute.CompletionParameter, out string? value) || value == null)
        {
            return null;
        }

        string? serializedState = SerializationHelper.Deserialize<string>(value, SerializationOption.User);
        return SerializationHelper.Deserialize<IState>(serializedState, SerializationOption.TypedInternal);
    }

    [IdempotentCompletion]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Charge()
    {
    }

    [IdempotentCompletion]
    [LatencyTimeout(60)]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void ChargeWithLatencyTimeout()
    {
    }

    [IdempotentCompletion]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void ChargeAndDeleteOnFailure()
    {
    }

    public class Node
    {
        public string Name { get; set; } = "n";
        public Node Next { get; set; }
    }
}
