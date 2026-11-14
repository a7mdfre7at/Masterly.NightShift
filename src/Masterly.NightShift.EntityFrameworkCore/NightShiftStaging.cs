using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Transactions;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        /// job is staged. SQLite has one writer at a time: while the context's transaction is open, anything the
        /// storage writes outside it would wait for it until the busy timeout, so staging then throws at once.
        /// Stage and save before <c>BeginTransaction</c>, or enqueue through the storage once the transaction
        /// committed.
        /// </para>
        /// <para>
        /// Staging takes part in the context's transaction. While the context has an open transaction
        /// (<c>BeginTransaction</c>, <c>UseTransaction</c>) or is inside a <see cref="TransactionScope"/>, what the
        /// client and its filters read or write about the jobs, sets, hashes, lists and counters saved in that
        /// transaction runs in it, on the context's connection, by whichever context of the application saved
        /// them. Everything else the filters read or write, the locks they take and the reservation of the id run
        /// on connections of the storage, outside the transaction.
        /// </para>
        /// <para>
        /// A continuation or a join of jobs that the context didn't stage is registered on its parents once the
        /// staged continuation is committed: when the context saves it without a transaction, or when the
        /// transaction or scope commits. It then starts at once if its parents already finished. A continuation
        /// rolled back, or never saved, is never registered, and its parents never wait for it. Should the
        /// process stop between the commit and the registration, a server registers it within a minute or two.
        /// </para>
        /// <para>
        /// A job with <see cref="PreventDuplicatesAttribute"/> reserves its fingerprint at once, outside the
        /// context's transaction, as a job created through the storage does: a second copy staged before the
        /// first is saved, or created by any process meanwhile, is refused. Should the context never be saved, or
        /// its transaction roll back, the reservation lapses as one whose creation stopped does (2 minutes).
        /// </para>
        /// <para>
        /// Work that runs once a job's creation is committed (alerts, activity feed entries and other
        /// after-commit work of filters) runs when the job is staged, not when the context is saved, and runs
        /// even if the context is never saved. A <c>SaveChanges</c> that saves staged queue entries wakes this
        /// process's waiting workers: at once without a transaction, and once the transaction commits with one
        /// (a transaction the context began, or a <see cref="TransactionScope"/>; with a database transaction the
        /// application commits itself, at the workers' next poll). Other processes see the jobs at their next
        /// poll. A job chain can't be staged.
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

            if (!Subscribed.TryGetValue(context, out StagingSubscription? subscription))
            {
                subscription = new StagingSubscription(storage);
                Subscribed.AddOrUpdate(context, subscription);
                TransactionEnds.Watch();
                context.SavingChanges += static (sender, _) =>
                {
                    if (sender is DbContext saving && Subscribed.TryGetValue(saving, out StagingSubscription? saved))
                    {
                        saved.Saving(saving);
                    }
                };
                context.SavedChanges += static (sender, _) =>
                {
                    if (sender is DbContext saved && Subscribed.TryGetValue(saved, out StagingSubscription? subscription))
                    {
                        subscription.Saved(saved);
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
}

/// <summary>
/// What a context that stages jobs needs between its calls: the storage, whether the save in progress writes
/// queue entries, and the continuations it staged that wait for their commit to be registered. A context is used
/// by one thread at a time.
/// </summary>
internal sealed class StagingSubscription(EntityFrameworkCoreStorage storage)
{
    private readonly List<string> _continuations = [];

    public EntityFrameworkCoreStorage Storage { get; } = storage;

    public bool QueuesSaving { get; set; }

    /// <summary>A continuation staged whose registration on its parents waits for its commit.</summary>
    public void Defer(string continuationId) => _continuations.Add(continuationId);

    /// <summary>Before a save: what it writes to the storage's tables is recorded for the transaction it is in.</summary>
    public void Saving(DbContext context)
    {
        QueuesSaving = context.ChangeTracker.Entries<JobQueueRow>().Any(static x => x.State == EntityState.Added);

        if (ApplicationTransaction.Of(context) is { } transaction)
        {
            transaction.Record(StorageRows.Written(context));
        }
    }

    /// <summary>
    /// After a save: the storage's rows are forgotten (the database now has them, and a later staged write must
    /// read what it has then, not what was saved here), and what waits for the commit is handed to it.
    /// </summary>
    public void Saved(DbContext context)
    {
        StorageRows.Forget(context, everyState: true);

        if (QueuesSaving)
        {
            QueuesSaving = false;
            ApplicationTransaction.OnceCommitted(context, Storage.Signal);
        }

        if (_continuations.Count > 0)
        {
            string[] continuations = [.. _continuations];
            _continuations.Clear();
            EntityFrameworkCoreStorage storage = Storage;
            ApplicationTransaction.OnceCommitted(context, () => StagedContinuations.Register(storage, continuations));
        }
    }
}

/// <summary>
/// The transaction an application's context takes part in, and what was saved in it to the storage's tables, by
/// any context: one transaction of the database (<see cref="DbTransaction"/>, shared by every context that uses
/// it), or an ambient <see cref="TransactionScope"/>. SQLite is never in one here: it can't take part in an
/// ambient transaction, and staging refuses its own open transaction.
/// </summary>
internal sealed class ApplicationTransaction
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    private static readonly ConditionalWeakTable<DbTransaction, ApplicationTransaction> Database = [];
    private static readonly ConcurrentDictionary<string, ApplicationTransaction> Scopes = new(StringComparer.Ordinal);

    private readonly NightShiftLock _syncRoot = new();
    private readonly HashSet<string> _saved = new(StringComparer.Ordinal);
    private readonly List<Action> _onceCommitted = [];

    private ApplicationTransaction()
    {
    }

    /// <summary>The transaction the context is in, or <c>null</c>.</summary>
    public static ApplicationTransaction? Of(DbContext context)
    {
        if (context.Database.ProviderName == SqliteProvider)
        {
            return null;
        }

        if (context.Database.CurrentTransaction is { } transaction)
        {
            return Database.GetValue(transaction.GetDbTransaction(), static _ => new ApplicationTransaction());
        }

        if (Transaction.Current is not { } ambient)
        {
            return null;
        }

        string id = ambient.TransactionInformation.LocalIdentifier;
        if (Scopes.TryGetValue(id, out ApplicationTransaction? known))
        {
            return known;
        }

        ApplicationTransaction created = new();
        if (!Scopes.TryAdd(id, created))
        {
            return Scopes[id];
        }

        ambient.TransactionCompleted += (_, e) =>
        {
            Scopes.TryRemove(id, out ApplicationTransaction? _);
            created.End(e.Transaction?.TransactionInformation.Status == TransactionStatus.Committed);
        };
        return created;
    }

    /// <summary>
    /// Runs <paramref name="action"/> once what the context saved is committed: now without a transaction, when
    /// its transaction or scope commits otherwise, and never if it doesn't. A transaction the application commits
    /// on the connection itself tells nobody: the action never runs then.
    /// </summary>
    public static void OnceCommitted(DbContext context, Action action)
    {
        if (Of(context) is { } transaction)
        {
            lock (transaction._syncRoot)
            {
                transaction._onceCommitted.Add(action);
            }

            return;
        }

        action();
    }

    /// <summary>The transaction of the database was committed, or ended otherwise.</summary>
    public static void Ended(DbTransaction transaction, bool committed)
    {
        if (Database.TryGetValue(transaction, out ApplicationTransaction? known))
        {
            Database.Remove(transaction);
            known.End(committed);
        }
    }

    /// <summary>Records resources saved in the transaction.</summary>
    public void Record(IEnumerable<string> resources)
    {
        lock (_syncRoot)
        {
            _saved.UnionWith(resources);
        }
    }

    /// <summary>Whether a resource was saved in the transaction: its rows are there, and maybe locked by it.</summary>
    public bool Saved(string resource)
    {
        lock (_syncRoot)
        {
            return _saved.Contains(resource);
        }
    }

    private void End(bool committed)
    {
        Action[] actions;
        lock (_syncRoot)
        {
            actions = [.. _onceCommitted];
            _onceCommitted.Clear();
            _saved.Clear();
        }

        if (!committed)
        {
            return;
        }

        foreach (Action action in actions)
        {
            action();
        }
    }
}

/// <summary>
/// Tells when a transaction of the database ends, from EF Core's diagnostics (<see cref="DiagnosticListener"/>),
/// which every context reports to whatever its options: a context's events can't be subscribed to otherwise
/// without an interceptor in its options, which belong to the application. Only the three events of a
/// transaction's end are asked for.
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
        if (value.Value is TransactionEventData data)
        {
            ApplicationTransaction.Ended(data.Transaction, committed: value.Key == Committed);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}

/// <summary>The storage's rows in an application's context.</summary>
internal static class StorageRows
{
    private static readonly HashSet<Type> Types =
    [
        typeof(JobRow), typeof(StateRow), typeof(JobParameterRow), typeof(JobQueueRow), typeof(SetRow), typeof(ListRow),
        typeof(HashRow), typeof(CounterRow), typeof(AggregatedCounterRow), typeof(LockRow), typeof(ServerRow), typeof(SchemaVersionRow),
    ];

    /// <summary>
    /// Stops tracking the storage's rows the context holds unchanged (or, after a save, every one of them): a
    /// staged write then reads what the database has, never a row the context loaded or saved earlier, which
    /// another process may have changed or deleted since, and which EF Core would compare the write with.
    /// </summary>
    public static void Forget(DbContext context, bool everyState)
    {
        foreach (EntityEntry entry in context.ChangeTracker.Entries().ToList())
        {
            if (Types.Contains(entry.Metadata.ClrType) && (everyState || entry.State == EntityState.Unchanged))
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>Detects the changes of the storage's rows the context tracks, and of no other entity.</summary>
    public static void DetectChanges(DbContext context)
    {
        foreach (EntityEntry entry in context.ChangeTracker.Entries().ToList())
        {
            if (Types.Contains(entry.Metadata.ClrType))
            {
                entry.DetectChanges();
            }
        }
    }

    /// <summary>What the context is about to write to the storage's tables.</summary>
    public static List<string> Written(DbContext context)
    {
        List<string> written = [];
        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && Resource(entry.Entity) is { } resource)
            {
                written.Add(resource);
            }
        }

        return written;
    }

    /// <summary>Whether the context holds writes of the job that it hasn't saved yet.</summary>
    public static bool Pending(DbContext context, long jobId)
    {
        string job = StagedResources.Job(jobId);
        return context.ChangeTracker.Entries().Any(x =>
            x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && Resource(x.Entity) == job);
    }

    private static string? Resource(object row)
    {
        return row switch
        {
            JobRow job => StagedResources.Job(job.Id),
            StateRow state => StagedResources.Job(state.JobId),
            JobQueueRow queue => StagedResources.Job(queue.JobId),
            JobParameterRow parameter => StagedResources.Job(parameter.JobId),
            SetRow set => StagedResources.Set(set.Key),
            ListRow list => StagedResources.List(list.Key),
            HashRow hash => StagedResources.Hash(hash.Key),
            CounterRow counter => StagedResources.Counter(counter.Key),
            AggregatedCounterRow counter => StagedResources.Counter(counter.Key),
            _ => null,
        };
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
/// at once either through the storage, or, for what was saved in the context's open transaction, in that
/// transaction (see <see cref="NightShiftDbContextExtensions"/>).
/// </summary>
internal sealed class StagingConnection(EntityFrameworkCoreStorage storage, EfConnection connection, DbContext context, StagingSubscription subscription) : JobStorageConnection
{
    /// <summary>
    /// The shortest time a reserved id lives unsaved. The reservation is committed at once, outside the context,
    /// and the expiration manager deletes it once it expires: a context saved after that would fail.
    /// </summary>
    internal static readonly TimeSpan MinimumReservation = TimeSpan.FromDays(1);

    // The parameters a continuation and a join write on their parents to register (ContinuationsSupportAttribute,
    // FanInContinuationsSupportAttribute)
    private const string ContinuationsParameter = "Continuations";
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    private readonly EntityFrameworkCoreStorage _storage = storage;
    private readonly EfConnection _connection = connection;
    private readonly DbContext _context = context;
    private readonly StagingSubscription _subscription = subscription;

    // The application's transaction when the client was asked for a connection: a client's call is one unit
    private readonly ApplicationTransaction? _transaction = ApplicationTransaction.Of(context);
    private EfConnection? _within;

    public override void Dispose()
    {
        _connection.Dispose();
        _within?.Dispose();
        base.Dispose();
    }

    public override IWriteOnlyTransaction CreateWriteTransaction() => new StagingTransaction(_storage, _connection, _context, _subscription, this);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        EnsureCanWriteOutside();
        return _connection.AcquireDistributedLock(resource, timeout);
    }

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        EnsureCanWriteOutside();
        return _connection.CreateExpiredJob(job, parameters, createdAt, expireIn < MinimumReservation ? MinimumReservation : expireIn);
    }

    /// <summary>
    /// Refuses at once what the storage would write outside the context's open transaction on SQLite: SQLite
    /// has one writer at a time, so it would wait for that transaction until the busy timeout, then fail.
    /// </summary>
    /// <exception cref="InvalidOperationException">The context is on SQLite and its transaction is open.</exception>
    internal void EnsureCanWriteOutside()
    {
        if (_context.Database.CurrentTransaction != null && _context.Database.ProviderName == SqliteProvider)
        {
            throw new InvalidOperationException(
                "A job can't be staged on SQLite while the context's transaction is open: SQLite has one writer at a time, and the storage writes the job's id, its locks and its reservations outside that transaction. Stage and save the job before BeginTransaction, or enqueue it through the storage once the transaction committed.");
        }
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Staged clients create jobs; servers fetch them through the storage.");
    }

    public override void SetJobParameter(string id, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);

        // A continuation or a join registers on a parent the application didn't stage once it is committed
        // itself (StagedContinuations): registered now, a parent that finishes first would wait for a
        // continuation that may never be saved, or start nothing when it is saved later
        if (name is ContinuationsParameter or FanInContinuationsSupportAttribute.ParameterName && !IsTheApplications(id))
        {
            return;
        }

        EnsureCanWriteOutside();
        RouteJob(id, x => x.SetJobParameter(id, name, value));
    }

    public override string? GetJobParameter(string id, string name) => RouteJob(id, x => x.GetJobParameter(id, name));

    public override JobData? GetJobData(string jobId) => RouteJob(jobId, x => x.GetJobData(jobId));

    public override StateData? GetStateData(string jobId) => RouteJob(jobId, x => x.GetStateData(jobId));

    public override void AnnounceServer(string serverId, ServerContext context) => _connection.AnnounceServer(serverId, context);

    public override void RemoveServer(string serverId) => _connection.RemoveServer(serverId);

    public override void Heartbeat(string serverId) => _connection.Heartbeat(serverId);

    public override int RemoveTimedOutServers(TimeSpan timeOut) => _connection.RemoveTimedOutServers(timeOut);

    public override HashSet<string> GetAllItemsFromSet(string key) => Route(StagedResources.Set(key), x => x.GetAllItemsFromSet(key));

    public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => Route(StagedResources.Set(key), x => x.GetFirstByLowestScoreFromSet(key, fromScore, toScore));

    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count) => Route(StagedResources.Set(key), x => x.GetFirstByLowestScoreFromSet(key, fromScore, toScore, count));

    public override long GetSetCount(string key) => Route(StagedResources.Set(key), x => x.GetSetCount(key));

    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string[] names = [.. keys];
        return Route(names.Select(StagedResources.Set), x => x.GetSetCount(names, limit));
    }

    public override bool GetSetContains(string key, string value) => Route(StagedResources.Set(key), x => x.GetSetContains(key, value));

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt) => Route(StagedResources.Set(key), x => x.GetRangeFromSet(key, startingFrom, endingAt));

    public override TimeSpan GetSetTtl(string key) => Route(StagedResources.Set(key), x => x.GetSetTtl(key));

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        EnsureCanWriteOutside();
        Route(StagedResources.Hash(key), x => x.SetRangeInHash(key, keyValuePairs));
    }

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key) => Route(StagedResources.Hash(key), x => x.GetAllEntriesFromHash(key));

    public override string? GetValueFromHash(string key, string name) => Route(StagedResources.Hash(key), x => x.GetValueFromHash(key, name));

    public override long GetHashCount(string key) => Route(StagedResources.Hash(key), x => x.GetHashCount(key));

    public override TimeSpan GetHashTtl(string key) => Route(StagedResources.Hash(key), x => x.GetHashTtl(key));

    public override long GetListCount(string key) => Route(StagedResources.List(key), x => x.GetListCount(key));

    public override List<string> GetAllItemsFromList(string key) => Route(StagedResources.List(key), x => x.GetAllItemsFromList(key));

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt) => Route(StagedResources.List(key), x => x.GetRangeFromList(key, startingFrom, endingAt));

    public override TimeSpan GetListTtl(string key) => Route(StagedResources.List(key), x => x.GetListTtl(key));

    public override long GetCounter(string key) => Route(StagedResources.Counter(key), x => x.GetCounter(key));

    // The database's clock, or the process's: the same for every connection, and never in a transaction
    public override DateTime GetUtcDateTime() => _connection.GetUtcDateTime();

    /// <summary>
    /// Whether a job is the application's: saved in its transaction, or staged in the context and not saved
    /// yet. What is written about it then goes with the application's rows, not ahead of them.
    /// </summary>
    internal bool IsTheApplications(string jobId)
    {
        return StorageValues.TryParseJobId(jobId, out long id)
            && ((_transaction != null && _transaction.Saved(StagedResources.Job(id))) || StorageRows.Pending(_context, id));
    }

    private T RouteJob<T>(string jobId, Func<EfConnection, T> call)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return StorageValues.TryParseJobId(jobId, out long id) ? Route(StagedResources.Job(id), call) : call(_connection);
    }

    private void RouteJob(string jobId, Action<EfConnection> call) => RouteJob(jobId, x =>
    {
        call(x);
        return true;
    });

    private T Route<T>(string resource, Func<EfConnection, T> call)
    {
        return _transaction != null && _transaction.Saved(resource) ? InTheTransaction(call) : call(_connection);
    }

    private T Route<T>(IEnumerable<string> resources, Func<EfConnection, T> call)
    {
        return _transaction != null && resources.Any(_transaction.Saved) ? InTheTransaction(call) : call(_connection);
    }

    private void Route(string resource, Action<EfConnection> call) => Route(resource, x =>
    {
        call(x);
        return true;
    });

    // The storage's reads and writes, on contexts that share the application's connection and transaction. A
    // retrying execution strategy can't run in a transaction it didn't begin: these run as one call nested in it.
    private T InTheTransaction<T>(Func<EfConnection, T> call)
    {
        using IDisposable direct = NestedExecution.Enter(_context);
        return call(_within ??= new EfConnection(_storage, Borrow));
    }

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

