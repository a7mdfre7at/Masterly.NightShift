using System.Text.RegularExpressions;
using System.Net;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class FailedJobsPageFacts
{
    private readonly PageRenderer _renderer = new();
    private readonly DateTime _now = DateTime.UtcNow;

    [Fact]
    public void Render_WithoutFailedJobs_ShowsTheSuccessMessage()
    {
        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains(Encode(Strings.FailedJobsPage_NoJobs), html);
        Assert.DoesNotContain("js-jobs-list", html);
    }

    [Fact]
    public void Render_WithFailedJobs_ShowsTheToolbarWithDownloadAndCommands()
    {
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains("data-url=\"/jobs/failed/export\" data-filename=\"nightshift-failed-jobs.csv\"", html);
        Assert.Contains("data-ajax=\"/jobs/failed/delete-all\"", html);
        Assert.Contains("data-ajax=\"/jobs/failed/requeue-all\"", html);
        Assert.Contains("data-confirm=\"" + Encode(Strings.FailedJobsPage_RequeueAllConfirm_Title) + "\" data-confirm-body=\"" + Encode(Strings.FailedJobsPage_RequeueAllConfirm) + "\"", html);
        Assert.Contains("data-url=\"/jobs/failed/requeue\"", html);
        Assert.Contains("data-url=\"/jobs/failed/delete\"", html);
        Assert.Contains($"{Encode(Strings.Common_DeleteSelected)}", html);
    }

    [Fact]
    public void Render_ForALiveRefresh_RendersOnlyThePage_WithoutTheLayoutOrTheClusters()
    {
        // The Live toggle re-rendered the whole page every few seconds, clusters (five pages of failed jobs) included
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
        _renderer.Request.Query[RazorPage.ListRefreshQuery] = "1";

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains("js-jobs-list", html);
        Assert.DoesNotContain("<html", html);
        Assert.DoesNotContain("hf-clusters", html);
        _renderer.Monitoring.Verify(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void Render_WhenReadOnly_KeepsTheDownloadButHidesTheCommands()
    {
        SetupJobs(new Dictionary<string, FailedJobDto>
        {
            { "1", Failed("InvalidOperationException", "Boom") },
            { "2", Failed("InvalidOperationException", "Boom") }
        });
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains("data-url=\"/jobs/failed/export\"", html);
        Assert.Contains("class=\"ns-clusters hf-clusters\"", html);
        Assert.DoesNotContain("hf-cluster-actions", html);
        Assert.DoesNotContain("/jobs/failed/delete-all", html);
        Assert.DoesNotContain("/jobs/failed/requeue", html);
    }

    [Fact]
    public void Render_WithRepeatedFailures_GroupsThemIntoAClusterWithBulkActions()
    {
        SetupJobs(new Dictionary<string, FailedJobDto>
        {
            { "1", Failed("InvalidOperationException", "Connection <lost>", _now.AddMinutes(-10)) },
            { "2", Failed("InvalidOperationException", "Connection reset", _now.AddMinutes(-1)) },
            { "3", Failed("TimeoutException", "Too slow") }
        });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains(Encode(string.Format(Strings.FailedJobsPage_Clusters_Hint, 500)), html);
        Assert.Contains("data-cluster=\"InvalidOperationException|FailedPageJobs.Work\"", html);
        Assert.DoesNotContain("data-cluster=\"TimeoutException", html);
        Assert.Contains($"<div class=\"hf-cluster-count\" title=\"{Encode(string.Format(Strings.FailedJobsPage_Clusters_Jobs, 2))}\">2</div>", html);
        Assert.Contains("<span class=\"hf-cluster-exception\" title=\"InvalidOperationException\">InvalidOperationException</span> <code class=\"hf-cluster-method\">FailedPageJobs.Work</code>", html);
        Assert.Contains("<div class=\"hf-cluster-message text-muted\">Connection &lt;lost&gt;</div>", html);
        Assert.Contains($"<span>{Encode(Strings.FailedJobsPage_Clusters_FirstSeen)} <span data-moment=", html);
        Assert.Contains($"<span>&middot; {Encode(Strings.FailedJobsPage_Clusters_LastSeen)} <span data-moment=", html);
        Assert.Contains("data-jobs=\"[&quot;1&quot;,&quot;2&quot;]\"", html);
        Assert.Contains(Encode(string.Format(Strings.FailedJobsPage_Clusters_Requeue, 2)), html);
        Assert.Contains(Encode(string.Format(Strings.FailedJobsPage_Clusters_Delete, 2)), html);
        Assert.Contains($"data-confirm=\"{Encode(Strings.FailedJobsPage_Clusters_DeleteConfirm)}\"", html);
    }

    [Fact]
    public void Render_WithAClusterWithoutMessageOrDates_ShowsOnlyItsTitle()
    {
        SetupJobs(new Dictionary<string, FailedJobDto>
        {
            { "1", Failed("InvalidOperationException", null, failedAt: null) },
            { "2", Failed("InvalidOperationException", null, failedAt: null) }
        });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains("<span class=\"hf-cluster-exception\" title=\"InvalidOperationException\">InvalidOperationException</span>", html);
        Assert.DoesNotContain("hf-cluster-message", html);
        Assert.DoesNotContain(Encode(Strings.FailedJobsPage_Clusters_FirstSeen), html);
        Assert.DoesNotContain(Encode(Strings.FailedJobsPage_Clusters_LastSeen), html);
    }

    [Fact]
    public void Render_WithDistinctFailures_ShowsNoClusters()
    {
        SetupJobs(new Dictionary<string, FailedJobDto>
        {
            { "1", Failed("InvalidOperationException", "Boom") },
            { "2", Failed("TimeoutException", "Too slow") }
        });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.DoesNotContain("hf-clusters", html);
    }

    [Fact]
    public void Render_WithAnAcknowledgedFailure_ShowsWhoAcknowledgedIt()
    {
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
        SetupNotes("1", new JobNote { User = "bob", Text = "ticket OPS-1", Acknowledges = true });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains($"<span class=\"badge text-bg-info hf-ack\" title=\"bob: ticket OPS-1\">{DashboardMarkup.Icon("check-lg")} {Encode(Strings.FailedJobsPage_Acknowledged)}</span>", html);
    }

    [Fact]
    public void Render_WithAnAnonymousAcknowledgementWithoutText_ShowsAnonymous()
    {
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
        SetupNotes("1", new JobNote { Text = "", Acknowledges = true });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains($"<span class=\"badge text-bg-info hf-ack\" title=\"{Encode(Strings.Common_Anonymous)}\">", html);
    }

    [Fact]
    public void Render_WithNotesThatDontAcknowledge_ShowsNoAcknowledgement()
    {
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
        SetupNotes("1", new JobNote { User = "bob", Text = "looking into it" });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.DoesNotContain("hf-ack", html);
    }

    [Fact]
    public void Render_ReadsTheNotes_OfTheFirstHundredRowsOnly()
    {
        Dictionary<string, FailedJobDto> jobs = [];
        for (int i = 1; i <= 150; i++)
        {
            jobs.Add(i.ToString(), Failed("InvalidOperationException", "Boom"));
        }

        SetupJobs(jobs);
        _renderer.Request.Query["count"] = "500";

        _renderer.Render(new FailedJobsPage());

        _renderer.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), JobNotes.ParameterName), Times.Exactly(100));
        _renderer.Connection.Verify(x => x.GetJobParameter("101", JobNotes.ParameterName), Times.Never);
    }

    [Fact]
    public void Render_OffersAtMostOneThousandRowsPerPage()
    {
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Matches("from=0&amp;count=1000\"\\s*>" + Regex.Escape(Encode(1000.ToString("N0"))) + "</option>", html);
        Assert.DoesNotContain("count=5000", html);
    }

    [Fact]
    public void Render_WithExpiredOrMovedJobs_DoesNotReadTheirNotes()
    {
        SetupJobs(new Dictionary<string, FailedJobDto>
        {
            { "1", null },
            { "2", new FailedJobDto { Job = SampleJob(), InFailedState = false } }
        });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains(Encode(Strings.Common_JobExpired), html);
        Assert.DoesNotContain("hf-ack", html);
        _renderer.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), JobNotes.ParameterName), Times.Never);
    }

    [Fact]
    public void Render_WithAFailure_ShowsItsExceptionInTheRow_AndItsDetailsInASideSheetTemplate()
    {
        FailedJobDto failed = Failed("System.InvalidOperationException", "Connection <lost>");
        failed.ExceptionDetails = "System.InvalidOperationException: Connection lost";
        failed.StateData = new Dictionary<string, string> { { "ServerId", "web-1:42:abc" } };
        SetupJobs(new Dictionary<string, FailedJobDto> { { "7", failed } });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains("<code class=\"ns-exception-type\" title=\"System.InvalidOperationException\">InvalidOperationException</code>", html);
        Assert.Contains("<span class=\"ns-job-error-message\">Connection &lt;lost&gt;</span>", html);
        Assert.Contains("class=\"btn btn-sm ns-btn-text js-sheet-open\" aria-haspopup=\"dialog\" data-sheet-title=\"FailedPageJobs.Work\" data-sheet-overline=\"#7\"", html);
        Assert.Contains("<template class=\"js-sheet-template\">", html);
        Assert.Contains("<pre class=\"stack-trace\"><code>", html);
        Assert.Contains("<div class=\"js-sheet-actions\">", html);
        Assert.Contains("data-url=\"/jobs/failed/requeue\" data-job=\"7\"", html);
        Assert.DoesNotContain("failed-job-details", html);
        Assert.DoesNotContain("class=\"expander\"", html);
    }

    [Fact]
    public void Render_WithFailures_PutsTheCountInTheHeader_AndTheRequeueAllThere()
    {
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });

        string html = _renderer.Render(new FailedJobsPage());

        Assert.Contains("<span class=\"ns-count-chip js-list-count\">1</span>", html);
        Assert.Contains("class=\"btn btn-sm btn-primary btn-requeue-all\"", html);
        Assert.Contains("class=\"ns-note js-note\" data-note=\"failed-jobs-retention\"", html);
        Assert.Contains($"aria-label=\"{Encode(string.Format(Strings.Common_RequeueJob, "#1"))}\"", html);
        Assert.Contains($"aria-label=\"{Encode(string.Format(Strings.Common_DeleteJob, "#1"))}\"", html);
        Assert.Contains($"<span class=\"visually-hidden\">{Encode(Strings.Common_Actions)}</span>", html);
        Assert.DoesNotContain("<th scope=\"col\" class=\"min-width\"></th>", html);
    }

    [Fact]
    public void Render_ForALiveRefresh_LeavesTheNoteOut()
    {
        SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
        _renderer.Request.Query[RazorPage.ListRefreshQuery] = "1";

        string html = _renderer.Render(new FailedJobsPage());

        Assert.DoesNotContain("js-note", html);
        Assert.Contains("js-list-count", html);
    }

    private void SetupJobs(Dictionary<string, FailedJobDto> jobs)
    {
        _renderer.Monitoring.Setup(x => x.FailedCount()).Returns(jobs.Count);
        _renderer.Monitoring.Setup(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<FailedJobDto>(jobs));
    }

    private void SetupNotes(string jobId, params JobNote[] notes)
    {
        _renderer.Connection.Setup(x => x.GetJobParameter(jobId, JobNotes.ParameterName))
            .Returns(SerializationHelper.Serialize(notes, SerializationOption.User));
    }

    private FailedJobDto Failed(string exceptionType, string message, DateTime? failedAt = default)
    {
        return new FailedJobDto
        {
            Job = SampleJob(),
            InFailedState = true,
            ExceptionType = exceptionType,
            ExceptionMessage = message,
            FailedAt = failedAt,
            Reason = "An exception occurred"
        };
    }

    private static Job SampleJob() => Job.FromExpression(() => FailedPageJobs.Work());

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

public static class FailedPageJobs
{
    public static void Work()
    {
    }
}
