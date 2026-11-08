using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;
using static Masterly.NightShift.ContinuationsSupportAttribute;

namespace Masterly.NightShift.Core.Tests
{
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
            var filter = CreateFilter();
            
            // Act
            filter.OnStateElection(_context.Object);

            // Assert
            _context.ApplyContext.Connection.Verify(x => x.AcquireDistributedLock("job:parent-id:state-lock", It.IsAny<TimeSpan>()));
            _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(
                _parentId,
                "Continuations",
                It.Is<string>(value => SerializationHelper.Deserialize<List<Continuation>>(value)
                    .Contains(new Continuation { JobId = _continuationId, Options = JobContinuationOptions.OnAnyFinishedState}))));
            
            Assert.IsType<AwaitingState>(_context.Object.CandidateState);
        }

        [Fact]
        public void OnStateElection_ChangesCandidateToTheNextState_OnAwaitingCompletedParentJob()
        {
            _context.ApplyContext.Connection.Setup(x => x.GetStateData(_parentId)).Returns(new StateData { Name = SucceededState.StateName });
            var filter = CreateFilter();

            filter.OnStateElection(_context.Object);

            Assert.Equal(_nextState.Object.Name, _context.Object.CandidateState.Name);
        }

        [Fact]
        public void OnStateElection_AddsAnotherContinuationForParentJob_OnAwaitingState()
        {
            // Arrange
            _context.ApplyContext.Connection.Setup(x => x.GetJobParameter(_parentId, "Continuations"))
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = "another-id" } }));

            var filter = CreateFilter();

            // Act
            filter.OnStateElection(_context.Object);

            // Assert
            var expectedList = SerializationHelper.Serialize(new List<Continuation>
            {
                new Continuation { JobId = "another-id", Options = JobContinuationOptions.OnAnyFinishedState },
                new Continuation { JobId = _continuationId, Options = JobContinuationOptions.OnAnyFinishedState }
            });

            _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(_parentId, "Continuations", expectedList));
        }

        [Fact]
        public void OnStateElection_ThrowsAnException_WhenParentJobDoesNotExist()
        {
            _context.ApplyContext.Connection.Setup(x => x.GetJobData(_parentId)).Returns((JobData)null);

            var filter = CreateFilter();

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
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = _continuationId } }));

            var filter = CreateFilter();
            
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
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = _continuationId } }));

            var filter = CreateFilter();

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
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = _continuationId } }));

            var filter = CreateFilter();

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
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = _continuationId } }));

            var filter = CreateFilter();

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
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = _continuationId } }));

            var filter = CreateFilter();

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
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = _continuationId } }));

            var filter = CreateFilter();

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
                .Returns(SerializationHelper.Serialize(new List<Continuation> { new Continuation { JobId = null } }));
            
            var filter = CreateFilter();

            // Act
            filter.OnStateElection(_context.Object);

            // Assert
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
            Assert.Equal("Succeeded", _context.Object.CandidateState.Name);
        }

        [Fact]
        public void OnStateUnapplied_DoesNotThrow()
        {
            var filter = (IApplyStateFilter)CreateFilter();
            // Does not throw
            filter.OnStateUnapplied(null, null);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void HandlesChangingProcessOfInternalDataSerialization()
        {
            SerializationHelper.SetUserSerializerSettings(SerializerSettingsHelper.DangerousSettings);

            var continuationsJson = SerializationHelper.Serialize(new List<Continuation>
            {
                new Continuation {JobId = "1", Options = JobContinuationOptions.OnAnyFinishedState},
                new Continuation {JobId = "3214324", Options = JobContinuationOptions.OnlyOnSucceededState}
            }, SerializationOption.User);

            var continuations = SerializationHelper.Deserialize<List<Continuation>>(continuationsJson);

            Assert.NotNull(continuations);
            Assert.Equal(2, continuations.Count);
            Assert.Equal("1", continuations[0].JobId);
            Assert.Equal(JobContinuationOptions.OnAnyFinishedState, continuations[0].Options);
            Assert.Equal("3214324", continuations[1].JobId);
            Assert.Equal(JobContinuationOptions.OnlyOnSucceededState, continuations[1].Options);
        }

        private ContinuationsSupportAttribute CreateFilter()
        {
            return new ContinuationsSupportAttribute(
                pushResults: false,
                ContinuationsSupportAttribute.KnownFinalStates,
                _stateChanger.Object);
        }
    }
}