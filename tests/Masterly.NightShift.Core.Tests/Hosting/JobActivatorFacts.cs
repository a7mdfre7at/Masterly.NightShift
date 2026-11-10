using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class JobActivatorFacts
{
    [Fact, GlobalLock]
    public void SetCurrent_ThrowsAnException_WhenValueIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => JobActivator.Current = null);
    }

    [Fact, GlobalLock]
    public void GetCurrent_ReturnsPreviouslySetValue()
    {
        JobActivator activator = new();
        JobActivator.Current = activator;

        Assert.Same(activator, JobActivator.Current);
    }

    [Fact]
    public void BeginScope_ThrowsArgumentNullException_WhenPerformContextIsNull()
    {
        JobActivator activator = new();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => activator.BeginScope((PerformContext)null));

        Assert.Equal("context", exception.ParamName);
    }

    [Fact]
    public void DefaultActivator_CanCreateInstanceOfClassWithDefaultConstructor()
    {
        JobActivator activator = new();

        object? instance = activator.ActivateJob(typeof(DefaultConstructor));

        Assert.NotNull(instance);
    }

    [Fact]
    public void DefaultActivator_ThrowAnException_IfThereIsNoDefaultConstructor()
    {
        JobActivator activator = new();

        Assert.Throws<MissingMethodException>(
            () => activator.ActivateJob(typeof(CustomConstructor)));
    }

    public class DefaultConstructor
    {
    }

    public class CustomConstructor(string arg)
    {
        public string Arg { get; } = arg;
    }
}
