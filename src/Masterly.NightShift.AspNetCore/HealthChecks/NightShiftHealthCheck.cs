#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift.HealthChecks
{
    /// <summary>
    /// The health check registered by <c>AddNightShift</c>. Its result never contains exception messages, which can
    /// name servers, databases or logins: storage errors are logged instead.
    /// </summary>
    internal sealed class NightShiftHealthCheck : IHealthCheck
    {
        private static readonly Action<ILogger, Exception?> LogStorageFailure = LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, "StorageDidNotAnswer"),
            "NightShift health check: the storage did not answer.");

        private readonly NightShiftHealthCheckOptions _options;
        private readonly Func<JobStorage> _storage;
        private readonly ILogger? _logger;

        public NightShiftHealthCheck(NightShiftHealthCheckOptions options, Func<JobStorage> storage, ILogger? logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger = logger;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            return Task.FromResult(Check(context.Registration.FailureStatus, DateTime.UtcNow));
        }

        private HealthCheckResult Check(HealthStatus failureStatus, DateTime utcNow)
        {
            JobStorage storage;
            IList<ServerDto> servers;

            try
            {
                storage = _storage();
                servers = storage.GetMonitoringApi().Servers() ?? new List<ServerDto>();
            }
            catch (Exception ex) when (IsCatchable(ex))
            {
                if (_logger != null) LogStorageFailure(_logger, ex);

                // The process scope serves liveness probes: restarting the process doesn't bring the database
                // back, and a server that restarts while the storage is down can't even register, so an outage
                // is reported as degraded there instead of failing every pod at once
                return _options.Scope == NightShiftHealthCheckScope.Process
                    ? HealthCheckResult.Degraded("The NightShift storage did not answer; this process keeps running.")
                    : new HealthCheckResult(failureStatus, "The NightShift storage did not answer.");
            }

            var oldest = utcNow - _options.HeartbeatTimeout;

            return _options.Scope == NightShiftHealthCheckScope.Process
                ? CheckProcess(storage, servers, oldest, failureStatus)
                : CheckCluster(servers, oldest, failureStatus);
        }

        private static bool IsCatchable(Exception exception)
        {
            return !(exception is OutOfMemoryException || exception is StackOverflowException || exception is ThreadAbortException || exception is AccessViolationException);
        }

        private static HealthCheckResult CheckCluster(IList<ServerDto> servers, DateTime oldest, HealthStatus failureStatus)
        {
            var alive = servers.Count(server => server.Heartbeat.HasValue && server.Heartbeat.Value >= oldest);
            var data = new Dictionary<string, object> { { "servers", servers.Count }, { "alive", alive } };

            return alive > 0
                ? HealthCheckResult.Healthy($"{alive.ToString(CultureInfo.InvariantCulture)} NightShift servers are alive.", data)
                : new HealthCheckResult(failureStatus, servers.Count == 0 ? "No NightShift server is registered." : "No NightShift server sent a heartbeat recently.", data: data);
        }

        private static HealthCheckResult CheckProcess(JobStorage storage, IList<ServerDto> servers, DateTime oldest, HealthStatus failureStatus)
        {
            var local = LocalServerTracker.GetServerIds(storage);
            if (local.Count == 0)
            {
                return new HealthCheckResult(failureStatus, "No NightShift server runs in this process.");
            }

            var registered = servers.Where(static server => server.Name != null).GroupBy(static server => server.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
            var silent = local.Count(id => !registered.TryGetValue(id, out var server) || !server.Heartbeat.HasValue || server.Heartbeat.Value < oldest);
            var data = new Dictionary<string, object> { { "servers", local.Count }, { "silent", silent } };

            return silent == 0
                ? HealthCheckResult.Healthy($"The {local.Count.ToString(CultureInfo.InvariantCulture)} NightShift servers of this process are alive.", data)
                : new HealthCheckResult(failureStatus, $"{silent.ToString(CultureInfo.InvariantCulture)} of the {local.Count.ToString(CultureInfo.InvariantCulture)} NightShift servers of this process sent no heartbeat recently.", data: data);
        }
    }
}
