using System.Transactions;
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
    /// single value (a date and time, or text such as <c>2026-01-31 12:00:00.000</c>), as
    /// <c>select sysutcdatetime()</c> does on SQL Server. <c>null</c> (the default) uses the statement the
    /// storage knows for the provider: SQL Server, PostgreSQL (Npgsql), MySQL and MariaDB (Pomelo and Oracle's
    /// MySQL provider) and Oracle have one. Set it for another provider, or to replace the statement of one of
    /// these. On SQLite, whose clock is the process's own, it makes the storage ask SQLite instead.
    /// </summary>
    /// <remarks>
    /// The reads of the clock check the statement, until it has answered once: its answer must be a time and, for
    /// every statement but the built-in one of SQL Server, a time within 30 minutes of this process's clock (a
    /// statement that answers local time instead of UTC is told that way). A statement whose answer is wrong, or
    /// that the database keeps refusing, is logged once as a warning; the storage answers with this process's
    /// clock meanwhile, asks the statement again every 15 seconds, and takes the database's clock up once it
    /// answers. See the remarks of <see cref="EntityFrameworkCoreStorage"/>.
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
/// MySQL and MariaDB (Pomelo and Oracle's MySQL provider) and Oracle; for another provider, set
/// <see cref="EntityFrameworkCoreStorageOptions.DatabaseClockSql"/>. The schedulers, the dashboard, the health
/// checks and the lease of a chain that is being created then go by that one clock; server heartbeats, the
/// leases of distributed locks and the invisibility of fetched jobs are stamped and timed out with it. The clocks
/// of the processes that share the storage needn't agree for any of them. Each read is one more statement on a
/// context of the storage; a free lock still costs two statements to take, and fetching reads the clock once a
/// second at most. There, the time provider set with <c>UseTimeProvider</c> no longer drives the schedulers, as
/// on the SQL Server storage.
/// </para>
/// <para>
/// SQLite runs inside the process and reads the machine's clock, which every process on that machine shares:
/// its clock is the process's own. On SQLite the storage runs no statement and doesn't advertise the feature, so
/// everything goes by <see cref="JobClock"/> as before, a time provider set for tests included;
/// <c>GetUtcDateTime</c> answers with it.
/// </para>
/// <para>
/// <see cref="HasFeature"/> answers from the configuration alone (the provider, or the option): it never asks
/// the database. A read of the clock that fails is never final. SQL Server's statement is never given up on: a
/// read that fails throws like any other read, and the next read asks again. Any other statement is checked by
/// the reads themselves (see <see cref="EntityFrameworkCoreStorageOptions.DatabaseClockSql"/>): until it has
/// answered once, a read the database refuses throws too, and only a statement the database, in reach, refused
/// three times over five seconds or more, or one whose answer isn't the UTC time, is taken for wrong. That is
/// logged once as a warning, and the process tells the time with its own clock, as before the storage read the
/// database's, while the statement is asked again every 15 seconds; once it answers, the database's clock is
/// taken up, which is logged. From its first answer on, a read that fails throws: a process changes clocks at
/// most once, from its own to the database's, and never back. A provider without a statement is logged once as
/// information when the storage is created: the feature isn't advertised, and the process tells the time with
/// its own clock.
/// </para>
/// <para>
/// The expiry of rows and the times written into states still go by the clock of the process that writes or
/// compares them (<see cref="JobClock"/>); they are counted in hours and days.
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

        // Providers have no common way to read the database's clock: the clock knows which have a statement.
        // A context is created here in any case, so a factory whose model lacks the tables is refused at once;
        // it tells the provider without connecting.
        _features.Remove(JobStorageFeatures.Connection.GetUtcDateTime);
        using (SuppressAmbientTransaction())
        using (DbContext context = CreateContext())
        {
            ProviderName = context.Database.ProviderName;
            Clock = new EfDatabaseClock(this, ProviderName);
        }

        if (dedicated && options.PrepareSchemaIfNecessary)
        {
            PrepareSchema();
        }
    }

    /// <summary>The options the storage was created with.</summary>
    public EntityFrameworkCoreStorageOptions Options { get; }

    internal Task Enqueued => Volatile.Read(ref _enqueued).Task;

    /// <summary>The name the provider of the storage's contexts gives itself (<c>DbContext.Database.ProviderName</c>).</summary>
    internal string? ProviderName { get; }

    /// <summary>The clock the storage tells the database's time with. Replaceable for tests.</summary>
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
        yield return new StagedContinuationsProcess(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Connection.GetUtcDateTime</c> is supported when the storage reads the database's clock with a statement:
    /// the provider has one, or <see cref="EntityFrameworkCoreStorageOptions.DatabaseClockSql"/> is set (see the
    /// remarks of <see cref="EntityFrameworkCoreStorage"/>). The answer comes from the configuration; the database
    /// is never asked.
    /// </remarks>
    public override bool HasFeature(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        if (string.Equals(featureId, JobStorageFeatures.Connection.GetUtcDateTime, StringComparison.OrdinalIgnoreCase))
        {
            return Clock.ReadsTheDatabase;
        }

        return _features.TryGetValue(featureId, out bool supported) ? supported : base.HasFeature(featureId);
    }

    /// <inheritdoc />
    public override void Purge(StoragePurgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using TransactionScope? suppressed = SuppressAmbientTransaction();
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
        logger.Info($"    Clock: {Clock.Source()}.");
    }

    /// <inheritdoc />
    public override string ToString() => "Entity Framework Core storage";

    /// <summary>
    /// Keeps the storage's contexts out of a caller's ambient transaction (<see cref="TransactionScope"/>), for
    /// as long as the result isn't disposed: the storage writes through contexts and connections of its own, so a
    /// job created inside a caller's transaction exists whether that transaction commits or not, and a write of
    /// the storage that fails never dooms it. Jobs staged in the application's context with <c>NightShift()</c>
    /// are the exception, by design: they are rows of that context, saved by its <c>SaveChanges</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ambient transaction is suppressed around everything that may open a connection, not only around the
    /// opening: the provider enlists a connection when it is opened (SqlClient, Npgsql, MySQL and Oracle by
    /// default), and EF Core itself looks at the ambient transaction every time it uses the connection,
    /// enlisting one that it opened, or refusing to begin a transaction of its own, or failing on a provider
    /// that can't take part in one (SQLite). Turning enlistment off in the connection string would cover
    /// neither EF Core's own enlisting nor the application's connection string, which the application's own
    /// context also uses, and the keyword differs from one provider to another.
    /// </para>
    /// <para>
    /// A context whose connection the application opened and enlisted itself is used as it is: what runs on
    /// it runs in that connection's transaction. Nothing is allocated outside a caller's transaction. The scope
    /// is created and disposed on one thread, around synchronous calls; inside a scope that flows with async
    /// calls, a scope of this thread still takes precedence while it lasts.
    /// </para>
    /// </remarks>
    internal static TransactionScope? SuppressAmbientTransaction()
    {
        return Transaction.Current != null ? new TransactionScope(TransactionScopeOption.Suppress) : null;
    }

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
        using TransactionScope? suppressed = SuppressAmbientTransaction();
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
        using TransactionScope? suppressed = SuppressAmbientTransaction();
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
