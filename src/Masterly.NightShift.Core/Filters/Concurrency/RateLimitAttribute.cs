#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Limits how many jobs sharing a resource may <i>start</i> within a sliding time window, across all
    /// servers. For example <c>[RateLimit("sms", Limit = 100, PerSeconds = 60)]</c> starts at most 100 SMS
    /// jobs per minute; the rest are rescheduled to the moment the oldest start leaves the window.
    /// </summary>
    /// <remarks>
    /// <para>The resource is a template like in <see cref="ThrottleAttribute"/>. Starts are members of the set
    /// <c>ratelimit:{resource}:{perSeconds}</c>, so limits with different windows on one resource are counted apart.
    /// Nothing has to be released when the job ends; a start is given back only when the job method was never about
    /// to be called: a later filter canceled the run, or threw first. When the storage supports it, the set expires
    /// one window (plus a minute) after the last recorded start.</para>
    /// <para>A job can carry several limits, such as 10 per second and 1000 per hour on one resource, and global,
    /// class and method limits all apply. The job starts only when every limit allows it: when one refuses, the starts
    /// the others recorded are given back. Two limits with the same resource and window count the same starts. A limit
    /// with <see cref="ConcurrencyAction.Wait"/> waits only while the run holds no start another limit recorded;
    /// otherwise it reschedules the job like <see cref="ConcurrencyAction.Reschedule"/>, because the start already
    /// recorded would age while it waited.</para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RateLimitAttribute : JobFilterAttribute, IServerFilter, IElectStateFilter
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(RateLimitAttribute));
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ExpirationMargin = TimeSpan.FromMinutes(1);

        // How many starts that left the window one start removes when the storage can read them by score. More of
        // them (a large set after a quiet period) are removed by reading the whole set once.
        private const int StaleBatchSize = 100;

        // Above any timestamp in seconds, and exact as a double in every storage
        private const double MaxScore = 1e15;

        private int _waitTimeoutSeconds;

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="resource">Resource template. <c>null</c> means the job's method (full type name, method name and parameter types).</param>
        /// <param name="limit">How many jobs may start per window.</param>
        /// <param name="perSeconds">Length of the sliding window, in seconds.</param>
        public RateLimitAttribute([CanBeNull] string? resource, int limit, int perSeconds)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit should be greater than zero.");
            if (perSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(perSeconds), "Window should be greater than zero.");

            Resource = resource;
            Limit = limit;
            PerSeconds = perSeconds;
            Action = ConcurrencyAction.Reschedule;
            WaitTimeoutSeconds = 30;
        }

        /// <summary>Gets the resource template.</summary>
        [CanBeNull]
        public string? Resource { get; }

        /// <summary>Gets how many jobs may start per window.</summary>
        public int Limit { get; }

        /// <summary>Gets the window length in seconds.</summary>
        public int PerSeconds { get; }

        /// <summary>Gets or sets what happens when the limit is reached. Default is <see cref="ConcurrencyAction.Reschedule"/>.</summary>
        public ConcurrencyAction Action { get; set; }

        /// <summary>
        /// Gets or sets the longest time <see cref="ConcurrencyAction.Wait"/> holds the worker before failing the job, even
        /// when the next free slot is further away. Default is 30 seconds. Zero or greater.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int WaitTimeoutSeconds
        {
            get => _waitTimeoutSeconds;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "WaitTimeoutSeconds should be zero or greater.");
                _waitTimeoutSeconds = value;
            }
        }

        /// <inheritdoc />
        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var resource = ConcurrencyControl.FormatResourceKey(Resource, context.BackgroundJob.Job!, MaxResourceLength);
            var key = GetKey(resource);
            var waitUntil = ConcurrencyControl.Clock().AddSeconds(WaitTimeoutSeconds);

            // Another limit of this job with the same resource and window already recorded this run's start
            var sharedStart = context.Items.ContainsKey(GetRecordedItemKey(key));

            // A start another limit recorded would age while this one waits, so the job could start after it left
            // that limit's window: this limit doesn't wait then, it reschedules the job, which gives the starts back
            var holdsStart = HoldsAnyStart(context);

            while (true)
            {
                if (TryRecordStart(context, key, sharedStart, out var slotFreesAt))
                {
                    return;
                }

                switch (Action)
                {
                    case ConcurrencyAction.Skip:
                        ConcurrencyControl.Skip(context, $"{nameof(RateLimitAttribute)}: limit of {Limit} per {PerSeconds}s reached for '{resource}'");
                        return;

                    case ConcurrencyAction.Wait when !holdsStart:
                        var now = ConcurrencyControl.Clock();
                        var remaining = waitUntil - now;

                        if (remaining <= TimeSpan.Zero)
                        {
                            throw new ConcurrencyLimitException(resource, $"Rate limit of {Limit} per {PerSeconds} seconds for resource '{resource}' stayed exhausted for {WaitTimeoutSeconds} seconds.");
                        }

                        // Sleep until the next slot frees up, but never past the wait timeout
                        var delay = slotFreesAt - now;
                        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(250);
                        if (delay > remaining) delay = remaining;

                        ConcurrencyControl.Wait(context, delay);
                        continue;

                    default:
                        ConcurrencyControl.Reschedule(
                            context,
                            slotFreesAt.Add(ConcurrencyControl.GetJitter()),
                            $"Rate limited: {Limit} job(s) per {PerSeconds}s already started for '{resource}'");
                        return;
                }
            }
        }

        /// <summary>
        /// When the job is about to run, remembers the queue it was fetched from, so that a job held by this filter
        /// goes back to that queue even when only its Enqueued state named it (one state read per run, for jobs
        /// without a queue of their own). When a held job is enqueued again, puts it back in that queue.
        /// </summary>
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.CandidateState is EnqueuedState enqueuedState)
            {
                HeldQueue.Restore(context, enqueuedState);
                return;
            }

            HeldQueue.RecordFetchedQueue(context);
        }

        /// <inheritdoc />
        public void OnPerformed(PerformedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var itemKey = GetItemKey(context.BackgroundJob.Job!);

            if (!context.Items.TryGetValue(itemKey, out var value) || !(value is ValueTuple<string, string> start))
            {
                return;
            }

            context.Items.Remove(itemKey);

            // The job didn't start when a later filter canceled the run, or threw (a lock timeout, for example) before
            // the performer was about to call the job method: it gives its start back. A job whose method was called
            // keeps it, whether it succeeded or failed.
            var methodCalled = context.Items.ContainsKey(ConcurrencyControl.MethodInvokedItem);
            if (!context.Canceled && (context.Exception == null || methodCalled)) return;

            context.Items.Remove(GetRecordedItemKey(start.Item1));

            // Only bookkeeping: the run's own outcome stands. A start that couldn't be given back counts against the
            // limit until it leaves the window.
            try
            {
                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    transaction.RemoveFromSet(start.Item1, start.Item2);
                    transaction.Commit();
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException(
                    $"Could not give back the start of background job '{context.BackgroundJob.Id}' in '{start.Item1}'; it counts until it leaves the {PerSeconds}-second window.",
                    ex);
            }
        }

        // Whether this run holds a start that a limit (on any resource or window) recorded
        private static bool HoldsAnyStart(PerformingContext context)
        {
            foreach (var itemKey in context.Items.Keys)
            {
                if (itemKey.StartsWith(RecordedItemPrefix, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private bool TryRecordStart(PerformingContext context, string key, bool sharedStart, out DateTime slotFreesAt)
        {
            var now = ConcurrencyControl.Clock();
            var timestamp = ConcurrencyControl.ToUnixSeconds(now);
            var windowStart = timestamp - PerSeconds;

            using (context.Connection.AcquireDistributedLock($"{key}:lock", LockTimeout))
            {
                if (!TryCountByScore(context, key, windowStart, out var liveCount, out var stale, out var oldestLive))
                {
                    // The whole set: every member is parsed, and the stale ones are all removed
                    ConcurrencyControl.Partition(context.Connection.GetAllItemsFromSet(key), windowStart, ownJobId: null, out var live, out stale);
                    liveCount = live.Count;
                    oldestLive = live.Count > 0 ? live.Min(static x => x.Timestamp) : timestamp;
                }

                // The run's own start, which another limit on this set recorded, is never removed as stale (another
                // filter may have held the run since): it still counts, and it is the one given back if the run stops
                if (sharedStart &&
                    context.Items.TryGetValue(GetRecordedItemKey(key), out var recorded) &&
                    recorded is string ownMember &&
                    stale.Remove(ownMember))
                {
                    liveCount++;
                }

                // A start already recorded by another limit on this set is one of the live ones
                var allowed = sharedStart ? liveCount <= Limit : liveCount < Limit;
                var record = allowed && !sharedStart;

                if (record || stale.Count > 0)
                {
                    using (var transaction = context.Connection.CreateWriteTransaction())
                    {
                        foreach (var member in stale)
                        {
                            transaction.RemoveFromSet(key, member);
                        }

                        if (record)
                        {
                            var member = ConcurrencyControl.CreateMember(timestamp, context.BackgroundJob.Id);
                            transaction.AddToSet(key, member, timestamp);
                            context.Items[GetItemKey(context.BackgroundJob.Job!)] = (key, member);
                            context.Items[GetRecordedItemKey(key)] = member;

                            // Every start in the set is outside the window once the newest one is
                            ConcurrencyControl.GetExpiringTransaction(context.Storage, transaction)?.ExpireSet(key, TimeSpan.FromSeconds(PerSeconds) + ExpirationMargin);
                        }

                        transaction.Commit();
                    }
                }

                slotFreesAt = allowed
                    ? now
                    : ConcurrencyControl.FromUnixSeconds(oldestLive + PerSeconds + 1);

                return allowed;
            }
        }

        // With a storage that reads sets by score, counts the starts in the window without reading them all: a start's
        // score is its timestamp. False when the storage can't, or when there are too many stale starts to remove in
        // one go; the caller then reads the whole set.
        private static bool TryCountByScore(
            PerformingContext context,
            string key,
            long windowStart,
            out int liveCount,
            out List<string> stale,
            out long oldestLive)
        {
            liveCount = 0;
            stale = new List<string>();
            oldestLive = 0;

            var storage = context.Storage;
            if (storage == null
                || !(context.Connection is JobStorageConnection connection)
                || !storage.HasFeature(JobStorageFeatures.ExtendedApi)
                || !storage.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest))
            {
                return false;
            }

            // Scores are whole seconds, so "older than the window" is a score up to windowStart - 1
            if (windowStart > 0)
            {
                stale = connection.GetFirstByLowestScoreFromSet(key, 0, windowStart - 1, StaleBatchSize) ?? new List<string>();
                if (stale.Count >= StaleBatchSize) return false;
            }

            var count = connection.GetSetCount(key) - stale.Count;
            if (count <= 0) return true;

            liveCount = (int)Math.Min(count, Int32.MaxValue);

            // Only needed to know when the next start is allowed, and the set holds only parsable members, unless
            // something else wrote to it: then the whole set is read and cleaned up
            var oldest = connection.GetFirstByLowestScoreFromSet(key, windowStart, MaxScore);
            if (oldest == null || !ConcurrencyControl.TryParseMember(oldest, out oldestLive, out _)) return false;

            return true;
        }

        private string GetKey(string resource) => $"{KeyPrefix}{resource}:{PerSeconds.ToString(CultureInfo.InvariantCulture)}";

        // One per limit: a job may carry several, on one resource with different windows
        private string GetItemKey(Job job) => $"RateLimit:{Resource ?? job.Method.Name}:{PerSeconds.ToString(CultureInfo.InvariantCulture)}";

        // Set while this run holds a start recorded in the set, whichever limit recorded it
        private static string GetRecordedItemKey(string key) => $"{RecordedItemPrefix}{key}";

        private const string RecordedItemPrefix = "X_NS_RateLimitStart:";

        private const string KeyPrefix = "ratelimit:";

        // ratelimit:{resource}:{perSeconds} is the longest key; a window has up to 10 digits
        internal static readonly int MaxResourceLength = ConcurrencyControl.GetMaxResourceLength(KeyPrefix.Length + 1 + 10);
    }
}
