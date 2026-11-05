using System.Security.Claims;
using Masterly.NightShift.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests.Dashboard
{
    public class AspNetCoreDashboardContextFacts
    {
        [Fact]
        public void GetUserName_ReturnsTheName_OfAnAuthenticatedUser()
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "alice") }, "Test"));
            var context = CreateContext(user);

            var name = context.GetUserName();

            Assert.Equal("alice", name);
        }

        [Fact]
        public void GetUserName_ReturnsNull_ForAnAnonymousUser()
        {
            // No authentication type means the identity is not authenticated, even with a name claim
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "alice") }));
            var context = CreateContext(user);

            var name = context.GetUserName();

            Assert.Null(name);
        }

        [Fact]
        public void GetUserName_ReturnsNull_WhenTheUserHasNoIdentity()
        {
            var context = CreateContext(new ClaimsPrincipal());

            var name = context.GetUserName();

            Assert.Null(name);
        }

        private static AspNetCoreDashboardContext CreateContext(ClaimsPrincipal user)
        {
            var httpContext = new DefaultHttpContext
            {
                User = user,
                RequestServices = new ServiceCollection().BuildServiceProvider()
            };

            return new AspNetCoreDashboardContext(new Mock<JobStorage>().Object, new DashboardOptions(), httpContext);
        }
    }
}