/// <summary>
/// Runs what EF Core executes meanwhile as nested in a call of an execution strategy, so a retrying strategy of
/// the storage's contexts executes it once, without retries, as it does the calls nested in its own: it refuses
/// otherwise to run in a transaction that the application began. A failure there belongs to the application.
/// </summary>
internal sealed class NestedExecution : ExecutionStrategy
{
    private NestedExecution(DbContext context)
        : base(context, 0, TimeSpan.Zero)
    {
    }

    /// <summary>Until the result is disposed, on this thread and its async flow.</summary>
    public static IDisposable Enter(DbContext context)
    {
        ExecutionStrategy? outer = Current;
        Current = new NestedExecution(context);
        return new Leave(outer);
    }

    protected override bool ShouldRetryOn(Exception exception) => false;

    private sealed class Leave(ExecutionStrategy? outer) : IDisposable
    {
        public void Dispose() => Current = outer;
    }
}

/// <summary>A transaction whose writes become tracked changes of the context, saved by the application.</summary>
internal sealed class StagingTransaction(EntityFrameworkCoreStorage storage, EfConnection connection, DbContext context, StagingSubscription subscription, StagingConnection staging) : RecordingTransaction
{
    // The record of a chain, which a client writes before the first step of a chain it creates, retries or replaces
    private const string ChainRecordPrefix = "chain:";
    private const string ChainJobsField = "Jobs";

