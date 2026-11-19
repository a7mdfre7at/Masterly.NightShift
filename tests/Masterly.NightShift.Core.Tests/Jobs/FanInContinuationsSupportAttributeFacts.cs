using System.Diagnostics;

namespace Masterly.NightShift.Core.Tests;

public class FanInContinuationsSupportAttributeFacts
{
    private const string Parameter = FanInContinuationsSupportAttribute.ParameterName;
    private const string Outcomes = FanInContinuationsSupportAttribute.OutcomesParameterName;

    private readonly ElectStateContextMock _context = new();
    private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new();
    private readonly Dictionary<string, string> _states = [];
    private readonly Dictionary<string, string> _parameters = [];
    private readonly Dictionary<string, Dictionary<string, string>> _hashes = [];

    // The entries of the set of the continuations being created, with their scores
    private readonly Dictionary<string, double> _unconfirmed = [];

    public FanInContinuationsSupportAttributeFacts()
    {
        Mock<IStorageConnection> connection = _context.ApplyContext.Connection;

        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        connection.Setup(x => x.GetJobData(It.IsAny<string>())).Returns<string>(id => id.StartsWith("missing") ? null : new JobData { CreatedAt = DateTime.UtcNow });
        connection.Setup(x => x.GetStateData(It.IsAny<string>())).Returns<string>(id =>
            _states.TryGetValue(id, out string? name) ? new StateData { Name = name, Data = StateDataFor(id) } : null);
        connection.Setup(x => x.GetJobParameter(It.IsAny<string>(), Parameter)).Returns<string, string>((id, _) =>
            _parameters.TryGetValue(id, out string? value) ? value : null);
        connection.Setup(x => x.SetJobParameter(It.IsAny<string>(), Parameter, It.IsAny<string>()))
            .Callback<string, string, string>((id, _, value) => _parameters[id] = value);
        connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns<string>(key =>
            _hashes.TryGetValue(key, out Dictionary<string, string>? hash) ? new Dictionary<string, string>(hash) : null);
        connection.Setup(x => x.GetJobData(It.Is<string>(id => !id.StartsWith("missing"))))
            .Returns<string>(id => new JobData { CreatedAt = DateTime.UtcNow, State = _states.GetValueOrDefault(id) });
        connection.Setup(x => x.CreateWriteTransaction()).Returns(() =>
        {
            List<Action> pending = [];
            Mock<IWriteOnlyTransaction> transaction = new();
            transaction.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((key, fields) =>
                {
                    List<KeyValuePair<string, string>> copy = [.. fields];
                    pending.Add(() =>
                    {
                        if (!_hashes.TryGetValue(key, out Dictionary<string, string>? hash))
                        {
                            _hashes[key] = hash = [];
                        }

                        foreach (KeyValuePair<string, string> field in copy)
                        {
                            hash[field.Key] = field.Value;
                        }
                    });
                });
            transaction.Setup(x => x.RemoveHash(It.IsAny<string>())).Callback<string>(key => pending.Add(() => _hashes.Remove(key)));
            transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
                .Callback<string, string, double>((key, value, score) => pending.Add(() => _unconfirmed[value] = score));
            transaction.Setup(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((key, value) => pending.Add(() => _unconfirmed.Remove(value)));
            transaction.Setup(x => x.Commit()).Callback(() => pending.ForEach(static action => action()));
            return transaction.Object;
        });
    }

    private readonly Dictionary<string, Dictionary<string, string>> _stateData = [];

    private Dictionary<string, string> StateDataFor(string id)
    {
        return _stateData.TryGetValue(id, out Dictionary<string, string>? data) ? data : [];
    }

    private FanInContinuationsSupportAttribute Create()
    {
        return new FanInContinuationsSupportAttribute([SucceededState.StateName, DeletedState.StateName], _stateChanger.Object);
    }

    [Fact]
    public void Ctor_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new FanInContinuationsSupportAttribute(null, _stateChanger.Object));
        Assert.Throws<ArgumentNullException>(() => new FanInContinuationsSupportAttribute([], null));
        Assert.Equal(1001, Create().Order);
    }

    [Fact]
    public void OnStateElection_ChildBranch_RegistersTheChildOnEveryParent()
    {
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "p2"]);

        Create().OnStateElection(_context.Object);

        Assert.Equal(new[] { "child" }, FanInContinuationsSupportAttribute.Parse(_parameters["p1"]));
        Assert.Equal(new[] { "child" }, FanInContinuationsSupportAttribute.Parse(_parameters["p2"]));
        Assert.IsType<AwaitingJobsState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ChildBranch_DoesNotDuplicateAnExistingRegistration()
    {
        _parameters["p1"] = SerializationHelper.Serialize(new List<string> { "child" });
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1"]);

        Create().OnStateElection(_context.Object);

        Assert.Equal(new[] { "child" }, FanInContinuationsSupportAttribute.Parse(_parameters["p1"]));
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("p1", Parameter, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ChildBranch_Throws_WhenAParentDoesNotExist()
    {
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["missing-parent"]);

        Assert.Throws<InvalidOperationException>(() => Create().OnStateElection(_context.Object));
    }

    [Fact]
    public void OnStateElection_ChildBranch_StartsImmediately_WhenAllParentsAlreadySucceeded()
    {
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = SucceededState.StateName;
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "p2"], new EnqueuedState("critical"), JobContinuationOptions.OnlyOnSucceededState, FanInMode.All);

        Create().OnStateElection(_context.Object);

        Assert.Equal("critical", Assert.IsType<EnqueuedState>(_context.Object.CandidateState).Queue);
    }

    [Fact]
    public void OnStateElection_ChildBranch_StaysAwaiting_WhileAParentIsRunning()
    {
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = ProcessingState.StateName;
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "p2"]);

        Create().OnStateElection(_context.Object);

        Assert.IsType<AwaitingJobsState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ChildBranch_DeletesTheChild_WhenAParentWasDeleted_InAllMode()
    {
        _states["p1"] = DeletedState.StateName;
        _states["p2"] = ProcessingState.StateName;
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "p2"]);

        Create().OnStateElection(_context.Object);

        Assert.Equal("Continuation condition was not met", Assert.IsType<DeletedState>(_context.Object.CandidateState).Reason);
    }

    [Fact]
    public void OnStateElection_ChildBranch_StartsOnTheFirstSuccess_InAnyMode()
    {
        _states["p1"] = ProcessingState.StateName;
        _states["p2"] = SucceededState.StateName;
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "p2"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.Any);

        Create().OnStateElection(_context.Object);

        Assert.IsType<EnqueuedState>(_context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ParentBranch_StartsTheChild_WhenItWasTheLastParent()
    {
        AwaitingJobsState awaiting = new(["parent", "other"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["other"] = SucceededState.StateName;

        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "child" && c.NewState is EnqueuedState && Enumerable.Contains(c.ExpectedStates, AwaitingState.StateName))));
    }

    [Fact]
    public void OnStateElection_ParentBranch_DoesNothing_WhileOtherParentsAreStillRunning()
    {
        AwaitingJobsState awaiting = new(["parent", "other"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["other"] = EnqueuedState.StateName;

        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ParentBranch_DeletesTheChild_WhenTheParentFailedTheCondition()
    {
        AwaitingJobsState awaiting = new(["parent", "other"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["other"] = EnqueuedState.StateName;

        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new DeletedState();

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "child" && c.NewState is DeletedState)));
    }

    [Fact]
    public void OnStateElection_ParentBranch_SkipsChildrenThatAreNoLongerAwaiting()
    {
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = EnqueuedState.StateName;

        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ParentBranch_TreatsAMissingParentAsDeleted()
    {
        AwaitingJobsState awaiting = new(["parent", "missing-other"], new EnqueuedState(), JobContinuationOptions.OnAnyFinishedState, FanInMode.All);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();

        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "child" && c.NewState is EnqueuedState)));
    }

    [Fact]
    public void OnStateElection_ParentBranch_StartsAnAnyFailedChild_WhenTheParentFailsForGood()
    {
        _context.ApplyContext.BackgroundJob = new BackgroundJobMock { Id = "p1", ParametersSnapshot = new Dictionary<string, string> { [Parameter] = SerializationHelper.SerializeInternal(new List<string> { "child" }) } };
        _context.ApplyContext.NewStateObject = new FailedState(new InvalidOperationException());
        _context.ApplyContext.Connection.Setup(x => x.GetJobData("child")).Returns(new JobData());
        _context.ApplyContext.Connection.Setup(x => x.GetStateData("child")).Returns(new StateData
        {
            Name = AwaitingJobsState.StateName,
            Data = new AwaitingJobsState(["p1", "p2"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.AnyFailed).SerializeData(),
        });
        _context.ApplyContext.Connection.Setup(x => x.GetStateData("p1")).Returns(new StateData { Name = FailedState.StateName });

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.BackgroundJobId == "child" && ctx.NewState is EnqueuedState)));
    }

    [Fact]
    public void OnStateElection_IgnoresNonFinalStates_AndOrdinaryAwaitingState()
    {
        _context.ApplyContext.BackgroundJob.Id = "job";
        _context.ApplyContext.NewStateObject = new ProcessingState("server", "worker");
        Create().OnStateElection(_context.Object);

        _context.ApplyContext.NewStateObject = new AwaitingState("parent");
        Create().OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), Parameter), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), Parameter, It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Succeeded", "Start")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Deleted", "Skip")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,", "Undecided")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnAnyFinishedState, "Succeeded,Deleted", "Start")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Deleted,Succeeded", "Start")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Deleted,", "Undecided")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Deleted,Deleted", "Skip")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnDeletedState, "Deleted,", "Start")]
    [InlineData(FanInMode.AnyFailed, JobContinuationOptions.OnlyOnSucceededState, "Failed,", "Start")]
    [InlineData(FanInMode.AnyFailed, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,", "Undecided")]
    [InlineData(FanInMode.AnyFailed, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Deleted", "Skip")]
    [InlineData(FanInMode.AnyFailedOrCanceled, JobContinuationOptions.OnlyOnSucceededState, "Deleted,", "Start")]
    [InlineData(FanInMode.AnyFailedOrCanceled, JobContinuationOptions.OnlyOnSucceededState, ",Failed", "Start")]
    [InlineData(FanInMode.AnyFailedOrCanceled, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Succeeded", "Skip")]
    [InlineData(FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState, "Failed,Deleted", "Start")]
    [InlineData(FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState, "Failed,", "Undecided")]
    [InlineData(FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Succeeded", "Start")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Failed,Succeeded", "Undecided")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Failed,Failed", "Undecided")]
    public void Evaluate_DecidesFromTheParentStates(FanInMode mode, JobContinuationOptions options, string states, string expected)
    {
        string[] names = states.Split(',');
        AwaitingJobsState awaiting = new(["a", "b"], new EnqueuedState(), options, mode);

        FanInContinuationsSupportAttribute.Decision decision = Create().Evaluate(awaiting, id => names[id == "a" ? 0 : 1] == "" ? null : names[id == "a" ? 0 : 1]);

        Assert.Equal(expected, decision.ToString());
    }

    [Fact]
    public void ReadChildren_ReturnsTheRegisteredChildren_InOrder()
    {
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "c2", "c1" });

        IReadOnlyList<string> children = FanInContinuationsSupportAttribute.ReadChildren(_context.ApplyContext.Connection.Object, "parent");

        Assert.Equal(["c2", "c1"], children);
    }

    [Fact]
    public void ReadChildren_ReturnsAnEmptyList_WhenTheJobHasNoChildren()
    {
        IReadOnlyList<string> children = FanInContinuationsSupportAttribute.ReadChildren(_context.ApplyContext.Connection.Object, "parent");

        Assert.Empty(children);
    }

    [Fact]
    public void ReadChildren_ThrowsAnException_WhenArgumentsAreNull()
    {
        Assert.Equal("connection", Assert.Throws<ArgumentNullException>(
            () => FanInContinuationsSupportAttribute.ReadChildren(null, "parent")).ParamName);
        Assert.Equal("jobId", Assert.Throws<ArgumentNullException>(
            () => FanInContinuationsSupportAttribute.ReadChildren(_context.ApplyContext.Connection.Object, null)).ParamName);
    }

    [Fact]
    public void OnStateElection_ThrowsAnException_WhenContextIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => Create().OnStateElection(null));

        Assert.Equal("context", exception.ParamName);
    }

    [Fact]
    public void OnStateElection_ParentBranch_SkipsChildrenThatDoNotExist()
    {
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "missing-child" });
        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("missing-child"), Times.Never);
    }

    [Fact]
    public void OnStateElection_ParentBranch_IgnoresAnAbortedChild_WhoseStateWasNeverInitialized()
    {
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "aborted-child" });
        _context.ApplyContext.Connection.Setup(x => x.GetJobData("aborted-child")).Returns(new JobData { CreatedAt = DateTime.UtcNow.AddHours(-1) });
        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("aborted-child"), Times.Once);
    }

    [Fact]
    public void OnStateElection_ParentBranch_RetriesReadingTheChildState_UntilItIsInitialized()
    {
        AwaitingJobsState awaiting = new(["parent"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "slow-child" });
        _context.ApplyContext.Connection.SetupSequence(x => x.GetStateData("slow-child"))
            .Returns((StateData)null)
            .Returns(new StateData { Name = AwaitingState.StateName, Data = awaiting.SerializeData() });
        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.GetStateData("slow-child"), Times.Exactly(2));
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "slow-child" && c.NewState is EnqueuedState)), Times.Once);
    }

    [Fact]
    public void OnStateElection_ParentBranch_UsesTheParametersSnapshot_InsteadOfStorage()
    {
        AwaitingJobsState awaiting = new(["parent"]);
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            { Parameter, SerializationHelper.Serialize(new List<string> { "child" }) }
        };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.GetJobParameter("parent", Parameter), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "child")), Times.Once);
    }

    [Fact]
    public void OnStateElection_ParentBranch_DecidesTheChildrenOnlyAfterTheCommit()
    {
        AwaitingJobsState awaiting = new(["parent", "other"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["parent"] = ProcessingState.StateName;
        _states["other"] = SucceededState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);

        _states["parent"] = SucceededState.StateName; // the commit
        afterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "child" && c.NewState is EnqueuedState && Enumerable.Contains(c.ExpectedStates, AwaitingState.StateName))), Times.Once);
    }

    [Fact]
    public void OnStateElection_ParentBranch_StartsTheChild_WhenTheLastTwoParentsFinishAtTheSameTime()
    {
        // Both parents are elected Succeeded while the other one is still Processing in storage, then both
        // commit. Deciding during the election, each saw the other unfinished and nobody started the child.
        AwaitingJobsState awaiting = new(["p1", "p2"]);
        _parameters["p1"] = SerializationHelper.Serialize(new List<string> { "child" });
        _parameters["p2"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["p1"] = ProcessingState.StateName;
        _states["p2"] = ProcessingState.StateName;

        ElectStateContextMock first = new();
        first.ApplyContext.Connection = _context.ApplyContext.Connection;
        ElectStateContextMock second = new();
        second.ApplyContext.Connection = _context.ApplyContext.Connection;
        PostCommitActions firstAfterCommit = UsePostCommitActions(first, "p1", new SucceededState(null, 0, 0));
        PostCommitActions secondAfterCommit = UsePostCommitActions(second, "p2", new SucceededState(null, 0, 0));
        FanInContinuationsSupportAttribute filter = Create();

        filter.OnStateElection(first.Object);
        filter.OnStateElection(second.Object);
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = SucceededState.StateName;
        firstAfterCommit.Run();
        secondAfterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "child" && c.NewState is EnqueuedState && Enumerable.Contains(c.ExpectedStates, AwaitingState.StateName))), Times.AtLeastOnce);
    }

    [Fact]
    public void OnStateElection_ParentBranch_ReadsItsOwnCommittedState_AfterTheCommit()
    {
        AwaitingJobsState awaiting = new(["parent", "other"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["other"] = SucceededState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        _states["parent"] = EnqueuedState.StateName; // requeued before the actions ran
        afterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ParentBranch_DoesNotQueueAnything_WhenThereAreNoChildren()
    {
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        afterCommit.Run();

        _context.ApplyContext.Connection.Verify(x => x.GetJobData(It.IsAny<string>()), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ParentBranch_AfterTheCommit_KeepsDecidingOtherChildren_WhenOneKeepsFailing()
    {
        AwaitingJobsState awaiting = new(["parent"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "broken", "child" });
        _states["broken"] = AwaitingState.StateName;
        _stateData["broken"] = awaiting.SerializeData();
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["parent"] = SucceededState.StateName;
        _stateChanger.Setup(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "broken"))).Throws(new InvalidOperationException("storage down"));
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        afterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "broken")), Times.Exactly(3));
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "child")), Times.Once);
    }

    [Fact]
    public void OnStateElection_ParentBranch_ReadsTheParentsSharedByChildren_OnlyOnce()
    {
        AwaitingJobsState awaiting = new(["parent", "other"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "c1", "c2" });
        foreach (string? child in new[] { "c1", "c2" })
        {
            _states[child] = AwaitingState.StateName;
            _stateData[child] = awaiting.SerializeData();
        }

        _states["parent"] = SucceededState.StateName;
        _states["other"] = SucceededState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        afterCommit.Run();

        _context.ApplyContext.Connection.Verify(x => x.GetStateData("other"), Times.Once);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("parent"), Times.Once);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is EnqueuedState)), Times.Exactly(2));
    }

    [Fact]
    public void OnStateElection_ParentBranch_StopsAtTheFirstUnfinishedParent_StartingFromTheLast()
    {
        AwaitingJobsState awaiting = new(["p1", "p2", "parent", "p4", "p5"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = SucceededState.StateName;
        _states["p4"] = SucceededState.StateName;
        _states["p5"] = ProcessingState.StateName;

        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.GetStateData("p5"), Times.Once);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("p4"), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("p1"), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Theory]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Succeeded,Succeeded", "Start")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Deleted,,Succeeded", "Skip")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,,Deleted", "Undecided")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Deleted,Succeeded", "Skip")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnAnyFinishedState, "Deleted,Succeeded,Deleted", "Start")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,,", "Start")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Deleted,,Succeeded", "Undecided")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Deleted,Succeeded,Deleted", "Start")]
    [InlineData(FanInMode.Any, JobContinuationOptions.OnlyOnSucceededState, "Deleted,Deleted,Deleted", "Skip")]
    [InlineData(FanInMode.All, JobContinuationOptions.OnlyOnSucceededState, "Processing,Succeeded,Succeeded", "Undecided")]
    [InlineData(FanInMode.AnyFailed, JobContinuationOptions.OnlyOnSucceededState, "Failed,,", "Start")]
    [InlineData(FanInMode.AnyFailed, JobContinuationOptions.OnlyOnSucceededState, "Succeeded,Succeeded,Deleted", "Skip")]
    [InlineData(FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState, "Failed,Deleted,Succeeded", "Start")]
    [InlineData(FanInMode.AllCompleted, JobContinuationOptions.OnlyOnSucceededState, "Failed,,Succeeded", "Undecided")]
    public void Evaluate_ForAFinishedParent_ReadsItFirst_ThenTheOthersFromTheLast(FanInMode mode, JobContinuationOptions options, string states, string expected)
    {
        // states: the finished parent "b", then "c", then "a"
        string[] names = states.Split(',');
        string[] order = ["b", "c", "a"];
        AwaitingJobsState awaiting = new(["a", "b", "c"], new EnqueuedState(), options, mode);
        List<string> read = [];

        FanInContinuationsSupportAttribute.Decision decision = Create().Evaluate(awaiting, id =>
        {
            read.Add(id);
            string name = names[Array.IndexOf(order, id)];
            return name == "" ? null : name;
        }, "b");

        Assert.Equal(expected, decision.ToString());
        Assert.Equal(order.Take(read.Count), read);
    }

    [Fact, StaticLock]
    public void OnStateElection_ParentBranch_SkipsAChildWhoseStateIsNotStoredInTime_WithoutWaitingLong()
    {
        TimeSpan budget = FanInContinuationsSupportAttribute.DecisionRoundBudget;
        FanInContinuationsSupportAttribute.DecisionRoundBudget = TimeSpan.FromMilliseconds(300);

        try
        {
            _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "slow-child" });
            _context.ApplyContext.BackgroundJob.Id = "parent";
            _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
            Stopwatch started = System.Diagnostics.Stopwatch.StartNew();

            Create().OnStateElection(_context.Object);

            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"Took {started.Elapsed}");
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }
        finally
        {
            FanInContinuationsSupportAttribute.DecisionRoundBudget = budget;
        }
    }

    [Fact, StaticLock]
    public void OnStateElection_ParentBranch_AfterTheCommit_StopsRetryingAChild_WhenTheRoundBudgetIsSpent()
    {
        TimeSpan budget = FanInContinuationsSupportAttribute.DecisionRoundBudget;
        FanInContinuationsSupportAttribute.DecisionRoundBudget = TimeSpan.FromMilliseconds(800);

        try
        {
            AwaitingJobsState awaiting = new(["parent"]);
            _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "broken" });
            _states["broken"] = AwaitingState.StateName;
            _stateData["broken"] = awaiting.SerializeData();
            _states["parent"] = SucceededState.StateName;
            _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>())).Throws(new InvalidOperationException("storage down"));
            PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

            Create().OnStateElection(_context.Object);
            afterCommit.Run();

            // The first retry waits 0.5 s; the second would wait 1 s more, past the budget
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Exactly(2));
        }
        finally
        {
            FanInContinuationsSupportAttribute.DecisionRoundBudget = budget;
        }
    }

    [Fact]
    public void OnStateElection_ChildBranch_DecidesItselfAfterItsCommit_WhenAParentFinishedMeanwhile()
    {
        AwaitingJobsState awaiting = new(["p1", "p2"]);
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = ProcessingState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "child", awaiting);

        Create().OnStateElection(_context.Object);

        Assert.IsType<AwaitingJobsState>(_context.Object.CandidateState);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);

        // p2 finished while the child's Awaiting state was being committed, and didn't see the child
        _states["p2"] = SucceededState.StateName;
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        afterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "child" && c.NewState is EnqueuedState && Enumerable.Contains(c.ExpectedStates, AwaitingState.StateName))), Times.Once);
    }

    [Fact]
    public void OnStateElection_ChildBranch_LeavesTheChildAwaiting_AfterItsCommit_WhileAParentStillRuns()
    {
        AwaitingJobsState awaiting = new(["p1", "p2"]);
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = ProcessingState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "child", awaiting);

        Create().OnStateElection(_context.Object);
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        afterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ChildBranch_DoesNotQueueACheck_WhenDecidedRightAway()
    {
        AwaitingJobsState awaiting = new(["p1"]);
        _states["p1"] = SucceededState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "child", awaiting);

        Create().OnStateElection(_context.Object);
        afterCommit.Run();

        Assert.IsType<EnqueuedState>(_context.Object.CandidateState);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("child"), Times.Never);
    }

    [Fact]
    public void OnStateElection_ParentBranch_DecidesTheChildren_WhenTheCommitWentThroughButThrew()
    {
        AwaitingJobsState awaiting = new(["parent"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        _states["parent"] = SucceededState.StateName; // committed, but the reply was lost
        afterCommit.RunAfterFailure();

        // The caller's retry finds the parent Succeeded and changes nothing, so this was the only chance
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "child" && c.NewState is EnqueuedState && Enumerable.Contains(c.ExpectedStates, AwaitingState.StateName))), Times.Once);
    }

    [Fact]
    public void OnStateElection_ParentBranch_DecidesNothing_WithoutWaitingForChildren_WhenTheCommitFailed()
    {
        AwaitingJobsState awaiting = new(["parent"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child", "slow-child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["parent"] = ProcessingState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));
        Stopwatch started = System.Diagnostics.Stopwatch.StartNew();

        Create().OnStateElection(_context.Object);
        afterCommit.RunAfterFailure();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"Took {started.Elapsed}");
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("child"), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData("slow-child"), Times.Never);
    }

    [Fact]
    public void OnStateElection_ChildBranch_DecidesItself_WhenItsCommitWentThroughButThrew()
    {
        AwaitingJobsState awaiting = new(["p1", "p2"]);
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = ProcessingState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "child", awaiting);

        Create().OnStateElection(_context.Object);

        // p2 finished meanwhile and gave up on the child, whose state was still being stored; the child's
        // commit went through but threw, and the creator's retry finds the state stored and stops
        _states["p2"] = SucceededState.StateName;
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        afterCommit.RunAfterFailure();

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.BackgroundJobId == "child" && c.NewState is EnqueuedState)), Times.Once);
    }

    [Fact]
    public void OnStateElection_ChildBranch_DecidesNothing_WhenItsCommitFailed()
    {
        AwaitingJobsState awaiting = new(["p1", "p2"]);
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = ProcessingState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "child", awaiting);

        Create().OnStateElection(_context.Object);
        _states["p2"] = SucceededState.StateName;
        afterCommit.RunAfterFailure(); // the child has no state: the creation is retried

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ChildBranch_RegistersOnNoParent_WhenALaterParentDoesNotExist()
    {
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "p2", "missing-parent"]);

        Assert.Throws<InvalidOperationException>(() => Create().OnStateElection(_context.Object));

        Assert.Empty(_parameters);
        _context.ApplyContext.Connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void OnStateElection_ChildBranch_RegistersTheChild_WhenTheApplicationsOptionsDoNotKnowItsList()
    {
        SerializationHelper.SetUserSerializerSettings(new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(int)) });
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1"]);

        Create().OnStateElection(_context.Object);

        Assert.Equal("[\"child\"]", _parameters["p1"]);
    }

    [Fact]
    public void OnStateElection_ParentBranch_TrustsTheParametersSnapshot_WhenItHasNoChildren()
    {
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _context.ApplyContext.BackgroundJob.Id = "parent";
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { "RetryCount", "1" } };
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        Create().OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _context.ApplyContext.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
    }

    // What a parent finished as, kept for the child that still waits

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Deleted")]
    [InlineData("Failed")]
    public void OnStateApplied_OfAParentThatFinishes_WritesWhatItFinishedAs_ForEachChild_InItsTransaction(string state)
    {
        ApplyStateContextMock context = ParentFinishes("parent", state, "child-1", "child-2");

        Apply(context);

        string[] children = ["child-1", "child-2"];
        foreach (string child in children)
        {
            context.Transaction.Verify(x => x.SetRangeInHash(
                $"fan-in:{child}",
                It.Is<IEnumerable<KeyValuePair<string, string>>>(fields => fields.Single().Key == "parent" && fields.Single().Value == state)), Times.Once);
        }

        // One field a child, whatever the child waits for: nothing else is read or written
        context.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Exactly(2));
        context.Connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
        context.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
        context.Connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Theory]
    [InlineData("Processing")]
    [InlineData("Enqueued")]
    [InlineData("Scheduled")]
    public void OnStateApplied_OfAParentThatDoesNotFinish_WritesNothing(string state)
    {
        ApplyStateContextMock context = ParentFinishes("parent", state, "child");

        Apply(context);

        context.Transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_OfAJobWithoutChildren_ReadsAndWritesNothing()
    {
        ApplyStateContextMock context = ParentFinishes("parent", SucceededState.StateName);

        Apply(context);

        context.Transaction.VerifyNoOtherCalls();
        context.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_OfAChildThatStopsWaiting_RemovesWhatItsParentsWrote_WhoeverDecidesIt()
    {
        ApplyStateContextMock context = new() { OldStateName = AwaitingState.StateName, NewStateObject = new EnqueuedState() };
        context.BackgroundJob.Id = "child";
        context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { [Outcomes] = "fan-in:child" };

        Apply(context);

        context.Transaction.Verify(x => x.RemoveHash("fan-in:child"), Times.Once);
    }

    [Fact]
    public void OnStateApplied_OfAContinuationOfASingleParent_ThatStopsWaiting_RemovesNothing()
    {
        ApplyStateContextMock context = new() { OldStateName = AwaitingState.StateName, NewStateObject = new EnqueuedState() };
        context.BackgroundJob.Id = "continuation";
        context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();

        Apply(context);

        context.Transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_NeverThrows_WhenWhatAParentFinishedAsCanNotBeQueued()
    {
        ApplyStateContextMock context = ParentFinishes("parent", SucceededState.StateName, "child");
        context.Transaction.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Throws(new ArgumentException("Too long"));

        Apply(context);
    }

    [Fact]
    public void OnStateElection_ChildBranch_MarksAChildThatWaits_SoThatWhatItsParentsWriteGoesWithIt()
    {
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = ProcessingState.StateName;
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "p2"]);

        Create().OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("child", Outcomes, "fan-in:child"), Times.Once);
    }

    [Theory]
    [InlineData("Succeeded", FanInMode.All, "Enqueued")]
    [InlineData("Deleted", FanInMode.All, "Deleted")]
    [InlineData("Failed", FanInMode.All, null)]
    [InlineData("Succeeded", FanInMode.AnyFailedOrCanceled, "Deleted")]
    [InlineData("Deleted", FanInMode.AnyFailedOrCanceled, "Enqueued")]
    [InlineData("Failed", FanInMode.AnyFailed, "Enqueued")]
    [InlineData("Failed", FanInMode.AllCompleted, "Enqueued")]
    [InlineData(null, FanInMode.All, "Deleted")]
    [InlineData(null, FanInMode.AnyFailedOrCanceled, "Enqueued")]
    public void OnStateElection_ParentBranch_CountsAParentWhoseJobIsGone_AsWhatItWrote_AndAsDeletedWhenItWroteNothing(
        string written, FanInMode mode, string expected)
    {
        // "missing-other" finished long ago and its job expired; "parent" is the last to finish, and succeeds
        AwaitingJobsState awaiting = new(["missing-other", "parent"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, mode);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        if (written != null)
        {
            _hashes["fan-in:child"] = new Dictionary<string, string> { ["missing-other"] = written };
        }

        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        _states["parent"] = SucceededState.StateName;
        afterCommit.Run();

        if (expected == null)
        {
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }
        else
        {
            _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "child" && c.NewState.Name == expected)), Times.Once);
        }
    }

    [Fact]
    public void OnStateElection_ParentBranch_GoesByTheStateOfAParentWhoseJobExists_WhateverItWroteBefore()
    {
        // "other" failed for good, which it wrote, and was requeued: it runs again
        AwaitingJobsState awaiting = new(["other", "parent"], new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.AllCompleted);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["other"] = ProcessingState.StateName;
        _hashes["fan-in:child"] = new Dictionary<string, string> { ["other"] = FailedState.StateName };
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        _states["parent"] = SucceededState.StateName;
        afterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ParentBranch_ReadsWhatTheParentsWrote_OnlyWhenTheJobOfOneIsGone_AndOnce()
    {
        AwaitingJobsState awaiting = new(["missing-1", "missing-2", "other", "parent"]);
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        _states["other"] = SucceededState.StateName;
        _hashes["fan-in:child"] = new Dictionary<string, string> { ["missing-1"] = SucceededState.StateName, ["missing-2"] = SucceededState.StateName };
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        _states["parent"] = SucceededState.StateName;
        afterCommit.Run();

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "child" && c.NewState is EnqueuedState)), Times.Once);
        _context.ApplyContext.Connection.Verify(x => x.GetAllEntriesFromHash("fan-in:child"), Times.Once);
    }

    [Fact]
    public void OnStateElection_ParentBranch_AfterTheCommit_RemovesWhatItWrote_ForAChildDecidedMeanwhile()
    {
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
        _states["child"] = EnqueuedState.StateName;
        _hashes["fan-in:child"] = new Dictionary<string, string> { ["parent"] = SucceededState.StateName };
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        _states["parent"] = SucceededState.StateName;
        afterCommit.Run();

        Assert.False(_hashes.ContainsKey("fan-in:child"));
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_ChildBranch_WritesWhatTheParentsThatFinishedBeforeItFinishedAs_OnceItIsStored()
    {
        AwaitingJobsState awaiting = new(["p1", "p2"]);
        _states["p1"] = SucceededState.StateName;
        _states["p2"] = ProcessingState.StateName;
        PostCommitActions afterCommit = UsePostCommitActions(_context, "child", awaiting);

        Create().OnStateElection(_context.Object);
        Assert.Empty(_hashes);

        _states["child"] = AwaitingState.StateName;
        _stateData["child"] = awaiting.SerializeData();
        afterCommit.Run();

        Assert.Equal(new Dictionary<string, string> { ["p1"] = SucceededState.StateName }, _hashes["fan-in:child"]);
        _context.ApplyContext.Connection.Verify(x => x.AcquireDistributedLock("job:p1:state-lock", It.IsAny<TimeSpan>()), Times.AtLeastOnce);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact]
    public void RecordOutcomes_WritesTheFinishedParents_EachUnderItsLock_AndRemovesThemWhenTheChildNoLongerWaits()
    {
        IStorageConnection connection = _context.ApplyContext.Connection.Object;
        HashSet<string> finalStates = [SucceededState.StateName, DeletedState.StateName];
        _states["p1"] = FailedState.StateName;
        _states["p2"] = ProcessingState.StateName;
        _states["child"] = AwaitingState.StateName;

        Assert.True(FanInContinuationsSupportAttribute.RecordOutcomes(connection, "child", ["p1", "p2", "missing-parent"], finalStates, TimeSpan.Zero));
        Assert.Equal(new Dictionary<string, string> { ["p1"] = FailedState.StateName }, _hashes["fan-in:child"]);

        // A parent that is changing state is left to that change
        _context.ApplyContext.Connection.Setup(x => x.AcquireDistributedLock("job:p2:state-lock", It.IsAny<TimeSpan>()))
            .Throws(new DistributedLockTimeoutException("job:p2:state-lock"));
        _states["p2"] = SucceededState.StateName;
        Assert.True(FanInContinuationsSupportAttribute.RecordOutcomes(connection, "child", ["p2"], finalStates, TimeSpan.Zero));
        Assert.False(_hashes["fan-in:child"].ContainsKey("p2"));

        // Decided meanwhile: what was written goes
        _states["child"] = EnqueuedState.StateName;
        Assert.True(FanInContinuationsSupportAttribute.RecordOutcomes(connection, "child", ["p1"], finalStates, TimeSpan.Zero));
        Assert.False(_hashes.ContainsKey("fan-in:child"));
    }

    [Fact]
    public void OnStateElection_ChildBranch_ListsTheChildAmongTheOnesBeingCreated_BeforeItIsRegisteredOnAnyParent()
    {
        _states["p1"] = ProcessingState.StateName;
        _context.ApplyContext.BackgroundJob.Id = "child";
        _context.ApplyContext.NewStateObject = new AwaitingJobsState(["p1", "missing-parent"]);

        // The creation fails at its second parent: the child is listed all the same
        Assert.Throws<InvalidOperationException>(() => Create().OnStateElection(_context.Object));

        double score = Assert.Single(_unconfirmed, static x => x.Key == "child").Value;
        Assert.InRange(score, JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(29)), JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(31)));
    }

    [Fact]
    public void OnStateElection_ParentBranch_AfterTheCommit_RemovesWhatItWrote_ForAChildWhoseCreationWasGivenUp()
    {
        // Created an hour ago, and still without a state: its creation stopped
        _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "aborted-child" });
        _context.ApplyContext.Connection.Setup(x => x.GetJobData("aborted-child")).Returns(new JobData { CreatedAt = DateTime.UtcNow.AddHours(-1) });
        _hashes["fan-in:aborted-child"] = new Dictionary<string, string> { ["parent"] = SucceededState.StateName };
        PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

        Create().OnStateElection(_context.Object);
        _states["parent"] = SucceededState.StateName;
        afterCommit.Run();

        Assert.False(_hashes.ContainsKey("fan-in:aborted-child"));
    }

    [Fact]
    public void OnStateElection_ParentBranch_AfterTheCommit_KeepsWhatItWrote_ForAChildThatMayStillBeBeingCreated()
    {
        FanInContinuationsSupportAttribute.DecisionRoundBudget = TimeSpan.FromMilliseconds(100);
        try
        {
            _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "young-child" });
            _context.ApplyContext.Connection.Setup(x => x.GetJobData("young-child")).Returns(new JobData { CreatedAt = DateTime.UtcNow });
            _hashes["fan-in:young-child"] = new Dictionary<string, string> { ["parent"] = SucceededState.StateName };
            PostCommitActions afterCommit = UsePostCommitActions(_context, "parent", new SucceededState(null, 0, 0));

            Create().OnStateElection(_context.Object);
            _states["parent"] = SucceededState.StateName;
            afterCommit.Run();

            Assert.True(_hashes.ContainsKey("fan-in:young-child"));
        }
        finally
        {
            FanInContinuationsSupportAttribute.DecisionRoundBudget = TimeSpan.FromSeconds(5);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Awaiting", false)]
    [InlineData("Enqueued", false)]
    public void ForgetIfNeverCreated_RemovesWhatTheParentsWrote_OnlyForAChildWithoutState_AndForgetsTheChildEitherWay(string state, bool removed)
    {
        _unconfirmed["child"] = 1;
        _hashes["fan-in:child"] = new Dictionary<string, string> { ["p1"] = SucceededState.StateName };
        _context.ApplyContext.Connection.Setup(x => x.GetJobData("child")).Returns(new JobData { State = state });

        Assert.Equal(removed, FanInContinuationsSupportAttribute.ForgetIfNeverCreated(_context.ApplyContext.Connection.Object, "child"));

        Assert.Equal(!removed, _hashes.ContainsKey("fan-in:child"));
        Assert.Empty(_unconfirmed);
    }

    [Fact]
    public void ForgetIfNeverCreated_RemovesWhatTheParentsWrote_ForAChildWhoseJobIsGone()
    {
        _hashes["fan-in:missing-child"] = new Dictionary<string, string> { ["p1"] = SucceededState.StateName };

        Assert.True(FanInContinuationsSupportAttribute.ForgetIfNeverCreated(_context.ApplyContext.Connection.Object, "missing-child"));

        Assert.Empty(_hashes);
    }

    private ApplyStateContextMock ParentFinishes(string parentId, string state, params string[] children)
    {
        ApplyStateContextMock context = new() { OldStateName = ProcessingState.StateName };
        context.NewState.Setup(x => x.Name).Returns(state);
        context.BackgroundJob.Id = parentId;
        Dictionary<string, string> parameters = [];
        if (children.Length > 0)
        {
            parameters[Parameter] = SerializationHelper.SerializeInternal(children.ToList());
        }

        context.BackgroundJob.ParametersSnapshot = parameters;
        return context;
    }

    private void Apply(ApplyStateContextMock context)
    {
        IApplyStateFilter filter = Create();
        filter.OnStateApplied(context.Object, context.Transaction.Object);
    }

    private static PostCommitActions UsePostCommitActions(ElectStateContextMock context, string jobId, IState candidate)
    {
        PostCommitActions afterCommit = new();
        context.ApplyContext.BackgroundJob.Id = jobId;
        context.ApplyContext.NewStateObject = candidate;
        context.ApplyContext.Object.AfterCommit = afterCommit;
        return afterCommit;
    }

    [Fact]
    public void Parse_HandlesNullAndMalformedInput()
    {
        Assert.Empty(FanInContinuationsSupportAttribute.Parse(null));
        Assert.Empty(FanInContinuationsSupportAttribute.Parse(""));
        Assert.Equal(new[] { "1", "2" }, FanInContinuationsSupportAttribute.Parse("[\"1\",\"2\"]"));
    }
}
