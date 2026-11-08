using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>POST /catalog/run</c>: creates a job from a <see cref="JobCatalog"/> entry and the form. Fields:
    /// <c>id</c> (the entry), <c>mode</c> (<c>enqueue</c>, <c>schedule</c> or <c>recurring</c>), <c>queue</c>,
    /// <c>delay</c> (minutes, for <c>schedule</c>), <c>recurringId</c> and <c>cron</c> (for <c>recurring</c>),
    /// and one <c>arg:{parameter}</c> field per parameter (several for a checkbox, which also sends a hidden
    /// <c>false</c>, and for a <see cref="FlagsAttribute"/> enum, whose choices are combined). Instead of <c>id</c>, <c>source</c> names an existing
    /// job whose method is run again with the given arguments, in that job's own queue (<see cref="Job.Queue"/>)
    /// when <c>queue</c> is empty. Answers <c>200 {jobId}</c> or
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
                AuditLog.Record(context, null, null, 422);
                await DashboardJson.WriteAsync(context, 422, new { error = "The catalog entry id or the source job id is required." }).ConfigureAwait(false);
                return;
            }

            JobCatalogEntry entry;
            Job sourceJob = null;

            if (!String.IsNullOrEmpty(source))
            {
                // "Run again with changes": the method comes from an existing job, catalogued or not
                JobData sourceData;
                using (var connection = context.Storage.GetReadOnlyConnection())
                {
                    sourceData = connection.GetJobData(source);
                }

                if (sourceData == null)
                {
                    AuditLog.Record(context, new[] { source }, null, (int)HttpStatusCode.NotFound);
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                sourceJob = sourceData.Job;
                if (sourceJob == null)
                {
                    // The job exists, but its type or method isn't loaded in this process (a dashboard hosted
                    // without the job's assemblies, or code that changed since)
                    var reason = sourceData.LoadException?.InnerException?.Message ?? sourceData.LoadException?.Message ?? "its method can't be loaded here";
                    AuditLog.Record(context, new[] { source }, null, 422);
                    await DashboardJson.WriteAsync(context, 422, new { error = $"Job {source} can't be run again from this dashboard: {reason}" }).ConfigureAwait(false);
                    return;
                }

                entry = JobCatalog.CreateEntry(sourceJob);
            }
            else
            {
                entry = JobCatalog.Find(id);
                if (entry == null)
                {
                    AuditLog.Record(context, new[] { id }, null, (int)HttpStatusCode.NotFound);
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
            }

            var mode = (await GetFormValueAsync(context, "mode").ConfigureAwait(false) ?? "enqueue").ToLowerInvariant();
            var queue = await GetFormValueAsync(context, "queue").ConfigureAwait(false);

            if (String.IsNullOrWhiteSpace(queue) && sourceJob?.Queue != null)
            {
                // Run again in the job's own queue (Enqueue("emails", ...)), not in "default"
                queue = sourceJob.Queue;
            }

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in entry.UserParameters)
            {
                // A checkbox comes with a hidden "false", a multiple choice with one value per choice
                var values = await context.Request.GetFormValuesAsync(ArgumentPrefix + parameter.Name).ConfigureAwait(false);
                arguments[parameter.Name] = JobCatalog.CombineFormValues(parameter, values?.ToArray());
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
                            if (Double.IsNaN(minutes) || minutes < 0 || minutes > MaxDelayMinutes)
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
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException || ex is InvalidOperationException || ex is OverflowException)
            {
                // The message stays out of the audit log: it can quote what was typed, [Redacted] arguments included
                AuditLog.Record(context, String.IsNullOrEmpty(source) ? null : new[] { source }, $"{entry.Type.Name}.{entry.Method.Name} not run ({mode})", 422);
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
