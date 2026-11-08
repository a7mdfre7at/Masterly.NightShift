using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard.Pages
{
    partial class RecurringJobsPage
    {
        // The schedule editor sends the job's start and end with its preview requests, to the millisecond
        private static string ToUnixMilliseconds(DateTime value)
        {
            var utc = ScheduleForecast.ToUtc(value) ?? value;
            return JobHelper.ToMillisecondTimestamp(utc).ToString(CultureInfo.InvariantCulture);
        }

        // The job /recurring?edit={id} names, when it isn't on this page
        private RecurringJobDto FindJobToEdit(IStorageConnection connection, List<RecurringJobDto> shown)
        {
            var id = IsReadOnly ? null : Query("edit");
            if (String.IsNullOrEmpty(id) || shown.Exists(job => job.Id == id)) return null;

            try
            {
                var found = connection.GetRecurringJobs(new[] { id });
                return found.Count == 1 && !found[0].Removed ? found[0] : null;
            }
            catch (ArgumentException)
            {
                // An identifier longer than the storage's keys allow names no job
                return null;
            }
        }

        // The edit button carries what the schedule editor opens with. The row's button and the hidden one that
        // /recurring?edit={id} adds for a job of another page are the same.
        private static NonEscapedString EditButton(RecurringJobDto job, bool hidden)
        {
            var builder = new StringBuilder();
            builder.Append("<button type=\"button\" class=\"btn btn-xs btn-default js-recurring-edit").Append(hidden ? " hidden" : null).Append('"');
            AppendAttribute(builder, "title", Strings.RecurringJobsPage_EditSchedule);
            AppendAttribute(builder, "data-id", job.Id);
            AppendAttribute(builder, "data-cron", job.Cron);
            AppendAttribute(builder, "data-timezone", job.TimeZoneId ?? TimeZoneInfo.Utc.Id);
            AppendAttribute(builder, "data-queue", job.Queue);
            AppendAttribute(builder, "data-misfire", job.MisfireHandling.ToString());
            AppendAttribute(builder, "data-start-at", job.StartAt.HasValue ? ToUnixMilliseconds(job.StartAt.Value) : null);
            AppendAttribute(builder, "data-end-at", job.EndAt.HasValue ? ToUnixMilliseconds(job.EndAt.Value) : null);
            builder.Append("><span class=\"glyphicon glyphicon-pencil\"></span> <span class=\"hidden-xs\">")
                .Append(WebUtility.HtmlEncode(Strings.RecurringJobsPage_Edit))
                .Append("</span></button>");

            return new NonEscapedString(builder.ToString());
        }

        private static void AppendAttribute(StringBuilder builder, string name, string value)
        {
            // Written empty when there's no value, as the page always wrote them
            builder.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
        }
    }
}
