using System.Text.Json.Nodes;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class LiveJobsDispatcherFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();

    public LiveJobsDispatcherFacts()
    {
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
    }

    [Fact]
    public async Task Dispatch_Sets422_WithoutJobIds()
    {
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub("POST", "/jobs/live"), _storage.Object);

        await new LiveJobsDispatcher().Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ReturnsStateProgressAndConsole_ForProcessingJobs()
    {
        DateTime startedAt = DateTime.UtcNow.AddSeconds(-30);
        _connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData
        {
            Name = ProcessingState.StateName,
            Data = new Dictionary<string, string> { { "ServerId", "server-a" }, { "StartedAt", JobHelper.SerializeDateTime(startedAt) } }
        });
        _connection.Setup(x => x.GetJobParameter("job-1", "Progress")).Returns("{\"p\":40.0,\"m\":\"working\",\"t\":1}");
        _connection.Setup(x => x.GetAllItemsFromList("job:job-1:console")).Returns(
        [
            "{\"t\":2,\"s\":2,\"c\":\"loaded\"}",
            "{\"t\":1,\"s\":1,\"l\":\"Info\",\"m\":\"hello\"}",
        ]);

        DashboardRequestStub request = new("POST", "/jobs/live");
        request.Form["jobs[]"] = ["job-1"];
        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object);

        await new LiveJobsDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        JsonNode? json = JsonNode.Parse(context.Response.Text);
        Assert.True(json["consoleSupported"].GetValue<bool>());

        JsonNode? job = json["jobs"]["job-1"];
        Assert.Equal("Processing", job["state"].GetValue<string>());
        Assert.Equal("server-a", job["serverId"].GetValue<string>());
        Assert.InRange(job["elapsedMs"].GetValue<long>(), 29000, 120000);
        Assert.Equal(40.0, job["progress"]["p"].GetValue<double>());
        Assert.Equal("working", job["progress"]["m"].GetValue<string>());

        JsonArray entries = job["entries"].AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Equal("hello", entries[0]["m"].GetValue<string>());
        Assert.Equal("info", entries[0]["l"].GetValue<string>());
        Assert.Equal("loaded", entries[1]["c"].GetValue<string>());
    }

    [Fact]
    public async Task Dispatch_DoesNotReadTheConsole_WhenAskedForStateAndProgressOnly()
    {
        // The Processing page's rows polled every job's whole console every two seconds, to show progress
        _connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = ProcessingState.StateName, Data = new Dictionary<string, string>() });
        _connection.Setup(x => x.GetJobParameter("job-1", "Progress")).Returns("{\"p\":40.0,\"m\":\"working\",\"t\":1}");
        DashboardRequestStub request = new("POST", "/jobs/live");
        request.Form["jobs[]"] = ["job-1"];
        request.Form["console"] = ["0"];
        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object);

        await new LiveJobsDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        JsonNode? job = JsonNode.Parse(context.Response.Text)["jobs"]["job-1"];
        Assert.Equal(40.0, job["progress"]["p"].GetValue<double>());
        Assert.Null(job["entries"]);
        _connection.Verify(x => x.GetAllItemsFromList(It.IsAny<string>()), Times.Never);
        _connection.Verify(x => x.GetRangeFromList(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_MeasuresElapsedTime_WithTheStorageClock_WhenSupported()
    {
        DateTime storageNow = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);
        _connection.Setup(x => x.GetUtcDateTime()).Returns(storageNow);
        _connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData
        {
            Name = ProcessingState.StateName,
            Data = new Dictionary<string, string> { { "ServerId", "server-a" }, { "StartedAt", JobHelper.SerializeDateTime(storageNow.AddSeconds(-90)) } }
        });
        _connection.Setup(x => x.GetAllItemsFromList("job:job-1:console")).Returns([]);
        DashboardRequestStub request = new("POST", "/jobs/live");
        request.Form["jobs[]"] = ["job-1"];
        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object);

        await new LiveJobsDispatcher().Dispatch(context);

        JsonNode? json = JsonNode.Parse(context.Response.Text);
        Assert.Equal(JobHelper.ToTimestamp(storageNow), json["now"].GetValue<long>());
        Assert.Equal(90000, json["jobs"]["job-1"]["elapsedMs"].GetValue<long>());
        _connection.Verify(x => x.GetUtcDateTime(), Times.Once);
    }

    [Fact]
    public async Task Dispatch_ReturnsNull_ForExpiredJobs_AndAcceptsGetQueries()
    {
        _connection.Setup(x => x.GetStateData("gone")).Returns((StateData)null);
        DashboardRequestStub request = new("GET", "/jobs/live");
        request.Query["jobs"] = "gone";
        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object);

        await new LiveJobsDispatcher().Dispatch(context);

        JsonNode? json = JsonNode.Parse(context.Response.Text);
        Assert.Null(json["jobs"]["gone"]);
    }

    [Fact]
    public async Task Dispatch_OmitsServerAndElapsed_ForJobsNoLongerProcessing()
    {
        _connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData
        {
            Name = SucceededState.StateName,
            Data = new Dictionary<string, string>()
        });
        _connection.Setup(x => x.GetAllItemsFromList("job:job-2:console")).Returns([]);
        DashboardRequestStub request = new("POST", "/jobs/live");
        request.Form["jobs[]"] = ["job-2"];
        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object);

        await new LiveJobsDispatcher().Dispatch(context);

        JsonNode? job = JsonNode.Parse(context.Response.Text)["jobs"]["job-2"];
        Assert.Equal("Succeeded", job["state"].GetValue<string>());
        Assert.Null(job["serverId"]);
        Assert.Null(job["elapsedMs"]);
    }
}
