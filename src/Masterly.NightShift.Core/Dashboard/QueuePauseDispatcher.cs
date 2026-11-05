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
    /// <c>POST /queues/pause/{queue}</c> (form field <c>reason</c>, optional) holds the jobs of one queue while
    /// the others keep flowing; <c>POST /queues/resume/{queue}</c> lets them go again. Answers <c>204</c>.
    /// </summary>
    internal sealed class QueuePauseDispatcher : IDashboardDispatcher
    {
        private const int MaxReasonLength = 200;
        private readonly bool _pause;

        public QueuePauseDispatcher(bool pause)
        {
            _pause = pause;
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

            var queue = Uri.UnescapeDataString(context.UriMatch.Groups["Queue"].Value).Trim();
            if (queue.Length == 0)
            {
                context.Response.StatusCode = 422;
                return;
            }

            string reason = null;
            using (var connection = context.Storage.GetConnection())
            {
                if (_pause)
                {
                    reason = (await context.Request.GetFormValuesAsync("reason").ConfigureAwait(false))?.FirstOrDefault()?.Trim();
                    if (!String.IsNullOrEmpty(reason) && reason.Length > MaxReasonLength) reason = reason.Substring(0, MaxReasonLength);

                    DrainMode.PauseQueue(connection, queue, String.IsNullOrEmpty(reason) ? null : reason);
                }
                else
                {
                    DrainMode.ResumeQueue(connection, queue);
                }
            }

            AuditLog.Record(context, new[] { queue }, reason);
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }
    }
}
