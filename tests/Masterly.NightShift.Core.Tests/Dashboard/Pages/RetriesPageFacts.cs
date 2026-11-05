using System;
using System.Collections.Generic;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class RetriesPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutRetries_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new RetriesPage());

            Assert.Contains(Encode(Strings.RetriesPage_NoJobs), html);
            Assert.DoesNotContain("/retries/delete-all", html);
        }

        [Fact]
        public void Render_WithRetries_ShowsTheCommands()
        {
            SetupRetries();

            var html = _renderer.Render(new RetriesPage());

            Assert.Contains("data-ajax=\"/retries/delete-all\"", html);
            Assert.Contains($"data-confirm=\"{Encode(Strings.Common_DeleteAllConfirm)}\"", html);
            Assert.Contains("data-url=\"/jobs/scheduled/enqueue\"", html);
            Assert.Contains("data-url=\"/jobs/scheduled/delete\"", html);
            Assert.Contains("value=\"1\"", html);
        }

        [Fact]
        public void Render_WhenReadOnly_HidesTheCommands()
        {
            SetupRetries();
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new RetriesPage());

            Assert.Contains("<a href=\"/jobs/details/1\">#1</a>", html);
            Assert.DoesNotContain("/retries/delete-all", html);
            Assert.DoesNotContain("/jobs/scheduled/enqueue", html);
        }

        private void SetupRetries()
        {
            _renderer.Connection.Setup(x => x.GetSetCount("retries")).Returns(1);
            _renderer.Connection.Setup(x => x.GetRangeFromSet("retries", It.IsAny<int>(), It.IsAny<int>()))
                .Returns(() => new List<string> { "1" });
            _renderer.Connection.Setup(x => x.GetJobData("1")).Returns(new JobData
            {
                Job = Job.FromExpression(() => RetriesPageJobs.Work()),
                State = "Scheduled",
                CreatedAt = DateTime.UtcNow
            });
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class RetriesPageJobs
    {
        public static void Work()
        {
        }
    }
}
