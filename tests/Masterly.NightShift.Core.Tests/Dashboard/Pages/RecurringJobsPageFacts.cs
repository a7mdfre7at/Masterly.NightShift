using System.Net;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class RecurringJobsPageFacts
{
    private readonly PageRenderer _renderer = new();
    private readonly List<string> _ids = [];

    public RecurringJobsPageFacts()
    {
        _renderer.Connection.Setup(x => x.GetSetCount("recurring-jobs")).Returns(() => _ids.Count);
        _renderer.Connection.Setup(x => x.GetRangeFromSet("recurring-jobs", It.IsAny<int>(), It.IsAny<int>())).Returns(() => [.. _ids]);
    }

    [Fact]
    public void Render_SaysThereAreNoJobs_AndStillRendersTheEditors_WhenNothingIsRegistered()
    {
        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<div class=\"ns-empty-state\">", html);
        Assert.Contains("<h2 class=\"ns-empty-state-title\">" + H(Strings.RecurringJobsPage_EmptyTitle) + "</h2>", html);
        Assert.Contains("<p>" + H(Strings.RecurringJobsPage_Empty) + "</p>", html);
        Assert.Contains("href=\"/catalog\">" + H(Strings.NavigationMenu_Catalog) + "</a>", html);
        Assert.DoesNotContain("js-jobs-list", html);
        Assert.DoesNotContain("/recurring/remove-all", html);
        Assert.Contains("id=\"recurringJobEditor\"", html);
        Assert.Contains("data-preview-url=\"/recurring/preview\"", html);
        Assert.Contains("<option value=\"" + H(TimeZoneInfo.Utc.Id) + "\">", html);
        Assert.Contains("<option value=\"Ignorable\">", html);
        Assert.Contains("id=\"manifestImport\"", html);
        Assert.Contains("data-import-url=\"/recurring/import\"", html);
    }

    [Fact]
    public void Render_HidesTheEditors_WhenTheDashboardIsReadOnly()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.DoesNotContain("id=\"recurringJobEditor\"", html);
        Assert.DoesNotContain("id=\"manifestImport\"", html);
    }

    [Fact]
    public void Render_ShowsTheToolbar_WithEveryCommand_AndAPager()
    {
        AddJob("daily", "0 0 * * *", Next(TimeSpan.FromHours(1)));

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("href=\"/recurring/forecast\"", html);
        Assert.Contains("data-url=\"/recurring/export\"", html);
        Assert.Contains("js-manifest-import", html);
        Assert.Contains("data-ajax=\"/recurring/remove-all\"", html);
        Assert.Contains("data-url=\"/recurring/trigger\"", html);
        Assert.Contains("data-url=\"/recurring/pause\"", html);
        Assert.Contains("data-url=\"/recurring/resume\"", html);
        Assert.Contains("data-url=\"/recurring/remove\"", html);
        Assert.Contains("js-jobs-list-select-all", html);
        Assert.Contains("name=\"jobs[]\" value=\"daily\"", html);
        Assert.Contains("<nav class=\"ns-pagination\"", html);
        Assert.DoesNotContain(H(string.Format(Strings.RecurringJobsPage_LateSummary, 1)), html);
    }

    [Fact]
    public void Render_HidesTheCommands_AndCheckboxes_WhenTheDashboardIsReadOnly()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
        AddJob("daily", "0 0 * * *", Next(TimeSpan.FromHours(1)), new Dictionary<string, string> { { "Error", "Boom" } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("data-url=\"/recurring/export\"", html);
        Assert.DoesNotContain("js-manifest-import", html);
        Assert.DoesNotContain("/recurring/remove-all", html);
        Assert.DoesNotContain("/recurring/trigger", html);
        Assert.DoesNotContain("js-jobs-list-checkbox", html);
        Assert.DoesNotContain("js-recurring-edit", html);
        Assert.DoesNotContain("ns-row-actions", html);
        Assert.DoesNotContain("ns-actions-cell", html);
        Assert.Contains("<template class=\"js-sheet-template\">", html);
        Assert.Contains("<pre class=\"stack-trace\"><code>Boom</code></pre>", html);
    }

    [Fact]
    public void Render_SummarisesAndMarksLateJobs()
    {
        AddJob("late-one", "* * * * *", Next(TimeSpan.FromMinutes(-10)));
        AddJob("late-two", "* * * * *", Next(TimeSpan.FromMinutes(-5)));
        AddJob("on-time", "* * * * *", Next(TimeSpan.FromSeconds(30)));

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains(H(string.Format(Strings.RecurringJobsPage_LateSummary, 2)), html);
        Assert.Equal(2, CountOf(html, "title=\"" + H(Strings.RecurringJobsPage_Late_Tooltip) + "\""));
    }

    [Fact]
    public void Render_SaysTheLateSummaryCoversThisPage_WhenThereAreOtherPages()
    {
        AddJob("late-one", "* * * * *", Next(TimeSpan.FromMinutes(-10)));
        AddJob("on-time", "* * * * *", Next(TimeSpan.FromSeconds(30)));
        _renderer.Request.Query["count"] = "1";

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains(H(string.Format(Strings.RecurringJobsPage_LateSummary_Page, 1)), html);
        Assert.DoesNotContain(H(string.Format(Strings.RecurringJobsPage_LateSummary, 1)), html);
    }

    [Fact]
    public void Render_ShowsTheStateOfTheNextExecution_ForEachKindOfJob()
    {
        AddJob("paused", "* * * * *", Next(TimeSpan.FromMinutes(-10)), new Dictionary<string, string> { { "Paused", "1" } });
        AddJob("disabled", "* * * * *", null);
        AddJob("errored", "* * * * *", null, new Dictionary<string, string> { { "Error", "System.InvalidOperationException: Cron went away" + Environment.NewLine + "   at Masterly.NightShift.Server.RecurringJobScheduler.Execute()" } });
        AddJob("retrying", "* * * * *", Next(TimeSpan.FromMinutes(5)), new Dictionary<string, string> { { "RetryAttempt", "2" } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("title=\"" + H(Strings.RecurringJobsPage_Paused_Tooltip) + "\">" + H(Strings.RecurringJobsPage_Paused) + "</span>", html);
        Assert.Contains("title=\"" + H(Strings.RecurringJobsPage_RecurringJobDisabled_Tooltip) + "\">" + H(Strings.Common_Disabled) + "</span>", html);
        Assert.Contains("<span class=\"ns-chip ns-chip-status ns-recurring-error\">" + DashboardMarkup.Icon("exclamation-circle-fill") + " " + H(Strings.Common_Error) + "</span>", html);
        Assert.Contains("<span class=\"ns-chip ns-chip-status ns-recurring-retrying\">" + DashboardMarkup.Icon("arrow-repeat") + " <span data-moment=", html);
        Assert.DoesNotContain(H(string.Format(Strings.RecurringJobsPage_LateSummary, 1)), html);
        // The error is a line under the job; the whole of it opens in the side sheet
        Assert.Contains("<span class=\"ns-job-error-message\">System.InvalidOperationException: Cron went away</span>", html);
        Assert.Contains("data-sheet-title=\"errored\" data-sheet-overline=\"" + H(Strings.Common_Error) + "\">" + H(Strings.Common_MoreDetails) + "</button>", html);
        Assert.DoesNotContain("failed-job-details", html);
        Assert.DoesNotContain("rowspan", html);
    }

    [Fact]
    public void Render_ShowsTheEditButton_WithTheScheduleOfTheJob()
    {
        AddJob("nightly", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string>
        {
            { "Queue", "critical" },
            { "Misfire", ((int)MisfireHandlingMode.Strict).ToString() },
        });
        AddJob("in-utc", "0 4 * * *", Next(TimeSpan.FromHours(4)), new Dictionary<string, string> { { "TimeZoneId", TimeZoneInfo.Utc.Id } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("data-id=\"nightly\"", html);
        Assert.Contains("data-cron=\"0 3 * * *\"", html);
        Assert.Contains("data-timezone=\"" + H(TimeZoneInfo.Utc.Id) + "\"", html);
        Assert.Contains("data-queue=\"critical\"", html);
        Assert.Contains("data-misfire=\"Strict\"", html);
        Assert.Contains("data-misfire=\"Relaxed\"", html);
        Assert.Contains("<span title=\"" + H(TimeZoneInfo.Utc.DisplayName) + "\" data-bs-container=\"body\">" + H(TimeZoneInfo.Utc.Id), html);
        Assert.Contains("data-start-at=\"\"", html);
        Assert.Contains("data-end-at=\"\"", html);
    }

    [Fact]
    public void Render_AddsAHiddenEditButton_ForTheJobToEdit_WhenItIsOnAnotherPage()
    {
        // /recurring?edit={id} only opened the editor when the job was on the page shown
        AddJob("on-this-page", "0 3 * * *", Next(TimeSpan.FromHours(3)));
        AddJob("elsewhere", "0 5 * * *", Next(TimeSpan.FromHours(5)), new Dictionary<string, string> { { "Queue", "reports" } });
        _ids.Remove("elsewhere");
        _renderer.Request.Query["edit"] = "elsewhere";

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("class=\"ns-icon-button js-recurring-edit d-none\" aria-haspopup=\"dialog\"", html);
        Assert.Contains("data-id=\"elsewhere\" data-cron=\"0 5 * * *\"", html);
        Assert.Contains("data-queue=\"reports\"", html);
        Assert.Equal(2, CountOf(html, "data-misfire="));
    }

    [Fact]
    public void Render_DoesNotAddAHiddenEditButton_ForAJobOnThisPage_AnUnknownOne_OrInReadOnlyMode()
    {
        AddJob("on-this-page", "0 3 * * *", Next(TimeSpan.FromHours(3)));
        _renderer.Request.Query["edit"] = "on-this-page";
        string onThisPage = _renderer.Render(new RecurringJobsPage());

        _renderer.Request.Query["edit"] = "missing";
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:missing")).Returns((Dictionary<string, string>)null);
        string missing = _renderer.Render(new RecurringJobsPage());

        AddJob("elsewhere", "0 5 * * *", Next(TimeSpan.FromHours(5)));
        _ids.Remove("elsewhere");
        _renderer.Request.Query["edit"] = "elsewhere";
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
        string readOnly = _renderer.Render(new RecurringJobsPage());

        Assert.DoesNotContain("js-recurring-edit hidden", onThisPage);
        Assert.DoesNotContain("js-recurring-edit hidden", missing);
        Assert.DoesNotContain("js-recurring-edit", readOnly);
        _renderer.Connection.Verify(x => x.GetAllEntriesFromHash("recurring-job:elsewhere"), Times.Never);
    }

    [Fact]
    public void Render_ShowsWhenAJob_StartsAndEnds()
    {
        DateTime start = new(2099, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
        DateTime end = new(2099, 6, 7, 8, 9, 10, 0, DateTimeKind.Utc);
        AddJob("later", "0 4 * * *", start.AddHours(1), new Dictionary<string, string>
        {
            { "StartAt", JobHelper.SerializeDateTime(start) },
            { "EndAt", JobHelper.SerializeDateTime(end) },
        });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains(string.Format(H(Strings.RecurringJobsPage_StartsAt), LocalTime(start)), html);
        Assert.Contains(string.Format(H(Strings.RecurringJobsPage_EndsAt), LocalTime(end)), html);
        Assert.DoesNotContain("data-moment=\"" + JobHelper.ToTimestamp(start.AddHours(1)), html);
        Assert.Contains("data-start-at=\"" + JobHelper.ToMillisecondTimestamp(start) + "\"", html);
        Assert.Contains("data-end-at=\"" + JobHelper.ToMillisecondTimestamp(end) + "\"", html);
    }

    [Fact]
    public void Render_ShowsTheEnd_AndTheNextRun_OfAnActiveJob()
    {
        DateTime next = Next(TimeSpan.FromHours(1));
        DateTime end = DateTime.UtcNow.AddDays(30);
        AddJob("active", "0 * * * *", next, new Dictionary<string, string>
        {
            { "StartAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddDays(-1)) },
            { "EndAt", JobHelper.SerializeDateTime(end) },
        });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("data-moment=\"" + JobHelper.ToTimestamp(next) + "\"", html);
        Assert.Contains(string.Format(H(Strings.RecurringJobsPage_EndsAt), LocalTime(end)), html);
        Assert.DoesNotContain(string.Format(H(Strings.RecurringJobsPage_StartsAt), "<span data-moment-local"), html);
        Assert.DoesNotContain(H(Strings.RecurringJobsPage_Ended_Tooltip), html);
    }

    [Fact]
    public void Render_SaysAJobHasEnded_InsteadOfDisabled_WhenItsEndHasPassed()
    {
        DateTime end = DateTime.UtcNow.AddDays(-2);
        AddJob("ended", "0 * * * *", null, new Dictionary<string, string> { { "EndAt", JobHelper.SerializeDateTime(end) } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("title=\"" + H(Strings.RecurringJobsPage_Ended_Tooltip) + "\">" + H(Strings.RecurringJobsPage_Ended) + "</span>", html);
        Assert.Contains("data-moment-local=\"" + JobHelper.ToTimestamp(end) + "\"", html);
        Assert.DoesNotContain(H(Strings.RecurringJobsPage_RecurringJobDisabled_Tooltip), html);
        Assert.DoesNotContain(string.Format(H(Strings.RecurringJobsPage_EndsAt), "<span data-moment-local"), html);
    }

    [Fact]
    public void Render_KeepsThePausedAndErrorLabels_OfAJobWithAnEnd()
    {
        DateTime end = DateTime.UtcNow.AddDays(-2);
        AddJob("paused", "0 * * * *", null, new Dictionary<string, string> { { "EndAt", JobHelper.SerializeDateTime(end) }, { "Paused", "1" } });
        AddJob("errored", "0 * * * *", null, new Dictionary<string, string> { { "EndAt", JobHelper.SerializeDateTime(end) }, { "Error", "Boom" } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains(H(Strings.RecurringJobsPage_Paused_Tooltip), html);
        Assert.Contains("<span class=\"ns-chip ns-chip-status ns-recurring-error\">" + DashboardMarkup.Icon("exclamation-circle-fill") + " " + H(Strings.Common_Error) + "</span>", html);
        Assert.DoesNotContain(H(Strings.RecurringJobsPage_Ended_Tooltip), html);
    }

    private static string LocalTime(DateTime value)
    {
        return "<span data-moment-local=\"" + JobHelper.ToTimestamp(value) + "\">";
    }

    [Fact]
    public void Render_FlagsAnUnknownTimeZone()
    {
        AddJob("on-mars", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string> { { "TimeZoneId", "Mars/Olympus_Mons" } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<span title=\"\" data-bs-container=\"body\">Mars/Olympus_Mons", html);
        Assert.Contains("<span class=\"hf-icon\" title=\"", html);
        Assert.Contains(DashboardMarkup.Icon("exclamation-circle-fill") + "</span>", html);
    }

    [Fact]
    public void Render_FlagsAnInvalidCronExpression()
    {
        AddJob("bad-cron", "every day", Next(TimeSpan.FromHours(3)));

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<code title=\"", html);
        Assert.Contains(DashboardMarkup.Icon("exclamation-circle-fill"), html);
        Assert.Contains("every day", html);
    }

    [Fact]
    public void Render_ShowsTheJobOrWhyItCouldNotBeLoaded()
    {
        AddJob("loaded", "0 3 * * *", Next(TimeSpan.FromHours(3)));
        AddJob("unloadable", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string>
        {
            { "Job", "{\"t\":\"Missing.Type, Missing.Assembly\",\"m\":\"Work\"}" },
        });
        AddJob("no-job", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string> { { "Job", "" } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("SampleJobs.Work", html);
        Assert.Contains("Missing.Assembly", html);
        Assert.Contains("<em class=\"ns-recurring-method\">" + H(Strings.Common_NotAvailable) + "</em>", html);
    }

    [Fact]
    public void Render_ColoursTheLastExecution_ByTheStateOfTheLastJob()
    {
        DateTime lastAt = DateTime.UtcNow.AddHours(-1);
        string lastExecution = JobHelper.SerializeDateTime(lastAt);
        AddJob("succeeded", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "LastExecution", lastExecution }, { "LastJobId", "101" } });
        AddJob("custom", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "LastExecution", lastExecution }, { "LastJobId", "102" } });
        AddJob("canceled", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "LastExecution", lastExecution } });
        AddJob("never", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "CreatedAt", lastExecution } });
        _renderer.Connection.Setup(x => x.GetStateData("101")).Returns(new StateData { Name = SucceededState.StateName });
        _renderer.Connection.Setup(x => x.GetStateData("102")).Returns(new StateData { Name = "Custom" });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<a class=\"ns-recurring-last\" href=\"/jobs/details/101\"><span class=\"ns-state-chip ns-state-succeeded label-hover\" data-state=\"Succeeded\">", html);
        Assert.Contains("<a class=\"ns-recurring-last\" href=\"/jobs/details/102\"><span class=\"ns-state-chip label-hover\" data-state=\"Custom\">", html);
        Assert.Contains("<span>Custom</span>", html);
        Assert.Contains("<span class=\"ns-recurring-last\" title=\"" + H(Strings.RecurringJobsPage_Canceled) + "\"><span class=\"ns-state-chip ns-state-deleted\" data-state=\"Deleted\">", html);
        Assert.Equal(1, CountOf(html, "<span class=\"ns-recurring-last\" title="));
        // The last execution is relative and exact
        Assert.Equal(3, CountOf(html, "<span class=\"ns-time-exact\"><span data-moment=\"" + JobHelper.ToTimestamp(lastAt)));
        Assert.Contains("<span class=\"ns-recurring-muted\">" + H(Strings.Common_NotAvailable) + "</span>", html);
    }

    [Fact]
    public void Render_ListsTheJobsWithoutAPager_WhenTheConnectionIsNotAJobStorageConnection()
    {
        Mock<IStorageConnection> connection = new();
        connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(["legacy"]);
        connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:legacy")).Returns(new Dictionary<string, string> { { "Cron", "0 0 * * *" } });
        _renderer.Storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("value=\"legacy\"", html);
        Assert.DoesNotContain("ns-pagination-pages", html);
        Assert.Contains("<span>" + H(Cron.TryDescribe("0 0 * * *")) + "</span>", html);
        Assert.Contains("<code class=\"cron-badge\">0 0 * * *</code>", html);
    }

    [Fact]
    public void Render_LinksEachJobsDetails_SoThatASlashAndALiteralPercent2FStayApart()
    {
        AddJob("a/b", "0 0 * * *", null);
        AddJob("a%2Fb", "0 0 * * *", null);

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("href=\"/recurring/details/a~2Fb\"", html);
        Assert.Contains("href=\"/recurring/details/a~252Fb\"", html);
    }

    [Fact]
    public void Render_HasTheSharedHeader_WithTheCount_AndRemoveAllSetApart()
    {
        AddJob("daily", "0 0 * * *", Next(TimeSpan.FromHours(1)));
        AddJob("hourly", "0 * * * *", Next(TimeSpan.FromMinutes(30)));

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<header class=\"ns-page-head\">", html);
        Assert.Contains("<span class=\"ns-page-name\">" + H(Strings.RecurringJobsPage_Title) + "</span> <span class=\"ns-count-chip js-list-count\">2</span></h1>", html);
        Assert.Contains("<button class=\"btn btn-sm ns-btn-outlined-error btn-delete-all\"", html);
        Assert.True(html.IndexOf("btn-delete-all", StringComparison.Ordinal) < html.IndexOf("<span class=\"ns-actions-divider\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("<span class=\"ns-actions-divider\"", StringComparison.Ordinal) < html.IndexOf("href=\"/recurring/forecast\"", StringComparison.Ordinal));
        Assert.Contains("data-list-primary=\"true\"", html);
        PageStructure.AssertHeadingsInOrder(PageStructure.WithoutTemplates(html));
        PageStructure.AssertNoEmptyHeaderCells(html);
    }

    [Fact]
    public void Render_ShowsEachJob_OnTwoLines_WithItsScheduleInWords_TimeZone_Queue_AndStatusChips()
    {
        AddJob("report", "0 9 * * 1-5", Next(TimeSpan.FromHours(2)), new Dictionary<string, string>
        {
            { "Queue", "reports" },
            { "TimeZoneId", TimeZoneInfo.Utc.Id },
            { RecurringJobEntity.PolicyField, "business-hours" },
            { RecurringJobSet.DeclaredField, RecurringJobSet.UnnamedMark },
            { RecurringJobSet.RetiredField, "1" },
            { "Paused", "1" },
        });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<a class=\"job-method\" href=\"/recurring/details/report\">report</a>", html);
        Assert.Matches("<span class=\"ns-recurring-method\">[^<]*SampleJobs.Work</span>", html);
        Assert.Contains("<span>" + H(Cron.TryDescribe("0 9 * * 1-5")) + "</span>", html);
        Assert.Contains("<code class=\"cron-badge\">0 9 * * 1-5</code>", html);
        Assert.Contains("<a class=\"ns-queue-chip\" href=\"/jobs/enqueued/reports\">", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"/policies#policy-business-hours\"", html);
        Assert.Contains("ns-recurring-declared\" title=\"" + H(Strings.RecurringJobDetailsPage_Declared_Tooltip) + "\">", html);
        Assert.Contains("ns-recurring-paused\" title=\"" + H(Strings.RecurringJobsPage_Paused_Tooltip) + "\">", html);
        Assert.Contains("ns-recurring-retired\">", html);
        Assert.Contains("<tr class=\"js-jobs-list-row hover ns-recurring-row-paused\">", html);
    }

    [Fact]
    public void Render_ShowsTheNextExecution_AsAChip_WithTheExactTimeUnderIt()
    {
        DateTime next = Next(TimeSpan.FromHours(2));
        AddJob("report", "0 9 * * 1-5", next);

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<span class=\"ns-chip ns-chip-status\">" + DashboardMarkup.Icon("hourglass-split") + " <span data-moment=\"" + JobHelper.ToTimestamp(next) + "\">", html);
        Assert.Contains("<span class=\"ns-time-exact\"><span data-moment-local=\"" + JobHelper.ToTimestamp(next) + "\">", html);
    }

    [Fact]
    public void Render_OffersTriggerPauseEditAndDelete_AsRowActions_WithDeleteSetApart_AndConfirmed()
    {
        AddJob("a/b", "0 0 * * *", Next(TimeSpan.FromHours(1)));
        AddJob("paused", "0 0 * * *", Next(TimeSpan.FromHours(1)), new Dictionary<string, string> { { "Paused", "1" } });

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains(Html.ActionsColumnHeader().ToString(), html);
        string trigger = H(string.Format(Strings.RecurringJobsPage_TriggerJob, "a/b"));
        Assert.Contains("<button type=\"button\" class=\"ns-icon-button\" data-ajax=\"/recurring/trigger-one/a~2Fb\" data-loading-text=\"" + H(Strings.RecurringJobsPage_Triggering) + "\" aria-label=\"" + trigger + "\" title=\"" + trigger + "\">", html);
        Assert.Contains("data-ajax=\"/recurring/pause-one/a~2Fb\"", html);
        Assert.Contains("data-ajax=\"/recurring/resume-one/paused\"", html);
        Assert.DoesNotContain("data-ajax=\"/recurring/resume-one/a~2Fb\"", html);
        Assert.DoesNotContain("data-ajax=\"/recurring/pause-one/paused\"", html);
        Assert.Contains("aria-label=\"" + H(string.Format(Strings.RecurringJobsPage_EditJob, "a/b")) + "\" data-id=\"a/b\"", html);

        string delete = H(string.Format(Strings.RecurringJobsPage_DeleteJob, "a/b"));
        int edit = html.IndexOf("data-id=\"a/b\" data-cron=", StringComparison.Ordinal);
        int divider = html.IndexOf("<span class=\"ns-actions-divider\" aria-hidden=\"true\"></span>", edit, StringComparison.Ordinal);
        int remove = html.IndexOf("class=\"ns-icon-button ns-row-danger js-job-command\" data-url=\"/recurring/remove\" data-job=\"a/b\"", StringComparison.Ordinal);
        Assert.True(edit > 0 && divider > edit && remove > divider, $"{edit} {divider} {remove}");
        Assert.Contains("aria-label=\"" + delete + "\" title=\"" + delete + "\"", html);
        Assert.Contains("data-confirm=\"" + H(string.Format(Strings.RecurringJobsPage_DeleteConfirmOne, "a/b")) + "\" data-confirm-body=\"" + H(Strings.RecurringJobsPage_RemoveAllConfirm) + "\" data-confirm-label=\"" + H(Strings.Common_Delete) + "\" data-confirm-destructive=\"true\"", html);
    }

    [Fact]
    public void Render_ShowsTheListWithoutCommands_ToReadOnlyUsers()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
        AddJob("daily", "0 0 * * *", Next(TimeSpan.FromHours(1)));

        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<a class=\"job-method\" href=\"/recurring/details/daily\">daily</a>", html);
        Assert.DoesNotContain("btn-delete-all", html);
        Assert.DoesNotContain("ns-actions-divider", html);
        Assert.DoesNotContain("data-ajax=", html);
        Assert.DoesNotContain("ns-list-commands", html);
        Assert.DoesNotContain(H(Strings.Common_Actions), html);
        Assert.Contains("js-manifest-export", html);
        PageStructure.AssertHeadingsInOrder(PageStructure.WithoutTemplates(html));
        PageStructure.AssertNoEmptyHeaderCells(html);
    }

    [Fact]
    public void Render_PutsTheScheduleEditor_InATemplateForTheSideSheet_WithGroupedLabelledFields()
    {
        string html = _renderer.Render(new RecurringJobsPage());

        Assert.Contains("<div id=\"recurringJobEditor\" hidden=\"hidden\"", html);
        Assert.Contains("data-update-url=\"/recurring/update/\"", html);
        Assert.Contains("data-text-title=\"" + H(Strings.RecurringJobsPage_EditSchedule) + "\"", html);
        Assert.Contains("<template class=\"js-recurring-edit-template\">", html);
        Assert.Contains("<form id=\"recurringJobEditorForm\" class=\"ns-sheet-form js-recurring-edit-form\" novalidate=\"novalidate\">", html);
        Assert.Contains("<legend>" + H(Strings.RecurringJobsPage_Group_When) + "</legend>", html);
        Assert.Contains("<legend>" + H(Strings.RecurringJobsPage_Group_How) + "</legend>", html);
        foreach (string field in (string[])["Cron", "TimeZone", "Queue", "Misfire"])
        {
            Assert.Contains("<label for=\"recurringJobEditor" + field + "\">", html);
            Assert.Contains("id=\"recurringJobEditor" + field + "\"", html);
        }

        Assert.Contains("name=\"cron\"", html);
        Assert.Contains("name=\"timezone\"", html);
        Assert.Contains("name=\"queue\"", html);
        Assert.Contains("name=\"misfire\"", html);
        Assert.Contains("<h3 id=\"recurringJobEditorNextRuns\">", html);
        Assert.Contains("<button type=\"submit\" form=\"recurringJobEditorForm\" class=\"btn btn-sm btn-primary js-recurring-edit-save\"", html);
        Assert.DoesNotContain("class=\"modal fade\" id=\"recurringJobEditor\"", html);
        Assert.Contains("<h2 class=\"modal-title\" id=\"manifestImportTitle\">", html);
    }

    private static HtmlHelper Html => new(new RecurringJobsPage());

    private void AddJob(string id, string cron, DateTime? nextExecution, Dictionary<string, string> extra = null)
    {
        Dictionary<string, string> hash = new()
        {
            { "Cron", cron },
            { "Job", InvocationData.SerializeJob(Job.FromExpression(() => SampleJobs.Work())).SerializePayload() },
        };

        if (nextExecution.HasValue)
        {
            hash["NextExecution"] = JobHelper.SerializeDateTime(nextExecution.Value);
        }

        foreach (KeyValuePair<string, string> pair in extra ?? [])
        {
            hash[pair.Key] = pair.Value;
        }

        _ids.Add(id);
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{id}")).Returns(hash);
    }

    private static DateTime Next(TimeSpan fromNow) => DateTime.UtcNow.Add(fromNow);

    private static int CountOf(string html, string fragment)
    {
        return (html.Length - html.Replace(fragment, string.Empty).Length) / fragment.Length;
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);

    public static class SampleJobs
    {
        public static void Work()
        {
        }
    }
}
