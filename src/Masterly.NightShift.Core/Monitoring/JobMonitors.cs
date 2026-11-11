using System.Collections.Concurrent;
using System.Globalization;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift;

/// <summary>The state of a <see cref="JobMonitor"/>.</summary>
public enum JobMonitorState
{
    /// <summary>Nothing to judge yet, such as a daily deadline that hasn't come.</summary>
    Waiting,

    /// <summary>All is well.</summary>
    Healthy,

    /// <summary>An incident is open: an alert was raised, and a recovery alert follows when it ends.</summary>
    NeedsAttention,

    /// <summary>The monitor couldn't be checked, for example because the storage failed.</summary>
    Unknown,

    /// <summary>The monitor is paused.</summary>
    Paused,
}

/// <summary>
/// Watches the jobs of one method, as <c>Type.Method</c> (the type's name without its namespace, as alerts name
/// it), and raises <see cref="AlertKind.MonitorIncident"/> when something is wrong and
/// <see cref="AlertKind.MonitorRecovered"/> when it is right again. Register monitors with <c>UseJobMonitor</c>.
/// </summary>
public abstract class JobMonitor
{
    /// <summary>Creates the monitor.</summary>
    /// <param name="name">Its name, 1 to 64 letters, digits, <c>.</c>, <c>_</c> and <c>-</c>; lower-cased.</param>
    /// <param name="method">The jobs it watches, as <c>Type.Method</c>.</param>
    protected JobMonitor([NotNull] string name, [NotNull] string method)
    {
        Name = SchedulePolicy.NormalizeName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (!method.Contains('.', StringComparison.Ordinal)) throw new ArgumentException($"'{method}' isn't a method as Type.Method.", nameof(method));
        Method = method.Trim();
    }

    /// <summary>The monitor's name.</summary>
    public string Name { get; }

    /// <summary>The jobs it watches, as <c>Type.Method</c>.</summary>
    public string Method { get; }

    /// <summary>Whether it is paused: it raises nothing and shows <see cref="JobMonitorState.Paused"/>.</summary>
    public bool Paused { get; init; }

    /// <summary>Whether the monitor needs the successes and failures of its jobs recorded.</summary>
    internal virtual bool TracksOutcomes => true;

    /// <summary>Judges the monitor now from its recorded data.</summary>
    internal abstract (JobMonitorState State, string? Reason) Evaluate(JobMonitorData data, DateTime now, JobStorage storage);
}

/// <summary>A job of the method must succeed every day by a time of day, with some grace.</summary>
public sealed class DailyDeadlineMonitor : JobMonitor
{
    /// <summary>Creates the monitor.</summary>
    /// <param name="name">The monitor's name.</param>
    /// <param name="method">The jobs it watches, as <c>Type.Method</c>.</param>
    /// <param name="by">The time of day a job must have succeeded by.</param>
    /// <param name="timeZone">The time zone of <paramref name="by"/>, UTC when <c>null</c>.</param>
    /// <param name="grace">How long after <paramref name="by"/> before the monitor raises an incident, 0 to 12 hours.</param>
    public DailyDeadlineMonitor([NotNull] string name, [NotNull] string method, TimeSpan by, [CanBeNull] TimeZoneInfo? timeZone = null, TimeSpan grace = default)
        : base(name, method)
    {
        if (by < TimeSpan.Zero || by >= TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(by), by, "A time of day is between 00:00 and 23:59:59.");
        if (grace < TimeSpan.Zero || grace > TimeSpan.FromHours(12)) throw new ArgumentOutOfRangeException(nameof(grace), grace, "The grace is between 0 and 12 hours.");

        By = by;
        TimeZone = timeZone ?? TimeZoneInfo.Utc;
        Grace = grace;
    }

    /// <summary>The time of day a job must have succeeded by.</summary>
    public TimeSpan By { get; }

    /// <summary>The time zone of <see cref="By"/>.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>How long after <see cref="By"/> before an incident.</summary>
    public TimeSpan Grace { get; }

    internal override (JobMonitorState State, string? Reason) Evaluate(JobMonitorData data, DateTime now, JobStorage storage)
    {
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZone);

        if (data.LastSuccessAt is { } success && TimeZoneInfo.ConvertTimeFromUtc(success, TimeZone).Date == local.Date)
        {
            return (JobMonitorState.Healthy, null);
        }

        DateTime due = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date + By, DateTimeKind.Unspecified), TimeZone) + Grace;
        return now < due
            ? (JobMonitorState.Waiting, null)
            : (JobMonitorState.NeedsAttention, string.Create(CultureInfo.InvariantCulture, $"No job of {Method} succeeded today by {By:hh\\:mm} ({TimeZone.Id})."));
    }
}

