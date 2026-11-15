using System.Globalization;
using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class BatchDetailsPageFacts
{
    private const string BatchId = "b-1";

    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Ctor_DecodesOnlyAnEncodedSlash_InTheId()
    {
        // ASP.NET Core has already decoded everything else in the path
        BatchDetailsPage page = new("nightly%2Fimport %41");

        Assert.Equal("nightly/import %41", page.Id);
    }

    [Fact]
    public void Render_ShowsTheExpiredWarning_WhenTheBatchDoesNotExist()
    {
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(BatchId))).Returns((Dictionary<string, string>)null);

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains(H(string.Format(Strings.BatchDetailsPage_Expired, BatchId)), html);
        Assert.Contains(H(string.Format(Strings.BatchDetailsPage_Title, BatchId)), html);
        Assert.DoesNotContain("ns-summary", html);
    }

    [Fact]
    public void Render_ShowsARunningBatch_WithItsDescriptionCountsAndJobs()
    {
        SetupBatch(new Dictionary<string, string>
        {
            { "Description", "Nightly import" },
            { "CreatedAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-3)) },
        }, total: 3, counts: new Dictionary<string, long> { { SucceededState.StateName, 1 }, { ProcessingState.StateName, 1 } });
        SetupJobs("11", "12");
        SetupJob("11", SucceededState.StateName);

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains("Nightly import", html);
        Assert.Contains("<span class=\"ns-overline\">" + H(Strings.JobDetailsPage_Batch) + "</span><span class=\"ns-page-name\">Nightly import</span>", html);
        Assert.Contains("ns-state-chip ns-state-processing\" data-state=\"Processing\">" + DashboardMarkup.Icon("play-circle-fill") + "<span>" + H(Strings.BatchesPage_State_Running) + "</span>", html);
        Assert.Contains("<code class=\"ns-job-id\">b-1</code>", html);
        Assert.Contains("<h2 id=\"summary-created\" class=\"ns-summary-label\">", html);
        Assert.Contains("<h2 id=\"summary-pending\" class=\"ns-summary-label\">" + DashboardMarkup.Icon("hourglass-split") + " " + H(Strings.BatchDetailsPage_Pending) + "</h2>", html);
        Assert.Equal(5, CountOf(html, "class=\"ns-summary-tile"));
        Assert.Contains("aria-valuenow=\"33\">", html);
        Assert.Contains("<span style=\"width: 33.3%\"></span>", html);
        Assert.Contains("<span class=\"ns-dot ns-state-succeeded\" aria-hidden=\"true\"></span>", html);
        Assert.Contains("href=\"/jobs/details/11\"", html);
        Assert.Contains("SampleJobs", html);
        Assert.Contains("href=\"/jobs/details/12\"", html);
        Assert.Contains("<div class=\"ns-job-meta\"><em>" + H(Strings.Common_JobExpired) + "</em></div>", html);
        Assert.Contains("<h2 id=\"batch-jobs\">", html);
        Assert.DoesNotContain("data-ajax=\"/jobs/actions/requeue/", html);
        Assert.DoesNotContain("id=\"batch-continuations\"", html);
        Assert.DoesNotContain("/batches/requeue-failed/", html);
        Assert.Contains("data-ajax=\"/batches/delete/b-1\"", html);
        Assert.Contains(H(string.Format(Strings.BatchDetailsPage_DeletePending, 2)), html);
    }

    [Fact]
    public void Render_NamesTheStatesOfItsTiles_InTheReadersLanguage()
    {
        SetupBatch(new Dictionary<string, string> { { "Description", "Nightly import" } }, total: 4,
            counts: new Dictionary<string, long> { { SucceededState.StateName, 1 }, { FailedState.StateName, 1 }, { ProcessingState.StateName, 1 }, { DeletedState.StateName, 1 } });
        SetupJobs("11");
        CultureInfo culture = CultureInfo.CurrentUICulture;

        string html;
        try
        {
            CultureHelper.SetCurrentUICulture("de-DE");
            html = _renderer.Render(new BatchDetailsPage(BatchId));
        }
        finally
        {
            CultureHelper.SetCurrentUICulture(culture);
        }

        Assert.Contains("<h2 id=\"summary-succeeded\" class=\"ns-summary-label\">" + DashboardMarkup.Icon("check-circle-fill") + " Erfolgreich</h2>", html);
        Assert.Contains("<h2 id=\"summary-failed\" class=\"ns-summary-label\">" + DashboardMarkup.Icon("x-circle-fill") + " Fehler</h2>", html);
        Assert.Contains("<span>Verarbeiten... 1</span>", html);
        Assert.Contains("<span>" + H("Gelöscht") + " 1</span>", html);
    }

    [Fact]
    public void Render_UsesTheDefaultTitle_WhenTheBatchHasNoDescription()
    {
        SetupBatch([], total: 1, counts: []);

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains("<title>" + H(string.Format(Strings.BatchDetailsPage_Title, BatchId)), html);
        Assert.DoesNotContain(H(Strings.BatchesPage_Table_Created) + " <span data-moment=", html);
    }

    [Fact]
    public void Render_ShowsAFinishedBatch_WithoutCommands()
    {
        SetupBatch([], total: 2, counts: new Dictionary<string, long> { { SucceededState.StateName, 2 } });

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains("ns-state-chip ns-state-succeeded\" data-state=\"Succeeded\">" + DashboardMarkup.Icon("check-circle-fill") + "<span>" + H(Strings.BatchesPage_State_Finished) + "</span>", html);
        Assert.DoesNotContain("ns-page-actions", html);
        Assert.DoesNotContain("/batches/requeue-failed/", html);
        Assert.DoesNotContain("/batches/delete/", html);
    }

    [Fact]
    public void Render_ShowsAStuckBatch_WithRequeueAndDeleteCommands()
    {
        SetupBatch([], total: 2, counts: new Dictionary<string, long> { { SucceededState.StateName, 1 }, { FailedState.StateName, 1 } });

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains("ns-state-chip ns-state-failed\" data-state=\"Failed\">" + DashboardMarkup.Icon("x-circle-fill") + "<span>" + H(Strings.BatchesPage_State_Failed) + "</span>", html);
        Assert.Contains("<button class=\"btn btn-sm btn-primary\" data-ajax=\"/batches/requeue-failed/b-1\"", html);
        Assert.Contains("<button class=\"btn btn-sm ns-btn-outlined-error\" data-ajax=\"/batches/delete/b-1\"", html);
        Assert.Contains("<span class=\"ns-actions-divider\" aria-hidden=\"true\"></span>", html);
        Assert.True(html.IndexOf("/batches/delete/b-1", StringComparison.Ordinal) < html.IndexOf("/batches/requeue-failed/b-1", StringComparison.Ordinal));
        Assert.Contains("<section class=\"ns-summary-tile ns-state-failed\" aria-labelledby=\"summary-failed\">", html);
        Assert.Contains(H(string.Format(Strings.BatchDetailsPage_RequeueFailed, 1)), html);
        Assert.Contains(H(string.Format(Strings.BatchDetailsPage_DeletePending, 1)), html);
    }

    [Fact]
    public void Render_OffersRequeue_OnlyOnTheRowsOfFailedJobs()
    {
        SetupBatch([], total: 2, counts: new Dictionary<string, long> { { SucceededState.StateName, 1 }, { FailedState.StateName, 1 } });
        SetupJobs("31", "32");
        SetupJob("31", FailedState.StateName);
        SetupJob("32", SucceededState.StateName);

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains("data-ajax=\"/jobs/actions/requeue/31\"", html);
        Assert.Contains("aria-label=\"" + H(string.Format(Strings.Common_RequeueJob, "#31")) + "\"", html);
        Assert.DoesNotContain("data-ajax=\"/jobs/actions/requeue/32\"", html);
        Assert.Contains("data-copy=\"32\"", html);
        Assert.Contains("<th scope=\"col\" class=\"ns-actions-cell\"><span class=\"visually-hidden\">" + H(Strings.Common_Actions) + "</span></th>", html);
    }

    [Fact]
    public void Render_HidesTheCommands_WhenTheDashboardIsReadOnly()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
        SetupBatch([], total: 2, counts: new Dictionary<string, long> { { FailedState.StateName, 2 } });

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.DoesNotContain("ns-page-actions", html);
        Assert.DoesNotContain("/batches/requeue-failed/", html);
    }

    [Fact]
    public void Render_ListsTheContinuations_IncludingExpiredOnes()
    {
        SetupBatch([], total: 1, counts: []);
        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetContinuationsKey(BatchId))).Returns(["21", "22"]);
        SetupJob("21", AwaitingState.StateName);

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains("id=\"batch-continuations\"", html);
        Assert.Contains("href=\"/jobs/details/21\"", html);
        Assert.Contains(">" + AwaitingState.StateName + "</span>", html);
        Assert.Contains("href=\"/jobs/details/22\"", html);
        Assert.Contains("<td class=\"ns-job-cell\" colspan=\"2\">", html);
        Assert.Contains("<h2 id=\"batch-continuations\">", html);
    }

    [Fact]
    public void Render_PagesTheJobsOfALargeBatch_ReadingOnlyThePageShown()
    {
        // The page read every job id of the batch, then the data of 500 jobs, on every view
        SetupBatch([], total: 750, counts: []);
        SetupJobs([.. Enumerable.Range(1, 750).Select(i => i.ToString())]);
        _renderer.Request.Query["from"] = "20";
        _renderer.Request.Query["count"] = "10";

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        _renderer.Connection.Verify(x => x.GetRangeFromSet(BatchJob.GetJobsKey(BatchId), 20, 29), Times.Once);
        _renderer.Connection.Verify(x => x.GetRangeFromSet(BatchJob.GetJobsKey(BatchId), 0, int.MaxValue - 1), Times.Never);
        _renderer.Connection.Verify(x => x.GetJobData(It.IsAny<string>()), Times.Exactly(10));
        Assert.Contains("class=\"ns-pagination-pages\"", html);
        Assert.DoesNotContain(H(string.Format(Strings.BatchDetailsPage_MoreJobs, 250)), html);
    }

    [Fact]
    public void Render_SaysHowManyJobsAreNotShown_WhenTheStorageCanNotReadRanges()
    {
        _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        SetupBatch([], total: 750, counts: []);

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        Assert.Contains(H(string.Format(Strings.BatchDetailsPage_MoreJobs, 250)), html);
        Assert.DoesNotContain("class=\"ns-pagination-pages\"", html);
    }

    [Fact]
    public void Render_KeepsTheHeadingsInOrder_AndNamesEveryHeaderCell()
    {
        SetupBatch(new Dictionary<string, string> { { "Description", "Nightly import" } }, total: 2, counts: new Dictionary<string, long> { { FailedState.StateName, 1 } });
        SetupJobs("11");
        SetupJob("11", FailedState.StateName);
        _renderer.Connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetContinuationsKey(BatchId))).Returns(["21"]);

        string html = _renderer.Render(new BatchDetailsPage(BatchId));

        PageStructure.AssertHeadingsInOrder(html);
        PageStructure.AssertNoEmptyHeaderCells(html);
        PageStructure.AssertHeadingsInOrder(_renderer.Render(new BatchesPage()));
    }

    private void SetupBatch(Dictionary<string, string> hash, long total, Dictionary<string, long> counts)
    {
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(BatchId))).Returns(hash);
        _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetTotalKey(BatchId))).Returns(total);

        foreach (KeyValuePair<string, long> count in counts)
        {
            _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(BatchId, count.Key))).Returns(count.Value);
        }
    }

    private void SetupJobs(params string[] jobIds)
    {
        _renderer.Connection.Setup(x => x.GetSetCount(BatchJob.GetJobsKey(BatchId))).Returns(jobIds.Length);
        _renderer.Connection.Setup(x => x.GetRangeFromSet(BatchJob.GetJobsKey(BatchId), It.IsAny<int>(), It.IsAny<int>()))
            .Returns<string, int, int>((_, from, to) => [.. jobIds.Skip(from).Take(to - from + 1)]);
    }

    private void SetupJob(string jobId, string state)
    {
        _renderer.Connection.Setup(x => x.GetJobData(jobId)).Returns(new JobData
        {
            State = state,
            Job = Job.FromExpression(() => SampleJobs.Work()),
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        });
    }

    private static int CountOf(string html, string fragment)
    {
        return (html.Length - html.Replace(fragment, string.Empty).Length) / fragment.Length;
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);

    public static class SampleJobs
    {
        public static void Work()
        {
        }
    }
}
