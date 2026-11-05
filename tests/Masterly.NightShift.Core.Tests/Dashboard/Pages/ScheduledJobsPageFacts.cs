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
    public class ScheduledJobsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutScheduledJobs_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new ScheduledJobsPage());

            Assert.Contains(Encode(Strings.ScheduledJobsPage_NoJobs), html);
            Assert.DoesNotContain("/jobs/scheduled/export", html);
        }

        [Fact]
        public void Render_WithScheduledJobs_ShowsTheDownloadAndCommands()
        {
            SetupJobs();

            var html = _renderer.Render(new ScheduledJobsPage());

            Assert.Contains("data-url=\"/jobs/scheduled/export\" data-filename=\"nightshift-scheduled-jobs.csv\"", html);
            Assert.Contains("data-ajax=\"/jobs/scheduled/delete-all\"", html);
            Assert.Contains($"data-confirm=\"{Encode(Strings.Common_DeleteAllConfirm)}\"", html);
            Assert.Contains("data-url=\"/jobs/scheduled/enqueue\"", html);
            Assert.Contains("data-url=\"/jobs/scheduled/delete\"", html);
            Assert.Contains("value=\"1\"", html);
        }

        [Fact]
        public void Render_WhenReadOnly_KeepsTheDownloadButHidesTheCommands()
        {
            SetupJobs();
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new ScheduledJobsPage());

            Assert.Contains("data-url=\"/jobs/scheduled/export\"", html);
            Assert.DoesNotContain("/jobs/scheduled/delete-all", html);
            Assert.DoesNotContain("/jobs/scheduled/enqueue", html);
        }

        private void SetupJobs()
        {
            _renderer.Monitoring.Setup(x => x.ScheduledCount()).Returns(1);
            _renderer.Monitoring.Setup(x => x.ScheduledJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<ScheduledJobDto>(new Dictionary<string, ScheduledJobDto>
                {
                    { "1", new ScheduledJobDto { Job = Job.FromExpression(() => ScheduledPageJobs.Work()), InScheduledState = true, EnqueueAt = DateTime.UtcNow.AddHours(1) } }
                }));
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class ScheduledPageJobs
    {
        public static void Work()
        {
        }
    }
}
