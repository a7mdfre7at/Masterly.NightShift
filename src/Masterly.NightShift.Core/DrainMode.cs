// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        /// <summary>Whether the server hands its jobs back, either by the global switch or on its own.</summary>
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

        /// <summary>Hash fields holding paused servers: <c>Server:{id}</c> = reason.</summary>
        public const string ServerFieldPrefix = "Server:";

        private const string ResumedMarker = "\u0000resumed";

        private static readonly object SyncRoot = new object();
        private static DrainModeStatus? _cached;
        private static long _cachedAtTicks;

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
                if (pair.Value == ResumedMarker) continue;

                if (pair.Key.StartsWith(QueueFieldPrefix, StringComparison.Ordinal) && pair.Key.Length > QueueFieldPrefix.Length)
                {
                    pausedQueues[pair.Key.Substring(QueueFieldPrefix.Length)] = String.IsNullOrEmpty(pair.Value) ? null : pair.Value;
                }
                else if (pair.Key.StartsWith(ServerFieldPrefix, StringComparison.Ordinal) && pair.Key.Length > ServerFieldPrefix.Length)
                {
                    pausedServers[pair.Key.Substring(ServerFieldPrefix.Length)] = String.IsNullOrEmpty(pair.Value) ? null : pair.Value;
                }
            }

            var active = hash.TryGetValue("Drain", out var drain) && drain == "1";
            if (!active && pausedQueues.Count == 0 && pausedServers.Count == 0) return DrainModeStatus.Inactive;

            var since = active && hash.TryGetValue("DrainSince", out var sinceValue) ? JobHelper.DeserializeNullableDateTime(sinceValue) : null;
            var reason = active && hash.TryGetValue("DrainReason", out var reasonValue) && reasonValue.Length > 0 ? reasonValue : null;

            return new DrainModeStatus(active, since, reason, pausedQueues, pausedServers);
        }

        /// <summary>Makes one server hand the jobs it fetches back to the queue, so the other servers take them. Its running jobs finish.</summary>
        public static void PauseServer([NotNull] IStorageConnection connection, [NotNull] string serverId, [CanBeNull] string? reason = null)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (String.IsNullOrWhiteSpace(serverId)) throw new ArgumentException("A server id is required.", nameof(serverId));

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(HashKey, new[] { new KeyValuePair<string, string>(ServerFieldPrefix + serverId.Trim(), reason ?? String.Empty) });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>Lets a paused server work again.</summary>
        public static void ResumeServer([NotNull] IStorageConnection connection, [NotNull] string serverId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (String.IsNullOrWhiteSpace(serverId)) throw new ArgumentException("A server id is required.", nameof(serverId));

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(HashKey, new[] { new KeyValuePair<string, string>(ServerFieldPrefix + serverId.Trim(), ResumedMarker) });
                transaction.Commit();
            }

            InvalidateCache();
        }

        /// <summary>Gets the current status, using the process-wide cache.</summary>
        public static DrainModeStatus GetStatus([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var cached = _cached;
            if (cached != null && CacheDuration > TimeSpan.Zero && Stopwatch.GetTimestamp() - _cachedAtTicks < CacheDuration.TotalSeconds * Stopwatch.Frequency)
            {
                return cached;
            }

            var status = Read(connection);

            lock (SyncRoot)
            {
                _cached = status;
                _cachedAtTicks = Stopwatch.GetTimestamp();
            }

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
                    new KeyValuePair<string, string>(QueueFieldPrefix + queue.Trim().ToLowerInvariant(), reason ?? String.Empty),
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

        /// <summary>Tells whether the switch is on, using the process-wide cache.</summary>
        public static bool IsActive([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return GetStatus(connection).Active;
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

        /// <summary>Forgets the cached answer, so the next check reads the storage.</summary>
        public static void InvalidateCache()
        {
            lock (SyncRoot)
            {
                _cached = null;
            }
        }
    }
}
