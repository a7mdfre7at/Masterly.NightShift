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
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /search?q=</c> behind the dashboard's command palette: finds a background job by its exact id
    /// recurring jobs whose id contains the text and tags containing it (<c>tag:</c> searches tags only).
    /// Returns <c>{ jobs: [...], recurringJobs: [...], tags: [...] }</c>.
    /// </summary>
    internal sealed class SearchDispatcher : IDashboardDispatcher
    {
        private const int MaxRecurringJobs = 15;
        private const int MaxTags = 10;

        public Task Dispatch(DashboardContext context)
        {
            var query = (context.Request.GetQuery("q") ?? String.Empty).Trim();

            if (query.Length == 0)
            {
                return DashboardJson.WriteAsync(context, 200, new { jobs = Array.Empty<object>(), recurringJobs = Array.Empty<object>(), tags = Array.Empty<object>() });
            }

            var jobs = new List<object>();
            var recurringJobs = new List<object>();
            var tags = new List<object>();

            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                var jobData = TryGetJobData(connection, query);
                if (jobData != null)
                {
                    jobs.Add(new
                    {
                        id = query,
                        state = jobData.State,
                        name = jobData.Job != null ? jobData.Job.ToString() : jobData.LoadException?.InnerException?.Message,
                        createdAt = JobHelper.ToTimestamp(jobData.CreatedAt),
                        url = new UrlHelper(context).JobDetails(query),
                    });
                }

                var recurringIds = connection.GetAllItemsFromSet("recurring-jobs")
                    .Where(id => id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(id => id.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(static id => id, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRecurringJobs)
                    .ToList();

                foreach (var dto in connection.GetRecurringJobs(recurringIds))
                {
                    recurringJobs.Add(new
                    {
                        id = dto.Id,
                        cron = dto.Cron,
                        name = dto.Job?.ToString(),
                        paused = dto.Paused,
                        nextExecution = dto.NextExecution.HasValue ? JobHelper.ToTimestamp(dto.NextExecution.Value) : (long?)null,
                        url = new UrlHelper(context).To("/recurring?edit=" + Uri.EscapeDataString(dto.Id)),
                    });
                }

                // "tag:" narrows the search to tags; otherwise tags containing the text come along
                var tagQuery = query.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) ? query.Substring(4).Trim() : query;

                foreach (var tag in JobTags.GetAll(connection)
                    .Where(t => tagQuery.Length == 0 || t.Tag.IndexOf(tagQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(MaxTags))
                {
                    tags.Add(new
                    {
                        tag = tag.Tag,
                        count = tag.Count,
                        url = new UrlHelper(context).To("/tags/" + Uri.EscapeDataString(tag.Tag)),
                    });
                }
            }

            return DashboardJson.WriteAsync(context, 200, new { jobs, recurringJobs, tags });
        }

        private static JobData TryGetJobData(IStorageConnection connection, string jobId)
        {
            if (jobId.Length > 128 || jobId.Any(static ch => Char.IsWhiteSpace(ch)))
            {
                return null;
            }

            try
            {
                return connection.GetJobData(jobId);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // An id in a format the storage rejects (for example non-numeric for SQL Server) is simply not a job
                return null;
            }
        }
    }
}
