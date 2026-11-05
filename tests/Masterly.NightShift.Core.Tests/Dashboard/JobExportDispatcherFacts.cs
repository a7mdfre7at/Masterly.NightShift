using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class JobExportDispatcherFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();

        public JobExportDispatcherFacts()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetAllItemsFromList(It.IsAny<string>())).Returns(new List<string>());
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Connect(string host, [Redacted] string password) { }

        [Fact]
        public async Task JobExport_WritesTheReport_WithRedactedArguments()
        {
            _monitor.Setup(x => x.JobDetails("7")).Returns(new JobDetailsDto
            {
                Job = Job.FromExpression(() => Connect("db", "secret")),
                CreatedAt = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc),
                Properties = new Dictionary<string, string> { { "RetryCount", "2" }, { JobNotes.ParameterName, SerializationHelper.Serialize(new List<JobNote> { new JobNote { Timestamp = 100, Text = "hi", User = "me" } }, SerializationOption.User) } },
                History = new List<StateHistoryDto> { new StateHistoryDto { StateName = "Failed", Reason = "boom", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string>() } },
            });
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);
            context.UriMatch = Regex.Match("/jobs/export/7", "/jobs/export/(?<JobId>.+)");

            await new JobExportDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            var json = JsonNode.Parse(context.Response.Text);
            Assert.Equal("7", json["id"].GetValue<string>());
            Assert.Equal("Failed", json["state"].GetValue<string>());
            Assert.Equal("Connect", json["job"]["method"].GetValue<string>());
            Assert.Equal("db", json["job"]["arguments"][0]["value"].GetValue<string>());
            Assert.Equal(RedactedAttribute.Mask, json["job"]["arguments"][1]["value"].GetValue<string>());
            Assert.DoesNotContain("secret", context.Response.Text);
            Assert.Equal("2", json["parameters"]["RetryCount"].GetValue<string>());
            Assert.Null(json["parameters"][JobNotes.ParameterName]);
            Assert.Equal("hi", json["notes"][0]["text"].GetValue<string>());
            Assert.Contains("BackgroundJob.Enqueue", json["job"]["code"].GetValue<string>());
        }

        [Fact]
        public async Task JobExport_Answers404_ForUnknownJobs()
        {
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);
            context.UriMatch = Regex.Match("/jobs/export/404", "/jobs/export/(?<JobId>.+)");

            await new JobExportDispatcher().Dispatch(context);

            Assert.Equal(404, context.Response.StatusCode);
        }

        [Fact]
        public async Task ListExport_WritesCsv_PagingThroughTheList()
        {
            var failed = Enumerable.Range(1, 600).Select(i => new KeyValuePair<string, FailedJobDto>(i.ToString(), new FailedJobDto
            {
                Job = Job.FromExpression(() => Connect("h", "p")),
                InFailedState = true,
                FailedAt = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                ExceptionType = "System.TimeoutException",
                ExceptionMessage = i == 1 ? "a \"quoted\", message\nwith a newline" : "=cmd()",
            })).ToList();
            _monitor.Setup(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>())).Returns<int, int>((from, count) => new JobList<FailedJobDto>(failed.Skip(from).Take(count)));
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);
            context.UriMatch = Regex.Match("/jobs/failed/export", "/jobs/failed/export");

            await new JobListExportDispatcher("failed").Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.StartsWith("text/csv", context.Response.ContentType);
            var lines = context.Response.Text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Id,State,Job,Time,Seconds,ExceptionType,ExceptionMessage", lines[0]);
            Assert.Equal(601, lines.Length);
            Assert.Contains("\"a \"\"quoted\"\", message", context.Response.Text);
            Assert.Contains("\"'=cmd()\"", context.Response.Text);
            Assert.StartsWith("1,Failed,", lines[1]);
            _monitor.Verify(x => x.FailedJobs(0, 500));
            _monitor.Verify(x => x.FailedJobs(500, 500));
        }

        [Fact]
        public async Task ListExport_UsesTheQueue_ForEnqueuedJobs()
        {
            _monitor.Setup(x => x.EnqueuedJobs("emails", 0, 500)).Returns(new JobList<EnqueuedJobDto>(new[] { new KeyValuePair<string, EnqueuedJobDto>("1", new EnqueuedJobDto { State = "Enqueued", EnqueuedAt = DateTime.UtcNow }) }));
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);
            context.UriMatch = Regex.Match("/jobs/enqueued/export/emails", "/jobs/enqueued/export/(?<Queue>.+)");

            await new JobListExportDispatcher("enqueued").Dispatch(context);

            Assert.Contains("\r\n1,Enqueued,", context.Response.Text);
        }

        [Fact]
        public async Task JobExport_IncludesTheConsole_InTimeOrder()
        {
            _monitor.Setup(x => x.JobDetails("8")).Returns(new JobDetailsDto { History = new List<StateHistoryDto>() });
            _connection.Setup(x => x.GetAllItemsFromList("job:8:console")).Returns(new List<string>
            {
                "{\"t\":2000,\"s\":2,\"c\":\"loaded\"}",
                "{\"t\":1000,\"s\":1,\"l\":\"Info\",\"m\":\"starting\"}",
            });
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);
            context.UriMatch = Regex.Match("/jobs/export/8", "/jobs/export/(?<JobId>.+)");

            await new JobExportDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            var console = JsonNode.Parse(context.Response.Text)["console"].AsArray();
            Assert.Equal(2, console.Count);
            Assert.Equal("starting", console[0]["message"].GetValue<string>());
            Assert.Equal("Info", console[0]["level"].GetValue<string>());
            Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), console[0]["at"].GetValue<DateTime>().ToUniversalTime());
            Assert.Equal("loaded", console[1]["checkpoint"].GetValue<string>());
            Assert.Null(console[1]["level"]);
            Assert.Null(console[1]["message"]);
        }

        [Fact]
        public async Task ListExport_WritesSucceededJobs_WithTheirDurationInSeconds()
        {
            var at = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
            _monitor.Setup(x => x.SucceededJobs(0, 500)).Returns(new JobList<SucceededJobDto>(new[]
            {
                new KeyValuePair<string, SucceededJobDto>("1", new SucceededJobDto { InSucceededState = true, SucceededAt = at, TotalDuration = 1500 }),
                new KeyValuePair<string, SucceededJobDto>("2", new SucceededJobDto { InSucceededState = false }),
                new KeyValuePair<string, SucceededJobDto>("3", null),
            }));

            var lines = await ExportAsync("succeeded", "/jobs/succeeded/export");

            Assert.Equal(new[]
            {
                "Id,State,Job,Time,Seconds,ExceptionType,ExceptionMessage",
                "1,Succeeded,,2026-11-05T01:00:00.000Z,1.5,,",
                "2,Changed,,,,,",
                "3,,,,,,",
            }, lines);
        }

        [Fact]
        public async Task ListExport_WritesScheduledJobs_WithTheDelayUntilTheyRun()
        {
            var enqueueAt = DateTime.UtcNow.AddHours(1);
            _monitor.Setup(x => x.ScheduledJobs(0, 500)).Returns(new JobList<ScheduledJobDto>(new[]
            {
                new KeyValuePair<string, ScheduledJobDto>("1", new ScheduledJobDto { InScheduledState = true, EnqueueAt = enqueueAt }),
                new KeyValuePair<string, ScheduledJobDto>("2", null),
            }));

            var lines = await ExportAsync("scheduled", "/jobs/scheduled/export");

            var fields = lines[1].Split(',');
            Assert.Equal("1", fields[0]);
            Assert.Equal("Scheduled", fields[1]);
            Assert.Equal(enqueueAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), fields[3]);
            Assert.InRange(Double.Parse(fields[4], CultureInfo.InvariantCulture), 3500, 3600);
            Assert.Equal("2,,,,,,", lines[2]);
        }

        [Fact]
        public async Task ListExport_WritesProcessingJobs_WithTheElapsedTimeAndTheServer()
        {
            var startedAt = DateTime.UtcNow.AddMinutes(-1);
            _monitor.Setup(x => x.ProcessingJobs(0, 500)).Returns(new JobList<ProcessingJobDto>(new[]
            {
                new KeyValuePair<string, ProcessingJobDto>("1", new ProcessingJobDto { InProcessingState = true, StartedAt = startedAt, ServerId = "web-1:42" }),
                new KeyValuePair<string, ProcessingJobDto>("2", new ProcessingJobDto { InProcessingState = false }),
            }));

            var lines = await ExportAsync("processing", "/jobs/processing/export");

            var fields = lines[1].Split(',');
            Assert.Equal("Processing", fields[1]);
            Assert.InRange(Double.Parse(fields[4], CultureInfo.InvariantCulture), 59, 120);
            Assert.Equal("web-1:42", fields[5]);
            Assert.Equal("2,Changed,,,,,", lines[2]);
        }

        [Fact]
        public async Task ListExport_WritesDeletedJobs_WhenTheMonitorListsThem()
        {
            var monitor = new Mock<JobStorageMonitor>();
            monitor.Setup(x => x.DeletedJobs(0, 500)).Returns(new JobList<DeletedJobDto>(new[]
            {
                new KeyValuePair<string, DeletedJobDto>("1", new DeletedJobDto { InDeletedState = true, DeletedAt = new DateTime(2026, 11, 5, 2, 0, 0, DateTimeKind.Utc) }),
                new KeyValuePair<string, DeletedJobDto>("2", new DeletedJobDto { InDeletedState = false }),
            }));
            _storage.Setup(x => x.GetMonitoringApi()).Returns(monitor.Object);

            var lines = await ExportAsync("deleted", "/jobs/deleted/export");

            Assert.Equal(new[]
            {
                "Id,State,Job,Time,Seconds,ExceptionType,ExceptionMessage",
                "1,Deleted,,2026-11-05T02:00:00.000Z,,,",
                "2,Changed,,,,,",
            }, lines);
        }

        [Fact]
        public async Task ListExport_WritesOnlyTheHeader_ForDeletedJobs_WhenTheMonitorCanNotListThem()
        {
            var lines = await ExportAsync("deleted", "/jobs/deleted/export");

            Assert.Equal(new[] { "Id,State,Job,Time,Seconds,ExceptionType,ExceptionMessage" }, lines);
        }

        [Fact]
        public async Task ListExport_WritesOnlyTheHeader_ForAnUnknownList()
        {
            var lines = await ExportAsync("archived", "/jobs/archived/export");

            Assert.Equal(new[] { "Id,State,Job,Time,Seconds,ExceptionType,ExceptionMessage" }, lines);
            _monitor.Verify(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            _monitor.Verify(x => x.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        private async Task<string[]> ExportAsync(string state, string path)
        {
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object);
            context.UriMatch = Regex.Match(path, path);

            await new JobListExportDispatcher(state).Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            return context.Response.Text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("plain", "plain")]
        [InlineData("a,b", "\"a,b\"")]
        [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
        [InlineData("-1+1", "\"'-1+1\"")]
        public void Csv_EscapesWhatSpreadsheetsMisread(string value, string expected)
        {
            Assert.Equal(expected, JobListExportDispatcher.Csv(value));
        }
    }
}
