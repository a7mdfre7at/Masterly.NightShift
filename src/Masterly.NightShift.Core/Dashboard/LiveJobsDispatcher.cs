using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>POST /jobs/live</c> with <c>jobs[]</c> ids (or <c>GET /jobs/live?jobs=a,b</c>): the live view of
    /// each job for the dashboard's polling scripts: current state, server, elapsed time, last reported
    /// progress and the console entries. With <c>console=0</c> (a form field, or a query parameter of the
    /// <c>GET</c>) the console isn't read and <c>entries</c> is left out: the Processing page's rows only show the
    /// state and the progress. Read-only, so it works in read-only dashboards too.
    /// </summary>
    internal sealed class LiveJobsDispatcher : IDashboardDispatcher
    {
        private static readonly char[] IdSeparators = { ',' };

        private const int MaxJobs = 100;
        private const int MaxEntries = 300;

        public async Task Dispatch(DashboardContext context)
        {
            var jobIds = await GetJobIdsAsync(context).ConfigureAwait(false);

            if (jobIds.Count == 0)
            {
                context.Response.StatusCode = 422;
                return;
            }

            var withConsole = await GetConsoleFlagAsync(context).ConfigureAwait(false);
            var jobs = new Dictionary<string, object>();

            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                var now = GetUtcNow(context.Storage, connection);

                foreach (var jobId in jobIds.Take(MaxJobs))
                {
                    jobs[jobId] = GetLiveJob(connection, jobId, now, withConsole);
                }

                await DashboardJson.WriteAsync(context, 200, new
                {
                    now = JobHelper.ToTimestamp(now),
                    consoleSupported = connection is JobStorageConnection,
                    jobs,
                }).ConfigureAwait(false);
            }
        }

        private static object GetLiveJob(IStorageConnection connection, string jobId, DateTime now, bool withConsole)
        {
            var stateData = connection.GetStateData(jobId);
            if (stateData == null)
            {
                return null;
            }

            DateTime? startedAt = null;
            string serverId = null;

            if (stateData.Data != null)
            {
                if (stateData.Data.TryGetValue("StartedAt", out var startedAtValue))
                {
                    startedAt = JobHelper.DeserializeNullableDateTime(startedAtValue);
                }

                stateData.Data.TryGetValue("ServerId", out serverId);
            }

            var isProcessing = ProcessingState.StateName.Equals(stateData.Name, StringComparison.OrdinalIgnoreCase);
            var progress = JobConsole.ReadProgress(connection, jobId);
            // The console can hold thousands of long lines; the rows of the Processing page don't show it
            var entries = withConsole ? JobConsole.ReadEntries(connection, jobId) : null;

            return new
            {
                state = stateData.Name,
                serverId = isProcessing ? serverId : null,
                startedAt = isProcessing && startedAt.HasValue ? JobHelper.ToTimestamp(startedAt.Value) : (long?)null,
                elapsedMs = isProcessing && startedAt.HasValue ? (long?)Math.Max(0, (long)(now - startedAt.Value).TotalMilliseconds) : null,
                progress,
                entries = entries == null ? null : entries.Count > MaxEntries ? entries.Skip(entries.Count - MaxEntries).ToList() : entries,
                truncated = entries != null && entries.Count > MaxEntries,
            };
        }

        private static DateTime GetUtcNow(JobStorage storage, IStorageConnection connection)
        {
            if (storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime) && connection is JobStorageConnection storageConnection)
            {
                return storageConnection.GetUtcDateTime();
            }

            return DateTime.UtcNow;
        }

        private static async Task<bool> GetConsoleFlagAsync(DashboardContext context)
        {
            string value;

            if ("POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                var values = await context.Request.GetFormValuesAsync("console").ConfigureAwait(false);
                value = values?.FirstOrDefault();
            }
            else
            {
                value = context.Request.GetQuery("console");
            }

            return !("0".Equals(value?.Trim(), StringComparison.Ordinal) || "false".Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<List<string>> GetJobIdsAsync(DashboardContext context)
        {
            var result = new List<string>();

            if ("POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                var values = await context.Request.GetFormValuesAsync("jobs[]").ConfigureAwait(false);
                result.AddRange(values ?? new List<string>());
            }
            else
            {
                var query = context.Request.GetQuery("jobs");
                if (!String.IsNullOrWhiteSpace(query))
                {
                    result.AddRange(query.Split(IdSeparators, StringSplitOptions.RemoveEmptyEntries));
                }
            }

            return result.Select(static x => x.Trim()).Where(static x => x.Length > 0).Distinct().ToList();
        }
    }
}
