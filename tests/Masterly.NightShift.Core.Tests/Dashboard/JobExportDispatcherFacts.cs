using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class JobExportDispatcherFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _monitor = new();
    private readonly Mock<JobStorageConnection> _connection = new();

    public JobExportDispatcherFacts()
    {
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.GetAllItemsFromList(It.IsAny<string>())).Returns([]);
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
            Properties = new Dictionary<string, string> { { "RetryCount", "2" }, { JobNotes.ParameterName, SerializationHelper.Serialize(new List<JobNote> { new() { Timestamp = 100, Text = "hi", User = "me" } }, SerializationOption.User) } },
            History = [new() { StateName = "Failed", Reason = "boom", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string>() }],
        });
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/export/7", "/jobs/export/(?<JobId>.+)")
        };

        await new JobExportDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        JsonNode? json = JsonNode.Parse(context.Response.Text);
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
    public async Task JobExport_LeavesOutTheRawArguments_WhenTheMethodCanNotBeLoaded()
    {
        // Without the method, [Redacted] can't be read, so the arguments the storage adds for debugging may hold secrets
        _monitor.Setup(x => x.JobDetails("8")).Returns(new JobDetailsDto
        {
            Job = null,
            LoadException = new JobLoadException("Could not load type", new TypeLoadException()),
            Properties = new Dictionary<string, string>
            {
                { "DBG_Type", "Contoso.Jobs, Contoso" },
                { "DBG_Method", "Connect" },
                { "DBG_Args", "[\"db\",\"hunter2\"]" },
                { "DBG_Payload", "{\"t\":\"Contoso.Jobs\",\"a\":\"[\\\"db\\\",\\\"hunter2\\\"]\"}" },
            },
            History = [],
        });
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/export/8", "/jobs/export/(?<JobId>.+)")
        };

        await new JobExportDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.DoesNotContain("hunter2", context.Response.Text);
        JsonNode? json = JsonNode.Parse(context.Response.Text);
        Assert.Equal("Connect", json["parameters"]["DBG_Method"].GetValue<string>());
        Assert.Null(json["parameters"]["DBG_Args"]);
        Assert.Null(json["parameters"]["DBG_Payload"]);
    }

    [Fact]
    public async Task JobExport_DecodesAnEncodedSlashInTheJobId()
    {
        _monitor.Setup(x => x.JobDetails("tenant/9")).Returns((JobDetailsDto)null);
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/export/tenant%2F9", "/jobs/export/(?<JobId>.+)")
        };

        await new JobExportDispatcher().Dispatch(context);

        _monitor.Verify(x => x.JobDetails("tenant/9"), Times.Once);
    }

    [Fact]
    public async Task JobExport_Answers404_ForUnknownJobs()
    {
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/export/404", "/jobs/export/(?<JobId>.+)")
        };

        await new JobExportDispatcher().Dispatch(context);

        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task ListExport_WritesCsv_PagingThroughTheList()
    {
        List<KeyValuePair<string, FailedJobDto>> failed = [.. Enumerable.Range(1, 600).Select(i => new KeyValuePair<string, FailedJobDto>(i.ToString(), new FailedJobDto
        {
            Job = Job.FromExpression(() => Connect("h", "p")),
            InFailedState = true,
            FailedAt = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
            ExceptionType = "System.TimeoutException",
            ExceptionMessage = i == 1 ? "a \"quoted\", message\nwith a newline" : "=cmd()",
        }))];
        _monitor.Setup(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>())).Returns<int, int>((from, count) => new JobList<FailedJobDto>(failed.Skip(from).Take(count)));
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/failed/export", "/jobs/failed/export")
        };

        await new JobListExportDispatcher("failed").Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("text/csv", context.Response.ContentType);
        string[] lines = context.Response.Text.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Id,State,Job,Time,Seconds,Server,ExceptionType,ExceptionMessage", lines[0]);
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
        _monitor.Setup(x => x.EnqueuedJobs("emails", 0, 500)).Returns(new JobList<EnqueuedJobDto>([new KeyValuePair<string, EnqueuedJobDto>("1", new EnqueuedJobDto { State = "Enqueued", EnqueuedAt = DateTime.UtcNow })]));
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/enqueued/export/emails", "/jobs/enqueued/export/(?<Queue>.+)")
        };

        await new JobListExportDispatcher("enqueued").Dispatch(context);

        Assert.Contains("\r\n1,Enqueued,", context.Response.Text);
    }

    [Fact]
    public async Task JobExport_IncludesTheConsole_InTimeOrder()
    {
        _monitor.Setup(x => x.JobDetails("8")).Returns(new JobDetailsDto { History = [] });
        _connection.Setup(x => x.GetAllItemsFromList("job:8:console")).Returns(
        [
            "{\"t\":2000,\"s\":2,\"c\":\"loaded\"}",
            "{\"t\":1000,\"s\":1,\"l\":\"Info\",\"m\":\"starting\"}",
        ]);
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/export/8", "/jobs/export/(?<JobId>.+)")
        };

        await new JobExportDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        JsonArray console = JsonNode.Parse(context.Response.Text)["console"].AsArray();
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
        DateTime at = new(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        _monitor.Setup(x => x.SucceededJobs(0, 500)).Returns(new JobList<SucceededJobDto>(
        [
            new KeyValuePair<string, SucceededJobDto>("1", new SucceededJobDto { InSucceededState = true, SucceededAt = at, TotalDuration = 1500 }),
            new KeyValuePair<string, SucceededJobDto>("2", new SucceededJobDto { InSucceededState = false }),
            new KeyValuePair<string, SucceededJobDto>("3", null),
        ]));

        string[] lines = await ExportAsync("succeeded", "/jobs/succeeded/export");

        Assert.Equal(new[]
        {
            "Id,State,Job,Time,Seconds,Server,ExceptionType,ExceptionMessage",
            "1,Succeeded,,2026-11-05T01:00:00.000Z,1.5,,,",
            "2,Changed,,,,,,",
            "3,,,,,,,",
        }, lines);
    }

    [Fact]
    public async Task ListExport_WritesScheduledJobs_WithTheDelayUntilTheyRun()
    {
        DateTime enqueueAt = DateTime.UtcNow.AddHours(1);
        _monitor.Setup(x => x.ScheduledJobs(0, 500)).Returns(new JobList<ScheduledJobDto>(
        [
            new KeyValuePair<string, ScheduledJobDto>("1", new ScheduledJobDto { InScheduledState = true, EnqueueAt = enqueueAt }),
            new KeyValuePair<string, ScheduledJobDto>("2", null),
        ]));

        string[] lines = await ExportAsync("scheduled", "/jobs/scheduled/export");

        string[] fields = lines[1].Split(',');
        Assert.Equal("1", fields[0]);
        Assert.Equal("Scheduled", fields[1]);
        Assert.Equal(enqueueAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), fields[3]);
        Assert.InRange(double.Parse(fields[4], CultureInfo.InvariantCulture), 3500, 3600);
        Assert.Equal("2,,,,,,,", lines[2]);
    }

    [Fact]
    public async Task ListExport_WritesProcessingJobs_WithTheElapsedTimeAndTheServer()
    {
        DateTime startedAt = DateTime.UtcNow.AddMinutes(-1);
        _monitor.Setup(x => x.ProcessingJobs(0, 500)).Returns(new JobList<ProcessingJobDto>(
        [
            new KeyValuePair<string, ProcessingJobDto>("1", new ProcessingJobDto { InProcessingState = true, StartedAt = startedAt, ServerId = "web-1:42" }),
            new KeyValuePair<string, ProcessingJobDto>("2", new ProcessingJobDto { InProcessingState = false }),
        ]));

        string[] lines = await ExportAsync("processing", "/jobs/processing/export");

        string[] fields = lines[1].Split(',');
        Assert.Equal("Processing", fields[1]);
        Assert.InRange(double.Parse(fields[4], CultureInfo.InvariantCulture), 59, 120);
        // The server has its own column; it was written under ExceptionType
        Assert.Equal("web-1:42", fields[5]);
        Assert.Equal("", fields[6]);
        Assert.Equal("2,Changed,,,,,,", lines[2]);
    }

    [Fact]
    public async Task ListExport_WritesDeletedJobs_WhenTheMonitorListsThem()
    {
        Mock<JobStorageMonitor> monitor = new();
        monitor.Setup(x => x.DeletedJobs(0, 500)).Returns(new JobList<DeletedJobDto>(
        [
            new KeyValuePair<string, DeletedJobDto>("1", new DeletedJobDto { InDeletedState = true, DeletedAt = new DateTime(2026, 11, 5, 2, 0, 0, DateTimeKind.Utc) }),
            new KeyValuePair<string, DeletedJobDto>("2", new DeletedJobDto { InDeletedState = false }),
        ]));
        _storage.Setup(x => x.GetMonitoringApi()).Returns(monitor.Object);

        string[] lines = await ExportAsync("deleted", "/jobs/deleted/export");

        Assert.Equal(new[]
        {
            "Id,State,Job,Time,Seconds,Server,ExceptionType,ExceptionMessage",
            "1,Deleted,,2026-11-05T02:00:00.000Z,,,,",
            "2,Changed,,,,,,",
        }, lines);
    }

    [Fact]
    public async Task ListExport_WritesOnlyTheHeader_ForDeletedJobs_WhenTheMonitorCanNotListThem()
    {
        string[] lines = await ExportAsync("deleted", "/jobs/deleted/export");

        Assert.Equal(new[] { "Id,State,Job,Time,Seconds,Server,ExceptionType,ExceptionMessage" }, lines);
    }

    [Fact]
    public async Task ListExport_WritesOnlyTheHeader_ForAnUnknownList()
    {
        string[] lines = await ExportAsync("archived", "/jobs/archived/export");

        Assert.Equal(new[] { "Id,State,Job,Time,Seconds,Server,ExceptionType,ExceptionMessage" }, lines);
        _monitor.Verify(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _monitor.Verify(x => x.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ListExport_CutsLongExceptionMessages()
    {
        _monitor.Setup(x => x.FailedJobs(0, 500)).Returns(new JobList<FailedJobDto>(
        [
            new KeyValuePair<string, FailedJobDto>("1", new FailedJobDto { InFailedState = true, ExceptionType = "System.Exception", ExceptionMessage = new string('x', 5000) }),
        ]));

        string[] lines = await ExportAsync("failed", "/jobs/failed/export");

        string[] fields = lines[1].Split(',');
        Assert.Equal("System.Exception", fields[6]);
        Assert.Equal(new string('x', JobListExportDispatcher.MaxMessageLength) + "\u2026", fields[7]);
    }

    [Fact]
    public async Task ListExport_WritesEachPage_AsSoonAsItIsRead()
    {
        // The whole list (up to 10,000 jobs) was held in one string before anything was sent
        List<int> writes = [];
        _monitor.Setup(x => x.SucceededJobs(It.IsAny<int>(), It.IsAny<int>())).Returns<int, int>((from, count) =>
        {
            writes.Add(from);
            return new JobList<SucceededJobDto>(Enumerable.Range(from, from < 1000 ? count : 10).Select(i => new KeyValuePair<string, SucceededJobDto>(i.ToString(), new SucceededJobDto())));
        });
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match("/jobs/succeeded/export", "/jobs/succeeded/export")
        };

        await new JobListExportDispatcher("succeeded").Dispatch(context);

        List<string> chunks = context.Response.Writes;

        Assert.Equal(new[] { 0, 500, 1000 }, writes);
        // The header, then one write per page
        Assert.Equal(4, chunks.Count);
        Assert.Equal(1010, chunks.Skip(1).Sum(c => c.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries).Length));
    }

    private async Task<string[]> ExportAsync(string state, string path)
    {
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub(), _storage.Object)
        {
            UriMatch = Regex.Match(path, path)
        };

        await new JobListExportDispatcher(state).Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        return context.Response.Text.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("-1+1", "\"'-1+1\"")]
    [InlineData("\t=cmd()", "\"'\t=cmd()\"")]
    [InlineData("\r=cmd()", "\"'\r=cmd()\"")]
    [InlineData("a\t=b", "a\t=b")]
    public void Csv_EscapesWhatSpreadsheetsMisread(string value, string expected)
    {
        Assert.Equal(expected, JobListExportDispatcher.Csv(value));
    }
}
