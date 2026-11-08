using System.Threading.Tasks;

namespace Masterly.NightShift.Dashboard
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
