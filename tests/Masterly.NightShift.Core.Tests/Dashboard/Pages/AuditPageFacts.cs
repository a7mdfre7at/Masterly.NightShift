using System.Globalization;
using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class AuditPageFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_WithoutEntries_ShowsTheEmptyMessage()
    {
        string html = _renderer.Render(new AuditPage());

        Assert.Contains($"<h2 class=\"ns-empty-state-title\">{Encode(Strings.AuditPage_EmptyTitle)}</h2><p>{Encode(Strings.AuditPage_NoEntries)}</p>", html);
        Assert.DoesNotContain(Encode(Strings.AuditPage_Disabled), html);
        Assert.DoesNotContain("js-audit-entry", html);
        Assert.DoesNotContain("js-audit-filter", html);
        PageStructure.AssertHeadingsInOrder(html);
    }

    [Fact, GlobalLock(Reason = "Changes AuditLog.Enabled")]
    public void Render_WhenRecordingIsOff_WarnsAboutIt()
    {
        AuditLog.Enabled = false;

        try
        {
            string html = _renderer.Render(new AuditPage());

            Assert.Contains($"<div class=\"alert alert-warning\">{Encode(Strings.AuditPage_Disabled)}</div>", html);
        }
        finally
        {
            AuditLog.Enabled = true;
        }
    }

    [Fact]
    public void Render_WithEntries_ShowsTheSummaryAndPageSizes()
    {
        SetupEntries(3, new AuditEntry { Action = "/tags/forget/x", Timestamp = Now() });

        string html = _renderer.Render(new AuditPage());

        Assert.Contains("<span class=\"hf-catalog-summary\">1 / 3</span>", html);
        Assert.Contains("<span class=\"ns-page-name\">" + Encode(Strings.AuditPage_Title) + "</span> <span class=\"ns-count-chip js-list-count\">3</span></h1>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-filter selected\" href=\"/audit?count=100\" aria-current=\"page\">100</a>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-filter\" href=\"/audit?count=50\">50</a>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-filter\" href=\"/audit?count=1000\">1000</a>", html);
        Assert.Contains($"<nav class=\"ns-audit-count js-audit-count\" aria-label=\"{Encode(Strings.AuditPage_Show)}\">", html);
        Assert.Contains($"<label for=\"auditFilter\" class=\"visually-hidden\">{Encode(Strings.AuditPage_Filter)}</label>", html);
        Assert.Contains("<input type=\"checkbox\" class=\"js-audit-failed\" />", html);
        Assert.Contains("<div class=\"ns-empty-state ns-empty-state-compact js-audit-nomatch\" hidden=\"hidden\">", html);
        PageStructure.AssertHeadingsInOrder(html);
        PageStructure.AssertNoEmptyHeaderCells(html);
        _renderer.Connection.Verify(x => x.GetRangeFromList(AuditLog.ListKey, 0, 99));
    }

    [Theory]
    [InlineData("250", 250)]
    [InlineData("-5", 100)]
    [InlineData("oops", 100)]
    [InlineData("5000", 1000)]
    public void Render_WithACountInTheQuery_ReadsThatManyEntriesUpToTheLimit(string count, int expected)
    {
        SetupEntries(1, new AuditEntry { Action = "/tags/forget/x", Timestamp = Now() });
        _renderer.Request.Query["count"] = count;

        string html = _renderer.Render(new AuditPage());

        Assert.Contains($"<a class=\"ns-chip ns-chip-filter selected\" href=\"/audit?count={expected}\" aria-current=\"page\">{expected}</a>", html);
        _renderer.Connection.Verify(x => x.GetRangeFromList(AuditLog.ListKey, 0, expected - 1));
    }

    [Fact]
    public void Render_WithAnIdentifiedUser_ShowsTheUserTheAddressAndTheAction()
    {
        SetupEntries(1, new AuditEntry
        {
            Timestamp = Now(),
            User = "alice <admin>",
            RemoteIpAddress = "10.0.0.1",
            Action = "/jobs/failed/requeue",
            Targets = ["12", "abc"],
            Detail = "partner API is back",
            Status = 204
        });

        string html = _renderer.Render(new AuditPage());

        Assert.Contains("<span class=\"ns-chip ns-chip-status ns-audit-user\"><strong>alice &lt;admin&gt;</strong></span>", html);
        Assert.Contains("<span class=\"hf-audit-ip\">10.0.0.1</span>", html);
        Assert.Contains("<span class=\"ns-audit-action\">Requeue failed jobs</span>", html);
        Assert.Contains("<code>/jobs/failed/requeue</code>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"/jobs/details/12\">#12</a>", html);
        Assert.Contains("<code>abc</code>", html);
        Assert.Contains("<p class=\"ns-audit-detail\">partner API is back</p>", html);
        Assert.Contains("data-failed=\"false\"", html);
        Assert.Contains("<span class=\"ns-dot ns-state-succeeded\" aria-hidden=\"true\"></span>", html);
        Assert.Contains("<span class=\"ns-time-exact\"><span data-moment-local=", html);
        Assert.DoesNotContain(Encode(Strings.AuditPage_Anonymous), html);
        Assert.DoesNotContain("data-state=\"Failed\"", html);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Render_ForReadOnlyUsers_ShowsTheAddresses_OnlyWhenTheOptionsSaySo(bool display, bool expected)
    {
        // Every operator's address was shown to anyone allowed to look
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true, DisplayAuditIpAddressesToReadOnlyUsers = display };
        SetupEntries(1, new AuditEntry { Timestamp = Now(), User = "alice", RemoteIpAddress = "10.0.0.1", Action = "/jobs/failed/requeue", Status = 204 });

        string html = _renderer.Render(new AuditPage());

        Assert.Contains("<strong>alice</strong>", html);
        Assert.Equal(expected, html.Contains("10.0.0.1"));
    }

    [Fact]
    public void Render_WithAnAnonymousFailedAction_ShowsTheStatusAndTheHiddenTargets()
    {
        SetupEntries(1, new AuditEntry
        {
            Timestamp = Now(),
            Action = "/catalog/run",
            Targets = ["99"],
            TargetCount = 30,
            Status = 500
        });

        string html = _renderer.Render(new AuditPage());

        Assert.Contains($"<span class=\"ns-chip ns-chip-status ns-audit-user\"><em>{Encode(Strings.AuditPage_Anonymous)}</em></span>", html);
        Assert.DoesNotContain("hf-audit-ip", html);
        Assert.Contains("<li class=\"ns-run js-audit-entry ns-audit-failed\"", html);
        Assert.Contains("data-failed=\"true\"", html);
        Assert.Contains("<span class=\"ns-state-chip ns-state-failed\" data-state=\"Failed\">" + DashboardMarkup.Icon("x-circle-fill") + $"<span>{Encode($"{Strings.JobsSidebarMenu_Failed} · 500")}</span></span>", html);
        Assert.Contains("<a class=\"ns-chip ns-chip-assist\" href=\"/jobs/details/99\">#99</a>", html);
        Assert.Contains($"<span class=\"ns-audit-more\">{Encode(string.Format(Strings.AuditPage_More, 29))}</span>", html);
    }

    [Fact]
    public void Render_WithAFailedAction_SaysFailedInTheReadersLanguage_BesideTheStatus()
    {
        SetupEntries(1, new AuditEntry { Timestamp = Now(), Action = "/jobs/failed/requeue", Targets = ["7"], Status = 422 });
        CultureInfo previous = CultureInfo.CurrentUICulture;
        string html;
        try
        {
            CultureHelper.SetCurrentUICulture("de");
            html = _renderer.Render(new AuditPage());
        }
        finally
        {
            CultureHelper.SetCurrentUICulture(previous);
        }

        Assert.Contains($"data-state=\"Failed\">{DashboardMarkup.Icon("x-circle-fill")}<span>{Encode("Fehler · 422")}</span></span>", html);
        Assert.DoesNotContain("<span>Failed", html);
    }

    [Fact]
    public void Render_WithNumericTargetsOfOtherActions_ShowsThemAsCode()
    {
        SetupEntries(1, new AuditEntry { Timestamp = Now(), Action = "/recurring/remove", Targets = ["42"] });

        string html = _renderer.Render(new AuditPage());

        Assert.Contains("<code>42</code>", html);
        Assert.DoesNotContain("/jobs/details/42", html);
    }

    [Fact]
    public void Render_WithAnEntryWithoutTargets_LeavesTheTargetsCellEmpty()
    {
        SetupEntries(1, new AuditEntry { Timestamp = Now(), Action = "/maintenance/drain" });

        string html = _renderer.Render(new AuditPage());

        Assert.Contains("<code>/maintenance/drain</code>", html);
        Assert.DoesNotContain(Encode(string.Format(Strings.AuditPage_More, 0)), html);
        Assert.DoesNotContain("ns-audit-targets", html);
    }

    private void SetupEntries(long total, params AuditEntry[] entries)
    {
        _renderer.Connection.Setup(x => x.GetRangeFromList(AuditLog.ListKey, It.IsAny<int>(), It.IsAny<int>()))
            .Returns([.. entries.Select(x => SerializationHelper.Serialize(x, SerializationOption.User))]);
        _renderer.Connection.Setup(x => x.GetListCount(AuditLog.ListKey)).Returns(total);
    }

    private static long Now() => JobHelper.ToTimestamp(DateTime.UtcNow);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