    // A duplicate's fingerprint, reserved and released by PreventDuplicatesAttribute in transactions of their own
    private const string FingerprintPrefix = "fingerprint:";
    private const string ReservationField = "Reservation";

    protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

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

        if (IsDuplicateReservation(operations))
        {
            staging.EnsureCanWriteOutside();
            // Committed at once, outside the application's transaction, as for a job created through the storage:
            // seen by every copy staged after it and by every process. Not saved or rolled back, the job never
            // names it, and it lapses as the reservation of a creation that stopped does.
            EfTransaction.Apply(storage, operations);
            return;
        }

        // A row the context loaded or saved earlier may have changed since: the writer reads it again
        StorageRows.Forget(context, everyState: false);

        // The writer changes rows it found, so they must be tracked, whatever the application set for its own
        // queries; the setting is the application's again afterwards
        QueryTrackingBehavior tracking = context.ChangeTracker.QueryTrackingBehavior;
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        try
        {
            EfOperationWriter writer = new(context, immediate: false);
            foreach (StorageOperation operation in operations)
            {
                writer.Write(operation);
            }

            foreach (string continuation in ContinuationsOfOthers(operations))
            {
                // Committed with the continuation, so a server registers it should the process stop before it does
                writer.Write(new AddToSetOperation(StagedContinuations.Key, continuation, JobHelper.ToTimestamp(JobClock.UtcNow)));
                subscription.Defer(continuation);
            }
        }
        finally
        {
            context.ChangeTracker.QueryTrackingBehavior = tracking;
        }

