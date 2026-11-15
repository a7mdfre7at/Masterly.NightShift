using System.Globalization;
using System.Net;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class JobDetailsPageFacts
{
    private const string JobId = "1";

    private readonly PageRenderer _renderer = new();
    private readonly DateTime _now = DateTime.UtcNow;

    [Fact]
    public void Render_SaysTheJobExpired_WhenItCannotBeFound()
    {
        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<h1 id=\"page-title\" class=\"hf-page-header\"><span class=\"ns-page-name\">" + H(Strings.Common_Job) + "</span></h1>", html);
        Assert.Contains(H(string.Format(Strings.JobDetailsPage_JobExpired, JobId)), html);
        Assert.DoesNotContain("job-snippet", html);
        Assert.Contains("id=\"catalogRunner\"", html);
    }

    [Fact]
    public void Render_ShowsTheSnippet_WithEveryToolbarCommand()
    {
        SetupJob(Details(EnqueuedState.StateName, job: Job.FromExpression(() => SampleJobs.Work(), "critical")));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("SampleJobs.Work", html);
        Assert.Contains("js-watch-toggle\" data-id=\"1\"", html);
        Assert.Contains("data-state=\"Enqueued\"", html);
        Assert.Contains("data-url=\"/jobs/export/1\" data-filename=\"job-1.json\"", html);
        Assert.Contains("js-catalog-run", html);
        Assert.Contains("data-method=\"Work\"", html);
        Assert.Contains("js-copy-code\" data-code=\"", html);
        Assert.Contains("<span class=\"comment\">// " + H(Strings.QueuesPage_Table_Queue) + ": critical</span>", html);
        Assert.DoesNotContain("data-ajax=\"/jobs/actions/requeue/1\"", html);
        Assert.Contains("data-ajax=\"/jobs/actions/delete/1\"", html);
        Assert.Contains("id=\"catalogRunner\"", html);
    }

    [Fact]
    public void Render_EncodesAnIdWithASlashAndALiteralPercent2F_InEveryUrlOfThePage()
    {
        // A third-party storage may use any text as a job id; the route hands the page the decoded id
        const string id = "a/b%2Fc~d";
        _renderer.Monitoring.Setup(x => x.JobDetails(id)).Returns(Details(FailedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(id));

        Assert.Contains("data-url=\"/jobs/export/a~2Fb~252Fc~7Ed\"", html);
        Assert.Contains("data-url=\"/jobs/notes/add/a~2Fb~252Fc~7Ed\"", html);
        Assert.Contains("data-ajax=\"/jobs/actions/requeue/a~2Fb~252Fc~7Ed\"", html);
        Assert.Contains("data-ajax=\"/jobs/actions/delete/a~2Fb~252Fc~7Ed\"", html);
        Assert.DoesNotContain("/jobs/export/a/", html);
        Assert.DoesNotContain("/jobs/actions/delete/a/", html);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Processing")]
    [InlineData("Succeeded")]
    [InlineData("Deleted")]
    [InlineData("Scheduled")]
    public void Render_ShowsRequeue_ForAJobThatDoesNotWaitInItsQueue(string state)
    {
        SetupJob(Details(state));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("data-ajax=\"/jobs/actions/requeue/1\"", html);
    }

    [Fact]
    public void Render_OmitsTheQueueComment_WhenTheJobHasNoQueue()
    {
        SetupJob(Details(EnqueuedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("// " + H(Strings.QueuesPage_Table_Queue) + ":", html);
    }

    [Fact]
    public void Render_PrefillsTheRunAgainForm_WithTheJobArguments_ExceptInjectedAndRedactedOnes()
    {
        SetupJob(Details(EnqueuedState.StateName, job: Job.FromExpression(() => SampleJobs.Report("monthly", DayOfWeek.Monday, "s3cr3t", CancellationToken.None))));

        string html = _renderer.Render(new JobDetailsPage(JobId));
        string parameters = Attribute(html, "data-parameters");

        Assert.Contains("\"name\":\"name\"", parameters);
        Assert.Contains("\"default\":\"monthly\"", parameters);
        Assert.Contains("\"name\":\"day\"", parameters);
        Assert.Contains("\"input\":\"select\"", parameters);
        Assert.Contains("\"Monday\"", parameters);
        Assert.Contains("\"name\":\"secret\"", parameters);
        Assert.Contains("\"redacted\":true", parameters);
        Assert.DoesNotContain("s3cr3t", parameters);
        Assert.DoesNotContain("\"name\":\"token\"", parameters);
        Assert.DoesNotContain("s3cr3t", html);
        Assert.Contains("hf-redacted", html);
    }

    [Fact]
    public void Render_HidesEveryCommand_WhenTheDashboardIsReadOnly()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
        SetupJob(Details(FailedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("js-watch-toggle", html);
        Assert.DoesNotContain("js-catalog-run", html);
        Assert.DoesNotContain("js-note-form", html);
        Assert.DoesNotContain("js-note-ack", html);
        Assert.DoesNotContain("/jobs/actions/requeue/", html);
        Assert.DoesNotContain("/jobs/actions/delete/", html);
        Assert.DoesNotContain("id=\"catalogRunner\"", html);
    }

    [Fact]
    public void Render_ListsTheDebugParameters_WhenTheTargetMethodCannotBeFound()
    {
        JobDetailsDto details = Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
        {
            { "DBG_Type", "Missing.Type, Missing" },
            { "DBG_Method", "Work" },
        });
        details.Job = null;
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<span class=\"ns-overline\">" + H(Strings.Common_Job) + "</span><span class=\"ns-page-name\">" + H(Strings.Common_Job) + "</span></h1>", html);
        Assert.Contains("<h2 id=\"job-details-missing-method\" class=\"ns-alert-heading\">", html);
        Assert.Contains("<td class=\"width-15\">Type</td>", html);
        Assert.Contains("<pre><code>Missing.Type, Missing</code></pre>", html);
        Assert.Contains("<td class=\"width-15\">Method</td>", html);
        Assert.DoesNotContain("job-snippet-toolbar", html);
        Assert.DoesNotContain("id=\"job-details-parameters\"", html);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Render_ShowsTheRawArguments_OfAJobWhoseMethodCanNotBeFound_OnlyToUsersWhoCanChangeJobs(bool readOnly, bool shown)
    {
        // Without the method, [Redacted] can't be read, so the raw arguments may hold secrets
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => readOnly };
        JobDetailsDto details = Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
        {
            { "DBG_Type", "Missing.Type, Missing" },
            { "DBG_Method", "Connect" },
            { "DBG_Args", "[\"db\",\"hunter2\"]" },
            { "DBG_Payload", "{\"a\":\"hunter2\"}" },
        });
        details.Job = null;
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<pre><code>Missing.Type, Missing</code></pre>", html);
        Assert.Equal(shown, html.Contains("hunter2"));
        Assert.Equal(shown, html.Contains("<td class=\"width-15\">Args</td>"));
        Assert.Equal(shown, html.Contains("<td class=\"width-15\">Payload</td>"));
    }

    [Fact]
    public void Render_WarnsThatTheJobWasAborted_WhenItsServerIsGone()
    {
        SetupJob(Details(ProcessingState.StateName, ProcessingData("srv-gone")));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(string.Format(Strings.JobDetailsPage_JobAbortedNotActive_Warning_Html, "srv-gone", "/servers"), html);
    }

    [Fact]
    public void Render_ReadsTheServer_FromTheLegacyServerNameKey()
    {
        SetupJob(Details(ProcessingState.StateName, new Dictionary<string, string> { { "ServerName", "srv-legacy" }, { "WorkerId", "worker-1" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(string.Format(Strings.JobDetailsPage_JobAbortedNotActive_Warning_Html, "srv-legacy", "/servers"), html);
    }

    [Fact]
    public void Render_AProcessingStateWithoutAServer_ShowsTheJob_WithoutTheAbortedWarning()
    {
        // Data of another writer: no ServerId and no legacy ServerName
        SetupJob(Details(ProcessingState.StateName, new Dictionary<string, string> { { "WorkerId", "worker-1" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("SampleJobs.Work", html);
        Assert.DoesNotContain(Strings.JobDetailsPage_JobAbortedNotActive_Warning_Html[..20], html);
    }

    [Fact]
    public void Render_AProcessingStateWithAnUnreadableStart_ShowsTheJob()
    {
        SetupJob(Details(ProcessingState.StateName, new Dictionary<string, string> { { "ServerId", "srv-1" }, { "StartedAt", "nonsense" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("SampleJobs.Work", html);
    }

    [Fact]
    public void Render_ASucceededStateWithAnUnreadableDuration_ShowsTheJob()
    {
        SetupJob(Details(SucceededState.StateName, new Dictionary<string, string> { { "PerformanceDuration", "abc" }, { "Latency", "x" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("SampleJobs.Work", html);
    }

    [Fact]
    public void Render_AScheduledStateWithAnUnreadableEnqueueAt_ShowsTheJob()
    {
        SetupJob(Details(ScheduledState.StateName, new Dictionary<string, string> { { "EnqueueAt", "nonsense" }, { "ScheduledAt", "nonsense" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("SampleJobs.Work", html);
    }

    [Fact]
    public void Render_EncodesTheServerId_InTheAbortedJobWarning()
    {
        SetupJob(Details(ProcessingState.StateName, ProcessingData("<img src=x onerror=alert(1)>")));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(string.Format(Strings.JobDetailsPage_JobAbortedNotActive_Warning_Html, "&lt;img src=x onerror=alert(1)&gt;", "/servers"), html);
        Assert.DoesNotContain("<img src=x", html);
    }

    [Fact]
    public void Render_ShowsTheNextStatesTypeName_WhenAContinuationsNextStateCanNotBeLoaded()
    {
        JobDetailsDto details = Details(AwaitingState.StateName, new Dictionary<string, string>
        {
            { "ParentId", "7" },
            { "NextState", "{\"$type\":\"Contoso.Jobs.HoldState, Contoso.Jobs\"}" },
            { "Options", "OnlyOnSucceededState" },
        });
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<code>Contoso.Jobs.HoldState</code>", html);
    }

    [Fact]
    public void Render_WarnsThatTheJobMayBeAborted_WhenItsServerMissedItsHeartbeat()
    {
        _renderer.Monitoring.Setup(x => x.Servers()).Returns([Server("srv-stale", _now.AddMinutes(-10))]);
        SetupJob(Details(ProcessingState.StateName, ProcessingData("srv-stale")));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(string.Format(Strings.JobDetailsPage_JobAbortedWithHeartbeat_Warning_Html, "srv-stale"), html);
    }

    [Fact]
    public void Render_ShowsTheLiveView_ForAProcessingJobOnALiveServer()
    {
        _renderer.Monitoring.Setup(x => x.Servers()).Returns([Server("srv-live", _now)]);
        SetupJob(Details(ProcessingState.StateName, ProcessingData("srv-live")));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain(string.Format(Strings.JobDetailsPage_JobAbortedWithHeartbeat_Warning_Html, "srv-live"), html);
        Assert.Contains("id=\"jobLive\"", html);
        Assert.Contains("data-live-url=\"/jobs/live\"", html);
        Assert.Contains("data-poll=\"true\"", html);
        Assert.Contains("data-started-at=\"" + JobHelper.ToTimestamp(_now.AddSeconds(-30)), html);
        Assert.Contains("<span class=\"ns-chip ns-chip-status job-live-indicator\">" + DashboardMarkup.Icon("circle-fill") + " " + H(Strings.JobDetailsPage_Live) + "</span>", html);
        Assert.Contains("<div class=\"ns-meter ns-meter-lg ns-meter-active\" role=\"progressbar\"", html);
        Assert.Contains("aria-valuenow=\"0\"", html);
        Assert.Contains("<span class=\"js-live-elapsed-label\" style=\"\">", html);
        Assert.Contains(H(Strings.JobDetailsPage_NoConsoleOutput), html);
    }

    [Fact]
    public void Render_HidesTheElapsedTime_WhenAProcessingJobHasNoStartTime()
    {
        SetupJob(Details(ProcessingState.StateName, new Dictionary<string, string> { { "ServerId", "srv-1" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("data-started-at=\"\"", html);
        Assert.Contains("style=\"display: none\">" + H(Strings.JobDetailsPage_Live_Elapsed), html);
    }

    [Fact]
    public void Render_DoesNotShowTheLiveView_WhenTheJobNeverReportedAnything()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData()));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("id=\"jobLive\"", html);
    }

    [Fact]
    public void Render_ShowsTheConsole_AndTheCheckpoints_InTheOrderTheyWereWritten()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData()));
        SetupConsole(
            new JobConsoleEntry { Timestamp = 7000, Sequence = 4, Checkpoint = "upload" },
            new JobConsoleEntry { Timestamp = 1000, Sequence = 1, Level = JobConsoleLevel.Info, Message = "Starting" },
            new JobConsoleEntry { Timestamp = 2000, Sequence = 2, Checkpoint = "download" },
            new JobConsoleEntry { Timestamp = 5000, Sequence = 3, Level = JobConsoleLevel.Warning, Message = "Slow <disk>" });

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("data-poll=\"false\"", html);
        Assert.DoesNotContain("role=\"progressbar\"", html);
        Assert.DoesNotContain(H(Strings.JobDetailsPage_NoConsoleOutput), html);
        Assert.Contains("<div class=\"job-console-line job-console-info\"><span class=\"job-console-time\" data-live-time=\"1000\" data-moment-local=\"1\" data-moment-local-format=\"timeWithSeconds\">00:00:01</span><span class=\"job-console-text\">Starting</span></div>", html);
        Assert.Contains("job-console-warning\"><span class=\"job-console-time\" data-live-time=\"5000\" data-moment-local=\"5\" data-moment-local-format=\"timeWithSeconds\">00:00:05</span><span class=\"job-console-text\">Slow &lt;disk&gt;</span></div>", html);
        Assert.Contains("job-console-checkpoint\"><span class=\"job-console-time\" data-live-time=\"2000\" data-moment-local=\"2\" data-moment-local-format=\"timeWithSeconds\">00:00:02</span><span class=\"job-console-text\">" + DashboardMarkup.Icon("flag-fill") + " download</span></div>", html);
        Assert.Contains("<div class=\"ns-live-columns ns-live-with-checkpoints\">", html);
        Assert.Contains("<h3 class=\"ns-live-subhead\">" + H(Strings.JobDetailsPage_Checkpoints) + "</h3>", html);
        Assert.True(html.IndexOf(">Starting<", StringComparison.Ordinal) < html.IndexOf(">Slow &lt;", StringComparison.Ordinal));
        Assert.Contains("<li><span class=\"job-checkpoint-name\">download</span><span class=\"text-muted job-checkpoint-delta\" data-moment-local=\"2\" data-moment-local-format=\"timeWithSeconds\">00:00:02</span></li>", html);
        Assert.Contains("<li><span class=\"job-checkpoint-name\">upload</span><span class=\"text-muted job-checkpoint-delta\">+" + Duration(TimeSpan.FromSeconds(5)) + "</span></li>", html);
    }

    [Theory]
    [InlineData("Failed", "class=\"ns-meter ns-meter-lg ns-meter-failed\"")]
    [InlineData("Processing", "class=\"ns-meter ns-meter-lg ns-meter-active\"")]
    [InlineData("Succeeded", "class=\"ns-meter ns-meter-lg \"")]
    [InlineData("Enqueued", "class=\"ns-meter ns-meter-lg \"")]
    public void Render_ColoursTheProgressBar_ByTheStateOfTheJob(string state, string expected)
    {
        SetupJob(Details(state));
        _renderer.Connection.Setup(x => x.GetJobParameter(JobId, JobConsole.ProgressParameter))
            .Returns(SerializationHelper.Serialize(new JobProgress { Percent = 30, Timestamp = 1 }, SerializationOption.Internal));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(expected, html);
        Assert.Contains("aria-valuenow=\"30\"", html);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Enqueued")]
    [InlineData("Awaiting")]
    public void Render_ShowsTheConsoleWithoutAProgressBar_WhenTheJobReportedNoProgress(string state)
    {
        SetupJob(Details(state));
        SetupConsole(new JobConsoleEntry { Timestamp = 1000, Level = JobConsoleLevel.Info, Message = "Hello" });

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"jobLive\"", html);
        Assert.Contains("<span class=\"job-console-text\">Hello</span>", html);
        Assert.DoesNotContain("role=\"progressbar\"", html);
        Assert.Contains("<div class=\"ns-live-columns\">", html);
        Assert.DoesNotContain("js-live-checkpoints", html);
    }

    [Fact]
    public void Render_ShowsTheReportedProgress_EvenWithoutConsoleOutput()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData()));
        _renderer.Connection.Setup(x => x.GetJobParameter(JobId, JobConsole.ProgressParameter))
            .Returns(SerializationHelper.Serialize(new JobProgress { Percent = 40, Message = "Halfway <there>", Timestamp = 1 }, SerializationOption.Internal));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"jobLive\"", html);
        Assert.Contains("role=\"progressbar\" aria-label=\"" + H(Strings.ProcessingJobsPage_Table_Progress) + "\" aria-valuemin=\"0\" aria-valuemax=\"100\" aria-valuenow=\"40\">", html);
        Assert.Contains("<span class=\"js-live-bar\" style=\"width: 40%\"></span>", html);
        Assert.Contains("<span class=\"ns-live-percent js-live-percent\">40%</span>", html);
        Assert.Contains("<span class=\"js-live-progress-message\">Halfway &lt;there&gt;</span>", html);
        Assert.Contains(H(Strings.JobDetailsPage_NoConsoleOutput), html);
    }

    [Fact]
    public void Render_WritesTheProgressWidth_WithADecimalPoint_InEveryCulture()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData()));
        _renderer.Connection.Setup(x => x.GetJobParameter(JobId, JobConsole.ProgressParameter))
            .Returns(SerializationHelper.Serialize(new JobProgress { Percent = 42.5, Timestamp = 1 }, SerializationOption.Internal));
        CultureInfo culture = Thread.CurrentThread.CurrentCulture;

        string html;
        try
        {
            CultureHelper.SetCurrentCulture("de-DE");
            html = _renderer.Render(new JobDetailsPage(JobId));
        }
        finally
        {
            CultureHelper.SetCurrentCulture(culture);
        }

        Assert.Contains("aria-valuenow=\"42.5\">", html);
        Assert.Contains("<span class=\"js-live-bar\" style=\"width: 42.5%\"></span>", html);
        Assert.Contains("<span class=\"ns-live-percent js-live-percent\">42,5%</span>", html);
    }

    [Fact]
    public void Render_ShowsANoteWithoutText_AsAnEmptyNote()
    {
        // Notes written by other tools may have no text at all
        SetupJob(Details(FailedState.StateName, properties: new Dictionary<string, string>
        {
            { JobNotes.ParameterName, "[{\"t\":1,\"u\":\"bob\",\"m\":null}]" },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<strong>bob</strong>", html);
        Assert.DoesNotContain("hf-note-text", html);
    }

    [Fact]
    public void Render_SaysTheDeadlineWasMissed_ByHowMuch()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData(), properties: new Dictionary<string, string>
        {
            { DeadlineAttribute.DeadlineMissedParameter, SerializationHelper.Serialize((TimeSpan?)TimeSpan.FromMinutes(5), SerializationOption.User) },
            { DeadlineAttribute.DeadlineAtParameter, SerializationHelper.Serialize((DateTime?)_now.AddHours(-1), SerializationOption.User) },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(H(string.Format(Strings.JobDetailsPage_DeadlineMissed, Duration(TimeSpan.FromMinutes(5)))), html);
        Assert.DoesNotContain(H(Strings.Common_Overdue), html);
        Assert.DoesNotContain(">" + DeadlineAttribute.DeadlineMissedParameter + "<", html);
        Assert.DoesNotContain(">" + DeadlineAttribute.DeadlineAtParameter + "<", html);
    }

    [Fact]
    public void Render_ShowsAnUpcomingDeadline_AsInformation()
    {
        SetupJob(Details(EnqueuedState.StateName, properties: Deadline(_now.AddHours(1))));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<div class=\"alert alert-info\">\r\n                        " + DashboardMarkup.Icon("clock"), html);
        Assert.Contains(H(Strings.Common_Deadline) + ": <span data-moment=", html);
        Assert.DoesNotContain(H(Strings.Common_Overdue), html);
    }

    [Fact]
    public void Render_ShowsAPassedDeadline_AsOverdue()
    {
        SetupJob(Details(EnqueuedState.StateName, properties: Deadline(_now.AddHours(-1))));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<div class=\"alert alert-danger\">\r\n                        " + DashboardMarkup.Icon("clock"), html);
        Assert.Contains("<span class=\"badge text-bg-danger text-uppercase\">" + H(Strings.Common_Overdue) + "</span>", html);
    }

    [Fact]
    public void Render_IgnoresTheDeadline_OfASucceededJob()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData(), properties: Deadline(_now.AddHours(-1))));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain(H(Strings.Common_Deadline) + ": <span data-moment=", html);
    }

    [Fact]
    public void Render_IgnoresAnEmptyDeadline()
    {
        SetupJob(Details(EnqueuedState.StateName, properties: new Dictionary<string, string> { { DeadlineAttribute.DeadlineAtParameter, "null" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain(H(Strings.Common_Deadline) + ": <span data-moment=", html);
    }

    [Fact]
    public void Render_ShowsTheJobExpiration_WhenItIsSet()
    {
        JobDetailsDto details = Details(SucceededState.StateName, SucceededData());
        details.ExpireAt = _now.AddDays(1);
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(string.Format(Strings.JobDetailsPage_JobFinished_Warning_Html, JobHelper.ToTimestamp(details.ExpireAt.Value), details.ExpireAt), html);
    }

    [Fact]
    public void Render_LinksTheTagsAndTheBatches_OfTheJob()
    {
        SetupJob(Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
        {
            { JobTags.ParameterName, SerializationHelper.Serialize(new[] { "billing", "eu west" }, SerializationOption.User) },
            { BatchJob.ParameterName, SerializationHelper.Serialize("batch 1", SerializationOption.User) },
            { BatchJob.ContinuationParameterName, SerializationHelper.Serialize("batch-0", SerializationOption.User) },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"/batches/batch%201\">" + DashboardMarkup.Icon("grid-fill") + " " + H(Strings.JobDetailsPage_Batch) + " batch 1</a>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"/batches/batch-0\">" + DashboardMarkup.Icon("grid-fill") + " " + H(Strings.JobDetailsPage_BatchContinuation) + " batch-0</a>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist ns-tag-chip\" href=\"/tags/billing\" title=\"" + H(Strings.JobDetailsPage_Tags) + "\">" + DashboardMarkup.Icon("tag-fill") + " billing</a>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist ns-tag-chip\" href=\"/tags/eu%20west\" title=\"" + H(Strings.JobDetailsPage_Tags) + "\">" + DashboardMarkup.Icon("tag-fill") + " eu west</a>", html);
        Assert.DoesNotContain("id=\"job-details-parameters\"", html);
    }

    [Fact]
    public void Render_LinksTheChain_OfAChainStep()
    {
        SetupJob(Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
        {
            { JobChains.ChainIdParameter, SerializationHelper.Serialize("0123456789abcdef", SerializationOption.User) },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"/chains/0123456789abcdef\">" + DashboardMarkup.Icon("link-45deg") + " " + H(string.Format(Strings.ChainDetailsPage_Title, "01234567")) + "</a>", html);
    }

    [Fact]
    public void Render_ShowsTheBatch_WithoutTagChips_WhenTheJobHasNoTags()
    {
        SetupJob(Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
        {
            { BatchJob.ParameterName, SerializationHelper.Serialize("batch-1", SerializationOption.User) },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("href=\"/batches/batch-1\">" + DashboardMarkup.Icon("grid-fill"), html);
        Assert.DoesNotContain("ns-tag-chip", html);
    }

    [Fact]
    public void Render_ShowsNoMembershipChips_WhenTheJobHasNeitherTagsNorBatches()
    {
        SetupJob(Details(EnqueuedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("ns-tag-chip", html);
        Assert.DoesNotContain("href=\"/batches/", html);
        Assert.DoesNotContain("href=\"/chains/", html);
    }

    [Fact, StaticLock]
    public void Render_ShowsADiagnosisProblem_WithALink_WhenNoServerCouldRunTheJob()
    {
        DrainMode.InvalidateCache();
        SetupJob(Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"job-details-diagnosis\"", html);
        Assert.Contains("<li class=\"hf-diagnosis-item hf-diagnosis-problem\" data-code=\"no-servers\">", html);
        Assert.Contains(DashboardMarkup.Icon("x-circle-fill", "hf-diagnosis-icon"), html);
        Assert.Contains("<a class=\"hf-diagnosis-link\" href=\"/servers\">" + H(Strings.NavigationMenu_Servers) + " &rarr;</a>", html);
    }

    [Fact, StaticLock]
    public void Render_ShowsADiagnosisWarning_WhenTheJobWaitsTooLong()
    {
        DrainMode.InvalidateCache();
        _renderer.Monitoring.Setup(x => x.Servers()).Returns([Server("srv-1", _now)]);
        SetupJob(Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }, createdAt: _now.AddMinutes(-10)));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("hf-diagnosis-warning\" data-code=\"waiting\"", html);
        Assert.Contains(DashboardMarkup.Icon("exclamation-triangle-fill", "hf-diagnosis-icon"), html);
        Assert.Contains("href=\"/jobs/enqueued/default\"", html);
    }

    [Fact, StaticLock]
    public void Render_ShowsADiagnosisInfo_WithoutALink_WhenTheJobIsAboutToRun()
    {
        DrainMode.InvalidateCache();
        _renderer.Monitoring.Setup(x => x.Servers()).Returns([Server("srv-1", _now)]);
        SetupJob(Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("hf-diagnosis-info\" data-code=\"healthy\"", html);
        Assert.Contains(DashboardMarkup.Icon("info-circle-fill", "hf-diagnosis-icon"), html);
        Assert.DoesNotContain("hf-diagnosis-link", html);
    }

    [Fact]
    public void Render_ShowsNoDiagnosis_ForASucceededJob()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData()));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("id=\"job-details-diagnosis\"", html);
    }

    [Fact]
    public void Render_ShowsAnEmptyNoteList_AndTheAcknowledgeButton_ForAFailedJob()
    {
        SetupJob(Details(FailedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<li class=\"hf-note hf-note-empty text-muted\">" + H(Strings.JobDetailsPage_Notes_Empty) + "</li>", html);
        Assert.Contains("data-url=\"/jobs/notes/add/1\"", html);
        Assert.Contains("js-note-ack", html);
        Assert.DoesNotContain("hf-ack", html);
    }

    [Fact]
    public void Render_OffersNoAcknowledgeButton_ForAJobThatDidNotFail()
    {
        SetupJob(Details(EnqueuedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("js-note-form", html);
        Assert.DoesNotContain("js-note-ack", html);
    }

    [Fact]
    public void Render_ListsTheNotes_AndShowsTheAcknowledgement()
    {
        List<JobNote> notes =
        [
            new() { Timestamp = JobHelper.ToTimestamp(_now.AddMinutes(-5)), User = "alice", Text = "Partner API <down>" },
            new() { Timestamp = JobHelper.ToTimestamp(_now.AddMinutes(-1)), Text = "", Acknowledges = true },
        ];
        SetupJob(Details(FailedState.StateName, properties: new Dictionary<string, string>
        {
            { JobNotes.ParameterName, SerializationHelper.Serialize(notes, SerializationOption.User) },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<span class=\"badge text-bg-info hf-ack\">", html);
        Assert.DoesNotContain("hf-note-empty", html);
        Assert.Contains("<strong>alice</strong>", html);
        Assert.Contains("<div class=\"hf-note-text\">Partner API &lt;down&gt;</div>", html);
        Assert.Contains("<li class=\"hf-note hf-note-ack\">", html);
        Assert.Contains("<strong>" + H(Strings.Common_Anonymous) + "</strong>", html);
        Assert.Contains("<div class=\"hf-note-text text-muted\">" + H(Strings.JobDetailsPage_Notes_Acknowledged) + "</div>", html);
        Assert.DoesNotContain("js-note-ack", html);
        Assert.DoesNotContain(">" + JobNotes.ParameterName + "<", html);
    }

    [Fact]
    public void Render_ShowsAnAcknowledgingNote_WithItsText()
    {
        List<JobNote> notes = [new() { Timestamp = JobHelper.ToTimestamp(_now), Text = "Ticket OPS-412", Acknowledges = true }];
        SetupJob(Details(FailedState.StateName, properties: new Dictionary<string, string>
        {
            { JobNotes.ParameterName, SerializationHelper.Serialize(notes, SerializationOption.User) },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<div class=\"hf-note-text\">Ticket OPS-412</div>", html);
        Assert.DoesNotContain("hf-note-text text-muted", html);
    }

    [Fact]
    public void Render_DrawsTheLineage_WhenTheJobAwaitsAParent()
    {
        SetupJob(Details(AwaitingState.StateName, new Dictionary<string, string> { { "ParentId", "0" }, { "Options", "1" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"job-details-lineage\"", html);
        Assert.Contains("hf-lineage-svg", html);
        Assert.DoesNotContain(H(Strings.JobDetailsPage_Lineage_Truncated), html);
    }

    [Fact]
    public void Render_SaysTheLineageWasTruncated_WhenTheJobHasTooManyContinuations()
    {
        string continuations = string.Join(",", Enumerable.Range(2, 45).Select(i => "{\"JobId\":\"" + i + "\",\"Options\":1}"));
        SetupJob(Details(SucceededState.StateName, SucceededData(), properties: new Dictionary<string, string> { { "Continuations", "[" + continuations + "]" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(H(Strings.JobDetailsPage_Lineage_Truncated), html);
    }

    [Fact]
    public void Render_DoesNotDrawTheLineage_ForAJobWithoutRelatives()
    {
        SetupJob(Details(EnqueuedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("id=\"job-details-lineage\"", html);
    }

    [Fact]
    public void Render_ListsOnlyTheParametersThatHaveNoDedicatedView()
    {
        SetupJob(Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
        {
            { "CurrentCulture", "\"en-US\"" },
            { "DBG_Hidden", "secret" },
            { JobConsole.ProgressParameter, "{}" },
            { FanInContinuationsSupportAttribute.ParameterName, "[]" },
            { "Continuations", "[]" },
        }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"job-details-parameters\"", html);
        Assert.Contains("<dt>CurrentCulture</dt>", html);
        Assert.Contains("<dd><code>&quot;en-US&quot;</code></dd>", html);
        Assert.DoesNotContain("DBG_Hidden", html);
        Assert.DoesNotContain(">" + JobConsole.ProgressParameter + "<", html);
        Assert.DoesNotContain(">" + FanInContinuationsSupportAttribute.ParameterName + "<", html);
        Assert.DoesNotContain("id=\"job-details-continuations\"", html);
        Assert.DoesNotContain("id=\"job-details-fanin\"", html);
    }

    [Fact]
    public void Render_ListsTheContinuations_IncludingExpiredOnes()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData(), properties: new Dictionary<string, string>
        {
            { "Continuations", "[{\"JobId\":\"2\",\"Options\":1},{\"JobId\":\"3\",\"Options\":0}]" },
        }));
        SetupJobData("2", AwaitingState.StateName);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"job-details-continuations\"", html);
        Assert.Contains("<td class=\"min-width\"><a href=\"/jobs/details/2\">#2</a></td>", html);
        Assert.Contains("<code>OnlyOnSucceededState</code>", html);
        Assert.Contains("<em>" + H(string.Format(Strings.JobDetailsPage_JobExpired, "3")) + "</em>", html);
    }

    [Fact]
    public void Render_ListsTheFanInContinuations_IncludingExpiredOnes()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData(), properties: new Dictionary<string, string>
        {
            { FanInContinuationsSupportAttribute.ParameterName, "[\"4\",\"5\"]" },
        }));
        SetupJobData("4", AwaitingState.StateName);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"job-details-fanin\"", html);
        Assert.Contains("<td class=\"min-width\"><a href=\"/jobs/details/4\">#4</a></td>", html);
        Assert.Contains("<td colspan=\"4\"><em>" + H(string.Format(Strings.JobDetailsPage_JobExpired, "5")) + "</em></td>", html);
    }

    [Fact]
    public void Render_DrawsTheTimeSpentInEachState()
    {
        JobDetailsDto details = Details(SucceededState.StateName, SucceededData(), createdAt: _now.AddSeconds(-3));
        details.History =
        [
            new() { StateName = SucceededState.StateName, CreatedAt = _now, Data = SucceededData() },
            new() { StateName = ProcessingState.StateName, CreatedAt = _now.AddSeconds(-2), Data = ProcessingData("srv-1") },
            new() { StateName = EnqueuedState.StateName, CreatedAt = _now.AddSeconds(-3), Data = new Dictionary<string, string> { { "Queue", "default" } } },
        ];
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<div class=\"hf-state-bar js-state-bar\" role=\"list\" aria-label=\"" + H(Strings.JobDetailsPage_StateTimeline) + "\">", html);
        // Each segment grows with its share of the time; a short one is marked tight, so its label hides, and the
        // state and its time stay in the tooltip and in the text for screen readers
        Assert.Contains("style=\"flex-grow: 33.33\"", html);
        Assert.Contains("style=\"flex-grow: 66.67\"", html);
        Assert.Contains("<em aria-hidden=\"true\">Processing</em><span class=\"visually-hidden\">Processing: " + H(Duration(TimeSpan.FromSeconds(2)).Trim()) + "</span></span>", html);
        Assert.Equal(4, CountOf(html, "<li class=\"ns-step"));
        Assert.Contains("<span role=\"listitem\" class=\"hf-state-seg ns-state-processing\"", html);
        Assert.DoesNotContain("width: 1.5%", html);
    }

    [Fact]
    public void Render_MarksAStateTooShortForItsLabel_AsTight_AndKeepsItsName()
    {
        JobDetailsDto details = Details(FailedState.StateName, createdAt: _now.AddSeconds(-100));
        details.History =
        [
            new() { StateName = FailedState.StateName, CreatedAt = _now, Data = new Dictionary<string, string>() },
            new() { StateName = ProcessingState.StateName, CreatedAt = _now.AddSeconds(-99), Data = ProcessingData("srv-1") },
            new() { StateName = EnqueuedState.StateName, CreatedAt = _now.AddSeconds(-100), Data = new Dictionary<string, string> { { "Queue", "default" } } },
        ];
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<span role=\"listitem\" class=\"hf-state-seg ns-state-enqueued hf-state-seg-tight\" style=\"flex-grow: 1\" data-state=\"Enqueued\" title=\"Enqueued: ", html);
        Assert.Contains("<span role=\"listitem\" class=\"hf-state-seg ns-state-processing\" style=\"flex-grow: 99\"", html);
        Assert.Contains("<em aria-hidden=\"true\">Enqueued</em><span class=\"visually-hidden\">Enqueued: ", html);
    }

    [Fact]
    public void Render_GivesTheCurrentStep_ItsColourGroup()
    {
        SetupJob(Details(SucceededState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<li class=\"ns-step ns-step-current ns-state-succeeded\">", html);
        Assert.Contains("<li class=\"ns-step\">", html);
    }

    [Fact]
    public void Render_GivesTheCurrentStep_ANeutralLook_ForAStateWithoutASuffix()
    {
        SetupJob(Details("Facts.Unstyled", new Dictionary<string, string> { { "Reason", "none" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<li class=\"ns-step ns-step-current\">", html);
        Assert.Contains("<h3 class=\"ns-step-title state-card-title\">", html);
        Assert.Contains("<div class=\"ns-step-data\">", html);
        Assert.DoesNotContain("state-card-state-", html);
        Assert.DoesNotContain("border-color: inherit", html);
    }

    [Fact]
    public void Render_DoesNotDrawTheStateTimeline_ForASingleFinishedState()
    {
        SetupJob(Details(SucceededState.StateName, SucceededData()));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("hf-state-bar", html);
    }

    [Fact]
    public void Render_HasAHeader_WithTheKindTheStateTheIdTheQueueAndTheRecurringJob()
    {
        SetupJob(Details(ScheduledState.StateName, new Dictionary<string, string> { { "EnqueueAt", JobHelper.SerializeDateTime(_now.AddMinutes(5)) } },
            properties: new Dictionary<string, string> { { "RecurringJobId", "\"nightly/report\"" } },
            job: Job.FromExpression(() => SampleJobs.Work(), "critical")));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<header class=\"ns-page-head ns-job-head\">", html);
        Assert.Contains("<span class=\"ns-overline\">" + H(Strings.Common_Job) + "</span><span class=\"ns-page-name\">", html);
        Assert.Contains("<span class=\"ns-job-state\"><span class=\"ns-state-chip ns-state-scheduled\" data-state=\"Scheduled\">", html);
        Assert.Contains("<span class=\"ns-job-id-fact\"><span class=\"ns-job-id\">#1</span><button type=\"button\" class=\"ns-icon-button ns-copy-button js-copy\" data-copy=\"1\"", html);
        Assert.Contains("<a class=\"ns-queue-chip\" href=\"/jobs/enqueued/critical\">", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"/recurring/details/nightly~2Freport\" title=\"" + H(Strings.RecurringJobDetailsPage_Kind) + "\">", html);
    }

    [Theory]
    [InlineData("Failed", "btn-primary")]
    [InlineData("Succeeded", "btn-primary")]
    [InlineData("Deleted", "btn-primary")]
    [InlineData("Scheduled", "ns-btn-tonal")]
    [InlineData("Processing", "ns-btn-tonal")]
    public void Render_SetsDeleteApart_AndMakesRequeueThePrimaryAction_OfAFinishedJob(string state, string requeueClass)
    {
        SetupJob(Details(state));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        int actions = html.IndexOf("<div class=\"ns-page-actions\">", StringComparison.Ordinal);
        int delete = html.IndexOf("<button class=\"btn btn-sm ns-btn-outlined-error\"\r\n                                data-ajax=\"/jobs/actions/delete/1\"", StringComparison.Ordinal);
        int divider = html.IndexOf("<span class=\"ns-actions-divider\" aria-hidden=\"true\"></span>", StringComparison.Ordinal);
        int requeue = html.IndexOf("<button class=\"btn btn-sm " + requeueClass + "\"\r\n                                    data-ajax=\"/jobs/actions/requeue/1\"", StringComparison.Ordinal);
        Assert.True(actions >= 0 && actions < delete && delete < divider && divider < requeue, $"{actions} {delete} {divider} {requeue}");
        Assert.Contains("data-confirm=\"" + H(Strings.JobDetailsPage_DeleteConfirm) + "\"", html);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Succeeded")]
    [InlineData("Processing")]
    public void Render_AsksBeforeRequeueing_AsBeforeDeleting(string state)
    {
        SetupJob(Details(state));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains(
            "data-ajax=\"/jobs/actions/requeue/1\"\r\n                                    data-loading-text=\"" + H(Strings.Common_Enqueueing) + "\"\r\n                                    "
            + "data-confirm=\"" + H(Strings.JobDetailsPage_RequeueConfirm) + "\" data-confirm-body=\"" + H(Strings.JobDetailsPage_RequeueConfirm_Body)
            + "\" data-confirm-label=\"" + H(Strings.JobDetailsPage_Requeue) + "\">",
            html);
        Assert.Equal(1, CountOf(html, "data-ajax=\"/jobs/actions/requeue/1\""));
    }

    [Fact]
    public void Render_ShowsTheExceptionOfAFailedJob_RightUnderTheHeader_WithItsStackTrace_AndRequeueOnlyInTheHeader()
    {
        Dictionary<string, string> data = new()
        {
            { "ExceptionType", "System.InvalidOperationException" },
            { "ExceptionMessage", "The <input> is empty" },
            { "ExceptionDetails", "System.InvalidOperationException: The input is empty\r\n   at Jobs.Run()" },
        };
        SetupJob(Details(FailedState.StateName, data));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        int header = html.IndexOf("</header>", StringComparison.Ordinal);
        int card = html.IndexOf("<section class=\"hf-card ns-exception-card\" aria-labelledby=\"job-details-exception\">", StringComparison.Ordinal);
        int summary = html.IndexOf("<div class=\"ns-summary ns-job-summary\">", StringComparison.Ordinal);
        Assert.True(header < card && card < summary, $"{header} {card} {summary}");
        Assert.Contains("<h2 id=\"job-details-exception\">" + DashboardMarkup.Icon("exclamation-circle-fill") + " " + H(Strings.JobDetailsPage_Exception) + "</h2>", html);
        Assert.Contains("<p class=\"ns-exception-head\"><code class=\"ns-exception-type\">System.InvalidOperationException</code></p>", html);
        Assert.Contains("<p class=\"ns-exception-message\">The &lt;input&gt; is empty</p>", html);
        Assert.Contains("<details class=\"ns-exception-trace js-exception-trace\" open>", html);
        Assert.Contains("<summary>" + H(Strings.JobDetailsPage_StackTrace) + "</summary>", html);
        // Requeue is the header's primary action, and only there
        int requeue = html.IndexOf("data-ajax=\"/jobs/actions/requeue/1\"", StringComparison.Ordinal);
        Assert.True(requeue >= 0 && requeue < card, $"{requeue} {card}");
        Assert.Equal(1, CountOf(html, "data-ajax=\"/jobs/actions/requeue/1\""));
        Assert.Contains("<h2 id=\"job-details-exception\">" + DashboardMarkup.Icon("exclamation-circle-fill") + " " + H(Strings.JobDetailsPage_Exception) + "</h2>\r\n                    </div>", html);
        // The history keeps the failed state's data, folded, as the card above shows it
        Assert.Contains("<details class=\"ns-step-more\">", html);
        Assert.Contains("<summary>" + H(Strings.JobDetailsPage_StateDetails) + "</summary>", html);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Processing")]
    [InlineData("Deleted")]
    public void Render_ShowsNoExceptionCard_ForAJobThatDidNotFail(string state)
    {
        SetupJob(Details(state));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("ns-exception-card", html);
        Assert.DoesNotContain("ns-step-more", html);
    }

    [Fact]
    public void Render_SummarizesTheLastRun_InTiles()
    {
        JobDetailsDto details = Details(SucceededState.StateName, createdAt: _now.AddSeconds(-40), properties: new Dictionary<string, string> { { "RetryCount", "2" } });
        details.History =
        [
            new() { StateName = SucceededState.StateName, CreatedAt = _now, Data = new Dictionary<string, string> { { "PerformanceDuration", "1500" }, { "Latency", "10" } } },
            new() { StateName = ProcessingState.StateName, CreatedAt = _now.AddSeconds(-30), Data = ProcessingData("web-1:42") },
            new() { StateName = EnqueuedState.StateName, CreatedAt = _now.AddSeconds(-40), Data = new Dictionary<string, string> { { "Queue", "default" } } },
        ];
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<h2 id=\"summary-created\" class=\"ns-summary-label\">", html);
        Assert.Contains("<h2 id=\"summary-started\" class=\"ns-summary-label\">", html);
        Assert.Contains("<h2 id=\"summary-duration\" class=\"ns-summary-label\">" + DashboardMarkup.Icon("hourglass-split") + " " + H(Strings.SucceededJobsPage_Table_Duration) + "</h2>\r\n                        <p class=\"ns-summary-value\">" + Duration(TimeSpan.FromMilliseconds(1500)) + "</p>", html);
        Assert.Contains("<h2 id=\"summary-retries\" class=\"ns-summary-label\">" + DashboardMarkup.Icon("arrow-repeat") + " " + H(Strings.Metrics_Retries) + "</h2>\r\n                        <p class=\"ns-summary-value\">2</p>", html);
        Assert.Contains("<h2 id=\"summary-server\" class=\"ns-summary-label\">", html);
        Assert.Contains("web-1:42", html);
    }

    [Fact]
    public void Render_ShowsOnlyTheCreationTile_ForAJobThatNeverRan()
    {
        SetupJob(Details(EnqueuedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("id=\"summary-created\"", html);
        Assert.DoesNotContain("id=\"summary-started\"", html);
        Assert.DoesNotContain("id=\"summary-duration\"", html);
        Assert.DoesNotContain("id=\"summary-retries\"", html);
        Assert.DoesNotContain("id=\"summary-server\"", html);
    }

    [Fact]
    public void Summarize_MeasuresTheRunFromItsStart_AndARunningOneUntilNow()
    {
        JobDetailsDto failed = Details(FailedState.StateName, createdAt: _now.AddSeconds(-10));
        failed.History =
        [
            new() { StateName = FailedState.StateName, CreatedAt = _now, Data = DefaultData(FailedState.StateName) },
            new() { StateName = ProcessingState.StateName, CreatedAt = _now.AddSeconds(-5), Data = new Dictionary<string, string> { { "ServerName", "old-server" } } },
        ];
        JobDetailsDto running = Details(ProcessingState.StateName);

        JobDetailsSummary failedSummary = JobDetailsPage.Summarize(failed, _now);
        JobDetailsSummary runningSummary = JobDetailsPage.Summarize(running, _now);

        Assert.Equal(TimeSpan.FromSeconds(5), failedSummary.Duration);
        Assert.False(failedSummary.Running);
        Assert.Equal("old-server", failedSummary.ServerId);
        Assert.Equal(TimeSpan.FromSeconds(30), runningSummary.Duration);
        Assert.True(runningSummary.Running);
        Assert.Equal("srv-1", runningSummary.ServerId);
    }

    [Fact]
    public void Render_LinksToTheSectionsShown_OnThisPage()
    {
        SetupJob(Details(SucceededState.StateName, properties: new Dictionary<string, string> { { "CurrentCulture", "\"en-US\"" } }));
        SetupConsole(new JobConsoleEntry { Timestamp = 1000, Level = JobConsoleLevel.Info, Message = "Hello" });

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<nav class=\"ns-jump-nav\" aria-label=\"" + H(Strings.JobDetailsPage_OnThisPage) + "\">", html);
        foreach (string section in new[] { "job-details-code", "job-details-live", "job-details-history", "job-details-parameters", "job-details-notes" })
        {
            Assert.Contains("href=\"#" + section + "\"", html);
            Assert.Contains("<h2 id=\"" + section + "\"", html);
        }

        Assert.DoesNotContain("href=\"#job-details-lineage\"", html);
    }

    [Fact]
    public void Render_LeavesOutTheLinks_ToSectionsThatAreNotShown()
    {
        SetupJob(Details(SucceededState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("href=\"#job-details-live\"", html);
        Assert.DoesNotContain("href=\"#job-details-parameters\"", html);
        Assert.Contains("href=\"#job-details-history\"", html);
    }

    [Fact]
    public void Render_ShowsTheHistory_AsAStepper_WithTheTimeAndTheDurationOfEachStep()
    {
        JobDetailsDto details = Details(SucceededState.StateName, SucceededData(), createdAt: _now.AddSeconds(-3));
        details.History =
        [
            new() { StateName = SucceededState.StateName, CreatedAt = _now, Data = SucceededData(), Reason = "Done <fast>" },
            new() { StateName = ProcessingState.StateName, CreatedAt = _now.AddSeconds(-2), Data = ProcessingData("srv-1") },
        ];
        SetupJob(details);

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<ol class=\"ns-stepper\">", html);
        Assert.Contains("<li class=\"ns-step ns-step-current ns-state-succeeded\">", html);
        Assert.Contains("<li class=\"ns-step ns-state-processing\">", html);
        Assert.Contains("<span class=\"ns-step-marker\" aria-hidden=\"true\">" + DashboardMarkup.Icon("check-circle-fill") + "</span>", html);
        Assert.Contains("<span class=\"ns-step-exact\"><span data-moment-local=", html);
        Assert.Contains("<span class=\"ns-step-duration\">" + SignedDuration(TimeSpan.FromSeconds(2)) + "</span>", html);
        Assert.Contains("<p class=\"ns-step-reason\">Done &lt;fast&gt;</p>", html);
        Assert.True(html.IndexOf("ns-state-succeeded\">", StringComparison.Ordinal) < html.IndexOf("<li class=\"ns-step ns-state-processing\">", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_UsesACustomStateRenderer_InTheHistory()
    {
        JobHistoryRenderer.Register("Facts.Custom", static (html, data) => new NonEscapedString("<p class=\"facts-custom\">" + html.HtmlEncode(data["Note"]) + "</p>"));
        JobHistoryRenderer.AddStateCssSuffix("Facts.Custom", "success");
        SetupJob(Details("Facts.Custom", new Dictionary<string, string> { { "Note", "kept <safe>" } }));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.Contains("<div class=\"ns-step-data\"><p class=\"facts-custom\">kept &lt;safe&gt;</p></div>", html);
        Assert.Contains("<li class=\"ns-step ns-step-current ns-state-succeeded\">", html);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Succeeded")]
    [InlineData("Processing")]
    [InlineData("Awaiting")]
    [InlineData("Deleted")]
    public void Render_KeepsTheHeadingsInOrder_AndNamesEveryHeaderCell(string state)
    {
        SetupJob(Details(state, properties: new Dictionary<string, string> { { "CurrentCulture", "\"en-US\"" } }));
        SetupConsole(new JobConsoleEntry { Timestamp = 2000, Checkpoint = "download" });

        string html = _renderer.Render(new JobDetailsPage(JobId));

        PageStructure.AssertHeadingsInOrder(html);
        PageStructure.AssertNoEmptyHeaderCells(html);
        Assert.DoesNotContain("<h4", html[..html.IndexOf("id=\"job-details-history\"", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Render_KeepsTheHeadingsInOrder_ForAJobThatExpired()
    {
        string html = _renderer.Render(new JobDetailsPage(JobId));

        PageStructure.AssertHeadingsInOrder(html);
    }

    [Fact]
    public void Render_ShowsNoCommandInTheHeaderOrTheExceptionCard_WhenTheDashboardIsReadOnly()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
        SetupJob(Details(FailedState.StateName));

        string html = _renderer.Render(new JobDetailsPage(JobId));

        Assert.DoesNotContain("ns-page-actions", html);
        Assert.Contains("ns-exception-card", html);
        Assert.DoesNotContain("ns-btn-tonal", html);
        Assert.Contains("js-copy", html);
    }

    private void SetupJob(JobDetailsDto details)
    {
        _renderer.Monitoring.Setup(x => x.JobDetails(JobId)).Returns(details);
    }

    private void SetupJobData(string jobId, string state)
    {
        _renderer.Connection.Setup(x => x.GetJobData(jobId)).Returns(new JobData
        {
            State = state,
            Job = Job.FromExpression(() => SampleJobs.Work()),
            CreatedAt = _now,
        });
    }

    private void SetupConsole(params JobConsoleEntry[] entries)
    {
        _renderer.Connection.Setup(x => x.GetAllItemsFromList(JobConsole.GetConsoleKey(JobId)))
            .Returns([.. entries.Select(x => SerializationHelper.Serialize(x, SerializationOption.Internal))]);
    }

    private JobDetailsDto Details(
        string state,
        Dictionary<string, string> data = null,
        Dictionary<string, string> properties = null,
        DateTime? createdAt = null,
        Job job = null)
    {
        return new JobDetailsDto
        {
            Job = job ?? Job.FromExpression(() => SampleJobs.Work()),
            CreatedAt = createdAt ?? _now,
            Properties = properties ?? [],
            History =
            [
                new() { StateName = state, CreatedAt = createdAt ?? _now, Data = data ?? DefaultData(state) },
            ],
        };
    }

    private Dictionary<string, string> DefaultData(string state)
    {
        return state switch
        {
            "Enqueued" => new Dictionary<string, string> { { "Queue", "default" } },
            "Failed" => new Dictionary<string, string> { { "ExceptionType", "System.InvalidOperationException" }, { "ExceptionMessage", "Boom" } },
            "Processing" => ProcessingData("srv-1"),
            "Succeeded" => SucceededData(),
            _ => [],
        };
    }

    private Dictionary<string, string> ProcessingData(string serverId)
    {
        return new Dictionary<string, string>
        {
            { "ServerId", serverId },
            { "WorkerId", "worker-1" },
            { "StartedAt", JobHelper.SerializeDateTime(_now.AddSeconds(-30)) },
        };
    }

    private static Dictionary<string, string> SucceededData()
    {
        return new Dictionary<string, string> { { "Latency", "10" }, { "PerformanceDuration", "20" } };
    }

    private static Dictionary<string, string> Deadline(DateTime deadlineAt)
    {
        return new Dictionary<string, string>
        {
            { DeadlineAttribute.DeadlineAtParameter, SerializationHelper.Serialize((DateTime?)deadlineAt, SerializationOption.User) },
        };
    }

    private static ServerDto Server(string name, DateTime heartbeat)
    {
        return new ServerDto { Name = name, Heartbeat = heartbeat, StartedAt = heartbeat.AddHours(-1), Queues = ["default"], WorkersCount = 5 };
    }

    private static string Attribute(string html, string name)
    {
        int start = html.IndexOf(name + "=\"", StringComparison.Ordinal) + name.Length + 2;
        return WebUtility.HtmlDecode(html[start..html.IndexOf('"', start)]);
    }

    private static string Duration(TimeSpan duration)
    {
        JobDetailsPage page = new(JobId);
        page.Assign(new DashboardContextStub(new DashboardOptions()));
        return new HtmlHelper(page).ToHumanDuration(duration, false);
    }

    private static string SignedDuration(TimeSpan duration)
    {
        JobDetailsPage page = new(JobId);
        page.Assign(new DashboardContextStub(new DashboardOptions()));
        return new HtmlHelper(page).ToHumanDuration(duration);
    }

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

        public static void Report(string name, DayOfWeek day, [Redacted] string secret, CancellationToken token)
        {
        }
    }
}
