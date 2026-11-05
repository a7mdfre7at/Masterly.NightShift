using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Moq;
using Xunit;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests
{
    public class BackgroundJobClientExtensionsFacts
    {
        private const string JobId = "job-id";

        private readonly Mock<IBackgroundJobClient> _client;
        private readonly Mock<IState> _state;

        public BackgroundJobClientExtensionsFacts()
        {
            _client = new Mock<IBackgroundJobClient>();
            _state = new Mock<IState>();
        }

        [Fact]
        public void StaticCreate_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Create(
                    null, () => StaticMethod(), _state.Object));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void StaticCreate_ShouldCreateAJobInTheGivenState()
        {
            _client.Object.Create(() => StaticMethod(), _state.Object);
            
            _client.Verify(x => x.Create(It.IsNotNull<Job>(), _state.Object));
        }

        [Fact]
        public void InstanceCreate_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Create<BackgroundJobClientExtensionsFacts>(
                    null, x => x.InstanceMethod(), _state.Object));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void InstanceCreate_ShouldCreateAJobInTheGivenState()
        {
            _client.Object.Create<BackgroundJobClientExtensionsFacts>(x => x.InstanceMethod(), _state.Object);

            _client.Verify(x => x.Create(It.IsNotNull<Job>(), _state.Object));
        }

        [Fact]
        public void StaticEnqueue_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Enqueue(
                    null, () => StaticMethod()));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void StaticEnqueue_ShouldCreateAJobInTheEnqueueState()
        {
            _client.Object.Enqueue(() => StaticMethod());

            _client.Verify(x => x.Create(It.IsNotNull<Job>(), It.IsAny<EnqueuedState>()));
        }

        [Fact]
        public void InstanceEnqueue_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Enqueue<BackgroundJobClientExtensionsFacts>(
                    null, x => x.InstanceMethod()));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void InstanceEnqueue_ShouldCreateAJobInTheEnqueuedState()
        {
            _client.Object.Enqueue<BackgroundJobClientExtensionsFacts>(x => x.InstanceMethod());

            _client.Verify(x => x.Create(It.IsNotNull<Job>(), It.IsAny<EnqueuedState>()));
        }

        [Fact]
        public void StaticSchedule_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule(
                    null, () => StaticMethod(), TimeSpan.FromDays(1)));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void StaticSchedule_ShouldCreateAJobInTheScheduledState()
        {
            _client.Object.Schedule(() => StaticMethod(), TimeSpan.FromDays(1));

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow)));
        }

        [Fact]
        public void StaticSchedule_WithDateTimeOffset_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule(
                    null, () => StaticMethod(), DateTimeOffset.UtcNow));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void StaticSchedule_WithDateTimeOffset_ShouldCreateAJob_InTheScheduledState()
        {
            var now = DateTimeOffset.Now;

            _client.Object.Schedule(() => StaticMethod(), now);

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.Is<ScheduledState>(state => state.EnqueueAt == now.UtcDateTime)));
        }

        [Fact]
        public void InstanceSchedule_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule<BackgroundJobClientExtensionsFacts>(
                    null, x => x.InstanceMethod(), TimeSpan.FromDays(1)));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void InstanceSchedule_ShouldCreateAJobInTheScheduledState()
        {
            _client.Object.Schedule<BackgroundJobClientExtensionsFacts>(
                x => x.InstanceMethod(), TimeSpan.FromDays(1));

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow)));
        }

        [Fact]
        public void InstanceSchedule_WithDateTimeOffset_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule<BackgroundJobClientExtensionsFacts>(
                    null, x => x.InstanceMethod(), DateTimeOffset.UtcNow));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void InstanceSchedule_WithDateTimeOffset_ShouldCreateAJobInTheScheduledState()
        {
            var now = DateTimeOffset.Now;

            _client.Object.Schedule<BackgroundJobClientExtensionsFacts>(
                x => x.InstanceMethod(),
                now);

            _client.Verify(x => x.Create(
                It.IsNotNull<Job>(),
                It.Is<ScheduledState>(state => state.EnqueueAt == now.UtcDateTime)));
        }

        [Fact]
        public void ChangeState_WithoutFromState_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.ChangeState(null, "job-id", _state.Object));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void ChangeState_WithoutFromState_CallsItsOverload()
        {
            _client.Object.ChangeState("job-id", _state.Object);

            _client.Verify(x => x.ChangeState("job-id", _state.Object, null));
        }

        [Fact]
        public void Delete_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Delete(null, JobId));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void Delete_ChangesTheStateOfAJob_ToDeleted()
        {
            _client.Object.Delete(JobId);

            _client.Verify(x => x.ChangeState(
                JobId,
                It.IsAny<DeletedState>(),
                null));
        }

        [Fact]
        public void Delete_WithFromState_ChangesTheStateOfAJob_ToDeletedWithFromStateValue()
        {
            _client.Object.Delete(JobId, FailedState.StateName);

            _client.Verify(x => x.ChangeState(
                JobId,
                It.IsAny<DeletedState>(),
                FailedState.StateName));
        }

        [Fact]
        public void Requeue_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Requeue(null, JobId, FailedState.StateName));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void Requeue_ChangesTheStateOfAJob_ToEnqueued()
        {
            _client.Object.Requeue(JobId);

            _client.Verify(x => x.ChangeState(JobId, It.IsAny<EnqueuedState>(), null));
        }

        [Fact]
        public void Requeue_WithFromState_ChangesTheStateOfAJob_ToEnqueued_FromTheGivenState()
        {
            _client.Object.Requeue(JobId, FailedState.StateName);

            _client.Verify(x => x.ChangeState(JobId, It.IsAny<EnqueuedState>(), FailedState.StateName));
        }

        [Fact]
        public void Reschedule_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
              () => BackgroundJobClientExtensions.Reschedule(null, JobId, TimeSpan.FromDays(1), FailedState.StateName));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void Reschedule_ChangesTheStateOfAJob_ToScheduled()
        {
            _client.Object.Reschedule(JobId, TimeSpan.FromDays(1));

            _client.Verify(x => x.ChangeState(JobId, It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow), null));
        }

        [Fact]
        public void Reschedule_WithFromState_ChangesTheStateOfAJob_ToScheduled_FromTheGivenState()
        {
            _client.Object.Reschedule(JobId, TimeSpan.FromDays(1), FailedState.StateName);

            _client.Verify(x => x.ChangeState(JobId,  It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow), FailedState.StateName));
        }

        [Fact]
        public void Reschedule_WithDateTimeOffset_ChangesTheStateOfAJob_ToScheduled()
        {
            var now = DateTimeOffset.Now;

            _client.Object.Reschedule(JobId, now);

            _client.Verify(x => x.ChangeState(JobId, It.Is<ScheduledState>(state => state.EnqueueAt == now.UtcDateTime), null));
        }

        [Fact]
        public void Reschedule_WithDateTimeOffset_WithFromState_ChangesTheStateOfAJob_ToScheduled_FromTheGivenState()
        {
            var now = DateTimeOffset.Now;

            _client.Object.Reschedule(JobId, now, FailedState.StateName);

            _client.Verify(x => x.ChangeState(JobId,  It.Is<ScheduledState>(state => state.EnqueueAt == now.UtcDateTime), FailedState.StateName));
        }

        [Fact]
        public void StaticSchedule_WithPhrase_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule(null, () => StaticMethod(), "in 2 hours"));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void StaticSchedule_WithPhrase_ThrowsAnException_WhenPhraseIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => _client.Object.Schedule(() => StaticMethod(), (string)null));

            Assert.Equal("when", exception.ParamName);
        }

        [Fact]
        public void StaticSchedule_WithPhrase_ThrowsFormatException_AndCreatesNothing_WhenPhraseIsNotUnderstood()
        {
            Assert.Throws<FormatException>(() => _client.Object.Schedule(() => StaticMethod(), "some day maybe"));

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        }

        [Fact]
        public void StaticSchedule_WithPhrase_CreatesAJobInTheScheduledState_AtTheParsedMoment()
        {
            var before = DateTime.UtcNow;

            _client.Object.Schedule(() => StaticMethod(), "in 2 hours");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(StaticMethod)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddHours(2) && state.EnqueueAt <= after.AddHours(2))));
        }

        [Fact]
        public void StaticSchedule_WithPhrase_ReturnsTheCreatedJobId()
        {
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns(JobId);

            var result = _client.Object.Schedule(() => StaticMethod(), "in 2 hours");

            Assert.Equal(JobId, result);
        }

        [Fact]
        public void StaticSchedule_WithPhrase_ReturnsNull_WhenCreationIsCanceled()
        {
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns((string)null);

            var result = _client.Object.Schedule(() => StaticMethod(), "in 2 hours");

            Assert.Null(result);
        }

        [Fact]
        public void StaticAsyncSchedule_WithPhrase_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule(null, () => StaticMethodAsync(), "in 2 hours"));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void StaticAsyncSchedule_WithPhrase_ThrowsAnException_WhenPhraseIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => _client.Object.Schedule(() => StaticMethodAsync(), (string)null));

            Assert.Equal("when", exception.ParamName);
        }

        [Fact]
        public void StaticAsyncSchedule_WithPhrase_CreatesAJobInTheScheduledState_AtTheParsedMoment()
        {
            var before = DateTime.UtcNow;

            _client.Object.Schedule(() => StaticMethodAsync(), "in 3 days");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Method.Name == nameof(StaticMethodAsync)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddDays(3) && state.EnqueueAt <= after.AddDays(3))));
        }

        [Fact]
        public void InstanceSchedule_WithPhrase_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule<BackgroundJobClientExtensionsFacts>(
                    null, x => x.InstanceMethod(), "in 2 hours"));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void InstanceSchedule_WithPhrase_ThrowsAnException_WhenPhraseIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => _client.Object.Schedule<BackgroundJobClientExtensionsFacts>(x => x.InstanceMethod(), (string)null));

            Assert.Equal("when", exception.ParamName);
        }

        [Fact]
        public void InstanceSchedule_WithPhrase_CreatesAJobInTheScheduledState_AtTheParsedMoment()
        {
            var before = DateTime.UtcNow;

            _client.Object.Schedule<BackgroundJobClientExtensionsFacts>(x => x.InstanceMethod(), "in 90 seconds");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobClientExtensionsFacts) && job.Method.Name == nameof(InstanceMethod)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddSeconds(90) && state.EnqueueAt <= after.AddSeconds(90))));
        }

        [Fact]
        public void InstanceAsyncSchedule_WithPhrase_ThrowsAnException_WhenClientIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => BackgroundJobClientExtensions.Schedule<BackgroundJobClientExtensionsFacts>(
                    null, x => x.InstanceMethodAsync(), "in 2 hours"));

            Assert.Equal("client", exception.ParamName);
        }

        [Fact]
        public void InstanceAsyncSchedule_WithPhrase_ThrowsAnException_WhenPhraseIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => _client.Object.Schedule<BackgroundJobClientExtensionsFacts>(x => x.InstanceMethodAsync(), (string)null));

            Assert.Equal("when", exception.ParamName);
        }

        [Fact]
        public void InstanceAsyncSchedule_WithPhrase_CreatesAJobInTheScheduledState_AtTheParsedMoment()
        {
            var before = DateTime.UtcNow;

            _client.Object.Schedule<BackgroundJobClientExtensionsFacts>(x => x.InstanceMethodAsync(), "in 1 week");

            var after = DateTime.UtcNow;
            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Type == typeof(BackgroundJobClientExtensionsFacts) && job.Method.Name == nameof(InstanceMethodAsync)),
                It.Is<ScheduledState>(state => state.EnqueueAt >= before.AddDays(7) && state.EnqueueAt <= after.AddDays(7))));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        public static void StaticMethod()
        {
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        public static Task StaticMethodAsync()
        {
            return Task.CompletedTask;
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
        public Task InstanceMethodAsync()
        {
            return Task.CompletedTask;
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
        public void InstanceMethod()
        {
        }
    }
}
