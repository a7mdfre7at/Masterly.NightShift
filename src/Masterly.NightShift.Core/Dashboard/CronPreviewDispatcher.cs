using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /recurring/preview?cron=...&amp;timezone=...&amp;start=...&amp;end=...</c>: resolves plain-English
    /// schedules and returns the next occurrences, so the schedule editor can show what it is about to save.
    /// The optional <c>start</c> and <c>end</c> (milliseconds since 1970-01-01 UTC) are the job's
    /// <see cref="RecurringJobOptions.StartAt"/> and <see cref="RecurringJobOptions.EndAt"/>: occurrences
    /// before the start or after the end are left out, as the scheduler skips them.
    /// Always answers <c>200</c>; problems are reported in the <c>error</c> field.
    /// </summary>
    internal sealed class CronPreviewDispatcher : IDashboardDispatcher
    {
        private static readonly char[] FieldSeparators = { ' ', '\t' };

        private const int OccurrenceCount = 5;

        public Task Dispatch(DashboardContext context)
        {
            var text = context.Request.GetQuery("cron");
            var timeZoneId = context.Request.GetQuery("timezone");
            var startAt = ParseInstant(context.Request.GetQuery("start"));
            var endAt = ParseInstant(context.Request.GetQuery("end"));

            if (String.IsNullOrWhiteSpace(text))
            {
                return DashboardJson.WriteAsync(context, 200, new { error = "A schedule is required." });
            }

            try
            {
                var cron = Cron.FromText(text.Trim());
                var timeZone = String.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Utc
                    : RecurringJobUpdateDispatcher.GetTimeZoneResolver(context).GetTimeZoneById(timeZoneId.Trim());

                // An occurrence exactly at the start runs, so the search begins just before it
                var now = DateTime.UtcNow;
                var from = startAt.HasValue && startAt.Value > now ? startAt.Value.AddTicks(-1) : now;

                var next = Cron.GetNextOccurrences(cron, timeZone, from, OccurrenceCount)
                    .Where(x => !endAt.HasValue || x <= endAt.Value)
                    .Select(static x => JobHelper.ToTimestamp(x))
                    .ToArray();

                var hasSeconds = cron.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries).Length == 6;

                return DashboardJson.WriteAsync(context, 200, new { cron, description = Cron.TryDescribe(cron), hasSeconds, timeZone = timeZone.Id, next });
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
            {
                return DashboardJson.WriteAsync(context, 200, new { error = RecurringJobUpdateDispatcher.GetMessage(ex) });
            }
        }

        private static DateTime? ParseInstant(string value)
        {
            if (String.IsNullOrWhiteSpace(value) ||
                !Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
