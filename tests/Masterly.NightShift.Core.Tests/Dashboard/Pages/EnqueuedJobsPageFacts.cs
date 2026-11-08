using System;
using System.Collections.Generic;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class EnqueuedJobsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutEnqueuedJobs_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new EnqueuedJobsPage("critical"));

            Assert.Contains(Encode(Strings.EnqueuedJobsPage_NoJobs), html);
            Assert.DoesNotContain("/jobs/enqueued/delete-all/", html);
        }

        [Fact]
        public void Render_WithEnqueuedJobs_OffersToDeleteThemAll()
        {
            SetupJobs("my queue");

            var html = _renderer.Render(new EnqueuedJobsPage("my queue"));

            Assert.Contains("data-ajax=\"/jobs/enqueued/delete-all/my%20queue\"", html);
            Assert.Contains($"data-confirm=\"{Encode(Strings.Common_DeleteAllConfirm)}\"", html);
            Assert.Contains("data-url=\"/jobs/enqueued/delete\"", html);
            Assert.Contains("value=\"1\"", html);
        }

        [Fact]
        public void Render_WithEnqueuedJobs_OffersTheQueueAsCsv()
        {
            SetupJobs("my queue");

            var html = _renderer.Render(new EnqueuedJobsPage("my queue"));

            Assert.Contains("<button type=\"button\" class=\"btn btn-sm btn-default js-download\" data-url=\"/jobs/enqueued/export/my%20queue\" data-filename=\"nightshift-enqueued-jobs-my queue.csv\">", html);
            Assert.Contains(Encode(Strings.Common_DownloadCsv), html);
        }

        [Fact]
        public void Render_WithoutEnqueuedJobs_HasNoCsvButton()
        {
            var html = _renderer.Render(new EnqueuedJobsPage("critical"));

            Assert.DoesNotContain("/jobs/enqueued/export/", html);
        }

        [Fact]
        public void Render_WhenReadOnly_HidesTheCommands()
        {
            SetupJobs("critical");
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new EnqueuedJobsPage("critical"));

            Assert.Contains("<a href=\"/jobs/details/1\">#1</a>", html);
            Assert.DoesNotContain("/jobs/enqueued/delete-all/", html);
            Assert.DoesNotContain("/jobs/enqueued/delete\"", html);
            Assert.Contains("data-url=\"/jobs/enqueued/export/critical\"", html);
        }

        private void SetupJobs(string queue)
        {
            _renderer.Monitoring.Setup(x => x.EnqueuedCount(queue)).Returns(1);
            _renderer.Monitoring.Setup(x => x.EnqueuedJobs(queue, It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<EnqueuedJobDto>(new Dictionary<string, EnqueuedJobDto>
                {
                    { "1", new EnqueuedJobDto { Job = Job.FromExpression(() => EnqueuedPageJobs.Work()), State = "Enqueued", InEnqueuedState = true, EnqueuedAt = DateTime.UtcNow } }
                }));
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class EnqueuedPageJobs
    {
        public static void Work()
        {
        }
    }
}
