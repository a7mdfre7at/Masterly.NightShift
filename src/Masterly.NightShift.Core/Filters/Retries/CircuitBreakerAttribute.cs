#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Stops running jobs against a resource that keeps failing. After <see cref="FailureThreshold"/> failures
    /// within <see cref="WindowSeconds"/> the circuit opens: for <see cref="OpenSeconds"/> every job on the
    /// resource is rescheduled instead of run. Then a single job is let through as the probe (half-open); its
    /// success closes the circuit, its failure opens it again. While the probe runs, the other jobs on the
    /// resource are rescheduled for another <see cref="OpenSeconds"/>.
    /// </summary>
    /// <remarks>
    /// <para>The resource is a template like in <see cref="ThrottleAttribute"/>; the default is the job's method (the
    /// full name of the job's type, the method's name and its parameter types), which protects that one method. Share
    /// a resource between the jobs that call the same dependency to protect the dependency. State lives in hash
    /// <c>circuit:{resource}</c>, failures in set <c>circuit:{resource}:failures</c>, and every resource with recent
    /// failures, or that is open, is listed in set <c>circuits</c> for the dashboard.</para>
    /// <para>A failure counts when the job moves to the Failed state, including a failure that
    /// <see cref="AutomaticRetryAttribute"/> turns into a retry, unless its exception is excluded by
    /// <see cref="ExceptOn"/> (by default the ones that mean the dependency was never called: a
    /// <see cref="ConcurrencyLimitException"/>, a <see cref="DistributedLockTimeoutException"/> or a
    /// <see cref="RetryLaterException"/>) or not listed in a non-empty <see cref="OnlyOn"/>. A failure that doesn't
    /// count doesn't decide a probe either.</para>
    /// <para>Failures are counted, and the circuit opened, under the distributed lock <c>circuit:{resource}:lock</c>,
    /// so concurrent failures are all counted and open the circuit once. The probe is chosen under the same lock, so
    /// only one job becomes the probe even when several servers pick up jobs at the same moment. When the probe stops
    /// running without deciding (it was deleted, or another filter rescheduled it), the next job takes its place.</para>
    /// <para>A closed circuit leaves the <c>circuits</c> set, and its hash is removed, at the first successful run that
    /// comes when it has had no failure for <see cref="WindowSeconds"/> and was closed at least
    /// <see cref="WindowSeconds"/> and <see cref="OpenSeconds"/> ago. When the storage supports it, the failure set
    /// expires with the window, and the hash of a closed circuit a day after that period.</para>
    /// </remarks>
    public sealed class CircuitBreakerAttribute : JobFilterAttribute, IServerFilter, IElectStateFilter
    {
        /// <summary>Set listing every circuit breaker resource.</summary>
        public const string CircuitsSetKey = "circuits";

        /// <summary>Circuit states, stored in the <c>State</c> field of the circuit hash.</summary>
        public const string ClosedState = "Closed";

        /// <inheritdoc cref="ClosedState" />
        public const string OpenState = "Open";

        /// <inheritdoc cref="ClosedState" />
        public const string HalfOpenState = "HalfOpen";

        private const string StateField = "State";
        private const string OpenedAtField = "OpenedAt";
        private const string OpenUntilField = "OpenUntil";
        private const string ProbeField = "Probe";
        private const string WindowField = "WindowSeconds";

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(CircuitBreakerAttribute));
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ExpirationMargin = TimeSpan.FromMinutes(1);

        // A closed circuit's hash outlives its retention by a day, so that a successful run can still remove it from
        // the circuits set before the hash expires.
        private static readonly TimeSpan ClosedHashMargin = TimeSpan.FromDays(1);

        // Failures already counted, per state object given to the state change. The worker retries a state change
        // whose commit failed with the same object, and the failure was committed on its own the first time, so the
        // retry must not count it again. Keyed by object so that it lives only as long as the state change does.
        private static readonly ConditionalWeakTable<IState, HashSet<string>> CountedFailures = new ConditionalWeakTable<IState, HashSet<string>>();

        /// <summary>
        /// Initializes the attribute with the job's method (full type name, method name and parameter types) as the
        /// resource.
        /// </summary>
        /// <param name="failureThreshold">Failures within the window that open the circuit.</param>
        /// <param name="windowSeconds">Window in which failures are counted.</param>
        /// <param name="openSeconds">How long the circuit stays open before a job is let through.</param>
        public CircuitBreakerAttribute(int failureThreshold, int windowSeconds, int openSeconds)
            : this(null, failureThreshold, windowSeconds, openSeconds)
        {
        }

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="resource">Resource template. <c>null</c> means the job's method (full type name, method name and parameter types).</param>
        /// <param name="failureThreshold">Failures within the window that open the circuit.</param>
        /// <param name="windowSeconds">Window in which failures are counted.</param>
        /// <param name="openSeconds">How long the circuit stays open before a job is let through.</param>
        public CircuitBreakerAttribute([CanBeNull] string? resource, int failureThreshold, int windowSeconds, int openSeconds)
        {
            if (failureThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
            if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
            if (openSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(openSeconds));

            Resource = resource;
            FailureThreshold = failureThreshold;
            WindowSeconds = windowSeconds;
            OpenSeconds = openSeconds;
            OnlyOn = Type.EmptyTypes;
            ExceptOn = new[] { typeof(ConcurrencyLimitException), typeof(DistributedLockTimeoutException), typeof(RetryLaterException) };
        }

        /// <summary>Gets the resource template.</summary>
        [CanBeNull]
        public string? Resource { get; }

        /// <summary>Gets the number of failures within the window that open the circuit.</summary>
        public int FailureThreshold { get; }

        /// <summary>Gets the failure counting window, in seconds.</summary>
        public int WindowSeconds { get; }

        /// <summary>Gets how long the circuit stays open, in seconds.</summary>
        public int OpenSeconds { get; }

        /// <summary>
        /// Gets or sets the exception types (including subtypes) whose failures count, for example
        /// <c>new[] { typeof(HttpRequestException), typeof(TimeoutException) }</c> to count only failures to reach the
        /// dependency. Empty (the default) means every exception that <see cref="ExceptOn"/> doesn't exclude.
        /// </summary>
        [NotNull]
        public Type[] OnlyOn { get; set; }

        /// <summary>
        /// Gets or sets the exception types (including subtypes) whose failures never count, for any job on the
        /// resource and for the probe. Default: <see cref="ConcurrencyLimitException"/> (a <see cref="ThrottleAttribute"/>
        /// or <see cref="RateLimitAttribute"/> wait that timed out), <see cref="DistributedLockTimeoutException"/> (a lock
        /// that wasn't granted, such as the one of <see cref="DisableConcurrentExecutionAttribute"/>) and
        /// <see cref="RetryLaterException"/> (the job asked to run later): in all of them the dependency was never
        /// called. Set it to an empty array to count them too. Checked after <see cref="OnlyOn"/>.
        /// </summary>
        [NotNull]
        public Type[] ExceptOn { get; set; }

        /// <inheritdoc />
        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var resource = ConcurrencyControl.FormatResourceKey(Resource, context.BackgroundJob.Job!, MaxResourceLength);
            var jobId = context.BackgroundJob.Id;
            var circuit = ReadCircuit(context.Connection, resource);

            // Lock-free fast paths: a closed circuit, the probe itself running again (after a crash, for example),
            // and an open period that hasn't ended yet.
            if (circuit.State != OpenState && circuit.State != HalfOpenState) return;
            if (circuit.State == HalfOpenState && jobId.Equals(circuit.ProbeJobId, StringComparison.Ordinal)) return;

            var holdUntil = GetOpenPeriodEnd(circuit, ConcurrencyControl.Clock(), out var reason);

            if (holdUntil == null)
            {
                // The state may have changed since it was read: decide on fresh data under the lock, so that only
                // one job becomes the probe.
                using (context.Connection.AcquireDistributedLock(GetLockKey(resource), LockTimeout))
                {
                    circuit = ReadCircuit(context.Connection, resource);
                    holdUntil = GetHoldTime(context.Connection, circuit, jobId, ConcurrencyControl.Clock(), out reason);

                    if (holdUntil == null && circuit.State == HalfOpenState && circuit.ProbeJobId != null && NeedsProbe(circuit, jobId))
                    {
                        // The probe doesn't take this lock: it commits its final state together with its verdict on
                        // the circuit. GetHoldTime read the probe's state after the circuit, so read the circuit
                        // again before taking over, or a verdict committed in between would be overwritten.
                        var current = ReadCircuit(context.Connection, resource);
                        if (current.State != HalfOpenState || !String.Equals(current.ProbeJobId, circuit.ProbeJobId, StringComparison.Ordinal))
                        {
                            circuit = current;
                            holdUntil = GetHoldTime(context.Connection, circuit, jobId, ConcurrencyControl.Clock(), out reason);
                        }
                    }

                    if (holdUntil == null && NeedsProbe(circuit, jobId))
                    {
                        Logger.Info(circuit.ProbeJobId == null
                            ? $"Circuit '{resource}' is half-open, background job '{jobId}' probes it."
                            : $"Circuit '{resource}' is half-open, background job '{jobId}' probes it instead of '{circuit.ProbeJobId}', which is no longer running.");

                        // Writing the same values again is harmless, so a transient storage error is retried
                        ConcurrencyControl.WithRetries(context, $"record background job '{jobId}' as the probe of circuit '{resource}'", () =>
                        {
                            using (var transaction = context.Connection.CreateWriteTransaction())
                            {
                                WriteState(transaction, context.Storage, resource, HalfOpenState, ConcurrencyControl.Clock(), jobId);
                                transaction.Commit();
                            }
                        });
                    }
                }
            }

            if (holdUntil != null)
            {
                ConcurrencyControl.Reschedule(context, holdUntil.Value.Add(ConcurrencyControl.GetJitter()), reason!);
            }
        }

        /// <inheritdoc />
        public void OnPerformed(PerformedContext context)
        {
        }

        /// <inheritdoc />
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.BackgroundJob.Job == null) return;

            if (context.CandidateState is EnqueuedState enqueuedState)
            {
                // A job this filter held is enqueued again: back to the queue it was held from
                HeldQueue.Restore(context, enqueuedState);
                return;
            }

            if (context.CandidateState is ProcessingState)
            {
                // A job held while it runs comes back to the default queue: remember the queue it is fetched from
                HeldQueue.RecordFetchedQueue(context);
                return;
            }

            var failure = context.CandidateState as FailedState ?? context.TraversedStates.OfType<FailedState>().LastOrDefault();
            var succeeded = context.CandidateState is SucceededState;

            if (failure == null && !succeeded)
            {
                return;
            }

            if (!succeeded && failure != null && !Counts(failure.Exception))
            {
                // The dependency was never called (a lock or slot that wasn't granted, a request to run later):
                // neither counted nor deciding a probe
                Logger.Debug($"Failure of background job '{context.BackgroundJob.Id}' with {failure.Exception?.GetType().Name} is not counted by its circuit breaker.");
                return;
            }

            var resource = ConcurrencyControl.FormatResourceKey(Resource, context.BackgroundJob.Job, MaxResourceLength);
            var now = ConcurrencyControl.Clock();
            var circuit = ReadCircuit(context.Connection, resource, out var stored);

            // Only the probe decides a half-open circuit. A circuit written before probes were recorded has none,
            // and then any job decides. The probe commits its verdict together with its own final state.
            var isProbe = circuit.State == HalfOpenState
                && (circuit.ProbeJobId == null || circuit.ProbeJobId.Equals(context.BackgroundJob.Id, StringComparison.Ordinal));

            if (succeeded)
            {
                if (isProbe)
                {
                    Logger.Info($"Circuit '{resource}' is closed again after a successful probe.");
                    WriteState(context.Transaction, context.Storage, resource, ClosedState, now);

                    foreach (var member in context.Connection.GetAllItemsFromSet(GetFailuresKey(resource)))
                    {
                        context.Transaction.RemoveFromSet(GetFailuresKey(resource), member);
                    }
                }
                else if (stored && IsQuiet(circuit, now))
                {
                    TryForget(context, resource, now);
                }

                return;
            }

            if (isProbe)
            {
                Logger.Warn($"Circuit '{resource}' probe failed, the circuit is open again for {OpenSeconds} seconds.");
                WriteState(context.Transaction, context.Storage, resource, OpenState, now);
                context.Transaction.AddToSet(CircuitsSetKey, resource);

                var alert = CreateOpenedAlert(context, resource, FailureThreshold);
                if (alert != null)
                {
                    // Sent once the state change is stored, so a retried state change doesn't alert twice
                    context.RunAfterCommit(() => Alerts.Raise(alert));
                }

                return;
            }

            RecordFailure(context, resource, now);
        }

        // Whether a failure with this exception counts, see OnlyOn and ExceptOn.
        private bool Counts(Exception? exception)
        {
            if (exception == null) return true;

            var exceptionType = exception.GetType();
            var onlyOn = OnlyOn;
            var exceptOn = ExceptOn;

            if (onlyOn != null && onlyOn.Length > 0 && !Array.Exists(onlyOn, x => x != null && x.IsAssignableFrom(exceptionType)))
            {
                return false;
            }

            return exceptOn == null || !Array.Exists(exceptOn, x => x != null && x.IsAssignableFrom(exceptionType));
        }

        // The state the state change started with, before election filters (AutomaticRetry, for example) replaced it.
        private static IState GetRequestedState(ElectStateContext context)
        {
            var traversed = context.TraversedStates;
            return traversed.Length > 0 ? traversed[0] : context.CandidateState;
        }

        private static string GetCountedFailureKey(string resource, string jobId) => $"{jobId}\n{resource}";

        private static bool IsFailureCounted(IState requestedState, string key)
        {
            if (!CountedFailures.TryGetValue(requestedState, out var keys)) return false;

            lock (keys)
            {
                return keys.Contains(key);
            }
        }

        private static void MarkFailureCounted(IState requestedState, string key)
        {
            var keys = CountedFailures.GetValue(requestedState, static _ => new HashSet<string>(StringComparer.Ordinal));

            lock (keys)
            {
                keys.Add(key);
            }
        }

        /// <summary>Reads a circuit's state, for the dashboard.</summary>
        public static CircuitState ReadCircuit([NotNull] IStorageConnection connection, [NotNull] string resource)
        {
            return ReadCircuit(connection, resource, out _);
        }

        /// <summary>
        /// Closes a circuit and forgets its failures, for the dashboard. The circuit leaves the <see cref="CircuitsSetKey"/>
        /// set until it fails again.
        /// </summary>
        public static void Reset([NotNull] IStorageConnection connection, [NotNull] string resource)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            using (connection.AcquireDistributedLock(GetLockKey(resource), LockTimeout))
            using (var transaction = connection.CreateWriteTransaction())
            {
                // The closed hash is removed by the first successful run once the circuit is quiet
                transaction.SetRangeInHash(GetCircuitKey(resource), new[]
                {
                    new KeyValuePair<string, string>(StateField, ClosedState),
                    new KeyValuePair<string, string>(OpenedAtField, ConcurrencyControl.ToUnixSeconds(ConcurrencyControl.Clock()).ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>(OpenUntilField, String.Empty),
                    new KeyValuePair<string, string>(ProbeField, String.Empty),
                });
                transaction.RemoveFromSet(CircuitsSetKey, resource);

                foreach (var member in connection.GetAllItemsFromSet(GetFailuresKey(resource)))
                {
                    transaction.RemoveFromSet(GetFailuresKey(resource), member);
                }

                transaction.Commit();
            }
        }

        private static CircuitState ReadCircuit(IStorageConnection connection, string resource, out bool stored)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            var hash = connection.GetAllEntriesFromHash(GetCircuitKey(resource));
            stored = hash != null && hash.Count > 0;

            var state = hash != null && hash.TryGetValue(StateField, out var stateValue) && !String.IsNullOrEmpty(stateValue) ? stateValue : ClosedState;
            var openedAt = ReadTimestamp(hash, OpenedAtField) ?? DateTime.MinValue;
            var openUntil = state == OpenState ? ReadTimestamp(hash, OpenUntilField) : null;

            var probeJobId = state == HalfOpenState && hash != null && hash.TryGetValue(ProbeField, out var probeValue) && !String.IsNullOrEmpty(probeValue)
                ? probeValue
                : null;

            // Only the failures within the window count. A circuit written before the window was stored counts them all.
            var members = connection.GetAllItemsFromSet(GetFailuresKey(resource));
            var failures = members.Count;

            if (hash != null && hash.TryGetValue(WindowField, out var windowValue)
                && Int32.TryParse(windowValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowSeconds))
            {
                var windowStart = ConcurrencyControl.ToUnixSeconds(ConcurrencyControl.Clock()) - windowSeconds;
                ConcurrencyControl.Partition(members, windowStart, ownJobId: null, out var live, out _);
                failures = live.Count;
            }

            return new CircuitState(resource, state, openedAt, failures, probeJobId, openUntil);
        }

        private static string ReadState(IStorageConnection connection, string resource)
        {
            var hash = connection.GetAllEntriesFromHash(GetCircuitKey(resource));
            return hash != null && hash.TryGetValue(StateField, out var state) && !String.IsNullOrEmpty(state) ? state : ClosedState;
        }

        private static DateTime? ReadTimestamp(Dictionary<string, string>? hash, string field)
        {
            return hash != null && hash.TryGetValue(field, out var value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? ConcurrencyControl.FromUnixSeconds(seconds)
                : null;
        }

        // Counts a failure of a job that isn't the probe, and opens the circuit when the threshold is reached. Done under
        // the circuit lock and committed at once, so that concurrent failures are all counted and open the circuit once;
        // the failure is recorded even when the job's own state change fails, and a retry of it doesn't count it again.
        private void RecordFailure(ElectStateContext context, string resource, DateTime now)
        {
            var requestedState = GetRequestedState(context);
            var countedKey = GetCountedFailureKey(resource, context.BackgroundJob.Id);

            if (IsFailureCounted(requestedState, countedKey))
            {
                Logger.Debug($"Failure of background job '{context.BackgroundJob.Id}' is already counted for circuit '{resource}', the state change is being retried.");
                return;
            }

            var timestamp = ConcurrencyControl.ToUnixSeconds(now);
            var failuresKey = GetFailuresKey(resource);
            var opened = 0;

            using (context.Connection.AcquireDistributedLock(GetLockKey(resource), LockTimeout))
            {
                var state = ReadState(context.Connection, resource);
                ConcurrencyControl.Partition(context.Connection.GetAllItemsFromSet(failuresKey), timestamp - WindowSeconds, ownJobId: null, out var live, out var stale);

                var opens = live.Count + 1 >= FailureThreshold && state != OpenState && state != HalfOpenState;

                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    foreach (var member in stale)
                    {
                        transaction.RemoveFromSet(failuresKey, member);
                    }

                    transaction.AddToSet(failuresKey, ConcurrencyControl.CreateMember(timestamp, context.BackgroundJob.Id), timestamp);
                    transaction.AddToSet(CircuitsSetKey, resource);
                    transaction.SetRangeInHash(GetCircuitKey(resource), new[]
                    {
                        new KeyValuePair<string, string>(WindowField, WindowSeconds.ToString(CultureInfo.InvariantCulture)),
                    });

                    var expiring = ConcurrencyControl.GetExpiringTransaction(context.Storage, transaction);
                    expiring?.ExpireSet(failuresKey, TimeSpan.FromSeconds(WindowSeconds) + ExpirationMargin);

                    if (opens)
                    {
                        Logger.Warn($"Circuit '{resource}' is open: {live.Count + 1} failure(s) within {WindowSeconds} seconds. Jobs are rescheduled for {OpenSeconds} seconds.");
                        WriteState(transaction, context.Storage, resource, OpenState, now);
                        opened = live.Count + 1;
                    }
                    else
                    {
                        SetHashExpiration(expiring, resource, state);
                    }

                    transaction.Commit();
                }
            }

            MarkFailureCounted(requestedState, countedKey);

            if (opened > 0)
            {
                // Already stored, so raised even when the job's own state change fails: a retry of it doesn't count
                // the failure again, and doesn't alert. Raised once the job's lock is released, so a slow alert sink
                // doesn't hold up the state change.
                var alert = CreateOpenedAlert(context, resource, opened);
                if (alert != null) context.RunAfterCommitAttempt(() => Alerts.Raise(alert));
            }
        }

        // A closed circuit with no failure in the window, closed long enough ago to have been seen on the dashboard.
        private bool IsQuiet(CircuitState circuit, DateTime now)
        {
            return circuit.State == ClosedState
                && circuit.Failures == 0
                && now - circuit.OpenedAt >= GetRetention();
        }

        // Only housekeeping: a storage error must not fail the state change of a job that succeeded. The circuit stays
        // listed, and the next successful run tries again.
        private void TryForget(ElectStateContext context, string resource, DateTime now)
        {
            try
            {
                Forget(context, resource, now);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Could not remove the quiet closed circuit '{resource}'; a later successful run removes it.", ex);
            }
        }

        // Removes a quiet closed circuit from the circuits set, with its hash and stale failures.
        private void Forget(ElectStateContext context, string resource, DateTime now)
        {
            var failuresKey = GetFailuresKey(resource);
            var windowStart = ConcurrencyControl.ToUnixSeconds(now) - WindowSeconds;

            using (context.Connection.AcquireDistributedLock(GetLockKey(resource), LockTimeout))
            {
                var circuit = ReadCircuit(context.Connection, resource, out var stored);
                ConcurrencyControl.Partition(context.Connection.GetAllItemsFromSet(failuresKey), windowStart, ownJobId: null, out var live, out var stale);

                if (!stored || live.Count > 0 || circuit.State != ClosedState || now - circuit.OpenedAt < GetRetention()) return;

                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    foreach (var member in stale)
                    {
                        transaction.RemoveFromSet(failuresKey, member);
                    }

                    transaction.RemoveFromSet(CircuitsSetKey, resource);
                    transaction.RemoveHash(GetCircuitKey(resource));
                    transaction.Commit();
                }
            }
        }

        // How long a closed circuit stays listed: the failure window or the open period, whichever is longer.
        private TimeSpan GetRetention() => TimeSpan.FromSeconds(Math.Max(WindowSeconds, OpenSeconds));

        private Alert? CreateOpenedAlert(ElectStateContext context, string resource, int failures)
        {
            if (!Alerts.HasSinks) return null;

            return new Alert(
                AlertKind.CircuitOpened,
                $"Circuit '{resource}' is open",
                $"{failures} failure(s) within {WindowSeconds} seconds; jobs are held for {OpenSeconds} seconds.")
            {
                Resource = resource,
                JobId = context.BackgroundJob.Id,
            };
        }

        // When the open period is not over yet, the time it ends; otherwise null.
        private DateTime? GetOpenPeriodEnd(CircuitState circuit, DateTime now, out string? reason)
        {
            var halfOpenAt = circuit.OpenedAt.AddSeconds(OpenSeconds);

            if (circuit.State == OpenState && now < halfOpenAt)
            {
                reason = $"Circuit '{circuit.Resource}' is open ({FailureThreshold} failures); retry at {halfOpenAt:HH:mm:ss} UTC";
                return halfOpenAt;
            }

            reason = null;
            return null;
        }

        // When the job has to wait instead of running: during the open period, or while another job probes the
        // half-open circuit. Null when the job may run.
        private DateTime? GetHoldTime(IStorageConnection connection, CircuitState circuit, string jobId, DateTime now, out string? reason)
        {
            if (circuit.State == OpenState)
            {
                return GetOpenPeriodEnd(circuit, now, out reason);
            }

            if (circuit.State == HalfOpenState
                && circuit.ProbeJobId != null
                && !jobId.Equals(circuit.ProbeJobId, StringComparison.Ordinal)
                && IsProcessing(connection, circuit.ProbeJobId))
            {
                // Shorter than the open period, so the held jobs don't all wait that long again once the probe
                // succeeds, and long enough not to reschedule them every few seconds while a slow probe runs
                var retryAt = now.AddSeconds(ProbeHoldSeconds);
                reason = $"Circuit '{circuit.Resource}' is half-open and job '{circuit.ProbeJobId}' is probing it; retry at {retryAt:HH:mm:ss} UTC";
                return retryAt;
            }

            reason = null;
            return null;
        }

        // How long other jobs wait while the probe runs: a tenth of the open period, between 5 seconds and the
        // open period itself.
        private int ProbeHoldSeconds => Math.Min(OpenSeconds, Math.Max(5, OpenSeconds / 10));

        // A job allowed to run on an open or half-open circuit becomes its probe, unless it already is.
        private static bool NeedsProbe(CircuitState circuit, string jobId)
        {
            return circuit.State == OpenState
                || (circuit.State == HalfOpenState && !jobId.Equals(circuit.ProbeJobId, StringComparison.Ordinal));
        }

        private static bool IsProcessing(IStorageConnection connection, string jobId)
        {
            var state = connection.GetStateData(jobId);
            return state != null && ProcessingState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase);
        }

        private void WriteState(IWriteOnlyTransaction transaction, JobStorage? storage, string resource, string state, DateTime changedAt, string? probeJobId = null)
        {
            transaction.SetRangeInHash(GetCircuitKey(resource), new[]
            {
                new KeyValuePair<string, string>(StateField, state),
                new KeyValuePair<string, string>(OpenedAtField, ConcurrencyControl.ToUnixSeconds(changedAt).ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(OpenUntilField, state == OpenState
                    ? ConcurrencyControl.ToUnixSeconds(changedAt.AddSeconds(OpenSeconds)).ToString(CultureInfo.InvariantCulture)
                    : String.Empty),
                new KeyValuePair<string, string>(ProbeField, probeJobId ?? String.Empty),
                new KeyValuePair<string, string>(WindowField, WindowSeconds.ToString(CultureInfo.InvariantCulture)),
            });

            SetHashExpiration(ConcurrencyControl.GetExpiringTransaction(storage, transaction), resource, state);
        }

        // An open or half-open circuit is kept; a closed one expires a day after its retention.
        private void SetHashExpiration(JobStorageTransaction? transaction, string resource, string state)
        {
            if (transaction == null) return;

            if (state == ClosedState)
            {
                transaction.ExpireHash(GetCircuitKey(resource), GetRetention() + ClosedHashMargin);
            }
            else
            {
                transaction.PersistHash(GetCircuitKey(resource));
            }
        }

        // circuit:{resource}:failures is the longest key
        internal static readonly int MaxResourceLength = ConcurrencyControl.GetMaxResourceLength("circuit:".Length + ":failures".Length);

        private static string GetCircuitKey(string resource) => $"circuit:{resource}";
        private static string GetFailuresKey(string resource) => $"circuit:{resource}:failures";
        private static string GetLockKey(string resource) => $"circuit:{resource}:lock";

        /// <summary>A circuit's stored state.</summary>
        public sealed class CircuitState
        {
            internal CircuitState(string resource, string state, DateTime openedAt, int failures, string? probeJobId, DateTime? openUntil = null)
            {
                Resource = resource;
                State = state;
                OpenedAt = openedAt;
                Failures = failures;
                ProbeJobId = probeJobId;
                OpenUntil = openUntil;
            }

            /// <summary>Gets the resource.</summary>
            public string Resource { get; }

            /// <summary>Gets <see cref="ClosedState"/>, <see cref="OpenState"/> or <see cref="HalfOpenState"/>.</summary>
            public string State { get; }

            /// <summary>Gets when the circuit last changed state (UTC).</summary>
            public DateTime OpenedAt { get; }

            /// <summary>Gets the number of recorded failures within the circuit's failure window.</summary>
            public int Failures { get; }

            /// <summary>
            /// Gets the identifier of the job that probes a half-open circuit, or <c>null</c> when the circuit
            /// isn't half-open or no probe is recorded.
            /// </summary>
            [CanBeNull]
            public string? ProbeJobId { get; }

            /// <summary>
            /// Gets when the open period of an open circuit ends (UTC): the next job after that becomes the probe. The
            /// circuit stays <see cref="OpenState"/> in storage until a job comes. <c>null</c> when the circuit isn't
            /// open, or was opened by a version that didn't store it.
            /// </summary>
            [CanBeNull]
            public DateTime? OpenUntil { get; }
        }
    }
}
