namespace Masterly.NightShift.Core.Tests.States;

public class EnqueuedStateFacts
{
    [Fact]
    public void StateName_IsCorrect()
    {
        EnqueuedState state = new();
        Assert.Equal(EnqueuedState.StateName, state.Name);
    }

    [Fact]
    public void Ctor_ShouldSetQueue_WhenItWasGiven()
    {
        EnqueuedState state = new("critical");
        Assert.Equal("critical", state.Queue);
    }

    [Fact]
    public void SetQueue_ThrowsAnException_WhenQueueValueIsEmpty()
    {
        EnqueuedState state = new();
        Assert.Throws<ArgumentNullException>(() => state.Queue = string.Empty);
    }

    [Fact]
    public void SetQueue_ThrowsAnException_WhenValueIsNotInAGivenFormat()
    {
        EnqueuedState state = new();

        Assert.Throws<ArgumentException>(() => state.Queue = "UppercaseLetters");
        Assert.Throws<ArgumentException>(() => state.Queue = "punctuation:un-allowed");
        Assert.Throws<ArgumentException>(() => state.Queue = "моя_твоя_непонимать");
    }

    [Fact]
    public void SetQueue_DoesNotThrowException_WhenValueIsInACorrectFormat()
    {
        EnqueuedState state = new()
        {
            // Does not throw
            Queue = "lowercasedcharacters"
        };
        state.Queue = "underscores_allowed";
        state.Queue = "1234567890_allowed";
    }

    [Fact]
    public void SerializeData_ReturnsCorrectData()
    {
        EnqueuedState state = new();

        Dictionary<string, string> serializedData = state.SerializeData();

        Assert.Equal(state.Queue, serializedData["Queue"]);
        Assert.Equal(JobHelper.SerializeDateTime(state.EnqueuedAt), serializedData["EnqueuedAt"]);
    }

    [Fact]
    public void IsFinal_ReturnsFalse()
    {
        EnqueuedState state = new();

        Assert.False(state.IsFinal);
    }

    [Fact]
    public void IgnoreExceptions_ReturnsFalse()
    {
        EnqueuedState state = new();

        Assert.False(state.IgnoreJobLoadException);
    }

    [DataCompatibilityRangeFact(MaxExcludingLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_Before170()
    {
        EnqueuedState state = new("default");

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.EnqueuedState, Masterly.NightShift.Core\",\"Queue\":\"default\"}",
            serialized);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_After170()
    {
        EnqueuedState state = new("default");

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.EnqueuedState, Masterly.NightShift.Core\",\"Queue\":\"default\"}",
            serialized);
    }

    [DataCompatibilityRangeFact]
    public void JsonDeserialize_CanHandlePreviousFormat()
    {
        string json = "{\"Queue\":\"critical\",\"EnqueuedAt\":\"2012-04-02T11:22:33.0000000Z\",\"Name\":\"Enqueued\",\"Reason\":\"hello\",\"IsFinal\":false,\"IgnoreJobLoadException\":false}";
        EnqueuedState? state = SerializationHelper.Deserialize<EnqueuedState>(json);

        Assert.Equal("critical", state.Queue);
        Assert.Equal("hello", state.Reason);
    }

    [DataCompatibilityRangeFact]
    public void JsonDeserialize_CanHandleNewFormat()
    {
        string json = "{\"$type\":\"Masterly.NightShift.States.EnqueuedState, Masterly.NightShift.Core\",\"Queue\":\"default\"}";
        EnqueuedState? state = SerializationHelper.Deserialize<EnqueuedState>(json);

        Assert.Equal("default", state.Queue);
        Assert.Null(state.Reason);
    }

    [DataCompatibilityRangeFact]
    public void JsonDeserialize_CanHandle170Beta1Format_WithDefaultValueForQueue()
    {
        string json = "{\"$type\":\"Masterly.NightShift.States.EnqueuedState, Masterly.NightShift.Core\"}";
        EnqueuedState? state = SerializationHelper.Deserialize<EnqueuedState>(json);

        Assert.Equal("default", state.Queue);
        Assert.Null(state.Reason);
    }
}
