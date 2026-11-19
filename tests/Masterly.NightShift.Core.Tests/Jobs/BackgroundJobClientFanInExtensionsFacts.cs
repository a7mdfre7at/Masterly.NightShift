using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.Core.Tests;

public class BackgroundJobClientFanInExtensionsFacts
{
    private readonly Mock<IBackgroundJobClient> _client = new();

    public BackgroundJobClientFanInExtensionsFacts()
    {
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("child");
    }

    [Theory]
    [InlineData(FanInMode.AnyFailed)]
    [InlineData(FanInMode.AnyFailedOrCanceled)]
    [InlineData(FanInMode.AllCompleted)]
    [InlineData(FanInMode.Any)]
    public void ContinueJobWithJoin_CreatesTheJobWaitingInTheGivenMode(FanInMode mode)
    {
        string id = _client.Object.ContinueJobWithJoin(["1", null, "2"], mode, () => Method());

        Assert.Equal("child", id);
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(state =>
            state.Mode == mode && state.ParentIds.SequenceEqual(new[] { "1", "2" }) && state.NextState is EnqueuedState)));
    }

    [Fact]
    public void ContinueJobWithJoin_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>("mode", () => _client.Object.ContinueJobWithJoin(["1"], (FanInMode)9, () => Method()));
        Assert.Throws<ArgumentException>(() => _client.Object.ContinueJobWithJoin([null], FanInMode.All, () => Method()));
        Assert.Throws<ArgumentNullException>(() => ((IBackgroundJobClient)null).ContinueJobWithJoin(["1"], FanInMode.All, () => Method()));
    }

    [Fact]
    public void ContinueJobWithJoin_HasOverloadsForEveryKindOfMethod()
    {
        _client.Object.ContinueJobWithJoin(["1"], FanInMode.AllCompleted, () => MethodAsync());
        _client.Object.ContinueJobWithJoin<BackgroundJobClientFanInExtensionsFacts>(["1"], FanInMode.AllCompleted, x => x.Instance());
        _client.Object.ContinueJobWithJoin<BackgroundJobClientFanInExtensionsFacts>(["1"], FanInMode.AllCompleted, x => x.InstanceAsync());

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingJobsState>()), Times.Exactly(3));
    }

    [Fact]
    public void ContinueJobWithAll_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ((IBackgroundJobClient)null).ContinueJobWithAll(["1"], () => Method()));
        Assert.Throws<ArgumentNullException>(() => _client.Object.ContinueJobWithAll(null, () => Method()));
        Assert.Throws<ArgumentException>(() => _client.Object.ContinueJobWithAll([null, ""], () => Method()));
    }

    [Fact]
    public void ContinueJobWithAll_CreatesTheJobInAwaitingJobsState_SkippingNullParents()
    {
        string? id = _client.Object.ContinueJobWithAll(["1", null, "2"], () => Method());

        Assert.Equal("child", id);
        _client.Verify(x => x.Create(
            It.Is<Job>(j => j.Method.Name == "Method"),
            It.Is<AwaitingJobsState>(s => s.ParentIds.Count == 2 && s.Mode == FanInMode.All && s.Options == JobContinuationOptions.OnlyOnSucceededState && s.NextState is EnqueuedState)));
    }

    [Fact]
    public void ContinueJobWithAll_PassesTheNextStateAndOptions()
    {
        _client.Object.ContinueJobWithAll(["1"], () => Method(), new EnqueuedState("critical"), JobContinuationOptions.OnAnyFinishedState);

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s =>
            ((EnqueuedState)s.NextState).Queue == "critical" && s.Options == JobContinuationOptions.OnAnyFinishedState)));
    }

    [Fact]
    public void ContinueJobWithAny_UsesTheAnyMode_ForAllOverloads()
    {
        _client.Object.ContinueJobWithAny(["1"], () => Method());
        _client.Object.ContinueJobWithAny(["1"], () => MethodAsync());
        _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.Instance());
        _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.InstanceAsync());

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.Any)), Times.Exactly(4));
    }

    [Fact]
    public void ContinueJobWithAll_SupportsGenericAndAsyncOverloads()
    {
        _client.Object.ContinueJobWithAll(["1"], () => MethodAsync());
        _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.Instance());
        _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.InstanceAsync());

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.All)), Times.Exactly(3));
    }

    [Fact]
    public void ContinueJobWithAll_PassesTheNextState_ForEveryShape()
    {
        EnqueuedState nextState = new("critical");

        _client.Object.ContinueJobWithAll(["1"], () => MethodAsync(), nextState);
        _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.Instance(), nextState);
        _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.InstanceAsync(), nextState, JobContinuationOptions.OnAnyFinishedState);

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.All && ReferenceEquals(s.NextState, nextState))), Times.Exactly(3));
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Options == JobContinuationOptions.OnAnyFinishedState)), Times.Once);
    }

    [Fact]
    public void ContinueJobWithAny_PassesTheNextState_ForEveryShape()
    {
        EnqueuedState nextState = new("critical");

        _client.Object.ContinueJobWithAny(["1"], () => Method(), nextState);
        _client.Object.ContinueJobWithAny(["1"], () => MethodAsync(), nextState);
        _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.Instance(), nextState);
        _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(["1"], x => x.InstanceAsync(), nextState, JobContinuationOptions.OnAnyFinishedState);

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.Any && ReferenceEquals(s.NextState, nextState))), Times.Exactly(4));
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => s.Options == JobContinuationOptions.OnAnyFinishedState)), Times.Once);
    }

    [Fact]
    public void ContinueJobWithAll_WithAQueue_CreatesTheJobForThatQueue_ForEveryShape()
    {
        _client.Object.ContinueJobWithAll(["1"], "critical", () => Method());
        _client.Object.ContinueJobWithAll(["1"], "critical", () => MethodAsync());
        _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(["1"], "critical", x => x.Instance());
        _client.Object.ContinueJobWithAll<BackgroundJobClientFanInExtensionsFacts>(["1"], "critical", x => x.InstanceAsync());

        _client.Verify(x => x.Create(
            It.Is<Job>(job => job.Queue == "critical"),
            It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.All && s.NextState is EnqueuedState && s.Options == JobContinuationOptions.OnlyOnSucceededState)), Times.Exactly(4));
    }

    [Fact]
    public void ContinueJobWithAny_WithAQueue_CreatesTheJobForThatQueue_ForEveryShape()
    {
        ScheduledState nextState = new(TimeSpan.FromMinutes(5));

        _client.Object.ContinueJobWithAny(["1"], "critical", () => Method());
        _client.Object.ContinueJobWithAny(["1"], "critical", () => MethodAsync());
        _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(["1"], "critical", x => x.Instance());
        _client.Object.ContinueJobWithAny<BackgroundJobClientFanInExtensionsFacts>(["1"], "critical", x => x.InstanceAsync(), nextState, JobContinuationOptions.OnAnyFinishedState);

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Queue == "critical"), It.Is<AwaitingJobsState>(s => s.Mode == FanInMode.Any)), Times.Exactly(4));
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingJobsState>(s => ReferenceEquals(s.NextState, nextState) && s.Options == JobContinuationOptions.OnAnyFinishedState)), Times.Once);
    }

    [Fact]
    public void ContinueJobWithAllAndAny_WithAQueue_ValidateArguments()
    {
        Assert.Throws<ArgumentNullException>(() => _client.Object.ContinueJobWithAll(["1"], null, () => Method()));
        Assert.Throws<ArgumentNullException>(() => _client.Object.ContinueJobWithAny(["1"], null, () => Method()));
        Assert.Throws<ArgumentNullException>(() => BackgroundJobClientFanInExtensions.ContinueJobWithAll(null, ["1"], "critical", () => Method()));
        Assert.Throws<ArgumentException>(() => _client.Object.ContinueJobWithAny([null], "critical", () => Method()));
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
