using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class BackgroundJobClientFanInExtensionsFacts
    {
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();

        public BackgroundJobClientFanInExtensionsFacts()
        {
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("child");
        }

        [Fact]
        public void ContinueJobWithAll_ValidatesArguments()
        {
            Assert.Throws<ArgumentNullException>(() => ((IBackgroundJobClient)null).ContinueJobWithAll(new[] { "1" }, () => Method()));
            Assert.Throws<ArgumentNullException>(() => _client.Object.ContinueJobWithAll(null, () => Method()));
            Assert.Throws<ArgumentException>(() => _client.Object.ContinueJobWithAll(new string[] { null, "" }, () => Method()));
        }

        [Fact]
        public void ContinueJobWithAll_CreatesTheJobInAwaitingJobsState_SkippingNullParents()
        {
            var id = _client.Object.ContinueJobWithAll(new[] { "1", null, "2" }, () => Method());

            Assert.Equal("child", id);
            _client.Verify(x => x.Create(
                It.Is<Job>(j => j.Method.Name == "Method"),
                It.Is<AwaitingJobsState>(s => s.ParentIds.Count == 2 && s.Mode == FanInMode.All && s.Options == JobContinuationOptions.OnlyOnSucceededState && s.NextState is EnqueuedState)));
        }

        [Fact]
        public void ContinueJobWithAll_PassesTheNextStateAndOptions()
        {
            _client.Object.ContinueJobWithAll(new[] { "1" }, () => Method(), new EnqueuedState("critical"), JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s =>
                ((EnqueuedState)s.NextState).Queue == "critical" && s.Options == JobContinuationOptions.OnAnyFinishedState)));
        }

        [Fact]
        public void ContinueJobWithAny_UsesTheAnyMode_ForAllOverloads()
        {
            _client.Object.ContinueJobWithAny(new[] { "1" }, () => Method());
            _client.Object.ContinueJobWithAny(new[] { "1" }, () => MethodAsync());
            _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.Instance());
            _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.InstanceAsync());

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.Any)), Times.Exactly(4));
        }

        [Fact]
        public void ContinueJobWithAll_SupportsGenericAndAsyncOverloads()
        {
            _client.Object.ContinueJobWithAll(new[] { "1" }, () => MethodAsync());
            _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.Instance());
            _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.InstanceAsync());

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.All)), Times.Exactly(3));
        }

        [Fact]
        public void ContinueJobWithAll_PassesTheNextState_ForEveryShape()
        {
            var nextState = new EnqueuedState("critical");

            _client.Object.ContinueJobWithAll(new[] { "1" }, () => MethodAsync(), nextState);
            _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.Instance(), nextState);
            _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.InstanceAsync(), nextState, JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.All && s.NextState == nextState)), Times.Exactly(3));
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Options == JobContinuationOptions.OnAnyFinishedState)), Times.Once);
        }

        [Fact]
        public void ContinueJobWithAny_PassesTheNextState_ForEveryShape()
        {
            var nextState = new EnqueuedState("critical");

            _client.Object.ContinueJobWithAny(new[] { "1" }, () => Method(), nextState);
            _client.Object.ContinueJobWithAny(new[] { "1" }, () => MethodAsync(), nextState);
            _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.Instance(), nextState);
            _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, x => x.InstanceAsync(), nextState, JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.Any && s.NextState == nextState)), Times.Exactly(4));
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Options == JobContinuationOptions.OnAnyFinishedState)), Times.Once);
        }

        [Fact]
        public void ContinueJobWithAll_WithAQueue_CreatesTheJobForThatQueue_ForEveryShape()
        {
            _client.Object.ContinueJobWithAll(new[] { "1" }, "critical", () => Method());
            _client.Object.ContinueJobWithAll(new[] { "1" }, "critical", () => MethodAsync());
            _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, "critical", x => x.Instance());
            _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, "critical", x => x.InstanceAsync());

            _client.Verify(x => x.Create(
                It.Is<Job>(job => job.Queue == "critical"),
                It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.All && s.NextState is EnqueuedState && s.Options == JobContinuationOptions.OnlyOnSucceededState)), Times.Exactly(4));
        }

        [Fact]
        public void ContinueJobWithAny_WithAQueue_CreatesTheJobForThatQueue_ForEveryShape()
        {
            var nextState = new ScheduledState(TimeSpan.FromMinutes(5));

            _client.Object.ContinueJobWithAny(new[] { "1" }, "critical", () => Method());
            _client.Object.ContinueJobWithAny(new[] { "1" }, "critical", () => MethodAsync());
            _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, "critical", x => x.Instance());
            _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(new[] { "1" }, "critical", x => x.InstanceAsync(), nextState, JobContinuationOptions.OnAnyFinishedState);

            _client.Verify(x => x.Create(It.Is<Job>(job => job.Queue == "critical"), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.Any)), Times.Exactly(4));
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.NextState == nextState && s.Options == JobContinuationOptions.OnAnyFinishedState)), Times.Once);
        }

        [Fact]
        public void ContinueJobWithAllAndAny_WithAQueue_ValidateArguments()
        {
            Assert.Throws<ArgumentNullException>(() => _client.Object.ContinueJobWithAll(new[] { "1" }, (string)null, () => Method()));
            Assert.Throws<ArgumentNullException>(() => _client.Object.ContinueJobWithAny(new[] { "1" }, (string)null, () => Method()));
            Assert.Throws<ArgumentNullException>(() => BackgroundJobClientFanInExtensions.ContinueJobWithAll(null, new[] { "1" }, "critical", () => Method()));
            Assert.Throws<ArgumentException>(() => _client.Object.ContinueJobWithAny(new string[] { null }, "critical", () => Method()));
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Method() { }
        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static Task MethodAsync() => Task.CompletedTask;
        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public void Instance() { }
        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public Task InstanceAsync() => Task.CompletedTask;
    }
}
