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
    public class TaggedJobsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();

        public TaggedJobsPageFacts()
        {
            _renderer.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        }

        [Fact]
        public void Ctor_KeepsTheTag()
        {
            var page = new TaggedJobsPage("Billing");

            Assert.Equal("Billing", page.Tag);
        }

        [Fact]
        public void Render_WithoutJobs_ShowsTheEmptyMessageAndBreadcrumbs()
        {
            var html = _renderer.Render(new TaggedJobsPage("urgent"));

            Assert.Contains($"<div class=\"hf-topbar-title\">{Encode(String.Format(Strings.TaggedJobsPage_Title, "urgent"))}</div>", html);
            Assert.Contains("href=\"/tags\"", html);
            Assert.Contains($"<div class=\"alert alert-info\">{Encode(Strings.TaggedJobsPage_NoJobs)}</div>", html);
            Assert.DoesNotContain("js-jobs-list", html);
            Assert.DoesNotContain(Encode(String.Format(Strings.TaggedJobsPage_Expired, 0)), html);
        }

        [Fact]
        public void Render_WithJobs_ListsThemNewestFirstWithTheTagCount()
        {
            SetupTag("tags:urgent", "1", "2");
            SetupJob("1");
            SetupJob("2");

            var html = _renderer.Render(new TaggedJobsPage("Urgent"));

            Assert.Contains("<span class=\"hf-tag hf-tag-lg\"><span class=\"glyphicon glyphicon-tag\"></span> Urgent <span class=\"hf-tag-count\">2</span></span>", html);
            Assert.Contains("<td class=\"min-width\"><a href=\"/jobs/details/1\">#1</a></td>", html);
            Assert.Contains(">Succeeded</span></td>", html);
            Assert.Contains("TaggedPageJobs.Work", html);
            Assert.True(html.IndexOf("/jobs/details/2", StringComparison.Ordinal) < html.IndexOf("/jobs/details/1", StringComparison.Ordinal));
            Assert.DoesNotContain(Encode(Strings.TaggedJobsPage_NoJobs), html);
        }

        [Fact]
        public void Render_WithExpiredJobs_HidesThem_OffersToForgetThem_AndWritesNothing()
        {
            SetupTag("tags:urgent", "1", "9");
            SetupJob("1");
            _renderer.Request.Query["from"] = "0";
            _renderer.Request.Query["count"] = "10";

            var html = _renderer.Render(new TaggedJobsPage("urgent"));

            Assert.Contains(Encode(String.Format(Strings.TaggedJobsPage_Expired, 1)), html);
            Assert.Contains("data-ajax=\"/tags/forget-expired/urgent?from=0&amp;count=10\"", html);
            Assert.Contains("/jobs/details/1", html);
            Assert.DoesNotContain("/jobs/details/9", html);
            _renderer.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
            _renderer.Storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact]
        public void Render_WithExpiredJobsWhenReadOnly_SaysSoWithoutTheButton()
        {
            SetupTag("tags:urgent", "9");
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new TaggedJobsPage("urgent"));

            Assert.Contains(Encode(String.Format(Strings.TaggedJobsPage_Expired, 1)), html);
            Assert.Contains(Encode(Strings.TaggedJobsPage_NoJobs), html);
            Assert.DoesNotContain("/tags/forget-expired/", html);
            _renderer.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        }

        private void SetupTag(string key, params string[] jobIds)
        {
            _renderer.Connection.Setup(x => x.GetSetCount(key)).Returns(jobIds.Length);
            _renderer.Connection.Setup(x => x.GetRangeFromSet(key, It.IsAny<int>(), It.IsAny<int>()))
                .Returns(() => new List<string>(jobIds));
        }

        private void SetupJob(string jobId)
        {
            _renderer.Connection.Setup(x => x.GetJobData(jobId)).Returns(new JobData
            {
                Job = Job.FromExpression(() => TaggedPageJobs.Work()),
                State = "Succeeded",
                CreatedAt = DateTime.UtcNow
            });
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class TaggedPageJobs
    {
        public static void Work()
        {
        }
    }
}
