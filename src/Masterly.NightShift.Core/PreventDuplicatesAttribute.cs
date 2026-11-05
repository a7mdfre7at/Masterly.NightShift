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

        /// <summary>The pending job is deleted and the new one takes its place.</summary>
        ReplaceExisting,
    }

    /// <summary>
    /// Refuses a second copy of a job while the first one is still pending. The fingerprint is the method and its
    /// arguments, or the <see cref="Template"/> (<c>{0}</c>..<c>{n}</c> are the arguments) when one is given.
    /// Works at creation time, so duplicates never reach the queue.
    /// </summary>
    /// <remarks>
    /// The fingerprint is kept in the <c>fingerprint:{hash}</c> storage hash and released when the owning job
    /// reaches a final state. The check reads the owner's current state, so a fingerprint can never block after
    /// its job has finished, even when the release was lost. New jobs carry the <c>Fingerprint</c> parameter.
    /// </remarks>
    public sealed class PreventDuplicatesAttribute : JobFilterAttribute, IClientFilter, IApplyStateFilter
    {
        /// <summary>The job parameter holding the fingerprint.</summary>
        public const string FingerprintParameter = "Fingerprint";

        private const string ReservationItem = "X_HF_FingerprintReservation";
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReservationTimeout = TimeSpan.FromMinutes(2);
        private static readonly ILog Logger = LogProvider.For<PreventDuplicatesAttribute>();

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

        /// <summary>The fingerprint template, or <c>null</c> for "method plus every argument".</summary>
        public string? Template { get; }

        /// <summary>What to do with the new job when a duplicate is pending. Default <see cref="DuplicateHandling.Cancel"/>.</summary>
        public DuplicateHandling OnDuplicate { get; set; } = DuplicateHandling.Cancel;

        /// <summary>
        /// Safety net: the fingerprint record expires after this many hours (default 24) even if its release was lost
        /// and the owner's state can't be read. Zero disables the expiration.
        /// </summary>
        public int ExpirationHours { get; set; } = 24;

        /// <summary>Computes the fingerprint of a job as this attribute sees it.</summary>
        public string GetFingerprint([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            if (!String.IsNullOrWhiteSpace(Template))
            {
                return ConcurrencyControl.FormatResource(Template, job);
            }

            var invocation = InvocationData.SerializeJob(job);
            var text = $"{job.Type.ToGenericTypeString()}.{job.Method.Name}({invocation.Arguments})";

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
            return $"fingerprint:{fingerprint}";
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

            using (context.Connection.AcquireDistributedLock(key, LockTimeout))
            {
                var hash = context.Connection.GetAllEntriesFromHash(key);

                if (hash != null && IsPending(context, hash, out var ownerId))
                {
                    if (OnDuplicate == DuplicateHandling.Cancel)
                    {
                        Logger.Debug($"Background job {context.Job.Type.Name}.{context.Job.Method.Name} was not created: a duplicate ({ownerId ?? "being created"}) is still pending.");
                        context.Canceled = true;
                        return;
                    }

                    if (ownerId != null)
                    {
                        ConcurrencyControl.StateChanger.ChangeState(new StateChangeContext(
                            context.Storage,
                            context.Connection,
                            ownerId,
                            new DeletedState { Reason = "Replaced by a newer duplicate" }));
                    }
                }

                WriteRecord(context.Connection, key, new Dictionary<string, string>
                {
                    { "Reservation", reservation },
                    { "ReservedAt", JobHelper.SerializeDateTime(Clock()) },
                    { "JobId", String.Empty },
                });
            }

            context.Items[ReservationItem] = new Reservation(key, reservation);
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
                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    transaction.RemoveHash(reservation.Key);
                    transaction.Commit();
                }

                return;
            }

            WriteRecord(context.Connection, reservation.Key, new Dictionary<string, string>
            {
                { "JobId", context.BackgroundJob.Id },
                { "CreatedAt", JobHelper.SerializeDateTime(Clock()) },
            });
        }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (!context.NewState.IsFinal) return;

            var parameter = context.Connection.GetJobParameter(context.BackgroundJob.Id, FingerprintParameter);
            if (parameter == null || parameter.Length == 0) return;

            var fingerprint = SerializationHelper.Deserialize<string>(parameter) ?? parameter;

            var owner = ReadOwner(context.Connection, fingerprint);
            if (String.Equals(owner, context.BackgroundJob.Id, StringComparison.Ordinal))
            {
                transaction.RemoveHash(GetKey(fingerprint));
            }
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }

        private static bool IsPending(CreateContext context, Dictionary<string, string> hash, out string? ownerId)
        {
            ownerId = hash.TryGetValue("JobId", out var jobId) && !String.IsNullOrEmpty(jobId) ? jobId : null;

            if (ownerId == null)
            {
                // Reserved by a creation in progress, unless the reservation is stale.
                var reservedAt = hash.TryGetValue("ReservedAt", out var value) ? JobHelper.DeserializeNullableDateTime(value) : null;
                return reservedAt.HasValue && Clock() - reservedAt.Value < ReservationTimeout;
            }

            var state = context.Connection.GetStateData(ownerId);
            if (state != null)
            {
                return !IsFinal(state.Name);
            }

            // Job exists without a state: its initial state is being applied.
            return context.Connection.GetJobData(ownerId) != null;
        }

        private static bool IsFinal(string stateName)
        {
            return SucceededState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase)
                || DeletedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);
        }

        private void WriteRecord(IStorageConnection connection, string key, Dictionary<string, string> fields)
        {
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(key, fields);

                if (ExpirationHours > 0 && transaction is JobStorageTransaction storageTransaction)
                {
                    storageTransaction.ExpireHash(key, TimeSpan.FromHours(ExpirationHours));
                }

                transaction.Commit();
            }
        }

        private sealed class Reservation
        {
            public Reservation(string key, string token)
            {
                Key = key;
                Token = token;
            }

            public string Key { get; }
            public string Token { get; }
        }
    }
}
