using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class LayoutPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithDarkModeEnabled_EmitsTheThemeScriptStylesheetAndSwitcher()
        {
            _renderer.Options = new DashboardOptions { DarkModeEnabled = true };

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("localStorage.getItem('nightshift.theme')", html);
            Assert.Contains("id=\"hfDarkStylesheet\"", html);
            Assert.Contains("class=\"hf-theme js-theme\"", html);
            Assert.Contains($"title=\"{Encode(Strings.LayoutPage_Theme_Dark)}\"", html);
            Assert.Contains("data-darkmode=\"true\"", html);
        }

        [Fact]
        public void Render_WithDarkModeDisabled_LeavesTheThemeSwitcherOut()
        {
            _renderer.Options = new DashboardOptions { DarkModeEnabled = false };

            var html = _renderer.Render(new TagsPage());

            Assert.DoesNotContain("nightshift.theme", html);
            Assert.DoesNotContain("hfDarkStylesheet", html);
            Assert.DoesNotContain("js-theme", html);
            Assert.Contains("data-darkmode=\"false\"", html);
        }

        [Fact]
        public void Render_WithAppPath_ShowsTheBackLink()
        {
            _renderer.Options = new DashboardOptions { AppPath = "/my-app" };

            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"<a href=\"/my-app\" title=\"{Encode(Strings.LayoutPage_Back)}\">", html);
        }

        [Fact]
        public void Render_WithoutAppPath_ShowsTheVersionInTheSidebarFooter()
        {
            _renderer.Options = new DashboardOptions { AppPath = null };
            var version = typeof(RazorPage).Assembly.GetName().Version;

            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"<span>Masterly.NightShift {version.Major}.{version.Minor}.{version.Build}</span>", html);
            Assert.DoesNotContain(Encode(Strings.LayoutPage_Back), html);
        }

        [Fact]
        public void Render_Always_RendersTheTopbarWatchlistAndClientConfiguration()
        {
            var html = _renderer.Render(new TagsPage());

            Assert.Contains("<div class=\"hf-topbar-title\">" + Encode(Strings.TagsPage_Title) + "</div>", html);
            Assert.Contains("data-live-url=\"/jobs/live\"", html);
            Assert.Contains("data-job-url=\"/jobs/details/\"", html);
            Assert.Contains($"data-text-title=\"{Encode(Strings.LayoutPage_Watchlist)}\"", html);
            Assert.Contains($"<span class=\"hf-search-text\">{Encode(Strings.LayoutPage_Search)}</span>", html);
            Assert.Contains("class=\"hf-icon-button js-page-refresh\"", html);
            Assert.Contains("id=\"errorAlert\"", html);
            Assert.Contains("data-pollurl=\"/stats\"", html);
            Assert.Contains("data-liveurl=\"/jobs/live\"", html);
            Assert.Contains($"data-text-live=\"{Encode(Strings.LayoutPage_LiveToggle)}\"", html);
        }

        [Fact]
        public void Render_WhenNothingIsPaused_ShowsNoDrainBanner()
        {
            var html = _renderer.Render(new TagsPage());

            Assert.DoesNotContain("hf-drain-banner", html);
        }

        [Fact]
        public void Render_WhenQueuesArePaused_ShowsTheQueuesBannerWithALinkToTheQueues()
        {
            SetupControlHash(new Dictionary<string, string> { { "Queue:critical", "maintenance" }, { "Queue:emails", "" } });

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("hf-drain-banner hf-drain-banner-queues", html);
            Assert.Contains(Encode(String.Format(Strings.LayoutPage_QueuesPaused, "critical, emails")), html);
            Assert.Contains("href=\"/jobs/enqueued\">" + Encode(Strings.LayoutPage_QueuesPaused_Link), html);
            Assert.DoesNotContain("href=\"/servers\">" + Encode(Strings.NavigationMenu_Servers) + " &rarr;", html);
        }

        [Fact]
        public void Render_WhenServersArePaused_ShowsTheServersBannerWithALinkToTheServers()
        {
            SetupServers("web-1");
            SetupControlHash(new Dictionary<string, string> { { "Server:web-1", "deploy" } });

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("hf-drain-banner hf-drain-banner-queues", html);
            Assert.Contains(Encode(String.Format(Strings.LayoutPage_ServersPaused, "web-1")), html);
            Assert.Contains("href=\"/servers\">" + Encode(Strings.NavigationMenu_Servers) + " &rarr;", html);
            Assert.DoesNotContain(Encode(Strings.LayoutPage_QueuesPaused_Link), html);
        }

        [Fact]
        public void Render_LeavesOutPausesOfServersThatAreGone()
        {
            SetupServers("web-1:2:new");
            SetupControlHash(new Dictionary<string, string> { { "Server:web-1:1:old", "deploy" }, { "Server:web-1:2:new", "" } });

            var html = _renderer.Render(new TagsPage());

            Assert.Contains(Encode(String.Format(Strings.LayoutPage_ServersPaused, "web-1:2:new")), html);
            Assert.DoesNotContain("web-1:1:old", html);
        }

        [Fact]
        public void Render_ShowsNoBanner_WhenOnlyServersThatAreGoneArePaused()
        {
            SetupControlHash(new Dictionary<string, string> { { "Server:web-1:1:old", "deploy" } });

            var html = _renderer.Render(new TagsPage());

            Assert.DoesNotContain("hf-drain-banner", html);
        }

        [Fact]
        public void Render_WhenDrainModeIsOn_ShowsTheBannerWithSinceReasonAndResumeButton()
        {
            SetupControlHash(new Dictionary<string, string>
            {
                { "Drain", "1" },
                { "DrainSince", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-3)) },
                { "DrainReason", "Database <upgrade>" },
                { "Queue:critical", "ignored while draining" }
            });

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("<div class=\"hf-drain-banner\" role=\"status\">", html);
            Assert.Contains($"<strong>{Encode(Strings.LayoutPage_Drain_Banner)}</strong>", html);
            Assert.Contains("class=\"hf-drain-banner-since\"", html);
            Assert.Contains("<em class=\"hf-drain-banner-reason\">Database &lt;upgrade&gt;</em>", html);
            Assert.Contains("data-ajax=\"/maintenance/resume\"", html);
            Assert.DoesNotContain("hf-drain-banner-queues", html);
        }

        [Fact]
        public void Render_WhenDrainModeIsOnAndTheDashboardIsReadOnly_ShowsTheBannerWithoutResumeButton()
        {
            SetupControlHash(new Dictionary<string, string> { { "Drain", "1" } });
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"<strong>{Encode(Strings.LayoutPage_Drain_Banner)}</strong>", html);
            Assert.DoesNotContain("hf-drain-banner-since", html);
            Assert.DoesNotContain("hf-drain-banner-reason", html);
            Assert.DoesNotContain("/maintenance/resume", html);
        }

        [Fact]
        public void Render_WhenTheStorageIsUnreachable_StillRendersThePageWithoutDrainBanner()
        {
            _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Retries = 0, Awaiting = 0 });
            _renderer.Storage.Setup(x => x.GetReadOnlyConnection()).Throws(new InvalidOperationException("Storage is down"));

            var html = _renderer.Render(new HomePage());

            Assert.Contains("js-page-container", html);
            Assert.DoesNotContain("hf-drain-banner", html);
        }

        [Fact]
        public void Render_WhenStorageDisplayIsOn_ListsTheStorageInTheFooter()
        {
            _renderer.Storage.Setup(x => x.ToString()).Returns("Test <Storage>");
            _renderer.Options = new DashboardOptions { DisplayStorageConnectionString = true };

            var html = _renderer.Render(new TagsPage());

            Assert.Contains("<li>Test &lt;Storage&gt; ", html);
        }

        [Fact]
        public void Render_WhenStorageDisplayIsOff_LeavesTheStorageOutOfTheFooter()
        {
            _renderer.Storage.Setup(x => x.ToString()).Returns("Test <Storage>");
            _renderer.Options = new DashboardOptions { DisplayStorageConnectionString = false };

            var html = _renderer.Render(new TagsPage());

            Assert.DoesNotContain("Test &lt;Storage&gt;", html);
        }

        [Fact]
        public void Render_WhenTheStorageReportsItsTime_ShowsItInTheFooter()
        {
            SetupStorageTime(DateTime.UtcNow);

            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"<li>{Encode(Strings.LayoutPage_Footer_StorageTime)} <span data-moment-local=", html);
            Assert.DoesNotContain(Encode(Strings.LayoutPage_Footer_TimeIsOutOfSync), html);
        }

        [Fact]
        public void Render_WhenTheStorageClockIsOutOfSync_WarnsInTheFooter()
        {
            SetupStorageTime(DateTime.UtcNow.AddMinutes(-5));

            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"<span class=\"text-warning\" title=\"{Encode(Strings.LayoutPage_Footer_TimeIsOutOfSync)}\">", html);
        }

        [Fact]
        public void Render_WhenTheStorageCantReportItsTime_ShowsOnlyTheApplicationTime()
        {
            var html = _renderer.Render(new TagsPage());

            Assert.DoesNotContain(Encode(Strings.LayoutPage_Footer_StorageTime), html);
            Assert.Contains($"<span>{Encode(Strings.LayoutPage_Footer_Time)} <span data-moment-local=", html);
        }

        [Fact]
        public void Render_OnASection_MarksItsNavigationItemActive()
        {
            _renderer.Request = new DashboardRequestStub("GET", "/tags");

            var html = _renderer.Render(new TagsPage());

            Assert.Contains($"<div class=\"hf-nav-section\">{Encode(Strings.NavigationMenu_Section_Monitor)}</div>", html);
            Assert.Contains("<a href=\"/tags\" class=\"hf-nav-item active\">", html);
            Assert.Contains("<a href=\"/batches\" class=\"hf-nav-item \">", html);
            Assert.Contains("glyphicon glyphicon-tag\"", html);
        }

        [Fact]
        public void Render_Always_ListsTheJobStatesUnderTheJobsItemWithTheirMetrics()
        {
            _renderer.Request = new DashboardRequestStub("GET", "/jobs/failed");
            _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Failed = 7, Retries = 0 });

            var html = _renderer.Render(new FailedJobsPage());

            Assert.Contains("<a href=\"/jobs/enqueued\" class=\"hf-nav-item active\">", html);
            Assert.Contains("<ul class=\"hf-nav-sub\">", html);
            Assert.Contains("<a href=\"/jobs/failed\" class=\"hf-nav-item active\">", html);
            Assert.Contains("data-metric=\"failed:count-or-null\"", html);
            Assert.Contains(">7</span>", html);
        }

        [Fact, GlobalLock(Reason = "Changes NavigationMenu.Items and JobsSidebarMenu.Items")]
        public void Render_WithCustomMenuItems_SkipsNullItemsAndUsesTheDefaultIcon()
        {
            Func<RazorPage, MenuItem> nullItem = _ => null;
            Func<RazorPage, MenuItem> customItem = page => new MenuItem("Custom <Item>", page.Url.To("/custom"));
            NavigationMenu.Items.Add(nullItem);
            NavigationMenu.Items.Add(customItem);
            JobsSidebarMenu.Items.Add(nullItem);

            try
            {
                var html = _renderer.Render(new TagsPage());

                Assert.Contains("<a href=\"/custom\" class=\"hf-nav-item \">", html);
                Assert.Contains("glyphicon glyphicon-chevron-right\"", html);
                Assert.Contains("<span class=\"hf-nav-text\">Custom &lt;Item&gt;</span>", html);
            }
            finally
            {
                NavigationMenu.Items.Remove(nullItem);
                NavigationMenu.Items.Remove(customItem);
                JobsSidebarMenu.Items.Remove(nullItem);
            }
        }

        [Fact, GlobalLock(Reason = "Changes NavigationMenu.Items")]
        public void Render_WithoutMenuItems_LeavesTheNavigationEmpty()
        {
            var saved = NavigationMenu.Items.ToList();
            NavigationMenu.Items.Clear();

            try
            {
                var html = _renderer.Render(new TagsPage());

                Assert.DoesNotContain("hf-nav-section", html);
                Assert.DoesNotContain("hf-nav-item", html);
            }
            finally
            {
                NavigationMenu.Items.Clear();
                NavigationMenu.Items.AddRange(saved);
            }
        }

        private void SetupServers(params string[] names)
        {
            _renderer.Monitoring.Setup(x => x.Servers()).Returns(names.Select(static name => new ServerDto
            {
                Name = name,
                Heartbeat = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                Queues = new[] { "default" },
            }).ToList());
        }

        private void SetupControlHash(Dictionary<string, string> hash)
        {
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(hash);
        }

        private void SetupStorageTime(DateTime utcNow)
        {
            _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);
            _renderer.Connection.Setup(x => x.GetUtcDateTime()).Returns(utcNow);
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
