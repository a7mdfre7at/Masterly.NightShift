namespace Masterly.NightShift.Core.Tests.States;

public class AwaitingJobsStateFacts
{
    [Fact]
    public void Ctor_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new AwaitingJobsState(null));
        Assert.Throws<ArgumentNullException>(() => new AwaitingJobsState(["1"], null, JobContinuationOptions.OnAnyFinishedState, FanInMode.All));
        Assert.Throws<ArgumentException>(() => new AwaitingJobsState([]));
        Assert.Throws<ArgumentException>(() => new AwaitingJobsState(["", " ", null]));
    }

    [Fact]
    public void Ctor_RemovesDuplicatesAndBlanks_AndKeepsOrder()
    {
        var state = new AwaitingJobsState(["2", "1", "2", "", "3"]);

        Assert.Equal(["2", "1", "3"], state.ParentIds);
        Assert.IsType<EnqueuedState>(state.NextState);
        Assert.Equal(JobContinuationOptions.OnlyOnSucceededState, state.Options);
        Assert.Equal(FanInMode.All, state.Mode);
    }

    [Fact]
    public void SharesTheAwaitingStateName_AndIsNotFinal()
    {
        var state = new AwaitingJobsState(["1"]);

        Assert.Equal(AwaitingState.StateName, state.Name);
        Assert.False(state.IsFinal);
        Assert.False(state.IgnoreJobLoadException);
    }

    [Fact]
    public void SerializeData_ContainsParentsModeOptionsAndNextState()
    {
        var state = new AwaitingJobsState(["1", "2"], new ScheduledState(TimeSpan.FromMinutes(5)), JobContinuationOptions.OnAnyFinishedState, FanInMode.Any);

        var data = state.SerializeData();

        Assert.Equal("1", data["ParentId"]);
        Assert.Equal("1,2", data["ParentIds"]);
        Assert.Equal("Any", data["Mode"]);
        Assert.Equal(((int)JobContinuationOptions.OnAnyFinishedState).ToString(), data["Options"]);
        Assert.IsType<ScheduledState>(SerializationHelper.Deserialize<IState>(data["NextState"], SerializationOption.TypedInternal));
    }

    [Fact]
    public void FromStateData_RoundTrips()
    {
        var original = new AwaitingJobsState(["a", "b", "c"], new EnqueuedState("critical"), JobContinuationOptions.OnlyOnDeletedState, FanInMode.Any);

        var restored = AwaitingJobsState.FromStateData(original.SerializeData());

        Assert.NotNull(restored);
        Assert.Equal(original.ParentIds, restored.ParentIds);
        Assert.Equal(FanInMode.Any, restored.Mode);
        Assert.Equal(JobContinuationOptions.OnlyOnDeletedState, restored.Options);
        Assert.Equal("critical", Assert.IsType<EnqueuedState>(restored.NextState).Queue);
    }

    [Fact]
    public void FromStateData_ReturnsNull_ForOrdinaryAwaitingStateData()
    {
        var data = new AwaitingState("parent").SerializeData();

        Assert.Null(AwaitingJobsState.FromStateData(data));
        Assert.Null(AwaitingJobsState.FromStateData(new Dictionary<string, string>()));
    }
}
