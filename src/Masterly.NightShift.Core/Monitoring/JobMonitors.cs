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
        if (!method.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{method}' isn't a method as Type.Method.", nameof(method));
        }

        Method = method.Trim();
    }

    /// <summary>The monitor's name.</summary>
    public string Name { get; }

    /// <summary>The jobs it watches, as <c>Type.Method</c>.</summary>
    public string Method { get; }

    /// <summary>
    /// Whether it is paused: it isn't checked, raises nothing and shows <see cref="JobMonitorState.Paused"/>. Its
    /// stored state stays as it was, so an incident open when it was paused is still open, without a new alert,
    /// when it resumes.
    /// </summary>
    public bool Paused { get; init; }

    /// <summary>Whether the monitor needs the successes and failures of its jobs recorded.</summary>
    internal virtual bool TracksOutcomes => true;

    /// <summary>
    /// Whether a success recorded at <paramref name="recorded"/> still stands for one at <paramref name="now"/>, so
    /// the new one needn't be written: successes of busy methods are written at most every few seconds.
    /// </summary>
    internal virtual bool IsSuccessFresh(DateTime recorded, DateTime now) => now >= recorded && now - recorded < JobMonitors.SuccessWriteInterval;

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
        if (by < TimeSpan.Zero || by >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(by), by, "A time of day is between 00:00 and 23:59:59.");
        }

        if (grace < TimeSpan.Zero || grace > TimeSpan.FromHours(12))
        {
            throw new ArgumentOutOfRangeException(nameof(grace), grace, "The grace is between 0 and 12 hours.");
        }

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

    // A deadline whose grace ends at or after midnight (by 23:00 with 2 hours of grace) is judged on the next local
    // day, from midnight until the next night's deadline is judged
    private bool EndsAfterMidnight => By + Grace >= TimeSpan.FromDays(1);

    internal override (JobMonitorState State, string? Reason) Evaluate(JobMonitorData data, DateTime now, JobStorage storage)
    {
        DateTime today = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZone).Date;
        DateTime day = EndsAfterMidnight ? today.AddDays(-1) : today;

        // A success after the deadline counts too: a late run ends the incident
        if (data.LastSuccessAt is { } success && GetDayOf(success) >= day)
        {
            return (JobMonitorState.Healthy, null);
        }

        if (now < GetDue(day))
        {
            return (JobMonitorState.Waiting, null);
        }

        return (JobMonitorState.NeedsAttention, day == today
            ? string.Create(CultureInfo.InvariantCulture, $"No job of {Method} succeeded today by {By:hh\\:mm} ({TimeZone.Id}).")
            : string.Create(CultureInfo.InvariantCulture, $"No job of {Method} succeeded by {By:hh\\:mm} on {day:yyyy-MM-dd} ({TimeZone.Id})."));
    }

    // A success recorded earlier for the same day is enough: a later one doesn't change the verdict
    internal override bool IsSuccessFresh(DateTime recorded, DateTime now)
    {
        return base.IsSuccessFresh(recorded, now) && GetDayOf(recorded) == GetDayOf(now);
    }

    // The local day a success at that instant counts for: its own date, except a success in the half day after
    // the night before's By (inside its grace, or late), which is that night's run and not an early one of the
    // next: a late run must not also stand for the next night, or a night missed after it would go unnoticed.
    // By is at least 12:00 here, because the grace is at most 12 hours
    private DateTime GetDayOf(DateTime instant)
    {
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(instant, TimeZone);
        return EndsAfterMidnight && local.TimeOfDay < By - HalfADay ? local.Date.AddDays(-1) : local.Date;
    }

    // The grace is a duration, so on the nights clocks change the deadline is that long after By on the clock
    private DateTime GetDue(DateTime day)
    {
        return ToUtc(DateTime.SpecifyKind(day + By, DateTimeKind.Unspecified)) + Grace;
    }

    private DateTime ToUtc(DateTime local)
    {
        // A due time in the gap of a daylight saving change (02:30 when clocks jump from 02:00 to 03:00) doesn't
        // exist that day, and ConvertTimeToUtc throws for it: the first valid minute after it is the deadline
        for (int minutes = 0; TimeZone.IsInvalidTime(local) && minutes < MaxGapMinutes; minutes++)
        {
            local = new DateTime(local.Ticks - (local.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Unspecified).AddMinutes(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }

    // Longer than any gap a time zone has had (Samoa skipped a whole day in 2011)
    private const int MaxGapMinutes = 2 * 24 * 60;
    private static readonly TimeSpan HalfADay = TimeSpan.FromHours(12);
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
        if (threshold < TimeSpan.FromSeconds(5) || threshold > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold is between 5 seconds and 7 days.");
        }

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
            if (job.Value?.Job is not { } processing || job.Value.StartedAt is not { } startedAt)
            {
                continue;
            }

            if (JobMonitors.GetMethodKey(processing) != Method)
            {
                continue;
            }

            TimeSpan running = now - startedAt;
            if (running > Threshold)
            {
                return (JobMonitorState.NeedsAttention, $"Job {job.Key} of {Method} has run for {SkipIfLateAttribute.Format(running)}, more than {SkipIfLateAttribute.Format(Threshold)}.");
            }
        }

        return (JobMonitorState.Healthy, null);
    }
}

