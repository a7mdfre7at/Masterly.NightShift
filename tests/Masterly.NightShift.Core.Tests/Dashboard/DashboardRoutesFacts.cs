using System.Reflection;
using System.Text.RegularExpressions;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class DashboardRoutesFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageMonitor> _monitor = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<JobStorageTransaction> _transaction = new();
    private readonly Mock<IBackgroundJobClient> _client = new();
    private readonly Mock<IRecurringJobManagerV3> _manager = new();

    public DashboardRoutesFacts()
    {
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);

        _monitor.Setup(x => x.EnqueuedJobs("critical", 0, 500)).Returns(List("enqueued-1", new EnqueuedJobDto { InEnqueuedState = true }));
        _monitor.Setup(x => x.ScheduledJobs(0, 500)).Returns(List("scheduled-1", new ScheduledJobDto { InScheduledState = true }));
        _monitor.Setup(x => x.ProcessingJobs(0, 500)).Returns(List("processing-1", new ProcessingJobDto { InProcessingState = true }));
        _monitor.Setup(x => x.SucceededJobs(0, 500)).Returns(List("succeeded-1", new SucceededJobDto { InSucceededState = true }));
        _monitor.Setup(x => x.FailedJobs(0, 500)).Returns(List("failed-1", new FailedJobDto { InFailedState = true }));
        _monitor.Setup(x => x.AwaitingJobs(0, 500)).Returns(List("awaiting-1", new AwaitingJobDto { InAwaitingState = true }));
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs)).Returns(true);
    }

    private static readonly Guid Build = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid AnotherBuild = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    // Fixed inputs for the independently computed hashes below, not the current resource names
    private const string JavaScript = "Hangfire.Dashboard.Content.js.hangfire.js";
    private const string Stylesheet = "Hangfire.Dashboard.Content.css.hangfire.css";

    [Fact]
    public void EveryFontTheStylesheetUses_HasARoute_AndAnEmbeddedFile()
    {
        Assembly assembly = typeof(DashboardRoutes).Assembly;
        string css;
        using (Stream? stream = assembly.GetManifestResourceStream(DashboardRoutes.GetContentResourceName("css", "nightshift-ui.css")))
        using (StreamReader reader = new(stream!))
        {
            css = reader.ReadToEnd();
        }

        string[] fonts = [.. Regex.Matches(css, @"url\(fonts/([a-z0-9-]+)/woff2\)").Select(x => x.Groups[1].Value)];

        Assert.Equal(15, fonts.Length);
        Assert.All(fonts, font =>
        {
            Assert.NotNull(DashboardRoutes.Routes.FindDispatcher($"/fonts/{font}/woff2"));
            using Stream? file = assembly.GetManifestResourceStream(DashboardRoutes.GetContentResourceName("fonts", font + ".woff2"));
            Assert.NotNull(file);
        });
    }

    [Fact]
    public void TheIconSprite_HasARoute_ThatChangesWithTheBuild_AndAnEmbeddedFile()
    {
        Assembly assembly = typeof(DashboardRoutes).Assembly;
        using Stream? sprite = assembly.GetManifestResourceStream(DashboardIcons.ResourceName);

        Assert.NotNull(sprite);
        Assert.Equal("Masterly.NightShift.Dashboard.Content.icons.icons.svg", DashboardIcons.ResourceName);
        Assert.NotNull(DashboardRoutes.Routes.FindDispatcher(DashboardIcons.Path));
        Assert.Equal(
            $"/icons{DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, assembly.ManifestModule.ModuleVersionId, DashboardIcons.ResourceName)}",
            DashboardIcons.Path);
    }

    [Theory]
    [InlineData("/fonts/glyphicons-halflings-regular/eot")]
    [InlineData("/fonts/glyphicons-halflings-regular/svg")]
    [InlineData("/fonts/glyphicons-halflings-regular/ttf")]
    [InlineData("/fonts/glyphicons-halflings-regular/woff")]
    [InlineData("/fonts/glyphicons-halflings-regular/woff2")]
    public void TheGlyphiconFonts_AreGone(string path)
    {
        Assert.Null(DashboardRoutes.Routes.FindDispatcher(path));
        Assert.DoesNotContain(typeof(DashboardRoutes).Assembly.GetManifestResourceNames(), static name => name.Contains("glyphicons", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryIconTheScriptDraws_IsInTheSprite()
    {
        string script;
        using (Stream? stream = typeof(DashboardRoutes).Assembly.GetManifestResourceStream(DashboardRoutes.GetContentResourceName("js", "nightshift.js")))
        using (StreamReader reader = new(stream!))
        {
            script = reader.ReadToEnd();
        }

        string[] icons = [.. Regex.Matches(script, @"nightshift\.icon\('([a-z0-9-]+)'").Select(static x => x.Groups[1].Value)];

        Assert.NotEmpty(icons);
        Assert.All(icons, icon => Assert.Contains(icon, DashboardIcons.Names));
        Assert.DoesNotContain("glyphicon", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundles_LoadJQuery4AndBootstrap5_InsteadOfJQuery3AndBootstrap3()
    {
        string[] names = typeof(DashboardRoutes).Assembly.GetManifestResourceNames();

        Assert.Contains(DashboardRoutes.GetContentResourceName("js", "jquery-4.0.0.min.js"), names);
        Assert.Contains(DashboardRoutes.GetContentResourceName("js", "bootstrap.bundle.min.js"), names);
        Assert.DoesNotContain(DashboardRoutes.GetContentResourceName("js", "jquery-3.7.1.min.js"), names);
        Assert.DoesNotContain(DashboardRoutes.GetContentResourceName("js", "bootstrap.min.js"), names);
    }

    [Fact]
    public void TheBundles_LoadChartJs4_WithoutTheStreamingPluginOrChartJs2sStylesheet()
    {
        Assembly assembly = typeof(DashboardRoutes).Assembly;
        string[] names = assembly.GetManifestResourceNames();
        string banner;
        using (Stream? stream = assembly.GetManifestResourceStream(DashboardRoutes.GetContentResourceName("js", "chart.umd.min.js")))
        using (StreamReader reader = new(stream!))
        {
            banner = reader.ReadLine() + "\n" + reader.ReadLine();
        }

        Assert.Equal("/*!\n * Chart.js v4.6.1", banner);
        Assert.DoesNotContain(DashboardRoutes.GetContentResourceName("js", "Chart.min.js"), names);
        Assert.DoesNotContain(DashboardRoutes.GetContentResourceName("js", "chartjs-plugin-streaming.min.js"), names);
        Assert.DoesNotContain(DashboardRoutes.GetContentResourceName("css", "Chart.min.css"), names);
        Assert.DoesNotContain(names, static name => name.Contains("streaming", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CombineResourceHash_IsTheSameInEveryProcessAndOnEveryPlatform()
    {
        // Expected values come from an independent FNV-1a implementation, so a result that depended
        // on per-process randomization (like String.GetHashCode) or on the platform would fail here
        int single = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript);
        int two = DashboardRoutes.CombineResourceHash(single, Build, Stylesheet);

        Assert.Equal(18652613, DashboardRoutes.InitialResourceHash);
        Assert.Equal(1170108145, single);
        Assert.Equal(1179987587, two);
    }

    [Fact]
    public void CombineResourceHash_Changes_WhenTheResourceAssemblyIsRebuilt()
    {
        int original = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript);
        int rebuilt = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, AnotherBuild, JavaScript);

        Assert.Equal(66424875, rebuilt);
        Assert.NotEqual(original, rebuilt);
    }

    [Fact]
    public void CombineResourceHash_DependsOnTheOrderOfResources_AsTheBundleDoes()
    {
        int jsFirst = DashboardRoutes.CombineResourceHash(
            DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript), Build, Stylesheet);
        int cssFirst = DashboardRoutes.CombineResourceHash(
            DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, Stylesheet), Build, JavaScript);

        Assert.Equal(461273807, cssFirst);
        Assert.NotEqual(jsFirst, cssFirst);
    }

    [Fact]
    public void CombineResourceHash_IsNeverNegative_SoMathAbsInTheLayoutCanNotOverflow()
    {
        int hash = DashboardRoutes.InitialResourceHash;

        foreach (int i in Enumerable.Range(0, 10000))
        {
            hash = DashboardRoutes.CombineResourceHash(hash, Guid.NewGuid(), $"resource-{i}");
            Assert.True(hash >= 0);
        }
    }

    [Fact]
    public void BundleHashes_IncludeNightShiftsOwnResources()
    {
        // The static constructor registers the built-in scripts and stylesheets
        Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.JavaScriptsHashCode);
        Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.StylesheetsHashCode);
        Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.StylesheetsDarkModeHashCode);
    }

    [Theory]
    [InlineData("/jobs/enqueued/delete-all/critical", "enqueued-1", "Enqueued")]
    [InlineData("/jobs/scheduled/delete-all", "scheduled-1", "Scheduled")]
    [InlineData("/jobs/processing/delete-all", "processing-1", "Processing")]
    [InlineData("/jobs/succeeded/delete-all", "succeeded-1", "Succeeded")]
    [InlineData("/jobs/failed/delete-all", "failed-1", "Failed")]
    [InlineData("/jobs/awaiting/delete-all", "awaiting-1", "Awaiting")]
    public async Task DeleteAllRoutes_DeleteEveryJobOfTheirList_ExpectingTheListsState(string path, string jobId, string expectedState)
    {
        DashboardContextStub context = Create(path);

        await Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("{\"processed\":1,\"remaining\":0,\"complete\":true}", context.Response.Text);
        _client.Verify(x => x.ChangeState(jobId, It.Is<DeletedState>(s => s.Reason == "Deleted via Dashboard UI (delete all)"), expectedState), Times.Once);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task FailedRequeueAllRoute_RequeuesEveryFailedJob_ExpectingTheFailedState()
    {
        _monitor.Setup(x => x.FailedJobs(0, 500)).Returns(new JobList<FailedJobDto>(
        [
            new KeyValuePair<string, FailedJobDto>("failed-1", new FailedJobDto { InFailedState = true }),
            new KeyValuePair<string, FailedJobDto>("moved-on", new FailedJobDto { InFailedState = false }),
        ]));
        DashboardContextStub context = Create("/jobs/failed/requeue-all");

        await Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        _client.Verify(x => x.ChangeState("failed-1", It.Is<EnqueuedState>(s => s.Reason == "Requeued via Dashboard UI (requeue all)"), FailedState.StateName), Times.Once);
        _client.Verify(x => x.ChangeState("moved-on", It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FailedRequeueAllRoute_Answers401_ForReadOnlyDashboards()
    {
        DashboardContextStub context = Create("/jobs/failed/requeue-all", readOnly: true);

        await Dispatch(context);

        Assert.Equal(401, context.Response.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AwaitingDeleteAllRoute_UsesTheAwaitingSet_WhenTheStorageHasNoAwaitingJobsFeature()
    {
        // Like the Awaiting page: storages without Monitoring.AwaitingJobs keep awaiting jobs in a set
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs)).Returns(false);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        int calls = 0;
        _connection.Setup(x => x.GetRangeFromSet("awaiting", 0, 499)).Returns(() => calls++ == 0 ? ["awaiting-9"] : []);
        DashboardContextStub context = Create("/jobs/awaiting/delete-all");

        await Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        _monitor.Verify(x => x.AwaitingJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _client.Verify(x => x.ChangeState("awaiting-9", It.IsAny<DeletedState>(), AwaitingState.StateName), Times.Once);
    }

    [Fact]
    public async Task AwaitingDeleteAllRoute_Answers422_WhenTheStorageCanNeitherListNorPageASet()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs)).Returns(false);
        _storage.Setup(x => x.GetConnection()).Returns(new Mock<IStorageConnection>().Object);
        DashboardContextStub context = Create("/jobs/awaiting/delete-all");

        await Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EnqueuedDeleteAllRoute_DecodesAnEncodedSlashInTheQueue_Only()
    {
        _monitor.Setup(x => x.EnqueuedJobs("a/b%41", 0, 500)).Returns(List("enqueued-2", new EnqueuedJobDto { InEnqueuedState = true }));
        DashboardContextStub context = Create("/jobs/enqueued/delete-all/a%2Fb%41");

        await Dispatch(context);

        _client.Verify(x => x.ChangeState("enqueued-2", It.IsAny<DeletedState>(), EnqueuedState.StateName), Times.Once);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("requeue")]
    public void EnqueuedPage_OfAQueueNamedLikeACommand_IsReachableWithGet(string queue)
    {
        Tuple<IDashboardDispatcher, System.Text.RegularExpressions.Match>? get = DashboardRoutes.Routes.FindDispatcher("/jobs/enqueued/" + queue, "GET");
        Tuple<IDashboardDispatcher, System.Text.RegularExpressions.Match>? post = DashboardRoutes.Routes.FindDispatcher("/jobs/enqueued/" + queue, "POST");

        Assert.IsType<RazorPageDispatcher>(get.Item1);
        Assert.Equal(queue, get.Item2.Groups["Queue"].Value);
        Assert.IsType<BatchCommandDispatcher>(post.Item1);
    }

    [Fact]
    public void CommandRoutes_StillAnswerGet_WhenNoOtherRouteMatches()
    {
        Tuple<IDashboardDispatcher, System.Text.RegularExpressions.Match>? route = DashboardRoutes.Routes.FindDispatcher("/jobs/failed/delete-all", "GET");

        Assert.IsType<JobListCommandDispatcher>(route.Item1);
    }

    [Fact]
    public async Task JobRequeueRoute_DecodesAnEncodedSlashInTheJobId()
    {
        _connection.Setup(x => x.GetStateData("tenant/42")).Returns(new StateData { Name = FailedState.StateName, Data = new Dictionary<string, string>() });
        _client.Setup(x => x.ChangeState("tenant/42", It.IsAny<IState>(), FailedState.StateName)).Returns(true);
        DashboardContextStub context = Create("/jobs/actions/requeue/tenant%2F42");

        await Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        _client.Verify(x => x.ChangeState("tenant/42", It.IsAny<EnqueuedState>(), FailedState.StateName), Times.Once);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Processing")]
    [InlineData("Succeeded")]
    [InlineData("Deleted")]
    public async Task JobRequeueRoute_RequeuesTheJob_OnlyFromTheStateItIsIn(string state)
    {
        // Two clicks in a row requeue it once: the second finds it Enqueued
        _connection.Setup(x => x.GetStateData("42")).Returns(new StateData { Name = state, Data = new Dictionary<string, string>() });
        _client.Setup(x => x.ChangeState("42", It.IsAny<IState>(), state)).Returns(true);

        await Dispatch(Create("/jobs/actions/requeue/42"));

        _client.Verify(x => x.ChangeState("42", It.IsAny<EnqueuedState>(), state), Times.Once);
        _client.Verify(x => x.ChangeState("42", It.IsAny<IState>(), It.Is<string>(s => s != state)), Times.Never);
    }

    [Theory]
    [InlineData("Enqueued")]
    [InlineData(null)]
    public async Task JobRequeueRoute_LeavesAJobThatWaitsInItsQueue_OrHasNoState(string state)
    {
        // An Enqueued job is in its queue already: requeued, it would get a second entry there
        _connection.Setup(x => x.GetStateData("42")).Returns(state == null ? null : new StateData { Name = state, Data = new Dictionary<string, string>() });
        DashboardContextStub context = Create("/jobs/actions/requeue/42");

        await Dispatch(context);

        Assert.NotEqual(204, context.Response.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAllRoutes_Answer401_ForReadOnlyDashboards()
    {
        DashboardContextStub context = Create("/jobs/failed/delete-all", readOnly: true);

        await Dispatch(context);

        Assert.Equal(401, context.Response.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RecurringPauseRoute_PausesEveryPostedRecurringJob()
    {
        DashboardContextStub context = Create("/recurring/pause", jobs: ["nightly", "weekly"]);

        await Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        _manager.Verify(x => x.Pause("nightly"), Times.Once);
        _manager.Verify(x => x.Pause("weekly"), Times.Once);
        _manager.Verify(x => x.Resume(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RecurringResumeRoute_ResumesEveryPostedRecurringJob()
    {
        DashboardContextStub context = Create("/recurring/resume", jobs: ["nightly"]);

        await Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        _manager.Verify(x => x.Resume("nightly"), Times.Once);
        _manager.Verify(x => x.Pause(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RecurringPauseRoute_Answers422_WithoutJobs()
    {
        DashboardContextStub context = Create("/recurring/pause");

        await Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        _manager.Verify(x => x.Pause(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TagForgetRoute_RemovesTheNormalizedTag()
    {
        // ASP.NET Core has already decoded the path, except "%2F"
        DashboardContextStub context = Create("/tags/forget/Customer:Contoso");

        await Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        _transaction.Verify(x => x.RemoveSet("tags:customer:contoso"), Times.Once);
        _transaction.Verify(x => x.RemoveFromSet(JobTags.IndexKey, "customer:contoso"), Times.Once);
    }

    [Fact]
    public async Task TagForgetRoute_Answers405_ForGet()
    {
        DashboardContextStub context = Create("/tags/forget/import", method: "GET");

        await Dispatch(context);

        Assert.Equal(405, context.Response.StatusCode);
        _transaction.Verify(x => x.RemoveSet(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TagForgetExpiredRoute_ForgetsTheMissingJobsOfThePage_AndKeepsTheOthers()
    {
        _connection.Setup(x => x.GetSetCount("tags:on call")).Returns(3);
        _connection.Setup(x => x.GetRangeFromSet("tags:on call", 0, 2)).Returns(["1", "2", "3"]);
        _connection.Setup(x => x.GetJobData("2")).Returns(new JobData());
        DashboardContextStub context = Create("/tags/forget-expired/on call");
        DashboardRequestStub request = (DashboardRequestStub)context.Request;
        request.Query["from"] = "0";
        request.Query["count"] = "20";

        await Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        _transaction.Verify(x => x.RemoveFromSet("tags:on call", "1"), Times.Once);
        _transaction.Verify(x => x.RemoveFromSet("tags:on call", "3"), Times.Once);
        _transaction.Verify(x => x.RemoveFromSet("tags:on call", "2"), Times.Never);
        _transaction.Verify(x => x.Commit(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task TagForgetExpiredRoute_Answers405_ForGet()
    {
        DashboardContextStub context = Create("/tags/forget-expired/import", method: "GET");

        await Dispatch(context);

        Assert.Equal(405, context.Response.StatusCode);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public async Task TagForgetExpiredRoute_Answers401_ForReadOnlyDashboards()
    {
        DashboardContextStub context = Create("/tags/forget-expired/import", readOnly: true);

        await Dispatch(context);

        Assert.Equal(401, context.Response.StatusCode);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public async Task CircuitResetRoute_ClosesTheCircuit_AndForgetsItsFailures()
    {
        _connection.Setup(x => x.GetAllItemsFromSet("circuit:payments api:failures")).Returns(["f1", "f2"]);
        DashboardContextStub context = Create("/insights/circuits/reset/payments api");

        await Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        _connection.Verify(x => x.AcquireDistributedLock("circuit:payments api:lock", It.IsAny<TimeSpan>()));
        _transaction.Verify(x => x.SetRangeInHash(
            "circuit:payments api",
            It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.ClosedState))));
        _transaction.Verify(x => x.RemoveFromSet("circuit:payments api:failures", "f1"), Times.Once);
        _transaction.Verify(x => x.RemoveFromSet("circuit:payments api:failures", "f2"), Times.Once);
        _transaction.Verify(x => x.Commit(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CircuitResetRoute_Answers401_ForReadOnlyDashboards()
    {
        DashboardContextStub context = Create("/insights/circuits/reset/payments", readOnly: true);

        await Dispatch(context);

        Assert.Equal(401, context.Response.StatusCode);
        _connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Theory]
    [InlineData("/servers/pause/web-1", typeof(ServerPauseDispatcher))]
    [InlineData("/servers/resume/web-1", typeof(ServerPauseDispatcher))]
    [InlineData("/queues/pause/default", typeof(QueuePauseDispatcher))]
    [InlineData("/queues/resume/default", typeof(QueuePauseDispatcher))]
    [InlineData("/maintenance/clear-jobs", typeof(MaintenanceDispatcher))]
    [InlineData("/maintenance/reset", typeof(MaintenanceDispatcher))]
    [InlineData("/maintenance/pause", typeof(DrainModeDispatcher))]
    [InlineData("/maintenance/resume", typeof(DrainModeDispatcher))]
    [InlineData("/catalog/run", typeof(JobCatalogRunDispatcher))]
    [InlineData("/recurring/export", typeof(RecurringJobManifestDispatcher))]
    [InlineData("/recurring/import", typeof(RecurringJobManifestDispatcher))]
    [InlineData("/jobs/failed/export", typeof(JobListExportDispatcher))]
    [InlineData("/jobs/enqueued/export/default", typeof(JobListExportDispatcher))]
    [InlineData("/search", typeof(SearchDispatcher))]
    [InlineData("/ping", typeof(PingDispatcher))]
    public void Routes_MapThePath_ToItsDispatcher(string path, Type dispatcherType)
    {
        Tuple<IDashboardDispatcher, System.Text.RegularExpressions.Match>? route = DashboardRoutes.Routes.FindDispatcher(path);

        Assert.NotNull(route);
        Assert.IsType(dispatcherType, route.Item1);
    }

    private static JobList<T> List<T>(string jobId, T dto)
    {
        return new JobList<T>([new KeyValuePair<string, T>(jobId, dto)]);
    }

    private static Task Dispatch(DashboardContextStub context)
    {
        Tuple<IDashboardDispatcher, System.Text.RegularExpressions.Match>? route = DashboardRoutes.Routes.FindDispatcher(context.Request.Path);
        context.UriMatch = route.Item2;
        return route.Item1.Dispatch(context);
    }

    private DashboardContextStub Create(string path, string method = "POST", bool readOnly = false, string[] jobs = null)
    {
        DashboardRequestStub request = new(method, path);
        if (jobs != null)
        {
            request.Form["jobs[]"] = [.. jobs];
        }

        return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object)
        {
            BackgroundJobClient = _client.Object,
            RecurringJobManager = _manager.Object,
        };
    }
}
