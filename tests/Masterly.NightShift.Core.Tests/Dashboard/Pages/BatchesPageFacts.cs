using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class BatchesPageFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_WithoutBatches_ShowsTheEmptyMessage()
    {
        string html = _renderer.Render(new BatchesPage());

        Assert.Contains($"<span class=\"ns-page-name\">{Encode(Strings.BatchesPage_Title)}</span> <span class=\"ns-count-chip js-list-count\">0</span></h1>", html);
        Assert.Contains($"<p class=\"ns-page-subtitle\">{Encode(Strings.BatchesPage_Hint)}</p>", html);
        Assert.Contains($"<h2 class=\"ns-empty-state-title\">{Encode(Strings.BatchesPage_EmptyTitle)}</h2>", html);
        Assert.Contains($"<p>{Strings.BatchesPage_NoBatches_Html}</p>", html);
        Assert.DoesNotContain("hf-batches", html);
    }

    [Fact]
    public void Render_WithAFinishedBatch_ShowsItWithItsDescriptionAndFullBar()
    {
        SetupIndex("nightly");
        SetupBatch("nightly", "Nightly <import>", DateTime.UtcNow.AddHours(-1), total: 4, succeeded: 4);

        string html = _renderer.Render(new BatchesPage());

        Assert.Contains("<tr class=\"hover hf-row-link\" data-href=\"/batches/nightly\">", html);
        Assert.Contains("<a class=\"job-method\" href=\"/batches/nightly\">Nightly &lt;import&gt;</a>", html);
        Assert.Contains(Chip("succeeded", "check-circle-fill", Strings.BatchesPage_State_Finished), html);
        Assert.Contains("<span class=\"ns-dot ns-state-succeeded\" aria-hidden=\"true\"></span>", html);
        Assert.Contains("<code class=\"ns-job-id\">nightly</code>", html);
        Assert.Contains("aria-valuenow=\"100\">", html);
        Assert.Contains("<span style=\"width: 100%\"></span>", html);
        Assert.Contains($"<div class=\"ns-progress-text\"><span>{Encode(string.Format(Strings.BatchesPage_Counts, 4, 0, 0))}</span></div>", html);
        Assert.Contains("<td class=\"align-right ns-tabular\">4</td>", html);
        Assert.DoesNotContain("/batches/requeue-failed/", html);
        Assert.DoesNotContain("/batches/delete/", html);
        Assert.Contains("<span data-moment=", html);
    }

    [Fact]
    public void Render_WithAStuckBatch_MarksItFailed()
    {
        SetupIndex("b-2");
        SetupBatch("b-2", null, null, total: 4, succeeded: 3, failed: 1);

        string html = _renderer.Render(new BatchesPage());

        Assert.Contains(Chip("failed", "x-circle-fill", Strings.BatchesPage_State_Failed), html);
        Assert.Contains("<a class=\"job-method\" href=\"/batches/b-2\">b-2</a>", html);
        Assert.DoesNotContain("<code class=\"ns-job-id\">", html);
        Assert.Contains("<span style=\"width: 100%\"></span>", html);
        Assert.Contains("data-ajax=\"/batches/requeue-failed/b-2\"", html);
        Assert.Contains($"aria-label=\"{Encode(string.Format(Strings.BatchesPage_RequeueFailed, "b-2"))}\"", html);
        Assert.Contains("data-ajax=\"/batches/delete/b-2\"", html);
        Assert.Contains($"aria-label=\"{Encode(string.Format(Strings.BatchesPage_DeletePending, "b-2"))}\"", html);
        Assert.Contains($"data-confirm=\"{Encode(Strings.BatchDetailsPage_DeletePendingConfirm)}\"", html);
        Assert.DoesNotContain("<span data-moment=", html);
    }

    [Fact]
    public void Render_WithARunningBatch_ShowsItsProgress()
    {
        SetupIndex("b-3");
        SetupBatch("b-3", null, null, total: 3, succeeded: 1, processing: 1, deleted: 1);

        string html = _renderer.Render(new BatchesPage());

        Assert.Contains(Chip("processing", "play-circle-fill", Strings.BatchesPage_State_Running), html);
        Assert.Contains("aria-valuenow=\"67\">", html);
        Assert.Contains("<span style=\"width: 66.7%\"></span>", html);
        Assert.Contains(Encode(string.Format(Strings.BatchesPage_Counts, 1, 0, 1)), html);
    }

    [Fact]
    public void Render_WithAnEmptyBatch_DoesNotDivideByZero()
    {
        SetupIndex("empty");
        SetupBatch("empty", null, null, total: 0);

        string html = _renderer.Render(new BatchesPage());

        Assert.Contains(Chip("processing", "play-circle-fill", Strings.BatchesPage_State_Running), html);
        Assert.Contains("<span style=\"width: 0%\"></span>", html);
    }

    [Fact]
    public void Render_WithSeveralBatches_ListsTheNewestFirst()
    {
        SetupIndex("old", "new");
        SetupBatch("old", null, null, total: 1, succeeded: 1);
        SetupBatch("new", null, null, total: 1);

        string html = _renderer.Render(new BatchesPage());

        Assert.True(html.IndexOf("/batches/new", StringComparison.Ordinal) < html.IndexOf("/batches/old", StringComparison.Ordinal));
    }

    private void SetupIndex(params string[] ids)
    {
        _renderer.Connection.Setup(x => x.GetSetCount(BatchJob.IndexKey)).Returns(ids.Length);
        _renderer.Connection.Setup(x => x.GetRangeFromSet(BatchJob.IndexKey, It.IsAny<int>(), It.IsAny<int>()))
            .Returns(() => [.. ids]);
    }

    private void SetupBatch(string id, string description, DateTime? createdAt, long total, long succeeded = 0, long failed = 0, long processing = 0, long deleted = 0)
    {
        Dictionary<string, string> hash = new() { { "Description", description ?? string.Empty } };
        if (createdAt.HasValue)
        {
            hash["CreatedAt"] = JobHelper.SerializeDateTime(createdAt.Value);
        }

        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(id))).Returns(hash);
        _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetTotalKey(id))).Returns(total);
        _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, SucceededState.StateName))).Returns(succeeded);
        _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, FailedState.StateName))).Returns(failed);
        _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, ProcessingState.StateName))).Returns(processing);
        _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, DeletedState.StateName))).Returns(deleted);
    }

    [Fact]
    public void Render_WhenReadOnly_ShowsNoRowActions()
    {
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = static _ => true };
        SetupIndex("b-2");
        SetupBatch("b-2", null, null, total: 4, succeeded: 3, failed: 1);

        string html = _renderer.Render(new BatchesPage());

        Assert.Contains("/batches/b-2", html);
        Assert.DoesNotContain("/batches/requeue-failed/", html);
        Assert.DoesNotContain("/batches/delete/", html);
        Assert.DoesNotContain("ns-actions-cell", html);
    }

    private static string Chip(string group, string icon, string text) =>
        $"<span class=\"ns-state-chip ns-state-{group}\">{DashboardMarkup.Icon(icon)}<span>{Encode(text)}</span></span>";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
