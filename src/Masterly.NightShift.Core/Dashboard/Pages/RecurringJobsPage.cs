using System.Globalization;
using System.Net;
using System.Text;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class RecurringJobsPage : RazorPage
{
    // The schedule editor sends the job's start and end with its preview requests, to the millisecond
    private static string ToUnixMilliseconds(DateTime value)
    {
        DateTime utc = ScheduleForecast.ToUtc(value) ?? value;
        return JobHelper.ToMillisecondTimestamp(utc).ToString(CultureInfo.InvariantCulture);
    }

    // The job /recurring?edit={id} names, when it isn't on this page
    private RecurringJobDto? FindJobToEdit(IStorageConnection connection, List<RecurringJobDto> shown)
    {
        string? id = IsReadOnly ? null : Query("edit");
        if (string.IsNullOrEmpty(id) || shown.Exists(job => job.Id == id))
        {
            return null;
        }

        try
        {
            List<RecurringJobDto> found = connection.GetRecurringJobs([id]);
            return found.Count == 1 && !found[0].Removed ? found[0] : null;
        }
        catch (ArgumentException)
        {
            // An identifier longer than the storage's keys allow names no job
            return null;
        }
    }

    // A row action that posts to one of the job's commands (/recurring/trigger-one/{id} and the others), named after
    // what it does to that job, such as "Trigger nightly now"
    private NonEscapedString RowAction(string path, string icon, string label, string jobId, string loadingText)
    {
        string name = WebUtility.HtmlEncode(string.Format(CultureInfo.CurrentCulture, label, jobId));
        return new NonEscapedString(
            $"<button type=\"button\" class=\"ns-icon-button\" data-ajax=\"{WebUtility.HtmlEncode(Url.To(path))}\" data-loading-text=\"{WebUtility.HtmlEncode(loadingText)}\" aria-label=\"{name}\" title=\"{name}\">{Html.Icon(icon)}</button>");
    }

    // The edit button carries what the schedule editor opens with: a row action, and the hidden button that
    // /recurring?edit={id} adds for a job of another page
    private NonEscapedString EditButton(RecurringJobDto job, bool hidden)
    {
        string name = string.Format(CultureInfo.CurrentCulture, Strings.RecurringJobsPage_EditJob, job.Id);
        StringBuilder builder = new();
        builder.Append("<button type=\"button\" class=\"ns-icon-button js-recurring-edit").Append(hidden ? " d-none" : null).Append("\" aria-haspopup=\"dialog\"");
        AppendAttribute(builder, "title", name);
        AppendAttribute(builder, "aria-label", name);
        AppendAttribute(builder, "data-id", job.Id);
        AppendAttribute(builder, "data-cron", job.Cron);
        AppendAttribute(builder, "data-timezone", job.TimeZoneId ?? TimeZoneInfo.Utc.Id);
        AppendAttribute(builder, "data-queue", job.Queue);
        AppendAttribute(builder, "data-misfire", job.MisfireHandling.ToString());
        AppendAttribute(builder, "data-start-at", job.StartAt.HasValue ? ToUnixMilliseconds(job.StartAt.Value) : null);
        AppendAttribute(builder, "data-end-at", job.EndAt.HasValue ? ToUnixMilliseconds(job.EndAt.Value) : null);
        builder.Append('>').Append(Html.Icon("pencil-fill")).Append("</button>");

        return new NonEscapedString(builder.ToString());
    }

    private static void AppendAttribute(StringBuilder builder, string name, string? value)
    {
        // Written empty when there's no value, as the page always wrote them
        builder.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
    }
}
