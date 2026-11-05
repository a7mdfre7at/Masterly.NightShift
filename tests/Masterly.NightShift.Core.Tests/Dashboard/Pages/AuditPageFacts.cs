using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class AuditPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutEntries_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new AuditPage());

            Assert.Contains($"<div class=\"alert alert-info\">{Encode(Strings.AuditPage_NoEntries)}</div>", html);
            Assert.DoesNotContain(Encode(Strings.AuditPage_Disabled), html);
            Assert.DoesNotContain("hf-audit", html);
        }

        [Fact, GlobalLock(Reason = "Changes AuditLog.Enabled")]
        public void Render_WhenRecordingIsOff_WarnsAboutIt()
        {
            AuditLog.Enabled = false;

            try
            {
                var html = _renderer.Render(new AuditPage());

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

            var html = _renderer.Render(new AuditPage());

            Assert.Contains("<span class=\"hf-catalog-summary text-muted\">1 / 3</span>", html);
            Assert.Contains("<a class=\"btn btn-sm btn-primary active\" href=\"/audit?count=100\">100</a>", html);
            Assert.Contains("<a class=\"btn btn-sm btn-default\" href=\"/audit?count=50\">50</a>", html);
            Assert.Contains("<a class=\"btn btn-sm btn-default\" href=\"/audit?count=1000\">1000</a>", html);
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

            var html = _renderer.Render(new AuditPage());

            Assert.Contains($"<a class=\"btn btn-sm btn-primary active\" href=\"/audit?count={expected}\">{expected}</a>", html);
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
                Targets = new[] { "12", "abc" },
                Detail = "partner API is back",
                Status = 204
            });

            var html = _renderer.Render(new AuditPage());

            Assert.Contains("<strong>alice &lt;admin&gt;</strong>", html);
            Assert.Contains("<div class=\"text-muted hf-audit-ip\">10.0.0.1</div>", html);
            Assert.Contains("<span class=\"\">Requeue failed jobs</span>", html);
            Assert.Contains("<code>/jobs/failed/requeue</code>", html);
            Assert.Contains("<a href=\"/jobs/details/12\">#12</a>", html);
            Assert.Contains("<code>abc</code>", html);
            Assert.Contains("<td class=\"word-break text-muted\">partner API is back</td>", html);
            Assert.DoesNotContain(Encode(Strings.AuditPage_Anonymous), html);
            Assert.DoesNotContain("label label-danger", html);
        }

        [Fact]
        public void Render_WithAnAnonymousFailedAction_ShowsTheStatusAndTheHiddenTargets()
        {
            SetupEntries(1, new AuditEntry
            {
                Timestamp = Now(),
                Action = "/catalog/run",
                Targets = new[] { "99" },
                TargetCount = 30,
                Status = 500
            });

            var html = _renderer.Render(new AuditPage());

            Assert.Contains($"<em class=\"text-muted\">{Encode(Strings.AuditPage_Anonymous)}</em>", html);
            Assert.DoesNotContain("hf-audit-ip", html);
            Assert.Contains("<span class=\"text-danger\">", html);
            Assert.Contains("<span class=\"label label-danger\">500</span>", html);
            Assert.Contains("<a href=\"/jobs/details/99\">#99</a>", html);
            Assert.Contains($"<span class=\"text-muted\">{Encode(String.Format(Strings.AuditPage_More, 29))}</span>", html);
        }

        [Fact]
        public void Render_WithNumericTargetsOfOtherActions_ShowsThemAsCode()
        {
            SetupEntries(1, new AuditEntry { Timestamp = Now(), Action = "/recurring/remove", Targets = new[] { "42" } });

            var html = _renderer.Render(new AuditPage());

            Assert.Contains("<code>42</code>", html);
            Assert.DoesNotContain("/jobs/details/42", html);
        }

        [Fact]
        public void Render_WithAnEntryWithoutTargets_LeavesTheTargetsCellEmpty()
        {
            SetupEntries(1, new AuditEntry { Timestamp = Now(), Action = "/maintenance/drain" });

            var html = _renderer.Render(new AuditPage());

            Assert.Contains("<code>/maintenance/drain</code>", html);
            Assert.DoesNotContain(Encode(String.Format(Strings.AuditPage_More, 0)), html);
        }

        private void SetupEntries(long total, params AuditEntry[] entries)
        {
            _renderer.Connection.Setup(x => x.GetRangeFromList(AuditLog.ListKey, It.IsAny<int>(), It.IsAny<int>()))
                .Returns(entries.Select(x => SerializationHelper.Serialize(x, SerializationOption.User)).ToList());
            _renderer.Connection.Setup(x => x.GetListCount(AuditLog.ListKey)).Returns(total);
        }

        private static long Now() => JobHelper.ToTimestamp(DateTime.UtcNow);

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
