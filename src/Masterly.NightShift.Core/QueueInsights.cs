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
using System.Globalization;
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>How a queue is doing: how long jobs wait in it and how fast it drains.</summary>
    public sealed class QueueHealth
    {
        internal QueueHealth(string queue, long[] latenciesMs, long startedLast5Minutes, DateTime? lastStartedAt)
        {
            Queue = queue;
            Samples = latenciesMs.Length;
            StartedLast5Minutes = startedLast5Minutes;
            LastStartedAt = lastStartedAt;

            if (latenciesMs.Length > 0)
            {
                var sorted = latenciesMs.OrderBy(static x => x).ToArray();
                MedianLatencyMs = sorted[sorted.Length / 2];
                P95LatencyMs = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
            }
        }

        public string Queue { get; }

        /// <summary>How many of the latest job starts contributed to the percentiles (up to 200).</summary>
        public int Samples { get; }

        /// <summary>Median time a job waited in the queue before a worker took it.</summary>
        public long MedianLatencyMs { get; }

        /// <summary>95th percentile of the wait.</summary>
        public long P95LatencyMs { get; }

        /// <summary>How many jobs workers took from the queue in the last five whole minutes.</summary>
        public long StartedLast5Minutes { get; }

        /// <summary>Jobs taken per minute, over the last five minutes.</summary>
        public double PerMinute => StartedLast5Minutes / 5.0;

        /// <summary>When a worker last took a job from the queue.</summary>
        public DateTime? LastStartedAt { get; }

        /// <summary>How long the queue needs to empty at the current rate, or <c>null</c> when nothing moves.</summary>
        public TimeSpan? EstimateDrain(long length)
        {
            if (length <= 0) return TimeSpan.Zero;
            if (PerMinute <= 0) return null;

            return TimeSpan.FromMinutes(length / PerMinute);
        }

        /// <summary>Whether the queue holds jobs that nobody took in the last five minutes.</summary>
        public bool IsStalled(long length) => length > 0 && StartedLast5Minutes == 0 && (LastStartedAt == null || LastStartedAt < QueueInsights.Clock().AddMinutes(-5));
    }

    /// <summary>
    /// Records, per queue, how long jobs waited before a worker took them and how many were taken per minute,
    /// so the Queues page can show latency percentiles, throughput and "clears in about 12 minutes". Turned on
    /// by <c>UseJobInsights()</c> together with the per-method statistics.
    /// </summary>
    /// <remarks>
    /// Storage: set <c>queue-insights</c> (queue names); list <c>queue:{name}:latency</c> (last 200 waits in
    /// ms); counters <c>queue:{name}:started:{yyyyMMddHHmm}</c> expiring after an hour; hash <c>queue:{name}:insights</c>
    /// (<c>LastStartedAt</c>). One extra state read per job start.
    /// </remarks>
    public static class QueueInsights
    {
        /// <summary>Set listing every queue with recorded statistics.</summary>
        public const string IndexKey = "queue-insights";

        /// <summary>How many recent waits are kept per queue.</summary>
        public const int LatencySampleSize = 200;

        private static readonly TimeSpan CounterLifetime = TimeSpan.FromHours(1);

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        public static string GetLatencyKey(string queue) => $"queue:{queue}:latency";
        public static string GetHashKey(string queue) => $"queue:{queue}:insights";
        public static string GetStartedKey(string queue, DateTime minuteUtc) => $"queue:{queue}:started:{minuteUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";

        /// <summary>Records that a worker took a job that waited <paramref name="latency"/> in <paramref name="queue"/>.</summary>
        public static void Record([NotNull] IWriteOnlyTransaction transaction, [NotNull] string queue, TimeSpan latency, DateTime now)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (queue == null) throw new ArgumentNullException(nameof(queue));

            var ms = Math.Max(0, (long)latency.TotalMilliseconds);

            transaction.AddToSet(IndexKey, queue);
            transaction.InsertToList(GetLatencyKey(queue), ms.ToString(CultureInfo.InvariantCulture));
            transaction.TrimList(GetLatencyKey(queue), 0, LatencySampleSize - 1);
            transaction.IncrementCounter(GetStartedKey(queue, Truncate(now)), CounterLifetime);
            transaction.SetRangeInHash(GetHashKey(queue), new[] { new KeyValuePair<string, string>("LastStartedAt", JobHelper.SerializeDateTime(now)) });
        }

        /// <summary>Reads a queue's statistics, or <c>null</c> when nothing was recorded or the storage can't read lists.</summary>
        public static QueueHealth? Read([NotNull] IStorageConnection connection, [NotNull] string queue)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (queue == null) throw new ArgumentNullException(nameof(queue));

            if (connection is not JobStorageConnection storageConnection) return null;

            var hash = storageConnection.GetAllEntriesFromHash(GetHashKey(queue));
            if (hash == null) return null;

            var latencies = (storageConnection.GetAllItemsFromList(GetLatencyKey(queue)) ?? new List<string>())
                .Select(static x => Int64.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : -1)
                .Where(static x => x >= 0)
                .ToArray();

            var now = Truncate(Clock());
            long started = 0;
            for (var i = 1; i <= 5; i++)
            {
                started += storageConnection.GetCounter(GetStartedKey(queue, now.AddMinutes(-i)));
            }

            // The current, partial minute counts too, so a burst shows up right away
            started += storageConnection.GetCounter(GetStartedKey(queue, now));

            var lastStartedAt = hash.TryGetValue("LastStartedAt", out var last) ? JobHelper.DeserializeNullableDateTime(last) : null;

            return new QueueHealth(queue, latencies, started, lastStartedAt);
        }

        /// <summary>Reads the statistics of every queue that has some.</summary>
        public static IReadOnlyDictionary<string, QueueHealth> ReadAll([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var result = new Dictionary<string, QueueHealth>(StringComparer.OrdinalIgnoreCase);
            if (connection is not JobStorageConnection) return result;

            foreach (var queue in connection.GetAllItemsFromSet(IndexKey) ?? new HashSet<string>())
            {
                var health = Read(connection, queue);
                if (health != null) result[queue] = health;
            }

            return result;
        }

        private static DateTime Truncate(DateTime utc) => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// The filter behind <see cref="QueueInsights"/>: when a job enters the Processing state it reads the
    /// Enqueued state it came from (queue and time) and records the wait.
    /// </summary>
    public sealed class QueueInsightsFilter : JobFilterAttribute, IApplyStateFilter
    {
        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            if (context.NewState is not ProcessingState) return;
            if (!EnqueuedState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase)) return;

            StateData? previous;
            try
            {
                previous = context.Connection.GetStateData(context.BackgroundJob.Id);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return;
            }

            if (previous?.Data == null || !previous.Data.TryGetValue("Queue", out var queue) || String.IsNullOrEmpty(queue)) return;

            var now = QueueInsights.Clock();
            var enqueuedAt = previous.Data.TryGetValue("EnqueuedAt", out var enqueued) ? JobHelper.DeserializeNullableDateTime(enqueued) : null;
            var latency = enqueuedAt.HasValue ? now - enqueuedAt.Value : TimeSpan.Zero;

            QueueInsights.Record(transaction, queue, latency, now);
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
