using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.States;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class JobHistoryRendererFacts
    {
        [Fact]
        public void RenderHistory_LinksEveryParent_AndShowsTheMode_OfAFanInContinuation()
        {
            var html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
            {
                { "ParentIds", "11,12" },
                { "Mode", "Any" },
            }).ToString();

            Assert.Contains("<dt>Parents</dt>", html);
            Assert.Contains("<a href=\"/jobs/details/11\">#11</a> <a href=\"/jobs/details/12\">#12</a>", html);
            Assert.Contains("<span class=\"label label-default\">Any</span>", html);
            Assert.DoesNotContain("<dt>Parent</dt>", html);
        }

        [Fact]
        public void RenderHistory_LinksOnlyTheFirstParents_OfALargeFanIn()
        {
            // A continuation of a batch of 5,000 jobs rendered 5,000 links
            var ids = String.Join(",", Enumerable.Range(1, 5000));

            var html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string> { { "ParentIds", ids } }).ToString();

            Assert.Equal(JobHistoryRenderer.MaxParentLinks, Regex.Matches(html, "<a href=").Count);
            Assert.Contains("/jobs/details/50\"", html);
            Assert.DoesNotContain("/jobs/details/51\"", html);
            Assert.Contains("and 4950 more", html);
        }

        [Fact]
        public void RenderHistory_ShowsAllAsTheMode_WhenAFanInContinuationHasNoMode()
        {
            var html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
            {
                { "ParentIds", "11" },
            }).ToString();

            Assert.Contains("<a href=\"/jobs/details/11\">#11</a> <span class=\"label label-default\">All</span>", html);
        }

        [Fact]
        public void RenderHistory_FallsBackToTheSingleParent_WhenParentIdsAreEmpty()
        {
            var html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
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
            var html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
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
            var html = CreateHelper().RenderHistory(AwaitingState.StateName, new Dictionary<string, string>
            {
                { "ParentId", "7" },
                { "NextState", Masterly.NightShift.Common.SerializationHelper.Serialize<IState>(new EnqueuedState("critical"), Masterly.NightShift.Common.SerializationOption.TypedInternal) },
            }).ToString();

            Assert.Contains("<dt>Next State</dt><dd><span class=\"label label-default  label-state-active\">Enqueued</span></dd>", html);
        }

        [Fact, StaticLock]
        public void RenderHistory_FallsBackToTheRawData_WhenARendererThrows()
        {
            JobHistoryRenderer.Register("Facts.Throwing", static (_, data) => throw new KeyNotFoundException(data["Missing"]));

            var html = CreateHelper().RenderHistory("Facts.Throwing", new Dictionary<string, string> { { "Size", "<large>" } }).ToString();

            Assert.Equal("<dl class=\"dl-horizontal\"><dt>Size</dt><dd>&lt;large&gt;</dd></dl>", html);
        }

        [Fact]
        public void RenderHistory_ShowsTheJobsOwnQueue_ForAnEnqueuedStateThatSaysDefault()
        {
            // Enqueue("emails", ...) records "default" in the state, and the job goes to "emails"
            var page = new JobDetailsPage("1")
            {
                RenderedJob = new Job(typeof(JobHistoryRendererFacts), typeof(JobHistoryRendererFacts).GetMethod(nameof(Work)), new object[0], "emails"),
            };
            page.Assign(new DashboardContextStub(new DashboardOptions()));

            var html = new HtmlHelper(page).RenderHistory(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }).ToString();

            Assert.Contains("<dt>Queue:</dt>", html);
            Assert.Contains("emails", html);
        }

        [Fact]
        public void RenderHistory_ShowsNoQueue_ForTheDefaultQueue()
        {
            var html = CreateHelper().RenderHistory(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } });

            Assert.Null(html);
        }

        [Fact]
        public void RenderHistory_ShowsTheStatesQueue_WhenItIsNotDefault()
        {
            var html = CreateHelper().RenderHistory(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "critical" } }).ToString();

            Assert.Contains("critical", html);
        }

        [Fact]
        public void RenderHistory_FallsBackToTheRawData_WhenABuiltInRendererMeetsUnexpectedData()
        {
            // Written by another version: Latency isn't a number
            var html = CreateHelper().RenderHistory(SucceededState.StateName, new Dictionary<string, string> { { "Latency", "fast" } }).ToString();

            Assert.Equal("<dl class=\"dl-horizontal\"><dt>Latency</dt><dd>fast</dd></dl>", html);
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

#pragma warning disable CS0618 // The color methods are obsolete, but still supported
        [Fact, StaticLock]
        public void AddStateColors_ReplaceTheColors_WhenCalledAgainForTheSameState()
        {
            JobHistoryRenderer.AddForegroundStateColor("Facts.Recolored", "#111");
            JobHistoryRenderer.AddForegroundStateColor("Facts.Recolored", "#222");
            JobHistoryRenderer.AddBackgroundStateColor("Facts.Recolored", "#333");
            JobHistoryRenderer.AddBackgroundStateColor("Facts.Recolored", "#444");

            Assert.Equal("#222", JobHistoryRenderer.GetForegroundStateColor("Facts.Recolored"));
            Assert.Equal("#444", JobHistoryRenderer.GetBackgroundStateColor("Facts.Recolored"));
        }

        [Fact, StaticLock]
        public void StateLabel_UsesTheRegisteredColor_ForAStateWithoutASuffix()
        {
            JobHistoryRenderer.AddForegroundStateColor("Facts.Colored", "#123456");

            var html = CreateHelper().StateLabel("Facts.Colored").ToString();

            Assert.Equal("<span class=\"label label-default \" style=\"background-color: #123456;\">Facts.Colored</span>", html);
        }
#pragma warning restore CS0618

        [Fact]
        public void StateLabel_UsesTheSuffixClass_ForABuiltInState()
        {
            var html = CreateHelper().StateLabel(SucceededState.StateName).ToString();

            Assert.Equal("<span class=\"label label-default  label-state-success\">Succeeded</span>", html);
        }

        [Fact]
        public void StateLabel_KeepsTheNeutralLook_ForAStateWithNeitherSuffixNorColor()
        {
            var html = CreateHelper().StateLabel("Facts.Unknown").ToString();

            Assert.Equal("<span class=\"label label-default \">Facts.Unknown</span>", html);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Work() { }

        private static HtmlHelper CreateHelper()
        {
            var page = new JobDetailsPage("1");
            page.Assign(new DashboardContextStub(new DashboardOptions()));
            return new HtmlHelper(page);
        }
    }
}
