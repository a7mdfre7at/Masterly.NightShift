using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class ActivityDispatcherFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();

        public ActivityDispatcherFacts()
        {
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, It.IsAny<int>())).Returns(new List<string>
            {
                "{\"t\":3000,\"j\":\"c\",\"s\":\"Succeeded\"}",
                "{\"t\":2000,\"j\":\"b\",\"s\":\"Processing\",\"v\":\"srv\"}",
                "{\"t\":1000,\"j\":\"a\",\"s\":\"Enqueued\"}",
            });
        }

        [Fact]
        public async Task Dispatch_ReturnsTheNewestEntries_WithNow()
        {
            var context = new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => true }, new DashboardRequestStub("GET"), _storage.Object);

            await new ActivityDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal("application/json", context.Response.ContentType);
            var json = JsonNode.Parse(context.Response.Text);
            Assert.True(json["now"].GetValue<long>() > 0);
            Assert.Equal(ActivityFeed.Enabled, json["enabled"].GetValue<bool>());
            Assert.Equal(3, json["entries"].AsArray().Count);
            Assert.Equal("c", json["entries"][0]["j"].GetValue<string>());
            _connection.Verify(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, 49));
        }

        [Fact]
        public async Task Dispatch_HonoursCount_AndSince()
        {
            var request = new DashboardRequestStub("GET");
            request.Query["count"] = "10";
            request.Query["since"] = "1500";
            var context = new DashboardContextStub(new DashboardOptions(), request, _storage.Object);

            await new ActivityDispatcher().Dispatch(context);

            var entries = JsonNode.Parse(context.Response.Text)["entries"];
            Assert.Equal(2, entries.AsArray().Count);
            Assert.Equal("b", entries[1]["j"].GetValue<string>());
            _connection.Verify(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, 9));
        }

        [Fact]
        public async Task Dispatch_ClampsTheCount()
        {
            var request = new DashboardRequestStub("GET");
            request.Query["count"] = "100000";
            var context = new DashboardContextStub(new DashboardOptions(), request, _storage.Object);

            await new ActivityDispatcher().Dispatch(context);

            _connection.Verify(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, 199));
        }
    }
}
