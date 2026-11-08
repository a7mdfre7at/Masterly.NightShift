#nullable enable

using System;
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.HealthChecks;
using Masterly.NightShift.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift
{
    /// <summary>Adds Masterly.NightShift to ASP.NET Core health checks.</summary>
    public static class NightShiftHealthChecksBuilderExtensions
    {
        /// <summary>The name the check is registered under unless another one is given.</summary>
        public const string DefaultName = "nightshift";

        /// <summary>
        /// Adds a health check of Masterly.NightShift. By default it checks the whole cluster: the storage answers
        /// and at least one server sent a heartbeat within <see cref="NightShiftHealthCheckOptions.HeartbeatTimeout"/>.
        /// With <see cref="NightShiftHealthCheckScope.Process"/> it checks the servers that this process runs
        /// (started by <c>AddNightShiftServer</c>). The result says what is wrong in plain words; it never includes
        /// exception messages, which are logged instead, so it is safe to expose on an anonymous endpoint.
        /// </summary>
        /// <param name="builder">The health checks builder.</param>
        /// <param name="configure">Optional changes to the default <see cref="NightShiftHealthCheckOptions"/>.</param>
        /// <param name="name">The name of the check. Default <see cref="DefaultName"/>.</param>
        /// <param name="failureStatus">The status reported when the check fails. <c>null</c> means <see cref="HealthStatus.Unhealthy"/>.</param>
        /// <param name="tags">Tags to filter checks by, for example <c>"live"</c> or <c>"ready"</c>.</param>
        /// <returns>The same builder, for chaining.</returns>
        public static IHealthChecksBuilder AddNightShift(
            [NotNull] this IHealthChecksBuilder builder,
            [CanBeNull] Action<NightShiftHealthCheckOptions>? configure = null,
            [CanBeNull] string? name = null,
            [CanBeNull] HealthStatus? failureStatus = null,
            [CanBeNull] IEnumerable<string>? tags = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var options = new NightShiftHealthCheckOptions();
            configure?.Invoke(options);
            if (options.HeartbeatTimeout <= TimeSpan.Zero) throw new ArgumentException("HeartbeatTimeout must be positive.", nameof(configure));

            if (options.Scope == NightShiftHealthCheckScope.Process)
            {
                // Every server started by AddNightShiftServer runs the registered IBackgroundProcess services,
                // unless additional processes are passed to it explicitly
                builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IBackgroundProcess, LocalServerTracker>());
            }

            return builder.Add(new HealthCheckRegistration(
                name ?? DefaultName,
                services => new NightShiftHealthCheck(
                    options,
                    () => options.Storage ?? services.GetService<JobStorage>() ?? JobStorage.Current,
                    services.GetService<ILoggerFactory>()?.CreateLogger(typeof(NightShiftHealthCheck))),
                failureStatus,
                tags));
        }
    }
}