        // A context that doesn't detect changes would save the rows the writer changed as they were: their
        // changes are detected now, and only theirs, so the application's own entities stay as it left them
        if (!context.ChangeTracker.AutoDetectChangesEnabled)
        {
            StorageRows.DetectChanges(context);
        }
    }

    // The reservation of a fingerprint, its release, or its return to the job it was to replace: transactions
    // that write nothing else. The record that names the new job goes with the job, staged.
    private static bool IsDuplicateReservation(IReadOnlyList<StorageOperation> operations)
    {
        return operations.Count > 0
            && operations.All(static x => x switch
            {
                SetRangeInHashOperation hash => hash.Key.StartsWith(FingerprintPrefix, StringComparison.Ordinal),
                RemoveHashOperation remove => remove.Key.StartsWith(FingerprintPrefix, StringComparison.Ordinal),
                ExpireKeyOperation expire => expire.Kind == KeyKind.Hash && expire.Key.StartsWith(FingerprintPrefix, StringComparison.Ordinal),
                _ => false,
            })
            && operations.Any(static x => x is RemoveHashOperation || (x is SetRangeInHashOperation hash && hash.Fields.Any(static y => y.Key == ReservationField)));
    }

    // The continuations and joins staged waiting for a parent that isn't the application's
    private IEnumerable<string> ContinuationsOfOthers(IReadOnlyList<StorageOperation> operations)
    {
        foreach (StorageOperation operation in operations)
        {
            if (operation is not AddJobStateOperation { SetCurrent: true } added
                || !AwaitingState.StateName.Equals(added.State.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Dictionary<string, string>? data = SerializationHelper.Deserialize<Dictionary<string, string>>(added.State.Data);
            string? parents = data == null ? null : data.TryGetValue("ParentIds", out string? all) ? all : data.GetValueOrDefault("ParentId");
            if (parents != null && parents.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(x => !staging.IsTheApplications(x)))
            {
                yield return StorageValues.FormatJobId(added.JobId);
            }
        }
    }
}

/// <summary>
/// Registers staged continuations and joins on parents that the application didn't stage, once they are
/// committed, by electing their waiting state again through the storage: the filters that register them do it
/// as for any continuation, under each parent's lock, and start the continuation at once when its parents
/// already finished. Each such continuation is kept in a set, committed with it, until it is registered; a
/// server registers what is left there (<see cref="StagedContinuationsProcess"/>).
/// </summary>
internal static class StagedContinuations
{
    /// <summary>The set of continuations committed and not registered yet, scored by when they were staged.</summary>
    public const string Key = "staging:continuations";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(StagedContinuations));

    public static void Register(EntityFrameworkCoreStorage storage, IEnumerable<string> continuations)
    {
        foreach (string continuation in continuations)
        {
            try
            {
                Register(storage, continuation);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A server registers it later; meanwhile its parents don't know of it
                Logger.WarnException($"Continuation '{continuation}', committed from an application's context, couldn't be registered on its parents yet; a server tries again within minutes.", ex);
            }
        }
    }

    private static void Register(EntityFrameworkCoreStorage storage, string continuation)
    {
        using IStorageConnection connection = storage.GetConnection();
        StateData? state = connection.GetStateData(continuation);

        if (state != null && AwaitingState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) && WaitingState(state.Data) is { } waiting)
        {
            new BackgroundJobClient(storage).ChangeState(continuation, waiting, AwaitingState.StateName);
        }

        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.RemoveFromSet(Key, continuation);
        transaction.Commit();
    }

    // The state the continuation waits in, as it was stored
    private static IState? WaitingState(IDictionary<string, string> data)
    {
        if (AwaitingJobsState.FromStateData(data) is { } join)
        {
            return join;
        }

        if (!data.TryGetValue("ParentId", out string? parent) || !data.TryGetValue("NextState", out string? next)
            || SerializationHelper.Deserialize<IState>(next, SerializationOption.TypedInternal) is not { } nextState)
        {
            return null;
        }

        JobContinuationOptions options = data.TryGetValue("Options", out string? text) && Enum.TryParse(text, out JobContinuationOptions parsed)
            ? parsed
            : JobContinuationOptions.OnlyOnSucceededState;

        return data.TryGetValue("Expiration", out string? expiration) && TimeSpan.TryParse(expiration, CultureInfo.InvariantCulture, out TimeSpan lasts)
            ? new AwaitingState(parent, nextState, options, lasts)
            : new AwaitingState(parent, nextState, options);
    }
}

