using System;
using System.Collections.Generic;
using System.Net;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class TagsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutTags_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"{Encode(Strings.TagsPage_Title)} <small class=\"text-muted\">{Encode(Strings.TagsPage_Hint)}</small>", html);
            Assert.Contains($"<div class=\"alert alert-info\">{Strings.TagsPage_NoTags_Html}</div>", html);
            Assert.DoesNotContain("hf-tags-cloud", html);
        }

        [Fact]
        public void Render_WithTags_ShowsTheSummaryAndACloudWeightedByCount()
        {
            SetupTags(("urgent", 2), ("billing", 4));

            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"placeholder=\"{Encode(Strings.TagsPage_Search)}\"", html);
            Assert.Contains($"2 {Encode(Strings.TagsPage_Title.ToLowerInvariant())} &middot; {Encode(String.Format(Strings.FailedJobsPage_Clusters_Jobs, 6))}", html);
            Assert.Contains("<a class=\"hf-tag hf-tag-lg js-tag\" href=\"/tags/billing\" data-tag=\"billing\" style=\"--hf-weight: 1.00\">", html);
            Assert.Contains("<a class=\"hf-tag hf-tag-lg js-tag\" href=\"/tags/urgent\" data-tag=\"urgent\" style=\"--hf-weight: 0.50\">", html);
            Assert.Contains("<span class=\"glyphicon glyphicon-tag\"></span> billing <span class=\"hf-tag-count\">4</span>", html);
            Assert.True(html.IndexOf("data-tag=\"billing\"", StringComparison.Ordinal) < html.IndexOf("data-tag=\"urgent\"", StringComparison.Ordinal));
        }

        [Fact]
        public void Render_WithTags_ListsThemInATableWithForgetButtons()
        {
            SetupTags(("on call", 1));

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("<tr class=\"js-tag-row\" data-tag=\"on call\">", html);
            Assert.Contains("<td><a href=\"/tags/on%20call\" class=\"hf-tag\"><span class=\"glyphicon glyphicon-tag\"></span> on call</a></td>", html);
            Assert.Contains("<td class=\"align-right\">1</td>", html);
            Assert.Contains("data-ajax=\"/tags/forget/on%20call\"", html);
            Assert.Contains($"data-confirm=\"{Encode(Strings.TagsPage_ForgetConfirm)}\"", html);
        }

        [Fact]
        public void Render_WhenReadOnly_HidesTheForgetButtons()
        {
            SetupTags(("urgent", 1));
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("<tr class=\"js-tag-row\" data-tag=\"urgent\">", html);
            Assert.DoesNotContain("/tags/forget/", html);
            Assert.DoesNotContain(Encode(Strings.TagsPage_Forget), html);
        }

        [Fact]
        public void Render_WithAnEmptyTag_DoesNotDivideByZero()
        {
            SetupTags(("stale", 0));

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("data-tag=\"stale\" style=\"--hf-weight: 0.00\"", html);
        }

        private void SetupTags(params (string Tag, long Count)[] tags)
        {
            var index = new HashSet<string>();
            foreach (var tag in tags)
            {
                index.Add(tag.Tag);
                _renderer.Connection.Setup(x => x.GetSetCount(JobTags.GetKey(tag.Tag))).Returns(tag.Count);
            }

            _renderer.Connection.Setup(x => x.GetAllItemsFromSet(JobTags.IndexKey)).Returns(index);
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
