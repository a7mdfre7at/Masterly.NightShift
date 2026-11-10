namespace Masterly.NightShift.Core.Tests.States;

public class DeletedStateFacts
{
    [Fact]
    public void StateName_ReturnsDeleted()
    {
        string result = DeletedState.StateName;
        Assert.Equal("Deleted", result);
    }

    [Fact]
    public void NameProperty_ReturnsStateName()
    {
        DeletedState state = CreateState();

        string result = state.Name;

        Assert.Equal(DeletedState.StateName, result);
    }

    [Fact]
    public void IsFinalProperty_ReturnsTrue()
    {
        DeletedState state = CreateState();

        bool result = state.IsFinal;

        Assert.True(result);
    }

    [Fact]
    public void IgnoreExceptions_ReturnsTrue()
    {
        DeletedState state = CreateState();

        bool result = state.IgnoreJobLoadException;

        Assert.True(result);
    }

    [Fact]
    public void DeletedAtProperty_ReturnsCurrentUtcDate()
    {
        DeletedState state = CreateState();

        Assert.True(DateTime.UtcNow.AddMinutes(-1) < state.DeletedAt);
        Assert.True(state.DeletedAt < DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void SerializeData_ReturnsSerializedStateData()
    {
        DeletedState state = CreateState();

        Dictionary<string, string> data = state.SerializeData();

        Assert.Single(data);
        Assert.True(JobHelper.DeserializeDateTime(data["DeletedAt"]) != default);
    }

    [DataCompatibilityRangeFact(MaxExcludingLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_Before170()
    {
        DeletedState state = new();

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.DeletedState, Masterly.NightShift.Core\"}",
            serialized);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_After170()
    {
        DeletedState state = new();

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.DeletedState, Masterly.NightShift.Core\"}",
            serialized);
    }

    private static DeletedState CreateState()
    {
        return new DeletedState();
    }
}
