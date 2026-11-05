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
    public class SucceededJobsFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutSucceededJobs_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new SucceededJobs());

            Assert.Contains(Encode(Strings.SucceededJobsPage_NoJobs), html);
            Assert.DoesNotContain("/jobs/succeeded/export", html);
        }

        [Fact]
        public void Render_WithSucceededJobs_ShowsTheDownloadAndCommands()
        {
            SetupJobs();

            var html = _renderer.Render(new SucceededJobs());

            Assert.Contains("data-url=\"/jobs/succeeded/export\" data-filename=\"nightshift-succeeded-jobs.csv\"", html);
            Assert.Contains(Encode(Strings.Common_DownloadCsv), html);
            Assert.Contains("data-ajax=\"/jobs/succeeded/delete-all\"", html);
            Assert.Contains($"data-confirm=\"{Encode(Strings.Common_DeleteAllConfirm)}\"", html);
            Assert.Contains("data-url=\"/jobs/succeeded/requeue\"", html);
            Assert.Contains("value=\"1\"", html);
        }

        [Fact]
        public void Render_WhenReadOnly_KeepsTheDownloadButHidesTheCommands()
        {
            SetupJobs();
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new SucceededJobs());

            Assert.Contains("data-url=\"/jobs/succeeded/export\"", html);
            Assert.DoesNotContain("/jobs/succeeded/delete-all", html);
            Assert.DoesNotContain("/jobs/succeeded/requeue", html);
        }

        private void SetupJobs()
        {
            _renderer.Monitoring.Setup(x => x.SucceededListCount()).Returns(1);
            _renderer.Monitoring.Setup(x => x.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<SucceededJobDto>(new Dictionary<string, SucceededJobDto>
                {
                    { "1", new SucceededJobDto { Job = Job.FromExpression(() => SucceededPageJobs.Work()), InSucceededState = true, SucceededAt = DateTime.UtcNow } }
                }));
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class SucceededPageJobs
    {
        public static void Work()
        {
        }
    }
}
