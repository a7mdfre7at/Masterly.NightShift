using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Moq;
using Xunit;

// ReSharper disable PossibleNullReferenceException

namespace Masterly.NightShift.Core.Tests
{
    public class BackgroundJobFacts
    {
        private readonly Mock<IBackgroundJobClient> _client;

        public BackgroundJobFacts()
        {
            _client = new Mock<IBackgroundJobClient>();
        }
        
        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Enqueue_CreatesAJobInEnqueuedState()
        {
            Initialize();

            BackgroundJob.Enqueue(() => Method());

            _client.Verify(x => x.Create(It.IsNotNull<Job>(), It.IsAny<EnqueuedState>()));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void EnqueueGeneric_CreatesAJobInEnqueuedState()
        {
            Initialize();

            BackgroundJob.Enqueue<BackgroundJobFacts>(x => x.Method());

            _client.Verify(x => x.Create(It.IsNotNull<Job>(), It.IsAny<EnqueuedState>()));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Schedule_WithTimeSpan_CreatesAJobInScheduledState()
        {
            Initialize();

            BackgroundJob.Schedule(() => Method(), TimeSpan.FromDays(1));

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Schedule_WithDateTimeOffset_CreatesAJobInScheduledState()
        {
            Initialize();

            BackgroundJob.Schedule(() => Method(), DateTimeOffset.Now);

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.IsNotNull<ScheduledState>()));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ScheduleGeneric_WithTimeSpan_CreatesAJobInScheduledState()
        {
            Initialize();

            BackgroundJob.Schedule<BackgroundJobFacts>(x => Method(), TimeSpan.FromDays(1));

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ScheduleGeneric_WithDateTimeOffset_CreatesAJobInScheduledState()
        {
            Initialize();

            BackgroundJob.Schedule<BackgroundJobFacts>(x => x.Method(), DateTimeOffset.Now);

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.IsNotNull<ScheduledState>()));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Schedule_WithPhrase_CreatesAJobInScheduledState_AtTheParsedMoment()
        {
            Initialize();
            var before = DateTime.UtcNow;

            BackgroundJob.Schedule(() => Method(), "in 2 hours");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(Method)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddHours(2) && state.EnqueueAt <= after.AddHours(2))));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Schedule_WithPhrase_ReturnsTheCreatedJobId()
        {
            Initialize();
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("new-id");

            var jobId = BackgroundJob.Schedule(() => Method(), "in 20 minutes");

            Assert.Equal("new-id", jobId);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Schedule_WithPhrase_ReturnsNull_WhenCreationIsCanceled()
        {
            Initialize();
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns((string)null);

            var jobId = BackgroundJob.Schedule(() => Method(), "in 20 minutes");

            Assert.Null(jobId);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Schedule_WithPhrase_ThrowsFormatException_AndCreatesNothing_WhenThePhraseIsNotUnderstood()
        {
            Initialize();

            Assert.Throws<FormatException>(() => BackgroundJob.Schedule(() => Method(), "whenever you like"));

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void Schedule_WithPhrase_ThrowsArgumentNullException_WhenThePhraseIsNull()
        {
            Initialize();

            var exception = Assert.Throws<ArgumentNullException>(() => BackgroundJob.Schedule(() => Method(), (string)null));

            Assert.Equal("when", exception.ParamName);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ScheduleAsync_WithPhrase_CreatesAJobInScheduledState_AtTheParsedMoment()
        {
            Initialize();
            var before = DateTime.UtcNow;

            BackgroundJob.Schedule(() => MethodAsync(), "in 3 days");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(MethodAsync)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddDays(3) && state.EnqueueAt <= after.AddDays(3))));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ScheduleGeneric_WithPhrase_CreatesAJobInScheduledState_AtTheParsedMoment()
        {
            Initialize();
            var before = DateTime.UtcNow;

            BackgroundJob.Schedule<BackgroundJobFacts>(x => x.Method(), "in 90 seconds");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobFacts) && job.Method.Name == nameof(Method)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddSeconds(90) && state.EnqueueAt <= after.AddSeconds(90))));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ScheduleGenericAsync_WithPhrase_CreatesAJobInScheduledState_AtTheParsedMoment()
        {
            Initialize();
            var before = DateTime.UtcNow;

            BackgroundJob.Schedule<BackgroundJobFacts>(x => x.MethodAsync(), "in 1 week");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobFacts) && job.Method.Name == nameof(MethodAsync)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddDays(7) && state.EnqueueAt <= after.AddDays(7))));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAll_CreatesAnAwaitingJob_ThatWaitsForEveryParent()
        {
            Initialize();
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("child");

            var jobId = BackgroundJob.ContinueJobWithAll(new[] { "p1", "p2" }, () => Method());

            Assert.Equal("child", jobId);
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(Method)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1", "p2" }) &&
                    state.Mode == FanInMode.All &&
                    state.Options == JobContinuationOptions.OnlyOnSucceededState &&
                    state.NextState is EnqueuedState)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAll_ReturnsNull_WhenCreationIsCanceled()
        {
            Initialize();
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns((string)null);

            var jobId = BackgroundJob.ContinueJobWithAll(new[] { "p1" }, () => Method());

            Assert.Null(jobId);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAll_ThrowsArgumentNullException_WhenParentIdsAreNull()
        {
            Initialize();

            var exception = Assert.Throws<ArgumentNullException>(() => BackgroundJob.ContinueJobWithAll(null, () => Method()));

            Assert.Equal("parentIds", exception.ParamName);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAll_ThrowsArgumentException_WhenNoParentIdIsGiven()
        {
            Initialize();

            Assert.Throws<ArgumentException>(() => BackgroundJob.ContinueJobWithAll(Array.Empty<string>(), () => Method()));

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAllAsync_PassesTheOptions_AndTheAllMode()
        {
            Initialize();

            BackgroundJob.ContinueJobWithAll(new[] { "p1", "p2" }, () => MethodAsync(), JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(MethodAsync)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1", "p2" }) &&
                    state.Mode == FanInMode.All &&
                    state.Options == JobContinuationOptions.OnAnyFinishedState)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAllGeneric_PassesTheOptions_AndTheAllMode()
        {
            Initialize();

            BackgroundJob.ContinueJobWithAll<BackgroundJobFacts>(new[] { "p1" }, x => x.Method(), JobContinuationOptions.OnlyOnDeletedState);

            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobFacts) && job.Method.Name == nameof(Method)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1" }) &&
                    state.Mode == FanInMode.All &&
                    state.Options == JobContinuationOptions.OnlyOnDeletedState)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAllGenericAsync_PassesTheOptions_AndTheAllMode()
        {
            Initialize();

            BackgroundJob.ContinueJobWithAll<BackgroundJobFacts>(new[] { "p1" }, x => x.MethodAsync(), JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobFacts) && job.Method.Name == nameof(MethodAsync)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1" }) &&
                    state.Mode == FanInMode.All &&
                    state.Options == JobContinuationOptions.OnAnyFinishedState)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAny_CreatesAnAwaitingJob_ThatWaitsForTheFirstParent()
        {
            Initialize();
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("child");

            var jobId = BackgroundJob.ContinueJobWithAny(new[] { "p1", "p2" }, () => Method());

            Assert.Equal("child", jobId);
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(Method)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1", "p2" }) &&
                    state.Mode == FanInMode.Any &&
                    state.Options == JobContinuationOptions.OnlyOnSucceededState &&
                    state.NextState is EnqueuedState)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAny_ReturnsNull_WhenCreationIsCanceled()
        {
            Initialize();
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns((string)null);

            var jobId = BackgroundJob.ContinueJobWithAny(new[] { "p1" }, () => Method());

            Assert.Null(jobId);
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAnyAsync_PassesTheOptions_AndTheAnyMode()
        {
            Initialize();

            BackgroundJob.ContinueJobWithAny(new[] { "p1", "p2" }, () => MethodAsync(), JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(MethodAsync)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1", "p2" }) &&
                    state.Mode == FanInMode.Any &&
                    state.Options == JobContinuationOptions.OnAnyFinishedState)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAnyGeneric_PassesTheOptions_AndTheAnyMode()
        {
            Initialize();

            BackgroundJob.ContinueJobWithAny<BackgroundJobFacts>(new[] { "p1" }, x => x.Method(), JobContinuationOptions.OnlyOnDeletedState);

            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobFacts) && job.Method.Name == nameof(Method)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1" }) &&
                    state.Mode == FanInMode.Any &&
                    state.Options == JobContinuationOptions.OnlyOnDeletedState)));
        }

        [Fact, GlobalLock(Reason = "Access BackgroundJob.ClientFactory member")]
        public void ContinueJobWithAnyGenericAsync_PassesTheOptions_AndTheAnyMode()
        {
            Initialize();

            BackgroundJob.ContinueJobWithAny<BackgroundJobFacts>(new[] { "p1" }, x => x.MethodAsync(), JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobFacts) && job.Method.Name == nameof(MethodAsync)),
                It.Is<AwaitingJobsState>(state =>
                    state.ParentIds.SequenceEqual(new[] { "p1" }) &&
                    state.Mode == FanInMode.Any &&
                    state.Options == JobContinuationOptions.OnAnyFinishedState)));
        }

        [Fact, GlobalLock]
        public void Delete_ChangesStateOfAJobToDeleted()
        {
            Initialize();

            BackgroundJob.Delete("job-id");

            _client.Verify(x => x.ChangeState(
                "job-id",
                It.IsAny<DeletedState>(),
                null));
        }

        [Fact, GlobalLock]
        public void Delete_WithFromState_ChangesStateOfAJobToDeleted_WithFromState()
        {
            Initialize();

            BackgroundJob.Delete("job-id", FailedState.StateName);

            _client.Verify(x => x.ChangeState(
                "job-id",
                It.IsAny<DeletedState>(),
                FailedState.StateName));
        }

        [Fact, GlobalLock]
        public void Requeue_ChangesStateOfAJobToEnqueued()
        {
            Initialize();

            BackgroundJob.Requeue("job-id");

            _client.Verify(x => x.ChangeState(
                "job-id",
                It.IsAny<EnqueuedState>(),
                null));
        }

        [Fact, GlobalLock]
        public void Requeue_WithFromState_ChangesStateOfAJobToEnqueued_WithFromState()
        {
            Initialize();

            BackgroundJob.Requeue("job-id", FailedState.StateName);

            _client.Verify(x => x.ChangeState(
                "job-id",
                It.IsAny<EnqueuedState>(),
                FailedState.StateName));
        }

        [Fact, GlobalLock]
        public void Reschedule_WithTimeSpan_ChangesStateOfAJobToScheduled()
        {
            Initialize();

            BackgroundJob.Reschedule("job-id", TimeSpan.FromDays(1));

            _client.Verify(x => x.ChangeState(
              "job-id",
              It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow),
              null));
        }

        [Fact, GlobalLock]
        public void Reschedule_WithTimeSpan_WithFromState_ChangesStateOfAJobToScheduled_WithFromState()
        {
            Initialize();

            BackgroundJob.Reschedule("job-id", TimeSpan.FromDays(1), FailedState.StateName);

            _client.Verify(x => x.ChangeState(
              "job-id",
              It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow),
              FailedState.StateName));
        }

        [Fact, GlobalLock]
        public void Reschedule_WithDateTimeOffset_ChangesStateOfAJobToScheduled()
        {
            var now = DateTimeOffset.Now;

            Initialize();

            BackgroundJob.Reschedule("job-id", now);

            _client.Verify(x => x.ChangeState(
              "job-id",
              It.Is<ScheduledState>(state => state.EnqueueAt == now.UtcDateTime),
              null));
        }

        [Fact, GlobalLock]
        public void Reschedule_WithDateTimeOffset_WithFromState_ChangesStateOfAJobToScheduled_WithFromState()
        {
            var now = DateTimeOffset.Now;

            Initialize();

            BackgroundJob.Reschedule("job-id", now, FailedState.StateName);

            _client.Verify(x => x.ChangeState(
              "job-id",
              It.Is<ScheduledState>(state => state.EnqueueAt == now.UtcDateTime),
              FailedState.StateName));
        }

        [Fact, GlobalLock(Reason = "Accesses to BJ.ClientFactory, JS.Current")]
        public void ClientFactory_HasDefaultValue_ThatReturns()
        {
            BackgroundJob.ClientFactory = null;
            JobStorage.Current = new Mock<JobStorage>().Object;

            var client = BackgroundJob.ClientFactory();
            Assert.NotNull(client);
        }

        private void Initialize()
        {
            BackgroundJob.ClientFactory = () => _client.Object;
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        public void Method() { }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        public Task MethodAsync() => Task.CompletedTask;
    }
}
