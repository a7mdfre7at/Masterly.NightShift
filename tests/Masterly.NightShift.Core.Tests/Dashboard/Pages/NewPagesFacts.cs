using Masterly.NightShift.Server;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

/// <summary>The recurring job details, chains, schedule policies and About pages.</summary>
public class NewPagesFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void RecurringJobDetails_SaysTheJobDoesNotExist()
    {
        string html = _renderer.Render(new RecurringJobDetailsPage("missing"));

        Assert.Contains("There is no recurring job &#39;missing&#39;.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringJobDetails_ShowsTheDefinition_TheBadges_TheNextRunsAndTheHistory()
    {
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:nightly")).Returns(new Dictionary<string, string>
        {
            ["Cron"] = "0 2 * * *",
            ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
            ["TimeZoneId"] = "UTC",
            ["Policy"] = "weekdays",
            ["Declared"] = "1",
            ["SkipAfter"] = "1800",
        });
        _renderer.Connection.Setup(x => x.GetRangeFromList(RecurringJobHistory.GetKey("nightly"), 0, 49)).Returns(["41"]);
        _renderer.Monitoring.Setup(x => x.JobDetails("41")).Returns(new Masterly.NightShift.Storage.Monitoring.JobDetailsDto
        {
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            Properties = new Dictionary<string, string>(),
            History = [new Masterly.NightShift.Storage.Monitoring.StateHistoryDto { StateName = "Succeeded", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string> { ["PerformanceDuration"] = "1500" } }],
        });

        string html = _renderer.Render(new RecurringJobDetailsPage("nightly"));

        Assert.Contains("0 2 * * *", html, StringComparison.Ordinal);
        Assert.Contains("From code", html, StringComparison.Ordinal);
        Assert.Contains("/policies#policy-weekdays", html, StringComparison.Ordinal);
        Assert.Contains("00:30:00", html, StringComparison.Ordinal);
        Assert.Contains("/recurring/trigger-one/nightly", html, StringComparison.Ordinal);
        Assert.Contains("/recurring/pause-one/nightly", html, StringComparison.Ordinal);
        Assert.Contains("/jobs/details/41", html, StringComparison.Ordinal);
        Assert.Contains("1.5 s", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringJobDetails_HidesTheActions_ForReadOnlyUsers()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = static _ => true };
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:nightly")).Returns(new Dictionary<string, string>
        {
            ["Cron"] = "0 2 * * *",
            ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
        });

        string html = _renderer.Render(new RecurringJobDetailsPage("nightly"));

        Assert.DoesNotContain("/recurring/trigger-one/", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Chains_SaysThereAreNone()
    {
        Assert.Contains("No chains.", _renderer.Render(new ChainsPage()), StringComparison.Ordinal);
    }

    [Fact]
    public void Chains_ListsTheChains_AndTheirDetailsShowEveryStep()
    {
        SetUpChain();

        string list = _renderer.Render(new ChainsPage());
        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.Contains("/chains/c1", list, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"hover hf-row-link\" data-href=\"/chains/c1\">", list, StringComparison.Ordinal);
        Assert.Contains("Finished with failures", list, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs/details/job-1\"", details, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs/details/job-2\"", details, StringComparison.Ordinal);
        Assert.Contains("/chains/retry/job-2", details, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/retry/job-1", details, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/delete/", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainDetails_DrawsTheFlowchart_WithItsToolbar_TheChainsFacts_AndItsLogs()
    {
        SetUpChain();
        _renderer.Connection.Setup(x => x.GetAllItemsFromList(JobConsole.GetConsoleKey("job-1"))).Returns(["{\"t\":1,\"s\":1,\"l\":1,\"m\":\"Charged the card\"}"]);

        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.Contains("class=\"hf-card hf-flow js-flow\"", details, StringComparison.Ordinal);
        Assert.Contains("js-flow-stage", details, StringComparison.Ordinal);
        Assert.Contains("data-zoom=\"in\"", details, StringComparison.Ordinal);
        Assert.Contains("data-zoom=\"fit\"", details, StringComparison.Ordinal);
        Assert.Contains("js-flow-fullscreen", details, StringComparison.Ordinal);
        Assert.Contains("Deletion: Cascade", details, StringComparison.Ordinal);
        Assert.Contains("Jobs: 2 &#183; Groups: 0", details, StringComparison.Ordinal);
        Assert.Contains("Chain logs", details, StringComparison.Ordinal);
        Assert.Contains("Charged the card", details, StringComparison.Ordinal);
        Assert.Contains("<details class=\"hf-card hf-chain-logs\" open", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainDetails_KeepsTheLogsClosed_WhenNoStepWrote()
    {
        SetUpChain();

        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.Contains("<details class=\"hf-card hf-chain-logs\">", details, StringComparison.Ordinal);
        Assert.Contains("No step has written to its console.", details, StringComparison.Ordinal);
    }

    [Fact]
    public void Chains_SaysAChainIsWaiting_UntilAStepStarts()
    {
        SetUpChain();
        _renderer.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = "Scheduled" });
        _renderer.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = "Awaiting" });

        string list = _renderer.Render(new ChainsPage());

        Assert.Contains("label-default text-uppercase\">Waiting<", list, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainDetails_HidesTheDeleteAndRetryButtons_ForReadOnlyUsers()
    {
        SetUpChain();
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = static _ => true };

        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.DoesNotContain("/chains/retry/", details, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/delete/", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainDetails_SaysTheChainIsNotRecorded()
    {
        Assert.Contains("isn&#39;t recorded any more", _renderer.Render(new ChainDetailsPage("gone")), StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_SaysThereAreNone_AndOffersTheEditor()
    {
        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("No schedule policies.", html, StringComparison.Ordinal);
        Assert.Contains("/policies/save", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_ShowsEachPolicy_TheEditorPrefilled_AndAPreview()
    {
        SetUpPolicy();
        _renderer.Request.Query["edit"] = "weekdays";
        _renderer.Request.Query["preview"] = "weekdays";
        _renderer.Request.Query["cron"] = "0 12 * * *";

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("id=\"policy-weekdays\"", html, StringComparison.Ordinal);
        Assert.Contains("revision 3", html, StringComparison.Ordinal);
        Assert.Contains("09:00-17:00", html, StringComparison.Ordinal);
        Assert.Contains("/policies/delete/weekdays/3", html, StringComparison.Ordinal);
        Assert.Contains("name=\"revision\" value=\"3\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Monday\" checked=\"checked\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Sunday\"/>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"weekdays\" selected=\"selected\">", html, StringComparison.Ordinal);
        Assert.True(html.Contains("Allowed", StringComparison.Ordinal) || html.Contains("Blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void Policies_ShowsAPreviewError_ForABadCron()
    {
        SetUpPolicy();
        _renderer.Request.Query["preview"] = "weekdays";
        _renderer.Request.Query["cron"] = "not a schedule at all";

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("alert-warning", html, StringComparison.Ordinal);
    }

    [Fact]
    public void About_ShowsTheSettings_TheFilters_TheFunctionsAndTheMonitors()
    {
        string html = _renderer.Render(new AboutPage());

        Assert.Contains("UseStaleRestartLimit", html, StringComparison.Ordinal);
        Assert.Contains("CaptureCultureAttribute", html, StringComparison.Ordinal);
        Assert.Contains("Job functions", html, StringComparison.Ordinal);
        Assert.Contains("Monitors", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationMenu_LinksToTheNewPages()
    {
        string html = _renderer.Render(new AboutPage());

        Assert.Contains("/chains\"", html, StringComparison.Ordinal);
        Assert.Contains("/policies\"", html, StringComparison.Ordinal);
        Assert.Contains("/about\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/recurring/details/nightly")]
    [InlineData("/chains")]
    [InlineData("/chains/c1")]
    [InlineData("/policies")]
    [InlineData("/about")]
    public void Routes_ServeTheNewPages(string path)
    {
        Assert.NotNull(DashboardRoutes.Routes.FindDispatcher(path));
    }

    private void SetUpChain()
    {
        JobChain chain = JobChain.StartWith(() => Run()).Then(() => Run());
        _renderer.Connection.Setup(x => x.GetSetCount(JobChains.SetKey)).Returns(1);
        _renderer.Connection.Setup(x => x.GetRangeFromSet(JobChains.SetKey, 0, 0)).Returns(["c1"]);
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("chain:c1")).Returns(new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(chain),
            ["Jobs"] = "{\"0.0\":\"job-1\",\"1.0\":\"job-2\"}",
            ["Generation"] = "1",
            ["CreatedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-5)),
        });
        _renderer.Connection.Setup(x => x.GetStateData("job-1")).Returns(new StateData { Name = "Succeeded" });
        _renderer.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = "Failed" });
    }

    private void SetUpPolicy()
    {
        Dictionary<string, string> fields = new SchedulePolicy("weekdays")
            .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Friday)
            .AllowBetween("09:00", "17:00")
            .ToFields();
        fields["Revision"] = "3";
        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(SchedulePolicies.SetKey)).Returns(["weekdays"]);
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("schedule-policy:weekdays")).Returns(fields);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }
}
