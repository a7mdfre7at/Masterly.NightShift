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

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>POST /jobs/notes/add/{jobId}</c> with form fields <c>text</c> and <c>ack</c> (<c>true</c> to acknowledge):
    /// adds a <see cref="JobNote"/>. Answers <c>204</c>, <c>404</c> for an unknown job, <c>422</c> for an empty note.
    /// </summary>
    internal sealed class JobNotesDispatcher : IDashboardDispatcher
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

            var jobId = Uri.UnescapeDataString(context.UriMatch.Groups["JobId"].Value);
            var text = (await context.Request.GetFormValuesAsync("text").ConfigureAwait(false))?.FirstOrDefault() ?? String.Empty;
            var ackValue = (await context.Request.GetFormValuesAsync("ack").ConfigureAwait(false))?.FirstOrDefault();
            var acknowledges = ackValue is "true" or "on" or "1";

            if (String.IsNullOrWhiteSpace(text) && !acknowledges)
            {
                context.Response.StatusCode = 422;
                return;
            }

            using (var connection = context.Storage.GetConnection())
            {
                if (connection.GetJobData(jobId) == null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                JobNotes.Add(connection, jobId, text, context.GetUserName(), acknowledges);
            }

            AuditLog.Record(context, new[] { jobId }, acknowledges ? "acknowledged" + (text.Length > 0 ? ": " + text.Trim() : String.Empty) : text.Trim());
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }
    }
}
