namespace Masterly.NightShift.Core.Tests;

public class GlobalStateHandlersFacts
{
    [Fact]
    public void AllBasicHandlersShouldBeIncluded()
    {
        Type[] handlerTypes = [.. GlobalStateHandlers.Handlers.Select(x => x.GetType())];

        Assert.Contains(typeof(SucceededState.Handler), handlerTypes);
        Assert.Contains(typeof(ScheduledState.Handler), handlerTypes);
        Assert.Contains(typeof(EnqueuedState.Handler), handlerTypes);
        Assert.Contains(typeof(DeletedState.Handler), handlerTypes);
    }
}
