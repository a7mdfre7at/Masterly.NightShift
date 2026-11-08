using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Moq;
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
            Assert.Contains($"<span class=\"hf-catalog-summary text-muted\">2 {Encode(Strings.TagsPage_Title.ToLowerInvariant())}</span>", html);
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

        [Fact]
        public void Render_WithManyTags_ShowsOnePageInNameOrder_AndCountsOnlyItsTags()
        {
            SetupTags(Enumerable.Range(0, 25).Select(i => ($"t{i:00}", (long)i)).Reverse().ToArray());

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("data-tag=\"t00\"", html);
            Assert.Contains("data-tag=\"t19\"", html);
            Assert.DoesNotContain("data-tag=\"t20\"", html);
            Assert.True(html.IndexOf("data-tag=\"t00\"", StringComparison.Ordinal) < html.IndexOf("data-tag=\"t01\"", StringComparison.Ordinal));
            Assert.Contains($">25 {Encode(Strings.TagsPage_Title.ToLowerInvariant())}</span>", html);
            Assert.Contains("?from=20&amp;count=20", html);
            _renderer.Connection.Verify(x => x.GetSetCount(JobTags.GetKey("t19")), Times.Once);
            _renderer.Connection.Verify(x => x.GetSetCount(JobTags.GetKey("t20")), Times.Never);
        }

        [Fact]
        public void Render_SecondPage_ShowsTheRemainingTags_WeightedWithinThePage()
        {
            SetupTags(Enumerable.Range(0, 25).Select(i => ($"t{i:00}", (long)i)).ToArray());
            _renderer.Request.Query["from"] = "20";
            _renderer.Request.Query["count"] = "20";

            var html = _renderer.Render(new TagsPage());

            Assert.DoesNotContain("data-tag=\"t19\"", html);
            Assert.Contains("data-tag=\"t24\" style=\"--hf-weight: 1.00\"", html);
            Assert.Contains("data-tag=\"t20\" style=\"--hf-weight: 0.83\"", html);
            _renderer.Connection.Verify(x => x.GetSetCount(JobTags.GetKey("t00")), Times.Never);
        }

        [Fact]
        public void Render_WithAFilter_FindsTagsOnEveryPage_AndPagesThroughTheMatchesOnly()
        {
            // t00..t24 and billing-a..billing-c: the billing tags sort after the first page of 20
            SetupTags(Enumerable.Range(0, 25).Select(i => ($"t{i:00}", (long)i)).Concat(new[] { ("billing-a", 1L), ("billing-b", 2L), ("billing-c", 3L) }).ToArray());
            _renderer.Request.Query["filter"] = "BILLING";

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("data-tag=\"billing-a\"", html);
            Assert.Contains("data-tag=\"billing-c\"", html);
            Assert.DoesNotContain("data-tag=\"t00\"", html);
            Assert.Contains($">3 {Encode(Strings.TagsPage_Title.ToLowerInvariant())}</span>", html);
            Assert.Contains("name=\"filter\" value=\"BILLING\"", html);
            _renderer.Connection.Verify(x => x.GetSetCount(JobTags.GetKey("t00")), Times.Never);
        }

        [Fact]
        public void Render_WithAFilter_KeepsItInThePageLinks()
        {
            SetupTags(Enumerable.Range(0, 25).Select(i => ($"x{i:00}", (long)i)).ToArray());
            _renderer.Request.Query["filter"] = "x";
            _renderer.Request.Query["count"] = "10";

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("?from=10&amp;count=10&amp;filter=x", html);
        }

        [Fact]
        public void Render_WithAFilterThatMatchesNothing_SaysSo_AndKeepsTheFilterBox()
        {
            SetupTags(("urgent", 1));
            _renderer.Request.Query["filter"] = "nope";

            var html = _renderer.Render(new TagsPage());

            Assert.Contains(Encode(String.Format(Strings.TagsPage_NoMatch, "nope")), html);
            Assert.Contains("name=\"filter\" value=\"nope\"", html);
            Assert.DoesNotContain(Strings.TagsPage_NoTags_Html, html);
            Assert.DoesNotContain("data-tag=\"urgent\"", html);
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
