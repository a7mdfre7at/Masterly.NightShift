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
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Stops running jobs against a resource that keeps failing. After <see cref="FailureThreshold"/> failures
    /// within <see cref="WindowSeconds"/> the circuit opens: for <see cref="OpenSeconds"/> every job on the
    /// resource is rescheduled instead of run. Then one job is let through (half-open); its success closes the
    /// circuit, its failure opens it again.
    /// </summary>
    /// <remarks>
    /// The resource is a template like in <see cref="ThrottleAttribute"/>; the default is the job's type and
    /// method, which protects one job type. Share a resource between the job types that call the same
    /// dependency to protect the dependency. State lives in hash <c>circuit:{resource}</c>, failures in set
    /// <c>circuit:{resource}:failures</c>, and every resource is listed in set <c>circuits</c> for the dashboard.
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

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(CircuitBreakerAttribute));
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Initializes the attribute with the job's type and method as the resource.
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
        /// <param name="resource">Resource template. <c>null</c> means the job's type and method.</param>
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

        /// <inheritdoc />
        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var resource = ConcurrencyControl.FormatResource(Resource, context.BackgroundJob.Job!);
            var circuit = ReadCircuit(context.Connection, resource);

            if (circuit.State != OpenState)
            {
                return;
            }

            var now = ConcurrencyControl.Clock();
            var halfOpenAt = circuit.OpenedAt.AddSeconds(OpenSeconds);

            if (now < halfOpenAt)
            {
                ConcurrencyControl.Reschedule(
                    context,
                    halfOpenAt.Add(ConcurrencyControl.GetJitter()),
                    $"Circuit '{resource}' is open ({FailureThreshold} failures); retry at {halfOpenAt:HH:mm:ss} UTC");
                return;
            }

            // The first job after the open period probes the dependency
            using (context.Connection.AcquireDistributedLock(GetLockKey(resource), LockTimeout))
            using (var transaction = context.Connection.CreateWriteTransaction())
            {
                Logger.Info($"Circuit '{resource}' is half-open, background job '{context.BackgroundJob.Id}' probes it.");
                WriteState(transaction, resource, HalfOpenState, circuit.OpenedAt);
                transaction.Commit();
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

            var failed = context.CandidateState is FailedState || context.TraversedStates.Any(static x => x is FailedState);
            var succeeded = context.CandidateState is SucceededState;

            if (!failed && !succeeded)
            {
                return;
            }

            var resource = ConcurrencyControl.FormatResource(Resource, context.BackgroundJob.Job);
            var now = ConcurrencyControl.Clock();
            var timestamp = ConcurrencyControl.ToUnixSeconds(now);
            var circuit = ReadCircuit(context.Connection, resource);

            if (succeeded)
            {
                if (circuit.State == HalfOpenState)
                {
                    Logger.Info($"Circuit '{resource}' is closed again after a successful probe.");
                    WriteState(context.Transaction, resource, ClosedState, now);

                    foreach (var member in context.Connection.GetAllItemsFromSet(GetFailuresKey(resource)))
                    {
                        context.Transaction.RemoveFromSet(GetFailuresKey(resource), member);
                    }
                }

                return;
            }

            if (circuit.State == HalfOpenState)
            {
                Logger.Warn($"Circuit '{resource}' probe failed, the circuit is open again for {OpenSeconds} seconds.");
                WriteState(context.Transaction, resource, OpenState, now);
                RaiseOpened(resource, FailureThreshold, context.BackgroundJob.Id);
                return;
            }

            var failuresKey = GetFailuresKey(resource);
            ConcurrencyControl.Partition(context.Connection.GetAllItemsFromSet(failuresKey), timestamp - WindowSeconds, ownJobId: null, out var live, out var stale);

            foreach (var member in stale)
            {
                context.Transaction.RemoveFromSet(failuresKey, member);
            }

            context.Transaction.AddToSet(failuresKey, ConcurrencyControl.CreateMember(timestamp, context.BackgroundJob.Id), timestamp);
            context.Transaction.AddToSet(CircuitsSetKey, resource);

            if (live.Count + 1 >= FailureThreshold && circuit.State != OpenState)
            {
                Logger.Warn($"Circuit '{resource}' is open: {live.Count + 1} failure(s) within {WindowSeconds} seconds. Jobs are rescheduled for {OpenSeconds} seconds.");
                WriteState(context.Transaction, resource, OpenState, now);
                RaiseOpened(resource, live.Count + 1, context.BackgroundJob.Id);
            }
        }

        /// <summary>Reads a circuit's state, for the dashboard.</summary>
        public static CircuitState ReadCircuit([NotNull] IStorageConnection connection, [NotNull] string resource)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            var hash = connection.GetAllEntriesFromHash(GetCircuitKey(resource));

            var state = hash != null && hash.TryGetValue("State", out var stateValue) ? stateValue : ClosedState;
            var openedAt = hash != null && hash.TryGetValue("OpenedAt", out var openedAtValue) && Int64.TryParse(openedAtValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? ConcurrencyControl.FromUnixSeconds(seconds)
                : DateTime.MinValue;

            var failures = connection.GetAllItemsFromSet(GetFailuresKey(resource)).Count;

            return new CircuitState(resource, state, openedAt, failures);
        }

        /// <summary>Closes a circuit and forgets its failures, for the dashboard.</summary>
        public static void Reset([NotNull] IStorageConnection connection, [NotNull] string resource)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            using (connection.AcquireDistributedLock(GetLockKey(resource), LockTimeout))
            using (var transaction = connection.CreateWriteTransaction())
            {
                WriteState(transaction, resource, ClosedState, ConcurrencyControl.Clock());

                foreach (var member in connection.GetAllItemsFromSet(GetFailuresKey(resource)))
                {
                    transaction.RemoveFromSet(GetFailuresKey(resource), member);
                }

                transaction.Commit();
            }
        }

        private void RaiseOpened(string resource, int failures, string jobId)
        {
            if (!Alerts.HasSinks) return;

            Alerts.Raise(new Alert(
                AlertKind.CircuitOpened,
                $"Circuit '{resource}' is open",
                $"{failures} failure(s) within {WindowSeconds} seconds; jobs are held for {OpenSeconds} seconds.")
            {
                Resource = resource,
                JobId = jobId,
            });
        }

        private static void WriteState(IWriteOnlyTransaction transaction, string resource, string state, DateTime openedAt)
        {
            transaction.SetRangeInHash(GetCircuitKey(resource), new[]
            {
                new KeyValuePair<string, string>("State", state),
                new KeyValuePair<string, string>("OpenedAt", ConcurrencyControl.ToUnixSeconds(openedAt).ToString(CultureInfo.InvariantCulture)),
            });
        }

        private static string GetCircuitKey(string resource) => $"circuit:{resource}";
        private static string GetFailuresKey(string resource) => $"circuit:{resource}:failures";
        private static string GetLockKey(string resource) => $"circuit:{resource}:lock";

        /// <summary>A circuit's stored state.</summary>
        public sealed class CircuitState
        {
            internal CircuitState(string resource, string state, DateTime openedAt, int failures)
            {
                Resource = resource;
                State = state;
                OpenedAt = openedAt;
                Failures = failures;
            }

            /// <summary>Gets the resource.</summary>
            public string Resource { get; }

            /// <summary>Gets <see cref="ClosedState"/>, <see cref="OpenState"/> or <see cref="HalfOpenState"/>.</summary>
            public string State { get; }

            /// <summary>Gets when the circuit last changed state (UTC).</summary>
            public DateTime OpenedAt { get; }

            /// <summary>Gets the number of recorded failures.</summary>
            public int Failures { get; }
        }
    }
}
