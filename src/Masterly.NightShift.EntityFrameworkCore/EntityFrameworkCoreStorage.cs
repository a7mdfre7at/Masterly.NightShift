using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NotNullWhenAttribute = System.Diagnostics.CodeAnalysis.NotNullWhenAttribute;

namespace Masterly.NightShift.EntityFrameworkCore;

/// <summary>Options of <see cref="EntityFrameworkCoreStorage"/>.</summary>
public sealed class EntityFrameworkCoreStorageOptions
{
    /// <summary>
    /// The schema of the tables of a dedicated database context, or <c>null</c> for the database's default
    /// schema (SQLite has no schemas). <c>nightshift</c> by default. An application's own context sets the
    /// schema in <c>ApplyNightShiftModel</c> instead.
    /// </summary>
    public string? Schema { get; set; } = NightShiftModelBuilderExtensions.DefaultSchema;

    /// <summary>
    /// Whether a dedicated context creates its database and tables when they are missing, and checks the schema
    /// version of tables that exist; they are never changed. Several processes may start at once: one creates the
    /// tables and the others use them. <c>true</c> by default; an application's own context creates them with its
    /// migrations.
    /// </summary>
    public bool PrepareSchemaIfNecessary { get; set; } = true;

    /// <summary>
    /// How long a worker waits before it looks at its queues again. Jobs enqueued through the same storage
    /// object, or staged in a context and saved, wake the workers at once; jobs enqueued by other processes are
    /// seen at the next poll. One second by default.
    /// </summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a fetched job stays invisible to other workers after its worker stopped keeping it alive. A
    /// worker keeps its job alive while it runs. Thirty minutes by default.
    /// </summary>
    public TimeSpan InvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often expired rows are deleted. Thirty minutes by default.</summary>
    public TimeSpan JobExpirationCheckInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often counter increments are merged. Five minutes by default.</summary>
    public TimeSpan CountersAggregateInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a distributed lock outlives a process that died holding it. A live holder renews it every third
    /// of this time. Thirty seconds by default.
    /// </summary>
    public TimeSpan DistributedLockLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The statement that reads the database's clock: SQL that answers the database's current time in UTC as a
    /// single value (a date and time, or text such as <c>2026-01-31 12:00:00.000</c>), for example
    /// <c>select utc_timestamp(6)</c>. <c>null</c> (the default) uses the statement the storage knows for the
    /// provider: SQL Server, PostgreSQL (Npgsql), MySQL and MariaDB (Pomelo and Oracle's MySQL provider), Oracle
    /// and SQLite have one. Set it for another provider, or to replace the statement of one of these.
    /// </summary>
    /// <remarks>
    /// The statement is run once, the first time the clock is asked for, before it is used: the storage reads the
    /// database's clock only when the statement answered a time and, for every statement but the built-in ones
    /// of SQL Server and SQLite, a time within 30 minutes of this process's clock (a statement that answers
    /// local time instead of UTC is told that way). Otherwise the process keeps its own clock and logs a warning
    /// that says why. See the remarks of <see cref="EntityFrameworkCoreStorage"/>.
    /// </remarks>
    public string? DatabaseClockSql { get; set; }
}

