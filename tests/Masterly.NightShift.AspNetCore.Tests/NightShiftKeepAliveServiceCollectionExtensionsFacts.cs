using Masterly.NightShift.AspNetCore.Tests.Stubs;
using Masterly.NightShift.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Masterly.NightShift.AspNetCore.Tests;

public class NightShiftKeepAliveServiceCollectionExtensionsFacts
{
    // Nothing listens on port 1, and the short timeout below keeps a failed request fast. The tests only
    // look at which URL was requested, which KeepAliveProcess.Status records whether the request succeeds or not.
    private const string ClosedPort = "http://localhost:1";

    [Fact]
    public void AddNightShiftKeepAlive_ThrowsAnException_WhenServicesIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => NightShiftKeepAliveServiceCollectionExtensions.AddNightShiftKeepAlive(null));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddNightShiftKeepAlive_ThrowsAnException_WhenPathIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddNightShiftKeepAlive(path: null));

        Assert.Equal("path", exception.ParamName);
    }

    [Fact]
    public void AddNightShiftKeepAlive_RegistersOneKeepAliveProcess_AsABackgroundProcess()
    {
        using ServiceProvider provider = BuildProvider(addresses: null, configure: x => x.Url = ClosedPort);
        IBackgroundProcess first = provider.GetRequiredService<IBackgroundProcess>();
        IBackgroundProcess second = provider.GetRequiredService<IBackgroundProcess>();

        Assert.IsType<KeepAliveProcess>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void AddNightShiftKeepAlive_AppliesTheConfiguredOptions()
    {
        using ServiceProvider provider = BuildProvider(addresses: null, configure: x =>
               {
                   x.Url = ClosedPort + "/custom";
                   x.Interval = TimeSpan.FromMinutes(1);
               });
        provider.GetRequiredService<IBackgroundProcess>();

        Assert.Equal(ClosedPort + "/custom", KeepAliveProcess.Status.Url);
        Assert.Equal(TimeSpan.FromMinutes(1), KeepAliveProcess.Status.Interval);
    }

    [Fact]
    public void Ping_RequestsTheConfiguredUrl_InsteadOfTheServerAddress()
    {
        using ServiceProvider provider = BuildProvider(["http://localhost:2"], configure: x => x.Url = ClosedPort + "/custom");
        GetProcess(provider).Ping(CancellationToken.None);

        Assert.Equal(ClosedPort + "/custom", KeepAliveProcess.Status.Url);
    }

    [Fact]
    public void Ping_RequestsTheUrlFromTheConfiguredFactory_InsteadOfTheServerAddress()
    {
        using ServiceProvider provider = BuildProvider(["http://localhost:2"], configure: x => x.UrlFactory = () => ClosedPort + "/factory");
        GetProcess(provider).Ping(CancellationToken.None);

        Assert.Equal(ClosedPort + "/factory", KeepAliveProcess.Status.Url);
    }

    [Theory]
    [InlineData("http://localhost:1")]
    [InlineData("http://localhost:1/")]
    [InlineData("http://*:1")]
    [InlineData("http://+:1")]
    [InlineData("http://0.0.0.0:1")]
    [InlineData("http://[::]:1")]
    public void Ping_RequestsTheDefaultPathOnTheServerAddress_UsingLocalhostForAnyAddress(string address)
    {
        using ServiceProvider provider = BuildProvider([address]);
        GetProcess(provider).Ping(CancellationToken.None);

        Assert.Equal("http://localhost:1/nightshift/ping", KeepAliveProcess.Status.Url);
    }

    [Fact]
    public void Ping_PrefersAnHttpAddress_OverAnHttpsOne()
    {
        using ServiceProvider provider = BuildProvider(["https://localhost:2", "http://localhost:1"]);
        GetProcess(provider).Ping(CancellationToken.None);

        Assert.Equal("http://localhost:1/nightshift/ping", KeepAliveProcess.Status.Url);
    }

    [Fact]
    public void Ping_UsesAnHttpsAddress_WhenItIsTheOnlyOne()
    {
        using ServiceProvider provider = BuildProvider(["https://localhost:1"]);
        GetProcess(provider).Ping(CancellationToken.None);

        Assert.Equal("https://localhost:1/nightshift/ping", KeepAliveProcess.Status.Url);
    }

    [Theory]
    [InlineData("/keep-alive")]
    [InlineData("keep-alive")]
    public void Ping_AppendsTheGivenPath_WithExactlyOneSlash(string path)
    {
        using ServiceProvider provider = BuildProvider([ClosedPort + "/"], path);
        GetProcess(provider).Ping(CancellationToken.None);

        Assert.Equal(ClosedPort + "/keep-alive", KeepAliveProcess.Status.Url);
    }

    [Fact]
    public void Ping_ReportsAnUnresolvedUrl_WhenThereIsNoServer()
    {
        using ServiceProvider provider = BuildProvider(addresses: null);
        bool answered = GetProcess(provider).Ping(CancellationToken.None);

        Assert.False(answered);
        Assert.Null(KeepAliveProcess.Status.Url);
        Assert.False(KeepAliveProcess.Status.LastSucceeded);
        Assert.Contains("could not be resolved", KeepAliveProcess.Status.LastError);
    }

    [Fact]
    public void Ping_ReportsAnUnresolvedUrl_WhenTheServerHasNoAddresses()
    {
        using ServiceProvider provider = BuildProvider([]);
        bool answered = GetProcess(provider).Ping(CancellationToken.None);

        Assert.False(answered);
        Assert.Null(KeepAliveProcess.Status.Url);
        Assert.Contains("could not be resolved", KeepAliveProcess.Status.LastError);
    }

    [Fact]
    public void Ping_ResolvesTheUrlAgain_WhenTheServerHadNoAddressYet()
    {
        ServerStub server = new([]);
        IServiceCollection services = new ServiceCollection()
            .AddSingleton<IServer>(server)
            .AddNightShiftKeepAlive(Fast);

        using ServiceProvider provider = services.BuildServiceProvider();
        KeepAliveProcess process = GetProcess(provider);
        process.Ping(CancellationToken.None);
        server.Features.Get<IServerAddressesFeature>().Addresses.Add(ClosedPort);

        process.Ping(CancellationToken.None);

        Assert.Equal(ClosedPort + "/nightshift/ping", KeepAliveProcess.Status.Url);
    }

    private static ServiceProvider BuildProvider(
        string[] addresses,
        string path = "/nightshift/ping",
        Action<KeepAliveOptions> configure = null)
    {
        ServiceCollection services = new();

        if (addresses != null)
        {
            services.AddSingleton<IServer>(new ServerStub(addresses));
        }

        services.AddNightShiftKeepAlive(options =>
        {
            Fast(options);
            configure?.Invoke(options);
        }, path);

        return services.BuildServiceProvider();
    }

    private static void Fast(KeepAliveOptions options)
    {
        options.Timeout = TimeSpan.FromMilliseconds(100);
        options.LogFailures = false;
    }

    private static KeepAliveProcess GetProcess(IServiceProvider provider)
    {
        return (KeepAliveProcess)provider.GetRequiredService<IBackgroundProcess>();
    }
}
