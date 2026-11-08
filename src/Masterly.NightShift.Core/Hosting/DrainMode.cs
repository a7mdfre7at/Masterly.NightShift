#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// The state of the processing switch: whether new work is held, since when, and why.
    /// </summary>
    public sealed class DrainModeStatus
    {
        internal DrainModeStatus(bool active, DateTime? since, string? reason)
            : this(active, since, reason, EmptyQueues)
        {
        }

        internal DrainModeStatus(bool active, DateTime? since, string? reason, IReadOnlyDictionary<string, string?> pausedQueues)
            : this(active, since, reason, pausedQueues, EmptyQueues)
        {
        }

        internal DrainModeStatus(bool active, DateTime? since, string? reason, IReadOnlyDictionary<string, string?> pausedQueues, IReadOnlyDictionary<string, string?> pausedServers)
        {
            Active = active;
            Since = since;
            Reason = reason;
            PausedQueues = pausedQueues;
            PausedServers = pausedServers;
        }

        /// <summary>Servers paused on their own, with the reason given for each (may be <c>null</c>).</summary>
        public IReadOnlyDictionary<string, string?> PausedServers { get; }

        /// <summary>Whether the server holds the jobs it picks up, either by the global switch or on its own. Needs the full server ID.</summary>
        public bool IsServerPaused(string serverId)
        {
            return Active || (serverId != null && PausedServers.ContainsKey(serverId));
        }

        private static readonly IReadOnlyDictionary<string, string?> EmptyQueues = new Dictionary<string, string?>();

        /// <summary>Queues paused on their own, with the reason given for each (may be <c>null</c>).</summary>
        public IReadOnlyDictionary<string, string?> PausedQueues { get; }

        /// <summary>Whether the queue is held, either by the global switch or on its own.</summary>
        public bool IsQueuePaused(string queue)
        {
            return Active || (queue != null && PausedQueues.ContainsKey(queue));
        }

        /// <summary>Whether workers hold new jobs instead of performing them.</summary>
        public bool Active { get; }

        /// <summary>When the switch was turned on.</summary>
        public DateTime? Since { get; }

        /// <summary>The reason given when the switch was turned on.</summary>
        public string? Reason { get; }

        /// <summary>The inactive status.</summary>
        public static readonly DrainModeStatus Inactive = new DrainModeStatus(false, null, null);
    }

    /// <summary>
    /// A cluster-wide switch that pauses processing without stopping servers: while it is on, every worker that
    /// picks up a job hands it back to the Scheduled state for <see cref="HoldInterval"/> instead of running it, and
    /// jobs that are already running finish normally. Turn it on before a deploy or a database maintenance window,
    /// from the dashboard's Servers page or with <see cref="Enable"/>.
    /// </summary>
    public static class DrainMode
    {
        /// <summary>The storage hash holding the switch.</summary>
        public const string HashKey = "hangfire:control";

        /// <summary>Hash fields holding paused queues: <c>Queue:{name}</c> = reason.</summary>
        public const string QueueFieldPrefix = "Queue:";

        /// <summary>
        /// Hash fields holding paused servers: <c>Server:{id}</c> = reason. A field longer than a storage keeps
        /// (100 characters, so an ID of more than 93 characters, which long host names give) is stored as
        /// <c>Server:#{SHA-256 of the lowercased ID}</c> = <c>{id}</c>, a line feed, then the reason.
        /// </summary>
        public const string ServerFieldPrefix = "Server:";

        // What a resumed queue or server field holds, since hash fields can't be removed one by one. Printable, because
        // some storages refuse a NUL character in text (PostgreSQL does); a reason equal to it is stored with a space
        // added, so a pause never reads as resumed
        private const string ResumedMarker = "[resumed]";

        // Written by earlier builds, still read as resumed
        private const string LegacyResumedMarker = "\u0000resumed";

        // SQL Server keeps hash field names of up to 100 characters and cuts longer ones silently
        private const int MaxFieldLength = 100;
        private const char HashedServerMarker = '#';
        private const char HashedServerIdSeparator = '\n';
        private const int HashedServerFieldLength = 7 + 1 + 64; // "Server:" + "#" + SHA-256 in hex

        // One cache per storage, so two storages in one process never share an answer
        private static readonly ConditionalWeakTable<JobStorage, CacheSlot> StorageCaches = new ConditionalWeakTable<JobStorage, CacheSlot>();

        // For the overloads that don't name the storage
        private static readonly CacheSlot SharedCache = new CacheSlot();

        // Raised by InvalidateCache: an answer read before is no longer used
        private static long _cacheGeneration;

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>How long a held job waits in the Scheduled state before a worker looks at it again. Default 30 seconds.</summary>
        public static TimeSpan HoldInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long a process trusts the last answer before asking the storage again. Default 2 seconds, so the
        /// switch costs one small read every couple of seconds per process rather than one per job.
        /// </summary>
        public static TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Reads the switch directly from the storage.</summary>
        public static DrainModeStatus Read([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var hash = connection.GetAllEntriesFromHash(HashKey);
            if (hash == null) return DrainModeStatus.Inactive;

            var pausedQueues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var pausedServers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in hash)
            {
                if (pair.Value == ResumedMarker || pair.Value == LegacyResumedMarker) continue;

                if (pair.Key.StartsWith(QueueFieldPrefix, StringComparison.Ordinal) && pair.Key.Length > QueueFieldPrefix.Length)
                {
                    pausedQueues[pair.Key.Substring(QueueFieldPrefix.Length)] = String.IsNullOrEmpty(pair.Value) ? null : pair.Value;
                }
                else if (pair.Key.StartsWith(ServerFieldPrefix, StringComparison.Ordinal) && pair.Key.Length > ServerFieldPrefix.Length)
                {
                    var serverId = ReadServerPause(pair.Key, pair.Value, out var serverReason);
                    pausedServers[serverId] = serverReason;
                }
            }

            var active = hash.TryGetValue("Drain", out var drain) && drain == "1";
            if (!active && pausedQueues.Count == 0 && pausedServers.Count == 0) return DrainModeStatus.Inactive;

            var since = active && hash.TryGetValue("DrainSince", out var sinceValue) ? JobHelper.DeserializeNullableDateTime(sinceValue) : null;
            var reason = active && hash.TryGetValue("DrainReason", out var reasonValue) && reasonValue.Length > 0 ? reasonValue : null;

            return new DrainModeStatus(active, since, reason, pausedQueues, pausedServers);
        }

        /// <summary>
        /// Makes one server hold every job it picks up instead of running it: the job goes to the Scheduled state for
        /// <see cref="HoldInterval"/>, and is enqueued again after that. Any server may pick it up then, including the
        /// paused one, which holds it again, so a job can wait for more than one hold. The server's running jobs finish.
        /// </summary>
        /// <param name="connection">An open storage connection.</param>
        /// <param name="serverId">
        /// The full server ID, as shown on the dashboard's Servers page and in <c>IMonitoringApi.Servers()</c>:
        /// <c>{name}:{process id}:{guid}</c>, for example <c>web-1:4242:0f8fad5b-d9cb-469f-a165-70867728950e</c>.
        /// The server name alone doesn't match. The ID is new every time the server starts, so the pause doesn't
        /// survive a restart. A pause left for a server that is gone is cleared with
        /// <see cref="ResumeServer(IStorageConnection, string)"/> and the old ID, or with <see cref="ClearStaleServerPauses"/>.
        /// </param>
        /// <param name="reason">An optional reason, shown on the dashboard and in the held jobs' history.</param>
        public static void PauseServer([NotNull] IStorageConnection connection, [NotNull] string serverId, [CanBeNull] string? reason = null)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (String.IsNullOrWhiteSpace(serverId)) throw new ArgumentException("A server id is required.", nameof(serverId));

            var id = serverId.Trim();
            var field = GetServerField(id);

            // A hashed field doesn't hold the ID, so the value starts with it
            var value = IsHashedServerField(field) ? id + HashedServerIdSeparator + reason : ToStoredReason(reason);

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(HashKey, new[] { new KeyValuePair<string, string>(field, value) });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>
        /// Lets a paused server work again. Also clears the pause left for a server that has stopped: pass the old,
        /// full server ID.
        /// </summary>
        public static void ResumeServer([NotNull] IStorageConnection connection, [NotNull] string serverId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (String.IsNullOrWhiteSpace(serverId)) throw new ArgumentException("A server id is required.", nameof(serverId));

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(HashKey, new[] { new KeyValuePair<string, string>(GetServerField(serverId.Trim()), ResumedMarker) });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>
        /// Clears the pause of every server that is no longer running, that is, no longer listed by the storage's
        /// <see cref="IMonitoringApi.Servers"/> (a server that stopped, or one the server watchdog removed after it
        /// died). Server IDs are new every time a server starts, so such a pause never applies again; it only keeps
        /// the dashboard banner and the health check reporting a pause. Pauses of running servers are kept.
        /// </summary>
        /// <param name="storage">The storage whose pauses are checked.</param>
        /// <returns>The IDs of the servers whose pause was cleared, empty when there was none.</returns>
        public static IReadOnlyList<string> ClearStaleServerPauses([NotNull] JobStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            using (var connection = storage.GetConnection())
            {
                // The pauses are read before the servers: a server paused after this read is never cleared by mistake
                var pausedServers = Read(connection).PausedServers;
                if (pausedServers.Count == 0) return Array.Empty<string>();

                var runningServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var server in storage.GetMonitoringApi().Servers())
                {
                    runningServers.Add(server.Name);
                }

                var stale = new List<string>();
                var fields = new List<KeyValuePair<string, string>>();
                foreach (var serverId in pausedServers.Keys)
                {
                    if (runningServers.Contains(serverId)) continue;

                    stale.Add(serverId);
                    fields.Add(new KeyValuePair<string, string>(GetServerField(serverId), ResumedMarker));
                }

                if (stale.Count == 0) return stale;

                using (var transaction = connection.CreateWriteTransaction())
                {
                    transaction.SetRangeInHash(HashKey, fields);
                    transaction.Commit();
                }

                InvalidateCache();
                return stale;
            }
        }

        /// <summary>
        /// Gets the current status, using a process-wide cache shared by every caller that doesn't name the storage.
        /// With more than one storage in a process, use <see cref="GetStatus(JobStorage, IStorageConnection)"/>,
        /// which keeps one cache per storage.
        /// </summary>
        public static DrainModeStatus GetStatus([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return GetStatus(SharedCache, connection);
        }

        /// <summary>
        /// Gets the current status of <paramref name="storage"/>, using the cache this process keeps for that storage
        /// (see <see cref="CacheDuration"/>).
        /// </summary>
        /// <param name="storage">The storage the switch is read from; the cache is kept per storage.</param>
        /// <param name="connection">An open connection to <paramref name="storage"/>, used when the cache is out of date.</param>
        public static DrainModeStatus GetStatus([NotNull] JobStorage storage, [NotNull] IStorageConnection connection)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return GetStatus(StorageCaches.GetValue(storage, static _ => new CacheSlot()), connection);
        }

        private static DrainModeStatus GetStatus(CacheSlot slot, IStorageConnection connection)
        {
            var generation = Interlocked.Read(ref _cacheGeneration);
            var cached = Volatile.Read(ref slot.Entry);

            if (cached != null && cached.Generation == generation && CacheDuration > TimeSpan.Zero &&
                Stopwatch.GetTimestamp() - cached.Timestamp < CacheDuration.TotalSeconds * Stopwatch.Frequency)
            {
                return cached.Status;
            }

            var status = Read(connection);

            // Tagged with the generation seen before the read, so a change made meanwhile isn't hidden
            Volatile.Write(ref slot.Entry, new CacheEntry(status, Stopwatch.GetTimestamp(), generation));
            return status;
        }

        /// <summary>Holds the jobs of one queue; the other queues keep flowing. Running jobs finish.</summary>
        public static void PauseQueue([NotNull] IStorageConnection connection, [NotNull] string queue, [CanBeNull] string? reason = null)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (String.IsNullOrWhiteSpace(queue)) throw new ArgumentException("A queue name is required.", nameof(queue));

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(HashKey, new[]
                {
                    new KeyValuePair<string, string>(QueueFieldPrefix + queue.Trim().ToLowerInvariant(), ToStoredReason(reason)),
                });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>Lets the jobs of a paused queue go again. Held jobs run as soon as their hold interval ends.</summary>
        public static void ResumeQueue([NotNull] IStorageConnection connection, [NotNull] string queue)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (String.IsNullOrWhiteSpace(queue)) throw new ArgumentException("A queue name is required.", nameof(queue));

            var field = QueueFieldPrefix + queue.Trim().ToLowerInvariant();

            using (var transaction = connection.CreateWriteTransaction())
            {
                // Hash fields can't be removed one by one, so a resumed queue keeps a marker that reads as "not paused"
                transaction.SetRangeInHash(HashKey, new[] { new KeyValuePair<string, string>(field, ResumedMarker) });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>
        /// Tells whether the switch is on, using the process-wide cache shared by every caller that doesn't name the
        /// storage. With more than one storage in a process, use <see cref="IsActive(JobStorage, IStorageConnection)"/>.
        /// </summary>
        public static bool IsActive([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return GetStatus(connection).Active;
        }

        /// <summary>Tells whether the switch of <paramref name="storage"/> is on, using the cache kept for that storage.</summary>
        public static bool IsActive([NotNull] JobStorage storage, [NotNull] IStorageConnection connection)
        {
            return GetStatus(storage, connection).Active;
        }

        /// <summary>Turns the switch on. Running jobs finish; new ones are held.</summary>
        public static void Enable([NotNull] IStorageConnection connection, [CanBeNull] string? reason = null)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(HashKey, new[]
                {
                    new KeyValuePair<string, string>("Drain", "1"),
                    new KeyValuePair<string, string>("DrainSince", JobHelper.SerializeDateTime(Clock())),
                    new KeyValuePair<string, string>("DrainReason", reason ?? String.Empty),
                });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>Turns the switch off. Held jobs run as soon as their hold interval ends.</summary>
        public static void Disable([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(HashKey, new[]
                {
                    new KeyValuePair<string, string>("Drain", "0"),
                    new KeyValuePair<string, string>("DrainReason", String.Empty),
                });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>Forgets the cached answers of every storage, so the next check reads the storage.</summary>
        public static void InvalidateCache()
        {
            Interlocked.Increment(ref _cacheGeneration);
        }

        /// <summary>
        /// The hash field of a server's pause: <c>Server:{id}</c>, or, when that is longer than a storage keeps,
        /// <c>Server:#</c> followed by the SHA-256 of the lowercased ID in hex.
        /// </summary>
        internal static string GetServerField([NotNull] string serverId)
        {
            var field = ServerFieldPrefix + serverId;
            if (field.Length <= MaxFieldLength) return field;

            var bytes = Encoding.UTF8.GetBytes(serverId.ToLowerInvariant());
#if NET8_0_OR_GREATER
            var hash = SHA256.HashData(bytes);
#else
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
#endif

            const string digits = "0123456789abcdef";
            var builder = new StringBuilder(HashedServerFieldLength);
            builder.Append(ServerFieldPrefix).Append(HashedServerMarker);

            foreach (var value in hash)
            {
                builder.Append(digits[value >> 4]).Append(digits[value & 0xF]);
            }

            return builder.ToString();
        }

        private static string ToStoredReason(string? reason)
        {
            return reason == ResumedMarker ? reason + " " : reason ?? String.Empty;
        }

        private static bool IsHashedServerField(string field)
        {
            return field.Length == HashedServerFieldLength && field[ServerFieldPrefix.Length] == HashedServerMarker;
        }

        private static string ReadServerPause(string field, string? value, out string? reason)
        {
            if (IsHashedServerField(field) && value != null)
            {
                // A long ID is kept at the start of the value, before the reason
                var separator = value.IndexOf(HashedServerIdSeparator);
                if (separator > 0)
                {
                    reason = separator + 1 < value.Length ? value.Substring(separator + 1) : null;
                    return value.Substring(0, separator);
                }
            }

            reason = String.IsNullOrEmpty(value) ? null : value;
            return field.Substring(ServerFieldPrefix.Length);
        }

        private sealed class CacheSlot
        {
            public CacheEntry? Entry;
        }

        private sealed class CacheEntry
        {
            public CacheEntry(DrainModeStatus status, long timestamp, long generation)
            {
                Status = status;
                Timestamp = timestamp;
                Generation = generation;
            }

            public DrainModeStatus Status { get; }

            public long Timestamp { get; }

            public long Generation { get; }
        }
    }
}
