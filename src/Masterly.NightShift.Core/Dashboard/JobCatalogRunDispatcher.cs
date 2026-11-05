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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>POST /catalog/run</c>: creates a job from a <see cref="JobCatalog"/> entry and the form. Fields:
    /// <c>id</c> (the entry), <c>mode</c> (<c>enqueue</c>, <c>schedule</c> or <c>recurring</c>), <c>queue</c>,
    /// <c>delay</c> (minutes, for <c>schedule</c>), <c>recurringId</c> and <c>cron</c> (for <c>recurring</c>),
    /// and one <c>arg:{parameter}</c> field per parameter. Instead of <c>id</c>, <c>source</c> names an existing
    /// job whose method is run again with the given arguments. Answers <c>200 {jobId}</c> or
    /// <c>{recurringId}</c>, <c>404</c> for an unknown entry or job, <c>422 {error}</c> for bad input.
    /// </summary>
    internal sealed class JobCatalogRunDispatcher : IDashboardDispatcher
    {
        private const string ArgumentPrefix = "arg:";
        private const int MaxDelayMinutes = 60 * 24 * 365;

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

            var id = await GetFormValueAsync(context, "id").ConfigureAwait(false);
            var source = await GetFormValueAsync(context, "source").ConfigureAwait(false);

            if (String.IsNullOrEmpty(id) && String.IsNullOrEmpty(source))
            {
                await DashboardJson.WriteAsync(context, 422, new { error = "The catalog entry id or the source job id is required." }).ConfigureAwait(false);
                return;
            }

            JobCatalogEntry entry;

            if (!String.IsNullOrEmpty(source))
            {
                // "Run again with changes": the method comes from an existing job, catalogued or not
                Job sourceJob;
                using (var connection = context.Storage.GetReadOnlyConnection())
                {
                    sourceJob = connection.GetJobData(source)?.Job;
                }

                if (sourceJob == null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                entry = JobCatalog.CreateEntry(sourceJob);
            }
            else
            {
                entry = JobCatalog.Find(id);
                if (entry == null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
            }

            var mode = (await GetFormValueAsync(context, "mode").ConfigureAwait(false) ?? "enqueue").ToLowerInvariant();
            var queue = await GetFormValueAsync(context, "queue").ConfigureAwait(false);

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in entry.UserParameters)
            {
                arguments[parameter.Name] = await GetFormValueAsync(context, ArgumentPrefix + parameter.Name).ConfigureAwait(false);
            }

            try
            {
                var job = JobCatalog.CreateJob(entry, arguments, queue);

                switch (mode)
                {
                    case "enqueue":
                    {
                        var jobId = context.GetBackgroundJobClient().Create(job, new EnqueuedState(String.IsNullOrWhiteSpace(queue) ? EnqueuedState.DefaultQueue : queue));
                        AuditLog.Record(context, new[] { jobId }, $"{entry.Type.Name}.{entry.Method.Name} enqueued", 200);
                        await DashboardJson.WriteAsync(context, 200, new { jobId }).ConfigureAwait(false);
                        return;
                    }

                    case "schedule":
                    {
                        // A number of minutes, or a phrase such as "tomorrow at 9am" or "in 2 hours"
                        var delayText = await GetFormValueAsync(context, "delay").ConfigureAwait(false);
                        DateTime enqueueAt;

                        if (Double.TryParse(delayText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
                        {
                            if (minutes < 0 || minutes > MaxDelayMinutes)
                            {
                                throw new ArgumentException("The delay must be a number of minutes between 0 and one year.");
                            }

                            enqueueAt = DateTime.UtcNow.AddMinutes(minutes);
                        }
                        else if (String.IsNullOrWhiteSpace(delayText) || !TimeText.TryParse(delayText, DateTime.UtcNow, null, out enqueueAt))
                        {
                            throw new ArgumentException("The delay must be a number of minutes or a phrase like \"in 2 hours\" or \"tomorrow at 9am\".");
                        }

                        var jobId = context.GetBackgroundJobClient().Create(job, new ScheduledState(enqueueAt));
                        AuditLog.Record(context, new[] { jobId }, $"{entry.Type.Name}.{entry.Method.Name} scheduled for {enqueueAt:u}", 200);
                        await DashboardJson.WriteAsync(context, 200, new { jobId }).ConfigureAwait(false);
                        return;
                    }

                    case "recurring":
                    {
                        var recurringId = await GetFormValueAsync(context, "recurringId").ConfigureAwait(false);
                        var cronText = await GetFormValueAsync(context, "cron").ConfigureAwait(false);

                        if (String.IsNullOrWhiteSpace(recurringId))
                        {
                            throw new ArgumentException("A recurring job needs an identifier.");
                        }

                        if (String.IsNullOrWhiteSpace(cronText))
                        {
                            throw new ArgumentException("A recurring job needs a schedule.");
                        }

                        // The queue travels with the job itself (Job.Queue), so the options only carry the time zone
                        context.GetRecurringJobManager().AddOrUpdate(recurringId.Trim(), job, Cron.FromText(cronText), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
                        AuditLog.Record(context, new[] { recurringId.Trim() }, $"{entry.Type.Name}.{entry.Method.Name} as a recurring job", 200);
                        await DashboardJson.WriteAsync(context, 200, new { recurringId = recurringId.Trim() }).ConfigureAwait(false);
                        return;
                    }

                    default:
                        throw new ArgumentException($"Unknown mode '{mode}'.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException || ex is InvalidOperationException)
            {
                await DashboardJson.WriteAsync(context, 422, new { error = ex.Message }).ConfigureAwait(false);
            }
        }

        private static async Task<string> GetFormValueAsync(DashboardContext context, string key)
        {
            var values = await context.Request.GetFormValuesAsync(key).ConfigureAwait(false);
            return values?.FirstOrDefault();
        }
    }
}