/// <summary>
/// Jobs of the method must not fail for good several times in a row within a time window. The window slides: the
/// last <see cref="Failures"/> failures in a row count. An incident stays open until a job of the method succeeds.
/// </summary>
public sealed class RepeatedFailuresMonitor : JobMonitor
{
    /// <summary>The most failures in a row an incident can take, and so the most failure times a monitor keeps.</summary>
    internal const int MaxFailures = 20;

    /// <summary>Creates the monitor.</summary>
    /// <param name="name">The monitor's name.</param>
    /// <param name="method">The jobs it watches, as <c>Type.Method</c>.</param>
    /// <param name="failures">How many failures in a row make an incident, 2 to 20.</param>
    /// <param name="window">How close together they must be, 1 hour to 7 days.</param>
    public RepeatedFailuresMonitor([NotNull] string name, [NotNull] string method, int failures, TimeSpan window)
        : base(name, method)
    {
        if (failures is < 2 or > MaxFailures)
        {
            throw new ArgumentOutOfRangeException(nameof(failures), failures, "The number of failures is between 2 and 20.");
        }

        if (window < TimeSpan.FromHours(1) || window > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window is between 1 hour and 7 days.");
        }

        Failures = failures;
        Window = window;
    }

    /// <summary>How many failures in a row make an incident.</summary>
    public int Failures { get; }

    /// <summary>How close together they must be.</summary>
    public TimeSpan Window { get; }

    internal override (JobMonitorState State, string? Reason) Evaluate(JobMonitorData data, DateTime now, JobStorage storage)
    {
        bool failing = data.ConsecutiveFailures >= Failures && GetWindowStart(data) is { } first && GetLastFailure(data) is { } last && last - first <= Window;

        // Only a success ends an incident: failures that keep coming further apart don't. A success since the incident
        // opened counts even when new failures followed it before this check (they start a new row)
        bool succeededSince = data.LastSuccessAt is { } success && data.IncidentSince is { } since && success > since;
        bool stillFailing = data.State == JobMonitorState.NeedsAttention && data.ConsecutiveFailures > 0 && !succeededSince;

        return failing || stillFailing
            ? (JobMonitorState.NeedsAttention, string.Create(CultureInfo.InvariantCulture, $"{Method} failed {data.ConsecutiveFailures} times in a row."))
            : (JobMonitorState.Healthy, null);
    }

    // The oldest of the last Failures failures; a hash written before the failure times were kept has only the first
    private DateTime? GetWindowStart(JobMonitorData data) => data.RecentFailures.Count >= Failures ? data.RecentFailures[^Failures] : data.FirstFailureAt;

