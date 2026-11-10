using System.Net;
using System.Security.Claims;
using System.Text;
using Masterly.NightShift.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Masterly.NightShift.AspNetCore.Tests.Dashboard;

public class BasicAuthDashboardAuthorizationFilterFacts
{
    private const string UserName = "operator";
    private const string Password = "s3cret";

    private readonly BasicAuthDashboardAuthorizationFilter _filter = new(UserName, Password);

    [Fact]
    public async Task AuthorizeAsync_AllowsTheRequest_WithTheRightCredentials()
    {
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password));

        bool allowed = await AuthorizeAsync(_filter, httpContext);

        Assert.True(allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_MakesTheUserTheRequestsUser_WhenAccepted()
    {
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password));

        await AuthorizeAsync(_filter, httpContext);

        Assert.True(httpContext.User.Identity!.IsAuthenticated); // Set by the filter above
        Assert.Equal(UserName, httpContext.User.Identity.Name);
    }

    [Fact]
    public async Task AuthorizeAsync_KeepsAnAlreadySignedInUser()
    {
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password));
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Cookies"));

        await AuthorizeAsync(_filter, httpContext);

        Assert.Equal("alice", httpContext.User.Identity!.Name); // Set above
    }

    [Fact]
    public async Task AuthorizeAsync_AcceptsTheSchemeInAnyCase()
    {
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password).Replace("Basic", "bAsIc", StringComparison.Ordinal));

        Assert.True(await AuthorizeAsync(_filter, httpContext));
    }

    [Fact]
    public async Task AuthorizeAsync_AcceptsAPasswordContainingAColon()
    {
        BasicAuthDashboardAuthorizationFilter filter = new(UserName, "with:colon");
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, "with:colon"));

        Assert.True(await AuthorizeAsync(filter, httpContext));
    }

    [Fact]
    public async Task AuthorizeAsync_ComparesNonAsciiCredentialsAsUtf8()
    {
        BasicAuthDashboardAuthorizationFilter filter = new("مشغل", "كلمة-سر");
        DefaultHttpContext httpContext = CreateHttpContext(Header("مشغل", "كلمة-سر"));

        Assert.True(await AuthorizeAsync(filter, httpContext));
    }

    [Theory]
    [InlineData(UserName, "wrong")]
    [InlineData("wrong", Password)]
    [InlineData("OPERATOR", Password)]
    [InlineData(UserName, "S3CRET")]
    [InlineData(UserName, "")]
    [InlineData("", Password)]
    public async Task AuthorizeAsync_RefusesAndChallenges_WithWrongCredentials(string userName, string password)
    {
        DefaultHttpContext httpContext = CreateHttpContext(Header(userName, password));

        bool allowed = await AuthorizeAsync(_filter, httpContext);

        Assert.False(allowed);
        AssertChallenged(httpContext);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic")]
    [InlineData("Basic ")]
    [InlineData("Basic not-base64!")]
    [InlineData("Basic b3BlcmF0b3I=")] // "operator", without a colon
    [InlineData("Bearer b3BlcmF0b3I6czNjcmV0")] // The right credentials, under another scheme
    public async Task AuthorizeAsync_RefusesAndChallenges_WithAMissingOrMalformedHeader(string header)
    {
        DefaultHttpContext httpContext = CreateHttpContext(header);

        bool allowed = await AuthorizeAsync(_filter, httpContext);

        Assert.False(allowed);
        AssertChallenged(httpContext);
    }

    [Fact]
    public async Task AuthorizeAsync_RefusesAndChallenges_WithAnOversizedHeader()
    {
        DefaultHttpContext httpContext = CreateHttpContext($"Basic {new string('A', 100_000)}");

        bool allowed = await AuthorizeAsync(_filter, httpContext);

        Assert.False(allowed);
        AssertChallenged(httpContext);
    }

    [Fact]
    public async Task AuthorizeAsync_RefusesAndChallenges_WhenTheProviderHasNoCredential()
    {
        BasicAuthDashboardAuthorizationFilter filter = new(ProviderReturning(null).Object);
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password));

        bool allowed = await AuthorizeAsync(filter, httpContext);

        Assert.False(allowed);
        AssertChallenged(httpContext);
    }

    [Fact]
    public async Task AuthorizeAsync_UsesTheProvidersCredential_WithTheRequestsToken()
    {
        Mock<IDashboardBasicAuthCredentialProvider> provider = ProviderReturning(new DashboardBasicAuthCredential(UserName, Password));
        BasicAuthDashboardAuthorizationFilter filter = new(provider.Object);
        using CancellationTokenSource aborted = new();
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password));
        httpContext.RequestAborted = aborted.Token;

        bool allowed = await AuthorizeAsync(filter, httpContext);

        Assert.True(allowed);
        provider.Verify(x => x.GetCredentialAsync(aborted.Token));
    }

    [Fact]
    public async Task AuthorizeAsync_RefusesPlainHttpFromAnotherMachine_WithoutAskingForThePassword()
    {
        Mock<IDashboardBasicAuthCredentialProvider> provider = ProviderReturning(new DashboardBasicAuthCredential(UserName, Password));
        BasicAuthDashboardAuthorizationFilter filter = new(provider.Object);
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password), IPAddress.Parse("203.0.113.9"));

        bool allowed = await AuthorizeAsync(filter, httpContext);

        Assert.False(allowed);
        Assert.False(httpContext.Response.Headers.ContainsKey("WWW-Authenticate"));
        provider.Verify(x => x.GetCredentialAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuthorizeAsync_RefusesPlainHttp_WhenTheRemoteAddressIsUnknown()
    {
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password), remoteIp: null);

        Assert.False(await AuthorizeAsync(_filter, httpContext));
    }

    [Fact]
    public async Task AuthorizeAsync_AllowsHttpsFromAnotherMachine()
    {
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password), IPAddress.Parse("203.0.113.9"));
        httpContext.Request.IsHttps = true;

        Assert.True(await AuthorizeAsync(_filter, httpContext));
    }

    [Fact]
    public async Task AuthorizeAsync_AllowsPlainHttp_WhenRequireHttpsIsOff()
    {
        BasicAuthDashboardAuthorizationFilter filter = new(UserName, Password) { RequireHttps = false };
        DefaultHttpContext httpContext = CreateHttpContext(Header(UserName, Password), IPAddress.Parse("203.0.113.9"));

        Assert.True(await AuthorizeAsync(filter, httpContext));
    }

    [Fact]
    public async Task AuthorizeAsync_EscapesTheRealm_InTheChallenge()
    {
        BasicAuthDashboardAuthorizationFilter filter = new(UserName, Password) { Realm = "My \"Jobs\"" };
        DefaultHttpContext httpContext = CreateHttpContext(header: null);

        await AuthorizeAsync(filter, httpContext);

        Assert.Equal("Basic realm=\"My \\\"Jobs\\\"\", charset=\"UTF-8\"", httpContext.Response.Headers.WWWAuthenticate.ToString());
    }

    [Theory]
    [InlineData("", Password)]
    [InlineData(UserName, "")]
    [InlineData("with:colon", Password)]
    public void Credential_ThrowsAnException_WithAValueBasicAuthCanNotCarry(string userName, string password)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DashboardBasicAuthCredential(userName, password));
    }

    [Fact]
    public void Credential_DoesNotPrintThePassword()
    {
        DashboardBasicAuthCredential credential = new(UserName, Password);

        Assert.DoesNotContain(Password, credential.ToString(), StringComparison.Ordinal);
    }

    private static Mock<IDashboardBasicAuthCredentialProvider> ProviderReturning(DashboardBasicAuthCredential credential)
    {
        Mock<IDashboardBasicAuthCredentialProvider> provider = new();
        provider.Setup(x => x.GetCredentialAsync(It.IsAny<CancellationToken>())).ReturnsAsync(credential);
        return provider;
    }

    private static Task<bool> AuthorizeAsync(BasicAuthDashboardAuthorizationFilter filter, DefaultHttpContext httpContext)
    {
        return filter.AuthorizeAsync(new AspNetCoreDashboardContext(new Mock<JobStorage>().Object, new DashboardOptions(), httpContext));
    }

    private static void AssertChallenged(DefaultHttpContext httpContext)
    {
        Assert.StartsWith("Basic realm=", httpContext.Response.Headers.WWWAuthenticate.ToString(), StringComparison.Ordinal);
    }

    private static string Header(string userName, string password)
    {
        return $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"))}";
    }

    private static DefaultHttpContext CreateHttpContext(string header)
    {
        return CreateHttpContext(header, IPAddress.Loopback);
    }

    private static DefaultHttpContext CreateHttpContext(string header, IPAddress remoteIp)
    {
        DefaultHttpContext httpContext = new()
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        httpContext.Connection.RemoteIpAddress = remoteIp;

        if (header != null)
        {
            httpContext.Request.Headers.Authorization = header;
        }

        return httpContext;
    }
}
