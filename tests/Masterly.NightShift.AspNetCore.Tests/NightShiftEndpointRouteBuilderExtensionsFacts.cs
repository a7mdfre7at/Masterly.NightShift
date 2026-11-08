// Endpoint routing (MapNightShiftDashboard...) is in the net8.0 and later builds only
#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
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

        [Fact]
        public void CopyOptions_CopiesEverySettableProperty()
        {
            // A property added to DashboardOptions must also be copied, or MapNightShiftDashboardWithNoAuthorizationFilters loses it
            var source = new DashboardOptions();
            var expected = new Dictionary<string, object>();

            foreach (var property in typeof(DashboardOptions).GetProperties().Where(static p => p.CanRead && p.CanWrite))
            {
                var value = SampleValue(property.PropertyType, property.GetValue(source));
                property.SetValue(source, value);
                expected[property.Name] = value;
            }

            var copy = NightShiftEndpointRouteBuilderExtensions.CopyOptions(source);

            Assert.NotSame(source, copy);
            foreach (var pair in expected)
            {
                Assert.Equal(pair.Value, typeof(DashboardOptions).GetProperty(pair.Key).GetValue(copy));
            }
        }

        private static object SampleValue(Type type, object current)
        {
            if (type == typeof(string)) return "changed";
            if (type == typeof(int)) return (int)current + 7;
            if (type == typeof(bool)) return !(bool)current;
            if (type == typeof(TimeSpan)) return ((TimeSpan)current).Add(TimeSpan.FromSeconds(7));
            if (type == typeof(IEnumerable<IDashboardAuthorizationFilter>)) return new[] { new Mock<IDashboardAuthorizationFilter>().Object };
            if (type == typeof(IEnumerable<IDashboardAsyncAuthorizationFilter>)) return new[] { new Mock<IDashboardAsyncAuthorizationFilter>().Object };
            if (type == typeof(Func<DashboardContext, bool>)) return new Func<DashboardContext, bool>(static _ => true);
            if (type == typeof(Func<DashboardContext, Job, string>)) return new Func<DashboardContext, Job, string>(static (_, _) => "name");
            if (type == typeof(ITimeZoneResolver)) return new Mock<ITimeZoneResolver>().Object;

            throw new NotSupportedException($"Add a sample value for {type} here, and copy the new property in CopyOptions.");
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
#endif
