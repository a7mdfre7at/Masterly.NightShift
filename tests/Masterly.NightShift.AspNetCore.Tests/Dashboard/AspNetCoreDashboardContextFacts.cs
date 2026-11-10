using System.Security.Claims;
using Masterly.NightShift.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Masterly.NightShift.AspNetCore.Tests.Dashboard;

public class AspNetCoreDashboardContextFacts
{
    [Fact]
    public void GetUserName_ReturnsTheName_OfAnAuthenticatedUser()
    {
        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Test"));
        AspNetCoreDashboardContext context = CreateContext(user);

        string? name = context.GetUserName();

        Assert.Equal("alice", name);
    }

    [Fact]
    public void GetUserName_ReturnsNull_ForAnAnonymousUser()
    {
        // No authentication type means the identity is not authenticated, even with a name claim
        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")]));
        AspNetCoreDashboardContext context = CreateContext(user);

        string? name = context.GetUserName();

        Assert.Null(name);
    }

    [Fact]
    public void GetUserName_ReturnsNull_WhenTheUserHasNoIdentity()
    {
        AspNetCoreDashboardContext context = CreateContext(new ClaimsPrincipal());

        string? name = context.GetUserName();

        Assert.Null(name);
    }

    private static AspNetCoreDashboardContext CreateContext(ClaimsPrincipal user)
    {
        DefaultHttpContext httpContext = new()
        {
            User = user,
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };

        return new AspNetCoreDashboardContext(new Mock<JobStorage>().Object, new DashboardOptions(), httpContext);
    }
}
