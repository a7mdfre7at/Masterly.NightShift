using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>POST /recurring/update/{id}</c> with form fields <c>cron</c> (a CRON expression or plain English),
    /// <c>timezone</c>, <c>queue</c> and <c>misfire</c>. Missing or empty fields keep their current value, except
    /// <c>queue</c>: sent empty, it puts the job back in the default queue (the schedule editor always sends it,
    /// pre-filled with the current queue). Answers
    /// <c>200 {cron}</c>, <c>404</c> for an unknown job, or <c>422 {error}</c> for input the manager rejects.
    /// </summary>
    internal sealed class RecurringJobUpdateDispatcher : IDashboardDispatcher
    {
        public async Task Dispatch(DashboardContext context)
        {
            if (context.IsReadOnly)
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (!"POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            var recurringJobId = RouteValues.Get(context, "JobId");
            var cronText = await GetFormValueAsync(context, "cron").ConfigureAwait(false);
            var timeZoneId = await GetFormValueAsync(context, "timezone").ConfigureAwait(false);
            var queue = await GetQueueAsync(context).ConfigureAwait(false);
            var misfireText = await GetFormValueAsync(context, "misfire").ConfigureAwait(false);

            try
            {
                var cron = cronText != null ? Cron.FromText(cronText) : null;
                var timeZone = timeZoneId != null ? GetTimeZoneResolver(context).GetTimeZoneById(timeZoneId) : null;

                MisfireHandlingMode? misfire = null;
                if (misfireText != null)
                {
                    if (!Enum.TryParse<MisfireHandlingMode>(misfireText, ignoreCase: true, out var parsed) || !Enum.IsDefined(typeof(MisfireHandlingMode), parsed))
                    {
                        throw new ArgumentException($"Unknown misfire handling mode '{misfireText}'.");
                    }

                    misfire = parsed;
                }

                var updated = context.GetRecurringJobManager().UpdateSchedule(recurringJobId, cron, timeZone, queue, misfire);

                // Recorded with the status the request answers, so an unknown job isn't logged as a change
                AuditLog.Record(context, new[] { recurringJobId }, cron != null ? $"cron {cron}" : null, updated ? (int)HttpStatusCode.OK : (int)HttpStatusCode.NotFound);

                if (!updated)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                await DashboardJson.WriteAsync(context, (int)HttpStatusCode.OK, new { cron }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException || ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
            {
                AuditLog.Record(context, new[] { recurringJobId }, cronText != null ? $"cron {cronText}" : null, 422);
                await DashboardJson.WriteAsync(context, 422, new { error = GetMessage(ex) }).ConfigureAwait(false);
            }
        }

        internal static ITimeZoneResolver GetTimeZoneResolver(DashboardContext context)
        {
            return context.Options.TimeZoneResolver ?? GlobalTimeZoneResolver.Current;
        }

        internal static string GetMessage(Exception exception)
        {
            // Validation errors carry the useful text in the inner exception (for example Cronos' message)
            return exception.InnerException != null && exception is ArgumentException
                ? $"{exception.Message} {exception.InnerException.Message}"
                : exception.Message;
        }

        // Missing keeps the queue. Sent empty, it means the default queue: UpdateSchedule then also clears the queue
        // stored in the job itself, so its retries leave the old queue too.
        private static async Task<string> GetQueueAsync(DashboardContext context)
        {
            var values = await context.Request.GetFormValuesAsync("queue").ConfigureAwait(false);
            if (values == null || values.Count == 0) return null;

            var value = values[0];
            return String.IsNullOrWhiteSpace(value) ? EnqueuedState.DefaultQueue : value.Trim();
        }

        private static async Task<string> GetFormValueAsync(DashboardContext context, string key)
        {
            var values = await context.Request.GetFormValuesAsync(key).ConfigureAwait(false);
            var value = values?.FirstOrDefault();

            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
