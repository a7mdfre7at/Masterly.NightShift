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
using System.Threading.Tasks;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /health</c>: a JSON health report for load balancers, Kubernetes probes and uptime monitors.
    /// <c>200</c> with <c>status: healthy</c> or <c>degraded</c>, <c>503</c> with <c>status: unhealthy</c> when
    /// the storage is unreachable or no server is alive. See <see cref="DashboardHealth"/> for the checks.
    /// Like every dashboard route it is subject to the dashboard's authorization filters; <c>/ping</c> is the
    /// cheaper, storage-free liveness URL.
    /// </summary>
    internal sealed class HealthDispatcher : IDashboardDispatcher
    {
        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        public Task Dispatch(DashboardContext context)
        {
            var report = DashboardHealth.Check(context.Storage, context.Options, Clock());
            var statusCode = report.Status == HealthStatus.Unhealthy ? 503 : 200;

            return DashboardJson.WriteAsync(context, statusCode, report.ToJson());
        }
    }
}
