using System.Text;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Microsoft.AspNetCore.Http;

namespace Masterly.NightShift.AspNetCore.Tests.Dashboard;

/// <summary>
/// Reading form fields through the ASP.NET Core adapter, without a real host.
/// </summary>
public class AspNetCoreDashboardRequestFacts
{
    [Fact]
    public async Task GetFormValuesAsync_ReturnsNoValues_WhenThePostHasNoBody()
    {
        // What jQuery's $.post(url) sends without data: no body and no Content-Type
        AspNetCoreDashboardRequest request = new(CreateHttpContext("POST", contentType: null, body: null));

        IList<string> values = await request.GetFormValuesAsync("reason");

        Assert.Empty(values);
    }

    [Fact]
    public async Task GetFormValuesAsync_ReturnsNoValues_WhenTheBodyIsNotAForm()
    {
        AspNetCoreDashboardRequest request = new(CreateHttpContext("POST", "application/json", "{\"reason\":\"x\"}"));

        IList<string> values = await request.GetFormValuesAsync("reason");

        Assert.Empty(values);
    }

    [Fact]
    public async Task GetFormValuesAsync_ReturnsNoValues_ForAGet()
    {
        AspNetCoreDashboardRequest request = new(CreateHttpContext("GET", contentType: null, body: null));

        IList<string> values = await request.GetFormValuesAsync("metrics[]");

        Assert.Empty(values);
    }

    [Fact]
    public async Task GetFormValuesAsync_ReadsTheField_WhenTheBodyIsAForm()
    {
        AspNetCoreDashboardRequest request = new(CreateHttpContext("POST", "application/x-www-form-urlencoded", "reason=smtp+down&other=1"));

        IList<string> values = await request.GetFormValuesAsync("reason");

        Assert.Equal(["smtp down"], values);
    }

    [Fact]
    public async Task TheMiddleware_PausesAQueue_WhenTheScriptPostsWithoutABody()
    {
        // The Queues page's Pause button: $.post(url) with no reason. It answered 500 before.
        Mock<JobStorage> storage = new();
        Mock<IStorageConnection> connection = new();
        Mock<IWriteOnlyTransaction> transaction = new();
        storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
        connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);

        HttpContext httpContext = CreateHttpContext("POST", contentType: null, body: null);
        httpContext.Request.Path = "/queues/pause/default";
        httpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        DashboardOptions options = new() { Authorization = [] };
        AspNetCoreDashboardMiddleware middleware = new(_ => Task.CompletedTask, storage.Object, options, DashboardRoutes.Routes);

        await middleware.Invoke(httpContext);

        Assert.Equal(204, httpContext.Response.StatusCode);
        transaction.Verify(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Once);
    }

    private static HttpContext CreateHttpContext(string method, string contentType, string body)
    {
        DefaultHttpContext httpContext = new()
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        httpContext.Request.Method = method;
        httpContext.Response.Body = new MemoryStream();

        if (contentType != null)
        {
            httpContext.Request.ContentType = contentType;
        }

        if (body != null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentLength = bytes.Length;
        }

        return httpContext;
    }
}
