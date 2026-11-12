using System.Runtime.CompilerServices;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore;

/// <summary>
/// Stages jobs in an application's context, so they are created with its other changes:
/// <c>context.NightShift(storage).Enqueue(() => ...)</c>, then <c>SaveChanges</c>. Saved, the jobs run; not saved
/// or rolled back, they never do.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class NightShiftDbContextExtensions
#pragma warning restore CA1708
{
    private static readonly ConditionalWeakTable<DbContext, StagingSubscription> Subscribed = [];

    /// <param name="context">The application's context, whose model includes <c>ApplyNightShiftModel</c>.</param>
    extension([NotNull] DbContext context)
    {
        /// <summary>
        /// A client whose jobs are staged in the context. A job's id is reserved at once, in a row that expires by
        /// itself unless the context saves the job; its state, queue entry and every other write of its creation
        /// wait for the context's <c>SaveChanges</c> (or its transaction) and commit or roll back with it.
        /// Client filters run when the job is staged, as they do when it is created.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The id is reserved on a connection of its own, outside the context's transaction, and the reservation
        /// lasts at least a day (30 days for jobs created by the client), so a context may be saved long after the
        /// job is staged. SQLite has one writer at a time: while the context's transaction is open, the
        /// reservation would wait for it until the busy timeout, so staging a job then throws at once. Stage it
        /// before <c>BeginTransaction</c>, or enqueue it through the storage once the transaction committed.
        /// </para>
        /// <para>
        /// Work that runs once a job's creation is committed (alerts, activity feed entries and other
        /// after-commit work of filters) runs when the job is staged, not when the context is saved, and runs
        /// even if the context is never saved. A <c>SaveChanges</c> that saves staged queue entries wakes this
        /// process's waiting workers at once; other processes see the jobs at their next poll.
        /// </para>
        /// </remarks>
        /// <param name="storage">The storage, whose tables are in the context's model.</param>
        /// <returns>A client to create jobs with, through any of its extension methods.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The context's model doesn't include the storage's tables; or, when a job is staged, the context is on
        /// SQLite and its transaction is open.
        /// </exception>
        public IBackgroundJobClient NightShift([NotNull] EntityFrameworkCoreStorage storage)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(storage);
            EntityFrameworkCoreStorage.EnsureModel(context);

            // Saving staged queue entries wakes this process's workers, as enqueuing through the storage does
            if (!Subscribed.TryGetValue(context, out _))
            {
                Subscribed.AddOrUpdate(context, new StagingSubscription(storage));
                context.SavingChanges += static (sender, _) =>
                {
                    if (sender is DbContext saving && Subscribed.TryGetValue(saving, out StagingSubscription? subscription))
                    {
                        subscription.QueuesSaving = saving.ChangeTracker.Entries<JobQueueRow>().Any(static x => x.State == EntityState.Added);
                    }
                };
                context.SavedChanges += static (sender, _) =>
                {
                    if (sender is DbContext saved && Subscribed.TryGetValue(saved, out StagingSubscription? subscription) && subscription.QueuesSaving)
                    {
                        subscription.QueuesSaving = false;
                        subscription.Storage.Signal();
                    }
                };
                context.SaveChangesFailed += static (sender, _) =>
                {
                    if (sender is DbContext failed && Subscribed.TryGetValue(failed, out StagingSubscription? subscription)) subscription.QueuesSaving = false;
                };
            }

            return new BackgroundJobClient(new StagingStorage(storage, context));
        }

        /// <summary>
        /// A client whose jobs are staged in the context, on <see cref="JobStorage.Current"/>, which must be an
        /// <see cref="EntityFrameworkCoreStorage"/>.
        /// </summary>
        /// <returns>A client to create jobs with, through any of its extension methods.</returns>
        /// <exception cref="InvalidOperationException">The current storage isn't an <see cref="EntityFrameworkCoreStorage"/>.</exception>
        public IBackgroundJobClient NightShift()
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.NightShift(JobStorage.Current as EntityFrameworkCoreStorage
                ?? throw new InvalidOperationException("The current job storage isn't an EntityFrameworkCoreStorage."));
        }
    }
}

/// <summary>The storage a context stages jobs for, and whether the save in progress writes queue entries.</summary>
internal sealed class StagingSubscription(EntityFrameworkCoreStorage storage)
{
    public EntityFrameworkCoreStorage Storage { get; } = storage;

    public bool QueuesSaving { get; set; }
}

