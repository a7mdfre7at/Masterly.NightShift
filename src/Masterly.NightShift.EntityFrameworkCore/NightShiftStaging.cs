using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Transactions;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

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
        /// Staging takes part in the context's transaction. While the context has an open transaction
        /// (<c>BeginTransaction</c>, <c>UseTransaction</c>) or is inside a <see cref="TransactionScope"/>, what the
        /// client and its filters read or write about the jobs, sets, hashes, lists and counters staged through
        /// the context in that transaction runs in it, on the context's connection: a continuation of a job
        /// staged and saved earlier in the same transaction is registered on it there, and commits or rolls back
        /// with it. Everything else the filters read or write, the locks they take, and the reservation of the id
        /// run on connections of the storage, outside the transaction, as without one.
        /// </para>
        /// <para>
        /// Work that runs once a job's creation is committed (alerts, activity feed entries and other
        /// after-commit work of filters) runs when the job is staged, not when the context is saved, and runs
        /// even if the context is never saved. A <c>SaveChanges</c> that saves staged queue entries wakes this
        /// process's waiting workers: at once without a transaction, and once the transaction commits with one
        /// (a transaction the context began, or a <see cref="TransactionScope"/>; with a database transaction the
        /// application commits itself, at the workers' next poll). Other processes see the jobs at their next
        /// poll.
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
            if (!Subscribed.TryGetValue(context, out StagingSubscription? subscription))
            {
                subscription = new StagingSubscription(storage);
                Subscribed.AddOrUpdate(context, subscription);
                TransactionEnds.Watch();
                context.SavingChanges += static (sender, _) =>
                {
                    if (sender is DbContext saving && Subscribed.TryGetValue(saving, out StagingSubscription? saved))
                    {
                        saved.QueuesSaving = saving.ChangeTracker.Entries<JobQueueRow>().Any(static x => x.State == EntityState.Added);
                    }
                };
                context.SavedChanges += static (sender, _) =>
                {
                    if (sender is DbContext saved && Subscribed.TryGetValue(saved, out StagingSubscription? subscription) && subscription.QueuesSaving)
                    {
                        subscription.QueuesSaving = false;
                        subscription.SignalOnceCommitted(saved);
                    }
                };
                context.SaveChangesFailed += static (sender, _) =>
                {
                    if (sender is DbContext failed && Subscribed.TryGetValue(failed, out StagingSubscription? subscription))
                    {
                        subscription.QueuesSaving = false;
                    }
                };
            }

            return new BackgroundJobClient(new StagingStorage(storage, context, subscription));
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

    /// <summary>The subscription of a context that stages jobs, if any.</summary>
    internal static StagingSubscription? SubscriptionOf(DbContext context) => Subscribed.TryGetValue(context, out StagingSubscription? subscription) ? subscription : null;
}

