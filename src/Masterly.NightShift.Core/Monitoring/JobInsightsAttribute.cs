#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Records per-method statistics every time a job finishes: run and failure counts, the last 200
    /// durations, the last run and which <see cref="JobInvokerKind"/> performed it. The dashboard's Insights
    /// page turns them into failure rates, p95 durations, sparklines and slow-run warnings. Enable it with
    /// <c>GlobalConfiguration.Configuration.UseJobInsights()</c>, or apply it to selected job types.
    /// </summary>
    /// <remarks>
    /// Storage per method: set <c>insights</c> lists the keys; counters <c>insights:{key}:ok</c> and
    /// <c>insights:{key}:failed</c>; list <c>insights:{key}:durations</c>; hash <c>insights:{key}</c> with
    /// <c>LastRunAt</c>, <c>LastDurationMs</c>, <c>LastJobId</c>, <c>LastServerId</c>, <c>Invoker</c>.
    /// Reading requires a <see cref="JobStorageConnection"/>; writing works with any storage.
    /// Only runs that succeeded or failed are recorded: a run canceled by a filter, aborted by a state change or
    /// stopped by server shutdown, a run that threw <see cref="RetryLaterException"/>, and a run whose method never
    /// ran because <c>[Throttle]</c>, <c>[RateLimit]</c> or <c>[DisableConcurrentExecution]</c> gave up waiting
    /// (<see cref="ConcurrencyLimitException"/>, <see cref="DistributedLockTimeoutException"/>), are not. A duration
    /// is measured from the moment the job method is called, so it leaves out those waits.
    /// </remarks>
    public sealed class JobInsightsAttribute : JobFilterAttribute, IServerFilter
    {
        /// <summary>Set listing every recorded method key.</summary>
        public const string IndexKey = "insights";

        /// <summary>How many recent durations are kept per method.</summary>
        public const int DurationSampleSize = 200;

        /// <summary>
        /// The longest key <see cref="GetKey"/> returns, so the storage keys built from it (the longest is
        /// <c>insights:{key}:durations</c>) fit in 100 characters, the limit of SQL Server's key columns.
        /// </summary>
        public const int MaxKeyLength = 80;

        private const string StartedAtItem = "X_HF_Insights_StartedAt";

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobInsightsAttribute));

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>
        /// Gets the key under which a job's statistics are stored: <c>Type.Method</c>, with the type's name
        /// without its namespace, so methods of types with the same name in different namespaces share one
        /// entry. A key longer than <see cref="MaxKeyLength"/> keeps the start of the type name and the method
        /// name, with a hash of the whole key between them: <c>LongTypeName&lt;Arg~1a2b3c4d.Method</c>.
        /// </summary>
        public static string GetKey([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            var typeName = job.Type.ToGenericTypeString();
            var key = $"{typeName}.{job.Method.Name}";
            if (key.Length <= MaxKeyLength) return key;

            var hash = GetShortHash(key);
            var suffix = $"~{hash}.{job.Method.Name}";

            return suffix.Length <= MaxKeyLength - 10
                ? typeName.Substring(0, MaxKeyLength - suffix.Length) + suffix
                : $"{key.Substring(0, MaxKeyLength - hash.Length - 1)}~{hash}";
        }

        private static string GetShortHash(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
#if NET8_0_OR_GREATER
            var hash = SHA256.HashData(bytes);
#else
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
#endif
            var text = new StringBuilder(8);
            for (var i = 0; i < 4; i++)
            {
                text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        /// <inheritdoc />
        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            context.Items[StartedAtItem] = Stopwatch.StartNew();
        }

        /// <inheritdoc />
        public void OnPerformed(PerformedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.BackgroundJob.Job == null) return;

            // A canceled run (a filter canceled it, a state change aborted it, the server shut down), a run that
            // asked to be retried later, and a run whose method never ran because a concurrency filter gave up
            // waiting for a slot or a lock, are neither a success nor a failure of the method
            var outcome = JobOutcomes.Classify(context);
            if (outcome != JobOutcome.Succeeded && outcome != JobOutcome.Failed) return;

            var durationMs = GetDurationMs(context);
            var key = GetKey(context.BackgroundJob.Job);
            var failed = outcome == JobOutcome.Failed;
            var invoker = JobInvokerRegistry.GetInvoker(context.BackgroundJob.Job.Method).Kind;

            try
            {
                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    transaction.AddToSet(IndexKey, key);
                    transaction.IncrementCounter(failed ? GetFailedKey(key) : GetSucceededKey(key));
                    transaction.InsertToList(GetDurationsKey(key), durationMs.ToString(CultureInfo.InvariantCulture));
                    transaction.TrimList(GetDurationsKey(key), 0, DurationSampleSize - 1);
                    transaction.SetRangeInHash(GetHashKey(key), new[]
                    {
                        new KeyValuePair<string, string>("LastRunAt", JobHelper.SerializeDateTime(Clock())),
                        new KeyValuePair<string, string>("LastDurationMs", durationMs.ToString(CultureInfo.InvariantCulture)),
                        new KeyValuePair<string, string>("LastJobId", context.BackgroundJob.Id),
                        new KeyValuePair<string, string>("LastServerId", context.ServerId ?? String.Empty),
                        new KeyValuePair<string, string>("LastFailed", failed ? "1" : "0"),
                        new KeyValuePair<string, string>("Invoker", invoker.ToString()),
                    });
                    transaction.Commit();
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The job already ran: failing it here would retry it and repeat its side effects for a statistic
                Logger.WarnException($"Could not record the statistics of job '{context.BackgroundJob.Id}' ({key}); the job's outcome is not affected.", ex);
            }
        }

        /// <summary>
        /// The run's duration: from the moment the performer called the method, so the waits of filters that run
        /// inside this one (<c>[Throttle]</c>, <c>[RateLimit]</c>, <c>[DisableConcurrentExecution]</c>) are left
        /// out. From this filter's <c>OnPerforming</c> when the method wasn't called, or the performer didn't say when.
        /// </summary>
        private static long GetDurationMs(PerformedContext context)
        {
            var invokedAt = JobOutcomes.GetMethodInvokedTimestamp(context);
            if (invokedAt.HasValue)
            {
                return Math.Max(0, (Stopwatch.GetTimestamp() - invokedAt.Value) * 1000 / Stopwatch.Frequency);
            }

            var stopwatch = context.Items.TryGetValue(StartedAtItem, out var value) ? value as Stopwatch : null;
            return stopwatch?.ElapsedMilliseconds ?? 0;
        }

        /// <summary>
        /// Reads every recorded method's statistics, most recently run first. Returns nothing when the connection
        /// isn't a <see cref="JobStorageConnection"/> or the storage can't read lists and counters.
        /// </summary>
        public static IReadOnlyList<JobInsight> ReadAll([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            if (connection is not JobStorageConnection storageConnection)
            {
                return [];
            }

            var result = new List<JobInsight>();

            foreach (var key in connection.GetAllItemsFromSet(IndexKey) ?? new HashSet<string>())
            {
                var insight = Read(storageConnection, key);
                if (insight != null) result.Add(insight);
            }

            return result.OrderByDescending(static x => x.LastRunAt).ToList();
        }

        /// <summary>
        /// Reads one method's statistics, or <c>null</c> when nothing was recorded for the key or the storage can't
        /// read lists and counters.
        /// </summary>
        public static JobInsight? Read([NotNull] JobStorageConnection connection, [NotNull] string key)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (key == null) throw new ArgumentNullException(nameof(key));

            var hash = connection.GetAllEntriesFromHash(GetHashKey(key));
            if (hash == null || hash.Count == 0) return null;

            List<string> durationItems;
            long succeeded, failed;
            try
            {
                durationItems = connection.GetAllItemsFromList(GetDurationsKey(key)) ?? new List<string>();
                succeeded = connection.GetCounter(GetSucceededKey(key));
                failed = connection.GetCounter(GetFailedKey(key));
            }
            catch (NotSupportedException)
            {
                // A storage without the extended API (JobStorageFeatures.ExtendedApi)
                return null;
            }

            var durations = durationItems
                .Select(static x => Int64.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : -1)
                .Where(static x => x >= 0)
                .Reverse() // lists come back newest first
                .ToArray();

            return new JobInsight(
                key,
                succeeded: succeeded,
                failed: failed,
                durations: durations,
                lastRunAt: hash.TryGetValue("LastRunAt", out var lastRunAt) ? JobHelper.DeserializeNullableDateTime(lastRunAt) : null,
                lastDurationMs: hash.TryGetValue("LastDurationMs", out var lastDuration) && Int64.TryParse(lastDuration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastMs) ? lastMs : 0,
                lastJobId: hash.TryGetValue("LastJobId", out var lastJobId) ? lastJobId : null,
                lastServerId: hash.TryGetValue("LastServerId", out var lastServerId) && lastServerId.Length > 0 ? lastServerId : null,
                lastFailed: hash.TryGetValue("LastFailed", out var lastFailed) && lastFailed == "1",
                invoker: hash.TryGetValue("Invoker", out var invoker) ? invoker : null);
        }

        private static string GetHashKey(string key) => $"insights:{key}";
        private static string GetSucceededKey(string key) => $"insights:{key}:ok";
        private static string GetFailedKey(string key) => $"insights:{key}:failed";
        private static string GetDurationsKey(string key) => $"insights:{key}:durations";
    }

    /// <summary>Statistics of one job method, see <see cref="JobInsightsAttribute"/>.</summary>
    public sealed class JobInsight
    {
        internal JobInsight(
            string key,
            long succeeded,
            long failed,
            long[] durations,
            DateTime? lastRunAt,
            long lastDurationMs,
            string? lastJobId,
            string? lastServerId,
            bool lastFailed,
            string? invoker)
        {
            Key = key;
            Succeeded = succeeded;
            Failed = failed;
            Durations = durations;
            LastRunAt = lastRunAt;
            LastDurationMs = lastDurationMs;
            LastJobId = lastJobId;
            LastServerId = lastServerId;
            LastFailed = lastFailed;
            Invoker = invoker;

            if (durations.Length > 0)
            {
                var sorted = durations.OrderBy(static x => x).ToArray();
                AverageMs = (long)sorted.Average();
                MaxMs = sorted[sorted.Length - 1];
                P95Ms = Percentile(sorted, 0.95);
                MedianMs = sorted[sorted.Length / 2];
            }

            if (durations.Length > 1)
            {
                // The runs before the last one, so a single slow run can't hide itself in the percentile
                _previousP95Ms = Percentile(durations.Take(durations.Length - 1).OrderBy(static x => x).ToArray(), 0.95);
            }

            if (durations.Length >= RecentSampleSize * 2)
            {
                var recent = durations.Skip(durations.Length - RecentSampleSize).OrderBy(static x => x).ToArray();
                var earlier = durations.Take(durations.Length - RecentSampleSize).OrderBy(static x => x).ToArray();

                RecentMedianMs = recent[recent.Length / 2];
                EarlierMedianMs = earlier[earlier.Length / 2];
            }
            else
            {
                RecentMedianMs = MedianMs;
                EarlierMedianMs = MedianMs;
            }
        }

        /// <summary>How many of the latest runs form the "recent" window of the drift detection.</summary>
        public const int RecentSampleSize = 10;

        /// <summary>Gets the median of the last <see cref="RecentSampleSize"/> runs.</summary>
        public long RecentMedianMs { get; }

        /// <summary>Gets the median of the runs before the recent window.</summary>
        public long EarlierMedianMs { get; }

        /// <summary>
        /// Gets whether the method got slower: the recent median is at least twice the earlier one and at least a
        /// second, with enough samples on both sides to judge.
        /// </summary>
        public bool IsSlowingDown => Durations.Length >= RecentSampleSize * 2 && RecentMedianMs >= 1000 && RecentMedianMs >= EarlierMedianMs * 2;

        /// <summary>Gets whether at least a fifth of the runs failed, with ten or more runs recorded.</summary>
        public bool HasHighFailureRate => Total >= 10 && FailureRate >= 0.2;

        /// <summary>Gets whether anything about this method deserves attention.</summary>
        public bool IsAnomalous => IsSlowingDown || HasHighFailureRate || LastRunWasSlow;

        /// <summary>
        /// Estimates when a run that started at <paramref name="startedAt"/> finishes, from the median duration.
        /// Returns <c>null</c> without enough samples (three) to guess.
        /// </summary>
        public DateTime? EstimateCompletion(DateTime startedAt)
        {
            if (Durations.Length < 3 || MedianMs <= 0) return null;
            return startedAt.AddMilliseconds(MedianMs);
        }

        private readonly long _previousP95Ms;

        private static long Percentile(long[] sorted, double percentile)
        {
            return sorted[Math.Min(sorted.Length - 1, Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1))];
        }

        /// <summary>Gets the method key, <c>Type.Method</c>.</summary>
        public string Key { get; }

        /// <summary>Gets how many runs succeeded.</summary>
        public long Succeeded { get; }

        /// <summary>Gets how many runs failed.</summary>
        public long Failed { get; }

        /// <summary>Gets all recorded runs.</summary>
        public long Total => Succeeded + Failed;

        /// <summary>Gets the share of failed runs, 0 to 1.</summary>
        public double FailureRate => Total == 0 ? 0 : (double)Failed / Total;

        /// <summary>Gets the recent durations in milliseconds, oldest first.</summary>
        public long[] Durations { get; }

        /// <summary>Gets the mean of the recent durations.</summary>
        public long AverageMs { get; }

        /// <summary>Gets the median of the recent durations.</summary>
        public long MedianMs { get; }

        /// <summary>Gets the 95th percentile of the recent durations.</summary>
        public long P95Ms { get; }

        /// <summary>Gets the longest recent duration.</summary>
        public long MaxMs { get; }

        /// <summary>Gets when the method last ran.</summary>
        public DateTime? LastRunAt { get; }

        /// <summary>Gets how long the last run took.</summary>
        public long LastDurationMs { get; }

        /// <summary>Gets the id of the last job.</summary>
        public string? LastJobId { get; }

        /// <summary>Gets the server that performed the last job.</summary>
        public string? LastServerId { get; }

        /// <summary>Gets whether the last run failed.</summary>
        public bool LastFailed { get; }

        /// <summary>Gets the <see cref="JobInvokerKind"/> name of the last run.</summary>
        public string? Invoker { get; }

        /// <summary>
        /// Gets whether the last run was unusually slow: more than three times the 95th percentile of the
        /// recent runs, and at least a second, with enough samples to judge.
        /// </summary>
        public bool LastRunWasSlow => Durations.Length >= 10 && LastDurationMs >= 1000 && LastDurationMs > _previousP95Ms * 3;
    }
}
