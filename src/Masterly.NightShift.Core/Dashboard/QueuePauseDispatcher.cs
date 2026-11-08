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

            var queue = RouteValues.Get(context, "Queue").Trim();
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