/// <summary>
/// A job storage on Entity Framework Core, for any relational provider (SQL Server, PostgreSQL, SQLite, MySQL and
/// others): through a context of its own, or through the application's context whose model includes
/// <c>ApplyNightShiftModel</c>. With the application's context, jobs can be staged with
/// <c>context.NightShift(storage)</c> and commit or roll back with its <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// <para>
/// Keys, hash fields, set values and queue names are compared by the database, with the collation of their
/// columns: SQLite and PostgreSQL compare them as written, while the default collations of SQL Server and MySQL
/// ignore case, so <c>Key</c> and <c>key</c> are one key there. Keys of at most 100 characters, hash fields of
/// 100, set values of 256, job parameter names of 40, state names of 20 and queue names of 50 fit the columns;
/// longer ones are refused with an <see cref="ArgumentException"/> before anything is written, on every provider.
/// </para>
/// <para>
/// A dedicated context records the version of the tables it creates, and refuses tables of another version.
/// With an application's context, the application's migrations create and upgrade the tables.
/// </para>
/// <para>
/// The storage reads the database's clock (<c>JobStorageConnection.GetUtcDateTime</c>, advertised as the feature
/// <c>Connection.GetUtcDateTime</c>) on the providers it knows a statement for: SQL Server, PostgreSQL (Npgsql),
/// MySQL and MariaDB (Pomelo and Oracle's MySQL provider), Oracle and SQLite; for another provider, set
/// <see cref="EntityFrameworkCoreStorageOptions.DatabaseClockSql"/>. The schedulers, the dashboard, server
/// heartbeats and the lease of a chain that is being created then go by that one clock, so the clocks of the
/// processes that share the storage needn't agree for them. SQLite runs inside the process and reads the
/// machine's own clock, which is the same clock for every process on that machine. Each read is one more
/// statement on a context of the storage.
/// </para>
/// <para>
/// The statement is checked once, the first time the clock is asked for (see
/// <see cref="EntityFrameworkCoreStorageOptions.DatabaseClockSql"/>). For an unknown provider without the option
/// (logged once as information), or a statement that fails the check (logged once as a warning), the storage
/// doesn't advertise the feature and the process tells the time with its own clock, for as long as the storage
/// object lives. While the database can't be reached at all, nothing is decided: <see cref="HasFeature"/> throws
/// the provider's exception for <c>Connection.GetUtcDateTime</c>, and the next call checks again. Once the
/// database's clock is in use, a read of it that fails throws like any other read; the storage never goes back
/// to the process's clock.
/// </para>
/// <para>
/// Fetched jobs' invisibility, the expiry of distributed locks and of rows, and the times written into states
/// still go by the clock of the process that writes or compares them (<see cref="JobClock"/>).
/// </para>
/// </remarks>
public sealed class EntityFrameworkCoreStorage : JobStorage
{
    private readonly Dictionary<string, bool> _features = StorageRules.Features(typeof(EfFetchedJob));
    private readonly Func<DbContext> _contextFactory;
    private TaskCompletionSource _enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A storage with a context of its own, configured for a provider: <c>o => o.UseSqlServer(connectionString)</c>.
    /// Unless <see cref="EntityFrameworkCoreStorageOptions.PrepareSchemaIfNecessary"/> is off, it connects to the
    /// database at once, creates what is missing and checks the version of the tables.
    /// </summary>
    /// <param name="configure">Configures the provider of the context.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The tables are of another schema version.</exception>
    public EntityFrameworkCoreStorage([NotNull] Action<DbContextOptionsBuilder> configure, [CanBeNull] EntityFrameworkCoreStorageOptions? options = null)
        : this(CreateDedicatedFactory(configure, options ??= new EntityFrameworkCoreStorageOptions()), options, dedicated: true)
    {
    }

    /// <summary>
    /// A storage on contexts of the application, created by <paramref name="contextFactory"/> (for example
    /// <c>IDbContextFactory&lt;T&gt;.CreateDbContext</c>). Their model must include <c>ApplyNightShiftModel</c>,
    /// and the application's migrations create the tables.
    /// </summary>
    /// <param name="contextFactory">Creates a new context each time it is called.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contextFactory"/> is <c>null</c>.</exception>
    public EntityFrameworkCoreStorage([NotNull] Func<DbContext> contextFactory, [CanBeNull] EntityFrameworkCoreStorageOptions? options = null)
        : this(contextFactory, options ?? new EntityFrameworkCoreStorageOptions(), dedicated: false)
    {
    }

    private EntityFrameworkCoreStorage(Func<DbContext> contextFactory, EntityFrameworkCoreStorageOptions options, bool dedicated)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        if (options.QueuePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The queue poll interval must be positive.", nameof(options));
        }

        if (options.InvisibilityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The invisibility timeout must be positive.", nameof(options));
        }

        if (options.JobExpirationCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The expiration check interval must be positive.", nameof(options));
        }

        if (options.CountersAggregateInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The counters aggregate interval must be positive.", nameof(options));
        }

        if (options.DistributedLockLifetime < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("The lock lifetime must be at least a second.", nameof(options));
        }

        if (options.DatabaseClockSql != null && string.IsNullOrWhiteSpace(options.DatabaseClockSql))
        {
            throw new ArgumentException("The database clock SQL must be a statement, or null for the provider's.", nameof(options));
        }

        _contextFactory = contextFactory;
        Options = options;

        // Providers have no common way to read the database's clock: whether this one's is read is found out
        // the first time the feature is asked for, not here, where the database may not be reachable yet
        _features.Remove(JobStorageFeatures.Connection.GetUtcDateTime);
        Clock = new EfDatabaseClock(this);

