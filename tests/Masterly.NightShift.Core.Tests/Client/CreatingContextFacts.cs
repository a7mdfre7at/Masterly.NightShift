using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

namespace Masterly.NightShift.Core.Tests.Client;

public class CreatingContextFacts
{
    [Fact]
    public void Ctor_ThrowsAnException_WhenContextIsNull()
    {
        Assert.Throws<NullReferenceException>(
            () => new CreatingContext(null));
    }

    [Fact]
    public void Ctor_CanceledProperty_IsFalseByDefault()
    {
        CreatingContext context = CreateContext();

        Assert.False(context.Canceled);
    }

    [Fact]
    public void SetJobParameter_ThrowsAnException_WhenParameterNameIsNull()
    {
        CreatingContext context = CreateContext();
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => context.SetJobParameter(null, null));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void SetJobParameter_AcceptsNullValues()
    {
        CreatingContext context = CreateContext();

        // Does noto throw
        context.SetJobParameter("name", null);
    }

    [Fact]
    public void SetJobParameter_CanBeCalledTwice_WithTheSameName()
    {
        CreatingContext context = CreateContext();
        context.SetJobParameter("name", null);

        // Does not throw
        context.SetJobParameter("name", null);
    }

    [Fact]
    public void GetJobParameter_ThrowsAnException_WhenParameterNameIsNull()
    {
        CreatingContext context = CreateContext();
        Assert.Throws<ArgumentNullException>(
            () => context.GetJobParameter<int>(null));
    }

    [Fact]
    public void GetJobParameter_ReturnsDefaultValue_IfParameterDoesNotExists()
    {
        CreatingContext context = CreateContext();

        Assert.Equal(default, context.GetJobParameter<int>("one"));
        Assert.Equal(default, context.GetJobParameter<string>("two"));
    }

    [Fact]
    public void GetJobParameter_ReturnsTheValue_ThatWasSetByTheCorrespondingMethod()
    {
        CreatingContext context = CreateContext();
        context.SetJobParameter("name", "value");

        Assert.Equal("value", context.GetJobParameter<string>("name"));
    }

    [Fact]
    public void GetJobParameter_ReturnsTheValue_OfTheSpecifiedParameterNameOnly()
    {
        CreatingContext context = CreateContext();

        context.SetJobParameter("name1", "value1");
        context.SetJobParameter("name2", "value2");

        Assert.Equal("value1", context.GetJobParameter<string>("name1"));
    }

    [Fact]
    public void GetJobParameter_ReturnsTheFreshestValue_WhenTwoSetOperationsPerformed()
    {
        CreatingContext context = CreateContext();

        context.SetJobParameter("name", "oldValue");
        context.SetJobParameter("name", "newValue");

        Assert.Equal("newValue", context.GetJobParameter<string>("name"));
    }

    [Fact]
    public void GetJobParameter_ThrowsAnException_WhenParameterCouldNotBeDeserialized()
    {
        CreatingContext context = CreateContext();

        context.SetJobParameter("name", "value");

        Assert.Throws<InvalidOperationException>(
            () => context.GetJobParameter<int>("name"));
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void TestMethod() { }

    private static CreatingContext CreateContext()
    {
        Mock<JobStorage> storage = new();
        Mock<IStorageConnection> connection = new();
        Job job = Job.FromExpression(() => TestMethod());
        Mock<IState> state = new();

        CreateContext createContext = new(storage.Object, connection.Object, job, state.Object);
        return new CreatingContext(createContext);
    }
}