/// <summary>A job of the method must not run longer than a threshold. The time it waited in its queue doesn't count.</summary>
public sealed class LongRunningMonitor : JobMonitor
{
    private const int MaxProcessingJobsRead = 1000;

    /// <summary>Creates the monitor.</summary>
    /// <param name="name">The monitor's name.</param>
    /// <param name="method">The jobs it watches, as <c>Type.Method</c>.</param>
    /// <param name="threshold">How long a job may run, 5 seconds to 7 days.</param>
    public LongRunningMonitor([NotNull] string name, [NotNull] string method, TimeSpan threshold)
        : base(name, method)
    {
        if (threshold < TimeSpan.FromSeconds(5) || threshold > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold is between 5 seconds and 7 days.");
        Threshold = threshold;
    }

    /// <summary>How long a job may run.</summary>
    public TimeSpan Threshold { get; }

    internal override bool TracksOutcomes => false;

    internal override (JobMonitorState State, string? Reason) Evaluate(JobMonitorData data, DateTime now, JobStorage storage)
    {
        IMonitoringApi monitoring = storage.GetMonitoringApi();

        foreach (KeyValuePair<string, ProcessingJobDto?> job in monitoring.ProcessingJobs(0, MaxProcessingJobsRead))
        {
            if (job.Value?.Job is not { } processing || job.Value.StartedAt is not { } startedAt) continue;
            if (JobMonitors.GetMethodKey(processing) != Method) continue;

            TimeSpan running = now - startedAt;
            if (running > Threshold)
            {
                return (JobMonitorState.NeedsAttention, $"Job {job.Key} of {Method} has run for {SkipIfLateAttribute.Format(running)}, more than {SkipIfLateAttribute.Format(Threshold)}.");
            }
        }

        return (JobMonitorState.Healthy, null);
    }
}

/// <summary>Jobs of the method must not fail for good several times in a row within a time window.</summary>
public sealed class RepeatedFailuresMonitor : JobMonitor
{
    /// <summary>Creates the monitor.</summary>
    /// <param name="name">The monitor's name.</param>
    /// <param name="method">The jobs it watches, as <c>Type.Method</c>.</param>
    /// <param name="failures">How many failures in a row make an incident, 2 to 20.</param>
    /// <param name="window">How close together they must be, 1 hour to 7 days.</param>
    public RepeatedFailuresMonitor([NotNull] string name, [NotNull] string method, int failures, TimeSpan window)
        : base(name, method)
    {
        if (failures is < 2 or > 20) throw new ArgumentOutOfRangeException(nameof(failures), failures, "The number of failures is between 2 and 20.");
        if (window < TimeSpan.FromHours(1) || window > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(window), window, "The window is between 1 hour and 7 days.");

        Failures = failures;
        Window = window;
    }

    /// <summary>How many failures in a row make an incident.</summary>
    public int Failures { get; }

    /// <summary>How close together they must be.</summary>
    public TimeSpan Window { get; }

