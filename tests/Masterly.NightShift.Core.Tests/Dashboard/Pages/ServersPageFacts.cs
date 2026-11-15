using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class ServersPageFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_ShowsAnEmptyState_AndHostingDetails_WhenNoServerIsRegistered()
    {
        string html = _renderer.Render(new ServersPage());

        Assert.Contains("<h2 class=\"ns-empty-state-title\">" + H(Strings.ServersPage_NoServers_Title) + "</h2><p>" + H(Strings.ServersPage_NoServers_Next) + "</p>", html);
        Assert.Contains("<span class=\"ns-page-name\">" + H(Strings.ServersPage_Title) + "</span> <span class=\"ns-count-chip js-list-count\">0</span></h1>", html);
        Assert.DoesNotContain("ns-server-card", html);
        PageStructure.AssertHeadingsInOrder(html);
        Assert.Contains("id=\"servers-hosting\"", html);
        Assert.Contains(H(Strings.ServersPage_Hosting_Uptime), html);
        Assert.Contains("invoker-stats", html);
        Assert.DoesNotContain(H(Strings.ServersPage_Hosting_IisWarning_Html), html);
    }

    [Fact]
    public void Render_MarksEachServer_ByHowLongAgoItsHeartbeatWas()
    {
        DateTime now = DateTime.UtcNow;
        SetupServers(
            Server("fresh-server", now),
            Server("late-server", now.AddSeconds(-90)),
            Server("dead-server", now.AddMinutes(-10)),
            Server("silent-server", null));

        string html = _renderer.Render(new ServersPage());

        Assert.Equal(2, CountOf(html, "<span class=\"ns-chip ns-chip-status ns-server-status\"><span class=\"ns-dot ns-state-succeeded\" aria-hidden=\"true\"></span> " + H(Strings.ServersPage_Active) + "</span>"));
        // Past ServerPossiblyAbortedThreshold (one minute) is possibly aborted, as on the job pages: its card is muted
        Assert.Equal(2, CountOf(html, "<section class=\"hf-card ns-server-card ns-server-stale\""));
        Assert.Equal(2, CountOf(html, "ns-server-status-stale\">" + DashboardMarkup.Icon("exclamation-triangle-fill") + " " + H(Strings.ServersPage_Possibly_Aborted)));
        Assert.Contains(H(Strings.ServersPage_Note_Title), html);
        Assert.Contains("/jobs/processing", html);
        Assert.Contains("fresh-server", html);
        Assert.Contains("silent-server", html);
    }

    [Fact]
    public void Render_DoesNotShowTheAbortedNote_WhenEveryServerIsAlive()
    {
        SetupServers(Server("fresh-server", DateTime.UtcNow));

        string html = _renderer.Render(new ServersPage());

        Assert.DoesNotContain(H(Strings.ServersPage_Note_Title), html);
        Assert.DoesNotContain("ns-server-stale", html);
    }

    [Fact]
    public void Render_ShowsACardPerServer_WithItsQueuesTimesAndBusyWorkers()
    {
        SetupServers(Server("web-1:42:abcd", DateTime.UtcNow, "critical", "default"));
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Processing = 3 });

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("<h2 id=\"server-0\" class=\"ns-server-name\">web-1:42</h2>", html);
        Assert.Contains("<code class=\"ns-server-id\">web-1:42:abcd</code>", html);
        Assert.Contains("<strong>" + H(string.Format(Strings.ServersPage_WorkersBusy, 3, 5)) + "</strong>", html);
        Assert.Contains("role=\"meter\" aria-label=\"" + H(Strings.ServersPage_BusyWorkers) + "\" aria-valuemin=\"0\" aria-valuemax=\"5\" aria-valuenow=\"3\"", html);
        Assert.Contains("<span style=\"width: 60%\"></span>", html);
        Assert.Contains("<a class=\"ns-queue-chip\" href=\"/jobs/enqueued/critical\">", html);
        Assert.Contains("<a class=\"ns-queue-chip\" href=\"/jobs/enqueued/default\">", html);
        Assert.Contains("<span class=\"ns-time-exact\"><span data-moment-local=", html);
        Assert.Contains(H(string.Format(Strings.WallboardPage_Workers, 5)), html);
        // One server: its busy workers are the processing jobs the statistics count, without another query
        _renderer.Monitoring.Verify(x => x.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        PageStructure.AssertHeadingsInOrder(html);
    }

    [Fact]
    public void Render_CountsTheBusyWorkersOfEachServer_FromOneBoundedReadOfTheProcessingJobs()
    {
        SetupServers(Server("a", DateTime.UtcNow), Server("b", DateTime.UtcNow));
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Processing = 3 });
        _renderer.Monitoring.Setup(x => x.ProcessingJobs(0, 3)).Returns(new JobList<ProcessingJobDto?>(
        [
            new KeyValuePair<string, ProcessingJobDto?>("1", new ProcessingJobDto { ServerId = "a", InProcessingState = true }),
            new KeyValuePair<string, ProcessingJobDto?>("2", new ProcessingJobDto { ServerId = "a", InProcessingState = true }),
            new KeyValuePair<string, ProcessingJobDto?>("3", new ProcessingJobDto { ServerId = "b", InProcessingState = true }),
        ]));

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("aria-valuemax=\"5\" aria-valuenow=\"2\"", html);
        Assert.Contains("aria-valuemax=\"5\" aria-valuenow=\"1\"", html);
        _renderer.Monitoring.Verify(x => x.ProcessingJobs(0, 3), Times.Once);
    }

    [Fact]
    public void Render_PutsPausingAndClearing_InADangerZone_SetApartInTheErrorColour()
    {
        _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
        SetupServers(Server("fresh-server", DateTime.UtcNow));

        string html = _renderer.Render(new ServersPage());

        int zone = html.IndexOf("<section class=\"ns-danger-zone\" aria-labelledby=\"servers-danger\">", StringComparison.Ordinal);
        Assert.True(zone > html.IndexOf("id=\"servers-hosting\"", StringComparison.Ordinal), "the danger zone comes last");
        Assert.Contains("<h2 id=\"servers-danger\">" + H(Strings.Common_DangerZone) + "</h2>", html);
        Assert.True(html.IndexOf("js-drain-form", StringComparison.Ordinal) > zone);
        Assert.True(html.IndexOf("data-ajax=\"/maintenance/clear-jobs\"", StringComparison.Ordinal) > zone);
        Assert.True(html.IndexOf("data-ajax=\"/maintenance/reset\"", StringComparison.Ordinal) > zone);
        Assert.Contains("<button type=\"submit\" class=\"btn btn-sm ns-btn-outlined-error\"", html);
        Assert.Contains("<button class=\"btn btn-sm ns-btn-outlined-error\" data-ajax=\"/maintenance/clear-jobs\"", html);
        Assert.Contains("data-confirm=\"" + H(Strings.ServersPage_ClearJobsConfirm_Title) + "\"", html);
        Assert.Contains("data-confirm=\"" + H(Strings.ServersPage_ResetStorageConfirm_Title) + "\"", html);
        Assert.Contains("data-confirm=\"" + H(Strings.ServersPage_PauseConfirm) + "\"", html);
        Assert.Contains("aria-label=\"" + H(Strings.ServersPage_Pause_Reason) + "\"", html);
        PageStructure.AssertHeadingsInOrder(html);
    }

    [Fact]
    public void Render_ListsTheQueuesOfAServer()
    {
        SetupServers(Server("fresh-server", DateTime.UtcNow, "critical", "default"));

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("critical", html);
        Assert.Contains("default", html);
    }

    [Fact]
    public void Render_ShowsPauseButtons_ForRunningServers()
    {
        SetupServers(Server("fresh-server", DateTime.UtcNow));

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("data-ajax=\"/servers/pause/fresh-server\"", html);
        Assert.DoesNotContain("/servers/resume/", html);
        Assert.DoesNotContain(H(Strings.ServersPage_Paused) + "</span>", html);
    }

    [Fact]
    public void Render_ShowsThePausedLabel_AndAResumeButton_ForAPausedServer()
    {
        SetupServers(Server("paused server", DateTime.UtcNow), Server("fresh-server", DateTime.UtcNow));
        SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "paused server", "disk swap" } });

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("ns-server-status-paused\" title=\"disk swap\">" + DashboardMarkup.Icon("pause-fill") + " " + H(Strings.ServersPage_Paused) + "</span>", html);
        Assert.Contains("<dd><em>disk swap</em></dd>", html);
        Assert.Contains("data-ajax=\"/servers/resume/paused%20server\"", html);
        Assert.Contains("data-ajax=\"/servers/pause/fresh-server\"", html);
    }

    [Fact]
    public void Render_ListsPausesLeftByServersThatAreGone_WithAClearButton()
    {
        SetupServers(Server("web-1:2:new", DateTime.UtcNow));
        SetupControlHash(new Dictionary<string, string>
        {
            { DrainMode.ServerFieldPrefix + "web-1:1:old", "before deploy" },
            { DrainMode.ServerFieldPrefix + "web-1:2:new", "" },
        });

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("<h2 id=\"servers-stale-pauses\" class=\"ns-note-title\">" + H(Strings.ServersPage_StalePauses_Title) + "</h2>", html);
        Assert.Contains("<code>web-1:1:old</code>", html);
        Assert.Contains("<em>before deploy</em>", html);
        Assert.Contains("data-ajax=\"/servers/resume/web-1%3A1%3Aold\"", html);
        Assert.Contains(H(Strings.ServersPage_StalePauses_Clear), html);
        Assert.DoesNotContain("<code>web-1:2:new</code>", html);
        Assert.Contains("data-ajax=\"/servers/resume/web-1%3A2%3Anew\"", html);
    }

    [Fact]
    public void Render_ListsStalePauses_EvenWithoutAnyServer()
    {
        SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "gone", "" } });

        string html = _renderer.Render(new ServersPage());

        Assert.Contains(H(Strings.ServersPage_NoServers_Title), html);
        Assert.Contains("<code>gone</code>", html);
        Assert.Contains("data-ajax=\"/servers/resume/gone\"", html);
    }

    [Fact]
    public void Render_DoesNotListStalePauses_WhenEveryPausedServerIsRunning()
    {
        SetupServers(Server("paused server", DateTime.UtcNow));
        SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "paused server", "" } });

        string html = _renderer.Render(new ServersPage());

        Assert.DoesNotContain(H(Strings.ServersPage_StalePauses_Title), html);
    }

    [Fact]
    public void Render_ListsStalePauses_WithoutTheClearButton_WhenTheDashboardIsReadOnly()
    {
        _renderer.Options = new Masterly.NightShift.DashboardOptions { IsReadOnlyFunc = _ => true };
        SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "gone", "" } });

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("<code>gone</code>", html);
        Assert.DoesNotContain("/servers/resume/", html);
    }

    [Fact]
    public void Render_HidesEveryCommand_WhenTheDashboardIsReadOnly()
    {
        _renderer.Options = new Masterly.NightShift.DashboardOptions { IsReadOnlyFunc = _ => true };
        _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
        SetupServers(Server("fresh-server", DateTime.UtcNow));

        string html = _renderer.Render(new ServersPage());

        Assert.DoesNotContain("/servers/pause/", html);
        Assert.DoesNotContain("<span class=\"visually-hidden\">" + H(Strings.Common_Actions) + "</span>", html);
        Assert.DoesNotContain("js-drain-form", html);
        Assert.DoesNotContain("/maintenance/resume", html);
        Assert.DoesNotContain("id=\"servers-maintenance\"", html);
        Assert.DoesNotContain("ns-danger-zone", html);
        Assert.DoesNotContain("ns-server-actions", html);
        Assert.Contains(H(Strings.ServersPage_Processing + ": " + Strings.ServersPage_Processing_Running), html);
    }

    [Fact]
    public void Render_ShowsProcessingAsRunning_WithAPauseForm_WhenNotDrained()
    {
        string html = _renderer.Render(new ServersPage());

        Assert.Contains(H(Strings.ServersPage_Processing_Running), html);
        Assert.Contains("data-url=\"/maintenance/pause\"", html);
        Assert.DoesNotContain("hf-drain-active", html);
    }

    [Fact]
    public void Render_ShowsProcessingAsPaused_WithSinceReasonAndResume_WhenDrained()
    {
        SetupControlHash(new Dictionary<string, string>
        {
            { "Drain", "1" },
            { "DrainSince", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-5)) },
            { "DrainReason", "deploying v2" },
        });

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("hf-drain-active", html);
        Assert.Contains(H(Strings.ServersPage_Processing_Paused), html);
        Assert.Contains("<em>deploying v2</em>", html);
        Assert.Contains("data-ajax=\"/maintenance/resume\"", html);
        Assert.DoesNotContain("js-drain-form", html);
        Assert.Contains(Strings.ServersPage_Paused_Since.Split(["{0}"], StringSplitOptions.None)[0] + "<span data-moment=", html);
    }

    [Fact]
    public void Render_ShowsProcessingAsPaused_WithoutSinceOrReason_WhenTheyAreNotStored()
    {
        SetupControlHash(new Dictionary<string, string> { { "Drain", "1" } });

        string html = _renderer.Render(new ServersPage());

        Assert.Contains(H(Strings.ServersPage_Processing_Paused), html);
        Assert.DoesNotContain("<em>", html);
        Assert.DoesNotContain(Strings.ServersPage_Paused_Since.Split(["{0}"], StringSplitOptions.None)[0] + "<span data-moment=", html);
        Assert.Contains("data-ajax=\"/maintenance/resume\"", html);
    }

    [Fact]
    public void Render_ShowsTheMaintenanceSection_WhenTheStorageSupportsPurge()
    {
        _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("id=\"servers-maintenance\"", html);
        Assert.Contains("data-ajax=\"/maintenance/clear-jobs\"", html);
        Assert.Contains("data-ajax=\"/maintenance/reset\"", html);
    }

    [Fact]
    public void Render_HidesTheMaintenanceSection_WhenTheStorageDoesNotSupportPurge()
    {
        string html = _renderer.Render(new ServersPage());

        Assert.DoesNotContain("id=\"servers-maintenance\"", html);
    }

    [Fact, StaticLock]
    public void Render_ShowsTheKeepAliveAsPending_BeforeTheFirstRequest()
    {
        using (new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/pending" }))
        {
            string html = _renderer.Render(new ServersPage());

            Assert.Contains("text-bg-info\">" + H(Strings.ServersPage_KeepAlive_Pending), html);
            Assert.DoesNotContain(H(Strings.ServersPage_KeepAlive_NotConfigured), html);
        }
    }

    [Fact, StaticLock]
    public void Render_ShowsTheKeepAliveAsOk_WithItsUrl_AfterASuccessfulRequest()
    {
        using KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/ok" }, new HttpClient(new StubHandler(null)));
        process.Ping(CancellationToken.None);

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("text-bg-success\">" + H(Strings.ServersPage_KeepAlive_Ok), html);
        Assert.Contains("<code>http://keep.alive/ok</code>", html);
    }

    [Fact, StaticLock]
    public void Render_ShowsTheKeepAliveAsFailed_WithTheError_AfterAFailedRequest()
    {
        using KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/failed", LogFailures = false }, new HttpClient(new StubHandler(new HttpRequestException("no route to host"))));
        process.Ping(CancellationToken.None);

        string html = _renderer.Render(new ServersPage());

        Assert.Contains("text-bg-danger\" title=\"no route to host\">" + H(Strings.ServersPage_KeepAlive_Failed), html);
        Assert.Contains("&middot; no route to host", html);
    }

    private void SetupServers(params ServerDto[] servers)
    {
        _renderer.Monitoring.Setup(x => x.Servers()).Returns([.. servers]);
    }

    private void SetupControlHash(Dictionary<string, string> hash)
    {
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(hash);
    }

    private static ServerDto Server(string name, DateTime? heartbeat, params string[] queues)
    {
        return new ServerDto
        {
            Name = name,
            WorkersCount = 5,
            StartedAt = DateTime.UtcNow.AddHours(-1),
            Heartbeat = heartbeat,
            Queues = queues.Length > 0 ? queues : ["default"],
        };
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static int CountOf(string html, string value) => html.Split([value], StringSplitOptions.None).Length - 1;

    private sealed class StubHandler(Exception exception) : HttpMessageHandler
    {
        private readonly Exception _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_exception != null)
            {
                throw _exception;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
