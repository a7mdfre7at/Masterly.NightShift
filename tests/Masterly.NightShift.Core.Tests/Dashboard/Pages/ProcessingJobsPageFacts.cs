using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class ProcessingJobsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly DateTime _now = DateTime.UtcNow;

        [Fact]
        public void Render_WithoutProcessingJobs_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains(Encode(Strings.ProcessingJobsPage_NoJobs), html);
            Assert.DoesNotContain("js-jobs-list", html);
        }

        [Fact]
        public void Render_WithProcessingJobs_ShowsTheToolbarWithDownloadAndCommands()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddSeconds(-5)) } });

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains("data-url=\"/jobs/processing/export\" data-filename=\"nightshift-processing-jobs.csv\"", html);
            Assert.Contains("data-ajax=\"/jobs/processing/delete-all\"", html);
            Assert.Contains("data-url=\"/jobs/processing/requeue\"", html);
            Assert.Contains("data-url=\"/jobs/processing/delete\"", html);
            Assert.Contains("class=\"js-jobs-list-select-all\"", html);
            Assert.Contains("<input type=\"checkbox\" class=\"js-jobs-list-checkbox\" name=\"jobs[]\" value=\"1\"/>", html);
            Assert.Contains($"<th class=\"min-width\">{Encode(Strings.ProcessingJobsPage_Table_Progress)}</th>", html);
        }

        [Fact]
        public void Render_WhenReadOnly_KeepsTheDownloadButHidesTheCommands()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddSeconds(-5)) } });
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains("data-url=\"/jobs/processing/export\"", html);
            Assert.DoesNotContain("/jobs/processing/delete-all", html);
            Assert.DoesNotContain("/jobs/processing/requeue", html);
            Assert.DoesNotContain("js-jobs-list-checkbox", html);
            Assert.DoesNotContain("js-jobs-list-select-all", html);
        }

        [Fact]
        public void Render_WithReportedProgress_ShowsThePercentAndMessage()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddSeconds(-5)) } });
            _renderer.Connection.Setup(x => x.GetJobParameter("1", JobConsole.ProgressParameter))
                .Returns(SerializationHelper.Serialize(new JobProgress { Percent = 40, Message = "Copying <files>" }, SerializationOption.Internal));

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains("title=\"Copying &lt;files&gt;\"", html);
            Assert.Contains("style=\"width: 40%\"", html);
            Assert.Contains("40% Copying &lt;files&gt;</small>", html);
            Assert.Contains("class=\"min-width job-live-cell js-live-row\" data-job-id=\"1\"", html);
        }

        [Fact]
        public void Render_WhenTheServerIsGone_FlagsTheJobAsPossiblyAborted()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddSeconds(-5), "gone:1") } });

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains($"<span title=\"{Encode(Strings.ProcessingJobsPage_Aborted)}\" class=\"glyphicon glyphicon-warning-sign", html);
        }

        [Fact]
        public void Render_WhenTheServerIsAlive_DoesNotFlagTheJob()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddSeconds(-5)) } });

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.DoesNotContain(Encode(Strings.ProcessingJobsPage_Aborted), html);
        }

        [Fact]
        public void Render_WhenTheDeadlinePassed_MarksTheJobOverdue()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddMinutes(-5)) } });
            SetupDeadline("1", _now.AddMinutes(-1));

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains(" overdue\">", html);
            Assert.Contains($"<span class=\"label label-danger text-uppercase\" title=\"{Encode(Strings.Common_Overdue_Tooltip)}\">{Encode(Strings.Common_Overdue)}</span>", html);
        }

        [Fact]
        public void Render_WhenTheDeadlineIsAhead_ShowsTheDeadline()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddMinutes(-5)) } });
            SetupDeadline("1", _now.AddHours(1));

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains($"<span class=\"label label-default\" title=\"{Encode(Strings.Common_Deadline)}\">{Encode(Strings.Common_Deadline)} <span data-moment=", html);
            Assert.DoesNotContain(Encode(Strings.Common_Overdue_Tooltip), html);
        }

        [Fact]
        public void Render_WithRecordedDurations_EstimatesTheRemainingTime()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(QuickJob(), _now.AddSeconds(-10)) } });
            SetupInsight(QuickJob(), "60000", "60000", "60000");

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains("job-live-progress-eta", html);
            Assert.DoesNotContain("job-live-progress-late", html);
            Assert.Contains($"title=\"{Encode(Strings.ProcessingJobsPage_Eta_Tooltip)}\"", html);
            Assert.Contains("data-eta=\"" + Encode(EtaPrefix()), html);
            Assert.DoesNotContain(Encode(Strings.ProcessingJobsPage_LongerThanUsual), html);
        }

        [Fact]
        public void Render_WhenAJobRunsLongerThanUsual_SaysSo()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto> { { "1", Processing(SlowJob(), _now.AddHours(-1)) } });
            SetupInsight(SlowJob(), "1000", "1000", "1000");

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains("job-live-progress-eta job-live-progress-late", html);
            Assert.Contains($"data-eta=\"{Encode(Strings.ProcessingJobsPage_LongerThanUsual)}\"", html);
        }

        [Fact]
        public void Render_WithJobsOfTheSameMethod_ReadsTheirInsightsOnce()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto>
            {
                { "1", Processing(QuickJob(), _now.AddSeconds(-10)) },
                { "2", Processing(QuickJob(), _now.AddSeconds(-20)) }
            });
            var key = SetupInsight(QuickJob(), "60000", "60000", "60000");

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains("data-job-id=\"1\"", html);
            Assert.Contains("data-job-id=\"2\"", html);
            _renderer.Connection.Verify(x => x.GetAllEntriesFromHash($"insights:{key}"), Times.Once);
        }

        [Fact]
        public void Render_WithExpiredOrMovedJobs_ShowsThemAsObsolete()
        {
            SetupJobs(new Dictionary<string, ProcessingJobDto>
            {
                { "1", null },
                { "2", new ProcessingJobDto { Job = QuickJob(), InProcessingState = false } }
            });

            var html = _renderer.Render(new ProcessingJobsPage());

            Assert.Contains($"<td colspan=\"4\"><em>{Encode(Strings.Common_JobExpired)}</em></td>", html);
            Assert.Contains($"<td colspan=\"4\">{Encode(Strings.Common_JobStateChanged_Text)}</td>", html);
            Assert.Contains("class=\"js-jobs-list-row obsolete-data  \"", html);
            Assert.DoesNotContain("js-jobs-list-checkbox", html);
            _renderer.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        private void SetupJobs(Dictionary<string, ProcessingJobDto> jobs)
        {
            _renderer.Monitoring.Setup(x => x.ProcessingCount()).Returns(jobs.Count);
            _renderer.Monitoring.Setup(x => x.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<ProcessingJobDto>(jobs));
            _renderer.Monitoring.Setup(x => x.Servers())
                .Returns(new List<ServerDto> { new ServerDto { Name = "server-1:abc", Heartbeat = _now } });
        }

        private void SetupDeadline(string jobId, DateTime deadline)
        {
            _renderer.Connection.Setup(x => x.GetJobParameter(jobId, DeadlineAttribute.DeadlineAtParameter))
                .Returns(SerializationHelper.Serialize<DateTime?>(deadline, SerializationOption.User));
        }

        private string SetupInsight(Job job, params string[] durations)
        {
            var key = JobInsightsAttribute.GetKey(job);

            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"insights:{key}"))
                .Returns(new Dictionary<string, string> { { "LastDurationMs", durations[0] } });
            _renderer.Connection.Setup(x => x.GetCounter($"insights:{key}:ok")).Returns(durations.Length);
            _renderer.Connection.Setup(x => x.GetAllItemsFromList($"insights:{key}:durations")).Returns(durations.ToList());

            return key;
        }

        private static ProcessingJobDto Processing(Job job, DateTime startedAt, string serverId = "server-1:abc")
        {
            return new ProcessingJobDto { Job = job, InProcessingState = true, ServerId = serverId, StartedAt = startedAt };
        }

        private static Job QuickJob() => Job.FromExpression(() => ProcessingPageJobs.Quick());

        private static Job SlowJob() => Job.FromExpression(() => ProcessingPageJobs.Slow());

        private static string EtaPrefix()
        {
            return Strings.ProcessingJobsPage_Eta.Substring(0, Strings.ProcessingJobsPage_Eta.IndexOf("{0}", StringComparison.Ordinal));
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class ProcessingPageJobs
    {
        public static void Quick()
        {
        }

        public static void Slow()
        {
        }
    }
}
