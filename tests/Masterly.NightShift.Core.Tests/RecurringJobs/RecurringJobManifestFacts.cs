using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;
#pragma warning disable 618

namespace Masterly.NightShift.Core.Tests
{
    public class RecurringJobManifestFacts
    {
        private static readonly string ReportType = typeof(RecurringJobManifestFacts).AssemblyQualifiedName;

        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IRecurringJobManagerV3> _manager = new Mock<IRecurringJobManagerV3>();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, Dictionary<string, string>> _writes = new Dictionary<string, Dictionary<string, string>>();

        public RecurringJobManifestFacts()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(() => new HashSet<string>(_hashes.Keys.Select(k => k.Substring("recurring-job:".Length))));
            _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns<string>(key => _hashes.TryGetValue(key, out var hash) ? hash : null);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _transaction
                .Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((key, pairs) => _writes[key] = pairs.ToDictionary(p => p.Key, p => p.Value));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Report(string name) { }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Connect(string host, [Redacted] string password, int port) { }

        private void AddExisting(string id, string cron, string timeZone = null, bool paused = false, string queue = null, string recurringQueue = "default")
        {
            var job = Job.FromExpression(() => Report(id));
            _hashes["recurring-job:" + id] = new Dictionary<string, string>
            {
                { "Cron", cron },
                { "Job", InvocationData.SerializeJob(queue == null ? job : new Job(job.Type, job.Method, job.Args.ToArray(), queue)).SerializePayload() },
                { "TimeZoneId", timeZone ?? "UTC" },
                { "Queue", recurringQueue },
                { "Paused", paused ? "1" : "0" },
            };
        }

        private static string Manifest(params string[] entries)
        {
            return "{ \"recurringJobs\": [ " + String.Join(", ", entries) + " ] }";
        }

        /// <summary>An entry for <see cref="Report"/>; <paramref name="fields"/> are extra fields, each followed by a comma.</summary>
        private static string Entry(string id, string fields = "")
        {
            return "{ \"id\": \"" + id + "\", \"cron\": \"0 1 * * *\", " + fields +
                "\"job\": { \"type\": \"" + ReportType + "\", \"method\": \"Report\", \"parameterTypes\": \"[\\\"System.String\\\"]\", \"arguments\": \"[\\\"\\\\\\\"x\\\\\\\"\\\"]\" } }";
        }

        private Mock<JobStorage> CreateStorage(bool jobQueues)
        {
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(jobQueues);
            return storage;
        }

        // Apply resolves the zone, and .NET Framework doesn't know IANA time zone ids
#if NETFRAMEWORK
        private const string BerlinTimeZoneId = "W. Europe Standard Time";
#else
        private const string BerlinTimeZoneId = "Europe/Berlin";
#endif

        private static RecurringJobManager CreateManager(JobStorage storage, ITimeZoneResolver timeZoneResolver = null)
        {
            return new RecurringJobManager(storage, new Mock<IBackgroundJobFactory>().Object, timeZoneResolver ?? new DefaultTimeZoneResolver());
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
            Assert.False(entries[0]["paused"].GetValue<bool>());
            Assert.Equal("Europe/Berlin", entries[1]["timeZone"].GetValue<string>());
            Assert.Null(entries[1]["queue"]);
            Assert.True(entries[1]["paused"].GetValue<bool>());
            Assert.Equal("Report", entries[1]["job"]["method"].GetValue<string>());
            Assert.Contains("RecurringJobManifestFacts", entries[1]["job"]["type"].GetValue<string>());
        }

        [Fact]
        public void Export_MasksRedactedArguments_WhenAsked_AndKeepsThemOtherwise()
        {
            var job = Job.FromExpression(() => Connect("db.local", "hunter2", 1433));
            _hashes["recurring-job:connect"] = new Dictionary<string, string>
            {
                { "Cron", "0 * * * *" },
                { "Job", InvocationData.SerializeJob(job).SerializePayload() },
                { "TimeZoneId", "UTC" },
                { "Queue", "default" },
            };

            var masked = RecurringJobManifest.Export(_connection.Object, maskRedacted: true);
            var plain = RecurringJobManifest.Export(_connection.Object);

            Assert.DoesNotContain("hunter2", masked);
            Assert.Contains("db.local", masked);
            Assert.Contains("1433", masked);
            Assert.Contains("hunter2", plain);
        }

