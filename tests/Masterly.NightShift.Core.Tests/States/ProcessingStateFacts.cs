namespace Masterly.NightShift.Core.Tests.States;

public class ProcessingStateFacts
{
    private const string WorkerId = "1";
    private const string ServerId = "Server1:4231";

    [Fact]
    public void Ctor_ThrowsAnException_WhenServerNameIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ProcessingState(null, WorkerId));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenServerNameIsEmpty()
    {
        Assert.Throws<ArgumentException>(
            () => new ProcessingState(string.Empty, WorkerId));
    }

    [Fact]
    public void StateName_IsCorrect()
    {
        ProcessingState state = CreateState();
        Assert.Equal(ProcessingState.StateName, state.Name);
    }

    [Fact]
    public void SerializeData_ReturnsCorrectData()
    {
        ProcessingState state = CreateState();

        Dictionary<string, string> data = state.SerializeData();

        Assert.Equal(JobHelper.SerializeDateTime(state.StartedAt), data["StartedAt"]);
        Assert.Equal(ServerId, data["ServerId"]);
        Assert.Equal(WorkerId.ToString(), data["WorkerId"]);
    }

    [Fact]
    public void IsFinal_ReturnsFalse()
    {
        ProcessingState state = CreateState();

        Assert.False(state.IsFinal);
    }

    [DataCompatibilityRangeFact(MaxExcludingLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_Before170()
    {
        ProcessingState state = new("server1", "worker1");

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.ProcessingState, Masterly.NightShift.Core\",\"ServerId\":\"server1\"," +
            "\"WorkerId\":\"worker1\"}",
            serialized);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_After170()
    {
        ProcessingState state = new("server1", "worker1");

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.ProcessingState, Masterly.NightShift.Core\",\"ServerId\":\"server1\"," +
            "\"WorkerId\":\"worker1\"}",
            serialized);
    }

    [Fact]
    public void IgnoreExceptions_ReturnsFalse()
    {
        ProcessingState state = CreateState();

        Assert.False(state.IgnoreJobLoadException);
    }

    private ProcessingState CreateState()
    {
        return new ProcessingState(ServerId, WorkerId);
    }
}