    internal override (JobMonitorState State, string? Reason) Evaluate(JobMonitorData data, DateTime now, JobStorage storage)
    {
        return data.ConsecutiveFailures >= Failures && data.FirstFailureAt is { } first && data.LastFailureAt is { } last && last - first <= Window
            ? (JobMonitorState.NeedsAttention, string.Create(CultureInfo.InvariantCulture, $"{Method} failed {data.ConsecutiveFailures} times in a row."))
            : (JobMonitorState.Healthy, null);
    }
}

/// <summary>What a monitor shows: its state and the data it judges from.</summary>
/// <param name="Name">The monitor's name.</param>
/// <param name="Method">The jobs it watches.</param>
/// <param name="State">Its state at the last check.</param>
/// <param name="Reason">Why it needs attention, or <c>null</c>.</param>
/// <param name="IncidentSince">When the open incident started, or <c>null</c>.</param>
/// <param name="CheckedAt">When it was last checked, or <c>null</c>.</param>
/// <param name="LastSuccessAt">When a job of the method last succeeded, or <c>null</c>.</param>
/// <param name="ConsecutiveFailures">How many jobs failed for good in a row since.</param>
public sealed record JobMonitorStatus(
    string Name,
    string Method,
    JobMonitorState State,
    string? Reason,
    DateTime? IncidentSince,
    DateTime? CheckedAt,
    DateTime? LastSuccessAt,
    int ConsecutiveFailures);

/// <summary>The data a monitor keeps in its storage hash.</summary>
internal sealed record JobMonitorData(
    JobMonitorState? State,
    string? Reason,
    DateTime? IncidentSince,
    DateTime? CheckedAt,
    DateTime? LastSuccessAt,
    int ConsecutiveFailures,
    DateTime? FirstFailureAt,
    DateTime? LastFailureAt)
{
    public static JobMonitorData Read(IDictionary<string, string>? hash)
    {
        hash ??= new Dictionary<string, string>();

        return new JobMonitorData(
            hash.TryGetValue("State", out string? state) && Enum.TryParse(state, out JobMonitorState parsed) ? parsed : null,
            Text(hash, "Reason"),
            Date(hash, "IncidentSince"),
            Date(hash, "CheckedAt"),
            Date(hash, "LastSuccessAt"),
            hash.TryGetValue("ConsecutiveFailures", out string? failures) && int.TryParse(failures, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0,
            Date(hash, "FirstFailureAt"),
            Date(hash, "LastFailureAt"));
    }

    private static string? Text(IDictionary<string, string> hash, string field) => hash.TryGetValue(field, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

    private static DateTime? Date(IDictionary<string, string> hash, string field) => Text(hash, field) is { } value ? JobHelper.DeserializeDateTime(value) : null;
}

/// <summary>
/// The registered <see cref="JobMonitor"/>s. Servers check them every <see cref="CheckInterval"/>, one server at a time
/// across the cluster; each monitor keeps its state in the storage hash <c>monitor:{name}</c>.
/// </summary>
public static class JobMonitors
{
    private static readonly ConcurrentDictionary<string, JobMonitor> Monitors = new(StringComparer.Ordinal);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobMonitors));
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often the monitors are checked, 5 seconds to 5 minutes. 30 seconds.</summary>
    public static TimeSpan CheckInterval
    {
        get;
        set => field = value < TimeSpan.FromSeconds(5) || value > TimeSpan.FromMinutes(5)
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "The interval is between 5 seconds and 5 minutes.")
            : value;
    } = TimeSpan.FromSeconds(30);

    /// <summary>Registers a monitor, replacing one of the same name.</summary>
    public static void Add([NotNull] JobMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        Monitors[monitor.Name] = monitor;
    }

    /// <summary>Removes a monitor; <c>false</c> when there was none of that name.</summary>
    public static bool Remove([NotNull] string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Monitors.TryRemove(name.Trim().ToLowerInvariant(), out _);
    }

    /// <summary>The registered monitors, by name.</summary>
    public static IReadOnlyList<JobMonitor> GetAll() => [.. Monitors.Values.OrderBy(static x => x.Name, StringComparer.Ordinal)];

    /// <summary>Reads the state of every registered monitor.</summary>
    [NotNull]
    public static IReadOnlyList<JobMonitorStatus> GetStatus([NotNull] JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        using IStorageConnection connection = storage.GetConnection();
        return [.. GetAll().Select(monitor =>
        {
            JobMonitorData data = JobMonitorData.Read(connection.GetAllEntriesFromHash(GetKey(monitor.Name)));
            return new JobMonitorStatus(
                monitor.Name,
                monitor.Method,
                monitor.Paused ? JobMonitorState.Paused : data.State ?? JobMonitorState.Waiting,
                data.Reason,
                data.IncidentSince,
                data.CheckedAt,
                data.LastSuccessAt,
                data.ConsecutiveFailures);
        })];
    }

    internal static string GetKey(string name) => "monitor:" + name;

    internal static string GetMethodKey(Job job) => $"{job.Type.Name}.{job.Method.Name}";

    internal static bool Any => !Monitors.IsEmpty;

    internal static IEnumerable<JobMonitor> ForMethod(string method) => Monitors.Values.Where(x => x.TracksOutcomes && x.Method == method);

