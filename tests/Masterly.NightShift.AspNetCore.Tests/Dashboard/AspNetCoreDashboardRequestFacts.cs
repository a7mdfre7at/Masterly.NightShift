using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests.Dashboard
{
    /// <summary>
    /// Reading form fields through the ASP.NET Core adapter, on every target: the net48 build runs it on ASP.NET
    /// Core 2.x, where a real host isn't available to the tests.
    /// </summary>
    public class AspNetCoreDashboardRequestFacts
    {
        [Fact]
        public async Task GetFormValuesAsync_ReturnsNoValues_WhenThePostHasNoBody()
        {
            // What jQuery's $.post(url) sends without data: no body and no Content-Type
            var request = new AspNetCoreDashboardRequest(CreateHttpContext("POST", contentType: null, body: null));

            var values = await request.GetFormValuesAsync("reason");

            Assert.Empty(values);
        }

        [Fact]
        public async Task GetFormValuesAsync_ReturnsNoValues_WhenTheBodyIsNotAForm()
        {
            var request = new AspNetCoreDashboardRequest(CreateHttpContext("POST", "application/json", "{\"reason\":\"x\"}"));

            var values = await request.GetFormValuesAsync("reason");

            Assert.Empty(values);
        }

        [Fact]
        public async Task GetFormValuesAsync_ReturnsNoValues_ForAGet()
        {
            var request = new AspNetCoreDashboardRequest(CreateHttpContext("GET", contentType: null, body: null));

            var values = await request.GetFormValuesAsync("metrics[]");

            Assert.Empty(values);
        }

        [Fact]
        public async Task GetFormValuesAsync_ReadsTheField_WhenTheBodyIsAForm()
        {
            var request = new AspNetCoreDashboardRequest(CreateHttpContext("POST", "application/x-www-form-urlencoded", "reason=smtp+down&other=1"));

            var values = await request.GetFormValuesAsync("reason");

            Assert.Equal(new[] { "smtp down" }, values);
        }

        [Fact]
        public async Task TheMiddleware_PausesAQueue_WhenTheScriptPostsWithoutABody()
        {
            // The Queues page's Pause button: $.post(url) with no reason. It answered 500 before.
            var storage = new Mock<JobStorage>();
            var connection = new Mock<IStorageConnection>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
            connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);

            var httpContext = CreateHttpContext("POST", contentType: null, body: null);
            httpContext.Request.Path = "/queues/pause/default";
            httpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

            var options = new DashboardOptions { Authorization = new IDashboardAuthorizationFilter[0] };
            var middleware = new AspNetCoreDashboardMiddleware(_ => Task.CompletedTask, storage.Object, options, DashboardRoutes.Routes);

            await middleware.Invoke(httpContext);

            Assert.Equal(204, httpContext.Response.StatusCode);
            transaction.Verify(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Once);
        }

        private static HttpContext CreateHttpContext(string method, string contentType, string body)
        {
            var httpContext = new DefaultHttpContext
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
                var bytes = Encoding.UTF8.GetBytes(body);
                httpContext.Request.Body = new MemoryStream(bytes);
                httpContext.Request.ContentLength = bytes.Length;
            }

            return httpContext;
        }
    }
}
