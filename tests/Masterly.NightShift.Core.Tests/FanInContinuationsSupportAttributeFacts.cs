using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class FanInContinuationsSupportAttributeFacts
    {
        private const string Parameter = FanInContinuationsSupportAttribute.ParameterName;

        private readonly ElectStateContextMock _context = new ElectStateContextMock();
        private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new Mock<IBackgroundJobStateChanger>();
        private readonly Dictionary<string, string> _states = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _parameters = new Dictionary<string, string>();

        public FanInContinuationsSupportAttributeFacts()
        {
            var connection = _context.ApplyContext.Connection;

            connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
            connection.Setup(x => x.GetJobData(It.IsAny<string>())).Returns<string>(id => id.StartsWith("missing") ? null : new JobData { CreatedAt = DateTime.UtcNow });
            connection.Setup(x => x.GetStateData(It.IsAny<string>())).Returns<string>(id =>
                _states.TryGetValue(id, out var name) ? new StateData { Name = name, Data = StateDataFor(id) } : null);
            connection.Setup(x => x.GetJobParameter(It.IsAny<string>(), Parameter)).Returns<string, string>((id, _) =>
                _parameters.TryGetValue(id, out var value) ? value : null);
            connection.Setup(x => x.SetJobParameter(It.IsAny<string>(), Parameter, It.IsAny<string>()))
                .Callback<string, string, string>((id, _, value) => _parameters[id] = value);
        }

        private readonly Dictionary<string, Dictionary<string, string>> _stateData = new Dictionary<string, Dictionary<string, string>>();

        private Dictionary<string, string> StateDataFor(string id)
        {
            return _stateData.TryGetValue(id, out var data) ? data : new Dictionary<string, string>();
        }

        private FanInContinuationsSupportAttribute Create()
        {
            return new FanInContinuationsSupportAttribute(new HashSet<string> { SucceededState.StateName, DeletedState.StateName }, _stateChanger.Object);
        }

        [Fact]
        public void Ctor_ValidatesArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new FanInContinuationsSupportAttribute(null, _stateChanger.Object));
            Assert.Throws<ArgumentNullException>(() => new FanInContinuationsSupportAttribute(new HashSet<string>(), null));
            Assert.Equal(1001, Create().Order);
        }

        [Fact]
        public void OnStateElection_ChildBranch_RegistersTheChildOnEveryParent()
        {
            _context.ApplyContext.BackgroundJob.Id = "child";
            _context.ApplyContext.NewStateObject = new AwaitingJobsState(new[] { "p1", "p2" });

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
            _context.ApplyContext.NewStateObject = new AwaitingJobsState(new[] { "p1" });

            Create().OnStateElection(_context.Object);

            Assert.Equal(new[] { "child" }, FanInContinuationsSupportAttribute.Parse(_parameters["p1"]));
            _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("p1", Parameter, It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void OnStateElection_ChildBranch_Throws_WhenAParentDoesNotExist()
        {
            _context.ApplyContext.BackgroundJob.Id = "child";
            _context.ApplyContext.NewStateObject = new AwaitingJobsState(new[] { "missing-parent" });

            Assert.Throws<InvalidOperationException>(() => Create().OnStateElection(_context.Object));
        }

        [Fact]
        public void OnStateElection_ChildBranch_StartsImmediately_WhenAllParentsAlreadySucceeded()
        {
            _states["p1"] = SucceededState.StateName;
            _states["p2"] = SucceededState.StateName;
            _context.ApplyContext.BackgroundJob.Id = "child";
            _context.ApplyContext.NewStateObject = new AwaitingJobsState(new[] { "p1", "p2" }, new EnqueuedState("critical"), JobContinuationOptions.OnlyOnSucceededState, FanInMode.All);

            Create().OnStateElection(_context.Object);

            Assert.Equal("critical", Assert.IsType<EnqueuedState>(_context.Object.CandidateState).Queue);
        }

        [Fact]
        public void OnStateElection_ChildBranch_StaysAwaiting_WhileAParentIsRunning()
        {
            _states["p1"] = SucceededState.StateName;
            _states["p2"] = ProcessingState.StateName;
            _context.ApplyContext.BackgroundJob.Id = "child";
            _context.ApplyContext.NewStateObject = new AwaitingJobsState(new[] { "p1", "p2" });

            Create().OnStateElection(_context.Object);

            Assert.IsType<AwaitingJobsState>(_context.Object.CandidateState);
        }

        [Fact]
        public void OnStateElection_ChildBranch_DeletesTheChild_WhenAParentWasDeleted_InAllMode()
        {
            _states["p1"] = DeletedState.StateName;
            _states["p2"] = ProcessingState.StateName;
            _context.ApplyContext.BackgroundJob.Id = "child";
            _context.ApplyContext.NewStateObject = new AwaitingJobsState(new[] { "p1", "p2" });

            Create().OnStateElection(_context.Object);

            Assert.Equal("Continuation condition was not met", Assert.IsType<DeletedState>(_context.Object.CandidateState).Reason);
        }

        [Fact]
        public void OnStateElection_ChildBranch_StartsOnTheFirstSuccess_InAnyMode()
        {
            _states["p1"] = ProcessingState.StateName;
            _states["p2"] = SucceededState.StateName;
            _context.ApplyContext.BackgroundJob.Id = "child";
            _context.ApplyContext.NewStateObject = new AwaitingJobsState(new[] { "p1", "p2" }, new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.Any);

            Create().OnStateElection(_context.Object);

            Assert.IsType<EnqueuedState>(_context.Object.CandidateState);
        }

        [Fact]
        public void OnStateElection_ParentBranch_StartsTheChild_WhenItWasTheLastParent()
        {
            var awaiting = new AwaitingJobsState(new[] { "parent", "other" });
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
            var awaiting = new AwaitingJobsState(new[] { "parent", "other" });
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
            var awaiting = new AwaitingJobsState(new[] { "parent", "other" });
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
            var awaiting = new AwaitingJobsState(new[] { "parent", "missing-other" }, new EnqueuedState(), JobContinuationOptions.OnAnyFinishedState, FanInMode.All);
            _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "child" });
            _states["child"] = AwaitingState.StateName;
            _stateData["child"] = awaiting.SerializeData();

            _context.ApplyContext.BackgroundJob.Id = "parent";
            _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

            Create().OnStateElection(_context.Object);

            _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "child" && c.NewState is EnqueuedState)));
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
        public void Evaluate_DecidesFromTheParentStates(FanInMode mode, JobContinuationOptions options, string states, string expected)
        {
            var names = states.Split(',');
            var awaiting = new AwaitingJobsState(new[] { "a", "b" }, new EnqueuedState(), options, mode);

            var decision = Create().Evaluate(awaiting, id => names[id == "a" ? 0 : 1] == "" ? null : names[id == "a" ? 0 : 1]);

            Assert.Equal(expected, decision.ToString());
        }

        [Fact]
        public void ReadChildren_ReturnsTheRegisteredChildren_InOrder()
        {
            _parameters["parent"] = SerializationHelper.Serialize(new List<string> { "c2", "c1" });

            var children = FanInContinuationsSupportAttribute.ReadChildren(_context.ApplyContext.Connection.Object, "parent");

            Assert.Equal(new[] { "c2", "c1" }, children);
        }

        [Fact]
        public void ReadChildren_ReturnsAnEmptyList_WhenTheJobHasNoChildren()
        {
            var children = FanInContinuationsSupportAttribute.ReadChildren(_context.ApplyContext.Connection.Object, "parent");

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
            var exception = Assert.Throws<ArgumentNullException>(() => Create().OnStateElection(null));

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
            var awaiting = new AwaitingJobsState(new[] { "parent" });
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
            var awaiting = new AwaitingJobsState(new[] { "parent" });
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
        public void Parse_HandlesNullAndMalformedInput()
        {
            Assert.Empty(FanInContinuationsSupportAttribute.Parse(null));
            Assert.Empty(FanInContinuationsSupportAttribute.Parse(""));
            Assert.Equal(new[] { "1", "2" }, FanInContinuationsSupportAttribute.Parse("[\"1\",\"2\"]"));
        }
    }
}