/// <summary>
/// What a context that stages jobs needs between its calls: the storage, whether the save in progress writes
/// queue entries, the transactions whose commit is to wake the workers, and what was staged in the context's
/// current transaction. A context is used by one thread at a time, but a transaction may end on another.
/// </summary>
internal sealed class StagingSubscription(EntityFrameworkCoreStorage storage)
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    private readonly NightShiftLock _syncRoot = new();
    private readonly HashSet<Guid> _pendingCommits = [];
    private readonly HashSet<string> _pendingScopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _staged = new(StringComparer.Ordinal);
    private string? _stagedIn;

    public EntityFrameworkCoreStorage Storage { get; } = storage;

    public bool QueuesSaving { get; set; }

    /// <summary>
    /// The transaction the context takes part in, as a text that tells one from another: one it began or was
    /// given, or the ambient one. <c>null</c> without one, and on SQLite, which can't take part in an ambient
    /// transaction and refuses staging while the context's own is open.
    /// </summary>
    public static string? TransactionOf(DbContext context)
    {
        if (context.Database.ProviderName == SqliteProvider)
        {
            return null;
        }

        if (context.Database.CurrentTransaction is { } transaction)
        {
            return $"database:{transaction.TransactionId:N}";
        }

        return Transaction.Current is { } ambient ? $"scope:{ambient.TransactionInformation.LocalIdentifier}" : null;
    }

    /// <summary>Whether a resource was staged through the context in <paramref name="transaction"/>.</summary>
    public bool Staged(string transaction, string resource)
    {
        lock (_syncRoot)
        {
            return _stagedIn == transaction && _staged.Contains(resource);
        }
    }

    /// <summary>Records resources staged through the context in <paramref name="transaction"/>, forgetting those of an earlier one.</summary>
    public void Stage(string transaction, IEnumerable<string> resources)
    {
        lock (_syncRoot)
        {
            if (_stagedIn != transaction)
            {
                _staged.Clear();
                _stagedIn = transaction;
            }

            _staged.UnionWith(resources);
        }
    }

    /// <summary>
    /// Wakes the workers once the queue entries just saved are committed: at once without a transaction; when
    /// a transaction the context began commits (told by EF Core's diagnostics, see <see cref="TransactionEnds"/>);
    /// when a <see cref="TransactionScope"/> commits. A transaction the application began on the connection
    /// itself tells nobody: the workers find the jobs at their next poll.
    /// </summary>
    public void SignalOnceCommitted(DbContext context)
    {
        if (context.Database.ProviderName != SqliteProvider && context.Database.CurrentTransaction is { } transaction)
        {
            lock (_syncRoot)
            {
                _pendingCommits.Add(transaction.TransactionId);
            }

            return;
        }

        if (context.Database.ProviderName != SqliteProvider && Transaction.Current is { } ambient)
        {
            string id = ambient.TransactionInformation.LocalIdentifier;
            lock (_syncRoot)
            {
                if (!_pendingScopes.Add(id))
                {
                    return;
                }
            }

            ambient.TransactionCompleted += (_, e) =>
            {
                lock (_syncRoot)
                {
                    _pendingScopes.Remove(id);
                }

                if (e.Transaction?.TransactionInformation.Status == TransactionStatus.Committed)
                {
                    Storage.Signal();
                }
            };
            return;
        }

        Storage.Signal();
    }

    /// <summary>A transaction of the context ended: wakes the workers if it committed queue entries.</summary>
    public void Ended(Guid transactionId, bool committed)
    {
        bool pending;
        lock (_syncRoot)
        {
            pending = _pendingCommits.Remove(transactionId);
        }

        if (pending && committed)
        {
            Storage.Signal();
        }
    }
}

/// <summary>
/// Tells the subscriptions when a transaction of their context commits or ends otherwise, from EF Core's
/// diagnostics (<see cref="DiagnosticListener"/>), which every context reports to whatever its options: a
/// context's events can't be subscribed to otherwise without an interceptor in its options, which belong to the
/// application. Only the three events of a transaction's end are asked for.
/// </summary>
internal sealed class TransactionEnds : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly string Committed = RelationalEventId.TransactionCommitted.Name!; // Every event id has a name
    private static readonly string RolledBack = RelationalEventId.TransactionRolledBack.Name!; // Every event id has a name
    private static readonly string Disposed = RelationalEventId.TransactionDisposed.Name!; // Every event id has a name
    private static readonly Lazy<IDisposable> Subscription = new(static () => DiagnosticListener.AllListeners.Subscribe(new TransactionEnds()));

    private TransactionEnds()
    {
    }

    /// <summary>Starts watching, once per process.</summary>
    public static void Watch() => _ = Subscription.Value;

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == DbLoggerCategory.Name)
        {
            value.Subscribe(this, static (name, _, _) => name == Committed || name == RolledBack || name == Disposed);
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Value is TransactionEventData { Context: { } context } data && NightShiftDbContextExtensions.SubscriptionOf(context) is { } subscription)
        {
            subscription.Ended(data.TransactionId, committed: value.Key == Committed);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}

/// <summary>The storage, with writes staged in an application's context.</summary>
internal sealed class StagingStorage(EntityFrameworkCoreStorage storage, DbContext context, StagingSubscription subscription) : JobStorage
{
    public override IMonitoringApi GetMonitoringApi() => storage.GetMonitoringApi();

    public override IStorageConnection GetConnection() => new StagingConnection(storage, (EfConnection)storage.GetConnection(), context, subscription);

    public override bool HasFeature(string featureId) => storage.HasFeature(featureId);

    public override string ToString() => storage.ToString();
}

/// <summary>
/// A connection that locks through the storage, stages its transactions in the context, and reads and writes
/// at once either through the storage, or, for what was staged in the context's open transaction, in that
/// transaction (see <see cref="NightShiftDbContextExtensions"/>).
/// </summary>
internal sealed class StagingConnection(EntityFrameworkCoreStorage storage, EfConnection connection, DbContext context, StagingSubscription subscription) : JobStorageConnection
{
    /// <summary>
    /// The shortest time a reserved id lives unsaved. The reservation is committed at once, outside the context,
    /// and the expiration manager deletes it once it expires: a context saved after that would fail.
    /// </summary>
    internal static readonly TimeSpan MinimumReservation = TimeSpan.FromDays(1);

    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    private readonly EntityFrameworkCoreStorage _storage = storage;
    private readonly EfConnection _connection = connection;
    private readonly DbContext _context = context;
    private readonly StagingSubscription _subscription = subscription;

