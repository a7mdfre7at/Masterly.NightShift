using System;
using System.Collections.Generic;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.States;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class BatchesPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_WithoutBatches_ShowsTheEmptyMessage()
        {
            var html = _renderer.Render(new BatchesPage());

            Assert.Contains($"{Encode(Strings.BatchesPage_Title)} <small class=\"text-muted\">{Encode(Strings.BatchesPage_Hint)}</small>", html);
            Assert.Contains($"<div class=\"alert alert-info\">{Strings.BatchesPage_NoBatches_Html}</div>", html);
            Assert.DoesNotContain("hf-batches", html);
        }

        [Fact]
        public void Render_WithAFinishedBatch_ShowsItWithItsDescriptionAndFullBar()
        {
            SetupIndex("nightly");
            SetupBatch("nightly", "Nightly <import>", DateTime.UtcNow.AddHours(-1), total: 4, succeeded: 4);

            var html = _renderer.Render(new BatchesPage());

            Assert.Contains("<a class=\"hf-batch-link\" href=\"/batches/nightly\">", html);
            Assert.Contains($"<span class=\"label label-success text-uppercase\">{Encode(Strings.BatchesPage_State_Finished)}</span>", html);
            Assert.Contains("<strong>Nightly &lt;import&gt;</strong>", html);
            Assert.Contains("<code class=\"hf-batch-id\">nightly</code>", html);
            Assert.Contains("<span class=\"hf-batch-seg hf-batch-seg-success\" style=\"width: 100%\"></span>", html);
            Assert.Contains("<span class=\"hf-batch-seg hf-batch-seg-failed\" style=\"width: 0%\"></span>", html);
            Assert.Contains($"<div class=\"hf-batch-counts text-muted\">{Encode(String.Format(Strings.BatchesPage_Counts, 4, 0, 0))}</div>", html);
            Assert.Contains("<td class=\"align-right\">4</td>", html);
            Assert.Contains("<span data-moment=", html);
        }

        [Fact]
        public void Render_WithAStuckBatch_MarksItFailed()
        {
            SetupIndex("b-2");
            SetupBatch("b-2", null, null, total: 4, succeeded: 3, failed: 1);

            var html = _renderer.Render(new BatchesPage());

            Assert.Contains($"<span class=\"label label-danger text-uppercase\">{Encode(Strings.BatchesPage_State_Failed)}</span>", html);
            Assert.Contains("<strong>b-2</strong>", html);
            Assert.DoesNotContain("hf-batch-id", html);
            Assert.Contains("<span class=\"hf-batch-seg hf-batch-seg-success\" style=\"width: 75%\"></span>", html);
            Assert.Contains("<span class=\"hf-batch-seg hf-batch-seg-failed\" style=\"width: 25%\"></span>", html);
            Assert.DoesNotContain("<span data-moment=", html);
        }

        [Fact]
        public void Render_WithARunningBatch_ShowsItsProgress()
        {
            SetupIndex("b-3");
            SetupBatch("b-3", null, null, total: 3, succeeded: 1, processing: 1, deleted: 1);

            var html = _renderer.Render(new BatchesPage());

            Assert.Contains($"<span class=\"label label-info text-uppercase\">{Encode(Strings.BatchesPage_State_Running)}</span>", html);
            Assert.Contains("<span class=\"hf-batch-seg hf-batch-seg-processing\" style=\"width: 33.3%\"></span>", html);
            Assert.Contains("<span class=\"hf-batch-seg hf-batch-seg-deleted\" style=\"width: 33.3%\"></span>", html);
            Assert.Contains(Encode(String.Format(Strings.BatchesPage_Counts, 1, 0, 1)), html);
        }

        [Fact]
        public void Render_WithAnEmptyBatch_DoesNotDivideByZero()
        {
            SetupIndex("empty");
            SetupBatch("empty", null, null, total: 0);

            var html = _renderer.Render(new BatchesPage());

            Assert.Contains($"<span class=\"label label-info text-uppercase\">{Encode(Strings.BatchesPage_State_Running)}</span>", html);
            Assert.Contains("<span class=\"hf-batch-seg hf-batch-seg-success\" style=\"width: 0%\"></span>", html);
        }

        [Fact]
        public void Render_WithSeveralBatches_ListsTheNewestFirst()
        {
            SetupIndex("old", "new");
            SetupBatch("old", null, null, total: 1, succeeded: 1);
            SetupBatch("new", null, null, total: 1);

            var html = _renderer.Render(new BatchesPage());

            Assert.True(html.IndexOf("/batches/new", StringComparison.Ordinal) < html.IndexOf("/batches/old", StringComparison.Ordinal));
        }

        private void SetupIndex(params string[] ids)
        {
            _renderer.Connection.Setup(x => x.GetSetCount(BatchJob.IndexKey)).Returns(ids.Length);
            _renderer.Connection.Setup(x => x.GetRangeFromSet(BatchJob.IndexKey, It.IsAny<int>(), It.IsAny<int>()))
                .Returns(() => new List<string>(ids));
        }

        private void SetupBatch(string id, string description, DateTime? createdAt, long total, long succeeded = 0, long failed = 0, long processing = 0, long deleted = 0)
        {
            var hash = new Dictionary<string, string> { { "Description", description ?? String.Empty } };
            if (createdAt.HasValue) hash["CreatedAt"] = JobHelper.SerializeDateTime(createdAt.Value);

            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(id))).Returns(hash);
            _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetTotalKey(id))).Returns(total);
            _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, SucceededState.StateName))).Returns(succeeded);
            _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, FailedState.StateName))).Returns(failed);
            _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, ProcessingState.StateName))).Returns(processing);
            _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(id, DeletedState.StateName))).Returns(deleted);
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
