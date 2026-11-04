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

using System.Threading.Tasks;

namespace Hangfire.Dashboard
{
    /// <summary>
    /// <c>GET /ping</c>: answers <c>200 pong</c> without touching the storage. The cheapest URL for
    /// <see cref="Server.KeepAliveProcess"/> and for load-balancer liveness checks. Like every dashboard route it
    /// is subject to the dashboard's authorization filters.
    /// </summary>
    internal sealed class PingDispatcher : IDashboardDispatcher
    {
        public Task Dispatch(DashboardContext context)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
            context.Response.SetExpire(null);
            return context.Response.WriteAsync("pong");
        }
    }
}