        [Fact]
        public void Export_LeavesOutTheArguments_OfAJobThatCannotBeLoaded_WhenMasking()
        {
            _hashes["recurring-job:broken"] = new Dictionary<string, string>
            {
                { "Cron", "0 * * * *" },
                { "Job", "{\"t\":\"Missing.Type, Missing\",\"m\":\"Run\",\"p\":[\"System.String\"],\"a\":[\"\\\"secret\\\"\"]}" },
                { "TimeZoneId", "UTC" },
            };

            var masked = JsonNode.Parse(RecurringJobManifest.Export(_connection.Object, maskRedacted: true));

            var entry = Assert.Single(masked["recurringJobs"].AsArray());
            Assert.Equal("Run", entry["job"]["method"].GetValue<string>());
            Assert.Null(entry["job"]["arguments"]);
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
            AddExisting("nightly", "30 2 * * *", BerlinTimeZoneId, paused: true);
            AddExisting("hourly", "0 * * * *");
            var json = RecurringJobManifest.Export(_connection.Object);
            _hashes.Remove("recurring-job:hourly");

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            Assert.Equal(new[] { "hourly" }, result.Added);
            Assert.Equal(new[] { "nightly" }, result.Updated);
            Assert.Empty(result.Removed);
            Assert.Empty(result.Errors);

            _manager.Verify(x => x.AddOrUpdate("nightly", It.Is<Job>(j => j.Method.Name == "Report" && (string)j.Args[0] == "nightly"), "30 2 * * *",
                It.Is<RecurringJobOptions>(o => o.TimeZone.Id == BerlinTimeZoneId)));
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

        [Fact]
        public void Apply_LeavesThePausedStateAsItIs_WhenTheEntryHasNoPausedField()
        {
            AddExisting("a", "0 1 * * *", paused: true);

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, Manifest(Entry("a")));

            Assert.Equal(new[] { "a" }, result.Updated);
            _manager.Verify(x => x.Pause(It.IsAny<string>()), Times.Never);
            _manager.Verify(x => x.Resume(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Apply_PausesOrResumes_WhenTheEntrySaysSo()
        {
            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, Manifest(
                Entry("paused", "\"paused\": true, "),
                Entry("active", "\"paused\": false, ")));

            Assert.Empty(result.Errors);
            _manager.Verify(x => x.Pause("paused"), Times.Once);
            _manager.Verify(x => x.Resume("active"), Times.Once);
            _manager.Verify(x => x.Resume("paused"), Times.Never);
            _manager.Verify(x => x.Pause("active"), Times.Never);
        }

        [Fact]
        public void Parse_ReadsAMissingPausedField_AsNull()
        {
            var entries = RecurringJobManifest.Parse(Manifest(Entry("a"), Entry("b", "\"paused\": false, ")));

            Assert.Null(entries[0].Paused);
            Assert.False(entries[1].Paused);
        }

        [Fact]
        public void Export_WritesTheQueueTheRunsGoTo_AndOmitsTheDefaultQueue()
        {
            AddExisting("default-queue", "0 * * * *");
            AddExisting("job-queue", "0 * * * *", queue: "reports");
            AddExisting("recurring-queue", "0 * * * *", recurringQueue: "billing");
            AddExisting("both", "0 * * * *", queue: "reports", recurringQueue: "low");

            var entries = JsonNode.Parse(RecurringJobManifest.Export(_connection.Object))["recurringJobs"].AsArray();

            Assert.Equal(new[] { "both", "default-queue", "job-queue", "recurring-queue" }, entries.Select(e => e["id"].GetValue<string>()));
            Assert.Equal("low", entries[0]["queue"].GetValue<string>());
            Assert.Null(entries[1]["queue"]);
            Assert.Equal("reports", entries[2]["queue"].GetValue<string>());
            Assert.Equal("billing", entries[3]["queue"].GetValue<string>());
        }

        [Theory]
        [InlineData("")]
        [InlineData("\"queue\": \"default\", ")]
        [InlineData("\"queue\": \" \", ")]
        public void Apply_RegistersTheJobWithoutAQueue_WhenTheEntryHasNoQueueOrTheDefaultOne(string fields)
        {
            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, Manifest(Entry("a", fields)));

            Assert.Empty(result.Errors);
            _manager.Verify(x => x.AddOrUpdate("a", It.Is<Job>(j => j.Queue == null), "0 1 * * *",
                It.Is<RecurringJobOptions>(o => o.QueueName == "default")));
        }

        [Fact]
        public void Apply_RoundTripsAnExport_OnAStorageWithoutPerJobQueues()
        {
            AddExisting("hourly", "0 * * * *");
            var json = RecurringJobManifest.Export(_connection.Object);
            var manager = CreateManager(CreateStorage(jobQueues: false).Object);

            var result = RecurringJobManifest.Apply(manager, _connection.Object, json);

            Assert.Empty(result.Errors);
            Assert.Equal(new[] { "hourly" }, result.Updated);
        }

        [Fact]
        public void Apply_PutsTheQueueInTheJob_WhenTheStorageSupportsPerJobQueues()
        {
            var manager = CreateManager(CreateStorage(jobQueues: true).Object);

            var result = RecurringJobManifest.Apply(manager, _connection.Object, Manifest(Entry("a", "\"queue\": \"reports\", ")));

            Assert.Empty(result.Errors);
            var written = _writes["recurring-job:a"];
            Assert.Equal("reports", InvocationData.DeserializePayload(written["Job"]).Queue);
            Assert.Equal("default", written["Queue"]);
        }

        [Fact]
        public void Apply_UsesTheRecurringJobsOwnQueue_WhenTheStorageHasNoPerJobQueues()
        {
            var manager = CreateManager(CreateStorage(jobQueues: false).Object);

            var result = RecurringJobManifest.Apply(manager, _connection.Object, Manifest(Entry("a", "\"queue\": \"reports\", ")));

            Assert.Empty(result.Errors);
            var written = _writes["recurring-job:a"];
            Assert.Null(InvocationData.DeserializePayload(written["Job"]).Queue);
            Assert.Equal("reports", written["Queue"]);
        }

        [Fact]
        public void Apply_ResolvesTimeZones_WithTheManagersResolver()
        {
            var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus two", "plus two");
            var resolver = new Mock<ITimeZoneResolver>();
            resolver.Setup(x => x.GetTimeZoneById("plus-two")).Returns(plusTwo);
            var manager = CreateManager(CreateStorage(jobQueues: true).Object, resolver.Object);

            var result = RecurringJobManifest.Apply(manager, _connection.Object, Manifest(Entry("a", "\"timeZone\": \"plus-two\", ")));

            Assert.Empty(result.Errors);
            Assert.Equal("plus-two", _writes["recurring-job:a"]["TimeZoneId"]);
        }

        [Fact, GlobalLock(Reason = "Sets the global time zone resolver")]
        public void Apply_ResolvesTimeZones_WithTheGlobalResolver_WhenTheManagerIsNotARecurringJobManager()
        {
            var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus two", "plus two");
            var resolver = new Mock<ITimeZoneResolver>();
            resolver.Setup(x => x.GetTimeZoneById("plus-two")).Returns(plusTwo);
            GlobalConfiguration.Configuration.UseTimeZoneResolver(resolver.Object);

            try
            {
                var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, Manifest(Entry("a", "\"timeZone\": \"plus-two\", ")));

                Assert.Empty(result.Errors);
                _manager.Verify(x => x.AddOrUpdate("a", It.IsAny<Job>(), "0 1 * * *", It.Is<RecurringJobOptions>(o => o.TimeZone == plusTwo)));
            }
            finally
            {
                GlobalConfiguration.Configuration.UseTimeZoneResolver(null);
            }
        }

