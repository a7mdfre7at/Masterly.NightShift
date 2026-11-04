using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.Core.Tests.Stubs;
using Hangfire.Dashboard;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hangfire.Core.Tests.Dashboard
{
    public class LiveJobsDispatcherFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();

        public LiveJobsDispatcherFacts()
        {
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        }

        [Fact]
        public async Task Dispatch_Sets422_WithoutJobIds()
        {
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST", "/jobs/live"), _storage.Object);

            await new LiveJobsDispatcher().Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_ReturnsStateProgressAndConsole_ForProcessingJobs()
        {
            var startedAt = DateTime.UtcNow.AddSeconds(-30);
            _connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData
            {
                Name = ProcessingState.StateName,
                Data = new Dictionary<string, string> { { "ServerId", "server-a" }, { "StartedAt", JobHelper.SerializeDateTime(startedAt) } }
            });
            _connection.Setup(x => x.GetJobParameter("job-1", "Progress")).Returns("{\"p\":40.0,\"m\":\"working\",\"t\":1}");
            _connection.Setup(x => x.GetAllItemsFromList("job:job-1:console")).Returns(new List<string>
            {
                "{\"t\":2,\"s\":2,\"c\":\"loaded\"}",
                "{\"t\":1,\"s\":1,\"l\":\"Info\",\"m\":\"hello\"}",
            });

            var request = new DashboardRequestStub("POST", "/jobs/live");
            request.Form["jobs[]"] = new List<string> { "job-1" };
            var context = new DashboardContextStub(new DashboardOptions(), request, _storage.Object);

            await new LiveJobsDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            var json = JObject.Parse(context.Response.Text);
            Assert.True(json["consoleSupported"].Value<bool>());

            var job = json["jobs"]["job-1"];
            Assert.Equal("Processing", job["state"].Value<string>());
            Assert.Equal("server-a", job["serverId"].Value<string>());
            Assert.InRange(job["elapsedMs"].Value<long>(), 29000, 120000);
            Assert.Equal(40.0, job["progress"]["p"].Value<double>());
            Assert.Equal("working", job["progress"]["m"].Value<string>());

            var entries = (JArray)job["entries"];
            Assert.Equal(2, entries.Count);
            Assert.Equal("hello", entries[0]["m"].Value<string>());
            Assert.Equal("info", entries[0]["l"].Value<string>());
            Assert.Equal("loaded", entries[1]["c"].Value<string>());
        }

        [Fact]
        public async Task Dispatch_ReturnsNull_ForExpiredJobs_AndAcceptsGetQueries()
        {
            _connection.Setup(x => x.GetStateData("gone")).Returns((StateData)null);
            var request = new DashboardRequestStub("GET", "/jobs/live");
            request.Query["jobs"] = "gone";
            var context = new DashboardContextStub(new DashboardOptions(), request, _storage.Object);

            await new LiveJobsDispatcher().Dispatch(context);

            var json = JObject.Parse(context.Response.Text);
            Assert.Equal(JTokenType.Null, json["jobs"]["gone"].Type);
        }

        [Fact]
        public async Task Dispatch_OmitsServerAndElapsed_ForJobsNoLongerProcessing()
        {
            _connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData
            {
                Name = SucceededState.StateName,
                Data = new Dictionary<string, string>()
            });
            _connection.Setup(x => x.GetAllItemsFromList("job:job-2:console")).Returns(new List<string>());
            var request = new DashboardRequestStub("POST", "/jobs/live");
            request.Form["jobs[]"] = new List<string> { "job-2" };
            var context = new DashboardContextStub(new DashboardOptions(), request, _storage.Object);

            await new LiveJobsDispatcher().Dispatch(context);

            var job = JObject.Parse(context.Response.Text)["jobs"]["job-2"];
            Assert.Equal("Succeeded", job["state"].Value<string>());
            Assert.Null(job["serverId"]);
            Assert.Null(job["elapsedMs"]);
        }
    }
}
