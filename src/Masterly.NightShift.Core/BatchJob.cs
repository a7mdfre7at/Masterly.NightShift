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
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>Creates the jobs of a batch inside <see cref="BatchJob.StartNew(Action{IBatchBuilder}, string?)"/>.</summary>
    public interface IBatchBuilder
    {
        /// <summary>The batch identifier.</summary>
        string BatchId { get; }

        /// <summary>Creates a job in the batch with the given initial state.</summary>
        string? Create([NotNull] Job job, [NotNull] IState state);

        /// <summary>Enqueues a job in the batch.</summary>
        string? Enqueue([NotNull, InstantHandle] Expression<Action> methodCall);

        /// <summary>Enqueues a job in the batch.</summary>
        string? Enqueue<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall);

        /// <summary>Enqueues an asynchronous job in the batch.</summary>
        string? Enqueue([NotNull, InstantHandle] Expression<Func<Task>> methodCall);

        /// <summary>Enqueues an asynchronous job in the batch.</summary>
        string? Enqueue<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall);

        /// <summary>Schedules a job in the batch.</summary>
        string? Schedule([NotNull, InstantHandle] Expression<Action> methodCall, TimeSpan delay);

        /// <summary>Schedules a job in the batch.</summary>
        string? Schedule<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, TimeSpan delay);
    }

    /// <summary>What is known about a batch: its description and how many of its jobs are in each state.</summary>
    public sealed class JobBatch
    {
        internal JobBatch(string id, string? description, DateTime? createdAt, long total, IReadOnlyDictionary<string, long> counts, IReadOnlyList<string> continuations)
        {
            Id = id;
            Description = description;
            CreatedAt = createdAt;
            Total = total;
            Counts = counts;
            Continuations = continuations;
        }

        public string Id { get; }

        public string? Description { get; }

        public DateTime? CreatedAt { get; }

        /// <summary>How many jobs were created in the batch.</summary>
        public long Total { get; }

        /// <summary>How many jobs are in each state right now, by state name.</summary>
        public IReadOnlyDictionary<string, long> Counts { get; }

        /// <summary>Ids of the jobs created with <see cref="BatchJob.ContinueWith(string, Job, JobContinuationOptions)"/>.</summary>
        public IReadOnlyList<string> Continuations { get; }

        public long Succeeded => Count(SucceededState.StateName);
        public long Failed => Count(FailedState.StateName);
        public long Deleted => Count(DeletedState.StateName);
        public long Processing => Count(ProcessingState.StateName);

        /// <summary>Jobs that are neither finished nor failed: enqueued, scheduled, awaiting, processing.</summary>
        public long Pending => Math.Max(0, Total - Succeeded - Failed - Deleted);

        /// <summary>Whether every job reached a final state (succeeded or deleted).</summary>
        public bool IsFinished => Total > 0 && Succeeded + Deleted >= Total;

        /// <summary>Whether some jobs failed and nothing is running or waiting any more.</summary>
        public bool IsStuck => Total > 0 && Failed > 0 && Pending == 0;

        /// <summary>Succeeded jobs as a share of the batch, 0 to 100.</summary>
        public double Progress => Total == 0 ? 0 : Math.Round(100.0 * Succeeded / Total, 1);

        public long Count(string state)
        {
            return Counts.TryGetValue(state, out var count) ? Math.Max(0, count) : 0;
        }
    }

    /// <summary>
    /// Groups jobs so they can be watched and continued as one: <c>BatchJob.StartNew(b =&gt; { b.Enqueue(...); b.Enqueue(...); })</c>
    /// creates them, the Batches page shows how far the batch got, and
    /// <see cref="ContinueWith(string, Job, JobContinuationOptions)"/> runs a job once every job in the batch has finished
    /// (a fan-in continuation). Batches expire <see cref="ExpireIn"/> after creation.
    /// </summary>
    /// <remarks>
    /// Storage: set <c>batches</c> (ids scored by creation time); hash <c>batch:{id}</c> (<c>Description</c>,
    /// <c>CreatedAt</c>); set <c>batch:{id}:jobs</c>; counters <c>batch:{id}:total</c> and <c>batch:{id}:state:{name}</c>;
    /// set <c>batch:{id}:continuations</c>. Jobs carry the <c>BatchId</c> parameter, continuations <c>BatchContinuationOf</c>.
    /// <see cref="BatchSupportAttribute"/>, a default global filter, keeps the counters up to date.
    /// </remarks>
    public static class BatchJob
    {
        /// <summary>The job parameter naming a job's batch.</summary>
        public const string ParameterName = "BatchId";

        /// <summary>The job parameter on a continuation naming the batch it waits for.</summary>
        public const string ContinuationParameterName = "BatchContinuationOf";

        /// <summary>The set listing every batch.</summary>
        public const string IndexKey = "batches";

        /// <summary>The longest description accepted.</summary>
        public const int MaxDescriptionLength = 200;

        private static readonly AsyncLocal<string?> CurrentBatchId = new AsyncLocal<string?>();
        private static readonly AsyncLocal<string?> CurrentContinuationOf = new AsyncLocal<string?>();

        /// <summary>How long a batch's bookkeeping is kept. Default: 7 days.</summary>
        public static TimeSpan ExpireIn { get; set; } = TimeSpan.FromDays(7);

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>The batch jobs created on the current async flow belong to, or <c>null</c>.</summary>
        public static string? Current => CurrentBatchId.Value;

        internal static string? CurrentContinuation => CurrentContinuationOf.Value;

        public static string GetHashKey(string batchId) => $"batch:{batchId}";
        public static string GetJobsKey(string batchId) => $"batch:{batchId}:jobs";
        public static string GetTotalKey(string batchId) => $"batch:{batchId}:total";
        public static string GetStateKey(string batchId, string state) => $"batch:{batchId}:state:{state}";
        public static string GetContinuationsKey(string batchId) => $"batch:{batchId}:continuations";

        /// <summary>Creates a batch through <see cref="JobStorage.Current"/> and returns its id.</summary>
        public static string StartNew([NotNull, InstantHandle] Action<IBatchBuilder> createJobs, [CanBeNull] string? description = null)
        {
            return StartNew(new BackgroundJobClient(JobStorage.Current), JobStorage.Current, createJobs, description);
        }

        /// <summary>Creates a batch: the action creates its jobs through the builder, all tagged with the new batch id.</summary>
        public static string StartNew([NotNull] IBackgroundJobClient client, [NotNull] JobStorage storage, [NotNull, InstantHandle] Action<IBatchBuilder> createJobs, [CanBeNull] string? description = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (createJobs == null) throw new ArgumentNullException(nameof(createJobs));
            if (description != null && description.Length > MaxDescriptionLength) throw new ArgumentException($"The description can't be longer than {MaxDescriptionLength} characters.", nameof(description));

            var batchId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var now = Clock();

            using (var connection = storage.GetConnection())
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash(GetHashKey(batchId), new[]
                {
                    new KeyValuePair<string, string>("Description", description ?? String.Empty),
                    new KeyValuePair<string, string>("CreatedAt", JobHelper.SerializeDateTime(now)),
                });
                transaction.AddToSet(IndexKey, batchId, JobHelper.ToTimestamp(now));

                if (transaction is JobStorageTransaction storageTransaction)
                {
                    storageTransaction.ExpireHash(GetHashKey(batchId), ExpireIn);
                    storageTransaction.ExpireSet(GetJobsKey(batchId), ExpireIn);
                    storageTransaction.ExpireSet(GetContinuationsKey(batchId), ExpireIn);
                }

                transaction.Commit();
            }

            var previous = CurrentBatchId.Value;
            CurrentBatchId.Value = batchId;
            try
            {
                createJobs(new Builder(client, batchId));
            }
            finally
            {
                CurrentBatchId.Value = previous;
            }

            return batchId;
        }

        /// <summary>Runs a job once every job currently in the batch has finished.</summary>
        public static string? ContinueWith([NotNull] string batchId, [NotNull, InstantHandle] Expression<Action> methodCall, JobContinuationOptions options = JobContinuationOptions.OnAnyFinishedState)
        {
            return ContinueWith(batchId, Job.FromExpression(methodCall), options);
        }

        /// <summary>Runs a job once every job currently in the batch has finished.</summary>
        public static string? ContinueWith<T>([NotNull] string batchId, [NotNull, InstantHandle] Expression<Action<T>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnAnyFinishedState)
        {
            return ContinueWith(batchId, Job.FromExpression(methodCall), options);
        }

        /// <summary>Runs a job once every job currently in the batch has finished.</summary>
        public static string? ContinueWith([NotNull] string batchId, [NotNull, InstantHandle] Expression<Func<Task>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnAnyFinishedState)
        {
            return ContinueWith(batchId, Job.FromExpression(methodCall), options);
        }

        /// <summary>Runs a job once every job currently in the batch has finished, through <see cref="JobStorage.Current"/>.</summary>
        public static string? ContinueWith([NotNull] string batchId, [NotNull] Job job, JobContinuationOptions options = JobContinuationOptions.OnAnyFinishedState)
        {
            return ContinueWith(new BackgroundJobClient(JobStorage.Current), JobStorage.Current, batchId, job, options);
        }

        /// <summary>
        /// Runs a job once every job currently in the batch has finished: a fan-in continuation over the batch's
        /// jobs. With <see cref="JobContinuationOptions.OnlyOnSucceededState"/> a failed or deleted job in the
        /// batch cancels the continuation.
        /// </summary>
        public static string? ContinueWith([NotNull] IBackgroundJobClient client, [NotNull] JobStorage storage, [NotNull] string batchId, [NotNull] Job job, JobContinuationOptions options = JobContinuationOptions.OnAnyFinishedState)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (batchId == null) throw new ArgumentNullException(nameof(batchId));
            if (job == null) throw new ArgumentNullException(nameof(job));

            IReadOnlyList<string> jobIds;
            using (var connection = storage.GetConnection())
            {
                if (connection.GetAllEntriesFromHash(GetHashKey(batchId)) == null)
                {
                    throw new InvalidOperationException($"Batch '{batchId}' does not exist or has expired.");
                }

                jobIds = GetJobIds(connection, batchId);
            }

            if (jobIds.Count == 0)
            {
                throw new InvalidOperationException($"Batch '{batchId}' has no jobs to wait for.");
            }

            var previous = CurrentContinuationOf.Value;
            CurrentContinuationOf.Value = batchId;
            try
            {
                return client.Create(job, new AwaitingJobsState(jobIds, new EnqueuedState(), options, FanInMode.All));
            }
            finally
            {
                CurrentContinuationOf.Value = previous;
            }
        }

        /// <summary>Reads a batch, or <c>null</c> when it doesn't exist or expired.</summary>
        public static JobBatch? Get([NotNull] IStorageConnection connection, [NotNull] string batchId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (batchId == null) throw new ArgumentNullException(nameof(batchId));

            var hash = connection.GetAllEntriesFromHash(GetHashKey(batchId));
            if (hash == null) return null;

            var storageConnection = connection as JobStorageConnection;
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long total = 0;

            if (storageConnection != null)
            {
                total = storageConnection.GetCounter(GetTotalKey(batchId));
                foreach (var state in TrackedStates)
                {
                    var count = storageConnection.GetCounter(GetStateKey(batchId, state));
                    if (count != 0) counts[state] = count;
                }
            }
            else
            {
                total = connection.GetAllItemsFromSet(GetJobsKey(batchId))?.Count ?? 0;
            }

            var continuations = (connection.GetAllItemsFromSet(GetContinuationsKey(batchId)) ?? new HashSet<string>()).OrderBy(static x => x, StringComparer.Ordinal).ToList();

            return new JobBatch(
                batchId,
                hash.TryGetValue("Description", out var description) && description.Length > 0 ? description : null,
                hash.TryGetValue("CreatedAt", out var createdAt) ? JobHelper.DeserializeNullableDateTime(createdAt) : null,
                total,
                counts,
                continuations);
        }

        /// <summary>The ids of the jobs in a batch, in creation order.</summary>
        public static IReadOnlyList<string> GetJobIds([NotNull] IStorageConnection connection, [NotNull] string batchId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (batchId == null) throw new ArgumentNullException(nameof(batchId));

            if (connection is JobStorageConnection storageConnection)
            {
                return storageConnection.GetRangeFromSet(GetJobsKey(batchId), 0, Int32.MaxValue - 1) ?? new List<string>();
            }

            return (connection.GetAllItemsFromSet(GetJobsKey(batchId)) ?? new HashSet<string>()).ToList();
        }

        /// <summary>How many batches are listed, expired ones included until they are met.</summary>
        public static long Count([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return connection is JobStorageConnection storageConnection
                ? storageConnection.GetSetCount(IndexKey)
                : connection.GetAllItemsFromSet(IndexKey)?.Count ?? 0;
        }

        /// <summary>Lists batches, newest first. Expired batches are dropped from the index as they are met.</summary>
        public static IReadOnlyList<JobBatch> List([NotNull] IStorageConnection connection, int from, int count)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (from < 0) throw new ArgumentOutOfRangeException(nameof(from));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            List<string> ids;

            if (connection is JobStorageConnection storageConnection)
            {
                var total = storageConnection.GetSetCount(IndexKey);
                var endingAt = total - 1 - from;
                if (endingAt < 0) return Array.Empty<JobBatch>();

                var startingFrom = Math.Max(0, endingAt - count + 1);
                ids = storageConnection.GetRangeFromSet(IndexKey, (int)startingFrom, (int)endingAt) ?? new List<string>();
                ids.Reverse();
            }
            else
            {
                ids = (connection.GetAllItemsFromSet(IndexKey) ?? new HashSet<string>()).Reverse().Skip(from).Take(count).ToList();
            }

            var result = new List<JobBatch>(ids.Count);
            var expired = new List<string>();

            foreach (var id in ids)
            {
                var batch = Get(connection, id);
                if (batch != null) result.Add(batch);
                else expired.Add(id);
            }

            if (expired.Count > 0)
            {
                using (var transaction = connection.CreateWriteTransaction())
                {
                    foreach (var id in expired) transaction.RemoveFromSet(IndexKey, id);
                    transaction.Commit();
                }
            }

            return result;
        }

        internal static readonly string[] TrackedStates =
        {
            EnqueuedState.StateName, ScheduledState.StateName, ProcessingState.StateName,
            SucceededState.StateName, FailedState.StateName, DeletedState.StateName, AwaitingState.StateName,
        };

        private sealed class Builder : IBatchBuilder
        {
            private readonly IBackgroundJobClient _client;

            public Builder(IBackgroundJobClient client, string batchId)
            {
                _client = client;
                BatchId = batchId;
            }

            public string BatchId { get; }

            public string? Create(Job job, IState state) => _client.Create(job, state);

            public string? Enqueue(Expression<Action> methodCall) => _client.Create(Job.FromExpression(methodCall), new EnqueuedState());

            public string? Enqueue<T>(Expression<Action<T>> methodCall) => _client.Create(Job.FromExpression(methodCall), new EnqueuedState());

            public string? Enqueue(Expression<Func<Task>> methodCall) => _client.Create(Job.FromExpression(methodCall), new EnqueuedState());

            public string? Enqueue<T>(Expression<Func<T, Task>> methodCall) => _client.Create(Job.FromExpression(methodCall), new EnqueuedState());

            public string? Schedule(Expression<Action> methodCall, TimeSpan delay) => _client.Create(Job.FromExpression(methodCall), new ScheduledState(delay));

            public string? Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay) => _client.Create(Job.FromExpression(methodCall), new ScheduledState(delay));
        }
    }

    /// <summary>
    /// Tags jobs created inside <see cref="BatchJob.StartNew(Action{IBatchBuilder}, string?)"/> with their batch
    /// and keeps the batch's per-state counters current on every state transition. Registered globally by default.
    /// </summary>
    public sealed class BatchSupportAttribute : JobFilterAttribute, IClientFilter, IApplyStateFilter
    {
        public BatchSupportAttribute()
        {
            Order = 40;
        }

        /// <inheritdoc />
        public void OnCreating(CreatingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var batchId = BatchJob.Current;
            if (batchId != null)
            {
                context.SetJobParameter(BatchJob.ParameterName, batchId);
            }

            var continuationOf = BatchJob.CurrentContinuation;
            if (continuationOf != null)
            {
                context.SetJobParameter(BatchJob.ContinuationParameterName, continuationOf);
            }
        }

        /// <inheritdoc />
        public void OnCreated(CreatedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Canceled || context.Exception != null || context.BackgroundJob == null) return;

            var continuationOf = BatchJob.CurrentContinuation;
            if (continuationOf == null) return;

            using (var transaction = context.Connection.CreateWriteTransaction())
            {
                transaction.AddToSet(BatchJob.GetContinuationsKey(continuationOf), context.BackgroundJob.Id);
                transaction.Commit();
            }
        }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var batchId = GetBatchId(context);
            if (String.IsNullOrEmpty(batchId)) return;

            var expireIn = BatchJob.ExpireIn;

            if (context.OldStateName == null)
            {
                transaction.AddToSet(BatchJob.GetJobsKey(batchId!), context.BackgroundJob.Id, JobHelper.ToTimestamp(BatchJob.Clock()));
                transaction.IncrementCounter(BatchJob.GetTotalKey(batchId!), expireIn);
            }
            else if (IsTracked(context.OldStateName))
            {
                transaction.DecrementCounter(BatchJob.GetStateKey(batchId!, context.OldStateName), expireIn);
            }

            if (IsTracked(context.NewState.Name))
            {
                transaction.IncrementCounter(BatchJob.GetStateKey(batchId!, context.NewState.Name), expireIn);
            }
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }

        private static bool IsTracked(string state)
        {
            foreach (var tracked in BatchJob.TrackedStates)
            {
                if (String.Equals(tracked, state, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string? GetBatchId(ApplyStateContext context)
        {
            var snapshot = context.BackgroundJob.ParametersSnapshot;
            if (snapshot != null)
            {
                return snapshot.TryGetValue(BatchJob.ParameterName, out var serialized) && !String.IsNullOrEmpty(serialized)
                    ? SerializationHelper.Deserialize<string>(serialized, SerializationOption.User)
                    : null;
            }

            try
            {
                return context.GetJobParameter<string>(BatchJob.ParameterName, allowStale: true);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return null;
            }
        }
    }
}
