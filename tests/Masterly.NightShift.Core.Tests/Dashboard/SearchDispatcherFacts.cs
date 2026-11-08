using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class SearchDispatcherFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();

        public SearchDispatcherFacts()
        {
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { "nightly-report", "cleanup", "report-weekly" });
            _connection.Setup(x => x.GetAllEntriesFromHash(It.Is<string>(k => k.StartsWith("recurring-job:")))).Returns<string>(key => new Dictionary<string, string>
            {
                { "Cron", "0 1 * * *" },
                { "Job", InvocationData.SerializeJob(Job.FromExpression(() => Sample())).SerializePayload() },
                { "Paused", key.Contains("cleanup") ? "1" : "0" },
            });
        }

        [Fact]
        public async Task Dispatch_ReturnsEmptyLists_ForEmptyQueries()
        {
            var context = Create("");

            await new SearchDispatcher().Dispatch(context);

            var json = JsonNode.Parse(context.Response.Text);
            Assert.Empty(json["jobs"].AsArray());
            Assert.Empty(json["recurringJobs"].AsArray());
        }

        [Fact]
        public async Task Dispatch_FindsRecurringJobs_ContainingTheText_PrefixesFirst()
        {
            var context = Create("report");

            await new SearchDispatcher().Dispatch(context);

            var recurring = JsonNode.Parse(context.Response.Text)["recurringJobs"].AsArray();
            Assert.Equal(2, recurring.Count);
            Assert.Equal("report-weekly", recurring[0]["id"].GetValue<string>());
            Assert.Equal("nightly-report", recurring[1]["id"].GetValue<string>());
            Assert.Equal("0 1 * * *", recurring[0]["cron"].GetValue<string>());
            Assert.False(recurring[0]["paused"].GetValue<bool>());
        }

        [Fact]
        public async Task Dispatch_FindsABackgroundJob_ByExactId()
        {
            _connection.Setup(x => x.GetJobData("12345")).Returns(new JobData
            {
                Job = Job.FromExpression(() => Sample()),
                State = "Succeeded",
                CreatedAt = DateTime.UtcNow,
            });
            var context = Create("12345");

            await new SearchDispatcher().Dispatch(context);

            var jobs = JsonNode.Parse(context.Response.Text)["jobs"].AsArray();
            var job = Assert.Single(jobs);
            Assert.Equal("12345", job["id"].GetValue<string>());
            Assert.Equal("Succeeded", job["state"].GetValue<string>());
            Assert.Contains("Sample", job["name"].GetValue<string>());
            Assert.Contains("/jobs/details/12345", job["url"].GetValue<string>());
        }

        [Fact]
        public async Task Dispatch_TreatsStorageErrorsOnIdLookup_AsNoMatch()
        {
            _connection.Setup(x => x.GetJobData("abc")).Throws(new FormatException());
            var context = Create("abc");

            await new SearchDispatcher().Dispatch(context);

            Assert.Empty(JsonNode.Parse(context.Response.Text)["jobs"].AsArray());
        }

        [Fact]
        public async Task Dispatch_DoesNotLookUpAJob_ForQueriesWithInnerWhitespace()
        {
            var context = Create("nightly report");

            await new SearchDispatcher().Dispatch(context);

            Assert.Empty(JsonNode.Parse(context.Response.Text)["jobs"].AsArray());
            _connection.Verify(x => x.GetJobData(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Dispatch_DoesNotLookUpAJob_ForQueriesLongerThan128Characters()
        {
            var context = Create(new string('7', 129));

            await new SearchDispatcher().Dispatch(context);

            Assert.Empty(JsonNode.Parse(context.Response.Text)["jobs"].AsArray());
            _connection.Verify(x => x.GetJobData(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Dispatch_LooksUpAJob_ForQueriesOfExactly128Characters()
        {
            var id = new string('7', 128);
            var context = Create(id);

            await new SearchDispatcher().Dispatch(context);

            _connection.Verify(x => x.GetJobData(id), Times.Once);
        }

        private DashboardContextStub Create(string query)
        {
            var request = new DashboardRequestStub("GET", "/search");
            request.Query["q"] = query;
            return new DashboardContextStub(new DashboardOptions(), request, _storage.Object);
        }

        [Fact]
        public async Task Dispatch_FindsTags_ContainingTheText_OrAfterTheTagPrefix()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("tags")).Returns(new HashSet<string> { "customer:contoso", "import", "report" });
            _connection.Setup(x => x.GetSetCount(It.IsAny<string>())).Returns(4);

            var context = Create("port");
            await new SearchDispatcher().Dispatch(context);
            var tags = JsonNode.Parse(context.Response.Text)["tags"].AsArray();
            Assert.Equal(new[] { "import", "report" }, tags.Select(t => t["tag"].GetValue<string>()).OrderBy(x => x));
            Assert.Equal(4, tags[0]["count"].GetValue<int>());
            Assert.EndsWith("/tags/import", tags.Single(t => t["tag"].GetValue<string>() == "import")["url"].GetValue<string>());

            var prefixed = Create("tag:contoso");
            await new SearchDispatcher().Dispatch(prefixed);
            var only = JsonNode.Parse(prefixed.Response.Text)["tags"].AsArray();
            Assert.Equal("customer:contoso", Assert.Single(only)["tag"].GetValue<string>());
        }

        [Fact]
        public async Task Dispatch_CountsOnlyTheTagsItReturns()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("tags")).Returns(new HashSet<string>(Enumerable.Range(0, 500).Select(i => $"customer:{i}")));
            _connection.Setup(x => x.GetSetCount(It.IsAny<string>())).Returns(1);

            var context = Create("tag:customer");
            await new SearchDispatcher().Dispatch(context);

            Assert.Equal(10, JsonNode.Parse(context.Response.Text)["tags"].AsArray().Count);
            _connection.Verify(x => x.GetSetCount(It.IsAny<string>()), Times.Exactly(10));
        }

        [Fact]
        public async Task Dispatch_ListsTagsStartingWithTheText_First()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("tags")).Returns(new HashSet<string> { "a-export", "export:eu", "b-export", "export:us" });

            var context = Create("tag:export");
            await new SearchDispatcher().Dispatch(context);

            var tags = JsonNode.Parse(context.Response.Text)["tags"].AsArray().Select(t => t["tag"].GetValue<string>());
            Assert.Equal(new[] { "export:eu", "export:us", "a-export", "b-export" }, tags);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
