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
    private static readonly ConditionalWeakTable<DbContext, EntityFrameworkCoreStorage> Subscribed = [];

    /// <param name="context">The application's context, whose model includes <c>ApplyNightShiftModel</c>.</param>
    extension([NotNull] DbContext context)
    {
        /// <summary>
        /// A client whose jobs are staged in the context. A job's id is reserved at once, in a row that expires by
        /// itself unless the context saves the job; its state, queue entry and every other write of its creation
        /// wait for the context's <c>SaveChanges</c> (or its transaction) and commit or roll back with it.
        /// Client filters run when the job is staged, as they do when it is created.
        /// </summary>
        /// <param name="storage">The storage, whose tables are in the context's model.</param>
        /// <returns>A client to create jobs with, through any of its extension methods.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The context's model doesn't include the storage's tables.</exception>
        public IBackgroundJobClient NightShift([NotNull] EntityFrameworkCoreStorage storage)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(storage);
            EntityFrameworkCoreStorage.EnsureModel(context);

            // Saving the context wakes this process's workers, as enqueuing through the storage does
            if (!Subscribed.TryGetValue(context, out _))
            {
                Subscribed.AddOrUpdate(context, storage);
                context.SavedChanges += static (sender, _) =>
                {
                    if (sender is DbContext saved && Subscribed.TryGetValue(saved, out EntityFrameworkCoreStorage? owner)) owner.Signal();
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
    public override void Dispose()
    {
        connection.Dispose();
        base.Dispose();
    }

    public override IWriteOnlyTransaction CreateWriteTransaction() => new StagingTransaction(connection, context);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        return connection.CreateExpiredJob(job, parameters, createdAt, expireIn);
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
        EfOperationWriter writer = new(context, immediate: false);
        foreach (StorageOperation operation in operations) writer.Write(operation);
    }
}
