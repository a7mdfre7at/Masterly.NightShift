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
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;

namespace Hangfire.Dashboard
{
    /// <summary>
    /// <c>POST /jobs/live</c> with <c>jobs[]</c> ids (or <c>GET /jobs/live?jobs=a,b</c>): the live view of
    /// each job for the dashboard's polling scripts: current state, server, elapsed time, last reported
    /// progress and the console entries. Read-only, so it works in read-only dashboards too.
    /// </summary>
    internal sealed class LiveJobsDispatcher : IDashboardDispatcher
    {
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

            var jobs = new Dictionary<string, object>();

            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                var now = GetUtcNow(context.Storage, connection);

                foreach (var jobId in jobIds.Take(MaxJobs))
                {
                    jobs[jobId] = GetLiveJob(connection, jobId, now);
                }

                await DashboardJson.WriteAsync(context, 200, new
                {
                    now = JobHelper.ToTimestamp(now),
                    consoleSupported = connection is JobStorageConnection,
                    jobs,
                }).ConfigureAwait(false);
            }
        }

        private static object GetLiveJob(IStorageConnection connection, string jobId, DateTime now)
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
            var entries = JobConsole.ReadEntries(connection, jobId);

            return new
            {
                state = stateData.Name,
                serverId = isProcessing ? serverId : null,
                startedAt = isProcessing && startedAt.HasValue ? JobHelper.ToTimestamp(startedAt.Value) : (long?)null,
                elapsedMs = isProcessing && startedAt.HasValue ? (long?)Math.Max(0, (long)(now - startedAt.Value).TotalMilliseconds) : null,
                progress,
                entries = entries.Count > MaxEntries ? entries.Skip(entries.Count - MaxEntries).ToList() : entries,
                truncated = entries.Count > MaxEntries,
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
                    result.AddRange(query.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                }
            }

            return result.Select(static x => x.Trim()).Where(static x => x.Length > 0).Distinct().ToList();
        }
    }
}
