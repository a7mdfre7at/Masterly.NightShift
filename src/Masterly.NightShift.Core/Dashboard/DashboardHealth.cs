#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>One check of a <see cref="HealthReport"/>.</summary>
    internal sealed class HealthCheck
    {
        public HealthCheck(string name, HealthStatus status, string detail)
        {
            Name = name;
            Status = status;
            Detail = detail;
        }

        public string Name { get; }
        public HealthStatus Status { get; }
        public string Detail { get; }
    }

    internal enum HealthStatus
    {
        Healthy,
        Degraded,
        Unhealthy,
    }

    /// <summary>What <c>GET /health</c> answers.</summary>
    internal sealed class HealthReport
    {
        public HealthReport(HealthStatus status, DateTime checkedAt, IReadOnlyList<HealthCheck> checks, StatisticsDto? statistics, IReadOnlyList<QueueWithTopEnqueuedJobsDto> queues, int servers, bool paused, int lateRecurringJobs = 0)
        {
            Status = status;
            CheckedAt = checkedAt;
            Checks = checks;
            Statistics = statistics;
            Queues = queues;
            Servers = servers;
            Paused = paused;
            LateRecurringJobs = lateRecurringJobs;
        }

        public HealthStatus Status { get; }
        public DateTime CheckedAt { get; }
        public IReadOnlyList<HealthCheck> Checks { get; }
        public StatisticsDto? Statistics { get; }
        public IReadOnlyList<QueueWithTopEnqueuedJobsDto> Queues { get; }
        public int Servers { get; }
        public bool Paused { get; }

        /// <summary>How many recurring jobs are late, counted up to <see cref="DashboardHealth.MaxRecurringCandidates"/>.</summary>
        public int LateRecurringJobs { get; }

        public object ToJson()
        {
            return new
            {
                status = Describe(Status),
                checkedAt = JobHelper.ToTimestamp(CheckedAt),
                checks = Checks.Select(static c => new { name = c.Name, status = Describe(c.Status), detail = c.Detail }).ToArray(),
                servers = Servers,
                paused = Paused,
                queues = Queues.Select(static q => new { name = q.Name, length = q.Length, fetched = q.Fetched }).ToArray(),
                enqueued = Statistics?.Enqueued,
                scheduled = Statistics?.Scheduled,
                processing = Statistics?.Processing,
                failed = Statistics?.Failed,
                awaiting = Statistics?.Awaiting,
                recurring = Statistics?.Recurring,
                retries = Statistics?.Retries,
            };
        }

        public static string Describe(HealthStatus status)
        {
            switch (status)
            {
                case HealthStatus.Healthy: return "healthy";
                case HealthStatus.Degraded: return "degraded";
                default: return "unhealthy";
            }
        }
    }

    /// <summary>
    /// The checks behind <c>GET /health</c>: the storage answers, at least one server is alive, every queue
    /// with jobs is listened to by a live server, processing isn't paused, no recurring job is late or disabled
    /// by an error. <c>unhealthy</c> (HTTP 503) means jobs cannot be processed at all; <c>degraded</c> means
    /// they are, but something needs attention. Pauses left by servers that are no longer registered are
    /// ignored (see <see cref="GetStalePausedServers"/>). Storage errors are logged; the report only says
    /// which check failed, because <c>/health</c> is often reachable without signing in.
    /// </summary>
    internal static class DashboardHealth
    {
        /// <summary>How late a recurring job's next run may be before it counts as late.</summary>
        public static readonly TimeSpan RecurringLateThreshold = TimeSpan.FromMinutes(1);

        /// <summary>How long <see cref="GetReport"/> reuses a report of the same storage.</summary>
        public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

        /// <summary>How many late, and how many unscheduled, recurring jobs the check reads at most.</summary>
        public const int MaxRecurringCandidates = 500;

        private const string RecurringJobsSetKey = "recurring-jobs";

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(DashboardHealth));
        private static readonly StorageCache<HealthReport> Reports = new StorageCache<HealthReport>(CacheDuration);

        /// <summary>
        /// Whether a recurring job's next run was due a while ago and nobody started it. A job with an error and a
        /// next run is waiting for the scheduler's retry; it is late too when that retry is overdue, as the
        /// score query of <see cref="Check"/> finds it.
        /// </summary>
        public static bool IsLate(RecurringJobDto job, DateTime utcNow)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return !job.Paused && !job.Removed && job.NextExecution.HasValue && job.NextExecution.Value < utcNow - RecurringLateThreshold;
        }

        /// <summary>
        /// The paused server ids that match no registered server, in order. A server gets a new id every time it
        /// starts, so a pause outlives a server that stops or restarts. Such a pause holds nothing back; the
        /// Servers page lists it so it can be cleared.
        /// </summary>
        public static IReadOnlyList<string> GetStalePausedServers(DrainModeStatus drain, IEnumerable<ServerDto>? servers)
        {
            if (drain == null) throw new ArgumentNullException(nameof(drain));
            if (drain.PausedServers.Count == 0) return Array.Empty<string>();

            var registered = new HashSet<string>((servers ?? Array.Empty<ServerDto>()).Select(static s => s.Name), StringComparer.OrdinalIgnoreCase);
            return drain.PausedServers.Keys.Where(id => !registered.Contains(id)).OrderBy(static id => id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The report of <see cref="Check"/>, computed at most once every <see cref="CacheDuration"/> per storage
        /// and options, however many probes and scrapers ask.
        /// </summary>
        public static HealthReport GetReport(JobStorage storage, DashboardOptions options, DateTime utcNow)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (options == null) throw new ArgumentNullException(nameof(options));

            return Reports.Get(storage, options, utcNow, () => Check(storage, options, utcNow));
        }

        public static HealthReport Check(JobStorage storage, DashboardOptions options, DateTime utcNow)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var checks = new List<HealthCheck>();
            StatisticsDto? statistics = null;
            IList<ServerDto> servers = Array.Empty<ServerDto>();
            IList<QueueWithTopEnqueuedJobsDto> queues = Array.Empty<QueueWithTopEnqueuedJobsDto>();
            var paused = false;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var monitor = storage.GetMonitoringApi();
                statistics = monitor.GetStatistics();
                servers = monitor.Servers() ?? Array.Empty<ServerDto>();
                queues = GetQueueCounts(storage, monitor) ?? Array.Empty<QueueWithTopEnqueuedJobsDto>();
                checks.Add(new HealthCheck("storage", HealthStatus.Healthy, $"answered in {stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms"));
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The message can name the server, the database or the login: it goes to the log, not the report
                Logger.WarnException("Health check: the storage did not answer.", ex);
                checks.Add(new HealthCheck("storage", HealthStatus.Unhealthy, "the storage did not answer, see the log"));
                return new HealthReport(HealthStatus.Unhealthy, utcNow, checks, null, Array.Empty<QueueWithTopEnqueuedJobsDto>(), 0, false);
            }

            var alive = servers.Where(s => !s.Heartbeat.HasValue || s.Heartbeat.Value >= utcNow - options.ServerPossiblyAbortedThreshold).ToList();
            var stale = servers.Count - alive.Count;

            if (alive.Count == 0)
            {
                checks.Add(new HealthCheck("servers", HealthStatus.Unhealthy, servers.Count == 0 ? "no servers are registered" : $"all {servers.Count.ToString(CultureInfo.InvariantCulture)} servers missed their heartbeat"));
            }
            else if (stale > 0)
            {
                checks.Add(new HealthCheck("servers", HealthStatus.Degraded, $"{alive.Count.ToString(CultureInfo.InvariantCulture)} alive, {stale.ToString(CultureInfo.InvariantCulture)} missed their heartbeat"));
            }
            else
            {
                checks.Add(new HealthCheck("servers", HealthStatus.Healthy, $"{alive.Count.ToString(CultureInfo.InvariantCulture)} alive, {alive.Sum(static s => s.WorkersCount).ToString(CultureInfo.InvariantCulture)} workers"));
            }

            var served = new HashSet<string>(alive.SelectMany(static s => s.Queues ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
            var unserved = queues.Where(q => q.Length > 0 && !served.Contains(q.Name)).Select(static q => q.Name).ToList();

            if (unserved.Count > 0 && alive.Count > 0)
            {
                checks.Add(new HealthCheck("queues", HealthStatus.Degraded, $"no live server listens to: {String.Join(", ", unserved)}"));
            }
            else
            {
                checks.Add(new HealthCheck("queues", HealthStatus.Healthy, $"{queues.Count.ToString(CultureInfo.InvariantCulture)} queues, {queues.Sum(static q => q.Length).ToString(CultureInfo.InvariantCulture)} jobs waiting"));
            }

            var lateCount = 0;

            try
            {
                using (var connection = storage.GetReadOnlyConnection())
                {
                    var drain = DrainMode.Read(connection);
                    var stalePausedServers = GetStalePausedServers(drain, servers);
                    var pausedServers = drain.PausedServers.Keys.Except(stalePausedServers, StringComparer.OrdinalIgnoreCase).OrderBy(static q => q, StringComparer.Ordinal).ToList();

                    paused = drain.Active;
                    if (paused)
                    {
                        checks.Add(new HealthCheck("processing", HealthStatus.Degraded, String.IsNullOrEmpty(drain.Reason) ? "paused from the dashboard" : $"paused: {drain.Reason}"));
                    }
                    else if (drain.PausedQueues.Count > 0 || pausedServers.Count > 0)
                    {
                        var parts = new List<string>();
                        if (drain.PausedQueues.Count > 0) parts.Add($"queues paused: {String.Join(", ", drain.PausedQueues.Keys.OrderBy(static q => q, StringComparer.Ordinal))}");
                        if (pausedServers.Count > 0) parts.Add($"servers paused: {String.Join(", ", pausedServers)}");
                        checks.Add(new HealthCheck("processing", HealthStatus.Degraded, String.Join("; ", parts)));
                    }
                    else
                    {
                        checks.Add(new HealthCheck("processing", HealthStatus.Healthy, "running"));
                    }

                    var recurring = ReadRecurring(storage, connection, statistics, utcNow);
                    var late = recurring.Late;
                    var disabled = recurring.Disabled;
                    lateCount = late.Count;

                    if (late.Count > 0)
                    {
                        checks.Add(new HealthCheck("recurring", HealthStatus.Degraded, $"{late.Count.ToString(CultureInfo.InvariantCulture)} late: {String.Join(", ", late.Take(5))}{(late.Count > 5 ? ", …" : String.Empty)}"));
                    }
                    else if (disabled.Count > 0)
                    {
                        checks.Add(new HealthCheck("recurring", HealthStatus.Degraded, $"{disabled.Count.ToString(CultureInfo.InvariantCulture)} disabled by errors: {String.Join(", ", disabled.Take(5))}{(disabled.Count > 5 ? ", …" : String.Empty)}"));
                    }
                    else
                    {
                        checks.Add(new HealthCheck("recurring", HealthStatus.Healthy, $"{recurring.Total.ToString(CultureInfo.InvariantCulture)} on schedule"));
                    }
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException("Health check: could not read the processing state or the recurring jobs.", ex);
                checks.Add(new HealthCheck("recurring", HealthStatus.Degraded, "could not read the recurring jobs, see the log"));
            }

            var status = checks.Any(static c => c.Status == HealthStatus.Unhealthy) ? HealthStatus.Unhealthy
                : checks.Any(static c => c.Status == HealthStatus.Degraded) ? HealthStatus.Degraded
                : HealthStatus.Healthy;

            return new HealthReport(status, utcNow, checks, statistics, queues.ToList(), alive.Count, paused, lateCount);
        }

        /// <summary>
        /// The queues with their lengths, through <see cref="JobStorageMonitor.QueueCounts"/> when the storage has a
        /// cheaper query for them (<see cref="JobStorageFeatures.Monitoring.QueueCounts"/>), otherwise through
        /// <see cref="IMonitoringApi.Queues"/>, which also reads each queue's first jobs.
        /// </summary>
        internal static IList<QueueWithTopEnqueuedJobsDto>? GetQueueCounts(JobStorage storage, IMonitoringApi monitor)
        {
            return monitor is JobStorageMonitor storageMonitor && storage.HasFeature(JobStorageFeatures.Monitoring.QueueCounts)
                ? storageMonitor.QueueCounts()
                : monitor.Queues();
        }

        /// <summary>
        /// Finds the late recurring jobs and the ones disabled by errors. With a storage that can query the
        /// <c>recurring-jobs</c> set by score (the next execution, <c>-1</c> for none), it reads only the ids due
        /// more than <see cref="RecurringLateThreshold"/> ago, and the error field of the unscheduled ones (paused
        /// or disabled), instead of every recurring job with its last job's state.
        /// </summary>
        private static RecurringProblems ReadRecurring(JobStorage storage, IStorageConnection connection, StatisticsDto? statistics, DateTime utcNow)
        {
            if (connection is JobStorageConnection storageConnection &&
                storage.HasFeature(JobStorageFeatures.ExtendedApi) &&
                storage.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest))
            {
                // Jobs whose next run, or next retry, was due a while ago and nobody started them
                var late = storageConnection.GetFirstByLowestScoreFromSet(RecurringJobsSetKey, 0, JobHelper.ToTimestamp(utcNow - RecurringLateThreshold), MaxRecurringCandidates)
                           ?? new List<string>();

                // Paused and disabled jobs have no next run; only the disabled ones keep an error
                var disabled = new List<string>();
                foreach (var id in storageConnection.GetFirstByLowestScoreFromSet(RecurringJobsSetKey, -1, -1, MaxRecurringCandidates) ?? new List<string>())
                {
                    if (!String.IsNullOrWhiteSpace(storageConnection.GetValueFromHash($"recurring-job:{id}", "Error"))) disabled.Add(id);
                }

                var total = statistics?.Recurring ?? storageConnection.GetSetCount(RecurringJobsSetKey);
                return new RecurringProblems(late, disabled, total);
            }

            var recurringJobs = connection.GetRecurringJobs();
            return new RecurringProblems(
                recurringJobs.Where(j => IsLate(j, utcNow)).Select(static j => j.Id).ToList(),
                recurringJobs.Where(static j => j.Error != null && !j.Removed && !j.NextExecution.HasValue).Select(static j => j.Id).ToList(),
                recurringJobs.Count(static j => !j.Removed));
        }

        private sealed class RecurringProblems
        {
            public RecurringProblems(IReadOnlyList<string> late, IReadOnlyList<string> disabled, long total)
            {
                Late = late;
                Disabled = disabled;
                Total = total;
            }

            public IReadOnlyList<string> Late { get; }
            public IReadOnlyList<string> Disabled { get; }
            public long Total { get; }
        }
    }

    /// <summary>
    /// Keeps one computed value per storage and report settings for a short time, so frequent probes and scrapes
    /// don't repeat the same storage queries. Concurrent callers wait for one computation instead of all running it.
    /// </summary>
    /// <remarks>
    /// The dashboard, <c>MapNightShiftHealth</c> and <c>MapNightShiftMetrics</c> usually pass different
    /// <see cref="DashboardOptions"/> instances, so the options are compared by the only value the report depends
    /// on, <see cref="DashboardOptions.ServerPossiblyAbortedThreshold"/>, not by reference.
    /// </remarks>
    internal sealed class StorageCache<T>
        where T : class
    {
        private readonly ConditionalWeakTable<JobStorage, Dictionary<TimeSpan, Entry>> _entries = new ConditionalWeakTable<JobStorage, Dictionary<TimeSpan, Entry>>();
        private readonly TimeSpan _duration;

        public StorageCache(TimeSpan duration)
        {
            _duration = duration;
        }

        public T Get(JobStorage storage, DashboardOptions options, DateTime utcNow, Func<T> compute)
        {
            var entries = _entries.GetValue(storage, static _ => new Dictionary<TimeSpan, Entry>());
            Entry? entry;

            lock (entries)
            {
                if (!entries.TryGetValue(options.ServerPossiblyAbortedThreshold, out entry) || entry == null)
                {
                    entry = new Entry();
                    entries[options.ServerPossiblyAbortedThreshold] = entry;
                }
            }

            lock (entry)
            {
                if (entry.Value != null && utcNow >= entry.ComputedAt && utcNow - entry.ComputedAt < _duration)
                {
                    return entry.Value;
                }

                var value = compute();
                entry.Value = value;
                entry.ComputedAt = utcNow;
                return value;
            }
        }

        private sealed class Entry
        {
            public T? Value;
            public DateTime ComputedAt;
        }
    }
}
