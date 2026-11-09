using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift.AspNetCore.Tests;

public class NightShiftHostApplicationBuilderExtensionsFacts
{
    private readonly Mock<JobStorage> _storage = new();

    [Fact]
    public void AddNightShift_RegistersTheServices()
    {
        var builder = CreateBuilder();

        builder.AddNightShift(_ => { });

        using var host = builder.Build();
        Assert.NotNull(host.Services.GetRequiredService<IBackgroundJobClient>());
    }

    [Fact]
    public void AddNightShift_WithTheServices_RegistersTheServices()
    {
        var builder = CreateBuilder();

        builder.AddNightShift((_, _) => { });

        using var host = builder.Build();
        Assert.NotNull(host.Services.GetRequiredService<IBackgroundJobClient>());
    }

    [Fact]
    public void AddNightShift_ReturnsTheBuilder()
    {
        var builder = CreateBuilder();

        var result = builder.AddNightShift(_ => { });

        Assert.Same(builder, result);
    }

    [Fact]
    public void AddNightShift_ThrowsAnException_WhenBuilderIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ((IHostApplicationBuilder)null).AddNightShift(_ => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AddNightShiftServer_AddsAHostedService()
    {
        var builder = CreateBuilder();

        builder.AddNightShiftServer();

        Assert.Contains(builder.Services, static x => x.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddNightShiftServer_WithOptions_AddsAHostedService()
    {
        var builder = CreateBuilder();

        builder.AddNightShiftServer(static x => x.WorkerCount = 1);

        Assert.Contains(builder.Services, static x => x.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddNightShiftServer_ThrowsAnException_WhenBuilderIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ((IHostApplicationBuilder)null).AddNightShiftServer());

        Assert.Equal("builder", exception.ParamName);
    }

    private HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSingleton(_storage.Object);
        return builder;
    }
}
