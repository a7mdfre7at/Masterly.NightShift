using System.Globalization;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Server;

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
        _renderer.Connection.Setup(x => x.GetRangeFromList(RecurringJobHistory.GetKey("nightly"), 0, 19)).Returns(["41"]);
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
    public void RecurringJobDetails_ShowsTheNameOfTheSetThatDeclaredTheJob_Encoded()
    {
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:nightly")).Returns(new Dictionary<string, string>
        {
            ["Cron"] = "0 2 * * *",
            ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
            ["Declared"] = "<billing>",
        });

        string html = _renderer.Render(new RecurringJobDetailsPage("nightly"));

        Assert.Contains("From code", html, StringComparison.Ordinal);
        Assert.Contains("&lt;billing&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<billing>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringJobDetails_OfAnIdWithALiteralPercent2F_ShowsThatJob_AndActsOnIt()
    {
        // The route hands the page the path as ASP.NET Core decoded it: "a~252Fb" for the id "a%2Fb"
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:a%2Fb")).Returns(new Dictionary<string, string>
        {
            ["Cron"] = "0 2 * * *",
            ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
        });

        string html = _renderer.Render(new RecurringJobDetailsPage("a~252Fb"));

        Assert.Contains("data-ajax=\"/recurring/trigger-one/a~252Fb\"", html, StringComparison.Ordinal);
        Assert.Contains("data-ajax=\"/recurring/pause-one/a~252Fb\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringJobDetails_OfAPausedJob_SaysSo_DescribesMissedOccurrencesInWords_AndLinksTheEditor()
    {
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:nightly")).Returns(new Dictionary<string, string>
        {
            ["Cron"] = "0 2 * * *",
            ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
            ["Paused"] = "1",
            ["Misfire"] = "1",
        });

        string html = _renderer.Render(new RecurringJobDetailsPage("nightly"));

        Assert.Contains("ns-recurring-paused", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ns-recurring-active", html, StringComparison.Ordinal);
        Assert.Contains("/recurring/resume-one/nightly", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/recurring?edit=nightly\"", html, StringComparison.Ordinal);
        Assert.Contains(Masterly.NightShift.Dashboard.Resources.Strings.RecurringJobsPage_Misfire_Strict, html, StringComparison.Ordinal);
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
        Assert.DoesNotContain("/recurring?edit=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Chains_SaysThereAreNone()
    {
        string html = _renderer.Render(new ChainsPage());

        Assert.Contains("<h2 class=\"ns-empty-state-title\">No chains yet</h2>", html, StringComparison.Ordinal);
        Assert.Contains("JobChain.StartWith(...)", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ns-count-chip js-list-count\">0</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Chains_ListsTheChains_AndTheirDetailsShowEveryStep()
    {
        SetUpChain();

        string list = _renderer.Render(new ChainsPage());
        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.Contains("/chains/c1", list, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"hover hf-row-link\" data-href=\"/chains/c1\">", list, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ns-state-chip ns-state-failed\" data-state=\"Failed\">" + DashboardMarkup.Icon("x-circle-fill") + "<span>Finished with failures</span></span>", list, StringComparison.Ordinal);
        Assert.Contains("<a class=\"job-method\" href=\"/chains/c1\">", list, StringComparison.Ordinal);
        Assert.Contains("aria-valuenow=\"100\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/delete/", list, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs/details/job-1\"", details, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs/details/job-2\"", details, StringComparison.Ordinal);
        Assert.Contains("/chains/retry/c1/job-2", details, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/retry/c1/job-1", details, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/delete/", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainDetails_DrawsTheFlowchart_WithItsToolbar_TheChainsFacts_AndItsLogs()
    {
        SetUpChain();
        _renderer.Connection.Setup(x => x.GetListCount(JobConsole.GetConsoleKey("job-1"))).Returns(1);
        _renderer.Connection.Setup(x => x.GetRangeFromList(JobConsole.GetConsoleKey("job-1"), 0, ChainFlowchart.MaxLogLines - 1)).Returns(["{\"t\":1,\"s\":1,\"l\":1,\"m\":\"Charged the card\"}"]);

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

        Assert.Contains("<span class=\"ns-state-chip ns-state-awaiting\" data-state=\"Awaiting\">" + DashboardMarkup.Icon("hourglass-split") + "<span>Waiting</span></span>", list, StringComparison.Ordinal);
        Assert.Contains("data-ajax=\"/chains/delete/c1\"", list, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Delete the unfinished steps of chain c1\"", list, StringComparison.Ordinal);
        Assert.Contains("data-confirm-destructive=\"true\"", list, StringComparison.Ordinal);
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
    public void Policies_SaysWhatAPolicyIsFor_WhenThereAreNone_AndOffersTheEditor()
    {
        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("<h2 class=\"ns-empty-state-title\">No schedule policies yet</h2>", html, StringComparison.Ordinal);
        Assert.Contains("A schedule policy is a business calendar", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ns-count-chip js-list-count\">0</span>", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "data-sheet-content=\"#tpl-policy-new\""));
        Assert.Contains("<template id=\"tpl-policy-new\">", html, StringComparison.Ordinal);
        Assert.Contains("/policies/save", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_SaysWherePoliciesComeFrom_WhenThereAreNone_ForReadOnlyUsers()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = static _ => true };

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("No schedule policies yet", html, StringComparison.Ordinal);
        Assert.Contains("SaveSchedulePolicy or AddPolicy", html, StringComparison.Ordinal);
        Assert.DoesNotContain("#tpl-policy-new", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/policies/save", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_HasAHeader_WithTheCount_AndNewPolicyAsItsAction()
    {
        SetUpPolicy();

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("<header class=\"ns-page-head\">", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ns-count-chip js-list-count\">1</span>", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"btn btn-sm btn-primary js-sheet-open\" aria-haspopup=\"dialog\" data-sheet-content=\"#tpl-policy-new\"", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"ns-policy-grid\">", html, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"ns-policy-title-0\" class=\"ns-policy-name\">weekdays</h2>", html, StringComparison.Ordinal);
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
        Assert.Contains("<template id=\"tpl-policy-edit-0\">", html, StringComparison.Ordinal);
        Assert.Contains("name=\"revision\" value=\"3\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Monday\" checked=\"checked\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Sunday\"/>", html, StringComparison.Ordinal);
        Assert.Contains("<input type=\"hidden\" name=\"preview\" value=\"weekdays\"/>", html, StringComparison.Ordinal);
        Assert.Contains("value=\"0 12 * * *\"", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Next occurrences</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<ol class=\"ns-next-runs ns-policy-candidates\">", html, StringComparison.Ordinal);
        Assert.True(
            html.Contains("<span class=\"ns-state-chip ns-state-succeeded\">", StringComparison.Ordinal) ||
            html.Contains("<span class=\"ns-state-chip ns-state-deleted\">", StringComparison.Ordinal));

        // The preview of the address opens; the editor opens only without one
        Assert.Equal(1, Count(html, "js-policy-autoopen"));
        Assert.Contains("js-sheet-open js-policy-autoopen\" aria-haspopup=\"dialog\" aria-describedby=\"ns-policy-title-0\" data-sheet-content=\"#tpl-policy-preview-0\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_OpensTheEditorOfThePolicyTheAddressNames()
    {
        SetUpPolicy();
        _renderer.Request.Query["edit"] = "weekdays";

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("js-sheet-open js-policy-autoopen\" aria-haspopup=\"dialog\" aria-describedby=\"ns-policy-title-0\" data-sheet-content=\"#tpl-policy-edit-0\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_ShowsTheDays_TheHours_TheExcludedDates_AndTheLinksBetweenPolicies_OnTheCards()
    {
        SetUpPolicies(
            (new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Friday).AllowBetween("09:00", "17:00"), 3),
            (new SchedulePolicy("nights").AllowBetween("22:00", "06:00")
                .ExcludeAnnualDate(12, 25)
                .ExcludeDate(new DateOnly(2026, 1, 1)).ExcludeDate(new DateOnly(2026, 1, 2)).ExcludeDate(new DateOnly(2026, 1, 3))
                .ExcludeDate(new DateOnly(2026, 1, 4)).ExcludeDate(new DateOnly(2026, 1, 5)).ExcludeDate(new DateOnly(2026, 1, 6))
                .ExcludeDate(new DateOnly(2026, 1, 7))
                .ExcludeDatesFrom("public-holidays"), 1),
            (new SchedulePolicy("trading").Require("weekdays").ExceptWhen("nights"), 2));

        string html = _renderer.Render(new SchedulePoliciesPage());

        // Every day of the week, allowed or blocked, in words for screen readers
        Assert.Contains("<span class=\"visually-hidden\">Monday: Allowed</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"visually-hidden\">Tuesday: Blocked</span>", html, StringComparison.Ordinal);
        Assert.Contains("<li class=\"ns-chip ns-policy-day ns-policy-day-allowed\">", html, StringComparison.Ordinal);
        Assert.Contains("<li class=\"ns-chip ns-policy-day\">", html, StringComparison.Ordinal);
        Assert.Equal(7, Count(html, "<li class=\"ns-chip ns-policy-day"));
        // A policy without a weekday rule allows every day, and says so in words, as its editor does
        Assert.Equal(2, Count(html, "<span class=\"ns-policy-any\">Every day</span>"));

        Assert.Contains("22:00-06:00", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "<span class=\"ns-policy-overnight\">"));
        Assert.Contains("crosses midnight</span>", html, StringComparison.Ordinal);
        Assert.Contains("<li class=\"ns-chip ns-chip-status ns-tabular\">12-25</li>", html, StringComparison.Ordinal);
        Assert.Contains("<summary class=\"ns-chip ns-chip-assist\">+2 more</summary>", html, StringComparison.Ordinal);
        Assert.Contains("public-holidays", html, StringComparison.Ordinal);

        Assert.Contains("id=\"policy-trading\"", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"#policy-weekdays\">", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"#policy-nights\">", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "<a class=\"ns-chip ns-chip-assist\" href=\"#policy-trading\">"));
        Assert.Equal(2, Count(html, "<dt>Used by</dt>"));
    }

    [Fact]
    public void Policies_RefusesToDeleteAPolicyOtherPoliciesUse_AndAsksBeforeDeletingTheOthers()
    {
        SetUpPolicies(
            (new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday), 3),
            (new SchedulePolicy("trading").Require("weekdays"), 2));

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.DoesNotContain("/policies/delete/weekdays/", html, StringComparison.Ordinal);
        Assert.Contains("disabled=\"disabled\" aria-describedby=\"ns-policy-title-1 ns-policy-delete-note-1\"", html, StringComparison.Ordinal);
        Assert.Contains("<p id=\"ns-policy-delete-note-1\" class=\"ns-policy-delete-note\">Other policies refer to it", html, StringComparison.Ordinal);
        Assert.Contains("data-ajax=\"/policies/delete/trading/2\"", html, StringComparison.Ordinal);
        Assert.Contains("data-confirm=\"Delete this policy?\" data-confirm-body=\"Jobs already scheduled with it run without it.", html, StringComparison.Ordinal);
        Assert.Contains("data-confirm-destructive=\"true\"", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "class=\"btn btn-sm ns-btn-text ns-btn-text-error\""));
    }

    [Fact]
    public void Policies_OffersOnlyThePreview_ToReadOnlyUsers()
    {
        SetUpPolicy();
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = static _ => true };
        _renderer.Request.Query["edit"] = "weekdays";

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("id=\"policy-weekdays\"", html, StringComparison.Ordinal);
        Assert.Contains("data-sheet-content=\"#tpl-policy-preview-0\"", html, StringComparison.Ordinal);
        Assert.Contains("<template id=\"tpl-policy-preview-0\" data-policy=\"weekdays\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("#tpl-policy-new", html, StringComparison.Ordinal);
        Assert.DoesNotContain("tpl-policy-edit-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/policies/save", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/policies/delete/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("js-policy-autoopen", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_GroupsTheEditorsFields_AndNamesEachOne()
    {
        SetUpPolicy();

        string html = _renderer.Render(new SchedulePoliciesPage());

        Assert.Contains("<legend>When jobs may start</legend>", html, StringComparison.Ordinal);
        Assert.Contains("<legend>Other policies</legend>", html, StringComparison.Ordinal);
        Assert.Contains("<label for=\"ns-policy-new-name\">Name</label>", html, StringComparison.Ordinal);
        Assert.Contains("<input id=\"ns-policy-new-name\" type=\"text\" name=\"name\" class=\"form-control\" required=\"required\"", html, StringComparison.Ordinal);
        Assert.Contains("<input id=\"ns-policy-0-name\" type=\"text\" name=\"name\" class=\"form-control\" value=\"weekdays\" readonly=\"readonly\"/>", html, StringComparison.Ordinal);
        Assert.Contains("role=\"group\" aria-labelledby=\"ns-policy-0-days-label\" aria-describedby=\"ns-policy-0-days-hint ns-policy-0-days-error\"", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\" form=\"ns-policy-0-form\" class=\"btn btn-sm btn-primary\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"alert alert-danger ns-policy-conflict js-policy-conflict\" role=\"alert\" hidden=\"hidden\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/policies?edit=weekdays\"", html, StringComparison.Ordinal);
        foreach (string name in new[] { "description", "timezone", "windows", "dates", "providers", "require", "except" })
        {
            Assert.Contains($"<label for=\"ns-policy-0-{name}\">", html, StringComparison.Ordinal);
            Assert.Contains($"name=\"{name}\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Policies_KeepTheHeadingsInOrder_AndNameEveryHeaderCell()
    {
        SetUpPolicies(
            (new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday), 3),
            (new SchedulePolicy("trading").Require("weekdays"), 2));
        _renderer.Request.Query["preview"] = "weekdays";
        _renderer.Request.Query["cron"] = "0 12 * * *";
        string withPolicies = _renderer.Render(new SchedulePoliciesPage());

        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(SchedulePolicies.SetKey)).Returns([]);
        string empty = _renderer.Render(new SchedulePoliciesPage());

        foreach (string html in new[] { withPolicies, empty })
        {
            PageStructure.AssertHeadingsInOrder(html);
            PageStructure.AssertNoEmptyHeaderCells(html);
        }
    }

    [Fact]
    public void Policies_GiveTheirTemplatesAndFieldsIds_ThatNoPolicyNameCanTake()
    {
        SetUpPolicies(
            (new SchedulePolicy("new"), 1),
            (new SchedulePolicy("edit-0"), 1),
            (new SchedulePolicy("preview-1"), 1),
            (new SchedulePolicy("title-2"), 1),
            (new SchedulePolicy("0-form"), 1));

        string html = _renderer.Render(new SchedulePoliciesPage());

        List<string> ids = [.. Regex.Matches(html, "\\sid=\"([^\"]+)\"").Select(static x => x.Groups[1].Value)];
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("policy-new", ids);
        Assert.Contains("policy-edit-0", ids);
        Assert.Contains("tpl-policy-new", ids);
        Assert.Contains("tpl-policy-edit-0", ids);
        Assert.Contains("tpl-policy-preview-1", ids);
        Assert.Contains("ns-policy-0-form", ids);
        // Only the cards' ids start with "policy-"
        Assert.Equal(
            ["policy-0-form", "policy-edit-0", "policy-new", "policy-preview-1", "policy-title-2"],
            ids.Where(static x => x.StartsWith("policy-", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Policies_AskBeforeTheirEditorsDiscardChanges_ButNotTheirPreviews()
    {
        SetUpPolicies((new SchedulePolicy("weekdays"), 1), (new SchedulePolicy("nights"), 1));

        string html = _renderer.Render(new SchedulePoliciesPage());

        // New policy and the editor of each policy
        Assert.Equal(3, Count(html, "data-sheet-guard=\"true\""));
        Assert.Contains("data-sheet-content=\"#tpl-policy-new\" data-sheet-title=\"New policy\" data-sheet-wide=\"true\" data-sheet-guard=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("data-sheet-content=\"#tpl-policy-edit-0\" data-sheet-title=\"nights\" data-sheet-overline=\"Schedule policy\" data-sheet-wide=\"true\" data-sheet-guard=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("data-sheet-content=\"#tpl-policy-preview-0\" data-sheet-title=\"nights\" data-sheet-overline=\"Preview\">", html, StringComparison.Ordinal);
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
    public void About_HasTheSharedHeader_AJumpNav_AndASectionCardPerPart()
    {
        _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);

        string html = _renderer.Render(new AboutPage());

        Assert.Contains("<header class=\"ns-page-head\">", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ns-page-name\">About</span></h1>", html, StringComparison.Ordinal);
        Assert.Contains("<nav class=\"ns-jump-nav\" aria-label=\"On this page\">", html, StringComparison.Ordinal);
        foreach (string section in (string[])["settings", "storage", "filters", "functions", "monitors"])
        {
            Assert.Contains($"href=\"#{section}\"", html, StringComparison.Ordinal);
            Assert.Contains($"aria-labelledby=\"{section}\">", html, StringComparison.Ordinal);
            Assert.Contains($"<h2 id=\"{section}\">", html, StringComparison.Ordinal);
        }

        Assert.Contains("<dt><code>UseStaleRestartLimit</code></dt>", html, StringComparison.Ordinal);
        Assert.Contains("<li class=\"ns-about-supported\">", html, StringComparison.Ordinal);
        Assert.Contains("<code>Connection.GetUtcDateTime</code>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"visually-hidden\">not supported</span>", html, StringComparison.Ordinal);
        Assert.Contains("<code>CaptureCultureAttribute</code>", html, StringComparison.Ordinal);
        Assert.Matches("<span class=\"ns-chip ns-chip-status ns-tabular\">Order -?[0-9]+</span>", html);
        PageStructure.AssertHeadingsInOrder(html);
        PageStructure.AssertNoEmptyHeaderCells(html);
    }

    [Fact]
    public void About_DescribesTheStorage_OnlyWhenTheOptionsShowItsConnectionString()
    {
        _renderer.Storage.Setup(x => x.ToString()).Returns("Server=db;Password=<secret>");
        _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);

        _renderer.Options = new DashboardOptions { DisplayStorageConnectionString = true };
        string shown = _renderer.Render(new AboutPage());
        _renderer.Options = new DashboardOptions { DisplayStorageConnectionString = false };
        string hidden = _renderer.Render(new AboutPage());

        Assert.Contains("<p class=\"ns-about-storage\">Server=db;Password=&lt;secret&gt;</p>", shown, StringComparison.Ordinal);
        Assert.Contains("<dt><code>JobStorage</code></dt>", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("ns-about-storage", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt><code>JobStorage</code></dt>", hidden, StringComparison.Ordinal);
        Assert.Contains("<dt><code>UseStaleRestartLimit</code></dt>", hidden, StringComparison.Ordinal);
        // The storage's features stay
        Assert.Contains("<code>Connection.GetUtcDateTime</code>", hidden, StringComparison.Ordinal);
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

    [Fact]
    public void Chains_ListsAChain_WithoutLoadingTheMethodsOfItsLaterSteps()
    {
        SetUpChain();
        Dictionary<string, string> record = _renderer.Connection.Object.GetAllEntriesFromHash("chain:c1")!;
        string definition = JobChains.ToDefinition(JobChain.StartWith(() => Run()).Then(() => Later()));
        record["Definition"] = definition.Replace(nameof(Later), "Removed", StringComparison.Ordinal);
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("chain:c1")).Returns(record);

        string list = _renderer.Render(new ChainsPage());

        Assert.Contains("/chains/c1", list, StringComparison.Ordinal);
        Assert.Contains(nameof(Run), list, StringComparison.Ordinal);
    }

    [Fact]
    public void Chains_ListsTheChains_NewestFirst_OnAStorageWithoutRanges()
    {
        _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(JobChains.SetKey)).Returns(["old", "new"]);
        SetUpRecord("old", DateTime.UtcNow.AddDays(-2));
        SetUpRecord("new", DateTime.UtcNow.AddMinutes(-1));

        string list = _renderer.Render(new ChainsPage());

        Assert.True(list.IndexOf("/chains/new", StringComparison.Ordinal) < list.IndexOf("/chains/old", StringComparison.Ordinal));
    }

    [Fact]
    public void ChainDetails_HasAHeader_SummaryTiles_AndEveryStepInAList()
    {
        SetUpChain();

        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.Contains("<header class=\"ns-page-head\">", details, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ns-job-state\"><span class=\"ns-state-chip ns-state-failed\" data-state=\"Failed\">", details, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"summary-steps\" class=\"ns-summary-label\">", details, StringComparison.Ordinal);
        Assert.Contains("<section class=\"ns-summary-tile ns-state-failed\" aria-labelledby=\"summary-failed\">", details, StringComparison.Ordinal);
        Assert.Contains("aria-valuenow=\"100\"", details, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"chain-steps\">", details, StringComparison.Ordinal);
        Assert.Contains("<code class=\"ns-step-key\">0.0</code>", details, StringComparison.Ordinal);
        Assert.Contains("<code class=\"ns-step-key\">1.0</code>", details, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ns-dot ns-state-failed\" aria-hidden=\"true\"></span>", details, StringComparison.Ordinal);
        Assert.Contains("data-copy=\"job-2\"", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainDetails_AsksBeforeRetryingFromAStep_InTheFlowchartAndInTheList()
    {
        SetUpChain();

        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.Equal(2, Count(details, "data-ajax=\"/chains/retry/c1/job-2\""));
        Assert.Equal(2, Count(details, "data-confirm=\"Retry the chain from this step?\""));
        Assert.Contains("data-confirm-body=\"This step runs again with every step after it. The steps before it keep their outcome.\"", details, StringComparison.Ordinal);
        Assert.Contains("data-confirm-label=\"Retry from here\"", details, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"ns-icon-button\" data-ajax=\"/chains/retry/c1/job-2\"", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainDetails_SetsDeleteApart_InTheErrorColour_WhileTheChainRuns()
    {
        SetUpChain();
        _renderer.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = "Processing" });

        string details = _renderer.Render(new ChainDetailsPage("c1"));

        Assert.Contains("<button class=\"btn btn-sm ns-btn-outlined-error\" data-ajax=\"/chains/delete/c1\"", details, StringComparison.Ordinal);
        Assert.Contains("data-confirm-destructive=\"true\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/retry/", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainPages_KeepTheHeadingsInOrder_AndNameEveryHeaderCell()
    {
        SetUpChain();

        foreach (string html in new[] { _renderer.Render(new ChainsPage()), _renderer.Render(new ChainDetailsPage("c1")), _renderer.Render(new ChainDetailsPage("gone")) })
        {
            PageStructure.AssertHeadingsInOrder(html);
            PageStructure.AssertNoEmptyHeaderCells(html);
        }
    }

    [Fact]
    public void Chains_ShowsNoRowActions_ForReadOnlyUsers()
    {
        SetUpChain();
        _renderer.Connection.Setup(x => x.GetStateData("job-2")).Returns(new StateData { Name = "Processing" });
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = static _ => true };

        string list = _renderer.Render(new ChainsPage());

        Assert.Contains("/chains/c1", list, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/delete/", list, StringComparison.Ordinal);
        Assert.DoesNotContain("ns-actions-cell", list, StringComparison.Ordinal);
    }

    private static int Count(string html, string fragment)
    {
        return (html.Length - html.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;
    }

    private void SetUpRecord(string chainId, DateTime createdAt)
    {
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"chain:{chainId}")).Returns(new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(JobChain.StartWith(() => Run())),
            ["Jobs"] = $"{{\"0.0\":\"{chainId}-1\"}}",
            ["CreatedAt"] = JobHelper.SerializeDateTime(createdAt),
        });
    }

    [Fact]
    public void ChainDetails_NamesTheStatesOfItsTiles_InTheReadersLanguage()
    {
        SetUpChain();
        CultureInfo culture = CultureInfo.CurrentUICulture;

        string details;
        try
        {
            CultureHelper.SetCurrentUICulture("de-DE");
            details = _renderer.Render(new ChainDetailsPage("c1"));
        }
        finally
        {
            CultureHelper.SetCurrentUICulture(culture);
        }

        Assert.Contains(" Erfolgreich</h2>", details, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"summary-failed\" class=\"ns-summary-label\">" + DashboardMarkup.Icon("x-circle-fill") + " Fehler</h2>", details, StringComparison.Ordinal);
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
        SetUpPolicies((new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Friday).AllowBetween("09:00", "17:00"), 3));
    }

    private void SetUpPolicies(params (SchedulePolicy Policy, long Revision)[] policies)
    {
        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(SchedulePolicies.SetKey)).Returns([.. policies.Select(static x => x.Policy.Name)]);
        foreach ((SchedulePolicy policy, long revision) in policies)
        {
            Dictionary<string, string> fields = policy.ToFields();
            fields["Revision"] = revision.ToString(CultureInfo.InvariantCulture);
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(SchedulePolicies.GetKey(policy.Name))).Returns(fields);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Later()
    {
    }
}
