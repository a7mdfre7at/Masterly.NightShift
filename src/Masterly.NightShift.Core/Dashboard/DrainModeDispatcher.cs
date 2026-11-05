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
    /// <c>POST /maintenance/pause</c> (form field <c>reason</c>, optional) turns <see cref="DrainMode"/> on,
    /// <c>POST /maintenance/resume</c> turns it off; <c>GET</c> on either answers the current status as JSON.
    /// </summary>
    internal sealed class DrainModeDispatcher : IDashboardDispatcher
    {
        private const int MaxReasonLength = 200;
        private readonly bool _enable;

        public DrainModeDispatcher(bool enable)
        {
            _enable = enable;
        }

        public async Task Dispatch(DashboardContext context)
        {
            if ("GET".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                await WriteStatusAsync(context).ConfigureAwait(false);
                return;
            }

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

            using (var connection = context.Storage.GetConnection())
            {
                if (_enable)
                {
                    var reasons = await context.Request.GetFormValuesAsync("reason").ConfigureAwait(false);
                    var reason = reasons?.FirstOrDefault()?.Trim();
                    if (!String.IsNullOrEmpty(reason) && reason.Length > MaxReasonLength)
                    {
                        reason = reason.Substring(0, MaxReasonLength);
                    }

                    DrainMode.Enable(connection, String.IsNullOrEmpty(reason) ? null : reason);
                    AuditLog.Record(context, null, String.IsNullOrEmpty(reason) ? null : reason, 200);
                }
                else
                {
                    DrainMode.Disable(connection);
                    AuditLog.Record(context, null, null, 200);
                }
            }

            await WriteStatusAsync(context).ConfigureAwait(false);
        }

        private static Task WriteStatusAsync(DashboardContext context)
        {
            DrainModeStatus status;
            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                status = DrainMode.Read(connection);
            }

            return DashboardJson.WriteAsync(context, 200, new
            {
                status.Active,
                status.Since,
                status.Reason,
                holdSeconds = (int)DrainMode.HoldInterval.TotalSeconds,
            });
        }
    }
}
