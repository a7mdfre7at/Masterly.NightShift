using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

// ReSharper disable AssignNullToNotNullAttribute


namespace Masterly.NightShift.Core.Tests.Client;

public class CreatedContextFacts
{
    private readonly Exception _exception;
    private readonly BackgroundJobMock _backgroundJob;

    public CreatedContextFacts()
    {
        _exception = new Exception();
        _backgroundJob = new BackgroundJobMock();
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenCreateContextIsNull()
    {
        Assert.Throws<NullReferenceException>(
            () => new CreatedContext(null, _backgroundJob.Object, false, null));
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void TestMethod() { }

    private CreatedContext CreateContext()
    {
        Mock<JobStorage> storage = new();
        Mock<IStorageConnection> connection = new();
        Job job = Job.FromExpression(() => TestMethod());
        Mock<IState> state = new();

        CreateContext createContext = new(storage.Object, connection.Object, job, state.Object);
        return new CreatedContext(createContext, _backgroundJob.Object, true, _exception);
    }

    [Fact]
    public void Ctor_CorrectlySetsAllProperties()
    {
        CreatedContext context = CreateContext();

        Assert.True(context.Canceled);
        Assert.Same(_exception, context.Exception);
        Assert.Equal(_backgroundJob.Id, context.BackgroundJob.Id);
    }
}
