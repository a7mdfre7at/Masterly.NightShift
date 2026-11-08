#nullable enable

using System;
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Limits how many jobs sharing a resource run at the same time across all servers: a distributed
    /// semaphore. With <see cref="MaxConcurrency"/> of 1 it is a mutex that, unlike
    /// <see cref="DisableConcurrentExecutionAttribute"/>, doesn't hold a worker thread while waiting: by default
    /// the job goes back to the schedule and the worker takes the next one.
    /// </summary>
    /// <remarks>
    /// <para>The resource is a template: <c>{0}</c>..<c>{n}</c> are replaced with the job's arguments, so
    /// <c>[Throttle("tenant-{0}", MaxConcurrency = 2)]</c> allows two jobs per tenant. Without a template
    /// the resource is the job's method: the full name of the job's type, the method's name and its parameter types,
    /// lower-cased, such as <c>orders.cleanupjob.execute(system.int32)</c>.</para>
    /// <para>Slots are members of the set <c>throttle:{resource}</c>. A slot is held only while its job is in the
    /// Processing state: when every slot is taken, a slot whose job is in another state or gone (its release failed) is
    /// reclaimed. A slot whose job crashed while processing is reclaimed after <see cref="StaleTimeoutMinutes"/>, or as
    /// soon as the same job runs again.
    /// When the storage supports it, the set expires <see cref="StaleTimeoutMinutes"/> after the last slot was taken.
    /// <see cref="GetSlots"/> lists the slots of a resource and <see cref="Release"/> frees the slots of a job.</para>
    /// <para>With <see cref="ConcurrencyAction.Reschedule"/> the delay doubles every time the same job is rescheduled
    /// again (<see cref="RetryDelaySeconds"/>, twice that, four times that...) up to <see cref="MaxRetryDelaySeconds"/>,
    /// so a long-running holder doesn't bring the waiting jobs back every few seconds. The count is kept in the
    /// <c>ThrottleReschedules</c> job parameter and cleared when the job gets a slot.</para>
    /// </remarks>
    public sealed class ThrottleAttribute : JobFilterAttribute, IServerFilter, IElectStateFilter
    {
        // Ten years: a longer timeout would overflow the expiration date in some storages
        private const int MaxStaleTimeoutMinutes = 10 * 365 * 24 * 60;
        private const string RescheduleCountParameter = "ThrottleReschedules";

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(ThrottleAttribute));
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

        // The pauses between the attempts to release a slot, after a storage error
        private static readonly TimeSpan[] ReleaseRetryDelays = { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500) };

        private int _retryDelaySeconds;
        private int _maxRetryDelaySeconds;
        private int _waitTimeoutSeconds;
        private int _staleTimeoutMinutes;

        /// <summary>
        /// Initializes the attribute with the job's method (full type name, method name and parameter types) as the
        /// resource.
        /// </summary>
        public ThrottleAttribute()
            : this(null)
        {
        }

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="resource">Resource template, see the class remarks. <c>null</c> means the job's method.</param>
        public ThrottleAttribute([CanBeNull] string? resource)
        {
            Resource = resource;
            MaxConcurrency = 1;
            Action = ConcurrencyAction.Reschedule;
            RetryDelaySeconds = 15;
            MaxRetryDelaySeconds = 300;
            WaitTimeoutSeconds = 30;
            StaleTimeoutMinutes = 60;
        }

        /// <summary>Gets the resource template.</summary>
        [CanBeNull]
        public string? Resource { get; }

        /// <summary>Gets or sets how many jobs may hold the resource at once. Default is 1.</summary>
        public int MaxConcurrency { get; set; }

        /// <summary>Gets or sets what happens when every slot is taken. Default is <see cref="ConcurrencyAction.Reschedule"/>.</summary>
        public ConcurrencyAction Action { get; set; }

        /// <summary>
        /// Gets or sets how long a rescheduled job waits before it is tried again the first time. Default is 15 seconds.
        /// The delay doubles on every further reschedule of the same job, up to <see cref="MaxRetryDelaySeconds"/>.
        /// Zero or greater.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int RetryDelaySeconds
        {
            get => _retryDelaySeconds;
            set => _retryDelaySeconds = NotNegative(value, nameof(RetryDelaySeconds));
        }

        /// <summary>
        /// Gets or sets the longest delay of a rescheduled job. Default is 300 seconds (5 minutes). A value not above
        /// <see cref="RetryDelaySeconds"/> turns the doubling off. Zero or greater.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int MaxRetryDelaySeconds
        {
            get => _maxRetryDelaySeconds;
            set => _maxRetryDelaySeconds = NotNegative(value, nameof(MaxRetryDelaySeconds));
        }

        /// <summary>
        /// Gets or sets how long <see cref="ConcurrencyAction.Wait"/> holds the worker before failing the job. Default is
        /// 30 seconds. Zero or greater.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int WaitTimeoutSeconds
        {
            get => _waitTimeoutSeconds;
            set => _waitTimeoutSeconds = NotNegative(value, nameof(WaitTimeoutSeconds));
        }

        /// <summary>
        /// Gets or sets after how long an unreleased slot is reclaimed, even when its job is still in the Processing state
        /// (its server died). A slot whose job left the Processing state is reclaimed sooner, when every slot is taken.
        /// Default is 60 minutes. Set it above your longest job. From 1 minute to 10 years.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative, or more than 10 years.</exception>
        public int StaleTimeoutMinutes
        {
            get => _staleTimeoutMinutes;
            set
            {
                if (value <= 0 || value > MaxStaleTimeoutMinutes)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value,$"StaleTimeoutMinutes should be between 1 and {MaxStaleTimeoutMinutes} (10 years).");
                }

                _staleTimeoutMinutes = value;
            }
        }

        /// <inheritdoc />
        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (MaxConcurrency <= 0) throw new InvalidOperationException("MaxConcurrency should be greater than zero.");

            var resource = ConcurrencyControl.FormatResourceKey(Resource, context.BackgroundJob.Job!, MaxResourceLength);
            var key = GetKey(resource);
            var waitUntil = ConcurrencyControl.Clock().Add(TimeSpan.FromSeconds(WaitTimeoutSeconds));
            var attempt = 0;

            while (true)
            {
                if (TryAcquire(context, key, out var inUse))
                {
                    ResetRescheduleCount(context);
                    return;
                }

                switch (Action)
                {
                    case ConcurrencyAction.Skip:
                        ConcurrencyControl.Skip(context, $"{nameof(ThrottleAttribute)}: all {MaxConcurrency} slot(s) of '{resource}' are in use");
                        return;

                    case ConcurrencyAction.Wait:
                        var remaining = waitUntil - ConcurrencyControl.Clock();
                        if (remaining <= TimeSpan.Zero)
                        {
                            throw new ConcurrencyLimitException(resource, $"All {MaxConcurrency} slot(s) of resource '{resource}' stayed in use for {WaitTimeoutSeconds} seconds.");
                        }

                        ConcurrencyControl.Wait(context, GetWaitDelay(attempt++, remaining));
                        continue;

                    default:
                        var reschedules = context.GetJobParameter<int>(RescheduleCountParameter, allowStale: true);
                        var enqueueAt = ConcurrencyControl.Clock().Add(GetRetryDelay(reschedules)).Add(ConcurrencyControl.GetJitter());

                        CountReschedule(context, reschedules + 1);
                        ConcurrencyControl.Reschedule(context, enqueueAt, $"Throttled: {inUse} of {MaxConcurrency} slot(s) of '{resource}' in use");
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

            if (!context.Items.TryGetValue(itemKey, out var value) || !(value is ValueTuple<string, string> slot))
            {
                return;
            }

            context.Items.Remove(itemKey);

            // Only bookkeeping: the job's own result stands. A transient storage error is retried a couple of times; a
            // slot still left behind is reclaimed by the next job that finds the resource full once this job is no
            // longer Processing, after StaleTimeoutMinutes in any case, or as soon as the same job runs again, and
            // Release frees it at once.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var transaction = context.Connection.CreateWriteTransaction())
                    {
                        transaction.RemoveFromSet(slot.Item1, slot.Item2);
                        transaction.Commit();
                    }

                    return;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType() && attempt < ReleaseRetryDelays.Length)
                {
                    Logger.DebugException($"Could not release the slot of background job '{context.BackgroundJob.Id}' in '{slot.Item1}', trying again.", ex);
                    context.CancellationToken.ShutdownToken.WaitHandle.WaitOne(ReleaseRetryDelays[attempt]);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    Logger.WarnException(
                        $"Could not release the slot of background job '{context.BackgroundJob.Id}' in '{slot.Item1}'; it is reclaimed once the job is no longer processing and another job finds the resource full, after {StaleTimeoutMinutes} minute(s) at the latest, or when the job runs again.",
                        ex);
                    return;
                }
            }
        }

        /// <summary>
        /// Reads the slots taken on a resource, oldest first, for an operator or a dashboard. A slot older than the
        /// <see cref="StaleTimeoutMinutes"/> of the filter that took it is still listed until a job reclaims it.
        /// </summary>
        /// <param name="connection">Storage connection.</param>
        /// <param name="resource">
        /// The resource as the filter formats it: the template with the job's arguments, such as <c>tenant-42</c> for
        /// <c>[Throttle("tenant-{0}")]</c>, or the job's method, such as <c>orders.cleanupjob.execute(system.int32)</c>.
        /// It is lower-cased and shortened like the filter does, so the formatted and the stored resource both work.
        /// </param>
        /// <returns>The slots, oldest first. Empty when none is taken.</returns>
        [NotNull]
        public static IReadOnlyList<ThrottleSlot> GetSlots([NotNull] IStorageConnection connection, [NotNull] string resource)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            var slots = new List<ThrottleSlot>();
            var members = connection.GetAllItemsFromSet(GetKey(NormalizeResource(resource)));

            if (members != null)
            {
                foreach (var member in members)
                {
                    if (ConcurrencyControl.TryParseMember(member, out var timestamp, out var jobId))
                    {
                        slots.Add(new ThrottleSlot(jobId, ConcurrencyControl.FromUnixSeconds(timestamp)));
                    }
                }
            }

            slots.Sort(static (x, y) => x.AcquiredAt != y.AcquiredAt
                ? x.AcquiredAt.CompareTo(y.AcquiredAt)
                : String.CompareOrdinal(x.JobId, y.JobId));

            return slots;
        }

        /// <summary>
        /// Frees the slots a job holds on a resource, for an operator: when the job is gone or stuck and the waiting
        /// jobs shouldn't wait for <see cref="StaleTimeoutMinutes"/>. A job that is still running keeps running, and
        /// another job can take the freed slot at once.
        /// </summary>
        /// <param name="connection">Storage connection.</param>
        /// <param name="resource">The resource, see <see cref="GetSlots"/>.</param>
        /// <param name="jobId">Identifier of the job whose slots are freed.</param>
        /// <returns><c>true</c> when a slot was freed; <c>false</c> when the job held none.</returns>
        public static bool Release([NotNull] IStorageConnection connection, [NotNull] string resource, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            var key = GetKey(NormalizeResource(resource));

            // The lock the filter takes slots under, so a slot isn't freed while a job is counting them
            using (connection.AcquireDistributedLock($"{key}:lock", LockTimeout))
            {
                var held = new List<string>();
                var members = connection.GetAllItemsFromSet(key);

                if (members != null)
                {
                    foreach (var member in members)
                    {
                        if (ConcurrencyControl.TryParseMember(member, out _, out var holder) && String.Equals(holder, jobId, StringComparison.Ordinal))
                        {
                            held.Add(member);
                        }
                    }
                }

                if (held.Count == 0) return false;

                using (var transaction = connection.CreateWriteTransaction())
                {
                    foreach (var member in held)
                    {
                        transaction.RemoveFromSet(key, member);
                    }

                    transaction.Commit();
                }

                Logger.Info($"Released {held.Count} slot(s) of background job '{jobId}' in '{key}'.");
                return true;
            }
        }

        /// <summary>
        /// The delay before the next attempt of a job already rescheduled <paramref name="reschedules"/> times:
        /// <see cref="RetryDelaySeconds"/>, doubled on every reschedule, up to <see cref="MaxRetryDelaySeconds"/>.
        /// </summary>
        internal TimeSpan GetRetryDelay(int reschedules)
        {
            var cap = Math.Max(RetryDelaySeconds, MaxRetryDelaySeconds);
            var seconds = RetryDelaySeconds * Math.Pow(2, Math.Min(Math.Max(reschedules, 0), 30));

            return TimeSpan.FromSeconds(Math.Min(seconds, cap));
        }

        /// <summary>
        /// How long <see cref="ConcurrencyAction.Wait"/> sleeps before its next try: 250 ms, doubled on every try up to
        /// 4 seconds, and never longer than what is left of <see cref="WaitTimeoutSeconds"/>.
        /// </summary>
        internal static TimeSpan GetWaitDelay(int attempt, TimeSpan remaining)
        {
            var backoff = TimeSpan.FromMilliseconds(Math.Min(250 * (1 << Math.Min(Math.Max(attempt, 0), 4)), 4000));
            return backoff < remaining ? backoff : remaining;
        }

        // The job got its slot: the next time it has to wait, the delay starts small again. Only bookkeeping, so a
        // storage error is logged rather than failing the job, which would also leave its slot taken.
        private static void ResetRescheduleCount(PerformingContext context)
        {
            try
            {
                if (context.GetJobParameter<int>(RescheduleCountParameter, allowStale: true) > 0)
                {
                    context.Connection.SetJobParameter(context.BackgroundJob.Id, RescheduleCountParameter, null);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Could not clear the '{RescheduleCountParameter}' parameter of background job '{context.BackgroundJob.Id}'; its next reschedule may wait longer.", ex);
            }
        }

        // Only bookkeeping for the back-off: a storage error is logged rather than failing a job that never ran, and
        // its next reschedule then waits as long as this one
        private static void CountReschedule(PerformingContext context, int reschedules)
        {
            try
            {
                context.SetJobParameter(RescheduleCountParameter, reschedules);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Could not update the '{RescheduleCountParameter}' parameter of background job '{context.BackgroundJob.Id}'; its next reschedule may come sooner.", ex);
            }
        }

        private bool TryAcquire(PerformingContext context, string key, out int inUse)
        {
            var now = ConcurrencyControl.Clock();
            var timestamp = ConcurrencyControl.ToUnixSeconds(now);
            var staleBefore = timestamp - (long)TimeSpan.FromMinutes(StaleTimeoutMinutes).TotalSeconds;

            using (context.Connection.AcquireDistributedLock($"{key}:lock", LockTimeout))
            {
                var members = context.Connection.GetAllItemsFromSet(key);
                ConcurrencyControl.Partition(members, staleBefore, context.BackgroundJob.Id, out var live, out var stale);

                inUse = live.Count;
                var acquired = live.Count < MaxConcurrency;

                if (!acquired)
                {
                    // A slot is held only while its job is Processing: one whose job finished, moved on or is gone was
                    // left behind (a release that failed), and is reclaimed. Only read when the resource is full
                    var finished = FindFinishedHolders(context.Connection, live);
                    if (finished.Count > 0)
                    {
                        foreach (var member in members)
                        {
                            if (ConcurrencyControl.TryParseMember(member, out var acquiredAt, out var holder) &&
                                acquiredAt >= staleBefore &&
                                finished.Contains(holder) &&
                                !stale.Contains(member))
                            {
                                stale.Add(member);
                            }
                        }

                        inUse = 0;
                        foreach (var slot in live)
                        {
                            if (!finished.Contains(slot.JobId)) inUse++;
                        }

                        acquired = inUse < MaxConcurrency;
                    }
                }

                if (!acquired && stale.Count == 0)
                {
                    return false;
                }

                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    foreach (var member in stale)
                    {
                        Logger.Debug($"Reclaiming stale or own slot '{member}' of throttled resource '{key}'.");
                        transaction.RemoveFromSet(key, member);
                    }

                    if (acquired)
                    {
                        var member = ConcurrencyControl.CreateMember(timestamp, context.BackgroundJob.Id);
                        transaction.AddToSet(key, member, timestamp);
                        context.Items[GetItemKey(context.BackgroundJob.Job!)] = (key, member);

                        // Every slot in the set is stale once the newest one is
                        ConcurrencyControl.GetExpiringTransaction(context.Storage, transaction)?.ExpireSet(key, TimeSpan.FromMinutes(StaleTimeoutMinutes));
                    }

                    transaction.Commit();
                }

                return acquired;
            }
        }

        /// <summary>
        /// Returns the holders of live slots whose job is not in the Processing state, or no longer exists. A holder
        /// whose state can't be read keeps its slot.
        /// </summary>
        private static HashSet<string> FindFinishedHolders(IStorageConnection connection, List<(long Timestamp, string JobId)> live)
        {
            var finished = new HashSet<string>(StringComparer.Ordinal);
            var checkedHolders = new HashSet<string>(StringComparer.Ordinal);

            foreach (var slot in live)
            {
                if (!checkedHolders.Add(slot.JobId)) continue;

                try
                {
                    var state = connection.GetStateData(slot.JobId);
                    if (state == null || !String.Equals(state.Name, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
                    {
                        var where = state == null ? "gone" : $"in the {state.Name} state";
                        Logger.Debug($"Background job '{slot.JobId}' holds a throttle slot but is {where}; reclaiming the slot.");
                        finished.Add(slot.JobId);
                    }
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    Logger.DebugException($"Could not read the state of background job '{slot.JobId}', which holds a throttle slot; it keeps the slot.", ex);
                }
            }

            return finished;
        }

        private static int NotNegative(int value, string name)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(name, value, $"{name} should be zero or greater.");
            return value;
        }

        private const string KeyPrefix = "throttle:";

        // throttle:{resource} is the longest key
        internal static readonly int MaxResourceLength = ConcurrencyControl.GetMaxResourceLength(KeyPrefix.Length);

        private static string GetKey(string resource) => KeyPrefix + resource;

        // The filter lower-cases a formatted resource, then shortens it; doing it again changes nothing
        private static string NormalizeResource(string resource) => ConcurrencyControl.ShortenResource(resource.ToLowerInvariant(), MaxResourceLength);

        private string GetItemKey(Job job) => $"Throttle:{Resource ?? job.Method.Name}";

        /// <summary>A slot taken on a throttled resource, see <see cref="GetSlots"/>.</summary>
        public sealed class ThrottleSlot
        {
            internal ThrottleSlot(string jobId, DateTime acquiredAt)
            {
                JobId = jobId;
                AcquiredAt = acquiredAt;
            }

            /// <summary>Gets the identifier of the job that holds the slot.</summary>
            [NotNull]
            public string JobId { get; }

            /// <summary>Gets when the job took the slot (UTC, in whole seconds).</summary>
            public DateTime AcquiredAt { get; }
        }
    }
}
