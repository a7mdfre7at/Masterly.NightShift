// This file is part of Hangfire. Copyright © 2016 Hangfire OÜ.
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

#nullable enable

using System;
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Server;
using Microsoft.AspNetCore.Builder;
#if NET8_0_OR_GREATER
using Microsoft.Extensions.Hosting;
#else
using Microsoft.AspNetCore.Hosting;
#endif
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Masterly.NightShift.Common;

namespace Masterly.NightShift
{
    /// <summary>
    /// Adds the dashboard to an ASP.NET Core request pipeline, and registers servers created by hand so they stop
    /// with the application. Call <c>AddNightShift</c> on the service collection first.
    /// </summary>
    public static class NightShiftApplicationBuilderExtensions
    {
        /// <summary>
        /// Serves the dashboard under <paramref name="pathMatch"/>.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="pathMatch">The path the dashboard is served under. Default is <c>/nightshift</c>.</param>
        /// <param name="options">The dashboard options, or <c>null</c> for the <see cref="DashboardOptions"/>
        /// registered in the container, or new ones. A <c>null</c> <see cref="DashboardOptions.TimeZoneResolver"/>
        /// is set to the registered <see cref="ITimeZoneResolver"/>.</param>
        /// <param name="storage">The storage to show, or <c>null</c> for the registered <see cref="JobStorage"/>.</param>
        /// <returns>The application builder.</returns>
        /// <exception cref="InvalidOperationException"><c>AddNightShift</c> wasn't called.</exception>
        public static IApplicationBuilder UseNightShiftDashboard(
            [NotNull] this IApplicationBuilder app,
            [NotNull] string pathMatch = "/nightshift",
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (pathMatch == null) throw new ArgumentNullException(nameof(pathMatch));

            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(app.ApplicationServices);

            var services = app.ApplicationServices;

            storage = storage ?? services.GetRequiredService<JobStorage>();
            options = options ?? services.GetService<DashboardOptions>() ?? new DashboardOptions();
            options.TimeZoneResolver = options.TimeZoneResolver ?? services.GetService<ITimeZoneResolver>();

            var routes = app.ApplicationServices.GetRequiredService<RouteCollection>();

            app.Map(new PathString(pathMatch), x => x.UseMiddleware<AspNetCoreDashboardMiddleware>(storage, options, routes));

            return app;
        }

        /// <summary>
        /// Starts a background job server at once and stops it when the application stops. Prefer
        /// <c>AddNightShiftServer</c>, which runs the server as a hosted service.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="options">The server options, or <c>null</c> for the registered ones, or the defaults.</param>
        /// <param name="additionalProcesses">Extra background processes, or <c>null</c> for the registered ones.</param>
        /// <param name="storage">The storage, or <c>null</c> for the registered <see cref="JobStorage"/>.</param>
        /// <returns>The application builder.</returns>
        [Obsolete("Use the IServiceCollection.AddNightShiftServer extension method instead. Kept for code ported from Hangfire.")]
        public static IApplicationBuilder UseNightShiftServer(
            [NotNull] this IApplicationBuilder app,
            [CanBeNull] BackgroundJobServerOptions? options = null,
            [CanBeNull] IEnumerable<IBackgroundProcess>? additionalProcesses = null,
            [CanBeNull] JobStorage? storage = null)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            
            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(app.ApplicationServices);

            var services = app.ApplicationServices;

            storage = storage ?? services.GetRequiredService<JobStorage>();
            options = options ?? services.GetService<BackgroundJobServerOptions>() ?? new BackgroundJobServerOptions();
            additionalProcesses = additionalProcesses ?? services.GetServices<IBackgroundProcess>();

            options.Activator = options.Activator ?? services.GetService<JobActivator>();
            options.FilterProvider = options.FilterProvider ?? services.GetService<IJobFilterProvider>();
            options.TimeZoneResolver = options.TimeZoneResolver ?? services.GetService<ITimeZoneResolver>();

            NightShiftServiceCollectionExtensions.GetInternalServices(services, out var factory, out var stateChanger, out var performer);

            services.RegisterNightShiftServer(factory != null && stateChanger != null && performer != null
#pragma warning disable 618
                ? new BackgroundJobServer(options, storage, additionalProcesses, null, null, factory, performer, stateChanger)
#pragma warning restore 618
                : new BackgroundJobServer(options, storage, additionalProcesses));

            return app;
        }

        /// <summary>
        /// Creates a background processing server with <paramref name="serverFactory"/> and stops it when the
        /// application stops.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="serverFactory">Creates the server.</param>
        /// <returns>The application builder.</returns>
        public static IApplicationBuilder UseNightShiftServer(
            [NotNull] this IApplicationBuilder app,
            [NotNull] Func<IBackgroundProcessingServer> serverFactory)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (serverFactory == null) throw new ArgumentNullException(nameof(serverFactory));

            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(app.ApplicationServices);
            app.ApplicationServices.RegisterNightShiftServer(serverFactory());

            return app;
        }

        /// <summary>
        /// Stops <paramref name="server"/> when the application is stopping, and disposes it when the application
        /// has stopped.
        /// </summary>
        /// <param name="services">The application's service provider.</param>
        /// <param name="server">The server.</param>
        /// <returns>The service provider.</returns>
        public static IServiceProvider RegisterNightShiftServer(
            [NotNull] this IServiceProvider services,
            [NotNull] IBackgroundProcessingServer server)
        {
#if NET8_0_OR_GREATER
            var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
#else
            var lifetime = services.GetRequiredService<IApplicationLifetime>();
#endif

            lifetime.ApplicationStopping.Register(server.SendStop);
            lifetime.ApplicationStopped.Register(server.Dispose);

            return services;
        }
    }
}
