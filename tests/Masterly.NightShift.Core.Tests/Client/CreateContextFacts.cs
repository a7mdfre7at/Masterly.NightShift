using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

// ReSharper disable ObjectCreationAsStatement
// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Client;

public class CreateContextFacts
{
    private readonly Job _job;
    private readonly Mock<IState> _state;
    private readonly Mock<IStorageConnection> _connection;
    private readonly Mock<JobStorage> _storage;

    public CreateContextFacts()
    {
        _job = Job.FromExpression(() => Method());
        _state = new Mock<IState>();
        _connection = new Mock<IStorageConnection>();
        _storage = new Mock<JobStorage>();
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new CreateContext(null, _connection.Object, _job, _state.Object));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenConnectionIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new CreateContext(_storage.Object, null, _job, _state.Object));

        Assert.Equal("connection", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenJobIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new CreateContext(_storage.Object, _connection.Object, null, _state.Object));

        Assert.Equal("job", exception.ParamName);
    }

    [Fact]
    public void Ctor_DoesNotThrowAnException_WhenStateIsNull()
    {
        // Does not throw
        new CreateContext(_storage.Object, _connection.Object, _job, null);
    }

    [Fact]
    public void Ctor_CorrectlyInitializes_AllProperties()
    {
        CreateContext context = CreateContext();

        Assert.Same(_storage.Object, context.Storage);
        Assert.Same(_connection.Object, context.Connection);
        Assert.Same(_job, context.Job);
        Assert.Same(_state.Object, context.InitialState);

        Assert.NotNull(context.Items);
        Assert.NotNull(context.Parameters);
    }

    [Fact]
    public void CopyCtor_CopiesItemsDictionary_FromTheGivenContext()
    {
        CreateContext context = CreateContext();
        CreateContext contextCopy = new(context);

        Assert.Same(context.Items, contextCopy.Items);
        Assert.Same(context.Parameters, contextCopy.Parameters);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void Method()
    {
    }

    private CreateContext CreateContext()
    {
        return new CreateContext(_storage.Object, _connection.Object, _job, _state.Object);
    }
}
