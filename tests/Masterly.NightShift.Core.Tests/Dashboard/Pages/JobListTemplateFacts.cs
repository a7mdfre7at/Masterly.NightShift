using System.Net;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

/// <summary>What every list of jobs shares: the page header, the two-line rows, the row actions, the empty state.</summary>
public class JobListTemplateFacts
{
    private readonly PageRenderer _renderer = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public static TheoryData<string> Lists() => ["enqueued", "fetched", "scheduled", "processing", "succeeded", "failed", "deleted"];

    [Theory]
    [MemberData(nameof(Lists))]
    public void AListWithAJob_HasTheHeaderTheTwoLineRowAndTheRowActions(string list)
    {
        SetupOneJob(list);

        string html = _renderer.Render(Page(list));

        Assert.Contains("<header class=\"ns-page-head\">", html);
        Assert.Contains("<span class=\"ns-count-chip js-list-count\">1</span>", html);
        Assert.Contains("class=\"table ns-job-table ns-table-reflow\" aria-describedby=\"page-title\"", html);
        Assert.Contains("<td class=\"ns-job-cell\">", html);
        Assert.Contains("<span class=\"ns-job-id\">#1</span>", html);
        Assert.Contains($"<span class=\"visually-hidden\">{Encode(Strings.Common_Actions)}</span>", html);
        Assert.Contains("<div class=\"ns-row-actions\">", html);
        Assert.Contains("data-copy=\"1\"", html);
        Assert.Contains("data-job=\"1\"", html);
        Assert.Equal(list != "enqueued", html.Contains("data-list-primary=\"true\"", StringComparison.Ordinal));
        AssertHeadingsInOrder(html);
        AssertNoEmptyHeaderCells(html);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void AListWithAJob_WhenReadOnly_KeepsOnlyTheCopyButton(string list)
    {
        SetupOneJob(list);
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(Page(list));

        Assert.Contains("data-copy=\"1\"", html);
        Assert.DoesNotContain("js-job-command", html);
        Assert.DoesNotContain("js-jobs-list-command", html);
        Assert.DoesNotContain("btn-delete-all", html);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void AnEmptyList_SaysSoInAnEmptyState_WithTheNextStep(string list)
    {
        string html = _renderer.Render(Page(list));

        Assert.Contains("<span class=\"ns-count-chip js-list-count\">0</span>", html);
        Assert.Contains("<div class=\"ns-empty-state\">", html);
        Assert.Matches("<h2 class=\"ns-empty-state-title\">[^<]+</h2><a class=\"btn btn-sm ns-btn-tonal\" href=\"/", html);
        Assert.DoesNotContain("js-jobs-list", html);
        Assert.DoesNotContain("alert alert-info", html);
        AssertHeadingsInOrder(html);
    }

    [Fact]
    public void TheQueuePage_ShowsItsHealth_AndOffersToPauseIt()
    {
        SetupOneJob("enqueued");
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(QueueInsights.GetHashKey("default")))
            .Returns(new Dictionary<string, string> { { "LastStartedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-1)) } });
        _renderer.Connection.Setup(x => x.GetAllItemsFromList(QueueInsights.GetLatencyKey("default"))).Returns(["100", "200"]);

        string html = _renderer.Render(new EnqueuedJobsPage("default"));

        Assert.Contains($"<span class=\"ns-overline\">{Encode(Strings.QueuesPage_Table_Queue)}</span><span class=\"ns-page-name\">default</span>", html);
        Assert.Contains("<div class=\"ns-summary\">", html);
        Assert.Contains("data-ajax=\"/queues/pause/default\"", html);
        Assert.Contains($"<a href=\"/jobs/enqueued\">{Encode(Strings.QueuesPage_Title)}</a>", html);
    }

    [Fact]
    public void TheQueuePage_OfAPausedQueue_OffersToResumeIt()
    {
        SetupOneJob("enqueued");
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey))
            .Returns(new Dictionary<string, string> { { "Queue:default", "maintenance" } });

        string html = _renderer.Render(new EnqueuedJobsPage("default"));

        Assert.Contains("data-ajax=\"/queues/resume/default\"", html);
        Assert.Contains("title=\"maintenance\"", html);
        Assert.DoesNotContain("/queues/pause/", html);
    }

    [Fact]
    public void ARowOfAJobThatMovedOn_HasNoCommands()
    {
        _renderer.Monitoring.Setup(x => x.SucceededListCount()).Returns(1);
        _renderer.Monitoring.Setup(x => x.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<SucceededJobDto>(new Dictionary<string, SucceededJobDto> { { "1", new SucceededJobDto { Job = SampleJob(), InSucceededState = false } } }));

        string html = _renderer.Render(new SucceededJobs());

        Assert.Contains("class=\"js-jobs-list-row obsolete-data\"", html);
        Assert.DoesNotContain("js-job-command", html);
        Assert.Contains("data-copy=\"1\"", html);
    }

    private static RazorPage Page(string list) => list switch
    {
        "enqueued" => new EnqueuedJobsPage("default"),
        "fetched" => new FetchedJobsPage("default"),
        "scheduled" => new ScheduledJobsPage(),
        "processing" => new ProcessingJobsPage(),
        "succeeded" => new SucceededJobs(),
        "failed" => new FailedJobsPage(),
        "deleted" => new DeletedJobsPage(),
        _ => throw new ArgumentOutOfRangeException(nameof(list)),
    };

    private void SetupOneJob(string list)
    {
        Job job = SampleJob();
        switch (list)
        {
            case "enqueued":
                _renderer.Monitoring.Setup(x => x.EnqueuedCount("default")).Returns(1);
                _renderer.Monitoring.Setup(x => x.EnqueuedJobs("default", It.IsAny<int>(), It.IsAny<int>()))
                    .Returns(new JobList<EnqueuedJobDto>(new Dictionary<string, EnqueuedJobDto> { { "1", new EnqueuedJobDto { Job = job, State = "Enqueued", InEnqueuedState = true, EnqueuedAt = _now } } }));
                break;
            case "fetched":
                _renderer.Monitoring.Setup(x => x.FetchedCount("default")).Returns(1);
                _renderer.Monitoring.Setup(x => x.FetchedJobs("default", It.IsAny<int>(), It.IsAny<int>()))
                    .Returns(new JobList<FetchedJobDto>(new Dictionary<string, FetchedJobDto> { { "1", new FetchedJobDto { Job = job, State = "Enqueued", FetchedAt = _now } } }));
                break;
            case "scheduled":
                _renderer.Monitoring.Setup(x => x.ScheduledCount()).Returns(1);
                _renderer.Monitoring.Setup(x => x.ScheduledJobs(It.IsAny<int>(), It.IsAny<int>()))
                    .Returns(new JobList<ScheduledJobDto>(new Dictionary<string, ScheduledJobDto> { { "1", new ScheduledJobDto { Job = job, InScheduledState = true, EnqueueAt = _now.AddMinutes(5), ScheduledAt = _now } } }));
                break;
            case "processing":
                _renderer.Monitoring.Setup(x => x.ProcessingCount()).Returns(1);
                _renderer.Monitoring.Setup(x => x.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
                    .Returns(new JobList<ProcessingJobDto>(new Dictionary<string, ProcessingJobDto> { { "1", new ProcessingJobDto { Job = job, InProcessingState = true, ServerId = "web-1:1", StartedAt = _now } } }));
                break;
            case "succeeded":
                _renderer.Monitoring.Setup(x => x.SucceededListCount()).Returns(1);
                _renderer.Monitoring.Setup(x => x.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
                    .Returns(new JobList<SucceededJobDto>(new Dictionary<string, SucceededJobDto> { { "1", new SucceededJobDto { Job = job, InSucceededState = true, SucceededAt = _now, TotalDuration = 12 } } }));
                break;
            case "failed":
                _renderer.Monitoring.Setup(x => x.FailedCount()).Returns(1);
                _renderer.Monitoring.Setup(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
                    .Returns(new JobList<FailedJobDto>(new Dictionary<string, FailedJobDto> { { "1", new FailedJobDto { Job = job, InFailedState = true, ExceptionType = "System.Exception", ExceptionMessage = "Boom", FailedAt = _now } } }));
                break;
            case "deleted":
                _renderer.Monitoring.Setup(x => x.DeletedListCount()).Returns(1);
                _renderer.Monitoring.Setup(x => x.DeletedJobs(It.IsAny<int>(), It.IsAny<int>()))
                    .Returns(new JobList<DeletedJobDto>(new Dictionary<string, DeletedJobDto> { { "1", new DeletedJobDto { Job = job, InDeletedState = true, DeletedAt = _now } } }));
                break;
        }
    }

    // h1 first, and no heading more than one level below the one before it
    private static void AssertHeadingsInOrder(string html)
    {
        int[] levels = [.. Regex.Matches(html, "<h([1-6])[ >]").Select(static m => m.Groups[1].Value[0] - '0')];
        Assert.NotEmpty(levels);
        Assert.Equal(1, levels[0]);
        for (int i = 1; i < levels.Length; i++)
        {
            Assert.True(levels[i] <= levels[i - 1] + 1, $"h{levels[i]} after h{levels[i - 1]}");
        }
    }

    private static void AssertNoEmptyHeaderCells(string html)
    {
        Assert.DoesNotMatch("<th[^>]*>\\s*</th>", html);
    }

    private static Job SampleJob() => Job.FromExpression(() => JobListTemplateJobs.Work());

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

public static class JobListTemplateJobs
{
    public static void Work()
    {
    }
}
