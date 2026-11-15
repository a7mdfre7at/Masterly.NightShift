using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class TagsPageFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_WithoutTags_ShowsTheEmptyMessage()
    {
        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"<span class=\"ns-page-name\">{Encode(Strings.TagsPage_Title)}</span> <span class=\"ns-count-chip js-list-count\">0</span></h1>", html);
        Assert.Contains($"<p class=\"ns-page-subtitle\">{Encode(Strings.TagsPage_Hint)}</p>", html);
        Assert.Contains($"<h2 class=\"ns-empty-state-title\">{Encode(Strings.TagsPage_EmptyTitle)}</h2>", html);
        Assert.Contains($"<p>{Strings.TagsPage_NoTags_Html}</p>", html);
        Assert.DoesNotContain("ns-tags-cloud", html);
    }

    [Fact]
    public void Render_WithTags_ShowsTheSummaryAndACloudWeightedByCount()
    {
        SetupTags(("urgent", 2), ("billing", 4));

        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"placeholder=\"{Encode(Strings.TagsPage_Search)}\"", html);
        Assert.Contains($"<span class=\"hf-catalog-summary text-muted\">2 {Encode(Strings.TagsPage_Title.ToLowerInvariant())}</span>", html);
        Assert.Contains($"aria-label=\"{Encode(Strings.TagsPage_Search)}\"", html);
        Assert.Contains("<span class=\"ns-count-chip js-list-count\">2</span>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist ns-tag-chip js-tag\" href=\"/tags/billing\" data-tag=\"billing\" style=\"--ns-weight: 1.00\">", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist ns-tag-chip js-tag\" href=\"/tags/urgent\" data-tag=\"urgent\" style=\"--ns-weight: 0.50\">", html);
        Assert.Contains(DashboardMarkup.Icon("tag-fill") + " billing <span class=\"ns-tag-count\">4</span>", html);
        Assert.DoesNotContain("hf-tag\"", html);
        Assert.True(html.IndexOf("data-tag=\"billing\"", StringComparison.Ordinal) < html.IndexOf("data-tag=\"urgent\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_WithTags_ListsThemInATableWithForgetButtons()
    {
        SetupTags(("on call", 1));

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<tr class=\"hover js-tag-row\" data-tag=\"on call\">", html);
        Assert.Contains("<td><a href=\"/tags/on%20call\" class=\"ns-chip ns-chip-assist ns-tag-chip\">" + DashboardMarkup.Icon("tag-fill") + " on call</a></td>", html);
        Assert.Contains("<td class=\"align-right ns-tabular\">1</td>", html);
        Assert.Contains("<td class=\"ns-actions-cell\">", html);
        Assert.Contains("class=\"ns-icon-button ns-row-danger\" data-ajax=\"/tags/forget/on%20call\"", html);
        Assert.Contains($"aria-label=\"{Encode(string.Format(Strings.TagsPage_ForgetTag, "on call"))}\"", html);
        Assert.Contains($"<span class=\"visually-hidden\">{Encode(Strings.Common_Actions)}</span>", html);
        Assert.Contains($"data-confirm=\"{Encode(Strings.TagsPage_ForgetConfirm)}\"", html);
    }

    [Fact]
    public void Render_WhenReadOnly_HidesTheForgetButtons()
    {
        SetupTags(("urgent", 1));
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<tr class=\"hover js-tag-row\" data-tag=\"urgent\">", html);
        Assert.DoesNotContain("ns-actions-cell", html);
        Assert.DoesNotContain("/tags/forget/", html);
        Assert.DoesNotContain(Encode(Strings.TagsPage_Forget), html);
    }

    [Fact]
    public void Render_WithAnEmptyTag_DoesNotDivideByZero()
    {
        SetupTags(("stale", 0));

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("data-tag=\"stale\" style=\"--ns-weight: 0.00\"", html);
    }

    [Fact]
    public void Render_WithManyTags_ShowsOnePageInNameOrder_AndCountsOnlyItsTags()
    {
        SetupTags([.. Enumerable.Range(0, 25).Select(i => ($"t{i:00}", (long)i)).Reverse()]);

        string html = _renderer.Render(new TagsPage());

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
        SetupTags([.. Enumerable.Range(0, 25).Select(i => ($"t{i:00}", (long)i))]);
        _renderer.Request.Query["from"] = "20";
        _renderer.Request.Query["count"] = "20";

        string html = _renderer.Render(new TagsPage());

        Assert.DoesNotContain("data-tag=\"t19\"", html);
        Assert.Contains("data-tag=\"t24\" style=\"--ns-weight: 1.00\"", html);
        Assert.Contains("data-tag=\"t20\" style=\"--ns-weight: 0.83\"", html);
        _renderer.Connection.Verify(x => x.GetSetCount(JobTags.GetKey("t00")), Times.Never);
    }

    [Fact]
    public void Render_WithAFilter_FindsTagsOnEveryPage_AndPagesThroughTheMatchesOnly()
    {
        // t00..t24 and billing-a..billing-c: the billing tags sort after the first page of 20
        SetupTags(
        [
            .. Enumerable.Range(0, 25).Select(i => ($"t{i:00}", (long)i)),
            .. new[] { ("billing-a", 1L), ("billing-b", 2L), ("billing-c", 3L) },
        ]);
        _renderer.Request.Query["filter"] = "BILLING";

        string html = _renderer.Render(new TagsPage());

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
        SetupTags([.. Enumerable.Range(0, 25).Select(i => ($"x{i:00}", (long)i))]);
        _renderer.Request.Query["filter"] = "x";
        _renderer.Request.Query["count"] = "10";

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("?from=10&amp;count=10&amp;filter=x", html);
    }

    [Fact]
    public void Render_WithAFilterThatMatchesNothing_SaysSo_AndKeepsTheFilterBox()
    {
        SetupTags(("urgent", 1));
        _renderer.Request.Query["filter"] = "nope";

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<h2 class=\"ns-empty-state-title\">" + Encode(string.Format(Strings.TagsPage_NoMatch, "nope")) + "</h2>", html);
        Assert.Contains("href=\"/tags\">" + Encode(Strings.TagsPage_Title) + "</a>", html);
        Assert.Contains("name=\"filter\" value=\"nope\"", html);
        Assert.DoesNotContain(Strings.TagsPage_NoTags_Html, html);
        Assert.DoesNotContain("data-tag=\"urgent\"", html);
    }

    [Fact]
    public void Render_KeepsTheHeadingsInOrder_AndNamesEveryHeaderCell()
    {
        string empty = _renderer.Render(new TagsPage());
        SetupTags(("urgent", 1));

        string html = _renderer.Render(new TagsPage());

        PageStructure.AssertHeadingsInOrder(empty);
        PageStructure.AssertHeadingsInOrder(html);
        PageStructure.AssertNoEmptyHeaderCells(html);
    }

    private void SetupTags(params (string Tag, long Count)[] tags)
    {
        HashSet<string> index = [];
        foreach ((string? Tag, long Count) in tags)
        {
            index.Add(Tag);
            _renderer.Connection.Setup(x => x.GetSetCount(JobTags.GetKey(Tag))).Returns(Count);
        }

        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(JobTags.IndexKey)).Returns(index);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
