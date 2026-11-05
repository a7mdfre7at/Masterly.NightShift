using System;
using System.Linq;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.States;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class TimelinePageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly long _nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        [Fact]
        public void Render_WithoutActivity_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new TimelinePage());

            Assert.Contains($"{Encode(Strings.TimelinePage_Title)} <small class=\"text-muted\">{Encode(Strings.TimelinePage_Hint)}</small>", html);
            Assert.Contains($"<div class=\"alert alert-info\">{Encode(Strings.TimelinePage_Empty)}</div>", html);
            Assert.DoesNotContain("hf-tl-track", html);
            _renderer.Connection.Verify(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, ActivityFeed.MaxEntries - 1));
        }

        [Fact]
        public void Render_WithRuns_ShowsTheSummaryLegendAndTimeAxis()
        {
            SetupActivity(
                Entry(-60000, "1", ProcessingState.StateName, "srv-1:a", "worker-aaaa-1111"),
                Entry(-50000, "1", SucceededState.StateName));

            var html = _renderer.Render(new TimelinePage());

            Assert.Contains("<p class=\"text-muted hf-tl-summary\">", html);
            Assert.Contains("<i class=\"hf-tl-swatch hf-tl-held\"></i> " + Encode(Strings.TimelinePage_Legend_Held), html);
            Assert.Contains("<i class=\"hf-tl-swatch hf-tl-running\"></i> " + Encode(Strings.TimelinePage_Running), html);
            Assert.Contains("class=\"hf-tl-tick hf-tl-tick-first\" style=\"left: 0%\"", html);
            Assert.Contains("class=\"hf-tl-tick \" style=\"left: 50%\"", html);
            Assert.Contains("class=\"hf-tl-tick hf-tl-tick-last\" style=\"left: 100%\"", html);
            Assert.Contains(" UTC</span>", html);
            Assert.DoesNotContain(Encode(Strings.TimelinePage_Empty), html);
        }

        [Fact]
        public void Render_WithRuns_DrawsOneLanePerWorkerGroupedByServer()
        {
            SetupActivity(
                Entry(-60000, "1", ProcessingState.StateName, "srv-1:a", "worker-aaaa-1111"),
                Entry(-50000, "1", SucceededState.StateName),
                Entry(-45000, "2", ProcessingState.StateName, "srv-1:a", "worker-bbbb-2222"),
                Entry(-40000, "2", FailedState.StateName),
                Entry(-30000, "3", ProcessingState.StateName, "srv-2:b", "w3"),
                Entry(-20000, "3", DeletedState.StateName));

            var html = _renderer.Render(new TimelinePage());

            Assert.Equal(2, CountOf(html, "<div class=\"hf-tl-server\">"));
            Assert.Contains("title=\"srv-1:a\">srv-1</span>", html);
            Assert.Contains("<div class=\"hf-tl-label\" title=\"worker-aaaa-1111\"><code>worker-a</code></div>", html);
            Assert.Contains("<div class=\"hf-tl-label\" title=\"w3\"><code>w3</code></div>", html);
        }

        [Fact]
        public void Render_WithRuns_ColorsEachSpanByItsOutcome()
        {
            SetupActivity(
                Entry(-60000, "1", ProcessingState.StateName, "srv", "w1", "Billing.Charge"),
                Entry(-50000, "1", SucceededState.StateName),
                Entry(-45000, "2", ProcessingState.StateName, "srv", "w2"),
                Entry(-40000, "2", FailedState.StateName),
                Entry(-35000, "3", ProcessingState.StateName, "srv", "w3"),
                Entry(-30000, "3", DeletedState.StateName),
                Entry(-25000, "4", ProcessingState.StateName, "srv", "w4"),
                Entry(-20000, "4", ScheduledState.StateName),
                Entry(-10000, "5", ProcessingState.StateName, "srv", "w5"));

            var html = _renderer.Render(new TimelinePage());

            Assert.Contains("<a class=\"hf-tl-span hf-tl-succeeded\" href=\"/jobs/details/1\" style=\"left: 0%;", html);
            Assert.Contains("class=\"hf-tl-span hf-tl-failed\" href=\"/jobs/details/2\"", html);
            Assert.Contains("class=\"hf-tl-span hf-tl-deleted\" href=\"/jobs/details/3\"", html);
            Assert.Contains("class=\"hf-tl-span hf-tl-held\" href=\"/jobs/details/4\"", html);
            Assert.Contains("class=\"hf-tl-span hf-tl-running\" href=\"/jobs/details/5\"", html);
            Assert.Contains("title=\"" + Encode("Billing.Charge · Succeeded · "), html);
            Assert.Contains("title=\"" + Encode("4 · Scheduled · "), html);
            Assert.Contains("title=\"" + Encode($"5 · {Strings.TimelinePage_Running} · "), html);
        }

        [Fact]
        public void Render_WithRunsOlderThanTheWindow_ClipsThemToTheWindow()
        {
            SetupActivity(
                Entry(-(long)TimeSpan.FromHours(1).TotalMilliseconds, "1", ProcessingState.StateName, "srv", "w1"),
                Entry(-60000, "1", SucceededState.StateName));

            var html = _renderer.Render(new TimelinePage());

            Assert.Contains("class=\"hf-tl-span hf-tl-succeeded\" href=\"/jobs/details/1\" style=\"left: 0%; width: ", html);
        }

        private void SetupActivity(params ActivityEntry[] chronological)
        {
            var newestFirst = chronological.Reverse().Select(x => SerializationHelper.Serialize(x, SerializationOption.User)).ToList();
            _renderer.Connection.Setup(x => x.GetRangeFromList(ActivityFeed.ListKey, It.IsAny<int>(), It.IsAny<int>())).Returns(newestFirst);
        }

        private ActivityEntry Entry(long offsetMs, string jobId, string state, string serverId = null, string workerId = null, string method = null)
        {
            return new ActivityEntry { Timestamp = _nowMs + offsetMs, JobId = jobId, State = state, ServerId = serverId, WorkerId = workerId, Method = method };
        }

        private static int CountOf(string html, string value) => html.Split(new[] { value }, StringSplitOptions.None).Length - 1;

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