/// <summary>The storage, with writes staged in an application's context.</summary>
internal sealed class StagingStorage(EntityFrameworkCoreStorage storage, DbContext context) : JobStorage
{
    public override IMonitoringApi GetMonitoringApi() => storage.GetMonitoringApi();

    public override IStorageConnection GetConnection() => new StagingConnection((EfConnection)storage.GetConnection(), context);

    public override bool HasFeature(string featureId) => storage.HasFeature(featureId);

    public override string ToString() => storage.ToString();
}

/// <summary>A connection that reads and locks through the storage, and stages its transactions in the context.</summary>
internal sealed class StagingConnection(EfConnection connection, DbContext context) : JobStorageConnection
{
    /// <summary>
    /// The shortest time a reserved id lives unsaved. The reservation is committed at once, outside the context,
    /// and the expiration manager deletes it once it expires: a context saved after that would fail.
    /// </summary>
    internal static readonly TimeSpan MinimumReservation = TimeSpan.FromDays(1);

    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    public override void Dispose()
    {
        connection.Dispose();
        base.Dispose();
    }

    public override IWriteOnlyTransaction CreateWriteTransaction() => new StagingTransaction(connection, context);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        // The reservation writes on another connection: SQLite would make it wait for the context's own write
        // lock until the busy timeout, then fail
        if (context.Database.CurrentTransaction != null && context.Database.ProviderName == SqliteProvider)
        {
            throw new InvalidOperationException(
                "A job can't be staged on SQLite while the context's transaction is open: SQLite has one writer at a time, and the job's id is reserved outside that transaction. Stage the job before BeginTransaction, or enqueue it through the storage once the transaction committed.");
        }

        return connection.CreateExpiredJob(job, parameters, createdAt, expireIn < MinimumReservation ? MinimumReservation : expireIn);
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Staged clients create jobs; servers fetch them through the storage.");
    }

    public override void SetJobParameter(string id, string name, string? value) => connection.SetJobParameter(id, name, value);

    public override string? GetJobParameter(string id, string name) => connection.GetJobParameter(id, name);

    public override JobData? GetJobData(string jobId) => connection.GetJobData(jobId);

    public override StateData? GetStateData(string jobId) => connection.GetStateData(jobId);

    public override void AnnounceServer(string serverId, ServerContext context) => connection.AnnounceServer(serverId, context);

    public override void RemoveServer(string serverId) => connection.RemoveServer(serverId);

    public override void Heartbeat(string serverId) => connection.Heartbeat(serverId);

    public override int RemoveTimedOutServers(TimeSpan timeOut) => connection.RemoveTimedOutServers(timeOut);

    public override HashSet<string> GetAllItemsFromSet(string key) => connection.GetAllItemsFromSet(key);

    public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => connection.GetFirstByLowestScoreFromSet(key, fromScore, toScore);

    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count) => connection.GetFirstByLowestScoreFromSet(key, fromScore, toScore, count);

    public override long GetSetCount(string key) => connection.GetSetCount(key);

    public override long GetSetCount(IEnumerable<string> keys, int limit) => connection.GetSetCount(keys, limit);

    public override bool GetSetContains(string key, string value) => connection.GetSetContains(key, value);

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt) => connection.GetRangeFromSet(key, startingFrom, endingAt);

    public override TimeSpan GetSetTtl(string key) => connection.GetSetTtl(key);

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => connection.SetRangeInHash(key, keyValuePairs);

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key) => connection.GetAllEntriesFromHash(key);

    public override string? GetValueFromHash(string key, string name) => connection.GetValueFromHash(key, name);

    public override long GetHashCount(string key) => connection.GetHashCount(key);

    public override TimeSpan GetHashTtl(string key) => connection.GetHashTtl(key);

    public override long GetListCount(string key) => connection.GetListCount(key);

    public override List<string> GetAllItemsFromList(string key) => connection.GetAllItemsFromList(key);

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt) => connection.GetRangeFromList(key, startingFrom, endingAt);

    public override TimeSpan GetListTtl(string key) => connection.GetListTtl(key);

    public override long GetCounter(string key) => connection.GetCounter(key);
}

/// <summary>A transaction whose writes become tracked changes of the context, saved by the application.</summary>
internal sealed class StagingTransaction(EfConnection connection, DbContext context) : RecordingTransaction
{
    protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    protected override void Apply(IReadOnlyList<StorageOperation> operations)
    {
        EfLimits.EnsureFits(operations);
        EfOperationWriter writer = new(context, immediate: false);
        foreach (StorageOperation operation in operations) writer.Write(operation);
    }
}
