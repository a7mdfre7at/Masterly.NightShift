using System.Globalization;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift.HealthChecks;

/// <summary>
/// The health check registered by <c>AddNightShift</c>. Its result never contains exception messages, which can
/// name servers, databases or logins: storage errors are logged instead.
/// </summary>
internal sealed partial class NightShiftHealthCheck(NightShiftHealthCheckOptions options, Func<JobStorage> storage, ILogger? logger) : IHealthCheck
{
    [LoggerMessage(EventId = 1, EventName = "StorageDidNotAnswer", Level = LogLevel.Warning,
        Message = "NightShift health check: the storage did not answer.")]
    private static partial void LogStorageFailure(ILogger logger, Exception exception);

    private readonly NightShiftHealthCheckOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<JobStorage> _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly ILogger? _logger = logger;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(Check(context.Registration.FailureStatus, JobClock.UtcNow));
    }

    private HealthCheckResult Check(HealthStatus failureStatus, DateTime utcNow)
    {
        JobStorage storage;
        IList<ServerDto> servers;

        try
        {
            storage = _storage();
            servers = storage.GetMonitoringApi().Servers() ?? [];
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

        DateTime oldest = utcNow - _options.HeartbeatTimeout;

        return _options.Scope == NightShiftHealthCheckScope.Process
            ? CheckProcess(storage, servers, oldest, failureStatus)
            : CheckCluster(servers, oldest, failureStatus);
    }

    private static bool IsCatchable(Exception exception)
    {
        return exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    }

    private static HealthCheckResult CheckCluster(IList<ServerDto> servers, DateTime oldest, HealthStatus failureStatus)
    {
        int alive = servers.Count(server => server.Heartbeat.HasValue && server.Heartbeat.Value >= oldest);
        Dictionary<string, object> data = new() { { "servers", servers.Count }, { "alive", alive } };

        return alive > 0
            ? HealthCheckResult.Healthy($"{alive.ToString(CultureInfo.InvariantCulture)} NightShift servers are alive.", data)
            : new HealthCheckResult(failureStatus, servers.Count == 0 ? "No NightShift server is registered." : "No NightShift server sent a heartbeat recently.", data: data);
    }

    private static HealthCheckResult CheckProcess(JobStorage storage, IList<ServerDto> servers, DateTime oldest, HealthStatus failureStatus)
    {
        IReadOnlyList<string> local = LocalServerTracker.GetServerIds(storage);
        if (local.Count == 0)
        {
            return new HealthCheckResult(failureStatus, "No NightShift server runs in this process.");
        }

        Dictionary<string, ServerDto> registered = servers
            .Where(static server => server.Name != null)
            .DistinctBy(static server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static server => server.Name, StringComparer.OrdinalIgnoreCase);
        int silent = local.Count(id => !registered.TryGetValue(id, out ServerDto? server) || !server.Heartbeat.HasValue || server.Heartbeat.Value < oldest);
        Dictionary<string, object> data = new() { { "servers", local.Count }, { "silent", silent } };

        return silent == 0
            ? HealthCheckResult.Healthy($"The {local.Count.ToString(CultureInfo.InvariantCulture)} NightShift servers of this process are alive.", data)
            : new HealthCheckResult(failureStatus, $"{silent.ToString(CultureInfo.InvariantCulture)} of the {local.Count.ToString(CultureInfo.InvariantCulture)} NightShift servers of this process sent no heartbeat recently.", data: data);
    }
}