    /// <summary>Records a success or a final failure of a monitored method. Best effort: a storage error is logged.</summary>
    internal static void RecordOutcome(JobStorage storage, string method, bool succeeded, DateTime now)
    {
        try
        {
            using IStorageConnection connection = storage.GetConnection();

            foreach (JobMonitor monitor in ForMethod(method))
            {
                string key = GetKey(monitor.Name);
                using (connection.AcquireDistributedLock($"lock:{key}", LockTimeout))
                {
                    JobMonitorData data = JobMonitorData.Read(connection.GetAllEntriesFromHash(key));
                    Dictionary<string, string> fields = succeeded
                        ? new Dictionary<string, string>
                        {
                            ["LastSuccessAt"] = JobHelper.SerializeDateTime(now),
                            ["ConsecutiveFailures"] = "0",
                            ["FirstFailureAt"] = string.Empty,
                            ["LastFailureAt"] = string.Empty,
                        }
                        : new Dictionary<string, string>
                        {
                            ["ConsecutiveFailures"] = (data.ConsecutiveFailures + 1).ToString(CultureInfo.InvariantCulture),
                            ["FirstFailureAt"] = JobHelper.SerializeDateTime(data.ConsecutiveFailures == 0 || data.FirstFailureAt == null ? now : data.FirstFailureAt.Value),
                            ["LastFailureAt"] = JobHelper.SerializeDateTime(now),
                        };

                    using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                    transaction.SetRangeInHash(key, fields);
                    transaction.Commit();
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"The outcome of a {method} job couldn't be recorded for its monitors.", ex);
        }
    }

    /// <summary>Checks every monitor, raising alerts on incidents and recoveries.</summary>
    internal static void Check(JobStorage storage, IStorageConnection connection, DateTime now)
    {
        foreach (JobMonitor monitor in GetAll())
        {
            string key = GetKey(monitor.Name);
            JobMonitorData data = JobMonitorData.Read(connection.GetAllEntriesFromHash(key));
            JobMonitorState previous = data.State ?? JobMonitorState.Waiting;

            (JobMonitorState state, string? reason) = monitor.Paused ? (JobMonitorState.Paused, null) : Evaluate(monitor, data, now, storage);

            // An open incident stays open until the monitor is healthy again: a missed deadline isn't forgotten at midnight
            if (previous == JobMonitorState.NeedsAttention && state is JobMonitorState.Waiting or JobMonitorState.Unknown)
            {
                state = JobMonitorState.NeedsAttention;
                reason = data.Reason;
            }

            DateTime? incidentSince = state == JobMonitorState.NeedsAttention ? data.IncidentSince ?? now : null;

            Dictionary<string, string> fields = new()
            {
                ["State"] = state.ToString(),
                ["Reason"] = reason ?? string.Empty,
                ["CheckedAt"] = JobHelper.SerializeDateTime(now),
                ["IncidentSince"] = incidentSince.HasValue ? JobHelper.SerializeDateTime(incidentSince.Value) : string.Empty,
            };

            using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(key, fields);
                transaction.Commit();
            }

            if (state == JobMonitorState.NeedsAttention && previous != JobMonitorState.NeedsAttention)
            {
                Raise(AlertKind.MonitorIncident, monitor, $"Monitor {monitor.Name} needs attention", reason ?? "Something is wrong.");
            }
            else if (state == JobMonitorState.Healthy && previous == JobMonitorState.NeedsAttention)
            {
                Raise(AlertKind.MonitorRecovered, monitor, $"Monitor {monitor.Name} recovered", $"{monitor.Method} is healthy again.");
            }
        }
    }

    private static (JobMonitorState State, string? Reason) Evaluate(JobMonitor monitor, JobMonitorData data, DateTime now, JobStorage storage)
    {
        try
        {
            return monitor.Evaluate(data, now, storage);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Monitor '{monitor.Name}' couldn't be checked.", ex);
            return (JobMonitorState.Unknown, ex.Message);
        }
    }

    private static void Raise(AlertKind kind, JobMonitor monitor, string title, string message)
    {
        if (!Alerts.HasSinks) return;

        Alerts.Raise(new Alert(kind, title, message) { Method = monitor.Method, Monitor = monitor.Name });
    }

    // For tests
    internal static void Clear() => Monitors.Clear();
}

/// <summary>Records the successes and final failures of monitored methods, once their state is committed.</summary>
internal sealed class JobMonitorFilter : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!JobMonitors.Any || context.BackgroundJob.Job is not { } job) return;

        bool succeeded = context.NewState is SucceededState;
        if (!succeeded && context.NewState is not FailedState) return;

        string method = JobMonitors.GetMethodKey(job);
        if (!JobMonitors.ForMethod(method).Any()) return;

        JobStorage storage = context.Storage;
        DateTime now = JobClock.UtcNow;
        context.RunAfterCommit(() => JobMonitors.RecordOutcome(storage, method, succeeded, now));
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}

/// <summary>Checks the monitors, on one server at a time.</summary>
internal sealed class JobMonitorProcess : IBackgroundProcess
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobMonitorProcess));

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (JobMonitors.Any)
        {
            try
            {
                using IStorageConnection connection = context.Storage.GetConnection();
                using (connection.AcquireDistributedLock("lock:monitors", TimeSpan.Zero))
                {
                    JobMonitors.Check(context.Storage, connection, JobClock.UtcNow);
                }
            }
            catch (DistributedLockTimeoutException)
            {
                // Another server is checking them
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException("The job monitors couldn't be checked.", ex);
            }
        }

        context.Wait(JobMonitors.CheckInterval);
    }

    public override string ToString() => "Job monitors";
}
