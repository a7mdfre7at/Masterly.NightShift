using System.Collections.Generic;
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

        private static HtmlHelper CreateHelper()
        {
            var page = new JobDetailsPage("1");
            page.Assign(new DashboardContextStub(new DashboardOptions()));
            return new HtmlHelper(page);
        }
    }
}
