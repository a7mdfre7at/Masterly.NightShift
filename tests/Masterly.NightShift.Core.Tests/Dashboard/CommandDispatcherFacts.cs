using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class CommandDispatcherFacts
{
    [Fact]
    public void Dispatch_Sets401StatusCode_WhenNotPermitted()
    {
        DashboardOptions options = new()
        {
            IsReadOnlyFunc = _ => true
        };
        DashboardContextStub context = new(options);
        CommandDispatcher dispatcher = new(_ => false);
        dispatcher.Dispatch(context);
        Assert.Equal(401, context.Response.StatusCode);
    }
}
