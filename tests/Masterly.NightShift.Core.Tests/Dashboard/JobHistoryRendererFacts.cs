using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class JobHistoryRendererFacts
{
    [Fact]
    public void RenderHistory_LinksEveryParent_AndShowsTheMode_OfAFanInContinuation()
    {
        string html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
        {
            { "ParentIds", "11,12" },
            { "Mode", "Any" },
        }).ToString();

        Assert.Contains("<dt>Parents</dt>", html);
        Assert.Contains("<a href=\"/jobs/details/11\">#11</a> <a href=\"/jobs/details/12\">#12</a>", html);
        Assert.Contains("<span class=\"badge text-bg-secondary\">Any</span>", html);
        Assert.DoesNotContain("<dt>Parent</dt>", html);
    }

    [Fact]
    public void RenderHistory_LinksOnlyTheFirstParents_OfALargeFanIn()
    {
        // A continuation of a batch of 5,000 jobs rendered 5,000 links
        string ids = string.Join(",", Enumerable.Range(1, 5000));

        string html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string> { { "ParentIds", ids } }).ToString();

        Assert.Equal(JobHistoryRenderer.MaxParentLinks, Regex.Matches(html, "<a href=").Count);
        Assert.Contains("/jobs/details/50\"", html);
        Assert.DoesNotContain("/jobs/details/51\"", html);
        Assert.Contains("and 4950 more", html);
    }

    [Fact]
    public void RenderHistory_ShowsAllAsTheMode_WhenAFanInContinuationHasNoMode()
    {
        string html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
        {
            { "ParentIds", "11" },
        }).ToString();

        Assert.Contains("<a href=\"/jobs/details/11\">#11</a> <span class=\"badge text-bg-secondary\">All</span>", html);
    }

    [Fact]
    public void RenderHistory_FallsBackToTheSingleParent_WhenParentIdsAreEmpty()
    {
        string html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
        {
            { "ParentIds", "" },
            { "ParentId", "7" },
        }).ToString();

        Assert.Contains("<dt>Parent</dt><dd><a href=\"/jobs/details/7\">#7</a></dd>", html);
        Assert.DoesNotContain("<dt>Parents</dt>", html);
    }

    [Fact]
    public void RenderHistory_ShowsTheStoredTypeName_WhenTheNextStateCanNotBeLoaded()
    {
        // A continuation created by another application, whose custom state type this dashboard doesn't have
        string html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
        {
            { "ParentId", "7" },
            { "NextState", "{\"$type\":\"Contoso.Jobs.HoldState, Contoso.Jobs\",\"Reason\":\"<b>hold</b>\"}" },
            { "Options", "OnlyOnSucceededState" },
        }).ToString();

        Assert.Contains("<dt>Next State</dt><dd><code>Contoso.Jobs.HoldState</code></dd>", html);
        Assert.Contains("<dt>Options</dt><dd><code>OnlyOnSucceededState</code></dd>", html);
    }

    [Fact]
    public void RenderHistory_ShowsTheStateLabel_WhenTheNextStateLoads()
    {
        string html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
        {
            { "ParentId", "7" },
            { "NextState", Masterly.NightShift.Common.SerializationHelper.Serialize<IState>(new EnqueuedState("critical"), Masterly.NightShift.Common.SerializationOption.TypedInternal) },
        }).ToString();

        Assert.Contains("<dt>Next State</dt><dd><span class=\"badge  label-state-active\">Enqueued</span></dd>", html);
    }

    [Fact, StaticLock]
    public void RenderHistory_FallsBackToTheRawData_WhenARendererThrows()
    {
        JobHistoryRenderer.Register("Facts.Throwing", static (_, data) => throw new KeyNotFoundException(data["Missing"]));

        string html = CreateHelper().RenderHistory("Facts.Throwing", new Dictionary<string, string> { { "Size", "<large>" } }).ToString();

        Assert.Equal("<dl class=\"hf-dl-horizontal\"><dt>Size</dt><dd>&lt;large&gt;</dd></dl>", html);
    }

    [Fact]
    public void RenderHistory_ShowsTheJobsOwnQueue_ForAnEnqueuedStateThatSaysDefault()
    {
        // Enqueue("emails", ...) records "default" in the state, and the job goes to "emails"
        JobDetailsPage page = new("1")
        {
            RenderedJob = new Job(typeof(JobHistoryRendererFacts), typeof(JobHistoryRendererFacts).GetMethod(nameof(Work)), [], "emails"),
        };
        page.Assign(new DashboardContextStub(new DashboardOptions()));

        string html = new HtmlHelper(page).RenderHistory(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }).ToString();

        Assert.Contains("<dt>Queue:</dt>", html);
        Assert.Contains("emails", html);
    }

    [Fact]
    public void RenderHistory_ShowsNoQueue_ForTheDefaultQueue()
    {
        NonEscapedString? html = CreateHelper().RenderHistory(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } });

        Assert.Null(html);
    }

    [Fact]
    public void RenderHistory_ShowsTheStatesQueue_WhenItIsNotDefault()
    {
        string html = CreateHelper().RenderHistory(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "critical" } }).ToString();

        Assert.Contains("critical", html);
    }

    [Fact]
    public void RenderHistory_FallsBackToTheRawData_WhenABuiltInRendererMeetsUnexpectedData()
    {
        // Written by another version: Latency isn't a number
        string html = CreateHelper().RenderHistory(SucceededState.StateName, new Dictionary<string, string> { { "Latency", "fast" } }).ToString();

        Assert.Equal("<dl class=\"hf-dl-horizontal\"><dt>Latency</dt><dd>fast</dd></dl>", html);
    }

    [Fact]
    public void GetStateCssSuffix_ReturnsTheBuiltInSuffix_OrNullForAStateWithoutOne()
    {
        Assert.Equal("success", JobHistoryRenderer.GetStateCssSuffix(SucceededState.StateName));
        Assert.Null(JobHistoryRenderer.GetStateCssSuffix("NoSuchState"));
        Assert.Null(JobHistoryRenderer.GetStateCssSuffix(null));
    }

    [Fact, StaticLock]
    public void AddStateCssSuffix_ReplacesTheSuffix_WhenCalledAgainForTheSameState()
    {
        JobHistoryRenderer.AddStateCssSuffix("Facts.Suffixed", "warning");
        JobHistoryRenderer.AddStateCssSuffix("Facts.Suffixed", "danger");

        Assert.Equal("danger", JobHistoryRenderer.GetStateCssSuffix("Facts.Suffixed"));
    }

    [Fact, StaticLock]
    public void StateColors_ComeFromTheCssSuffix_ForACustomState()
    {
        JobHistoryRenderer.AddStateCssSuffix("Facts.Colored", "danger");

        Assert.Equal(
            JobHistoryRenderer.GetForegroundStateColor(FailedState.StateName),
            JobHistoryRenderer.GetForegroundStateColor("Facts.Colored"));
        Assert.Equal(
            JobHistoryRenderer.GetBackgroundStateColor(FailedState.StateName),
            JobHistoryRenderer.GetBackgroundStateColor("Facts.Colored"));
    }

    [Fact]
    public void StateColors_AreInherit_ForAStateWithoutASuffix()
    {
        Assert.Equal("inherit", JobHistoryRenderer.GetForegroundStateColor("Facts.NoSuffix"));
        Assert.Equal("inherit", JobHistoryRenderer.GetBackgroundStateColor("Facts.NoSuffix"));
    }

    [Fact]
    public void StateLabel_UsesTheSuffixClass_ForABuiltInState()
    {
        string html = CreateHelper().StateLabel(SucceededState.StateName).ToString();

        Assert.Equal("<span class=\"badge  label-state-success\">Succeeded</span>", html);
    }

    [Fact]
    public void StateLabel_KeepsTheNeutralLook_ForAStateWithoutASuffix()
    {
        string html = CreateHelper().StateLabel("Facts.Unknown").ToString();

        Assert.Equal("<span class=\"badge text-bg-secondary \">Facts.Unknown</span>", html);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Work() { }

    private static HtmlHelper CreateHelper()
    {
        JobDetailsPage page = new("1");
        page.Assign(new DashboardContextStub(new DashboardOptions()));
        return new HtmlHelper(page);
    }
}
