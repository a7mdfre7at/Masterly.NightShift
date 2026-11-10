// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.States;

public class ElectStateContextFacts
{
    private readonly ApplyStateContextMock _applyContext;

    public ElectStateContextFacts()
    {
        _applyContext = new ApplyStateContextMock { OldStateName = "State" };
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenApplyContextIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new ElectStateContext(null));

        Assert.Equal("applyContext", exception.ParamName);
    }

    [Fact]
    public void Ctor_CorrectlySetAllProperties()
    {
        ElectStateContext context = CreateContext();

        Assert.Same(_applyContext.Connection.Object, context.Connection);
        Assert.Same(_applyContext.Transaction.Object, context.Transaction);
        Assert.Same(_applyContext.BackgroundJob.Object, context.BackgroundJob);
        Assert.Same(_applyContext.NewState.Object, context.CandidateState);
        Assert.Equal("State", context.CurrentState);
        Assert.Empty(context.TraversedStates);
        Assert.Null(context.CustomData);
    }

    [Fact]
    public void Ctor_CopiesTheCustomDataDictionary_ToAnotherInstance()
    {
        // Arrange
        _applyContext.CustomData = new Dictionary<string, object>
        {
            { "lalala", new object() }
        };

        // Act
        ElectStateContext context = CreateContext();

        // Assert
        Assert.Equal(_applyContext.CustomData, context.CustomData);
        Assert.NotSame(_applyContext.CustomData, context.CustomData);
    }

    [Fact]
    public void SetCandidateState_ThrowsAnException_WhenValueIsNull()
    {
        ElectStateContext context = CreateContext();

        Assert.Throws<ArgumentNullException>(() => context.CandidateState = null);
    }

    [Fact]
    public void SetCandidateState_SetsTheGivenValue()
    {
        ElectStateContext context = CreateContext();
        Mock<IState> newState = new();

        context.CandidateState = newState.Object;

        Assert.Same(newState.Object, context.CandidateState);
    }

    [Fact]
    public void SetCandidateState_AddsPreviousCandidateState_ToTraversedStatesList()
    {
        ElectStateContext context = CreateContext();
        Mock<IState> state = new();

        context.CandidateState = state.Object;

        Assert.Contains(_applyContext.NewState.Object, context.TraversedStates);
    }

    [Fact]
    public void SetJobParameter_CallsTheCorrespondingMethod_WithJsonEncodedValue()
    {
        ElectStateContext context = CreateContext();

        context.SetJobParameter("Name", "Value");

        _applyContext.Connection.Verify(x => x.SetJobParameter(
            _applyContext.BackgroundJob.Id, "Name", TestJson.ToJson("Value")));
    }

    [Fact]
    public void SetJobParameter_CanReceiveNullValue()
    {
        ElectStateContext context = CreateContext();

        context.SetJobParameter("Name", (string)null);

        _applyContext.Connection.Verify(x => x.SetJobParameter(
            _applyContext.BackgroundJob.Id, "Name", TestJson.ToJson(null)));
    }

    [Fact]
    public void GetJobParameter_CallsTheCorrespondingMethod_WithJsonDecodedValue()
    {
        ElectStateContext context = CreateContext();
        _applyContext.Connection.Setup(x => x.GetJobParameter(_applyContext.BackgroundJob.Id, "Name"))
            .Returns(TestJson.ToJson("Value"));

        string? value = context.GetJobParameter<string>("Name");

        Assert.Equal("Value", value);
    }

    [Fact]
    public void GetJobParameter_ReturnsDefaultValue_WhenNoValueProvided()
    {
        ElectStateContext context = CreateContext();
        _applyContext.Connection.Setup(x => x.GetJobParameter("1", "Value"))
            .Returns(TestJson.ToJson(null));

        int value = context.GetJobParameter<int>("Name");

        Assert.Equal(default, value);
    }

    private ElectStateContext CreateContext()
    {
        return new ElectStateContext(_applyContext.Object);
    }
}