        [DataCompatibilityRangeFact]
        public void Export_WritesJobsWhoseTypeCannotBeLoaded_AsStored()
        {
            _hashes["recurring-job:orphan"] = new Dictionary<string, string>
            {
                { "Cron", "0 3 * * *" },
                { "Job", new InvocationData("Missing.Jobs, Missing", "Run", "[\"System.Int32\"]", "[\"42\"]").SerializePayload() },
                { "TimeZoneId", "UTC" },
                { "Queue", "default" },
            };

            var entry = Assert.Single(JsonNode.Parse(RecurringJobManifest.Export(_connection.Object))["recurringJobs"].AsArray());

            Assert.Equal("orphan", entry["id"].GetValue<string>());
            Assert.Equal("0 3 * * *", entry["cron"].GetValue<string>());
            Assert.Equal("Missing.Jobs, Missing", entry["job"]["type"].GetValue<string>());
            Assert.Equal("Run", entry["job"]["method"].GetValue<string>());
            Assert.Equal("[\"System.Int32\"]", entry["job"]["parameterTypes"].GetValue<string>());
            Assert.Equal("[\"42\"]", entry["job"]["arguments"].GetValue<string>());
        }

        [DataCompatibilityRangeFact]
        public void Export_WritesEmptyArrays_ForAStoredMethodWithoutParameters()
        {
            _hashes["recurring-job:orphan"] = new Dictionary<string, string>
            {
                { "Cron", "0 3 * * *" },
                { "Job", new InvocationData("Missing.Jobs, Missing", "Run", "[]", "[]").SerializePayload() },
            };

            var entry = Assert.Single(JsonNode.Parse(RecurringJobManifest.Export(_connection.Object))["recurringJobs"].AsArray());

            Assert.Equal("[]", entry["job"]["parameterTypes"].GetValue<string>());
            Assert.Equal("[]", entry["job"]["arguments"].GetValue<string>());
        }

