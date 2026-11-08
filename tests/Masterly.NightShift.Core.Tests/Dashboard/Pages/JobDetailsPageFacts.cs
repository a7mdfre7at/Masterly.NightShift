using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class JobDetailsPageFacts
    {
        private const string JobId = "1";

        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly DateTime _now = DateTime.UtcNow;

        [Fact]
        public void Render_SaysTheJobExpired_WhenItCannotBeFound()
        {
            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<h1 class=\"page-header\">" + H(Strings.Common_Job) + "</h1>", html);
            Assert.Contains(H(String.Format(Strings.JobDetailsPage_JobExpired, JobId)), html);
            Assert.DoesNotContain("job-snippet", html);
            Assert.Contains("id=\"catalogRunner\"", html);
        }

        [Fact]
        public void Render_ShowsTheSnippet_WithEveryToolbarCommand()
        {
            SetupJob(Details(EnqueuedState.StateName, job: Job.FromExpression(() => SampleJobs.Work(), "critical")));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("SampleJobs.Work", html);
            Assert.Contains("js-watch-toggle\" data-id=\"1\"", html);
            Assert.Contains("data-state=\"Enqueued\"", html);
            Assert.Contains("data-url=\"/jobs/export/1\" data-filename=\"job-1.json\"", html);
            Assert.Contains("js-catalog-run", html);
            Assert.Contains("data-method=\"Work\"", html);
            Assert.Contains("js-copy-code\" data-code=\"", html);
            Assert.Contains("<span class=\"comment\">// " + H(Strings.QueuesPage_Table_Queue) + ": critical</span>", html);
            Assert.Contains("data-ajax=\"/jobs/actions/requeue/1\"", html);
            Assert.Contains("data-ajax=\"/jobs/actions/delete/1\"", html);
            Assert.Contains("id=\"catalogRunner\"", html);
        }

        [Fact]
        public void Render_OmitsTheQueueComment_WhenTheJobHasNoQueue()
        {
            SetupJob(Details(EnqueuedState.StateName));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.DoesNotContain("// " + H(Strings.QueuesPage_Table_Queue) + ":", html);
        }

        [Fact]
        public void Render_PrefillsTheRunAgainForm_WithTheJobArguments_ExceptInjectedAndRedactedOnes()
        {
            SetupJob(Details(EnqueuedState.StateName, job: Job.FromExpression(() => SampleJobs.Report("monthly", DayOfWeek.Monday, "s3cr3t", CancellationToken.None))));

            var html = _renderer.Render(new JobDetailsPage(JobId));
            var parameters = Attribute(html, "data-parameters");

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

            var html = _renderer.Render(new JobDetailsPage(JobId));

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
            var details = Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
            {
                { "DBG_Type", "Missing.Type, Missing" },
                { "DBG_Method", "Work" },
            });
            details.Job = null;
            SetupJob(details);

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<h1 class=\"page-header\">" + H(Strings.Common_Job) + "</h1>", html);
            Assert.Contains("id=\"job-details-missing-method\"", html);
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
            var details = Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
            {
                { "DBG_Type", "Missing.Type, Missing" },
                { "DBG_Method", "Connect" },
                { "DBG_Args", "[\"db\",\"hunter2\"]" },
                { "DBG_Payload", "{\"a\":\"hunter2\"}" },
            });
            details.Job = null;
            SetupJob(details);

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<pre><code>Missing.Type, Missing</code></pre>", html);
            Assert.Equal(shown, html.Contains("hunter2"));
            Assert.Equal(shown, html.Contains("<td class=\"width-15\">Args</td>"));
            Assert.Equal(shown, html.Contains("<td class=\"width-15\">Payload</td>"));
        }

        [Fact]
        public void Render_WarnsThatTheJobWasAborted_WhenItsServerIsGone()
        {
            SetupJob(Details(ProcessingState.StateName, ProcessingData("srv-gone")));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains(String.Format(Strings.JobDetailsPage_JobAbortedNotActive_Warning_Html, "srv-gone", "/servers"), html);
        }

        [Fact]
        public void Render_EncodesTheServerId_InTheAbortedJobWarning()
        {
            SetupJob(Details(ProcessingState.StateName, ProcessingData("<img src=x onerror=alert(1)>")));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains(String.Format(Strings.JobDetailsPage_JobAbortedNotActive_Warning_Html, "&lt;img src=x onerror=alert(1)&gt;", "/servers"), html);
            Assert.DoesNotContain("<img src=x", html);
        }

        [Fact]
        public void Render_ShowsTheNextStatesTypeName_WhenAContinuationsNextStateCanNotBeLoaded()
        {
            var details = Details(AwaitingState.StateName, new Dictionary<string, string>
            {
                { "ParentId", "7" },
                { "NextState", "{\"$type\":\"Contoso.Jobs.HoldState, Contoso.Jobs\"}" },
                { "Options", "OnlyOnSucceededState" },
            });
            SetupJob(details);

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<code>Contoso.Jobs.HoldState</code>", html);
        }

        [Fact]
        public void Render_WarnsThatTheJobMayBeAborted_WhenItsServerMissedItsHeartbeat()
        {
            _renderer.Monitoring.Setup(x => x.Servers()).Returns(new List<ServerDto> { Server("srv-stale", _now.AddMinutes(-10)) });
            SetupJob(Details(ProcessingState.StateName, ProcessingData("srv-stale")));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains(String.Format(Strings.JobDetailsPage_JobAbortedWithHeartbeat_Warning_Html, "srv-stale"), html);
        }

        [Fact]
        public void Render_ShowsTheLiveView_ForAProcessingJobOnALiveServer()
        {
            _renderer.Monitoring.Setup(x => x.Servers()).Returns(new List<ServerDto> { Server("srv-live", _now) });
            SetupJob(Details(ProcessingState.StateName, ProcessingData("srv-live")));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.DoesNotContain(String.Format(Strings.JobDetailsPage_JobAbortedWithHeartbeat_Warning_Html, "srv-live"), html);
            Assert.Contains("id=\"jobLive\"", html);
            Assert.Contains("data-live-url=\"/jobs/live\"", html);
            Assert.Contains("data-poll=\"true\"", html);
            Assert.Contains("data-started-at=\"" + JobHelper.ToTimestamp(_now.AddSeconds(-30)), html);
            Assert.Contains("<span class=\"label label-info job-live-indicator\">Processing</span>", html);
            Assert.Contains("progress-bar js-live-bar progress-bar-striped active", html);
            Assert.Contains("aria-valuenow=\"0\"", html);
            Assert.Contains("<span class=\"js-live-elapsed-label\" style=\"\">", html);
            Assert.Contains(H(Strings.JobDetailsPage_NoConsoleOutput), html);
        }

        [Fact]
        public void Render_HidesTheElapsedTime_WhenAProcessingJobHasNoStartTime()
        {
            SetupJob(Details(ProcessingState.StateName, new Dictionary<string, string> { { "ServerId", "srv-1" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("data-started-at=\"\"", html);
            Assert.Contains("style=\"display: none\">" + H(Strings.JobDetailsPage_Live_Elapsed), html);
        }

        [Fact]
        public void Render_DoesNotShowTheLiveView_WhenTheJobNeverReportedAnything()
        {
            SetupJob(Details(SucceededState.StateName, SucceededData()));

            var html = _renderer.Render(new JobDetailsPage(JobId));

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

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("data-poll=\"false\"", html);
            Assert.Contains("progress-bar js-live-bar progress-bar-success", html);
            Assert.Contains("aria-valuenow=\"100\"", html);
            Assert.DoesNotContain(H(Strings.JobDetailsPage_NoConsoleOutput), html);
            Assert.Contains("<div class=\"job-console-line job-console-info\"><span class=\"job-console-time\" data-live-time=\"1000\">00:00:01</span>Starting</div>", html);
            Assert.Contains("job-console-warning\"><span class=\"job-console-time\" data-live-time=\"5000\">00:00:05</span>Slow &lt;disk&gt;</div>", html);
            Assert.Contains("job-console-checkpoint\"><span class=\"job-console-time\" data-live-time=\"2000\">00:00:02</span><span class=\"glyphicon glyphicon-flag\"></span> download</div>", html);
            Assert.True(html.IndexOf(">Starting<", StringComparison.Ordinal) < html.IndexOf(">Slow &lt;", StringComparison.Ordinal));
            Assert.Contains("<li><span class=\"job-checkpoint-name\">download</span><span class=\"text-muted job-checkpoint-delta\">00:00:02</span></li>", html);
            Assert.Contains("<li><span class=\"job-checkpoint-name\">upload</span><span class=\"text-muted job-checkpoint-delta\">+" + Duration(TimeSpan.FromSeconds(5)) + "</span></li>", html);
        }

        [Theory]
        [InlineData("Failed", "progress-bar js-live-bar progress-bar-danger\"")]
        [InlineData("Enqueued", "progress-bar js-live-bar \"")]
        [InlineData("Awaiting", "progress-bar js-live-bar \"")]
        public void Render_ColoursTheProgressBar_ByTheStateOfTheJob(string state, string expected)
        {
            SetupJob(Details(state));
            SetupConsole(new JobConsoleEntry { Timestamp = 1000, Level = JobConsoleLevel.Info, Message = "Hello" });

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains(expected, html);
            Assert.Contains("aria-valuenow=\"0\"", html);
        }

        [Fact]
        public void Render_ShowsTheReportedProgress_EvenWithoutConsoleOutput()
        {
            SetupJob(Details(SucceededState.StateName, SucceededData()));
            _renderer.Connection.Setup(x => x.GetJobParameter(JobId, JobConsole.ProgressParameter))
                .Returns(SerializationHelper.Serialize(new JobProgress { Percent = 40, Message = "Halfway <there>", Timestamp = 1 }, SerializationOption.Internal));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("id=\"jobLive\"", html);
            Assert.Contains("aria-valuenow=\"40\" style=\"width: 40%; min-width: 2em;\">40%</div>", html);
            Assert.Contains("<p class=\"text-muted js-live-progress-message\">Halfway &lt;there&gt;</p>", html);
            Assert.Contains(H(Strings.JobDetailsPage_NoConsoleOutput), html);
        }

        [Fact]
        public void Render_WritesTheProgressWidth_WithADecimalPoint_InEveryCulture()
        {
            SetupJob(Details(SucceededState.StateName, SucceededData()));
            _renderer.Connection.Setup(x => x.GetJobParameter(JobId, JobConsole.ProgressParameter))
                .Returns(SerializationHelper.Serialize(new JobProgress { Percent = 42.5, Timestamp = 1 }, SerializationOption.Internal));
            var culture = Thread.CurrentThread.CurrentCulture;

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

            Assert.Contains("aria-valuenow=\"42.5\" style=\"width: 42.5%; min-width: 2em;\">42,5%</div>", html);
        }

        [Fact]
        public void Render_ShowsANoteWithoutText_AsAnEmptyNote()
        {
            // Notes written by other tools may have no text at all
            SetupJob(Details(FailedState.StateName, properties: new Dictionary<string, string>
            {
                { JobNotes.ParameterName, "[{\"t\":1,\"u\":\"bob\",\"m\":null}]" },
            }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

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

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains(H(String.Format(Strings.JobDetailsPage_DeadlineMissed, Duration(TimeSpan.FromMinutes(5)))), html);
            Assert.DoesNotContain(H(Strings.Common_Overdue), html);
            Assert.DoesNotContain(">" + DeadlineAttribute.DeadlineMissedParameter + "<", html);
            Assert.DoesNotContain(">" + DeadlineAttribute.DeadlineAtParameter + "<", html);
        }

        [Fact]
        public void Render_ShowsAnUpcomingDeadline_AsInformation()
        {
            SetupJob(Details(EnqueuedState.StateName, properties: Deadline(_now.AddHours(1))));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"alert alert-info\">\r\n                        <span class=\"glyphicon glyphicon-time\"></span>", html);
            Assert.Contains(H(Strings.Common_Deadline) + ": <span data-moment=", html);
            Assert.DoesNotContain(H(Strings.Common_Overdue), html);
        }

        [Fact]
        public void Render_ShowsAPassedDeadline_AsOverdue()
        {
            SetupJob(Details(EnqueuedState.StateName, properties: Deadline(_now.AddHours(-1))));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"alert alert-danger\">\r\n                        <span class=\"glyphicon glyphicon-time\"></span>", html);
            Assert.Contains("<span class=\"label label-danger text-uppercase\">" + H(Strings.Common_Overdue) + "</span>", html);
        }

        [Fact]
        public void Render_IgnoresTheDeadline_OfASucceededJob()
        {
            SetupJob(Details(SucceededState.StateName, SucceededData(), properties: Deadline(_now.AddHours(-1))));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.DoesNotContain(H(Strings.Common_Deadline) + ": <span data-moment=", html);
        }

        [Fact]
        public void Render_IgnoresAnEmptyDeadline()
        {
            SetupJob(Details(EnqueuedState.StateName, properties: new Dictionary<string, string> { { DeadlineAttribute.DeadlineAtParameter, "null" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.DoesNotContain(H(Strings.Common_Deadline) + ": <span data-moment=", html);
        }

        [Fact]
        public void Render_ShowsTheJobExpiration_WhenItIsSet()
        {
            var details = Details(SucceededState.StateName, SucceededData());
            details.ExpireAt = _now.AddDays(1);
            SetupJob(details);

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains(String.Format(Strings.JobDetailsPage_JobFinished_Warning_Html, JobHelper.ToTimestamp(details.ExpireAt.Value), details.ExpireAt), html);
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

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"hf-tags\" aria-label=\"" + H(Strings.JobDetailsPage_Tags) + "\">", html);
            Assert.Contains("href=\"/batches/batch%201\"><span class=\"glyphicon glyphicon-th-large\"></span> " + H(Strings.JobDetailsPage_Batch) + " batch 1</a>", html);
            Assert.Contains("href=\"/batches/batch-0\"><span class=\"glyphicon glyphicon-th-large\"></span> " + H(Strings.JobDetailsPage_BatchContinuation) + " batch-0</a>", html);
            Assert.Contains("<span class=\"glyphicon glyphicon-tag text-muted\"></span>", html);
            Assert.Contains("<a class=\"hf-tag\" href=\"/tags/billing\">billing</a>", html);
            Assert.Contains("<a class=\"hf-tag\" href=\"/tags/eu%20west\">eu west</a>", html);
            Assert.DoesNotContain("id=\"job-details-parameters\"", html);
        }

        [Fact]
        public void Render_ShowsTheBatch_WithoutTheTagIcon_WhenTheJobHasNoTags()
        {
            SetupJob(Details(EnqueuedState.StateName, properties: new Dictionary<string, string>
            {
                { BatchJob.ParameterName, SerializationHelper.Serialize("batch-1", SerializationOption.User) },
            }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("hf-tag hf-tag-batch", html);
            Assert.DoesNotContain("glyphicon-tag text-muted", html);
        }

        [Fact]
        public void Render_ShowsNoTagBar_WhenTheJobHasNeitherTagsNorBatches()
        {
            SetupJob(Details(EnqueuedState.StateName));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.DoesNotContain("class=\"hf-tags\"", html);
        }

        [Fact, StaticLock]
        public void Render_ShowsADiagnosisProblem_WithALink_WhenNoServerCouldRunTheJob()
        {
            DrainMode.InvalidateCache();
            SetupJob(Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("id=\"job-details-diagnosis\"", html);
            Assert.Contains("<li class=\"hf-diagnosis-item hf-diagnosis-problem\" data-code=\"no-servers\">", html);
            Assert.Contains("glyphicon glyphicon-remove-sign", html);
            Assert.Contains("<a class=\"hf-diagnosis-link\" href=\"/servers\">" + H(Strings.NavigationMenu_Servers) + " &rarr;</a>", html);
        }

        [Fact, StaticLock]
        public void Render_ShowsADiagnosisWarning_WhenTheJobWaitsTooLong()
        {
            DrainMode.InvalidateCache();
            _renderer.Monitoring.Setup(x => x.Servers()).Returns(new List<ServerDto> { Server("srv-1", _now) });
            SetupJob(Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }, createdAt: _now.AddMinutes(-10)));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("hf-diagnosis-warning\" data-code=\"waiting\"", html);
            Assert.Contains("glyphicon glyphicon-warning-sign", html);
            Assert.Contains("href=\"/jobs/enqueued/default\"", html);
        }

        [Fact, StaticLock]
        public void Render_ShowsADiagnosisInfo_WithoutALink_WhenTheJobIsAboutToRun()
        {
            DrainMode.InvalidateCache();
            _renderer.Monitoring.Setup(x => x.Servers()).Returns(new List<ServerDto> { Server("srv-1", _now) });
            SetupJob(Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("hf-diagnosis-info\" data-code=\"healthy\"", html);
            Assert.Contains("glyphicon glyphicon-info-sign", html);
            Assert.DoesNotContain("hf-diagnosis-link", html);
        }

        [Fact]
        public void Render_ShowsNoDiagnosis_ForASucceededJob()
        {
            SetupJob(Details(SucceededState.StateName, SucceededData()));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.DoesNotContain("id=\"job-details-diagnosis\"", html);
        }

        [Fact]
        public void Render_ShowsAnEmptyNoteList_AndTheAcknowledgeButton_ForAFailedJob()
        {
            SetupJob(Details(FailedState.StateName));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<li class=\"hf-note hf-note-empty text-muted\">" + H(Strings.JobDetailsPage_Notes_Empty) + "</li>", html);
            Assert.Contains("data-url=\"/jobs/notes/add/1\"", html);
            Assert.Contains("js-note-ack", html);
            Assert.DoesNotContain("hf-ack", html);
        }

        [Fact]
        public void Render_OffersNoAcknowledgeButton_ForAJobThatDidNotFail()
        {
            SetupJob(Details(EnqueuedState.StateName));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("js-note-form", html);
            Assert.DoesNotContain("js-note-ack", html);
        }

        [Fact]
        public void Render_ListsTheNotes_AndShowsTheAcknowledgement()
        {
            var notes = new List<JobNote>
            {
                new JobNote { Timestamp = JobHelper.ToTimestamp(_now.AddMinutes(-5)), User = "alice", Text = "Partner API <down>" },
                new JobNote { Timestamp = JobHelper.ToTimestamp(_now.AddMinutes(-1)), Text = "", Acknowledges = true },
            };
            SetupJob(Details(FailedState.StateName, properties: new Dictionary<string, string>
            {
                { JobNotes.ParameterName, SerializationHelper.Serialize(notes, SerializationOption.User) },
            }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<span class=\"label label-info hf-ack\">", html);
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
            var notes = new List<JobNote> { new JobNote { Timestamp = JobHelper.ToTimestamp(_now), Text = "Ticket OPS-412", Acknowledges = true } };
            SetupJob(Details(FailedState.StateName, properties: new Dictionary<string, string>
            {
                { JobNotes.ParameterName, SerializationHelper.Serialize(notes, SerializationOption.User) },
            }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"hf-note-text\">Ticket OPS-412</div>", html);
            Assert.DoesNotContain("hf-note-text text-muted", html);
        }

        [Fact]
        public void Render_DrawsTheLineage_WhenTheJobAwaitsAParent()
        {
            SetupJob(Details(AwaitingState.StateName, new Dictionary<string, string> { { "ParentId", "0" }, { "Options", "1" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("id=\"job-details-lineage\"", html);
            Assert.Contains("hf-lineage-svg", html);
            Assert.DoesNotContain(H(Strings.JobDetailsPage_Lineage_Truncated), html);
        }

        [Fact]
        public void Render_SaysTheLineageWasTruncated_WhenTheJobHasTooManyContinuations()
        {
            var continuations = String.Join(",", Enumerable.Range(2, 45).Select(i => "{\"JobId\":\"" + i + "\",\"Options\":1}"));
            SetupJob(Details(SucceededState.StateName, SucceededData(), properties: new Dictionary<string, string> { { "Continuations", "[" + continuations + "]" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains(H(Strings.JobDetailsPage_Lineage_Truncated), html);
        }

        [Fact]
        public void Render_DoesNotDrawTheLineage_ForAJobWithoutRelatives()
        {
            SetupJob(Details(EnqueuedState.StateName));

            var html = _renderer.Render(new JobDetailsPage(JobId));

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

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("id=\"job-details-parameters\"", html);
            Assert.Contains("<td class=\"width-20 word-break\">CurrentCulture</td>", html);
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

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("id=\"job-details-continuations\"", html);
            Assert.Contains("<td class=\"min-width\"><a href=\"/jobs/details/2\">#2</a></td>", html);
            Assert.Contains("<code>OnlyOnSucceededState</code>", html);
            Assert.Contains("<em>" + H(String.Format(Strings.JobDetailsPage_JobExpired, "3")) + "</em>", html);
        }

        [Fact]
        public void Render_ListsTheFanInContinuations_IncludingExpiredOnes()
        {
            SetupJob(Details(SucceededState.StateName, SucceededData(), properties: new Dictionary<string, string>
            {
                { FanInContinuationsSupportAttribute.ParameterName, "[\"4\",\"5\"]" },
            }));
            SetupJobData("4", AwaitingState.StateName);

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("id=\"job-details-fanin\"", html);
            Assert.Contains("<td class=\"min-width\"><a href=\"/jobs/details/4\">#4</a></td>", html);
            Assert.Contains("<td colspan=\"4\"><em>" + H(String.Format(Strings.JobDetailsPage_JobExpired, "5")) + "</em></td>", html);
        }

        [Fact]
        public void Render_DrawsTheTimeSpentInEachState()
        {
            var details = Details(SucceededState.StateName, SucceededData(), createdAt: _now.AddSeconds(-3));
            details.History = new List<StateHistoryDto>
            {
                new StateHistoryDto { StateName = SucceededState.StateName, CreatedAt = _now, Data = SucceededData() },
                new StateHistoryDto { StateName = ProcessingState.StateName, CreatedAt = _now.AddSeconds(-2), Data = ProcessingData("srv-1") },
                new StateHistoryDto { StateName = EnqueuedState.StateName, CreatedAt = _now.AddSeconds(-3), Data = new Dictionary<string, string> { { "Queue", "default" } } },
            };
            SetupJob(details);

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"hf-state-bar\" title=\"" + H(Strings.JobDetailsPage_StateTimeline) + "\">", html);
            Assert.Contains("style=\"width: 1.5%; background: ", html);
            Assert.Contains("style=\"width: 33.33%; background: ", html);
            Assert.Contains("style=\"width: 66.67%; background: ", html);
            Assert.Contains("<em>Processing</em></span>", html);
            Assert.Equal(4, CountOf(html, "<div class=\"state-card "));
        }

        [Fact]
        public void Render_GivesTheCurrentStateCard_ItsCssSuffixClass()
        {
            SetupJob(Details(SucceededState.StateName));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"state-card state-card-state-success\" style=\"\">", html);
        }

#pragma warning disable CS0618 // The color methods are obsolete, but still supported
        [Fact, StaticLock]
        public void Render_GivesTheCurrentStateCard_TheColorsRegisteredForACustomState()
        {
            JobHistoryRenderer.AddForegroundStateColor("Facts.Quarantined", "#aa00aa");
            JobHistoryRenderer.AddBackgroundStateColor("Facts.Quarantined", "#fbeefb");
            SetupJob(Details("Facts.Quarantined", new Dictionary<string, string> { { "Reason", "suspicious" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"state-card \" style=\"border-color: #aa00aa\">", html);
            Assert.Contains("<h4 class=\"state-card-title\" style=\"color: #aa00aa\">", html);
            Assert.Contains("<div class=\"state-card-body\" style=\"background-color: #fbeefb\">", html);
            Assert.DoesNotContain("state-card-state-", html);
        }
#pragma warning restore CS0618

        [Fact]
        public void Render_GivesTheCurrentStateCard_ANeutralLook_ForAStateWithoutSuffixOrColors()
        {
            SetupJob(Details("Facts.Unstyled", new Dictionary<string, string> { { "Reason", "none" } }));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.Contains("<div class=\"state-card \" style=\"\">", html);
            Assert.Contains("<h4 class=\"state-card-title\" style=\"\">", html);
            Assert.Contains("<div class=\"state-card-body\" style=\"\">", html);
            Assert.DoesNotContain("state-card-state-", html);
            Assert.DoesNotContain("border-color: inherit", html);
        }

        [Fact]
        public void Render_DoesNotDrawTheStateTimeline_ForASingleFinishedState()
        {
            SetupJob(Details(SucceededState.StateName, SucceededData()));

            var html = _renderer.Render(new JobDetailsPage(JobId));

            Assert.DoesNotContain("hf-state-bar", html);
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
                .Returns(entries.Select(x => SerializationHelper.Serialize(x, SerializationOption.Internal)).ToList());
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
                Properties = properties ?? new Dictionary<string, string>(),
                History = new List<StateHistoryDto>
                {
                    new StateHistoryDto { StateName = state, CreatedAt = createdAt ?? _now, Data = data ?? DefaultData(state) },
                },
            };
        }

        private Dictionary<string, string> DefaultData(string state)
        {
            switch (state)
            {
                case "Enqueued":
                    return new Dictionary<string, string> { { "Queue", "default" } };
                case "Failed":
                    return new Dictionary<string, string> { { "ExceptionType", "System.InvalidOperationException" }, { "ExceptionMessage", "Boom" } };
                case "Processing":
                    return ProcessingData("srv-1");
                case "Succeeded":
                    return SucceededData();
                default:
                    return new Dictionary<string, string>();
            }
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
            return new ServerDto { Name = name, Heartbeat = heartbeat, StartedAt = heartbeat.AddHours(-1), Queues = new[] { "default" }, WorkersCount = 5 };
        }

        private static string Attribute(string html, string name)
        {
            var start = html.IndexOf(name + "=\"", StringComparison.Ordinal) + name.Length + 2;
            return WebUtility.HtmlDecode(html.Substring(start, html.IndexOf('"', start) - start));
        }

        private static string Duration(TimeSpan duration)
        {
            var page = new JobDetailsPage(JobId);
            page.Assign(new DashboardContextStub(new DashboardOptions()));
            return new HtmlHelper(page).ToHumanDuration(duration, false);
        }

        private static int CountOf(string html, string fragment)
        {
            return (html.Length - html.Replace(fragment, String.Empty).Length) / fragment.Length;
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
}
