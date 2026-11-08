using System;
using System.Threading.Tasks;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /health</c>: a JSON health report for load balancers, Kubernetes probes and uptime monitors.
    /// <c>200</c> with <c>status: healthy</c> or <c>degraded</c>, <c>503</c> with <c>status: unhealthy</c> when
    /// the storage is unreachable or no server is alive. See <see cref="DashboardHealth"/> for the checks; a
    /// report is reused for <see cref="DashboardHealth.CacheDuration"/>, so frequent probes cost no more storage
    /// queries. Like every dashboard route it is subject to the dashboard's authorization filters; <c>/ping</c>
    /// is the cheaper, storage-free liveness URL.
    /// </summary>
    internal sealed class HealthDispatcher : IDashboardDispatcher
    {
        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        public Task Dispatch(DashboardContext context)
        {
            var report = DashboardHealth.GetReport(context.Storage, context.Options, Clock());
            var statusCode = report.Status == HealthStatus.Unhealthy ? 503 : 200;

            return DashboardJson.WriteAsync(context, statusCode, report.ToJson());
        }
    }
}