    private static DateTime? GetLastFailure(JobMonitorData data) => data.RecentFailures.Count > 0 ? data.RecentFailures[^1] : data.LastFailureAt;
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
    DateTime? LastFailureAt,
    IReadOnlyList<DateTime>? RecentFailureTimes = null)
{
    /// <summary>The field with the times of the last failures in a row, oldest first, comma-separated.</summary>
    internal const string RecentFailuresField = "RecentFailures";

    /// <summary>The times of the last failures in a row, oldest first; at most <see cref="RepeatedFailuresMonitor.MaxFailures"/>.</summary>
    public IReadOnlyList<DateTime> RecentFailures => RecentFailureTimes ?? [];

    /// <summary>Reads the hash. A value that can't be read is left out, so one corrupt field doesn't stop the monitor.</summary>
    public static JobMonitorData Read(IDictionary<string, string>? hash)
    {
        hash ??= new Dictionary<string, string>();

        return new JobMonitorData(
            hash.TryGetValue("State", out string? state) && Enum.TryParse(state, out JobMonitorState parsed) && Enum.IsDefined(parsed) ? parsed : null,
            Text(hash, "Reason"),
            Date(hash, "IncidentSince"),
            Date(hash, "CheckedAt"),
            Date(hash, "LastSuccessAt"),
            hash.TryGetValue("ConsecutiveFailures", out string? failures) && int.TryParse(failures, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0 ? count : 0,
            Date(hash, "FirstFailureAt"),
            Date(hash, "LastFailureAt"),
            Dates(hash, RecentFailuresField));
    }

    /// <summary>Writes failure times for <see cref="RecentFailuresField"/>.</summary>
    public static string WriteDates(IEnumerable<DateTime> dates) => string.Join(',', dates.Select(JobHelper.SerializeDateTime));

    private static string? Text(IDictionary<string, string> hash, string field) => hash.TryGetValue(field, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

    private static DateTime? Date(IDictionary<string, string> hash, string field) => Text(hash, field) is { } value ? ParseDate(value) : null;

    private static List<DateTime> Dates(IDictionary<string, string> hash, string field)
    {
        List<DateTime> dates = [];
        if (Text(hash, field) is not { } value)
        {
            return dates;
        }

        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseDate(part) is { } date)
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    private static DateTime? ParseDate(string value)
    {
        try
        {
            return JobHelper.DeserializeDateTime(value);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
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
        return [.. GetAll().Select(monitor => GetStatus(connection, monitor))];
    }

    // One monitor whose hash can't be read shows Unknown; the others are still read
    private static JobMonitorStatus GetStatus(IStorageConnection connection, JobMonitor monitor)
    {
        try
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
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"The state of monitor '{monitor.Name}' couldn't be read.", ex);
            return new JobMonitorStatus(monitor.Name, monitor.Method, monitor.Paused ? JobMonitorState.Paused : JobMonitorState.Unknown, ex.Message, null, null, null, 0);
        }
    }

    /// <summary>The longest a success of a method that isn't failing waits to be written: 5 seconds.</summary>
    internal static readonly TimeSpan SuccessWriteInterval = TimeSpan.FromSeconds(5);

    internal static string GetKey(string name) => "monitor:" + name;

    internal static string GetMethodKey(Job job) => $"{job.Type.Name}.{job.Method.Name}";

    internal static bool Any => !Monitors.IsEmpty;

    internal static IEnumerable<JobMonitor> ForMethod(string method) => Monitors.Values.Where(x => x.TracksOutcomes && x.Method == method);

    /// <summary>
    /// Records a success or a final failure of a monitored method. Best effort: a storage error is logged. A failure,
    /// or a success after failures, is written under the monitor's lock; a success while nothing failed only
    /// refreshes <c>LastSuccessAt</c>, without the lock and at most every <see cref="SuccessWriteInterval"/>, so a
    /// busy method doesn't take a distributed lock for each job.
    /// </summary>
    internal static void RecordOutcome(JobStorage storage, string method, bool succeeded, DateTime now)
    {
        try
        {
            using IStorageConnection connection = storage.GetConnection();

            foreach (JobMonitor monitor in ForMethod(method))
            {
                string key = GetKey(monitor.Name);
                if (succeeded && TryRecordPlainSuccess(connection, monitor, key, now))
                {
                    continue;
                }

                using (connection.AcquireDistributedLock($"lock:{key}", LockTimeout))
                {
                    JobMonitorData data = JobMonitorData.Read(connection.GetAllEntriesFromHash(key));
                    Write(connection, key, succeeded ? GetSuccessFields(now) : GetFailureFields(data, now));
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"The outcome of a {method} job couldn't be recorded for its monitors.", ex);
        }
    }

    // Returns false when failures must be reset, which needs the lock
    private static bool TryRecordPlainSuccess(IStorageConnection connection, JobMonitor monitor, string key, DateTime now)
    {
        JobMonitorData data = JobMonitorData.Read(connection.GetAllEntriesFromHash(key));
        if (data.ConsecutiveFailures > 0 || data.RecentFailures.Count > 0)
        {
            return false;
        }

        if (data.LastSuccessAt is not { } recorded || !monitor.IsSuccessFresh(recorded, now))
        {
            // A failure another server records meanwhile, under the lock, isn't undone: only this field is written
            Write(connection, key, new Dictionary<string, string> { ["LastSuccessAt"] = JobHelper.SerializeDateTime(now) });
        }

        return true;
    }

    private static Dictionary<string, string> GetSuccessFields(DateTime now) => new()
    {
        ["LastSuccessAt"] = JobHelper.SerializeDateTime(now),
        ["ConsecutiveFailures"] = "0",
        ["FirstFailureAt"] = string.Empty,
        ["LastFailureAt"] = string.Empty,
        [JobMonitorData.RecentFailuresField] = string.Empty,
    };

    private static Dictionary<string, string> GetFailureFields(JobMonitorData data, DateTime now)
    {
        bool firstInARow = data.ConsecutiveFailures == 0;

        // The last failures in a row, so the window of a RepeatedFailuresMonitor slides with them
        List<DateTime> recent = firstInARow ? [now] : [.. data.RecentFailures, now];
        if (recent.Count > RepeatedFailuresMonitor.MaxFailures)
        {
            recent.RemoveRange(0, recent.Count - RepeatedFailuresMonitor.MaxFailures);
        }

        return new Dictionary<string, string>
        {
            ["ConsecutiveFailures"] = (data.ConsecutiveFailures + 1).ToString(CultureInfo.InvariantCulture),
            ["FirstFailureAt"] = JobHelper.SerializeDateTime(firstInARow || data.FirstFailureAt == null ? now : data.FirstFailureAt.Value),
            ["LastFailureAt"] = JobHelper.SerializeDateTime(now),
            [JobMonitorData.RecentFailuresField] = JobMonitorData.WriteDates(recent),
        };
    }

    private static void Write(IStorageConnection connection, string key, Dictionary<string, string> fields)
    {
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(key, fields);
        transaction.Commit();
    }

    /// <summary>
    /// Checks every monitor, raising alerts on incidents and recoveries. A monitor that fails is logged and the
    /// others are still checked. A paused monitor is skipped and keeps its stored state, an open incident included.
    /// </summary>
    internal static void Check(JobStorage storage, IStorageConnection connection, DateTime now)
    {
        foreach (JobMonitor monitor in GetAll())
        {
            if (monitor.Paused)
            {
                continue;
            }

            try
            {
                Check(storage, connection, monitor, now);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Monitor '{monitor.Name}' couldn't be checked.", ex);
            }
        }
    }

    private static void Check(JobStorage storage, IStorageConnection connection, JobMonitor monitor, DateTime now)
    {
        string key = GetKey(monitor.Name);
        JobMonitorData data = JobMonitorData.Read(connection.GetAllEntriesFromHash(key));
        JobMonitorState previous = data.State ?? JobMonitorState.Waiting;

        (JobMonitorState state, string? reason) = Evaluate(monitor, data, now, storage);

        // An open incident stays open until the monitor is healthy again: a missed deadline isn't forgotten at midnight
        if (previous == JobMonitorState.NeedsAttention && state is JobMonitorState.Waiting or JobMonitorState.Unknown)
        {
            state = JobMonitorState.NeedsAttention;
            reason = data.Reason;
        }

        DateTime? incidentSince = state == JobMonitorState.NeedsAttention ? data.IncidentSince ?? now : null;

        Write(connection, key, new Dictionary<string, string>
        {
            ["State"] = state.ToString(),
            ["Reason"] = reason ?? string.Empty,
            ["CheckedAt"] = JobHelper.SerializeDateTime(now),
            ["IncidentSince"] = incidentSince.HasValue ? JobHelper.SerializeDateTime(incidentSince.Value) : string.Empty,
        });

        if (state == JobMonitorState.NeedsAttention && previous != JobMonitorState.NeedsAttention)
        {
            Raise(AlertKind.MonitorIncident, monitor, $"Monitor {monitor.Name} needs attention", reason ?? "Something is wrong.");
        }
        else if (state == JobMonitorState.Healthy && previous == JobMonitorState.NeedsAttention)
        {
            Raise(AlertKind.MonitorRecovered, monitor, $"Monitor {monitor.Name} recovered", $"{monitor.Method} is healthy again.");
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
        if (!Alerts.HasSinks)
        {
            return;
        }

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

        if (!JobMonitors.Any || context.BackgroundJob.Job is not { } job)
        {
            return;
        }

        bool succeeded = context.NewState is SucceededState;
        if (!succeeded && context.NewState is not FailedState)
        {
            return;
        }

        string method = JobMonitors.GetMethodKey(job);
        if (!JobMonitors.ForMethod(method).Any())
        {
            return;
        }

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
