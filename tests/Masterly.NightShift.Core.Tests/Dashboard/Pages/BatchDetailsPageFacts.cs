using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class BatchDetailsPageFacts
    {
        private const string BatchId = "b-1";

        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Ctor_UnescapesTheId_FromTheRoute()
        {
            var page = new BatchDetailsPage("nightly%20import");

            Assert.Equal("nightly import", page.Id);
        }

        [Fact]
        public void Render_ShowsTheExpiredWarning_WhenTheBatchDoesNotExist()
        {
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(BatchId))).Returns((Dictionary<string, string>)null);

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.Contains(H(String.Format(Strings.BatchDetailsPage_Expired, BatchId)), html);
            Assert.Contains(H(String.Format(Strings.BatchDetailsPage_Title, BatchId)), html);
            Assert.DoesNotContain("hf-batch-summary", html);
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

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.Contains("Nightly import", html);
            Assert.Contains("label label-info text-uppercase\">" + H(Strings.BatchesPage_State_Running), html);
            Assert.Contains("<code class=\"hf-batch-id\">b-1</code>", html);
            Assert.Contains(H(Strings.BatchesPage_Table_Created) + " <span data-moment=", html);
            Assert.Contains("hf-batch-seg-success\" style=\"width: 33.3%\"", html);
            Assert.Contains("hf-batch-seg-processing\" style=\"width: 33.3%\"", html);
            Assert.Contains("hf-batch-seg-failed\" style=\"width: 0%\"", html);
            Assert.Contains("<span class=\"hf-batch-stat-value\">3</span>", html);
            Assert.Equal(3, CountOf(html, "class=\"hf-batch-stat\""));
            Assert.Contains("href=\"/jobs/details/11\"", html);
            Assert.Contains("SampleJobs", html);
            Assert.Contains("href=\"/jobs/details/12\"", html);
            Assert.Contains("<td colspan=\"3\"><em>" + H(Strings.Common_JobExpired) + "</em></td>", html);
            Assert.DoesNotContain("id=\"batch-continuations\"", html);
            Assert.DoesNotContain("/batches/requeue-failed/", html);
            Assert.Contains("data-ajax=\"/batches/delete/b-1\"", html);
            Assert.Contains(H(String.Format(Strings.BatchDetailsPage_DeletePending, 2)), html);
        }

        [Fact]
        public void Render_UsesTheDefaultTitle_WhenTheBatchHasNoDescription()
        {
            SetupBatch(new Dictionary<string, string>(), total: 1, counts: new Dictionary<string, long>());

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.Contains("<title>" + H(String.Format(Strings.BatchDetailsPage_Title, BatchId)), html);
            Assert.DoesNotContain(H(Strings.BatchesPage_Table_Created) + " <span data-moment=", html);
        }

        [Fact]
        public void Render_ShowsAFinishedBatch_WithoutCommands()
        {
            SetupBatch(new Dictionary<string, string>(), total: 2, counts: new Dictionary<string, long> { { SucceededState.StateName, 2 } });

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.Contains("label label-success text-uppercase\">" + H(Strings.BatchesPage_State_Finished), html);
            Assert.Contains("hf-batch-actions", html);
            Assert.DoesNotContain("/batches/requeue-failed/", html);
            Assert.DoesNotContain("/batches/delete/", html);
        }

        [Fact]
        public void Render_ShowsAStuckBatch_WithRequeueAndDeleteCommands()
        {
            SetupBatch(new Dictionary<string, string>(), total: 2, counts: new Dictionary<string, long> { { SucceededState.StateName, 1 }, { FailedState.StateName, 1 } });

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.Contains("label label-danger text-uppercase\">" + H(Strings.BatchesPage_State_Failed), html);
            Assert.Contains("data-ajax=\"/batches/requeue-failed/b-1\"", html);
            Assert.Contains(H(String.Format(Strings.BatchDetailsPage_RequeueFailed, 1)), html);
            Assert.Contains(H(String.Format(Strings.BatchDetailsPage_DeletePending, 1)), html);
        }

        [Fact]
        public void Render_HidesTheCommands_WhenTheDashboardIsReadOnly()
        {
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
            SetupBatch(new Dictionary<string, string>(), total: 2, counts: new Dictionary<string, long> { { FailedState.StateName, 2 } });

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.DoesNotContain("hf-batch-actions", html);
            Assert.DoesNotContain("/batches/requeue-failed/", html);
        }

        [Fact]
        public void Render_ListsTheContinuations_IncludingExpiredOnes()
        {
            SetupBatch(new Dictionary<string, string>(), total: 1, counts: new Dictionary<string, long>());
            _renderer.Connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetContinuationsKey(BatchId))).Returns(new HashSet<string> { "21", "22" });
            SetupJob("21", AwaitingState.StateName);

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.Contains("id=\"batch-continuations\"", html);
            Assert.Contains("href=\"/jobs/details/21\"", html);
            Assert.Contains(">" + AwaitingState.StateName + "</span>", html);
            Assert.Contains("href=\"/jobs/details/22\"", html);
            Assert.Contains("<td colspan=\"2\"><em>" + H(Strings.Common_JobExpired) + "</em></td>", html);
        }

        [Fact]
        public void Render_SaysHowManyJobsAreNotShown_WhenTheBatchIsLarge()
        {
            SetupBatch(new Dictionary<string, string>(), total: 750, counts: new Dictionary<string, long>());

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.Contains(H(String.Format(Strings.BatchDetailsPage_MoreJobs, 250)), html);
        }

        [Fact]
        public void Render_DoesNotSayJobsAreHidden_WhenEveryJobIsShown()
        {
            SetupBatch(new Dictionary<string, string>(), total: 500, counts: new Dictionary<string, long>());

            var html = _renderer.Render(new BatchDetailsPage(BatchId));

            Assert.DoesNotContain(H(String.Format(Strings.BatchDetailsPage_MoreJobs, 0)), html);
        }

        private void SetupBatch(Dictionary<string, string> hash, long total, Dictionary<string, long> counts)
        {
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey(BatchId))).Returns(hash);
            _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetTotalKey(BatchId))).Returns(total);

            foreach (var count in counts)
            {
                _renderer.Connection.Setup(x => x.GetCounter(BatchJob.GetStateKey(BatchId, count.Key))).Returns(count.Value);
            }
        }

        private void SetupJobs(params string[] jobIds)
        {
            _renderer.Connection.Setup(x => x.GetRangeFromSet(BatchJob.GetJobsKey(BatchId), 0, Int32.MaxValue - 1)).Returns(jobIds.ToList());
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
            return (html.Length - html.Replace(fragment, String.Empty).Length) / fragment.Length;
        }

        private static string H(string text) => WebUtility.HtmlEncode(text);

        public static class SampleJobs
        {
            public static void Work()
            {
            }
        }
    }
}
