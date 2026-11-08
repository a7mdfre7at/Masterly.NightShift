#if NET8_0_OR_GREATER
#nullable enable

using System;
using Masterly.NightShift.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Masterly.NightShift
{
    /// <summary>
    /// Maps the dashboard's <c>/health</c> report and <c>/metrics</c> text as endpoints of their own, outside the
    /// dashboard and its authorization filters, so a probe or a Prometheus scraper can reach them without signing in.
    /// Add endpoint conventions such as <c>RequireHost</c> or <c>RequireAuthorization</c> to limit who can.
    /// </summary>
    public static class NightShiftMonitoringEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps <c>GET {pattern}</c> to the JSON health report the dashboard serves at <c>/health</c>: <c>200</c>
        /// when healthy or degraded, <c>503</c> when unhealthy. The report names queues and recurring job ids;
        /// for a plain status, use ASP.NET Core health checks with <c>AddNightShift</c> instead.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="pattern">The route, for example <c>"/health/nightshift"</c>.</param>
        /// <param name="options">The dashboard options to use, for <c>ServerPossiblyAbortedThreshold</c>. <c>null</c> uses the registered ones, or the defaults.</param>
        /// <param name="storage">The storage to report on. <c>null</c> uses the registered one.</param>
        /// <returns>A builder to add conventions to the endpoint.</returns>
        public static IEndpointConventionBuilder MapNightShiftHealth(
            [NotNull] this IEndpointRouteBuilder endpoints,
            [NotNull] string pattern,
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            return MapDashboardRoute(endpoints, pattern, "/health", options, storage);
        }

        /// <summary>
        /// Maps <c>GET {pattern}</c> to the Prometheus text the dashboard serves at <c>/metrics</c>: job counts by
        /// state, queues, servers, recurring jobs and, when recorded, the per-method and per-queue insights.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="pattern">The route, for example <c>"/metrics/nightshift"</c>.</param>
        /// <param name="options">The dashboard options to use. <c>null</c> uses the registered ones, or the defaults.</param>
        /// <param name="storage">The storage to report on. <c>null</c> uses the registered one.</param>
        /// <returns>A builder to add conventions to the endpoint.</returns>
        public static IEndpointConventionBuilder MapNightShiftMetrics(
            [NotNull] this IEndpointRouteBuilder endpoints,
            [NotNull] string pattern,
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            return MapDashboardRoute(endpoints, pattern, "/metrics", options, storage);
        }

        private static IEndpointConventionBuilder MapDashboardRoute(
            IEndpointRouteBuilder endpoints,
            string pattern,
            string dashboardPath,
            DashboardOptions? options,
            JobStorage? storage)
        {
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));

            var services = endpoints.ServiceProvider;
            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(services);

            var resolvedStorage = storage ?? services.GetRequiredService<JobStorage>();
            var resolvedOptions = options ?? services.GetService<DashboardOptions>() ?? new DashboardOptions();
            var routes = services.GetRequiredService<Dashboard.RouteCollection>();
            var route = routes.FindDispatcher(dashboardPath)
                        ?? throw new InvalidOperationException($"The dashboard has no '{dashboardPath}' route.");

            return endpoints.MapGet(pattern, httpContext =>
            {
                var context = new Dashboard.AspNetCoreDashboardContext(resolvedStorage, resolvedOptions, httpContext)
                {
                    UriMatch = route.Item2,
                };

                return route.Item1.Dispatch(context);
            });
        }
    }
}

#endif