        [Fact]
        public void Apply_WithPruning_KeepsJobsThatWereExportedAsStored()
        {
            _hashes["recurring-job:orphan"] = new Dictionary<string, string>
            {
                { "Cron", "0 3 * * *" },
                { "Job", new InvocationData("Missing.Jobs, Missing", "Run", "[]", "[]").SerializePayload() },
            };
            var json = RecurringJobManifest.Export(_connection.Object);

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json, prune: true);

            Assert.Empty(result.Removed);
            Assert.Equal("orphan", Assert.Single(result.Errors).Id);
            _manager.Verify(x => x.RemoveIfExists(It.IsAny<string>()), Times.Never);
        }

        [Fact, GlobalLock(Reason = "Sets the log provider")]
        public void Export_LeavesOutAJobWithoutAMethodCall_WithAWarning()
        {
            AddExisting("good", "0 * * * *");
            _hashes["recurring-job:broken"] = new Dictionary<string, string> { { "Cron", "0 3 * * *" } };
            var recording = new RecordingLogProvider();

            // Restores the late-bound provider afterwards, so loggers created by later tests keep working
            using (recording.Use())
            {
                var entries = JsonNode.Parse(RecurringJobManifest.Export(_connection.Object))["recurringJobs"].AsArray();

                Assert.Equal("good", Assert.Single(entries)["id"].GetValue<string>());
            }

            var warning = Assert.Single(recording.Entries, m => m.Level == LogLevel.Warn);
            Assert.Contains("'broken'", warning.Message);
        }

        [Theory]
        [InlineData("1", MisfireHandlingMode.Strict)]
        [InlineData("\"ignorable\"", MisfireHandlingMode.Ignorable)]
        public void Apply_PassesADefinedMisfireHandling_ToTheManager(string value, MisfireHandlingMode expected)
        {
            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, Manifest(Entry("a", "\"misfireHandling\": " + value + ", ")));

