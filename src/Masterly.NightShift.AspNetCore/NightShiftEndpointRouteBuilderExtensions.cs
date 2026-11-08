// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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

#if NET8_0_OR_GREATER
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Masterly.NightShift
{
    /// <summary>
    /// Maps the dashboard as an endpoint, so endpoint routing features such as <c>RequireAuthorization</c> apply
    /// to it. Call <c>AddNightShift</c> on the service collection first.
    /// </summary>
    public static class NightShiftEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps the dashboard under <c>/nightshift</c>.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="options">The dashboard options, or <c>null</c> for the registered ones, or new ones.</param>
        /// <param name="storage">The storage to show, or <c>null</c> for the registered <see cref="JobStorage"/>.</param>
        /// <returns>A builder to add conventions, such as authorization, to the endpoint.</returns>
        public static IEndpointConventionBuilder MapNightShiftDashboard(
            [NotNull] this IEndpointRouteBuilder endpoints,
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            return MapNightShiftDashboard(endpoints, "/nightshift", options, storage);
        }

        /// <summary>
        /// Maps the dashboard under <paramref name="pattern"/>.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="pattern">The path the dashboard is served under, for example <c>/nightshift</c>.</param>
        /// <param name="options">The dashboard options, or <c>null</c> for the <see cref="DashboardOptions"/>
        /// registered in the container, or new ones. A <c>null</c> <see cref="DashboardOptions.TimeZoneResolver"/>
        /// is set to the registered <see cref="ITimeZoneResolver"/>.</param>
        /// <param name="storage">The storage to show, or <c>null</c> for the registered <see cref="JobStorage"/>.</param>
        /// <returns>A builder to add conventions, such as authorization, to the endpoint.</returns>
        /// <exception cref="InvalidOperationException"><c>AddNightShift</c> wasn't called.</exception>
        public static IEndpointConventionBuilder MapNightShiftDashboard(
            [NotNull] this IEndpointRouteBuilder endpoints,
            [NotNull] string pattern,
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));

            var app = endpoints.CreateApplicationBuilder();

            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(app.ApplicationServices);

            var services = app.ApplicationServices;

            storage = storage ?? services.GetRequiredService<JobStorage>();
            options = options ?? services.GetService<DashboardOptions>() ?? new DashboardOptions();
            options.TimeZoneResolver = options.TimeZoneResolver ?? services.GetService<ITimeZoneResolver>();

            var routes = app.ApplicationServices.GetRequiredService<Dashboard.RouteCollection>();

            var pipeline = app
                .UsePathBase(pattern)
                .UseMiddleware<AspNetCoreDashboardMiddleware>(storage, options, routes, true)
                .Build();

            return endpoints.Map(pattern + "/{**path}", pipeline);
        }

        /// <summary>
        /// Maps the dashboard like <see cref="MapNightShiftDashboard(IEndpointRouteBuilder, string, DashboardOptions, JobStorage)"/>,
        /// without the dashboard's own authorization filters, for an endpoint protected with <c>RequireAuthorization</c>.
        /// The options are <paramref name="options"/>, or the <see cref="DashboardOptions"/> registered in DI, or new
        /// ones. A copy of them is used with empty <see cref="DashboardOptions.Authorization"/> and
        /// <see cref="DashboardOptions.AsyncAuthorization"/>, so the instance you pass or registered keeps its filters.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="pattern">The path the dashboard is served under. Default is <c>/nightshift</c>.</param>
        /// <param name="options">The dashboard options to copy, or <c>null</c>.</param>
        /// <param name="storage">The storage to show, or <c>null</c> for the registered <see cref="JobStorage"/>.</param>
        /// <returns>A builder to add conventions, such as authorization, to the endpoint.</returns>
        public static IEndpointConventionBuilder MapNightShiftDashboardWithNoAuthorizationFilters(
            [NotNull] this IEndpointRouteBuilder endpoints,
            [NotNull] string pattern = "/nightshift",
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));

            NightShiftServiceCollectionExtensions.ThrowIfNotConfigured(endpoints.ServiceProvider);

            options = CopyOptions(options ?? endpoints.ServiceProvider.GetService<DashboardOptions>() ?? new DashboardOptions());

            // We don't require the default LocalRequestsOnlyAuthorizationFilter since we provide our own policy
            options.Authorization = Enumerable.Empty<IDashboardAuthorizationFilter>();
            options.AsyncAuthorization = Enumerable.Empty<IDashboardAsyncAuthorizationFilter>();

            return endpoints.MapNightShiftDashboard(pattern, options, storage);
        }

        /// <summary>
        /// Maps the dashboard like <see cref="MapNightShiftDashboardWithNoAuthorizationFilters"/>, and requires
        /// the ASP.NET Core authorization policy <paramref name="authorizationPolicyName"/> on the endpoint.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="authorizationPolicyName">The name of the authorization policy.</param>
        /// <param name="pattern">The path the dashboard is served under. Default is <c>/nightshift</c>.</param>
        /// <param name="options">The dashboard options to copy, or <c>null</c>.</param>
        /// <param name="storage">The storage to show, or <c>null</c> for the registered <see cref="JobStorage"/>.</param>
        /// <returns>A builder to add further conventions to the endpoint.</returns>
        public static IEndpointConventionBuilder MapNightShiftDashboardWithAuthorizationPolicy(
            [NotNull] this IEndpointRouteBuilder endpoints,
            [NotNull] string authorizationPolicyName,
            [NotNull] string pattern = "/nightshift",
            [CanBeNull] DashboardOptions? options = null,
            [CanBeNull] JobStorage? storage = null)
        {
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));
            if (authorizationPolicyName == null) throw new ArgumentNullException(nameof(authorizationPolicyName));

            return endpoints
                .MapNightShiftDashboardWithNoAuthorizationFilters(pattern, options, storage)
                .RequireAuthorization(authorizationPolicyName);
        }

        internal static DashboardOptions CopyOptions(DashboardOptions source)
        {
            var copy = new DashboardOptions
            {
                AppPath = source.AppPath,
                PrefixPath = source.PrefixPath,
                IsReadOnlyFunc = source.IsReadOnlyFunc,
                StatsPollingInterval = source.StatsPollingInterval,
                DisplayStorageConnectionString = source.DisplayStorageConnectionString,
                DashboardTitle = source.DashboardTitle,
                DisplayNameFunc = source.DisplayNameFunc,
                IgnoreAntiforgeryToken = source.IgnoreAntiforgeryToken,
                TimeZoneResolver = source.TimeZoneResolver,
                DefaultRecordsPerPage = source.DefaultRecordsPerPage,
                DarkModeEnabled = source.DarkModeEnabled,
                FaviconPath = source.FaviconPath,
                ServerPossiblyAbortedThreshold = source.ServerPossiblyAbortedThreshold,
                AllowAnyMethodInManifestImport = source.AllowAnyMethodInManifestImport,
                DisplayAuditIpAddressesToReadOnlyUsers = source.DisplayAuditIpAddressesToReadOnlyUsers,
            };

            // Setting AsyncAuthorization can clear Authorization, so it goes first
            copy.AsyncAuthorization = source.AsyncAuthorization;
            copy.Authorization = source.Authorization;

            return copy;
        }
    }
}

#endif
