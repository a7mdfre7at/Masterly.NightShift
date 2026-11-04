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
using System.Net;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.Storage;

namespace Hangfire.Dashboard
{
    /// <summary>
    /// <c>POST /maintenance/clear-jobs</c> (keeps recurring jobs and servers) and <c>POST /maintenance/reset</c>
    /// (keeps nothing): purge the storage through <see cref="JobStorage.Purge"/>. Answers <c>204</c>, or
    /// <c>422 {error}</c> when the storage doesn't support purging.
    /// </summary>
    internal sealed class MaintenanceDispatcher : IDashboardDispatcher
    {
        private readonly bool _keepRecurringJobsAndServers;

        public MaintenanceDispatcher(bool keepRecurringJobsAndServers)
        {
            _keepRecurringJobsAndServers = keepRecurringJobsAndServers;
        }

        public Task Dispatch(DashboardContext context)
        {
            if (context.IsReadOnly)
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            }

            if (!"POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return Task.CompletedTask;
            }

            if (!context.Storage.HasFeature(JobStorageFeatures.Purge))
            {
                return DashboardJson.WriteAsync(context, 422, new { error = JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Purge).Message });
            }

            try
            {
                context.Storage.Purge(new StoragePurgeOptions
                {
                    KeepRecurringJobs = _keepRecurringJobsAndServers,
                    KeepServers = _keepRecurringJobsAndServers,
                });

                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return DashboardJson.WriteAsync(context, 500, new { error = ex.Message });
            }
        }
    }
}
