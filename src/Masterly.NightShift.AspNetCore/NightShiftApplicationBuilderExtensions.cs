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

using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift;

/// <summary>
/// Adds the dashboard to an ASP.NET Core request pipeline, and registers servers created by hand so they stop
/// with the application. Call <c>AddNightShift</c> on the service collection first.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class NightShiftApplicationBuilderExtensions
#pragma warning restore CA1708
{
    /// <param name="app">The application builder.</param>
    extension([NotNull] IApplicationBuilder app)
    {
        /// <summary>
        /// Serves the dashboard under <paramref name="pathMatch"/>.
        /// </summary>
        /// <param name="pathMatch">The path the dashboard is served under. Default is <c>/nightshift</c>.</param>
        /// <param name="options">The dashboard options, or <c>null</c> for the <see cref="DashboardOptions"/>
        /// registered in the container, or new ones. A <c>null</c> <see cref="DashboardOptions.TimeZoneResolver"/>
        /// is set to the registered <see cref="ITimeZoneResolver"/>.</param>
        /// <param name="storage">The storage to show, or <c>null</c> for the registered <see cref="JobStorage"/>.</param>
        /// <returns>The application builder.</returns>
        /// <exception cref="InvalidOperationException"><c>AddNightShift</c> wasn't called.</exception>
        public IApplicationBuilder UseNightShiftDashboard(
            [NotNull] string pathMatch = "/nightshift",
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            ArgumentNullException.ThrowIfNull(app);
            ArgumentNullException.ThrowIfNull(pathMatch);

            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(app.ApplicationServices);

            IServiceProvider services = app.ApplicationServices;

            storage ??= services.GetRequiredService<JobStorage>();
            options ??= services.GetService<DashboardOptions>() ?? new DashboardOptions();
            options.TimeZoneResolver ??= services.GetService<ITimeZoneResolver>();

            RouteCollection routes = app.ApplicationServices.GetRequiredService<RouteCollection>();

            app.Map(new PathString(pathMatch), x => x.UseMiddleware<AspNetCoreDashboardMiddleware>(storage, options, routes));

            return app;
        }

        /// <summary>
        /// Creates a background processing server with <paramref name="serverFactory"/> and stops it when the
        /// application stops.
        /// </summary>
        /// <param name="serverFactory">Creates the server.</param>
        /// <returns>The application builder.</returns>
        public IApplicationBuilder UseNightShiftServer(
            [NotNull] Func<IBackgroundProcessingServer> serverFactory)
        {
            ArgumentNullException.ThrowIfNull(app);
            ArgumentNullException.ThrowIfNull(serverFactory);

            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(app.ApplicationServices);
            app.ApplicationServices.RegisterNightShiftServer(serverFactory());

            return app;
        }
    }

    /// <param name="services">The application's service provider.</param>
    extension([NotNull] IServiceProvider services)
    {
        /// <summary>
        /// Stops <paramref name="server"/> when the application is stopping, and disposes it when the application
        /// has stopped.
        /// </summary>
        /// <param name="server">The server.</param>
        /// <returns>The service provider.</returns>
        public IServiceProvider RegisterNightShiftServer(
            [NotNull] IBackgroundProcessingServer server)
        {
            IHostApplicationLifetime lifetime = services.GetRequiredService<IHostApplicationLifetime>();

            lifetime.ApplicationStopping.Register(server.SendStop);
            lifetime.ApplicationStopped.Register(server.Dispose);

            return services;
        }
    }
}
