using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Core.Tests.Dashboard.Pages;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class NavigationMenuFacts
    {
        private static readonly string[] BuiltInUrls =
        {
            "/jobs/enqueued", "/batches", "/retries", "/recurring", "/timeline", "/insights",
            "/catalog", "/tags", "/servers", "/audit", "/wallboard"
        };

        [Fact]
        public void Items_ListTheSectionsInOrder_WithTheirUrlsAndIcons()
        {
            var items = CreateItems("/");

            Assert.Equal(BuiltInUrls, items.Select(x => x.Url));
            Assert.Equal(
                new[] { "tasks", "th-large", "repeat", "time", "align-left", "stats", "book", "tag", "hdd", "list-alt", "blackboard" },
                items.Select(x => x.Icon));
        }

        [Fact]
        public void Items_UseTheLocalizedSectionNames()
        {
            var items = CreateItems("/");

            Assert.Equal(
                new[]
                {
                    Strings.NavigationMenu_Jobs, Strings.NavigationMenu_Batches, Strings.NavigationMenu_Retries,
                    Strings.NavigationMenu_RecurringJobs, Strings.NavigationMenu_Timeline, Strings.NavigationMenu_Insights,
                    Strings.NavigationMenu_Catalog, Strings.NavigationMenu_Tags, Strings.NavigationMenu_Servers,
                    Strings.NavigationMenu_Audit, Strings.NavigationMenu_Wallboard
                },
                items.Select(x => x.Text));
        }

        [Fact]
        public void Items_CarryTheCountersShownInTheSidebar()
        {
            var items = CreateItems("/").ToDictionary(x => x.Url);

            Assert.Equal(
                new[] { DashboardMetrics.EnqueuedCountOrNull, DashboardMetrics.FailedCountOrNull },
                items["/jobs/enqueued"].GetAllMetrics());
            Assert.Equal(new[] { DashboardMetrics.RetriesCount }, items["/retries"].GetAllMetrics());
            Assert.Equal(new[] { DashboardMetrics.RecurringJobCount }, items["/recurring"].GetAllMetrics());
            Assert.Equal(new[] { DashboardMetrics.ServerCount }, items["/servers"].GetAllMetrics());
            Assert.Empty(items["/batches"].GetAllMetrics());
            Assert.Empty(items["/wallboard"].GetAllMetrics());
        }

        [Theory]
        [InlineData("/jobs/failed", "/jobs/enqueued")]
        [InlineData("/JOBS/enqueued", "/jobs/enqueued")]
        [InlineData("/batches/42", "/batches")]
        [InlineData("/retries", "/retries")]
        [InlineData("/recurring/forecast", "/recurring")]
        [InlineData("/timeline", "/timeline")]
        [InlineData("/insights", "/insights")]
        [InlineData("/catalog", "/catalog")]
        [InlineData("/tags/urgent", "/tags")]
        [InlineData("/Servers", "/servers")]
        [InlineData("/audit", "/audit")]
        public void Items_ForARequestPath_MarkOnlyTheMatchingSectionActive(string path, string expectedUrl)
        {
            var items = CreateItems(path);

            Assert.Equal(new[] { expectedUrl }, items.Where(x => x.Active).Select(x => x.Url));
        }

        [Theory]
        [InlineData("/")]
        [InlineData("/wallboard")]
        [InlineData("/servers/web-1")]
        public void Items_ForAPathOutsideTheSections_MarkNothingActive(string path)
        {
            var items = CreateItems(path);

            Assert.DoesNotContain(items, x => x.Active);
        }

        private static List<MenuItem> CreateItems(string path)
        {
            var renderer = new PageRenderer();
            var page = new TagsPage();
            page.Assign(new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("GET", path), renderer.Storage.Object));

            return NavigationMenu.Items
                .Select(x => x(page))
                .Where(x => x != null && BuiltInUrls.Contains(x.Url))
                .ToList();
        }
    }
}