        if (dedicated && options.PrepareSchemaIfNecessary)
        {
            PrepareSchema();
        }
        else
        {
            using DbContext context = CreateContext();
        }
    }

    /// <summary>The options the storage was created with.</summary>
    public EntityFrameworkCoreStorageOptions Options { get; }

    internal Task Enqueued => Volatile.Read(ref _enqueued).Task;

    /// <summary>The database's clock, where it is read. Replaceable for tests.</summary>
    internal EfDatabaseClock Clock { get; set; }

    /// <inheritdoc />
    public override IMonitoringApi GetMonitoringApi() => new EfMonitoringApi(this);

    /// <inheritdoc />
    public override IStorageConnection GetConnection() => new EfConnection(this);

    /// <inheritdoc />
    public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
    {
        yield return new EfExpirationManager(this);
        yield return new EfCountersAggregator(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Connection.GetUtcDateTime</c> is supported when the database's clock is read (see the remarks of
    /// <see cref="EntityFrameworkCoreStorage"/>). The first call that asks for it runs one statement against the
    /// database to find out, and throws the provider's exception when the database can't be reached; later calls
    /// answer from memory.
    /// </remarks>
    public override bool HasFeature(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        if (string.Equals(featureId, JobStorageFeatures.Connection.GetUtcDateTime, StringComparison.OrdinalIgnoreCase))
        {
            return Clock.IsRead();
        }

        return _features.TryGetValue(featureId, out bool supported) ? supported : base.HasFeature(featureId);
    }

    /// <inheritdoc />
    public override void Purge(StoragePurgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using DbContext context = CreateContext();
        long removed = 0;

        removed += context.Set<JobQueueRow>().ExecuteDelete();
        removed += context.Set<StateRow>().ExecuteDelete();
        removed += context.Set<JobParameterRow>().ExecuteDelete();
        removed += context.Set<JobRow>().ExecuteDelete();
        removed += context.Set<CounterRow>().ExecuteDelete();
        removed += context.Set<AggregatedCounterRow>().ExecuteDelete();
        removed += options.KeepRecurringJobs ? context.Set<SetRow>().Where(static x => x.Key != "recurring-jobs").ExecuteDelete() : context.Set<SetRow>().ExecuteDelete();
        removed += options.KeepAuditLog ? context.Set<ListRow>().Where(static x => x.Key != AuditLog.ListKey).ExecuteDelete() : context.Set<ListRow>().ExecuteDelete();

        IQueryable<HashRow> hashes = context.Set<HashRow>();
        if (options.KeepRecurringJobs)
        {
            hashes = hashes.Where(static x => !x.Key.StartsWith("recurring-job:"));
        }

        if (options.KeepDrainMode)
        {
            hashes = hashes.Where(static x => x.Key != DrainMode.HashKey);
        }

        removed += hashes.ExecuteDelete();

        if (!options.KeepServers)
        {
            removed += context.Set<ServerRow>().ExecuteDelete();
        }

        LogProvider.GetLogger(typeof(EntityFrameworkCoreStorage)).Info($"The Entity Framework Core job storage was purged: {removed} rows deleted.");
    }

    /// <inheritdoc />
    public override void WriteOptionsToLog(ILog logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Info("Using the following options for the Entity Framework Core job storage:");
        logger.Info($"    Schema: {Options.Schema ?? "(default)"}, prepare schema: {Options.PrepareSchemaIfNecessary}.");
        logger.Info($"    Queue poll interval: {Options.QueuePollInterval}.");
        logger.Info($"    Invisibility timeout: {Options.InvisibilityTimeout}.");
        logger.Info($"    Expiration check interval: {Options.JobExpirationCheckInterval}.");
        logger.Info($"    Counters aggregate interval: {Options.CountersAggregateInterval}.");
        logger.Info($"    Distributed lock lifetime: {Options.DistributedLockLifetime}.");
        logger.Info($"    Database clock SQL: {Options.DatabaseClockSql ?? "(the provider's)"}.");
    }

    /// <inheritdoc />
    public override string ToString() => "Entity Framework Core storage";

    /// <summary>A new context whose model includes the storage's tables.</summary>
    internal DbContext CreateContext()
    {
        DbContext context = _contextFactory() ?? throw new InvalidOperationException("The context factory returned null.");
        EnsureModel(context);
        return context;
    }

    /// <summary>Wakes the workers of this process waiting for a job.</summary>
    internal void Signal()
    {
        Interlocked.Exchange(ref _enqueued, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Runs <paramref name="write"/> in a database transaction, through the provider's execution strategy.</summary>
    internal void InTransaction(Action<DbContext> write)
    {
        using DbContext context = CreateContext();
        context.Database.CreateExecutionStrategy().Execute(() =>
        {
            context.ChangeTracker.Clear();
            using IDbContextTransaction transaction = context.Database.BeginTransaction();
            write(context);
            context.SaveChanges();
            transaction.Commit();
        });
    }

    internal static void EnsureModel(DbContext context)
    {
        if (context.Model.FindEntityType(typeof(JobRow)) == null)
        {
            throw new InvalidOperationException(
                $"The model of {context.GetType().Name} doesn't include the Masterly.NightShift tables: call modelBuilder.ApplyNightShiftModel() in its OnModelCreating.");
        }
    }

    /// <summary>
    /// Creates what is missing, tolerating another process that creates it at the same time, then checks the
    /// version of the tables. Providers report a missing table, a refused login and an unreachable server with
    /// exceptions of their own, so a failed probe is only taken as missing tables when creating them works, or
    /// when someone else's creation explains the failure; otherwise the creation's error is what surfaces.
    /// </summary>
    private void PrepareSchema()
    {
        using DbContext context = CreateContext();
        IRelationalDatabaseCreator creator = context.GetService<IRelationalDatabaseCreator>();
        ILog logger = LogProvider.GetLogger(typeof(EntityFrameworkCoreStorage));

        if (!creator.Exists())
        {
            try
            {
                creator.Create();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && creator.Exists())
            {
                logger.DebugException("Another process created the database first.", ex);
            }
        }

        if (!TablesExist(context, out Exception? probeError))
        {
            logger.InfoException("The Masterly.NightShift tables weren't found (the exception tells why); creating them.", probeError);

            try
            {
                creator.CreateTables();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && TablesExist(context, out _))
            {
                logger.InfoException("Another process created the Masterly.NightShift tables first.", ex);
            }
        }

        EnsureSchemaVersion(context, logger);
    }

    private static bool TablesExist(DbContext context, [NotNullWhen(false)] out Exception? error)
    {
        try
        {
            _ = context.Set<JobRow>().Any();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            context.ChangeTracker.Clear();
            error = ex;
            return false;
        }
    }

    /// <summary>Records the version of tables just created, and refuses tables of another version.</summary>
    private static void EnsureSchemaVersion(DbContext context, ILog logger)
    {
        List<int> versions = ReadSchemaVersions(context);

        if (versions.Count == 0)
        {
            try
            {
                context.Add(new SchemaVersionRow { Version = SchemaVersionRow.Current });
                context.SaveChanges();
                return;
            }
            catch (DbUpdateException ex)
            {
                // Another process that created the tables at the same time recorded the version first
                logger.DebugException("Another process recorded the schema version first.", ex);
                context.ChangeTracker.Clear();
                versions = ReadSchemaVersions(context);
            }
        }

        if (versions is not [SchemaVersionRow.Current])
        {
            string found = string.Join(", ", versions);
            throw new InvalidOperationException(versions.Count > 0 && versions.Max() > SchemaVersionRow.Current
                ? $"The Masterly.NightShift tables are at schema version {found}, written by a newer version of Masterly.NightShift.EntityFrameworkCore than this one (schema version {SchemaVersionRow.Current}). Upgrade every process that uses them."
                : $"The Masterly.NightShift tables are at schema version {found}, but this version of Masterly.NightShift.EntityFrameworkCore uses schema version {SchemaVersionRow.Current} and can't upgrade them. Use a new schema or database.");
        }
    }

    private static List<int> ReadSchemaVersions(DbContext context)
    {
        try
        {
            return [.. context.Set<SchemaVersionRow>().AsNoTracking().Select(static x => x.Version)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "Couldn't read the schema version of the Masterly.NightShift tables. Tables created by a build of Masterly.NightShift.EntityFrameworkCore that didn't record it can't be used: use a new schema or database.",
                ex);
        }
    }

    private static Func<DbContext> CreateDedicatedFactory(Action<DbContextOptionsBuilder> configure, EntityFrameworkCoreStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(configure);

        DbContextOptionsBuilder<NightShiftDbContext> builder = new();
        configure(builder);
        builder.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();
        DbContextOptions<NightShiftDbContext> contextOptions = builder.Options;
        string? schema = options.Schema;

        return () => new NightShiftDbContext(contextOptions, schema);
    }
}
