// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// Runs a command for every id posted in <c>jobs[]</c>: <c>204</c> when all went through. An id whose job type
    /// isn't loaded in this process (a dashboard hosted without the job assemblies) doesn't stop the others; the
    /// request then answers <c>422 {error}</c> naming each such id and what couldn't be loaded. The action is
    /// recorded in the audit log in every case, with what was done.
    /// </summary>
    internal sealed class BatchCommandDispatcher : IDashboardCommandDispatcher
    {
        private readonly Action<DashboardContext, string> _command;

        public BatchCommandDispatcher(Action<DashboardContext, string> command)
        {
            _command = command;
        }

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

            var jobIds = await context.Request.GetFormValuesAsync("jobs[]").ConfigureAwait(false);
            if (jobIds.Count == 0)
            {
                context.Response.StatusCode = 422;
                return;
            }

            var failures = new List<string>();
            var done = 0;
            var status = (int)HttpStatusCode.InternalServerError;

            try
            {
                foreach (var jobId in jobIds)
                {
                    try
                    {
                        _command(context, jobId);
                        done++;
                    }
                    catch (Exception ex) when (TryGetLoadFailure(ex, out var reason))
                    {
                        // "Trigger now" builds the job, so it needs its type; the other ids may not
                        failures.Add($"{jobId} ({reason})");
                    }
                }

                status = failures.Count == 0 ? (int)HttpStatusCode.NoContent : 422;
            }
            finally
            {
                // Recorded even when an id failed midway, with what was done before it
                AuditLog.Record(context, jobIds, status == (int)HttpStatusCode.NoContent ? null : $"{done} of {jobIds.Count} done{(failures.Count > 0 ? "; not loaded: " + String.Join(", ", failures) : null)}", status);
            }

            if (failures.Count == 0)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            await DashboardJson.WriteAsync(context, 422, new
            {
                error = $"{failures.Count} of {jobIds.Count} could not be handled, because their job type can't be loaded in the dashboard's process: {String.Join("; ", failures)}. This action needs the job assemblies.",
            }).ConfigureAwait(false);
        }

        /// <summary>Whether the exception says a job's type or method couldn't be loaded, and why.</summary>
        internal static bool TryGetLoadFailure(Exception exception, out string reason)
        {
            var load = exception as JobLoadException
                ?? (exception as AggregateException)?.Flatten().InnerExceptions.OfType<JobLoadException>().FirstOrDefault();

            reason = load?.InnerException?.Message ?? load?.Message;
            return load != null;
        }
    }
}
