using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// <c>GET /health</c>: a JSON health report for load balancers, Kubernetes probes and uptime monitors.
/// <c>200</c> with <c>status: healthy</c> or <c>degraded</c>, <c>503</c> with <c>status: unhealthy</c> when
/// the storage is unreachable or no server is alive. See <see cref="DashboardHealth"/> for the checks; a
/// report is reused for <see cref="DashboardHealth.CacheDuration"/>, so frequent probes cost no more storage
/// queries than one read of the storage's clock each, where the storage has one. Like every dashboard route it
/// is subject to the dashboard's authorization filters; <c>/ping</c> is the cheaper, storage-free liveness URL.
/// </summary>
internal sealed class HealthDispatcher : IDashboardDispatcher
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(HealthDispatcher));

    /// <summary>This process's clock, for a storage without a clock of its own. Replaceable for tests.</summary>
    internal static Func<DateTime> Clock { get; set; } = static () => JobClock.UtcNow;

    public Task Dispatch(DashboardContext context)
    {
        HealthReport report = DashboardHealth.GetReport(context.Storage, context.Options, Now(context.Storage, Clock));
        int statusCode = report.Status == HealthStatus.Unhealthy ? 503 : 200;

        return DashboardJson.WriteAsync(context, statusCode, report.ToJson());
    }

    /// <summary>
    /// The time a report compares heartbeats and the next runs of recurring jobs with, read once per request:
    /// the storage's where it has a clock (see <see cref="StorageClock"/>), as the dashboard's pages do, and
    /// <paramref name="processClock"/>'s otherwise. A storage that doesn't answer for its clock doesn't fail
    /// the request: the report is made with this process's time, and says that the storage didn't answer.
    /// </summary>
    internal static DateTime Now(JobStorage storage, Func<DateTime> processClock)
    {
        try
        {
            return StorageClock.UtcNow(storage, processClock);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The message can name the server, the database or the login: it goes to the log only
            Logger.DebugException("Health check: the storage did not answer for its clock; this process's is used.", ex);
            return processClock();
        }
    }
}
