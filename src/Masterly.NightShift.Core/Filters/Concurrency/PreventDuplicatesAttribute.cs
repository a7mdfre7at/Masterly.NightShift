#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>What happens when a job with the same fingerprint is still pending.</summary>
    public enum DuplicateHandling
    {
        /// <summary>The new job is not created; <c>Enqueue</c> and friends return <c>null</c>.</summary>
        Cancel,

        /// <summary>
        /// The pending job is deleted and the new one takes its place. Only a job that hasn't started yet (Enqueued,
        /// Scheduled or Awaiting) is replaced; while the pending job runs, or is still being created, the new job is
        /// not created, as with <see cref="Cancel"/>. The pending job is deleted after the new one is created, and
        /// only if it still hasn't started: when a worker picks it up in between, or deleting it fails (the error is
        /// logged), it is left alone and both jobs exist.
        /// </summary>
        ReplaceExisting,
    }

    /// <summary>
    /// Refuses a second copy of a job while the first one is still pending. The fingerprint is a hash of the method
    /// (the full name of the job's type, the method's name and its parameter types) and its arguments, or the
    /// <see cref="Template"/> (<c>{0}</c>..<c>{n}</c> are the arguments) when one is given. Works at creation time, so
    /// duplicates never reach the queue.
    /// </summary>
    /// <remarks>
    /// The fingerprint is kept in the <c>fingerprint:{hash}</c> storage hash and released when the owning job
    /// reaches a final state, or the Failed state (see <see cref="ReleaseOnFailure"/>). The check reads the
    /// owner's current state, so a fingerprint can never block after its job has finished, even when the release
    /// was lost. New jobs carry the <c>Fingerprint</c> parameter. A <see cref="RedactedAttribute"/> argument used
    /// in the <see cref="Template"/> is replaced with a hash of its value, and a templated fingerprint longer than 88
    /// characters (the longest that fits the 100-character key <c>fingerprint:{fingerprint}</c>) is shortened to its
    /// start and a hash of the whole text, so that the storage key fits.
    /// </remarks>
    public sealed class PreventDuplicatesAttribute : JobFilterAttribute, IClientFilter, IApplyStateFilter
    {
        /// <summary>The job parameter holding the fingerprint.</summary>
        public const string FingerprintParameter = "Fingerprint";

        private const string ReservationItem = "X_HF_FingerprintReservation";
        private const string KeyPrefix = "fingerprint:";

        // The longest templated fingerprint whose key fits the storage
        internal static readonly int MaxFingerprintLength = ConcurrencyControl.GetMaxResourceLength(KeyPrefix.Length);
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReservationTimeout = TimeSpan.FromMinutes(2);
        private static readonly ILog Logger = LogProvider.For<PreventDuplicatesAttribute>();

        // States of a job that hasn't started yet, which ReplaceExisting may delete
        private static readonly string[] ReplaceableStates = { EnqueuedState.StateName, ScheduledState.StateName, AwaitingState.StateName };

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>Fingerprints the method and all its arguments.</summary>
        public PreventDuplicatesAttribute()
            : this(null)
        {
        }

        /// <summary>Fingerprints with a template, for example <c>"import:{0}"</c> to key on the first argument only.</summary>
        public PreventDuplicatesAttribute([CanBeNull] string? template)
        {
            Template = template;
            Order = 10;
        }

        /// <summary>
        /// The fingerprint template, or <c>null</c> for "method plus every argument": a hash of the full name of the
        /// job's type, the method's name, its parameter types and the serialized arguments.
        /// </summary>
        public string? Template { get; }

        /// <summary>What to do with the new job when a duplicate is pending. Default <see cref="DuplicateHandling.Cancel"/>.</summary>
        public DuplicateHandling OnDuplicate { get; set; } = DuplicateHandling.Cancel;

        /// <summary>
        /// Safety net: the fingerprint record expires after this many hours (default 24) even if its release was lost
        /// and the owner's state can't be read. For a scheduled job the hours count from the time it is scheduled
        /// for, and they start again every time the job is scheduled (a retry, for example). Zero disables the
        /// expiration.
        /// </summary>
        public int ExpirationHours { get; set; } = 24;

        /// <summary>
        /// Whether the fingerprint is released when the job moves to the Failed state, that is when its retries
        /// are used up. Default <c>true</c>: a failed job doesn't stop a new copy from being created. Set it to
        /// <c>false</c> to keep refusing duplicates until the failed job is deleted, or succeeds after a requeue.
        /// </summary>
        public bool ReleaseOnFailure { get; set; } = true;

        /// <summary>
        /// Computes the fingerprint of a job as this attribute sees it. A templated fingerprint longer than 88 characters
        /// keeps its start and ends with <c>~</c> and a hash of the whole text, 88 characters in all.
        /// </summary>
        public string GetFingerprint([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            if (!String.IsNullOrWhiteSpace(Template))
            {
                return ConcurrencyControl.FormatResourceKey(Template, job, MaxFingerprintLength);
            }

            // The full type name and the parameter types, so same-named types in other namespaces, and overloads that
            // get the same serialized arguments, don't share a fingerprint
            var invocation = InvocationData.SerializeJob(job);
            var text = $"{ConcurrencyControl.GetMethodIdentity(job)}:{invocation.Arguments}";

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(32);
                for (var i = 0; i < 16; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        /// <summary>The storage key of a fingerprint record.</summary>
        public static string GetKey([NotNull] string fingerprint)
        {
            if (fingerprint == null) throw new ArgumentNullException(nameof(fingerprint));
            return KeyPrefix + fingerprint;
        }

        /// <summary>Reads the identifier of the job that currently owns a fingerprint, or <c>null</c>.</summary>
        public static string? ReadOwner([NotNull] IStorageConnection connection, [NotNull] string fingerprint)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var hash = connection.GetAllEntriesFromHash(GetKey(fingerprint));
            return hash != null && hash.TryGetValue("JobId", out var jobId) && !String.IsNullOrEmpty(jobId) ? jobId : null;
        }

        /// <inheritdoc />
        public void OnCreating(CreatingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Canceled) return;

            var fingerprint = GetFingerprint(context.Job);
            var key = GetKey(fingerprint);
            var reservation = Guid.NewGuid().ToString("N");
            string? replacedJobId = null;

            using (context.Connection.AcquireDistributedLock(key, LockTimeout))
            {
                var hash = context.Connection.GetAllEntriesFromHash(key);

                if (hash != null && IsPending(context, hash, out var ownerId, out var ownerState))
                {
                    // Only a job that hasn't started yet is replaced: a running one, or one still being created, stays
                    var replace = OnDuplicate == DuplicateHandling.ReplaceExisting && ownerId != null && IsReplaceable(ownerState);

                    if (!replace)
                    {
                        Logger.Debug($"Background job {context.Job.Type.Name}.{context.Job.Method.Name} was not created: a duplicate ({ownerId ?? "being created"}, {ownerState ?? "no state yet"}) is still pending.");
                        context.Canceled = true;
                        return;
                    }

                    replacedJobId = ownerId;
                }

                WriteRecord(context.Storage, context.Connection, key, context.InitialState, new Dictionary<string, string>
                {
                    { "Reservation", reservation },
                    { "ReservedAt", JobHelper.SerializeDateTime(Clock()) },
                    { "JobId", String.Empty },
                });
            }

            context.Items[ReservationItem] = new Reservation(key, reservation, replacedJobId);
            context.SetJobParameter(FingerprintParameter, fingerprint);
        }

        /// <inheritdoc />
        public void OnCreated(CreatedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!context.Items.TryGetValue(ReservationItem, out var value) || value is not Reservation reservation) return;
            context.Items.Remove(ReservationItem);

            if (context.Canceled || context.Exception != null || context.BackgroundJob == null)
            {
                ReleaseReservation(context, reservation);
                return;
            }

            // The new job exists already, so nothing here may make its creation fail
            try
            {
                WriteRecord(context.Storage, context.Connection, reservation.Key, context.InitialState, new Dictionary<string, string>
                {
                    { "JobId", context.BackgroundJob.Id },
                    { "CreatedAt", JobHelper.SerializeDateTime(Clock()) },
                });
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The reservation stays, so duplicates are refused for a while and then allowed again
                Logger.WarnException($"Background job '{context.BackgroundJob.Id}' was created, but its fingerprint could not be recorded.", ex);
            }

            if (reservation.ReplacedJobId != null)
            {
                ReplaceOwner(context, reservation.ReplacedJobId, context.BackgroundJob.Id);
            }
        }

        // The job wasn't created. Only bookkeeping, so a storage error doesn't replace the creation's own outcome: the
        // reservation then refuses duplicates until it times out (ReservationTimeout).
        private void ReleaseReservation(CreatedContext context, Reservation reservation)
        {
            try
            {
                if (reservation.ReplacedJobId != null)
                {
                    // The job it was to replace wasn't deleted and is still pending: give the fingerprint back to it
                    WriteRecord(context.Storage, context.Connection, reservation.Key, null, new Dictionary<string, string>
                    {
                        { "Reservation", String.Empty },
                        { "JobId", reservation.ReplacedJobId },
                    });

                    return;
                }

                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    transaction.RemoveHash(reservation.Key);
                    transaction.Commit();
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"The duplicate reservation '{reservation.Key}' of a job that wasn't created could not be released; it refuses duplicates until it times out in {ReservationTimeout.TotalMinutes:0} minutes.", ex);
            }
        }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var release = context.NewState.IsFinal || IsReleasing(context.NewState.Name);
            var storageTransaction = ConcurrencyControl.GetExpiringTransaction(context.Storage, transaction);

            // A rescheduled owner (a retry, for example) keeps its fingerprint until its new time plus the window
            var extendTo = !release && storageTransaction != null && context.NewState is ScheduledState
                ? GetExpiration(context.NewState)
                : null;

            if (!release && extendTo == null) return;

            var parameter = context.Connection.GetJobParameter(context.BackgroundJob.Id, FingerprintParameter);
            if (parameter == null || parameter.Length == 0) return;

            var fingerprint = SerializationHelper.Deserialize<string>(parameter) ?? parameter;

            var owner = ReadOwner(context.Connection, fingerprint);
            if (!String.Equals(owner, context.BackgroundJob.Id, StringComparison.Ordinal)) return;

            if (release)
            {
                transaction.RemoveHash(GetKey(fingerprint));
            }
            else if (storageTransaction != null && extendTo != null)
            {
                storageTransaction.ExpireHash(GetKey(fingerprint), extendTo.Value);
            }
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }

        // The new job exists and the record names it: delete the job it replaces, unless that one started running
        // (or finished) since it was checked. The expected states make the check and the delete one step. When it
        // started, or the delete fails, both jobs stay: the new one is created already and must not be reported as
        // failed.
        private static void ReplaceOwner(CreatedContext context, string replacedJobId, string newJobId)
        {
            IState? applied;

            try
            {
                applied = GetStateChanger(context).ChangeState(new StateChangeContext(
                    context.Storage,
                    context.Connection,
                    replacedJobId,
                    new DeletedState { Reason = $"Replaced by a newer duplicate, background job '{newJobId}'" },
                    ReplaceableStates));
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Background job '{replacedJobId}' could not be replaced by its newer duplicate '{newJobId}' and stays, so both exist.", ex);
                return;
            }

            if (applied == null)
            {
                Logger.Info($"Background job '{replacedJobId}' was not replaced by its newer duplicate '{newJobId}': it is no longer waiting to run.");
            }
        }

        // The factory's state changer uses the client's filter provider; without a factory, the global one.
        private static IBackgroundJobStateChanger GetStateChanger(CreateContext context)
        {
            return context.Factory?.StateMachine is StateMachine stateMachine
                ? new BackgroundJobStateChanger(stateMachine)
                : ConcurrencyControl.StateChanger;
        }

        private static bool IsReplaceable(string? stateName)
        {
            return stateName != null && Array.Exists(ReplaceableStates, x => x.Equals(stateName, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsPending(CreateContext context, Dictionary<string, string> hash, out string? ownerId, out string? ownerState)
        {
            ownerId = hash.TryGetValue("JobId", out var jobId) && !String.IsNullOrEmpty(jobId) ? jobId : null;
            ownerState = null;

            if (ownerId == null)
            {
                // Reserved by a creation in progress, unless the reservation is stale.
                var reservedAt = hash.TryGetValue("ReservedAt", out var value) ? JobHelper.DeserializeNullableDateTime(value) : null;
                return reservedAt.HasValue && Clock() - reservedAt.Value < ReservationTimeout;
            }

            var state = context.Connection.GetStateData(ownerId);
            if (state != null)
            {
                ownerState = state.Name;
                return !IsFinal(state.Name) && !IsReleasing(state.Name);
            }

            // Job exists without a state: its initial state is being applied.
            return context.Connection.GetJobData(ownerId) != null;
        }

        private static bool IsFinal(string stateName)
        {
            return SucceededState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase)
                || DeletedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);
        }

        // Non-final states that release the fingerprint
        private bool IsReleasing(string stateName)
        {
            return ReleaseOnFailure && FailedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);
        }

        // How long the record lives: the window, counted from the scheduled time for a scheduled job.
        private TimeSpan? GetExpiration(IState? state)
        {
            if (ExpirationHours <= 0) return null;

            var expiration = TimeSpan.FromHours(ExpirationHours);

            if (state is ScheduledState scheduled)
            {
                var wait = scheduled.EnqueueAt - Clock();
                if (wait > TimeSpan.Zero) expiration += wait;
            }

            return expiration;
        }

        private void WriteRecord(JobStorage storage, IStorageConnection connection, string key, IState? initialState, Dictionary<string, string> fields)
        {
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(key, fields);

                var expiration = GetExpiration(initialState);
                var storageTransaction = ConcurrencyControl.GetExpiringTransaction(storage, transaction);
                if (expiration != null && storageTransaction != null)
                {
                    storageTransaction.ExpireHash(key, expiration.Value);
                }

                transaction.Commit();
            }
        }

        private sealed class Reservation
        {
            public Reservation(string key, string token, string? replacedJobId)
            {
                Key = key;
                Token = token;
                ReplacedJobId = replacedJobId;
            }

            public string Key { get; }
            public string Token { get; }

            /// <summary>The pending job that <see cref="DuplicateHandling.ReplaceExisting"/> deletes once the new job exists.</summary>
            public string? ReplacedJobId { get; }
        }
    }
}
