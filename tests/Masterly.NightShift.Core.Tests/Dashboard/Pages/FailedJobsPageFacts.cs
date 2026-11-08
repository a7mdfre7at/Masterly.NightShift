using System;
using System.Collections.Generic;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class FailedJobsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly DateTime _now = DateTime.UtcNow;

        [Fact]
        public void Render_WithoutFailedJobs_ShowsTheSuccessMessage()
        {
            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains(Encode(Strings.FailedJobsPage_NoJobs), html);
            Assert.DoesNotContain("js-jobs-list", html);
        }

        [Fact]
        public void Render_WithFailedJobs_ShowsTheToolbarWithDownloadAndCommands()
        {
            SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains("data-url=\"/jobs/failed/export\" data-filename=\"nightshift-failed-jobs.csv\"", html);
            Assert.Contains("data-ajax=\"/jobs/failed/delete-all\"", html);
            Assert.Contains("data-ajax=\"/jobs/failed/requeue-all\"", html);
            Assert.Contains("data-confirm=\"" + Encode(Strings.FailedJobsPage_RequeueAllConfirm) + "\"", html);
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

            var html = _renderer.Render(new FailedJobsPage());

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

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains("data-url=\"/jobs/failed/export\"", html);
            Assert.Contains("class=\"hf-card hf-clusters\"", html);
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

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains(Encode(String.Format(Strings.FailedJobsPage_Clusters_Hint, 500)), html);
            Assert.Contains("data-cluster=\"InvalidOperationException|FailedPageJobs.Work\"", html);
            Assert.DoesNotContain("data-cluster=\"TimeoutException", html);
            Assert.Contains($"<div class=\"hf-cluster-count\" title=\"{Encode(String.Format(Strings.FailedJobsPage_Clusters_Jobs, 2))}\">2</div>", html);
            Assert.Contains("<span class=\"hf-cluster-exception\">InvalidOperationException</span> <code class=\"hf-cluster-method\">FailedPageJobs.Work</code>", html);
            Assert.Contains("<div class=\"hf-cluster-message text-muted\">Connection &lt;lost&gt;</div>", html);
            Assert.Contains($"<span>{Encode(Strings.FailedJobsPage_Clusters_FirstSeen)} <span data-moment=", html);
            Assert.Contains($"<span>&middot; {Encode(Strings.FailedJobsPage_Clusters_LastSeen)} <span data-moment=", html);
            Assert.Contains("data-jobs=\"[&quot;1&quot;,&quot;2&quot;]\"", html);
            Assert.Contains(Encode(String.Format(Strings.FailedJobsPage_Clusters_Requeue, 2)), html);
            Assert.Contains(Encode(String.Format(Strings.FailedJobsPage_Clusters_Delete, 2)), html);
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

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains("<span class=\"hf-cluster-exception\">InvalidOperationException</span>", html);
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

            var html = _renderer.Render(new FailedJobsPage());

            Assert.DoesNotContain("hf-clusters", html);
        }

        [Fact]
        public void Render_WithAnAcknowledgedFailure_ShowsWhoAcknowledgedIt()
        {
            SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
            SetupNotes("1", new JobNote { User = "bob", Text = "ticket OPS-1", Acknowledges = true });

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains($"<span class=\"label label-info hf-ack\" title=\"bob: ticket OPS-1\"><span class=\"glyphicon glyphicon-ok\"></span> {Encode(Strings.FailedJobsPage_Acknowledged)}</span>", html);
        }

        [Fact]
        public void Render_WithAnAnonymousAcknowledgementWithoutText_ShowsAnonymous()
        {
            SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
            SetupNotes("1", new JobNote { Text = "", Acknowledges = true });

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains($"<span class=\"label label-info hf-ack\" title=\"{Encode(Strings.Common_Anonymous)}\">", html);
        }

        [Fact]
        public void Render_WithNotesThatDontAcknowledge_ShowsNoAcknowledgement()
        {
            SetupJobs(new Dictionary<string, FailedJobDto> { { "1", Failed("InvalidOperationException", "Boom") } });
            SetupNotes("1", new JobNote { User = "bob", Text = "looking into it" });

            var html = _renderer.Render(new FailedJobsPage());

            Assert.DoesNotContain("hf-ack", html);
        }

        [Fact]
        public void Render_ReadsTheNotes_OfTheFirstHundredRowsOnly()
        {
            var jobs = new Dictionary<string, FailedJobDto>();
            for (var i = 1; i <= 150; i++) jobs.Add(i.ToString(), Failed("InvalidOperationException", "Boom"));
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

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains("from=0&amp;count=1000\">" + Encode(1000.ToString("N0")) + "</a>", html);
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

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains(Encode(Strings.Common_JobExpired), html);
            Assert.DoesNotContain("hf-ack", html);
            _renderer.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), JobNotes.ParameterName), Times.Never);
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
}
