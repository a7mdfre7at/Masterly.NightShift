using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

namespace Masterly.NightShift.Core.Tests.Client;

public class ClientExceptionContextFacts
{
    private readonly CreateContext _createContext;

    public ClientExceptionContextFacts()
    {
        Mock<JobStorage> storage = new();
        Mock<IStorageConnection> connection = new();
        Job job = Job.FromExpression(() => TestMethod());
        Mock<IState> state = new();

        _createContext = new CreateContext(
            storage.Object, connection.Object, job, state.Object);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenCreateContextIsNull()
    {
        Assert.Throws<NullReferenceException>(
            () => new ClientExceptionContext(null, new Exception()));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenExceptionIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new ClientExceptionContext(_createContext, null));

        Assert.Equal("exception", exception.ParamName);
    }

    [Fact]
    public void Ctor_CorrectlySets_AllProperties()
    {
        Exception exception = new();
        ClientExceptionContext context = new(_createContext, exception);

        Assert.Same(exception, context.Exception);
        Assert.False(context.ExceptionHandled);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void TestMethod()
    {
    }
}
