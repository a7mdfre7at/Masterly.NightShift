using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class RecurringJobManifestDispatcherFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<IRecurringJobManagerV3> _manager = new Mock<IRecurringJobManagerV3>();

        public RecurringJobManifestDispatcherFacts()
        {
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { "nightly" });
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:nightly")).Returns(new Dictionary<string, string>
            {
                { "Cron", "0 2 * * *" },
                { "Job", InvocationData.SerializeJob(Job.FromExpression(() => Sample())).SerializePayload() },
            });
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample() { }

        private DashboardContextStub Context(string method, bool readOnly = false, params (string, string)[] form)
        {
            var request = new DashboardRequestStub(method);
            foreach (var (key, value) in form) request.Form[key] = new List<string> { value };
            return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object) { RecurringJobManager = _manager.Object };
        }

        [Fact]
        public async Task Export_WritesTheManifest()
        {
            var context = Context("GET", readOnly: true);

            await new RecurringJobManifestDispatcher(import: false).Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal("application/json", context.Response.ContentType);
            var json = JsonNode.Parse(context.Response.Text);
            Assert.Equal("nightly", json["recurringJobs"][0]["id"].GetValue<string>());
        }

        [Fact]
        public async Task Import_Sets401_WhenReadOnly_And405_ForGet()
        {
            var readOnly = Context("POST", readOnly: true);
            await new RecurringJobManifestDispatcher(import: true).Dispatch(readOnly);
            Assert.Equal(401, readOnly.Response.StatusCode);

            var get = Context("GET");
            await new RecurringJobManifestDispatcher(import: true).Dispatch(get);
            Assert.Equal(405, get.Response.StatusCode);
        }

        [Fact]
        public async Task Import_Sets422_WithoutAManifest_OrWithAnUnreadableOne()
        {
            var empty = Context("POST");
            await new RecurringJobManifestDispatcher(import: true).Dispatch(empty);
            Assert.Equal(422, empty.Response.StatusCode);

            var garbage = Context("POST", form: ("manifest", "nope"));
            await new RecurringJobManifestDispatcher(import: true).Dispatch(garbage);
            Assert.Equal(422, garbage.Response.StatusCode);
            Assert.Contains("JSON", JsonNode.Parse(garbage.Response.Text)["error"].GetValue<string>());
        }

        [Fact]
        public async Task Import_AppliesTheManifest_AndReportsTheOutcome()
        {
            var manifest = "{ \"recurringJobs\": [ { \"id\": \"weekly\", \"cron\": \"0 0 * * 1\", \"job\": { \"type\": \"" + typeof(RecurringJobManifestDispatcherFacts).AssemblyQualifiedName.Replace("\"", "\\\"") + "\", \"method\": \"Sample\" } } ] }";
            var context = Context("POST", form: new[] { ("manifest", manifest), ("prune", "true") });

            await new RecurringJobManifestDispatcher(import: true).Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            var json = JsonNode.Parse(context.Response.Text);
            Assert.Equal("weekly", json["added"][0].GetValue<string>());
            Assert.Equal("nightly", json["removed"][0].GetValue<string>());
            Assert.Empty(json["errors"].AsArray());
            _manager.Verify(x => x.AddOrUpdate("weekly", It.IsAny<Job>(), "0 0 * * 1", It.IsAny<RecurringJobOptions>()));
            _manager.Verify(x => x.RemoveIfExists("nightly"));
        }

        [Fact]
        public async Task Import_ReportsTheEntriesItCouldNotApply_WithoutPruning()
        {
            var manifest = "{ \"recurringJobs\": [ { \"cron\": \"0 0 * * 1\" } ] }";
            var context = Context("POST", form: ("manifest", manifest));

            await new RecurringJobManifestDispatcher(import: true).Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            var json = JsonNode.Parse(context.Response.Text);
            var error = Assert.Single(json["errors"].AsArray());
            Assert.Equal("?", error["id"].GetValue<string>());
            Assert.Equal("An entry has no \"id\".", error["message"].GetValue<string>());
            Assert.Empty(json["added"].AsArray());
            Assert.Empty(json["removed"].AsArray());
            _manager.Verify(x => x.RemoveIfExists(It.IsAny<string>()), Times.Never);
        }
    }
}
