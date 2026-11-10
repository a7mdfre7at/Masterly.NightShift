using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class JobCatalogRunDispatcherFacts : IDisposable
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IBackgroundJobClient> _client = new();
    private readonly Mock<IRecurringJobManager> _manager = new();
    private readonly string _entryId;

    public JobCatalogRunDispatcherFacts()
    {
        JobCatalog.Clear();
        JobCatalog.Add<RunnerJobs>();
        _entryId = JobCatalog.GetEntries().Single(e => e.Type == typeof(RunnerJobs) && e.Method.Name == "Send").Id;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("123");
    }

    public void Dispose()
    {
        JobCatalog.Clear();
    }

    private DashboardContextStub Post(params (string, string)[] form)
    {
        return Context("POST", false, form);
    }

    private DashboardContextStub Context(string method = "POST", bool readOnly = false, (string, string)[] form = null)
    {
        DashboardRequestStub request = new(method);
        foreach ((string? key, string? value) in form ?? []) request.Form[key] = [value];

        return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object)
        {
            BackgroundJobClient = _client.Object,
            RecurringJobManager = _manager.Object,
        };
    }

    [Fact]
    public async Task Dispatch_Sets401_WhenReadOnly()
    {
        DashboardContextStub context = Context(readOnly: true);

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_Sets405_ForGet()
    {
        DashboardContextStub context = Context("GET");

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(405, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_Sets422_WithoutAnId_And404_ForAnUnknownOne()
    {
        DashboardContextStub missing = Context();
        await new JobCatalogRunDispatcher().Dispatch(missing);
        Assert.Equal(422, missing.Response.StatusCode);

        DashboardContextStub unknown = Post(("id", "Nope|Method|"));
        await new JobCatalogRunDispatcher().Dispatch(unknown);
        Assert.Equal(404, unknown.Response.StatusCode);
    }

    [Fact, StaticLock]
    public async Task Dispatch_EnqueuesTheJob_WithConvertedArguments()
    {
        DashboardContextStub context = Post(("id", _entryId), ("mode", "enqueue"), ("queue", "critical"), ("arg:to", "bob"), ("arg:count", "3"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("123", JsonNode.Parse(context.Response.Text)["jobId"].GetValue<string>());
        _client.Verify(x => x.Create(
            It.Is<Job>(j => j.Method.Name == "Send" && (string)j.Args[0] == "bob" && (int)j.Args[1] == 3 && j.Queue == "critical"),
            It.Is<EnqueuedState>(s => s.Queue == "critical")));
    }

    [Fact, StaticLock]
    public async Task Dispatch_SchedulesTheJob_WithADelay()
    {
        DashboardContextStub context = Post(("id", _entryId), ("mode", "schedule"), ("delay", "15"), ("arg:to", "bob"), ("arg:count", "1"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(s => s.EnqueueAt > DateTime.UtcNow.AddMinutes(14) && s.EnqueueAt < DateTime.UtcNow.AddMinutes(16))));
    }

    [Theory, StaticLock]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("1e308")]
    public async Task Dispatch_Sets422_ForABadDelay(string delay)
    {
        DashboardContextStub context = Post(("id", _entryId), ("mode", "schedule"), ("delay", delay), ("arg:to", "bob"), ("arg:count", "1"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Contains("delay", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
    }

    [Fact, StaticLock]
    public async Task Dispatch_SchedulesTheJob_WithADelayPhrase()
    {
        DashboardContextStub context = Post(("id", _entryId), ("mode", "schedule"), ("delay", "in 2 hours"), ("arg:to", "bob"), ("arg:count", "1"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(s => s.EnqueueAt > DateTime.UtcNow.AddMinutes(115) && s.EnqueueAt < DateTime.UtcNow.AddMinutes(125))));
    }

    [Theory, StaticLock]
    [InlineData("whenever you like")]
    [InlineData("")]
    [InlineData("in 99999999 days")]
    [InlineData("in 3000000000 months")]
    public async Task Dispatch_Sets422_ForADelayThatIsNeitherMinutesNorAPhrase(string delay)
    {
        DashboardContextStub context = Post(("id", _entryId), ("mode", "schedule"), ("delay", delay), ("arg:to", "bob"), ("arg:count", "1"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Contains("phrase", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact, StaticLock]
    public async Task Dispatch_Sets422_ForAnUnknownMode()
    {
        DashboardContextStub context = Post(("id", _entryId), ("mode", "teleport"), ("arg:to", "bob"), ("arg:count", "1"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Equal("Unknown mode 'teleport'.", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
    }

    [Fact, StaticLock]
    public async Task Dispatch_CreatesARecurringJob_FromPlainEnglish()
    {
        DashboardContextStub context = Post(("id", _entryId), ("mode", "recurring"), ("recurringId", " nightly "), ("cron", "every day at 3am"), ("arg:to", "ops"), ("arg:count", "2"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("nightly", JsonNode.Parse(context.Response.Text)["recurringId"].GetValue<string>());
        _manager.Verify(x => x.AddOrUpdate("nightly", It.Is<Job>(j => (string)j.Args[0] == "ops"), "0 3 * * *", It.IsAny<RecurringJobOptions>()));
    }

    [Fact, StaticLock]
    public async Task Dispatch_Sets422_ForRecurringJobsWithoutIdOrCron()
    {
        DashboardContextStub noId = Post(("id", _entryId), ("mode", "recurring"), ("cron", "* * * * *"), ("arg:to", "x"), ("arg:count", "1"));
        await new JobCatalogRunDispatcher().Dispatch(noId);
        Assert.Equal(422, noId.Response.StatusCode);

        DashboardContextStub noCron = Post(("id", _entryId), ("mode", "recurring"), ("recurringId", "x"), ("arg:to", "x"), ("arg:count", "1"));
        await new JobCatalogRunDispatcher().Dispatch(noCron);
        Assert.Equal(422, noCron.Response.StatusCode);
    }

    [Fact, StaticLock]
    public async Task Dispatch_Sets422_ForBadArguments()
    {
        DashboardContextStub context = Post(("id", _entryId), ("arg:to", "bob"), ("arg:count", "many"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Contains("count", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact, StaticLock]
    public async Task Dispatch_RunsAnExistingJobAgain_WithTheGivenArguments()
    {
        Mock<IStorageConnection> connection = new();
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
        connection.Setup(x => x.GetJobData("41")).Returns(new JobData { Job = Job.FromExpression<UncataloguedJobs>(x => x.Replay("old", 1)) });
        DashboardContextStub context = Post(("source", "41"), ("arg:name", "new"), ("arg:count", "7"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        _client.Verify(x => x.Create(
            It.Is<Job>(j => j.Type == typeof(UncataloguedJobs) && j.Method.Name == "Replay" && (string)j.Args[0] == "new" && (int)j.Args[1] == 7),
            It.IsAny<EnqueuedState>()));
    }

    [Fact, StaticLock]
    public async Task Dispatch_RunsAnExistingJobAgain_InItsOwnQueue_WhenTheQueueFieldIsEmpty()
    {
        // A job created with Enqueue("emails", ...) and run again landed in "default"
        Mock<IStorageConnection> connection = new();
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
        Job source = new(typeof(UncataloguedJobs), typeof(UncataloguedJobs).GetMethod("Replay"), ["old", 1], "emails");
        connection.Setup(x => x.GetJobData("41")).Returns(new JobData { Job = source });
        DashboardContextStub context = Post(("source", "41"), ("arg:name", "new"), ("arg:count", "7"), ("queue", ""));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        _client.Verify(x => x.Create(It.Is<Job>(j => j.Queue == "emails"), It.Is<EnqueuedState>(s => s.Queue == "emails")));
    }

    [Fact, StaticLock]
    public async Task Dispatch_UsesTheQueueGiven_ForAnExistingJob()
    {
        Mock<IStorageConnection> connection = new();
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
        Job source = new(typeof(UncataloguedJobs), typeof(UncataloguedJobs).GetMethod("Replay"), ["old", 1], "emails");
        connection.Setup(x => x.GetJobData("41")).Returns(new JobData { Job = source });
        DashboardContextStub context = Post(("source", "41"), ("arg:name", "new"), ("arg:count", "7"), ("queue", "critical"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        _client.Verify(x => x.Create(It.Is<Job>(j => j.Queue == "critical"), It.Is<EnqueuedState>(s => s.Queue == "critical")));
    }

    [Fact, StaticLock]
    public async Task Dispatch_Sets422_NamingWhatCanNotBeLoaded_WhenTheSourceJobsTypeIsMissing()
    {
        // A dashboard hosted without the job's assemblies answered 404, as if the job didn't exist
        Mock<IStorageConnection> connection = new();
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
        connection.Setup(x => x.GetJobData("41")).Returns(new JobData
        {
            LoadException = new JobLoadException("Could not load the job.", new InvalidOperationException("Could not resolve assembly 'Shop.Web'.")),
        });
        DashboardContextStub context = Post(("source", "41"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Contains("Shop.Web", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact, StaticLock]
    public async Task Dispatch_Sets404_WhenTheSourceJobIsGone()
    {
        Mock<IStorageConnection> connection = new();
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
        DashboardContextStub context = Post(("source", "404"));

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact, StaticLock]
    public async Task Dispatch_GivesFalse_ForAnUncheckedBoxWhoseDefaultIsTrue_AndTrue_ForACheckedOne()
    {
        // An unchecked box sent nothing, and nothing meant the default, true
        Job uncheckedBox = await RunAgainAsync(x => x.Flag(true), ("arg:dryRun", "false"));
        Job checkedBox = await RunAgainAsync(x => x.Flag(false), ("arg:dryRun", "false"), ("arg:dryRun", "true"));

        Assert.Equal(false, uncheckedBox.Args[0]);
        Assert.Equal(true, checkedBox.Args[0]);
    }

    [Fact, StaticLock]
    public async Task Dispatch_CombinesTheChoicesOfAFlagsEnum_AndGivesNoFlags_WhenNothingIsChosen()
    {
        Job combined = await RunAgainAsync(x => x.Flags(BindingFlags.Static), ("arg:flags", "0"), ("arg:flags", "Instance"), ("arg:flags", "Public"));
        Job none = await RunAgainAsync(x => x.Flags(BindingFlags.Static), ("arg:flags", "0"));

        Assert.Equal(BindingFlags.Instance | BindingFlags.Public, combined.Args[0]);
        Assert.Equal(BindingFlags.Default, none.Args[0]);
    }

    [Fact, StaticLock]
    public async Task Dispatch_GivesNull_ForTheNullChoiceOfABoolOrEnumThatIsNullable()
    {
        Job job = await RunAgainAsync(x => x.Maybe(true, DayOfWeek.Friday), ("arg:enabled", "null"), ("arg:day", "null"));

        Assert.Null(job.Args[0]);
        Assert.Null(job.Args[1]);
    }

    private async Task<Job> RunAgainAsync(Expression<Action<FormJobs>> source, params (string, string)[] form)
    {
        Mock<IStorageConnection> connection = new();
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
        connection.Setup(x => x.GetJobData("41")).Returns(new JobData { Job = Job.FromExpression(source) });

        DashboardRequestStub request = new("POST");
        request.Form["source"] = ["41"];
        foreach ((string? key, string? value) in form)
        {
            if (!request.Form.TryGetValue(key, out List<string>? values)) request.Form[key] = values = [];
            values.Add(value);
        }

        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object) { BackgroundJobClient = _client.Object };
        Job created = null;
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((job, _) => created = job).Returns("124");

        await new JobCatalogRunDispatcher().Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        return created;
    }

    public class FormJobs
    {
        public void Flag(bool dryRun = true) { }
        public void Flags(BindingFlags flags) { }
        public void Maybe(bool? enabled = true, DayOfWeek? day = DayOfWeek.Monday) { }
    }

    public class UncataloguedJobs
    {
        public void Replay(string name, int count) { }
    }

    public class RunnerJobs
    {
        public void Send(string to, int count) { }
    }
}
