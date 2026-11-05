using System;
using System.Collections.Generic;
using System.Globalization;
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
    public class WallboardPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly List<ServerDto> _servers = new List<ServerDto>();
        private readonly List<QueueWithTopEnqueuedJobsDto> _queues = new List<QueueWithTopEnqueuedJobsDto>();
        private readonly Dictionary<string, string> _control = new Dictionary<string, string>();

        public WallboardPageFacts()
        {
            _renderer.Monitoring.Setup(x => x.Servers()).Returns(_servers);
            _renderer.Monitoring.Setup(x => x.Queues()).Returns(_queues);
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(_control);
        }

        [Fact]
        public void Render_ShowsAHealthyBoard_WithoutTheLayout()
        {
            AddServer("srv-1", DateTime.UtcNow, "default");
            _queues.Add(Queue("default", 0));
            _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Processing = 3, Enqueued = 1234, Scheduled = 2, Failed = 0 });

            var html = _renderer.Render(new WallboardPage());

            Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
            Assert.DoesNotContain("navbar", html);
            Assert.Contains("<meta http-equiv=\"refresh\" content=\"10\">", html);
            Assert.Contains("<body class=\"hf-app hf-wallboard hf-wb-ok\">", html);
            Assert.Contains("<span class=\"hf-wb-status-dot\"></span>" + H(Strings.WallboardPage_Healthy), html);
            Assert.Contains("<title>" + H(Strings.WallboardPage_Title) + " – Masterly.NightShift Dashboard</title>", html);
            Assert.Contains("<div class=\"hf-wb-value\">1</div>", html);
            Assert.Contains(H(String.Format(Strings.WallboardPage_Workers, 5)), html);
            Assert.Contains("<div class=\"hf-wb-value\">" + 1234L.ToString("N0", CultureInfo.CurrentCulture) + "</div>", html);
            Assert.Contains("<section class=\"hf-wb-tile \">", html);
            Assert.Contains("hf-wb-attention hf-wb-attention-ok", html);
            Assert.Contains(H(Strings.WallboardPage_NothingToReport), html);
            Assert.Contains(H(Strings.WallboardPage_NothingRunning), html);
            Assert.Contains("<div class=\"hf-wb-value\">–</div>", html);
            Assert.Contains("<div class=\"hf-wb-queue \">", html);
            Assert.Contains("<span class=\"hf-wb-queue-count\">0</span>", html);
            Assert.Contains("href=\"/\">" + H(Strings.WallboardPage_Exit), html);
        }

        [Fact]
        public void Render_UsesTheDefaultTitle_WhenTheDashboardTitleContainsHtml()
        {
            _renderer.Options = new DashboardOptions { DashboardTitle = "<b>Ops</b>" };

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("– Masterly.NightShift Dashboard</title>", html);
            Assert.Contains("<span><b>Ops</b></span>", html);
        }

        [Fact]
        public void Render_UsesTheDashboardTitle_WhenItIsPlainText()
        {
            _renderer.Options = new DashboardOptions { DashboardTitle = "Ops board" };

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("– Ops board</title>", html);
        }

        [Fact]
        public void Render_ShowsADegradedBoard_AndListsWhatNeedsAttention()
        {
            AddServer("srv-1", DateTime.UtcNow, "default");
            AddServer("srv-2", DateTime.UtcNow.AddMinutes(-10), "default");

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("hf-wallboard hf-wb-warn", html);
            Assert.Contains(H(Strings.WallboardPage_Degraded), html);
            Assert.DoesNotContain("hf-wb-attention-ok", html);
            Assert.Contains("<li>servers: 1 alive, 1 missed their heartbeat</li>", html);
            Assert.Contains(H(String.Format(Strings.WallboardPage_Workers, 5)), html);
        }

        [Fact]
        public void Render_ShowsAnUnhealthyBoard_WithoutNumbers_WhenTheStorageDoesNotAnswer()
        {
            _renderer.Monitoring.Setup(x => x.GetStatistics()).Throws(new InvalidOperationException("storage is down"));

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("hf-wallboard hf-wb-down", html);
            Assert.Contains(H(Strings.WallboardPage_Unhealthy), html);
            Assert.Contains("<li>storage: InvalidOperationException: storage is down</li>", html);
            Assert.Equal(5, CountOf(html, "<div class=\"hf-wb-value\">–</div>"));
            _renderer.Monitoring.Verify(x => x.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public void Render_StillListsRunningJobs_WhenTheBoardIsUnhealthyOnlyBecauseOfServers()
        {
            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("hf-wb-down", html);
            Assert.Contains("<li>servers: no servers are registered</li>", html);
            _renderer.Monitoring.Verify(x => x.ProcessingJobs(0, 8), Times.Once);
        }

        [Fact]
        public void Render_ShowsALowSuccessRate_AsBad()
        {
            var now = DateTime.UtcNow;
            _renderer.Monitoring.Setup(x => x.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long> { { now, 80 }, { now.AddHours(-1), 10 }, { now.AddHours(-2), 1000 } });
            _renderer.Monitoring.Setup(x => x.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long> { { now, 10 } });

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("<section class=\"hf-wb-tile hf-wb-tile-bad\">", html);
            Assert.Contains("<div class=\"hf-wb-value\">" + 90.0.ToString("0.#", CultureInfo.CurrentCulture) + "%</div>", html);
            Assert.Contains(100L.ToString("N0", CultureInfo.CurrentCulture) + " " + H(Strings.WallboardPage_Finished), html);
        }

        [Fact]
        public void Render_ShowsAHighSuccessRate_AsGood()
        {
            _renderer.Monitoring.Setup(x => x.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long> { { DateTime.UtcNow, 50 } });

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("<section class=\"hf-wb-tile hf-wb-tile-good\">", html);
            Assert.Contains("<div class=\"hf-wb-value\">100%</div>", html);
        }

        [Fact]
        public void Render_MarksTheFailedTile_WhenThereAreFailedJobs()
        {
            _renderer.Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto { Failed = 7 });

            var html = _renderer.Render(new WallboardPage());

            Assert.Equal(1, CountOf(html, "hf-wb-tile-bad"));
            Assert.Contains("<div class=\"hf-wb-value\">7</div>", html);
        }

        [Fact]
        public void Render_ShowsEachQueue_WithItsLengthPauseAndDrainEstimate()
        {
            AddServer("srv-1", DateTime.UtcNow, "critical", "default", "low", "idle");
            _queues.Add(Queue("default", 5));
            _queues.Add(Queue("critical", 10));
            _queues.Add(Queue("low", 3));
            _queues.Add(Queue("idle", 0));
            _control.Add(DrainMode.QueueFieldPrefix + "critical", "migration");
            AddQueueHealth("critical", startedPerMinute: 5);
            AddQueueHealth("default", startedPerMinute: 0);

            var html = _renderer.Render(new WallboardPage());

            Assert.True(html.IndexOf(">critical ", StringComparison.Ordinal) < html.IndexOf(">default ", StringComparison.Ordinal));
            Assert.True(html.IndexOf(">default ", StringComparison.Ordinal) < html.IndexOf(">low ", StringComparison.Ordinal));
            Assert.Equal(1, CountOf(html, "<div class=\"hf-wb-queue hf-wb-queue-paused\">"));
            Assert.Contains("<span class=\"hf-wb-pill\">" + H(Strings.QueuesPage_Paused) + "</span>", html);
            Assert.Contains("<span style=\"width: 100%\"></span>", html);
            Assert.Contains("<span style=\"width: 50%\"></span>", html);
            Assert.Contains("<span style=\"width: 30%\"></span>", html);
            Assert.Contains(H(String.Format(Strings.WallboardPage_ClearsIn, Duration(TimeSpan.FromMinutes(10 / 6.0)))), html);
            Assert.Equal(1, CountOf(html, "&infin;"));
            Assert.Contains("<span class=\"hf-wb-queue-count\">3</span>", html);
        }

        [Fact]
        public void Render_ShowsRunningJobs_WithProgressFromTheirUsualDuration()
        {
            var now = DateTime.UtcNow;
            AddInsight(Job.FromExpression(() => SampleJobs.Quick()), 10000);
            AddInsight(Job.FromExpression(() => SampleJobs.Long()), 3600000);
            _renderer.Monitoring.Setup(x => x.ProcessingJobs(0, 8)).Returns(new JobList<ProcessingJobDto>(new[]
            {
                Processing("1", new ProcessingJobDto { Job = Job.FromExpression(() => SampleJobs.Quick()), ServerId = "srv-quick", StartedAt = now.AddMinutes(-1) }),
                Processing("2", new ProcessingJobDto { Job = Job.FromExpression(() => SampleJobs.Long()), ServerId = "srv-long", StartedAt = now.AddSeconds(-1) }),
                Processing("3", new ProcessingJobDto { Job = null, ServerId = null, StartedAt = null }),
                Processing("4", new ProcessingJobDto { Job = Job.FromExpression(() => SampleJobs.Quick()), ServerId = "srv-gone", InProcessingState = false }),
                Processing("5", null),
            }));

            var html = _renderer.Render(new WallboardPage());

            Assert.DoesNotContain(H(Strings.WallboardPage_NothingRunning), html);
            Assert.Equal(3, CountOf(html, "<div class=\"hf-wb-job\">"));
            Assert.Contains("SampleJobs.Quick</span>", html);
            Assert.Contains("SampleJobs.Long</span>", html);
            Assert.Contains("<span class=\"hf-wb-job-name\">" + H(Strings.Common_CannotFindTargetMethod) + "</span>", html);
            Assert.Contains("srv-quick", html);
            Assert.DoesNotContain("srv-gone", html);
            Assert.Equal(1, CountOf(html, "hf-wb-bar hf-wb-bar-job hf-wb-bar-late\"><span style=\"width: 100%\">"));
            Assert.Equal(1, CountOf(html, "hf-wb-bar hf-wb-bar-job \"><span style=\"width: 100%\">"));
            Assert.Equal(1, CountOf(html, "hf-wb-bar hf-wb-bar-job \"><span style=\"width: 0%\">"));
        }

        [Fact]
        public void Render_ListsOpenCircuits_AsNeedingAttention()
        {
            AddServer("srv-1", DateTime.UtcNow, "default");
            _renderer.Connection.Setup(x => x.GetAllItemsFromSet(CircuitBreakerAttribute.CircuitsSetKey)).Returns(new HashSet<string> { "payments", "mail" });
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("circuit:payments")).Returns(new Dictionary<string, string> { { "State", CircuitBreakerAttribute.OpenState } });
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("circuit:mail")).Returns(new Dictionary<string, string> { { "State", CircuitBreakerAttribute.ClosedState } });

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("<li>" + H(String.Format(Strings.WallboardPage_OpenCircuits, "payments")) + "</li>", html);
        }

        [Fact]
        public void Render_StillShowsTheBoard_WhenTheConnectionCannotBeOpened()
        {
            AddServer("srv-1", DateTime.UtcNow, "default");
            _renderer.Storage.Setup(x => x.GetReadOnlyConnection()).Throws(new InvalidOperationException("no connection"));

            var html = _renderer.Render(new WallboardPage());

            Assert.Contains("hf-wb-warn", html);
            Assert.Contains("<li>recurring: InvalidOperationException: no connection</li>", html);
            Assert.Contains(H(Strings.WallboardPage_NothingRunning), html);
        }

        private void AddServer(string name, DateTime? heartbeat, params string[] queues)
        {
            _servers.Add(new ServerDto { Name = name, Heartbeat = heartbeat, StartedAt = DateTime.UtcNow.AddHours(-1), Queues = queues, WorkersCount = 5 });
        }

        private static QueueWithTopEnqueuedJobsDto Queue(string name, long length)
        {
            return new QueueWithTopEnqueuedJobsDto { Name = name, Length = length, FirstJobs = new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>()) };
        }

        private void AddQueueHealth(string queue, long startedPerMinute)
        {
            _renderer.Connection.Setup(x => x.GetAllItemsFromSet(QueueInsights.IndexKey)).Returns(new HashSet<string> { "critical", "default" });
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(QueueInsights.GetHashKey(queue))).Returns(new Dictionary<string, string>());
            _renderer.Connection.Setup(x => x.GetAllItemsFromList(QueueInsights.GetLatencyKey(queue))).Returns(new List<string> { "100" });
            _renderer.Connection.Setup(x => x.GetCounter(It.Is<string>(k => k.StartsWith($"queue:{queue}:started:", StringComparison.Ordinal)))).Returns(startedPerMinute);
        }

        private void AddInsight(Job job, long durationMs)
        {
            var key = JobInsightsAttribute.GetKey(job);
            var duration = durationMs.ToString(CultureInfo.InvariantCulture);

            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"insights:{key}")).Returns(new Dictionary<string, string> { { "LastDurationMs", duration } });
            _renderer.Connection.Setup(x => x.GetAllItemsFromList($"insights:{key}:durations")).Returns(new List<string> { duration, duration, duration });
        }

        private static KeyValuePair<string, ProcessingJobDto> Processing(string id, ProcessingJobDto dto)
        {
            return new KeyValuePair<string, ProcessingJobDto>(id, dto);
        }

        private static string Duration(TimeSpan duration)
        {
            var page = new WallboardPage();
            page.Assign(new DashboardContextStub(new DashboardOptions()));
            return new HtmlHelper(page).ToHumanDuration(duration, false);
        }

        private static int CountOf(string html, string fragment)
        {
            return (html.Length - html.Replace(fragment, String.Empty).Length) / fragment.Length;
        }

        private static string H(string text) => WebUtility.HtmlEncode(text);

        public static class SampleJobs
        {
            public static void Quick()
            {
            }

            public static void Long()
            {
            }
        }
    }
}
