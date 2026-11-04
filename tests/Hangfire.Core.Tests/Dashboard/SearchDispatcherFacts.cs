using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.Core.Tests.Stubs;
using Hangfire.Dashboard;
using Hangfire.Storage;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hangfire.Core.Tests.Dashboard
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

            var json = JObject.Parse(context.Response.Text);
            Assert.Empty((JArray)json["jobs"]);
            Assert.Empty((JArray)json["recurringJobs"]);
        }

        [Fact]
        public async Task Dispatch_FindsRecurringJobs_ContainingTheText_PrefixesFirst()
        {
            var context = Create("report");

            await new SearchDispatcher().Dispatch(context);

            var recurring = (JArray)JObject.Parse(context.Response.Text)["recurringJobs"];
            Assert.Equal(2, recurring.Count);
            Assert.Equal("report-weekly", recurring[0]["id"].Value<string>());
            Assert.Equal("nightly-report", recurring[1]["id"].Value<string>());
            Assert.Equal("0 1 * * *", recurring[0]["cron"].Value<string>());
            Assert.False(recurring[0]["paused"].Value<bool>());
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

            var jobs = (JArray)JObject.Parse(context.Response.Text)["jobs"];
            var job = Assert.Single(jobs);
            Assert.Equal("12345", job["id"].Value<string>());
            Assert.Equal("Succeeded", job["state"].Value<string>());
            Assert.Contains("Sample", job["name"].Value<string>());
            Assert.Contains("/jobs/details/12345", job["url"].Value<string>());
        }

        [Fact]
        public async Task Dispatch_TreatsStorageErrorsOnIdLookup_AsNoMatch()
        {
            _connection.Setup(x => x.GetJobData("abc")).Throws(new FormatException());
            var context = Create("abc");

            await new SearchDispatcher().Dispatch(context);

            Assert.Empty((JArray)JObject.Parse(context.Response.Text)["jobs"]);
        }

        private DashboardContextStub Create(string query)
        {
            var request = new DashboardRequestStub("GET", "/search");
            request.Query["q"] = query;
            return new DashboardContextStub(new DashboardOptions(), request, _storage.Object);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
