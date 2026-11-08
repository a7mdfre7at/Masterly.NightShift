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
using Masterly.NightShift.Logging;
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
    /// <see cref="BatchSupportAttribute"/>, a default global filter, keeps the counters up to date. Expiration needs
    /// a storage with <see cref="JobStorageFeatures.ExtendedApi"/>; on other storages the bookkeeping is kept.
    /// Ids older than <see cref="ExpireIn"/> are removed from <c>batches</c> when new batches are started.
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

        /// <summary>How many expired batch ids one <c>StartNew</c> removes from the index at most.</summary>
        private const int MaxExpiredRemovedPerStart = 100;

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(BatchJob));
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

        /// <summary>
        /// Reads the <see cref="ParameterName"/> or <see cref="ContinuationParameterName"/> parameter of a job. They
        /// are written with the internal serializer options; values written with the application's options before
        /// are read too.
        /// </summary>
        internal static string? ParseParameter(string? serialized)
        {
            return String.IsNullOrEmpty(serialized) ? null : SerializationHelper.Deserialize<string>(serialized);
        }

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
                RemoveExpiredFromIndex(storage, connection, transaction, now);

                // Only the hash has rows yet: expiring an empty set doesn't reach the rows added later
                if (ExtendedStorage.IsSupported(storage) && transaction is JobStorageTransaction storageTransaction)
                {
                    storageTransaction.ExpireHash(GetHashKey(batchId), ExpireIn);
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
                ExpireJobList(storage, batchId);
            }

            return batchId;
        }

        /// <summary>
        /// Expires the batch's job list once its jobs are created. Expiring it with every job would update every
        /// row of the list each time, which grows with the square of the batch size.
        /// </summary>
        private static void ExpireJobList(JobStorage storage, string batchId)
        {
            if (!ExtendedStorage.IsSupported(storage)) return;

            try
            {
                using (var connection = storage.GetConnection())
                using (var transaction = connection.CreateWriteTransaction())
                {
                    if (transaction is JobStorageTransaction storageTransaction)
                    {
                        storageTransaction.ExpireSet(GetJobsKey(batchId), ExpireIn);
                        transaction.Commit();
                    }
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The batch and its jobs exist already; failing here would make callers create them again
                Logger.WarnException($"Could not set the expiration of the job list of batch '{batchId}'. Its rows stay in storage until removed.", ex);
            }
        }

        /// <summary>Removes the ids of batches older than <see cref="ExpireIn"/> from the index, a page at a time.</summary>
        private static void RemoveExpiredFromIndex(JobStorage storage, IStorageConnection connection, IWriteOnlyTransaction transaction, DateTime now)
        {
            if (now - DateTime.MinValue <= ExpireIn) return;

            var expiredBefore = JobHelper.ToTimestamp(now - ExpireIn);
            if (expiredBefore <= 0) return;

            IEnumerable<string> expired;

            if (connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest))
            {
                expired = storageConnection.GetFirstByLowestScoreFromSet(IndexKey, 0, expiredBefore, MaxExpiredRemovedPerStart) ?? new List<string>();
            }
            else
            {
                var oldest = connection.GetFirstByLowestScoreFromSet(IndexKey, 0, expiredBefore);
                expired = oldest != null ? new[] { oldest } : Array.Empty<string>();
            }

            foreach (var id in expired)
            {
                transaction.RemoveFromSet(IndexKey, id);
            }
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
        /// jobs. With <see cref="JobContinuationOptions.OnlyOnSucceededState"/> a deleted job in the batch deletes the
        /// continuation. A failed job isn't finished: the continuation waits until it is requeued (and succeeds or is
        /// deleted) or deleted, whatever the options.
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

        /// <summary>
        /// Cancels a batch through <see cref="JobStorage.Current"/>: deletes every job of the batch that hasn't
        /// succeeded or been deleted. See <see cref="Cancel(IBackgroundJobClient, JobStorage, string, TimeSpan?)"/>.
        /// </summary>
        public static BulkJobResult Cancel([NotNull] string batchId, TimeSpan? timeBudget = null)
        {
            return Cancel(new BackgroundJobClient(JobStorage.Current), JobStorage.Current, batchId, timeBudget);
        }

        /// <summary>
        /// Cancels a batch: deletes every job of the batch that hasn't succeeded or been deleted (enqueued, scheduled,
        /// awaiting, processing and failed jobs), like "Delete" on the batch's dashboard page. Each job is deleted only
        /// if it is still in the state that was read, so a job that succeeds meanwhile is kept. A processing job keeps
        /// running until it checks its cancellation token. Once every job is deleted or succeeded, the batch is
        /// finished and its continuations run (or are deleted, with <see cref="JobContinuationOptions.OnlyOnSucceededState"/>).
        /// </summary>
        /// <param name="client">The client that changes the states.</param>
        /// <param name="storage">The storage that holds the batch.</param>
        /// <param name="batchId">The batch.</param>
        /// <param name="timeBudget">
        /// How long the call may take, or <c>null</c> (the default) to go on until every job is reached. When the time
        /// is up, the result isn't <see cref="BulkJobResult.Complete"/>, and another call goes on with the jobs left.
        /// </param>
        /// <returns>How many jobs were deleted, and whether every job was reached.</returns>
        /// <exception cref="InvalidOperationException">The batch doesn't exist or expired.</exception>
        public static BulkJobResult Cancel([NotNull] IBackgroundJobClient client, [NotNull] JobStorage storage, [NotNull] string batchId, TimeSpan? timeBudget = null)
        {
            return ChangeJobs(client, storage, batchId, timeBudget, IsUnfinished, static (c, jobId, state) => c.ChangeState(jobId, new DeletedState { Reason = "Canceled with BatchJob.Cancel" }, state));
        }

        /// <summary>
        /// Requeues every failed job of a batch through <see cref="JobStorage.Current"/>.
        /// See <see cref="RequeueFailed(IBackgroundJobClient, JobStorage, string, TimeSpan?)"/>.
        /// </summary>
        public static BulkJobResult RequeueFailed([NotNull] string batchId, TimeSpan? timeBudget = null)
        {
            return RequeueFailed(new BackgroundJobClient(JobStorage.Current), JobStorage.Current, batchId, timeBudget);
        }

        /// <summary>
        /// Requeues every failed job of a batch, like "Requeue failed" on the batch's dashboard page. A job is requeued
        /// only if it is still failed when its turn comes.
        /// </summary>
        /// <param name="client">The client that changes the states.</param>
        /// <param name="storage">The storage that holds the batch.</param>
        /// <param name="batchId">The batch.</param>
        /// <param name="timeBudget">
        /// How long the call may take, or <c>null</c> (the default) to go on until every job is reached. When the time
        /// is up, the result isn't <see cref="BulkJobResult.Complete"/>, and another call goes on with the jobs left.
        /// </param>
        /// <returns>How many jobs were requeued, and whether every job was reached.</returns>
        /// <exception cref="InvalidOperationException">The batch doesn't exist or expired.</exception>
        public static BulkJobResult RequeueFailed([NotNull] IBackgroundJobClient client, [NotNull] JobStorage storage, [NotNull] string batchId, TimeSpan? timeBudget = null)
        {
            return ChangeJobs(client, storage, batchId, timeBudget, IsFailed, static (c, jobId, _) => c.ChangeState(jobId, new EnqueuedState { Reason = "Requeued with BatchJob.RequeueFailed" }, FailedState.StateName));
        }

        /// <summary>Selects the failed jobs of a batch.</summary>
        internal static bool IsFailed(string? state) => state == FailedState.StateName;

        /// <summary>Selects the jobs of a batch that haven't reached a final state.</summary>
        internal static bool IsUnfinished(string? state) => state != SucceededState.StateName && state != DeletedState.StateName;

        private static BulkJobResult ChangeJobs(
            IBackgroundJobClient client,
            JobStorage storage,
            string batchId,
            TimeSpan? timeBudget,
            Func<string?, bool> select,
            Func<IBackgroundJobClient, string, string?, bool> action)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (batchId == null) throw new ArgumentNullException(nameof(batchId));
            if (timeBudget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeBudget), "The time budget can't be negative.");

            return BulkStateChange.RunOnBatch(storage, client, batchId, select, action, BulkStateChange.Unlimited, timeBudget)
                ?? throw new InvalidOperationException($"Batch '{batchId}' does not exist or has expired.");
        }

        /// <summary>Reads a batch, or <c>null</c> when it doesn't exist or expired.</summary>
        public static JobBatch? Get([NotNull] IStorageConnection connection, [NotNull] string batchId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (batchId == null) throw new ArgumentNullException(nameof(batchId));

            var hash = connection.GetAllEntriesFromHash(GetHashKey(batchId));
            if (hash == null) return null;

            // Counters need the extended API; without it the total comes from the job list and counts are unknown
            var counts = ExtendedStorage.Read(
                connection,
                storageConnection => ReadCounts(storageConnection, batchId),
                static _ => new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
            var total = ExtendedStorage.Read(
                connection,
                storageConnection => storageConnection.GetCounter(GetTotalKey(batchId)),
                basicConnection => (long)(basicConnection.GetAllItemsFromSet(GetJobsKey(batchId))?.Count ?? 0));

            var continuations = (connection.GetAllItemsFromSet(GetContinuationsKey(batchId)) ?? new HashSet<string>()).OrderBy(static x => x, StringComparer.Ordinal).ToList();

            return new JobBatch(
                batchId,
                hash.TryGetValue("Description", out var description) && description.Length > 0 ? description : null,
                hash.TryGetValue("CreatedAt", out var createdAt) ? JobHelper.DeserializeNullableDateTime(createdAt) : null,
                total,
                counts,
                continuations);
        }

        private static Dictionary<string, long> ReadCounts(JobStorageConnection connection, string batchId)
        {
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var state in TrackedStates)
            {
                var count = connection.GetCounter(GetStateKey(batchId, state));
                if (count != 0) counts[state] = count;
            }

            return counts;
        }

        /// <summary>The ids of the jobs in a batch, in creation order.</summary>
        public static IReadOnlyList<string> GetJobIds([NotNull] IStorageConnection connection, [NotNull] string batchId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (batchId == null) throw new ArgumentNullException(nameof(batchId));

            var key = GetJobsKey(batchId);

            return ExtendedStorage.Read<IReadOnlyList<string>>(
                connection,
                storageConnection => storageConnection.GetRangeFromSet(key, 0, Int32.MaxValue - 1) ?? new List<string>(),
                basicConnection => (basicConnection.GetAllItemsFromSet(key) ?? new HashSet<string>()).ToList());
        }

        /// <summary>
        /// How many batches are listed. Expired batches are included until a later <c>StartNew</c> removes them
        /// from the index.
        /// </summary>
        public static long Count([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return ExtendedStorage.Read(
                connection,
                static storageConnection => storageConnection.GetSetCount(IndexKey),
                static basicConnection => (long)(basicConnection.GetAllItemsFromSet(IndexKey)?.Count ?? 0));
        }

        /// <summary>
        /// Lists batches, newest first. Batches that expired but are still in the index are skipped, so a page can
        /// hold fewer than <paramref name="count"/> batches. Nothing is written: a later <c>StartNew</c> removes
        /// expired ids from the index.
        /// </summary>
        public static IReadOnlyList<JobBatch> List([NotNull] IStorageConnection connection, int from, int count)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (from < 0) throw new ArgumentOutOfRangeException(nameof(from));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            var ids = ExtendedStorage.Read(
                connection,
                storageConnection =>
                {
                    var total = storageConnection.GetSetCount(IndexKey);
                    var endingAt = total - 1 - from;
                    if (endingAt < 0) return new List<string>();

                    var startingFrom = Math.Max(0, endingAt - count + 1);
                    var range = storageConnection.GetRangeFromSet(IndexKey, (int)startingFrom, (int)endingAt) ?? new List<string>();
                    range.Reverse();
                    return range;
                },
                basicConnection => (basicConnection.GetAllItemsFromSet(IndexKey) ?? new HashSet<string>()).Reverse().Skip(from).Take(count).ToList());

            var result = new List<JobBatch>(ids.Count);

            foreach (var id in ids)
            {
                var batch = Get(connection, id);
                if (batch != null) result.Add(batch);
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
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(BatchSupportAttribute));

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

            // A continuation is added to its batch's list in the transaction that stores its first state (see
            // OnStateApplied), not here: a write after the job exists could fail and make the caller create it again
        }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            context.BatchCountersApplied = true;
            Apply(context, transaction);
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }

        /// <summary>
        /// Keeps the batch counters right for a state change made without filters, such as the
        /// <see cref="FailedState"/> the worker stores when every attempt to store a job's final state failed. Called
        /// by the state machine for every state change; does nothing when the filter already ran, or when it isn't
        /// registered in <see cref="GlobalJobFilters"/> (batches are then turned off). Bookkeeping never fails the
        /// state change: an error is logged.
        /// </summary>
        internal static void ApplyWithoutFilter(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context.BatchCountersApplied || !IsRegistered()) return;

            try
            {
                Apply(context, transaction);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Could not update the batch counters of job '{context.BackgroundJob.Id}'.", ex);
            }
        }

        private static bool IsRegistered()
        {
            foreach (var filter in GlobalJobFilters.Filters)
            {
                if (filter.Instance is BatchSupportAttribute) return true;
            }

            return false;
        }

        private static void Apply(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context.OldStateName == null)
            {
                RecordContinuation(context, transaction);
            }

            var batchId = GetParameter(context, BatchJob.ParameterName);
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

        private static bool IsTracked(string state)
        {
            foreach (var tracked in BatchJob.TrackedStates)
            {
                if (String.Equals(tracked, state, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Adds a continuation created by <see cref="BatchJob.ContinueWith(string, Job, JobContinuationOptions)"/> to
        /// its batch's list, in the transaction of its first state, so the list never misses a continuation that
        /// exists. The continuation itself doesn't depend on the list: <see cref="FanInContinuationsSupportAttribute"/>
        /// decides it.
        /// </summary>
        private static void RecordContinuation(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            var continuationOf = GetParameter(context, BatchJob.ContinuationParameterName);
            if (String.IsNullOrEmpty(continuationOf)) return;

            var key = BatchJob.GetContinuationsKey(continuationOf!);
            transaction.AddToSet(key, context.BackgroundJob.Id);

            // After adding: on SQL Server, expiring a set only reaches the rows it already has
            if (ExtendedStorage.IsSupported(context.Storage) && transaction is JobStorageTransaction storageTransaction)
            {
                storageTransaction.ExpireSet(key, BatchJob.ExpireIn);
            }
        }

        /// <summary>Reads one of the batch parameters, from the snapshot when there is one.</summary>
        private static string? GetParameter(ApplyStateContext context, string name)
        {
            try
            {
                var snapshot = context.BackgroundJob.ParametersSnapshot;
                string? serialized = null;

                if (snapshot != null)
                {
                    snapshot.TryGetValue(name, out serialized);
                }
                else
                {
                    serialized = context.Connection.GetJobParameter(context.BackgroundJob.Id, name);
                }

                return BatchJob.ParseParameter(serialized);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Bookkeeping: a parameter that can't be read leaves the job out of its batch's records
                return null;
            }
        }
    }
}
