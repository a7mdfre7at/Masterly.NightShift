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
    public class RecurringJobsPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();
        private readonly List<string> _ids = new List<string>();

        public RecurringJobsPageFacts()
        {
            _renderer.Connection.Setup(x => x.GetSetCount("recurring-jobs")).Returns(() => _ids.Count);
            _renderer.Connection.Setup(x => x.GetRangeFromSet("recurring-jobs", It.IsAny<int>(), It.IsAny<int>())).Returns(() => _ids.ToList());
        }

        [Fact]
        public void Render_SaysThereAreNoJobs_AndStillRendersTheEditors_WhenNothingIsRegistered()
        {
            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains(H(Strings.RecurringJobsPage_NoJobs), html);
            Assert.DoesNotContain("js-jobs-list", html);
            Assert.Contains("id=\"recurringJobEditor\"", html);
            Assert.Contains("data-preview-url=\"/recurring/preview\"", html);
            Assert.Contains("<option value=\"" + H(TimeZoneInfo.Utc.Id) + "\">", html);
            Assert.Contains("<option value=\"Ignorable\">", html);
            Assert.Contains("id=\"manifestImport\"", html);
            Assert.Contains("data-import-url=\"/recurring/import\"", html);
        }

        [Fact]
        public void Render_HidesTheEditors_WhenTheDashboardIsReadOnly()
        {
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.DoesNotContain("id=\"recurringJobEditor\"", html);
            Assert.DoesNotContain("id=\"manifestImport\"", html);
        }

        [Fact]
        public void Render_ShowsTheToolbar_WithEveryCommand_AndAPager()
        {
            AddJob("daily", "0 0 * * *", Next(TimeSpan.FromHours(1)));

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("href=\"/recurring/forecast\"", html);
            Assert.Contains("data-url=\"/recurring/export\"", html);
            Assert.Contains("js-manifest-import", html);
            Assert.Contains("data-ajax=\"/recurring/remove-all\"", html);
            Assert.Contains("data-url=\"/recurring/trigger\"", html);
            Assert.Contains("data-url=\"/recurring/pause\"", html);
            Assert.Contains("data-url=\"/recurring/resume\"", html);
            Assert.Contains("data-url=\"/recurring/remove\"", html);
            Assert.Contains("js-jobs-list-select-all", html);
            Assert.Contains("name=\"jobs[]\" value=\"daily\"", html);
            Assert.Contains("btn-group pull-right paginator", html);
            Assert.DoesNotContain(H(String.Format(Strings.RecurringJobsPage_LateSummary, 1)), html);
        }

        [Fact]
        public void Render_HidesTheCommands_AndCheckboxes_WhenTheDashboardIsReadOnly()
        {
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
            AddJob("daily", "0 0 * * *", Next(TimeSpan.FromHours(1)), new Dictionary<string, string> { { "Error", "Boom" } });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("data-url=\"/recurring/export\"", html);
            Assert.DoesNotContain("js-manifest-import", html);
            Assert.DoesNotContain("/recurring/remove-all", html);
            Assert.DoesNotContain("/recurring/trigger", html);
            Assert.DoesNotContain("js-jobs-list-checkbox", html);
            Assert.DoesNotContain("js-recurring-edit\"", html);
            Assert.Contains("<td colspan=\"6\" class=\"failed-job-details\">", html);
        }

        [Fact]
        public void Render_SummarisesAndMarksLateJobs()
        {
            AddJob("late-one", "* * * * *", Next(TimeSpan.FromMinutes(-10)));
            AddJob("late-two", "* * * * *", Next(TimeSpan.FromMinutes(-5)));
            AddJob("on-time", "* * * * *", Next(TimeSpan.FromSeconds(30)));

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains(H(String.Format(Strings.RecurringJobsPage_LateSummary, 2)), html);
            Assert.Equal(2, CountOf(html, "title=\"" + H(Strings.RecurringJobsPage_Late_Tooltip) + "\""));
        }

        [Fact]
        public void Render_SaysTheLateSummaryCoversThisPage_WhenThereAreOtherPages()
        {
            AddJob("late-one", "* * * * *", Next(TimeSpan.FromMinutes(-10)));
            AddJob("on-time", "* * * * *", Next(TimeSpan.FromSeconds(30)));
            _renderer.Request.Query["count"] = "1";

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains(H(String.Format(Strings.RecurringJobsPage_LateSummary_Page, 1)), html);
            Assert.DoesNotContain(H(String.Format(Strings.RecurringJobsPage_LateSummary, 1)), html);
        }

        [Fact]
        public void Render_ShowsTheStateOfTheNextExecution_ForEachKindOfJob()
        {
            AddJob("paused", "* * * * *", Next(TimeSpan.FromMinutes(-10)), new Dictionary<string, string> { { "Paused", "1" } });
            AddJob("disabled", "* * * * *", null);
            AddJob("errored", "* * * * *", null, new Dictionary<string, string> { { "Error", "System.InvalidOperationException: Cron went away" + Environment.NewLine + "   at Masterly.NightShift.Server.RecurringJobScheduler.Execute()" } });
            AddJob("retrying", "* * * * *", Next(TimeSpan.FromMinutes(5)), new Dictionary<string, string> { { "RetryAttempt", "2" } });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("title=\"" + H(Strings.RecurringJobsPage_Paused_Tooltip) + "\">" + H(Strings.RecurringJobsPage_Paused) + "</span>", html);
            Assert.Contains("title=\"" + H(Strings.RecurringJobsPage_RecurringJobDisabled_Tooltip) + "\">" + H(Strings.Common_Disabled) + "</span>", html);
            Assert.Contains("<span class=\"label label-danger text-uppercase\">" + H(Strings.Common_Error) + "</span>", html);
            Assert.Contains("<span class=\"label label-warning\"><span data-moment=", html);
            Assert.DoesNotContain(H(String.Format(Strings.RecurringJobsPage_LateSummary, 1)), html);
            Assert.Contains("<td rowspan=\"2\">", html);
            Assert.Contains("<td colspan=\"8\" class=\"failed-job-details\">", html);
            Assert.Contains("Cron went away", html);
        }

        [Fact]
        public void Render_ShowsTheEditButton_WithTheScheduleOfTheJob()
        {
            AddJob("nightly", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string>
            {
                { "Queue", "critical" },
                { "Misfire", ((int)MisfireHandlingMode.Strict).ToString() },
            });
            AddJob("in-utc", "0 4 * * *", Next(TimeSpan.FromHours(4)), new Dictionary<string, string> { { "TimeZoneId", TimeZoneInfo.Utc.Id } });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("data-id=\"nightly\"", html);
            Assert.Contains("data-cron=\"0 3 * * *\"", html);
            Assert.Contains("data-timezone=\"" + H(TimeZoneInfo.Utc.Id) + "\"", html);
            Assert.Contains("data-queue=\"critical\"", html);
            Assert.Contains("data-misfire=\"Strict\"", html);
            Assert.Contains("data-misfire=\"Relaxed\"", html);
            Assert.Contains("<span title=\"" + H(TimeZoneInfo.Utc.DisplayName) + "\" data-container=\"body\">" + H(TimeZoneInfo.Utc.Id), html);
            Assert.Contains("data-start-at=\"\"", html);
            Assert.Contains("data-end-at=\"\"", html);
        }

        [Fact]
        public void Render_AddsAHiddenEditButton_ForTheJobToEdit_WhenItIsOnAnotherPage()
        {
            // /recurring?edit={id} only opened the editor when the job was on the page shown
            AddJob("on-this-page", "0 3 * * *", Next(TimeSpan.FromHours(3)));
            AddJob("elsewhere", "0 5 * * *", Next(TimeSpan.FromHours(5)), new Dictionary<string, string> { { "Queue", "reports" } });
            _ids.Remove("elsewhere");
            _renderer.Request.Query["edit"] = "elsewhere";

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("class=\"btn btn-xs btn-default js-recurring-edit hidden\"", html);
            Assert.Contains("data-id=\"elsewhere\" data-cron=\"0 5 * * *\"", html);
            Assert.Contains("data-queue=\"reports\"", html);
            Assert.Equal(2, CountOf(html, "data-misfire="));
        }

        [Fact]
        public void Render_DoesNotAddAHiddenEditButton_ForAJobOnThisPage_AnUnknownOne_OrInReadOnlyMode()
        {
            AddJob("on-this-page", "0 3 * * *", Next(TimeSpan.FromHours(3)));
            _renderer.Request.Query["edit"] = "on-this-page";
            var onThisPage = _renderer.Render(new RecurringJobsPage());

            _renderer.Request.Query["edit"] = "missing";
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:missing")).Returns((Dictionary<string, string>)null);
            var missing = _renderer.Render(new RecurringJobsPage());

            AddJob("elsewhere", "0 5 * * *", Next(TimeSpan.FromHours(5)));
            _ids.Remove("elsewhere");
            _renderer.Request.Query["edit"] = "elsewhere";
            _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };
            var readOnly = _renderer.Render(new RecurringJobsPage());

            Assert.DoesNotContain("js-recurring-edit hidden", onThisPage);
            Assert.DoesNotContain("js-recurring-edit hidden", missing);
            Assert.DoesNotContain("js-recurring-edit", readOnly);
            _renderer.Connection.Verify(x => x.GetAllEntriesFromHash("recurring-job:elsewhere"), Times.Never);
        }

        [Fact]
        public void Render_ShowsWhenAJob_StartsAndEnds()
        {
            var start = new DateTime(2099, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
            var end = new DateTime(2099, 6, 7, 8, 9, 10, 0, DateTimeKind.Utc);
            AddJob("later", "0 4 * * *", start.AddHours(1), new Dictionary<string, string>
            {
                { "StartAt", JobHelper.SerializeDateTime(start) },
                { "EndAt", JobHelper.SerializeDateTime(end) },
            });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains(String.Format(H(Strings.RecurringJobsPage_StartsAt), LocalTime(start)), html);
            Assert.Contains(String.Format(H(Strings.RecurringJobsPage_EndsAt), LocalTime(end)), html);
            Assert.DoesNotContain("data-moment=\"" + JobHelper.ToTimestamp(start.AddHours(1)), html);
            Assert.Contains("data-start-at=\"" + JobHelper.ToMillisecondTimestamp(start) + "\"", html);
            Assert.Contains("data-end-at=\"" + JobHelper.ToMillisecondTimestamp(end) + "\"", html);
        }

        [Fact]
        public void Render_ShowsTheEnd_AndTheNextRun_OfAnActiveJob()
        {
            var next = Next(TimeSpan.FromHours(1));
            var end = DateTime.UtcNow.AddDays(30);
            AddJob("active", "0 * * * *", next, new Dictionary<string, string>
            {
                { "StartAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddDays(-1)) },
                { "EndAt", JobHelper.SerializeDateTime(end) },
            });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("data-moment=\"" + JobHelper.ToTimestamp(next) + "\"", html);
            Assert.Contains(String.Format(H(Strings.RecurringJobsPage_EndsAt), LocalTime(end)), html);
            Assert.DoesNotContain(String.Format(H(Strings.RecurringJobsPage_StartsAt), "<span data-moment-local"), html);
            Assert.DoesNotContain(H(Strings.RecurringJobsPage_Ended_Tooltip), html);
        }

        [Fact]
        public void Render_SaysAJobHasEnded_InsteadOfDisabled_WhenItsEndHasPassed()
        {
            var end = DateTime.UtcNow.AddDays(-2);
            AddJob("ended", "0 * * * *", null, new Dictionary<string, string> { { "EndAt", JobHelper.SerializeDateTime(end) } });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("title=\"" + H(Strings.RecurringJobsPage_Ended_Tooltip) + "\">" + H(Strings.RecurringJobsPage_Ended) + "</span>", html);
            Assert.Contains("data-moment-local=\"" + JobHelper.ToTimestamp(end) + "\"", html);
            Assert.DoesNotContain(H(Strings.RecurringJobsPage_RecurringJobDisabled_Tooltip), html);
            Assert.DoesNotContain(String.Format(H(Strings.RecurringJobsPage_EndsAt), "<span data-moment-local"), html);
        }

        [Fact]
        public void Render_KeepsThePausedAndErrorLabels_OfAJobWithAnEnd()
        {
            var end = DateTime.UtcNow.AddDays(-2);
            AddJob("paused", "0 * * * *", null, new Dictionary<string, string> { { "EndAt", JobHelper.SerializeDateTime(end) }, { "Paused", "1" } });
            AddJob("errored", "0 * * * *", null, new Dictionary<string, string> { { "EndAt", JobHelper.SerializeDateTime(end) }, { "Error", "Boom" } });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains(H(Strings.RecurringJobsPage_Paused_Tooltip), html);
            Assert.Contains("<span class=\"label label-danger text-uppercase\">" + H(Strings.Common_Error) + "</span>", html);
            Assert.DoesNotContain(H(Strings.RecurringJobsPage_Ended_Tooltip), html);
        }

        private static string LocalTime(DateTime value)
        {
            return "<span data-moment-local=\"" + JobHelper.ToTimestamp(value) + "\">";
        }

        [Fact]
        public void Render_FlagsAnUnknownTimeZone()
        {
            AddJob("on-mars", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string> { { "TimeZoneId", "Mars/Olympus_Mons" } });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("<span title=\"\" data-container=\"body\">Mars/Olympus_Mons", html);
            Assert.Contains("<span class=\"glyphicon glyphicon-exclamation-sign\" title=\"", html);
        }

        [Fact]
        public void Render_FlagsAnInvalidCronExpression()
        {
            AddJob("bad-cron", "every day", Next(TimeSpan.FromHours(3)));

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("<code title=\"", html);
            Assert.Contains("<span class=\"glyphicon glyphicon-exclamation-sign\"></span>", html);
            Assert.Contains("every day", html);
        }

        [Fact]
        public void Render_ShowsTheJobOrWhyItCouldNotBeLoaded()
        {
            AddJob("loaded", "0 3 * * *", Next(TimeSpan.FromHours(3)));
            AddJob("unloadable", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string>
            {
                { "Job", "{\"t\":\"Missing.Type, Missing.Assembly\",\"m\":\"Work\"}" },
            });
            AddJob("no-job", "0 3 * * *", Next(TimeSpan.FromHours(3)), new Dictionary<string, string> { { "Job", "" } });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("SampleJobs.Work", html);
            Assert.Contains("Missing.Assembly", html);
            Assert.Contains("<em>" + H(Strings.Common_NotAvailable) + "</em>", html);
        }

        [Fact]
        public void Render_ColoursTheLastExecution_ByTheStateOfTheLastJob()
        {
            var lastExecution = JobHelper.SerializeDateTime(DateTime.UtcNow.AddHours(-1));
            AddJob("succeeded", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "LastExecution", lastExecution }, { "LastJobId", "101" } });
            AddJob("custom", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "LastExecution", lastExecution }, { "LastJobId", "102" } });
            AddJob("canceled", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "LastExecution", lastExecution } });
            AddJob("never", "0 * * * *", Next(TimeSpan.FromMinutes(30)), new Dictionary<string, string> { { "CreatedAt", lastExecution } });
            _renderer.Connection.Setup(x => x.GetStateData("101")).Returns(new StateData { Name = SucceededState.StateName });
            _renderer.Connection.Setup(x => x.GetStateData("102")).Returns(new StateData { Name = "Custom" });

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("href=\"/jobs/details/101\"", html);
            Assert.Contains("label-state-success", html);
            Assert.Contains("href=\"/jobs/details/102\"", html);
            Assert.Contains("<span class=\"label label-default label-hover\" style=\"\">", html);
            Assert.DoesNotContain("label-state-inherit", html);
            Assert.Contains("label-state-inactive\" title=\"" + H(Strings.RecurringJobsPage_Canceled) + "\"", html);
            Assert.Contains("<em>N/A</em>", html);
            Assert.Contains("<em>" + H(Strings.Common_NotAvailable) + "</em>", html);
            Assert.Equal(1, CountOf(html, "label-state-inactive\" title="));
        }

        [Fact]
        public void Render_ListsTheJobsWithoutAPager_WhenTheConnectionIsNotAJobStorageConnection()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { "legacy" });
            connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:legacy")).Returns(new Dictionary<string, string> { { "Cron", "0 0 * * *" } });
            _renderer.Storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);

            var html = _renderer.Render(new RecurringJobsPage());

            Assert.Contains("value=\"legacy\"", html);
            Assert.DoesNotContain("paginator", html);
            Assert.Contains("<code title=\"", html);
            Assert.Contains("0 0 * * *", html);
        }

        private void AddJob(string id, string cron, DateTime? nextExecution, Dictionary<string, string> extra = null)
        {
            var hash = new Dictionary<string, string>
            {
                { "Cron", cron },
                { "Job", InvocationData.SerializeJob(Job.FromExpression(() => SampleJobs.Work())).SerializePayload() },
            };

            if (nextExecution.HasValue)
            {
                hash["NextExecution"] = JobHelper.SerializeDateTime(nextExecution.Value);
            }

            foreach (var pair in extra ?? new Dictionary<string, string>())
            {
                hash[pair.Key] = pair.Value;
            }

            _ids.Add(id);
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{id}")).Returns(hash);
        }

        private static DateTime Next(TimeSpan fromNow) => DateTime.UtcNow.Add(fromNow);

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