            Assert.Equal(new[] { "a" }, result.Added);
            _manager.Verify(x => x.AddOrUpdate("a", It.IsAny<Job>(), "0 1 * * *", It.Is<RecurringJobOptions>(o => o.MisfireHandling == expected)));
        }

        [Fact]
        public void Apply_ReportsAnUndefinedMisfireHandling_WithoutRegisteringTheEntry()
        {
            // The lenient enum reader accepts any number; storing it would leave a job no server can read
            var json = Manifest(Entry("bad", "\"misfireHandling\": 7, "), Entry("good"));

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            var error = Assert.Single(result.Errors);
            Assert.Equal("bad", error.Id);
            Assert.Equal("\"misfireHandling\" 7 is not defined. Use 0 (Relaxed), 1 (Strict) or 2 (Ignorable).", error.Message);
            Assert.Equal(new[] { "good" }, result.Added);
            _manager.Verify(x => x.AddOrUpdate("bad", It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
        }

        [Fact]
        public void Apply_RejectsAMethodTheApplicationDoesNotUseAsAJob()
        {
            var json = Manifest(ManifestOnlyEntry("new-method", typeof(ManifestOnlyJobs), nameof(ManifestOnlyJobs.NeverUsedAsAJob)));

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            var error = Assert.Single(result.Errors);
            Assert.Equal("new-method", error.Id);
            Assert.StartsWith("RecurringJobManifestFacts.ManifestOnlyJobs.NeverUsedAsAJob is not a method this application uses as a job", error.Message);
            Assert.Contains("allowAnyMethod: true", error.Message);
            Assert.Empty(result.Added);
            _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
        }

        [Fact]
        public void Apply_RejectsAFrameworkMethod_ThatNoJobUses()
        {
            var json = Manifest(@"{ ""id"": ""x"", ""cron"": ""0 0 31 2 *"", ""paused"": true,
                ""job"": { ""type"": ""System.Diagnostics.Process, System.Diagnostics.Process"", ""method"": ""Start"",
                           ""parameterTypes"": ""[\""System.String\"",\""System.String\""]"", ""arguments"": ""[\""\\\""cmd.exe\\\""\"",\""\\\""/c echo hi\\\""\""]"" } }");

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            Assert.Contains("Process.Start is not a method this application uses as a job", Assert.Single(result.Errors).Message);
            _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
            _manager.Verify(x => x.Pause(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Apply_AcceptsAnyMethod_WhenAllowed()
        {
            var json = Manifest(ManifestOnlyEntry("new-method", typeof(ManifestOnlyJobs), nameof(ManifestOnlyJobs.NeverUsedAsAJob)));

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json, prune: false, allowAnyMethod: true);

            Assert.Empty(result.Errors);
            Assert.Equal(new[] { "new-method" }, result.Added);
            _manager.Verify(x => x.AddOrUpdate("new-method", It.Is<Job>(j => j.Method.Name == nameof(ManifestOnlyJobs.NeverUsedAsAJob)), "0 1 * * *", It.IsAny<RecurringJobOptions>()));
        }

        [Fact]
        public void Apply_AcceptsTheMethodOfAnExistingRecurringJob_UnderAnotherId()
        {
            var method = typeof(ManifestOnlyJobs).GetMethod(nameof(ManifestOnlyJobs.UsedByAStoredRecurringJob));
            _hashes["recurring-job:existing"] = new Dictionary<string, string>
            {
                { "Cron", "0 1 * * *" },
                { "Job", InvocationData.SerializeJob(new Job(method, "first")).SerializePayload() },
            };
            var json = Manifest(ManifestOnlyEntry("copy", typeof(ManifestOnlyJobs), nameof(ManifestOnlyJobs.UsedByAStoredRecurringJob)));

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            Assert.Empty(result.Errors);
            Assert.Equal(new[] { "copy" }, result.Added);
        }

        [Fact, StaticLock]
        public void Apply_AcceptsAJobCatalogMethod()
        {
            JobCatalog.Add<CatalogOnlyJobs>();
            try
            {
                var json = Manifest(ManifestOnlyEntry("catalog", typeof(CatalogOnlyJobs), nameof(CatalogOnlyJobs.Run)));

                var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

                Assert.Empty(result.Errors);
                Assert.Equal(new[] { "catalog" }, result.Added);
            }
            finally
            {
                JobCatalog.Clear();
            }
        }

        [Fact]
        public void Apply_AcceptsAMethodWithAGeneratedInvoker_WithoutAnyRecurringJob()
        {
            // Report is used in a job expression in this assembly, so the source generator emitted its invoker
            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(RecurringJobManifestFacts).GetMethod(nameof(Report)), out _));

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, Manifest(Entry("generated")));

            Assert.Empty(result.Errors);
            Assert.Equal(new[] { "generated" }, result.Added);
        }

        [Fact]
        public void Export_WritesTheWindowInUtc_AndOmitsItWhenThereIsNone()
        {
            AddExisting("windowed", "0 9 * * *");
            AddExisting("open", "0 9 * * *");
            _hashes["recurring-job:windowed"]["StartAt"] = JobHelper.SerializeDateTime(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
            _hashes["recurring-job:windowed"]["EndAt"] = JobHelper.SerializeDateTime(new DateTime(2026, 12, 31, 22, 59, 59, DateTimeKind.Utc));
            _hashes["recurring-job:open"]["EndAt"] = String.Empty;

            var entries = JsonNode.Parse(RecurringJobManifest.Export(_connection.Object))["recurringJobs"].AsArray();

            Assert.Null(entries[0]["startAt"]);
            Assert.Null(entries[0]["endAt"]);
            Assert.Equal("2026-12-01T00:00:00Z", entries[1]["startAt"].GetValue<string>());
            Assert.Equal("2026-12-31T22:59:59Z", entries[1]["endAt"].GetValue<string>());
        }

        [Fact]
        public void Apply_PassesTheWindow_ToTheManager_ReadingADateWithoutAnOffsetAsUtc()
        {
            var json = Manifest(Entry("a", "\"startAt\": \"2026-12-01T00:00:00+02:00\", \"endAt\": \"2026-12-31T23:00:00\", "));

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            Assert.Empty(result.Errors);
            _manager.Verify(x => x.AddOrUpdate("a", It.IsAny<Job>(), "0 1 * * *", It.Is<RecurringJobOptions>(o =>
                o.StartAt == new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero) &&
                o.EndAt == new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero))));
        }

        [Fact]
        public void Apply_RegistersWithoutAWindow_AnEntryOfAnOlderManifest()
        {
            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, "{ \"version\": 1, \"recurringJobs\": [ " + Entry("a") + " ] }");

            Assert.Empty(result.Errors);
            Assert.Null(RecurringJobManifest.Parse(Manifest(Entry("a")))[0].StartAt);
            _manager.Verify(x => x.AddOrUpdate("a", It.IsAny<Job>(), "0 1 * * *", It.Is<RecurringJobOptions>(o =>
                o.StartAt == null && o.EndAt == null)));
        }

        [Fact]
        public void Apply_RoundTripsTheWindowOfAnExport()
        {
            AddExisting("windowed", "0 9 * * *");
            _hashes["recurring-job:windowed"]["StartAt"] = JobHelper.SerializeDateTime(new DateTime(2026, 12, 1, 0, 0, 0, 250, DateTimeKind.Utc));
            _hashes["recurring-job:windowed"]["EndAt"] = JobHelper.SerializeDateTime(new DateTime(2026, 12, 31, 23, 0, 0, DateTimeKind.Utc));
            var json = RecurringJobManifest.Export(_connection.Object);

            var result = RecurringJobManifest.Apply(_manager.Object, _connection.Object, json);

            Assert.Empty(result.Errors);
            _manager.Verify(x => x.AddOrUpdate("windowed", It.IsAny<Job>(), "0 9 * * *", It.Is<RecurringJobOptions>(o =>
                o.StartAt == new DateTimeOffset(2026, 12, 1, 0, 0, 0, 250, TimeSpan.Zero) &&
                o.EndAt == new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero))));
        }

        [Fact]
        public void Parse_RejectsADateItCannotRead()
        {
            var exception = Assert.Throws<FormatException>(() => RecurringJobManifest.Parse(Manifest(Entry("a", "\"endAt\": \"next friday\", "))));

            Assert.Contains("next friday", exception.Message);
        }

        [Fact]
        public void Apply_ReportsAWindowThatEndsBeforeItStarts_WithoutStoppingTheOtherEntries()
        {
            var storage = CreateStorage(jobQueues: true);
            var json = Manifest(
                Entry("bad", "\"startAt\": \"2026-12-31T00:00:00Z\", \"endAt\": \"2026-12-01T00:00:00Z\", "),
                Entry("good", "\"startAt\": \"2026-12-01T00:00:00Z\", "));

            var result = RecurringJobManifest.Apply(CreateManager(storage.Object), _connection.Object, json, prune: false, allowAnyMethod: true);

            var error = Assert.Single(result.Errors);
            Assert.Equal("bad", error.Id);
            Assert.Contains("must be later than StartAt", error.Message);
            Assert.Equal(new[] { "good" }, result.Added);
            Assert.Equal(
                new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                JobHelper.DeserializeDateTime(_writes["recurring-job:good"]["StartAt"]));
        }

        private static string ManifestOnlyEntry(string id, Type type, string method)
        {
            return "{ \"id\": \"" + id + "\", \"cron\": \"0 1 * * *\", " +
                "\"job\": { \"type\": \"" + type.AssemblyQualifiedName + "\", \"method\": \"" + method + "\", \"parameterTypes\": \"[\\\"System.String\\\"]\", \"arguments\": \"[\\\"\\\\\\\"x\\\\\\\"\\\"]\" } }";
        }

        // None of these methods is used in a job expression, so none has a generated invoker
        public static class ManifestOnlyJobs
        {
            public static void NeverUsedAsAJob(string name) { }

            public static void UsedByAStoredRecurringJob(string name) { }
        }

        public class CatalogOnlyJobs
        {
            public void Run(string name) { }
        }
    }
}
