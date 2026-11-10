// Endpoint routing (MapNightShiftHealth, MapNightShiftMetrics) is in the net8.0 and later builds only
using System.Text;
using System.Text.Json.Nodes;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Masterly.NightShift.AspNetCore.Tests;

public class NightShiftMonitoringEndpointRouteBuilderExtensionsFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _monitor = new();
    private readonly Mock<JobStorageConnection> _connection = new();

    public NightShiftMonitoringEndpointRouteBuilderExtensionsFacts()
    {
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Enqueued = 4 });
        _monitor.Setup(x => x.Servers()).Returns([new() { Name = "web-1", Heartbeat = DateTime.UtcNow, StartedAt = DateTime.UtcNow, Queues = ["default"], WorkersCount = 2 }]);
        _monitor.Setup(x => x.Queues()).Returns([]);
        _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns([]);
        _connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
    }

    [Fact]
    public async Task MapNightShiftHealth_AnswersTheHealthReport_WithoutTheDashboardFilters()
    {
        WebApplication app = CreateApplication();
        app.MapNightShiftDashboard(); // its default filter only lets local requests in
        app.MapNightShiftHealth("/health/nightshift");

        (int status, string? body) = await InvokeAsync(app, "/health/nightshift");

        Assert.Equal(200, status);
        JsonNode? json = JsonNode.Parse(body);
        Assert.Equal("healthy", json["status"].GetValue<string>());
        Assert.Equal(4, json["enqueued"].GetValue<long>());
    }

    [Fact]
    public async Task MapNightShiftMetrics_AnswersThePrometheusText()
    {
        WebApplication app = CreateApplication();
        app.MapNightShiftMetrics("/metrics/nightshift");

        (int status, string? body) = await InvokeAsync(app, "/metrics/nightshift");

        Assert.Equal(200, status);
        Assert.Contains("nightshift_jobs{state=\"enqueued\"} 4\n", body);
    }

    [Fact]
    public void MapNightShiftHealth_ThrowsAnException_WhenArgumentsAreNull_OrNightShiftIsNotAdded()
    {
        Assert.Throws<ArgumentNullException>("endpoints", () => NightShiftMonitoringEndpointRouteBuilderExtensions.MapNightShiftHealth(null, "/h"));
        Assert.Throws<ArgumentNullException>("pattern", () => CreateApplication().MapNightShiftMetrics(null));

        WebApplication bare = WebApplication.CreateBuilder().Build();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => bare.MapNightShiftHealth("/h"));
        Assert.Contains("AddNightShift", exception.Message);
    }

    private WebApplication CreateApplication()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(_storage.Object);
        builder.Services.AddNightShift(_ => { });
        return builder.Build();
    }

    private static async Task<(int Status, string Body)> InvokeAsync(WebApplication app, string path)
    {
        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == path);

        DefaultHttpContext httpContext = new() { RequestServices = app.Services };
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = path;
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        MemoryStream body = new();
        httpContext.Response.Body = body;

        await endpoint.RequestDelegate(httpContext);

        return (httpContext.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }
}