/// <summary>
/// Registers the staged continuations whose process stopped between their commit and their registration, a
/// minute after they were staged at the earliest, one server at a time.
/// </summary>
internal sealed class StagedContinuationsProcess(EntityFrameworkCoreStorage storage) : IBackgroundProcess
{
    private const int BatchSize = 100;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(StagedContinuationsProcess));

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            using IStorageConnection connection = storage.GetConnection();
            using (connection.AcquireDistributedLock(StagedContinuations.Key, TimeSpan.Zero))
            {
                long before = JobHelper.ToTimestamp(JobClock.UtcNow - Interval);
                StagedContinuations.Register(storage, ((JobStorageConnection)connection).GetFirstByLowestScoreFromSet(StagedContinuations.Key, 0, before, BatchSize));
            }
        }
        catch (DistributedLockTimeoutException)
        {
            // Another server is at it
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or DbException)
        {
            Logger.ErrorException("Couldn't register the staged continuations left over.", ex);
        }

        context.Wait(Interval);
    }

    public override string ToString() => "Entity Framework Core staged continuations";
}

/// <summary>Names what a staged operation writes, so reads and writes of it can be told apart from others.</summary>
internal static class StagedResources
{
    public static string Job(long id) => $"job:{id.ToString(CultureInfo.InvariantCulture)}";

    public static string Set(string key) => $"set:{key}";

    public static string Hash(string key) => $"hash:{key}";

    public static string List(string key) => $"list:{key}";

    public static string Counter(string key) => $"counter:{key}";
}
