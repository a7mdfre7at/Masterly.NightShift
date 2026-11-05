using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class BatchCommandDispatcherFacts
    {
        [Fact]
        public void Dispatch_Sets401StatusCode_WhenNotPermitted()
        {
            var options = new DashboardOptions
            {
                IsReadOnlyFunc = _ => true
            };
            var context = new DashboardContextStub(options);
            var dispatcher = new BatchCommandDispatcher((DashboardContext ctx, string str) => { });
            dispatcher.Dispatch(context);
            Assert.Equal(401, context.Response.StatusCode);
        }
    }
}
