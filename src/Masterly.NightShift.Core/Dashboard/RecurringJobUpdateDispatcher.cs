// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>POST /recurring/update/{id}</c> with form fields <c>cron</c> (a CRON expression or plain English),
    /// <c>timezone</c>, <c>queue</c> and <c>misfire</c>. Empty fields keep their current value. Answers
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

            var recurringJobId = context.UriMatch.Groups["JobId"].Value;
            var cronText = await GetFormValueAsync(context, "cron").ConfigureAwait(false);
            var timeZoneId = await GetFormValueAsync(context, "timezone").ConfigureAwait(false);
            var queue = await GetFormValueAsync(context, "queue").ConfigureAwait(false);
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
                AuditLog.Record(context, new[] { recurringJobId }, cron != null ? $"cron {cron}" : null, 200);

                if (!updated)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                await DashboardJson.WriteAsync(context, (int)HttpStatusCode.OK, new { cron }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException || ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
            {
                await DashboardJson.WriteAsync(context, 422, new { error = GetMessage(ex) }).ConfigureAwait(false);
            }
        }

        internal static ITimeZoneResolver GetTimeZoneResolver(DashboardContext context)
        {
            return context.Options.TimeZoneResolver ?? new DefaultTimeZoneResolver();
        }

        internal static string GetMessage(Exception exception)
        {
            // Validation errors carry the useful text in the inner exception (for example Cronos' message)
            return exception.InnerException != null && exception is ArgumentException
                ? $"{exception.Message} {exception.InnerException.Message}"
                : exception.Message;
        }

        private static async Task<string> GetFormValueAsync(DashboardContext context, string key)
        {
            var values = await context.Request.GetFormValuesAsync(key).ConfigureAwait(false);
            var value = values?.FirstOrDefault();

            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
