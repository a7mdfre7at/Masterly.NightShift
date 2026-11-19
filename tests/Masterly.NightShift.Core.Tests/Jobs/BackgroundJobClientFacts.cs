using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests;

public class BackgroundJobClientFacts
{
    private readonly Mock<JobStorage> _storage;
    private readonly Mock<IBackgroundJobFactory> _factory;
    private readonly Mock<IState> _state;
    private readonly Job _job;
    private readonly Mock<IBackgroundJobStateChanger> _stateChanger;

    public BackgroundJobClientFacts()
    {
        Mock<IStorageConnection> connection = new();
        _storage = new Mock<JobStorage>();
        _storage.Setup(x => x.GetConnection()).Returns(connection.Object);

        _stateChanger = new Mock<IBackgroundJobStateChanger>();

        _state = new Mock<IState>();
        _state.Setup(x => x.Name).Returns("Mock");
        _job = Job.FromExpression(() => Method());

        _factory = new Mock<IBackgroundJobFactory>();
        _factory.Setup(x => x.Create(It.IsAny<CreateContext>()))
            .Returns(new BackgroundJob("some-job", _job, DateTime.UtcNow));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobClient(null, _factory.Object, _stateChanger.Object));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenFactoryIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobClient(_storage.Object, null, _stateChanger.Object));

        Assert.Equal("factory", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStateChangerIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobClient(_storage.Object, _factory.Object, null));

        Assert.Equal("stateChanger", exception.ParamName);
    }

    [Fact, GlobalLock(Reason = "Needs JobStorage.Current instance")]
    public void Ctor_UsesCurrent_JobStorageInstance_ByDefault()
    {
        JobStorage.Current = new Mock<JobStorage>().Object;
        // ReSharper disable once ObjectCreationAsStatement
        // Does not throw
        new BackgroundJobClient();
    }

    [Fact]
    public void CreateJob_ThrowsAnException_WhenJobIsNull()
    {
        BackgroundJobClient client = CreateClient();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => client.Create(null, _state.Object));

        Assert.Equal("job", exception.ParamName);
    }

    [Fact]
    public void CreateJob_ThrowsAnException_WhenStateIsNull()
    {
        BackgroundJobClient client = CreateClient();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => client.Create(_job, null));

        Assert.Equal("state", exception.ParamName);
    }

    [Fact]
    public void CreateJob_DelegatesBackgroundJobCreation_ToFactory()
    {
        BackgroundJobClient client = CreateClient();

        client.Create(_job, _state.Object);

        _factory.Verify(x => x.Create(It.IsNotNull<CreateContext>()));
    }

    [Fact]
    public void CreateJob_ReturnsJobIdentifier()
    {
        BackgroundJobClient client = CreateClient();

        string? id = client.Create(_job, _state.Object);

        Assert.Equal("some-job", id);
    }

    [Fact]
    public void CreateJob_WrapsOccurringExceptions_IntoItsOwnException()
    {
        BackgroundJobClient client = CreateClient();
        _factory.Setup(x => x.Create(It.IsAny<CreateContext>()))
            .Throws<InvalidOperationException>();

        BackgroundJobClientException exception = Assert.Throws<BackgroundJobClientException>(
            () => client.Create(_job, _state.Object));

        Assert.NotNull(exception.InnerException);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void ChangeState_ThrowsAnException_WhenJobIdIsNull()
    {
        BackgroundJobClient client = CreateClient();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => client.ChangeState(null, _state.Object, null));

        Assert.Equal("jobId", exception.ParamName);
    }

    [Fact]
    public void ChangeState_ThrowsAnException_WhenStateIsNull()
    {
        BackgroundJobClient client = CreateClient();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => client.ChangeState("jobId", null, null));

        Assert.Equal("state", exception.ParamName);
    }

    [Fact]
    public void ChangeState_ChangesTheStateOfAJob_ToTheGivenOne()
    {
        BackgroundJobClient client = CreateClient();

        client.ChangeState("job-id", _state.Object, null);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.BackgroundJobId == "job-id" &&
            ReferenceEquals(ctx.NewState, _state.Object) &&
            ctx.ExpectedStates == null)));
    }

    [Fact]
    public void ChangeState_WithFromState_ChangesTheStateOfAJob_WithFromStateValue()
    {
        BackgroundJobClient client = CreateClient();

        client.ChangeState("job-id", _state.Object, "State");

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.BackgroundJobId == "job-id" &&
            ReferenceEquals(ctx.NewState, _state.Object) &&
            ctx.ExpectedStates.SequenceEqual(new[] { "State" }))));
    }

    [Fact]
    public void ChangeState_ReturnsTheResult_OfStateChangerInvocation()
    {
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns(_state.Object);
        BackgroundJobClient client = CreateClient();

        bool result = client.ChangeState("job-id", _state.Object, null);

        Assert.True(result);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void Method()
    {
    }

    private BackgroundJobClient CreateClient()
    {
        return new BackgroundJobClient(_storage.Object, _factory.Object, _stateChanger.Object);
    }
}
