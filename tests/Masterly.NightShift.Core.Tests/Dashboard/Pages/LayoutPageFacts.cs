using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class LayoutPageFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_WithDarkModeEnabled_EmitsTheThemeScriptStylesheetAndSwitcher()
    {
        _renderer.Options = new DashboardOptions { DarkModeEnabled = true };

        string html = _renderer.Render(new TagsPage());

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

        string html = _renderer.Render(new TagsPage());

        Assert.DoesNotContain("nightshift.theme", html);
        Assert.DoesNotContain("hfDarkStylesheet", html);
        Assert.DoesNotContain("js-theme", html);
        Assert.Contains("data-darkmode=\"false\"", html);
    }

    [Fact]
    public void Render_WithAppPath_ShowsTheBackLink()
    {
        _renderer.Options = new DashboardOptions { AppPath = "/my-app" };

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<a href=\"/my-app\">", html);
    }

    [Fact]
    public void Render_WithoutAppPath_ShowsTheVersionInTheSidebarFooter()
    {
        _renderer.Options = new DashboardOptions { AppPath = null };
        Version? version = typeof(RazorPage).Assembly.GetName().Version;

        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"<span>Masterly.NightShift {version.Major}.{version.Minor}.{version.Build}</span>", html);
        Assert.DoesNotContain(Encode(Strings.LayoutPage_Back), html);
    }

    [Fact]
    public void Render_Always_RendersTheTopbarWatchlistAndClientConfiguration()
    {
        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<title>" + Encode(Strings.TagsPage_Title) + " – ", html);
        Assert.Contains("data-live-url=\"/jobs/live\"", html);
        Assert.Contains("data-job-url=\"/jobs/details/\"", html);
        Assert.Contains($"data-text-title=\"{Encode(Strings.LayoutPage_Watchlist)}\"", html);
        Assert.Contains($"<span class=\"hf-search-text\">{Encode(Strings.LayoutPage_Search)}</span>", html);
        Assert.Contains("class=\"ns-icon-button js-page-refresh\"", html);
        Assert.Contains("id=\"errorAlert\"", html);
        Assert.Contains("data-pollurl=\"/stats\"", html);
        Assert.Contains("data-liveurl=\"/jobs/live\"", html);
        Assert.Contains($"data-text-live=\"{Encode(Strings.LayoutPage_LiveToggle)}\"", html);
    }

    [Fact]
    public void Render_WhenNothingIsPaused_ShowsNoDrainBanner()
    {
        string html = _renderer.Render(new TagsPage());

        Assert.DoesNotContain("hf-drain-banner", html);
    }

    [Fact]
    public void Render_WhenQueuesArePaused_ShowsTheQueuesBannerWithALinkToTheQueues()
    {
        SetupControlHash(new Dictionary<string, string> { { "Queue:critical", "maintenance" }, { "Queue:emails", "" } });

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("hf-drain-banner hf-drain-banner-queues", html);
        Assert.Contains(Encode(string.Format(Strings.LayoutPage_QueuesPaused, "critical, emails")), html);
        Assert.Contains("href=\"/jobs/enqueued\">" + Encode(Strings.LayoutPage_QueuesPaused_Link), html);
        Assert.DoesNotContain("href=\"/servers\">" + Encode(Strings.NavigationMenu_Servers) + "</a>", html);
    }

    [Fact]
    public void Render_WhenServersArePaused_ShowsTheServersBannerWithALinkToTheServers()
    {
        SetupServers("web-1");
        SetupControlHash(new Dictionary<string, string> { { "Server:web-1", "deploy" } });

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("hf-drain-banner hf-drain-banner-queues", html);
        Assert.Contains(Encode(string.Format(Strings.LayoutPage_ServersPaused, "web-1")), html);
        Assert.Contains("href=\"/servers\">" + Encode(Strings.NavigationMenu_Servers) + "</a>", html);
        Assert.DoesNotContain(Encode(Strings.LayoutPage_QueuesPaused_Link), html);
    }

    [Fact]
    public void Render_LeavesOutPausesOfServersThatAreGone()
    {
        SetupServers("web-1:2:new");
        SetupControlHash(new Dictionary<string, string> { { "Server:web-1:1:old", "deploy" }, { "Server:web-1:2:new", "" } });

        string html = _renderer.Render(new TagsPage());

        Assert.Contains(Encode(string.Format(Strings.LayoutPage_ServersPaused, "web-1:2:new")), html);
        Assert.DoesNotContain("web-1:1:old", html);
    }

    [Fact]
    public void Render_ShowsNoBanner_WhenOnlyServersThatAreGoneArePaused()
    {
        SetupControlHash(new Dictionary<string, string> { { "Server:web-1:1:old", "deploy" } });

        string html = _renderer.Render(new TagsPage());

        Assert.DoesNotContain("hf-drain-banner", html);
    }

    [Fact]
    public void Render_WhenDrainModeIsOn_ShowsTheBannerWithSinceReasonAndResumeButton_AndThePausedQueuesToo()
    {
        SetupControlHash(new Dictionary<string, string>
        {
            { "Drain", "1" },
            { "DrainSince", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-3)) },
            { "DrainReason", "Database <upgrade>" },
            { "Queue:critical", "ignored while draining" }
        });

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<div class=\"ns-banner ns-banner-warning hf-drain-banner\" role=\"status\">", html);
        Assert.Contains($"<strong>{Encode(Strings.LayoutPage_Drain_Banner)}</strong>", html);
        Assert.Contains("class=\"hf-drain-banner-since\"", html);
        Assert.Contains("<em class=\"hf-drain-banner-reason\">Database &lt;upgrade&gt;</em>", html);
        Assert.Contains("<button type=\"button\" class=\"btn ns-btn-tonal\" data-ajax=\"/maintenance/resume\"", html);
        Assert.Contains("hf-drain-banner-queues", html);
        Assert.Contains(Encode(string.Format(Strings.LayoutPage_QueuesPaused, "critical")), html);
    }

    [Fact]
    public void Render_WhenDrainModeIsOnAndTheDashboardIsReadOnly_ShowsTheBannerWithoutResumeButton()
    {
        SetupControlHash(new Dictionary<string, string> { { "Drain", "1" } });
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new TagsPage());

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

        string html = _renderer.Render(new HomePage());

        Assert.Contains("js-page-container", html);
        Assert.DoesNotContain("hf-drain-banner", html);
        Assert.Contains("<div class=\"ns-banner ns-banner-error hf-storage-banner\" role=\"alert\">", html);
        Assert.Contains(Encode(Strings.LayoutPage_StorageUnavailable), html);
    }

    [Fact]
    public void Render_HandsTheWatchlistTheNamesOfTheBuiltInStates_InTheReadersLanguage()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        string html;
        try
        {
            CultureHelper.SetCurrentUICulture("de");
            html = _renderer.Render(new TagsPage());
        }
        finally
        {
            CultureHelper.SetCurrentUICulture(previous);
        }

        Group names = Regex.Match(html, "<div class=\"hf-watchlist js-watchlist\"[^>]* data-state-names=\"([^\"]+)\"").Groups[1];
        Assert.True(names.Success);
        Dictionary<string, string> parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(WebUtility.HtmlDecode(names.Value))!;
        Assert.Equal("Erfolgreich", parsed["Succeeded"]);
        Assert.Equal(7, parsed.Count);
    }

    [Fact]
    public void Render_SaysWhyNoServerIsAProblem_InTheTooltipOfTheServersCount_InTheReadersLanguage()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        string html;
        string tooltip;
        try
        {
            CultureHelper.SetCurrentUICulture("de");
            html = _renderer.Render(new TagsPage());
            tooltip = Strings.Metrics_NoServers_Title;
        }
        finally
        {
            CultureHelper.SetCurrentUICulture(previous);
        }

        Assert.Equal("Keine aktiven Server gefunden. Jobs werden nicht verarbeitet.", tooltip);
        Assert.Contains($"<span data-metric=\"servers:count\" class=\"metric metric-warning highlighted\" title=\"{Encode(tooltip)}\">0</span>", html);
    }

    [Fact]
    public void Render_GivesTheServersCount_NoTooltip_WhileServersRun()
    {
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Servers = 2 });

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<span data-metric=\"servers:count\" class=\"metric metric-default \">2</span>", html);
    }

    [Fact]
    public void Render_WhenStorageDisplayIsOn_ListsTheStorageInTheFooter()
    {
        _renderer.Storage.Setup(x => x.ToString()).Returns("Test <Storage>");
        _renderer.Options = new DashboardOptions { DisplayStorageConnectionString = true };

        string html = _renderer.Render(new TagsPage());

        Assert.Contains("<li class=\"list-inline-item\">Test &lt;Storage&gt; ", html);
    }

    [Fact]
    public void Render_WhenStorageDisplayIsOff_LeavesTheStorageOutOfTheFooter()
    {
        _renderer.Storage.Setup(x => x.ToString()).Returns("Test <Storage>");
        _renderer.Options = new DashboardOptions { DisplayStorageConnectionString = false };

        string html = _renderer.Render(new TagsPage());

        Assert.DoesNotContain("Test &lt;Storage&gt;", html);
    }

    [Fact]
    public void Render_WhenTheStorageReportsItsTime_ShowsItInTheFooter()
    {
        SetupStorageTime(DateTime.UtcNow);

        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"<li class=\"list-inline-item\">{Encode(Strings.LayoutPage_Footer_StorageTime)} <span data-moment-local=", html);
        Assert.DoesNotContain(Encode(Strings.LayoutPage_Footer_TimeIsOutOfSync), html);
    }

    [Fact]
    public void Render_WhenTheStorageClockIsOutOfSync_WarnsInTheFooter()
    {
        SetupStorageTime(DateTime.UtcNow.AddMinutes(-5));

        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"<span class=\"text-warning\" title=\"{Encode(Strings.LayoutPage_Footer_TimeIsOutOfSync)}\">", html);
    }

    [Fact]
    public void Render_WhenTheStorageCantReportItsTime_ShowsOnlyTheApplicationTime()
    {
        string html = _renderer.Render(new TagsPage());

        Assert.DoesNotContain(Encode(Strings.LayoutPage_Footer_StorageTime), html);
        Assert.Contains($"<span>{Encode(Strings.LayoutPage_Footer_Time)} <span data-moment-local=", html);
    }

    [Fact]
    public void Render_OnASection_MarksItsNavigationItemActive()
    {
        _renderer.Request = new DashboardRequestStub("GET", "/tags");

        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"<div class=\"hf-nav-section\" id=\"hfNavGroup1\">{Encode(Strings.NavigationMenu_Group_Jobs)}</div>", html);
        Assert.Contains("<a href=\"/tags\" class=\"hf-nav-item active\" aria-current=\"page\">", html);
        Assert.Contains("<a href=\"/batches\" class=\"hf-nav-item\">", html);
        Assert.Single(Regex.Matches(html, "aria-current=\"page\""));
        Assert.Contains(DashboardMarkup.Icon("tag-fill", "hf-nav-icon"), html);
    }

    [Fact]
    public void Render_Always_ListsTheJobStatesUnderTheJobsItemWithTheirMetrics()
    {
        _renderer.Request = new DashboardRequestStub("GET", "/jobs/failed");
        _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Failed = 7, Retries = 0 });

        string html = _renderer.Render(new FailedJobsPage());

        // One active indicator: the state's own item; the Jobs item is only marked as its parent
        Assert.Contains("<a href=\"/jobs/enqueued\" class=\"hf-nav-item hf-nav-item-parent\">", html);
        Assert.Contains("<ul class=\"hf-nav-sub\">", html);
        Assert.Contains("<a href=\"/jobs/failed\" class=\"hf-nav-item active\" aria-current=\"page\">", html);
        Assert.Single(Regex.Matches(html, "class=\"hf-nav-item active\""));
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
            string html = _renderer.Render(new TagsPage());

            Assert.Contains("<a href=\"/custom\" class=\"hf-nav-item\">", html);
            Assert.Contains(DashboardMarkup.Icon("chevron-right", "hf-nav-icon"), html);
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
    public void Render_WithMenuItemIcons_TakesSpriteNames_GlyphiconNamesItReplaced_AndFallsBackToTheChevron()
    {
        Func<RazorPage, MenuItem> spriteName = page => new MenuItem("Sprite", page.Url.To("/sprite")) { Icon = "card-list" };
        Func<RazorPage, MenuItem> glyphiconName = page => new MenuItem("Glyphicon", page.Url.To("/glyphicon")) { Icon = "time" };
        Func<RazorPage, MenuItem> unknownName = page => new MenuItem("Unknown", page.Url.To("/unknown")) { Icon = "no-such-icon" };
        NavigationMenu.Items.Add(spriteName);
        NavigationMenu.Items.Add(glyphiconName);
        NavigationMenu.Items.Add(unknownName);

        try
        {
            string html = _renderer.Render(new TagsPage());

            Assert.Matches("href=\"/sprite\" class=\"hf-nav-item\">\\s*" + Regex.Escape(DashboardMarkup.Icon("card-list", "hf-nav-icon")), html);
            Assert.Matches("href=\"/glyphicon\" class=\"hf-nav-item\">\\s*" + Regex.Escape(DashboardMarkup.Icon("clock", "hf-nav-icon")), html);
            Assert.Matches("href=\"/unknown\" class=\"hf-nav-item\">\\s*" + Regex.Escape(DashboardMarkup.Icon("chevron-right", "hf-nav-icon")), html);
            Assert.DoesNotContain("no-such-icon", html);
        }
        finally
        {
            NavigationMenu.Items.Remove(spriteName);
            NavigationMenu.Items.Remove(glyphiconName);
            NavigationMenu.Items.Remove(unknownName);
        }
    }

    [Fact]
    public void Render_Always_StartsWithASkipLinkToTheContent()
    {
        string html = _renderer.Render(new TagsPage());

        int skip = html.IndexOf($"<a class=\"ns-skip-link\" href=\"#content\">{Encode(Strings.LayoutPage_SkipToContent)}</a>", StringComparison.Ordinal);
        Assert.True(skip > 0);
        Assert.True(skip < html.IndexOf("class=\"hf-nav\"", StringComparison.Ordinal));
        Assert.Contains("<main id=\"content\" class=\"hf-content js-page-container\" tabindex=\"-1\">", html);
    }

    [Fact]
    public void Render_Always_GivesTheMenuButtonItsDrawer_AndTheDrawerACloseButton()
    {
        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"aria-label=\"{Encode(Strings.LayoutPage_Menu)}\" aria-expanded=\"false\" aria-controls=\"hfSidebar\"", html);
        Assert.Contains($"<div class=\"hf-sidebar\" id=\"hfSidebar\" data-label=\"{Encode(Strings.LayoutPage_Navigation)}\">", html);
        Assert.Contains($"<aside class=\"hf-sidebar-landmark\" aria-label=\"{Encode(Strings.LayoutPage_Navigation)}\">", html);
        Assert.Contains($"class=\"ns-icon-button hf-sidebar-close js-sidebar-close\" aria-label=\"{Encode(Strings.LayoutPage_CloseMenu)}\"", html);
        Assert.Contains($"<nav class=\"hf-nav\" aria-label=\"{Encode(Strings.LayoutPage_Navigation)}\">", html);
        Assert.Contains("class=\"hf-scrim js-sidebar-scrim\" hidden", html);
    }

    [Fact]
    public void Render_Always_ListsTheMenuInItsGroups()
    {
        string html = _renderer.Render(new TagsPage());

        int jobs = html.IndexOf($">{Encode(Strings.NavigationMenu_Group_Jobs)}</div>", StringComparison.Ordinal);
        int workflows = html.IndexOf($">{Encode(Strings.NavigationMenu_Group_Workflows)}</div>", StringComparison.Ordinal);
        int observe = html.IndexOf($">{Encode(Strings.NavigationMenu_Group_Observe)}</div>", StringComparison.Ordinal);
        int system = html.IndexOf($">{Encode(Strings.NavigationMenu_Group_System)}</div>", StringComparison.Ordinal);

        Assert.True(jobs > 0 && jobs < workflows && workflows < observe && observe < system);
        Assert.InRange(html.IndexOf("href=\"/batches\" class", StringComparison.Ordinal), workflows, observe);
        Assert.InRange(html.IndexOf("href=\"/servers\" class", StringComparison.Ordinal), system, html.Length);
        Assert.DoesNotContain($">{Encode(Strings.NavigationMenu_Group_More)}</div>", html);
    }

    [Fact]
    public void Render_ShowsTheLiveStatusChip_AndTheReadOnlyChip_OnlyWhenReadOnly()
    {
        string html = _renderer.Render(new TagsPage());
        Assert.Contains("class=\"ns-chip ns-chip-status hf-status js-status\" role=\"status\"", html);
        Assert.DoesNotContain("hf-readonly-chip", html);

        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
        string readOnly = _renderer.Render(new TagsPage());
        Assert.Contains("hf-readonly-chip", readOnly);
        Assert.Contains(Encode(Strings.LayoutPage_ReadOnly), readOnly);
        Assert.Contains("data-readonly=\"true\"", readOnly);
    }

    [Theory]
    [InlineData("", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("de", "de")]
    [InlineData("pt-BR", "pt-BR")]
    public void Render_NamesTheLanguageItsTextIsIn_InTheLangAttribute(string culture, string expected)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        string html;
        try
        {
            CultureHelper.SetCurrentUICulture(culture);
            html = _renderer.Render(new TagsPage());
        }
        finally
        {
            CultureHelper.SetCurrentUICulture(previous);
        }

        Assert.Contains($"<html lang=\"{expected}\"", html);
    }

    [Fact]
    public void Render_WithDarkModeDisabled_PinsTheLightTheme_SoTheSystemPreferenceDoesntApply()
    {
        _renderer.Options = new DashboardOptions { DarkModeEnabled = false };
        Assert.Contains("<html lang=\"en\" data-theme=\"light\">", _renderer.Render(new TagsPage()));

        _renderer.Options = new DashboardOptions { DarkModeEnabled = true };
        Assert.DoesNotContain("data-theme=\"light\"", _renderer.Render(new TagsPage()));
    }

    [Fact]
    public void Render_Always_GivesTheScriptsTheTextsOfTheDialogsAndSnackbars()
    {
        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"data-text-cancel=\"{Encode(Strings.Common_Cancel)}\"", html);
        Assert.Contains($"data-text-type-to-confirm=\"{Encode(Strings.Dialog_TypeToConfirm)}\"", html);
        Assert.Contains($"data-text-discard-title=\"{Encode(Strings.Dialog_DiscardTitle)}\"", html);
        Assert.Contains($"data-text-palette-placeholder=\"{Encode(Strings.Palette_Placeholder)}\"", html);
        Assert.Contains($"data-text-palette-nothing=\"{Encode(Strings.Palette_NothingFound)}\"", html);
        Assert.Contains($"data-text-shortcut-search=\"{Encode(Strings.Shortcuts_Search)}\"", html);
        Assert.Contains($"data-text-remove=\"{Encode(Strings.LayoutPage_Watchlist_Remove)}\"", html);
        Assert.Contains("class=\"ns-snackbar-host js-snackbar-host\" role=\"status\" aria-live=\"polite\"", html);
    }

    [Fact]
    public void Render_Always_GivesTheScriptsTheIconSpriteUrl_AndNoGlyphicons()
    {
        string html = _renderer.Render(new TagsPage());

        Assert.Contains($"data-icons-url=\"{DashboardIcons.Path}\"", html);
        Assert.DoesNotContain("glyphicon", html);
    }

    [Fact, GlobalLock(Reason = "Changes NavigationMenu.Items")]
    public void Render_WithoutMenuItems_LeavesTheNavigationEmpty()
    {
        List<Func<RazorPage, MenuItem>> saved = [.. NavigationMenu.Items];
        NavigationMenu.Items.Clear();

        try
        {
            string html = _renderer.Render(new TagsPage());

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
        _renderer.Monitoring.Setup(x => x.Servers()).Returns([.. names.Select(static name => new ServerDto
        {
            Name = name,
            Heartbeat = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            Queues = ["default"],
        })]);
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
