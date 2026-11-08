#nullable enable

using System;

namespace Masterly.NightShift
{
    /// <summary>What the health check added by <c>AddNightShift</c> verifies.</summary>
    public enum NightShiftHealthCheckScope
    {
        /// <summary>
        /// The storage answers and at least one server anywhere in the cluster sent a heartbeat recently. Use it for
        /// a readiness probe of an app that creates jobs, or for a cluster-wide monitor.
        /// </summary>
        Cluster,

        /// <summary>
        /// Every server that runs in this process sent a heartbeat recently. Unhealthy when no server runs in this
        /// process, or one stopped sending heartbeats. Use it for the liveness probe of a process that runs a server
        /// started by <c>AddNightShiftServer</c>.
        /// </summary>
        Process,
    }

    /// <summary>Settings of the health check added by <c>IHealthChecksBuilder.AddNightShift</c>.</summary>
    public sealed class NightShiftHealthCheckOptions
    {
        /// <summary>What the check verifies. Default <see cref="NightShiftHealthCheckScope.Cluster"/>.</summary>
        public NightShiftHealthCheckScope Scope { get; set; } = NightShiftHealthCheckScope.Cluster;

        /// <summary>
        /// How old a server's last heartbeat may be for the server to count as alive. Servers send one every 30
        /// seconds by default (<c>BackgroundJobServerOptions.HeartbeatInterval</c>). Default 1 minute.
        /// </summary>
        public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The storage to check. <c>null</c>, the default, uses the <see cref="JobStorage"/> registered by
        /// <c>AddNightShift</c>, or <see cref="JobStorage.Current"/> without one.
        /// </summary>
        public JobStorage? Storage { get; set; }
    }
}
