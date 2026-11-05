using System;
using System.Collections.Generic;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class AwaitingJobsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutAwaitingJobs_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new AwaitingJobsPage());

            Assert.Contains(Encode(Strings.AwaitingJobsPage_NoJobs), html);
            Assert.DoesNotContain("/jobs/awaiting/delete-all", html);
        }

        [Fact]
        public void Render_WithAwaitingJobs_ShowsTheCommands()
        {
            SetupAwaiting();

            var html = _renderer.Render(new AwaitingJobsPage());

            Assert.Contains("data-ajax=\"/jobs/awaiting/delete-all\"", html);
            Assert.Contains($"data-confirm=\"{Encode(Strings.Common_DeleteAllConfirm)}\"", html);
            Assert.Contains("data-url=\"/jobs/awaiting/enqueue\"", html);
            Assert.Contains("data-url=\"/jobs/awaiting/delete\"", html);
            Assert.Contains("<a href=\"/jobs/details/0\" class=\"text-decoration-none\">", html);
        }

        [Fact]
        public void Render_WhenReadOnly_HidesTheCommands()
        {
            SetupAwaiting();
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new AwaitingJobsPage());

            Assert.Contains("<a href=\"/jobs/details/1\">#1</a>", html);
            Assert.DoesNotContain("/jobs/awaiting/delete-all", html);
            Assert.DoesNotContain("/jobs/awaiting/enqueue", html);
        }

        [Fact]
        public void Render_ShowsAnExpiredJob_WhenItsDataIsGone()
        {
            SetupAwaitingIds();

            var html = _renderer.Render(new AwaitingJobsPage());

            Assert.Contains("<a href=\"/jobs/details/1\">#1</a>", html);
            Assert.Contains(Encode(Strings.Common_JobExpired), html);
        }

        [Fact]
        public void Render_ShowsNoParentState_WhenTheParentExpired()
        {
            SetupAwaiting();
            _renderer.Connection.Setup(x => x.GetStateData("0")).Returns((StateData)null);

            var html = _renderer.Render(new AwaitingJobsPage());

            Assert.Contains("AwaitingPageJobs.Work", html);
            Assert.DoesNotContain("href=\"/jobs/details/0\"", html);
            Assert.Contains(Encode(Strings.Common_NotAvailable), html);
        }

        [Fact]
        public void Render_ShowsNoParentState_ForAContinuationWithoutASingleParent()
        {
            SetupAwaiting();
            _renderer.Connection.Setup(x => x.GetStateData("1")).Returns(new StateData
            {
                Name = AwaitingState.StateName,
                Data = new Dictionary<string, string> { { "ParentIds", "[\"0\",\"2\"]" } }
            });

            var html = _renderer.Render(new AwaitingJobsPage());

            Assert.Contains("AwaitingPageJobs.Work", html);
            Assert.DoesNotContain("href=\"/jobs/details/0\"", html);
            _renderer.Connection.Verify(x => x.GetStateData("0"), Times.Never);
        }

        private void SetupAwaitingIds()
        {
            _renderer.Connection.Setup(x => x.GetSetCount("awaiting")).Returns(1);
            _renderer.Connection.Setup(x => x.GetRangeFromSet("awaiting", It.IsAny<int>(), It.IsAny<int>()))
                .Returns(() => new List<string> { "1" });
        }

        private void SetupAwaiting()
        {
            SetupAwaitingIds();
            _renderer.Connection.Setup(x => x.GetJobData("1")).Returns(new JobData
            {
                Job = Job.FromExpression(() => AwaitingPageJobs.Work()),
                State = AwaitingState.StateName,
                CreatedAt = DateTime.UtcNow
            });
            _renderer.Connection.Setup(x => x.GetStateData("1")).Returns(new StateData
            {
                Name = AwaitingState.StateName,
                Data = new Dictionary<string, string> { { "ParentId", "0" }, { "Options", "OnlyOnSucceededState" } }
            });
            _renderer.Connection.Setup(x => x.GetStateData("0")).Returns(new StateData
            {
                Name = ProcessingState.StateName,
                Data = new Dictionary<string, string>()
            });
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class AwaitingPageJobs
    {
        public static void Work()
        {
        }
    }
}