    // The application's transaction when the client was asked for a connection: a client's call is one unit
    private readonly string? _transaction = StagingSubscription.TransactionOf(context);
    private EfConnection? _within;

    public override void Dispose()
    {
        _connection.Dispose();
        _within?.Dispose();
        base.Dispose();
    }

    public override IWriteOnlyTransaction CreateWriteTransaction() => new StagingTransaction(_connection, _context, _subscription, _transaction);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => _connection.AcquireDistributedLock(resource, timeout);

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        // The reservation writes on another connection: SQLite would make it wait for the context's own write
        // lock until the busy timeout, then fail
        if (_context.Database.CurrentTransaction != null && _context.Database.ProviderName == SqliteProvider)
        {
            throw new InvalidOperationException(
                "A job can't be staged on SQLite while the context's transaction is open: SQLite has one writer at a time, and the job's id is reserved outside that transaction. Stage the job before BeginTransaction, or enqueue it through the storage once the transaction committed.");
        }

        return _connection.CreateExpiredJob(job, parameters, createdAt, expireIn < MinimumReservation ? MinimumReservation : expireIn);
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Staged clients create jobs; servers fetch them through the storage.");
    }

    // The staging storage reports the storage's features
    public override string? GetQueueEntryId(IFetchedJob fetchedJob) => _connection.GetQueueEntryId(fetchedJob);

    public override void SetJobParameter(string id, string name, string? value) => ForJob(id).SetJobParameter(id, name, value);

    public override string? GetJobParameter(string id, string name) => ForJob(id).GetJobParameter(id, name);

    public override JobData? GetJobData(string jobId) => ForJob(jobId).GetJobData(jobId);

    public override StateData? GetStateData(string jobId) => ForJob(jobId).GetStateData(jobId);

    public override void AnnounceServer(string serverId, ServerContext context) => _connection.AnnounceServer(serverId, context);

    public override void RemoveServer(string serverId) => _connection.RemoveServer(serverId);

    public override void Heartbeat(string serverId) => _connection.Heartbeat(serverId);

    public override int RemoveTimedOutServers(TimeSpan timeOut) => _connection.RemoveTimedOutServers(timeOut);

    public override HashSet<string> GetAllItemsFromSet(string key) => For(StagedResources.Set(key)).GetAllItemsFromSet(key);

    public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => For(StagedResources.Set(key)).GetFirstByLowestScoreFromSet(key, fromScore, toScore);

    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count) => For(StagedResources.Set(key)).GetFirstByLowestScoreFromSet(key, fromScore, toScore, count);

    public override long GetSetCount(string key) => For(StagedResources.Set(key)).GetSetCount(key);

    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string[] names = [.. keys];
        return For(names.Select(StagedResources.Set)).GetSetCount(names, limit);
    }

    public override bool GetSetContains(string key, string value) => For(StagedResources.Set(key)).GetSetContains(key, value);

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt) => For(StagedResources.Set(key)).GetRangeFromSet(key, startingFrom, endingAt);

    public override TimeSpan GetSetTtl(string key) => For(StagedResources.Set(key)).GetSetTtl(key);

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => For(StagedResources.Hash(key)).SetRangeInHash(key, keyValuePairs);

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key) => For(StagedResources.Hash(key)).GetAllEntriesFromHash(key);

    public override string? GetValueFromHash(string key, string name) => For(StagedResources.Hash(key)).GetValueFromHash(key, name);

    public override long GetHashCount(string key) => For(StagedResources.Hash(key)).GetHashCount(key);

    public override TimeSpan GetHashTtl(string key) => For(StagedResources.Hash(key)).GetHashTtl(key);

    public override long GetListCount(string key) => For(StagedResources.List(key)).GetListCount(key);

    public override List<string> GetAllItemsFromList(string key) => For(StagedResources.List(key)).GetAllItemsFromList(key);

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt) => For(StagedResources.List(key)).GetRangeFromList(key, startingFrom, endingAt);

    public override TimeSpan GetListTtl(string key) => For(StagedResources.List(key)).GetListTtl(key);

    public override long GetCounter(string key) => For(StagedResources.Counter(key)).GetCounter(key);

    // The database's clock, or the process's: the same for every connection, and never in a transaction
    public override DateTime GetUtcDateTime() => _connection.GetUtcDateTime();

    private EfConnection ForJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return StorageValues.TryParseJobId(jobId, out long id) ? For(StagedResources.Job(id)) : _connection;
    }

    private EfConnection For(string resource) => _transaction != null && _subscription.Staged(_transaction, resource) ? Within() : _connection;

    private EfConnection For(IEnumerable<string> resources) => _transaction != null && resources.Any(x => _subscription.Staged(_transaction, x)) ? Within() : _connection;

    // The storage's reads and writes, on contexts that share the application's connection and transaction
    private EfConnection Within() => _within ??= new EfConnection(_storage, Borrow);

    /// <summary>
    /// A context of the storage on the application's connection, in its open transaction: one the context
    /// began or was given, or, inside a <see cref="TransactionScope"/>, the ambient one, which the connection
    /// joins when it opens. It doesn't own the connection.
    /// </summary>
    private DbContext Borrow()
    {
        DbContext borrowed = _storage.CreateContext();
        try
        {
            borrowed.Database.SetDbConnection(_context.Database.GetDbConnection(), contextOwnsConnection: false);
            if (_context.Database.CurrentTransaction is { } transaction)
            {
                borrowed.Database.UseTransaction(transaction.GetDbTransaction());
            }

            return borrowed;
        }
        catch
        {
            borrowed.Dispose();
            throw;
        }
    }
}

