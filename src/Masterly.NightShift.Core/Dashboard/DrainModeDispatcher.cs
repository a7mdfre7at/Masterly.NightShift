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
