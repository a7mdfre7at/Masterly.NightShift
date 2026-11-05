using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class RecurringJobManifestFacts
    {
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IRecurringJobManagerV3> _manager = new Mock<IRecurringJobManagerV3>();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new Dictionary<string, Dictionary<string, string>>();

        public RecurringJobManifestFacts()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(() => new HashSet<string>(_hashes.Keys.Select(k => k.Substring("recurring-job:".Length))));
            _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns<string>(key => _hashes.TryGetValue(key, out var hash) ? hash : null);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Report(string name) { }

        private void AddExisting(string id, string cron, string timeZone = null, bool paused = false, string queue = null)
        {
            var job = Job.FromExpression(() => Report(id));
            _hashes["recurring-job:" + id] = new Dictionary<string, string>
            {
                { "Cron", cron },
                { "Job", InvocationData.SerializeJob(queue == null ? job : new Job(job.Type, job.Method, job.Args.ToArray(), queue)).SerializePayload() },
                { "TimeZoneId", timeZone ?? "UTC" },
                { "Paused", paused ? "1" : "0" },
            };
        }

        [Fact]
        public void Export_WritesEveryRecurringJob_Sorted_WithThePortableJobFormat()
        {
            AddExisting("nightly", "30 2 * * *", "Europe/Berlin", paused: true);
            AddExisting("hourly", "0 * * * *", queue: "reports");

            var json = RecurringJobManifest.Export(_connection.Object);
            var document = JsonNode.Parse(json);

            Assert.Equal(1, document["version"].GetValue<int>());
            var entries = document["recurringJobs"].AsArray();
            Assert.Equal(new[] { "hourly", "nightly" }, entries.Select(e => e["id"].GetValue<string>()));
            Assert.Equal("reports", entries[0]["queue"].GetValue<string>());
            Assert.Null(entries[0]["timeZone"]);
            Assert.Equal("Europe/Berlin", entries[1]["timeZone"].GetValue<string>());
            Assert.True(entries[1]["paused"].GetValue<bool>());
            Assert.Equal("Report", entries[1]["job"]["method"].GetValue<string>());
            Assert.Contains("RecurringJobManifestFacts", entries[1]["job"]["type"].GetValue<string>());
        }

        [Fact]
        public void Parse_RejectsDocumentsItCannotRead()
        {
            Assert.Throws<FormatException>(() => RecurringJobManifest.Parse("not json"));
            Assert.Throws<FormatException>(() => RecurringJobManifest.Parse("{}"));
            Assert.Throws<FormatException>(() => RecurringJobManifest.Parse("{\"version\": 99, \"recurringJobs\": []}"));
            Assert.Empty(RecurringJobManifest.Parse("{\"recurringJobs\": []}"));
        }

        [Fact]
        public void Apply_RoundTripsAnExport_ClassifyingAddedAndUpdated()
        {
            AddExisting("nightly", "30 2 * * *", "Europe/Berlin", paused: true);
            AddExisting("hourly", "0 * * * *");
            var json = RecurringJobManifest.Export(_connection.Object);
            _hashes.Remove("recurring-job:hourly");

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            Assert.Equal(new[] { "hourly" }, result.Added);
            Assert.Equal(new[] { "nightly" }, result.Updated);
            Assert.Empty(result.Removed);
            Assert.Empty(result.Errors);

            _manager.Verify(x => x.AddOrUpdate("nightly", It.Is<Job>(j => j.Method.Name == "Report" && (string)j.Args[0] == "nightly"), "30 2 * * *",
                It.Is<RecurringJobOptions>(o => o.TimeZone.Id == "Europe/Berlin")));
            _manager.Verify(x => x.Pause("nightly"));
            _manager.Verify(x => x.Resume("hourly"));
        }

        [Fact]
        public void Apply_Prunes_WhenAsked_AndReportsBadEntries()
        {
            AddExisting("old", "* * * * *");
            var json = @"{ ""recurringJobs"": [
                { ""id"": ""good"", ""cron"": ""0 1 * * *"", ""job"": { ""type"": """ + typeof(RecurringJobManifestFacts).AssemblyQualifiedName.Replace("\"", "\\\"") + @""", ""method"": ""Report"", ""parameterTypes"": ""[\""System.String\""]"", ""arguments"": ""[\""\\\""x\\\""\""]"" } },
                { ""id"": ""bad-type"", ""cron"": ""0 1 * * *"", ""job"": { ""type"": ""Nope.Missing, Nope"", ""method"": ""Report"" } },
                { ""cron"": ""0 1 * * *"", ""job"": { ""type"": ""x"", ""method"": ""y"" } },
                { ""id"": ""good"", ""cron"": ""0 1 * * *"", ""job"": { ""type"": ""x"", ""method"": ""y"" } },
                { ""id"": ""bad-zone"", ""cron"": ""0 1 * * *"", ""timeZone"": ""Mars/Olympus"", ""job"": { ""type"": """ + typeof(RecurringJobManifestFacts).AssemblyQualifiedName.Replace("\"", "\\\"") + @""", ""method"": ""Report"", ""parameterTypes"": ""[\""System.String\""]"", ""arguments"": ""[\""\\\""x\\\""\""]"" } }
            ] }";

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json, prune: true);

            Assert.Equal(new[] { "good" }, result.Added);
            Assert.Equal(new[] { "old" }, result.Removed);
            Assert.Equal(new[] { "bad-type", "?", "good", "bad-zone" }, result.Errors.Select(e => e.Id));
            _manager.Verify(x => x.RemoveIfExists("old"));
            _manager.Verify(x => x.AddOrUpdate("good", It.IsAny<Job>(), "0 1 * * *", It.IsAny<RecurringJobOptions>()), Times.Once);
        }

        [Theory]
        [InlineData(@"{ ""id"": ""a"", ""cron"": ""* * * * *"" }", "The entry needs a \"job\" with \"type\" and \"method\".")]
        [InlineData(@"{ ""id"": ""a"", ""cron"": ""* * * * *"", ""job"": null }", "The entry needs a \"job\" with \"type\" and \"method\".")]
        [InlineData(@"{ ""id"": ""a"", ""cron"": ""* * * * *"", ""job"": { ""type"": "" "", ""method"": ""Report"" } }", "The entry needs a \"job\" with \"type\" and \"method\".")]
        [InlineData(@"{ ""id"": ""a"", ""cron"": ""* * * * *"", ""job"": { ""type"": ""T, A"", ""method"": """" } }", "The entry needs a \"job\" with \"type\" and \"method\".")]
        [InlineData(@"{ ""id"": ""a"", ""job"": { ""type"": ""T, A"", ""method"": ""Report"" } }", "The entry needs a \"cron\" expression.")]
        [InlineData(@"{ ""id"": ""a"", ""cron"": ""  "", ""job"": { ""type"": ""T, A"", ""method"": ""Report"" } }", "The entry needs a \"cron\" expression.")]
        public void Apply_ReportsEntriesWithoutAJobOrACron_WithoutCallingTheManager(string entry, string message)
        {
            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, "{ \"recurringJobs\": [ " + entry + " ] }");

            var error = Assert.Single(result.Errors);
            Assert.Equal("a", error.Id);
            Assert.Equal(message, error.Message);
            Assert.Empty(result.Added);
            _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
        }

        [Fact]
        public void Apply_ThrowsArgumentNullException_ForMissingArguments()
        {
            Assert.Throws<ArgumentNullException>("manager", () => RecurringJobManifest.Apply(null, _connection.Object, "{}"));
            Assert.Throws<ArgumentNullException>("connection", () => RecurringJobManifest.Apply(_manager.Object, null, "{}"));
            Assert.Throws<ArgumentNullException>("json", () => RecurringJobManifest.Apply(_manager.Object, _connection.Object, null));
        }

        [Fact, GlobalLock(Reason = "Sets JobStorage.Current")]
        public void Export_WithoutAConnection_ReadsJobStorageCurrent_AndDisposesTheConnection()
        {
            AddExisting("hourly", "0 * * * *");
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            JobStorage.Current = storage.Object;

            var json = RecurringJobManifest.Export();

            var entry = Assert.Single(JsonNode.Parse(json)["recurringJobs"].AsArray());
            Assert.Equal("hourly", entry["id"].GetValue<string>());
            Assert.Equal("0 * * * *", entry["cron"].GetValue<string>());
            _connection.Verify(x => x.Dispose());
        }

        [Fact, GlobalLock(Reason = "Sets JobStorage.Current")]
        public void Apply_WithoutAManager_UsesJobStorageCurrent_ToPrune()
        {
            AddExisting("old", "* * * * *");
            var transaction = new Mock<IWriteOnlyTransaction>();
            _connection.Setup(x => x.AcquireDistributedLock("lock:recurring-job:old", It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            JobStorage.Current = storage.Object;

            var result = RecurringJobManifest.Apply("{ \"recurringJobs\": [] }", prune: true);

            Assert.Equal(new[] { "old" }, result.Removed);
            transaction.Verify(x => x.RemoveHash("recurring-job:old"));
            transaction.Verify(x => x.RemoveFromSet("recurring-jobs", "old"));
            transaction.Verify(x => x.Commit());
            _connection.Verify(x => x.Dispose(), Times.AtLeastOnce);
        }

        [Fact]
        public void Apply_DoesNotTouchPauseState_WhenTheManagerCannotPause()
        {
            var manager = new Mock<IRecurringJobManager>();
            var json = "{ \"recurringJobs\": [ { \"id\": \"a\", \"cron\": \"* * * * *\", \"paused\": true, \"job\": { \"type\": \"" + typeof(RecurringJobManifestFacts).AssemblyQualifiedName.Replace("\"", "\\\"") + "\", \"method\": \"Report\", \"parameterTypes\": \"[\\\"System.String\\\"]\", \"arguments\": \"[\\\"\\\\\\\"x\\\\\\\"\\\"]\" } } ] }";

            var result = RecurringJobManifest.Apply(manager.Object, _connection.Object, json);

            Assert.Equal(new[] { "a" }, result.Added);
            manager.Verify(x => x.AddOrUpdate("a", It.IsAny<Job>(), "* * * * *", It.IsAny<RecurringJobOptions>()));
        }
    }
}