/// <summary>A transaction whose writes become tracked changes of the context, saved by the application.</summary>
internal sealed class StagingTransaction(EfConnection connection, DbContext context, StagingSubscription subscription, string? transaction) : RecordingTransaction
{
    protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    // The record of a chain, which a client writes before the first step of a chain it creates, retries or replaces
    private const string ChainRecordPrefix = "chain:";
    private const string ChainJobsField = "Jobs";

    protected override void Apply(IReadOnlyList<StorageOperation> operations)
    {
        EfLimits.EnsureFits(operations);
        if (operations.Any(static x => x is SetRangeInHashOperation hash
                && hash.Key.StartsWith(ChainRecordPrefix, StringComparison.Ordinal)
                && hash.Fields.Any(static y => y.Key == ChainJobsField)))
        {
            // Nothing of the chain exists yet: its record is written before its first step
            throw new NotSupportedException(
                "A job chain can't be staged in a context: the client checks the steps it has created, which the context only writes when it is saved. Create the chain with a client of the storage (new BackgroundJobClient(storage)) once the context is saved and its transaction committed.");
        }

        EfOperationWriter writer = new(context, immediate: false);
        foreach (StorageOperation operation in operations)
        {
            writer.Write(operation);
        }

        // What was staged in the context's transaction is read and written in it from here on
        if (transaction != null)
        {
            subscription.Stage(transaction, operations.SelectMany(StagedResources.Of));
        }
    }
}

/// <summary>Names what a staged operation writes, so reads and writes of it can be told apart from others.</summary>
internal static class StagedResources
{
    public static string Job(long id) => $"job:{id.ToString(CultureInfo.InvariantCulture)}";

    public static string Set(string key) => $"set:{key}";

    public static string Hash(string key) => $"hash:{key}";

    public static string List(string key) => $"list:{key}";

    public static string Counter(string key) => $"counter:{key}";

    public static IEnumerable<string> Of(StorageOperation operation)
    {
        return operation switch
        {
            ExpireJobOperation expire => [Job(expire.JobId)],
            AddJobStateOperation state => [Job(state.JobId)],
            AddToQueueOperation queue => [Job(queue.JobId)],
            SetJobParameterOperation parameter => [Job(parameter.JobId)],
            CounterOperation counter => [Counter(counter.Key)],
            AddToSetOperation add => [Set(add.Key)],
            AddRangeToSetOperation range => [Set(range.Key)],
            RemoveFromSetOperation remove => [Set(remove.Key)],
            RemoveSetOperation remove => [Set(remove.Key)],
            InsertToListOperation insert => [List(insert.Key)],
            RemoveFromListOperation remove => [List(remove.Key)],
            TrimListOperation trim => [List(trim.Key)],
            SetRangeInHashOperation hash => [Hash(hash.Key)],
            RemoveHashOperation remove => [Hash(remove.Key)],
            ExpireKeyOperation expire => [Of(expire.Kind, expire.Key)],
            _ => [],
        };
    }

    private static string Of(KeyKind kind, string key)
    {
        return kind switch
        {
            KeyKind.Set => Set(key),
            KeyKind.List => List(key),
            _ => Hash(key),
        };
    }
}
