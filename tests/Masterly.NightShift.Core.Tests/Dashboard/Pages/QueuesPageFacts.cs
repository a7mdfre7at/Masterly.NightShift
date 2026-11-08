using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class QueuesPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly DateTime _now = DateTime.UtcNow;

        [Fact]
        public void Render_WithoutQueues_ShowsTheWarning()
        {
            var html = _renderer.Render(new QueuesPage());

            Assert.Contains(Encode(Strings.QueuesPage_NoQueues), html);
            Assert.DoesNotContain("table-striped", html);
        }

        [Fact]
        public void Render_WithAnEmptyQueueWithoutStatistics_ShowsPlaceholders()
        {
            SetupQueues(Queue("default", length: 0));

            var html = _renderer.Render(new QueuesPage());

            Assert.Contains("<a href=\"/jobs/enqueued/default\">default</a>", html);
            Assert.Contains($"<em>{Encode(Strings.Common_NotAvailable)}</em>", html);
            Assert.Contains($"<em class=\"text-muted\" title=\"{Encode(Strings.QueuesPage_Insights_Hint)}\">&ndash;</em>", html);
            Assert.Contains("<span class=\"text-muted\">&ndash;</span>", html);
            Assert.Contains(Encode(Strings.QueuesPage_NoJobs), html);
            Assert.Contains($"title=\"{Encode(Strings.QueuesPage_Table_Latency_Hint)}\">{Encode(Strings.QueuesPage_Table_Latency)}</th>", html);
            Assert.DoesNotContain(Encode(Strings.QueuesPage_Stalled), html);
        }

        [Fact]
        public void Render_WithAWaitingQueueWithoutStatistics_ShowsNoEstimate()
        {
            SetupQueues(Queue("default", length: 5));

            var html = _renderer.Render(new QueuesPage());

            Assert.Contains("<td>5</td>", html);
            Assert.Contains("<em class=\"text-muted\">&ndash;</em>", html);
            Assert.DoesNotContain("<span class=\"text-muted\">&ndash;</span>", html);
            Assert.DoesNotContain("&infin;", html);
        }

        [Fact]
        public void Render_WhenNotReadOnly_OffersToPauseTheQueue()
        {
            SetupQueues(Queue("my queue", length: 0));

            var html = _renderer.Render(new QueuesPage());

            Assert.Contains("<tr class=\"\">", html);
            Assert.Contains("data-ajax=\"/queues/pause/my%20queue\"", html);
            Assert.Contains($"data-confirm=\"{Encode(Strings.QueuesPage_PauseConfirm)}\"", html);
            Assert.DoesNotContain("/queues/resume/", html);
        }

        [Fact]
        public void Render_WhenReadOnly_HidesThePauseAndResumeButtons()
        {
            SetupQueues(Queue("default", length: 0));
            SetupControlHash(new Dictionary<string, string> { { "Queue:default", "" } });
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new QueuesPage());

            Assert.DoesNotContain("hf-queue-actions", html);
            Assert.DoesNotContain("/queues/pause/", html);
            Assert.DoesNotContain("/queues/resume/", html);
        }

        [Fact]
        public void Render_WithAPausedQueue_MarksItPausedAndOffersToResume()
        {
            SetupQueues(Queue("emails", length: 3));
            SetupControlHash(new Dictionary<string, string> { { "Queue:emails", "SMTP <down>" } });

            var html = _renderer.Render(new QueuesPage());

            Assert.Contains("<tr class=\"hf-queue-paused\">", html);
            Assert.Contains($"<span class=\"label label-warning text-uppercase\" title=\"SMTP &lt;down&gt;\">{Encode(Strings.QueuesPage_Paused)}</span>", html);
            Assert.Contains("data-ajax=\"/queues/resume/emails\"", html);
            Assert.DoesNotContain("/queues/pause/emails", html);
        }

        [Fact]
        public void Render_WithStatistics_ShowsLatencyThroughputAndDrainEstimate()
        {
            SetupQueues(Queue("critical", length: 24, fetched: 3));
            SetupHealth("critical", lastStartedAt: _now.AddMinutes(-1), startedPerMinute: 2, "100", "200", "300");

            var html = _renderer.Render(new QueuesPage());

            Assert.Contains("<a href=\"/jobs/enqueued/fetched/critical\">", html);
            Assert.Contains($"<span title=\"{Encode(Strings.QueuesPage_Table_Latency_Hint)}\">", html);
            // Two jobs in each of the last five whole minutes: two per minute
            Assert.Matches(@"<td class=""min-width hf-queue-health"">\s*2\s*</td>", html);
            Assert.Contains("<strong>", html);
            Assert.DoesNotContain(Encode(Strings.QueuesPage_Stalled), html);
        }

        [Fact]
        public void Render_WhenNobodyTakesJobsFromTheQueue_MarksItStalled()
        {
            SetupQueues(Queue("stuck", length: 5));
            SetupHealth("stuck", lastStartedAt: _now.AddHours(-1), startedPerMinute: 0);

            var html = _renderer.Render(new QueuesPage());

            Assert.Contains($"<span class=\"label label-danger text-uppercase\" title=\"{Encode(Strings.QueuesPage_Stalled_Tooltip)}\">{Encode(Strings.QueuesPage_Stalled)}</span>", html);
            Assert.Contains("<span class=\"text-muted\">&infin;</span>", html);
            Assert.Contains($"<em class=\"text-muted\" title=\"{Encode(Strings.QueuesPage_Insights_Hint)}\">&ndash;</em>", html);
        }

        [Fact]
        public void Render_WhenDrainModeIsOn_DoesNotMarkTheQueueStalled()
        {
            SetupQueues(Queue("stuck", length: 5));
            SetupHealth("stuck", lastStartedAt: _now.AddHours(-1), startedPerMinute: 0);
            SetupControlHash(new Dictionary<string, string> { { "Drain", "1" } });

            var html = _renderer.Render(new QueuesPage());

            Assert.DoesNotContain(Encode(Strings.QueuesPage_Stalled_Tooltip), html);
        }

        [Fact]
        public void Render_WithFirstJobs_ListsThemWithTheirStates()
        {
            SetupQueues(Queue("default", length: 4, firstJobs: new Dictionary<string, EnqueuedJobDto>
            {
                { "1", new EnqueuedJobDto { Job = SampleJob(), State = "Enqueued", InEnqueuedState = true, EnqueuedAt = _now } },
                { "2", new EnqueuedJobDto { Job = SampleJob(), State = "Processing", InEnqueuedState = false } },
                { "3", null }
            }));

            var html = _renderer.Render(new QueuesPage());

            Assert.Contains("table table-condensed table-inner", html);
            Assert.Contains("<a href=\"/jobs/details/1\">#1</a>", html);
            Assert.Contains("QueuesPageJobs.Work", html);
            Assert.Contains("<span data-moment=", html);
            Assert.Contains($"<span title=\"{Encode(Strings.Common_JobStateChanged_Text)}\" class=\"glyphicon glyphicon-question-sign\"></span>", html);
            Assert.Contains($"<td colspan=\"3\"><em>{Encode(Strings.Common_JobExpired)}</em></td>", html);
            Assert.Contains("<tr class=\"obsolete-data\">", html);
        }

        private void SetupQueues(params QueueWithTopEnqueuedJobsDto[] queues)
        {
            _renderer.Monitoring.Setup(x => x.Queues()).Returns(queues.ToList());
        }

        private void SetupControlHash(Dictionary<string, string> hash)
        {
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(hash);
        }

        private void SetupHealth(string queue, DateTime lastStartedAt, long startedPerMinute, params string[] latencies)
        {
            _renderer.Connection.Setup(x => x.GetAllItemsFromSet(QueueInsights.IndexKey)).Returns(new HashSet<string> { queue });
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(QueueInsights.GetHashKey(queue)))
                .Returns(new Dictionary<string, string> { { "LastStartedAt", JobHelper.SerializeDateTime(lastStartedAt) } });
            _renderer.Connection.Setup(x => x.GetAllItemsFromList(QueueInsights.GetLatencyKey(queue))).Returns(latencies.ToList());
            _renderer.Connection.Setup(x => x.GetCounter(It.Is<string>(key => key.StartsWith($"queue:{queue}:started:", StringComparison.Ordinal))))
                .Returns(startedPerMinute);
        }

        private static QueueWithTopEnqueuedJobsDto Queue(string name, long length, long? fetched = null, Dictionary<string, EnqueuedJobDto> firstJobs = null)
        {
            return new QueueWithTopEnqueuedJobsDto
            {
                Name = name,
                Length = length,
                Fetched = fetched,
                FirstJobs = new JobList<EnqueuedJobDto>(firstJobs ?? new Dictionary<string, EnqueuedJobDto>())
            };
        }

        private static Job SampleJob() => Job.FromExpression(() => QueuesPageJobs.Work());

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }

    public static class QueuesPageJobs
    {
        public static void Work()
        {
        }
    }
}
