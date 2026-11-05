using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests
{
    public class NightShiftEndpointRouteBuilderExtensionsFacts
    {
        [Fact]
        public void MapNightShiftDashboard_ThrowsAnException_WhenEndpointsIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => NightShiftEndpointRouteBuilderExtensions.MapNightShiftDashboard(null));

            Assert.Equal("endpoints", exception.ParamName);
        }

        [Fact]
        public void MapNightShiftDashboard_ThrowsAnException_WhenPatternIsNull()
        {
            var app = CreateApplication(addNightShift: true);

            var exception = Assert.Throws<ArgumentNullException>(() => app.MapNightShiftDashboard((string)null));

            Assert.Equal("pattern", exception.ParamName);
        }

        [Fact]
        public void MapNightShiftDashboard_ThrowsAnException_WhenNightShiftServicesWereNotAdded()
        {
            var app = CreateApplication(addNightShift: false);

            var exception = Assert.Throws<InvalidOperationException>(() => app.MapNightShiftDashboard());

            Assert.Contains("AddNightShift", exception.Message);
        }

        [Fact]
        public void MapNightShiftDashboard_MapsEveryPathUnderThePattern()
        {
            var app = CreateApplication(addNightShift: true);

            app.MapNightShiftDashboard("/jobs");

            Assert.Equal("/jobs/{**path}", GetEndpoint(app).RoutePattern.RawText);
        }

        [Fact]
        public async Task MapNightShiftDashboardWithNoAuthorizationFilters_AnswersThePingRoute()
        {
            var app = CreateApplication(addNightShift: true);
            app.MapNightShiftDashboardWithNoAuthorizationFilters();
            var httpContext = new DefaultHttpContext { RequestServices = app.Services };
            httpContext.Request.Method = "GET";
            httpContext.Request.Path = "/nightshift/ping";
            var body = new MemoryStream();
            httpContext.Response.Body = body;

            await GetEndpoint(app).RequestDelegate(httpContext);

            Assert.Equal(200, httpContext.Response.StatusCode);
            Assert.Equal("pong", Encoding.UTF8.GetString(body.ToArray()));
        }

        private static WebApplication CreateApplication(bool addNightShift)
        {
            var builder = WebApplication.CreateBuilder();

            if (addNightShift)
            {
                builder.Services.AddSingleton(new Mock<JobStorage>().Object);
                builder.Services.AddNightShift(_ => { });
            }

            return builder.Build();
        }

        private static RouteEndpoint GetEndpoint(IEndpointRouteBuilder endpoints)
        {
            return endpoints.DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().Single();
        }
    }
}
