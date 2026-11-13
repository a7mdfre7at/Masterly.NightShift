using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Shared;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Masterly.NightShift.MongoDB;

/// <summary>Options of <see cref="MongoStorage"/>.</summary>
public sealed class MongoStorageOptions
{
    /// <summary>The prefix of the collection names, to keep several applications apart in one database. <c>nightshift.</c> by default.</summary>
    public string CollectionPrefix { get; set; } = "nightshift.";

    /// <summary>
    /// How long a fetched job stays invisible to other workers after its worker stopped keeping it alive. A
    /// worker keeps its job alive while it runs. Thirty minutes by default.
    /// </summary>
    public TimeSpan InvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a worker waits before it looks at its queues again. Jobs enqueued through the same storage
    /// object wake the workers at once; jobs enqueued by other processes are seen at the next poll. One second
    /// by default.
    /// </summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often counter increments are merged. Five minutes by default.</summary>
    public TimeSpan CountersAggregateInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a distributed lock outlives a process that died holding it. A live holder renews it every third
    /// of this time. Thirty seconds by default.
    /// </summary>
    public TimeSpan DistributedLockLifetime { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// A job storage on MongoDB 5.0 or later, through the official driver. It needs a replica set or a sharded
/// cluster, because transactions span several documents; a standalone server is refused. Collections and
/// their indexes are created when the storage starts, and TTL indexes remove what expires.
/// </summary>
/// <remarks>
/// Fetched jobs, distributed locks and server heartbeats are stamped and timed out with the server's clock, so
/// processes whose clocks differ agree on when a job's worker, a lock's holder or a server is gone. List items
/// are ordered by their ObjectId, which the driver makes from the second they are inserted, a value per process
/// and a counter: items that different processes insert within the same second are ordered by process, not by
/// the moment they were inserted.
/// </remarks>
public sealed class MongoStorage : JobStorage
{
    // MongoDB 5.0
    private const int MinimumWireVersion = 13;

    private readonly Dictionary<string, bool> _features = StorageRules.Features(typeof(MongoFetchedJob));
    private TaskCompletionSource _enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A storage on a new client, from a connection string and a database name. It connects to the server at once:
    /// it reads the server's version and topology, then creates the indexes.
    /// </summary>
    /// <param name="connectionString">A MongoDB connection string.</param>
    /// <param name="databaseName">The database that holds the collections.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> or <paramref name="databaseName"/> is <c>null</c>.</exception>
    /// <exception cref="NotSupportedException">The server is a standalone server, without transactions, or older than MongoDB 5.0.</exception>
    public MongoStorage([NotNull] string connectionString, [NotNull] string databaseName, [CanBeNull] MongoStorageOptions? options = null)
        : this(new MongoClient(connectionString ?? throw new ArgumentNullException(nameof(connectionString))).GetDatabase(databaseName ?? throw new ArgumentNullException(nameof(databaseName))), options)
    {
    }

    /// <summary>
    /// A storage on a database of a client the application owns, for example one registered in its service
    /// container. It connects to the server at once: it reads the server's version and topology, then creates the
    /// indexes.
    /// </summary>
    /// <param name="database">The database that holds the collections.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is <c>null</c>.</exception>
    /// <exception cref="NotSupportedException">The server is a standalone server, without transactions, or older than MongoDB 5.0.</exception>
    public MongoStorage([NotNull] IMongoDatabase database, [CanBeNull] MongoStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        options ??= new MongoStorageOptions();
        if (string.IsNullOrEmpty(options.CollectionPrefix))
        {
            throw new ArgumentException("The collection prefix can't be empty.", nameof(options));
        }

        if (options.InvisibilityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The invisibility timeout must be positive.", nameof(options));
        }

        if (options.QueuePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The queue poll interval must be positive.", nameof(options));
        }

        if (options.CountersAggregateInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The counters aggregate interval must be positive.", nameof(options));
        }

        if (options.DistributedLockLifetime < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("The lock lifetime must be at least a second.", nameof(options));
        }

        Database = database;
        Options = options;
        Collections = new MongoCollections(database, options.CollectionPrefix);

        EnsureTransactions(database);
        Collections.CreateIndexes();
    }

    /// <summary>The options the storage was created with.</summary>
    public MongoStorageOptions Options { get; }

    /// <summary>The database that holds the collections.</summary>
    public IMongoDatabase Database { get; }

    internal MongoCollections Collections { get; }

    internal Task Enqueued => Volatile.Read(ref _enqueued).Task;

    /// <inheritdoc />
    public override IMonitoringApi GetMonitoringApi() => new MongoMonitoringApi(this);

    /// <inheritdoc />
    public override IStorageConnection GetConnection() => new MongoConnection(this);

    /// <inheritdoc />
    public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
    {
        yield return new MongoCountersAggregator(this);
    }

    /// <inheritdoc />
    public override bool HasFeature(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        return _features.TryGetValue(featureId, out bool supported) ? supported : base.HasFeature(featureId);
    }

    /// <inheritdoc />
    public override void Purge(StoragePurgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        FilterDefinitionBuilder<BsonDocument> filter = Builders<BsonDocument>.Filter;
        long removed = 0;

        removed += Collections.Jobs.DeleteMany(filter.Empty).DeletedCount;
        removed += Collections.JobQueue.DeleteMany(filter.Empty).DeletedCount;
        removed += Collections.Counters.DeleteMany(filter.Empty).DeletedCount;
        removed += Collections.AggregatedCounters.DeleteMany(filter.Empty).DeletedCount;
        removed += Collections.Sets.DeleteMany(options.KeepRecurringJobs ? filter.Ne("Key", "recurring-jobs") : filter.Empty).DeletedCount;
        removed += Collections.Lists.DeleteMany(options.KeepAuditLog ? filter.Ne("Key", AuditLog.ListKey) : filter.Empty).DeletedCount;

        List<FilterDefinition<BsonDocument>> hashes = [];
        if (options.KeepRecurringJobs)
        {
            hashes.Add(filter.Not(filter.Regex("Key", new BsonRegularExpression("^recurring-job:"))));
        }

        if (options.KeepDrainMode)
        {
            hashes.Add(filter.Ne("Key", DrainMode.HashKey));
        }

        removed += Collections.Hashes.DeleteMany(hashes.Count > 0 ? filter.And(hashes) : filter.Empty).DeletedCount;

        if (!options.KeepServers)
        {
            removed += Collections.Servers.DeleteMany(filter.Empty).DeletedCount;
        }

        LogProvider.GetLogger(typeof(MongoStorage)).Info($"The MongoDB job storage was purged: {removed} documents deleted.");
    }

    /// <inheritdoc />
    public override void WriteOptionsToLog(ILog logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Info("Using the following options for the MongoDB job storage:");
        logger.Info($"    Database: '{Database.DatabaseNamespace.DatabaseName}', collection prefix: '{Options.CollectionPrefix}'.");
        logger.Info($"    Invisibility timeout: {Options.InvisibilityTimeout}.");
        logger.Info($"    Queue poll interval: {Options.QueuePollInterval}.");
        logger.Info($"    Counters aggregate interval: {Options.CountersAggregateInterval}.");
        logger.Info($"    Distributed lock lifetime: {Options.DistributedLockLifetime}.");
    }

    /// <inheritdoc />
    public override string ToString() => $"MongoDB storage ({Database.DatabaseNamespace.DatabaseName}, {Options.CollectionPrefix})";

    /// <summary>Wakes the workers of this process waiting for a job.</summary>
    internal void Signal()
    {
        Interlocked.Exchange(ref _enqueued, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Runs <paramref name="write"/> in a transaction, which the driver retries on transient errors. It reads
    /// from one snapshot and commits once a majority of the replica set has the writes, whatever the defaults of
    /// the client or the database are, so a committed write survives a failover.
    /// </summary>
    internal void InTransaction(Action<IClientSessionHandle> write)
    {
        using IClientSessionHandle session = Database.Client.StartSession();
        session.WithTransaction(
            (handle, _) =>
            {
                write(handle);
                return true;
            },
            new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
    }

    /// <summary>
    /// Refuses a server without what the storage needs: multi-document transactions (a replica set or a sharded
    /// cluster) and MongoDB 5.0 (wire version 13), for snapshot reads outside transactions.
    /// </summary>
    /// <param name="hello">The server's answer to the <c>hello</c> command.</param>
    /// <exception cref="NotSupportedException">The server is a standalone server, or older than MongoDB 5.0.</exception>
    internal static void EnsureSupported(BsonDocument hello)
    {
        bool replicaSet = hello.Contains("setName");
        bool sharded = hello.TryGetValue("msg", out BsonValue message) && message == "isdbgrid";

        if (!replicaSet && !sharded)
        {
            throw new NotSupportedException(
                "The MongoDB storage needs a replica set or a sharded cluster, because its transactions span several documents. " +
                "A single server can run as a one-member replica set: start it with --replSet and run rs.initiate().");
        }

        int wireVersion = hello.TryGetValue("maxWireVersion", out BsonValue version) && version.IsNumeric ? version.ToInt32() : 0;
        if (wireVersion < MinimumWireVersion)
        {
            throw new NotSupportedException(
                $"The MongoDB storage needs MongoDB 5.0 or later (wire version {MinimumWireVersion}), but the server reports wire version {wireVersion}.");
        }
    }

    private static void EnsureTransactions(IMongoDatabase database)
    {
        EnsureSupported(database.RunCommand<BsonDocument>(new BsonDocument("hello", 1)));
    }
}

/// <summary>The storage's collections and their indexes.</summary>
internal sealed class MongoCollections(IMongoDatabase database, string prefix)
{
    public IMongoCollection<BsonDocument> Jobs { get; } = database.GetCollection<BsonDocument>(prefix + "jobs");

    public IMongoCollection<BsonDocument> JobQueue { get; } = database.GetCollection<BsonDocument>(prefix + "jobQueue");

    public IMongoCollection<BsonDocument> Sets { get; } = database.GetCollection<BsonDocument>(prefix + "sets");

    public IMongoCollection<BsonDocument> Lists { get; } = database.GetCollection<BsonDocument>(prefix + "lists");

    public IMongoCollection<BsonDocument> Hashes { get; } = database.GetCollection<BsonDocument>(prefix + "hashes");

    public IMongoCollection<BsonDocument> Counters { get; } = database.GetCollection<BsonDocument>(prefix + "counters");

    public IMongoCollection<BsonDocument> AggregatedCounters { get; } = database.GetCollection<BsonDocument>(prefix + "aggregatedCounters");

    public IMongoCollection<BsonDocument> Servers { get; } = database.GetCollection<BsonDocument>(prefix + "servers");

    public IMongoCollection<BsonDocument> Locks { get; } = database.GetCollection<BsonDocument>(prefix + "locks");

    public IMongoCollection<BsonDocument> Sequences { get; } = database.GetCollection<BsonDocument>(prefix + "sequences");

    public IMongoClient Client { get; } = database.Client;

    /// <summary>Creates the indexes; creating one that already exists does nothing.</summary>
    public void CreateIndexes()
    {
        IndexKeysDefinitionBuilder<BsonDocument> keys = Builders<BsonDocument>.IndexKeys;
        CreateIndexOptions unique = new() { Unique = true };

        Jobs.Indexes.CreateMany(
        [
            new CreateIndexModel<BsonDocument>(keys.Ascending("StateName").Ascending("_id")),
            Ttl(),
        ]);
        JobQueue.Indexes.CreateMany(
        [
            new CreateIndexModel<BsonDocument>(keys.Ascending("Queue").Ascending("FetchedAt").Ascending("_id")),
            new CreateIndexModel<BsonDocument>(keys.Ascending("JobId")),
        ]);
        Sets.Indexes.CreateMany(
        [
            new CreateIndexModel<BsonDocument>(keys.Ascending("Key").Ascending("Value"), unique),
            new CreateIndexModel<BsonDocument>(keys.Ascending("Key").Ascending("Score").Ascending("Value")),
            Ttl(),
        ]);
        Lists.Indexes.CreateMany([new CreateIndexModel<BsonDocument>(keys.Ascending("Key").Descending("_id")), Ttl()]);
        Hashes.Indexes.CreateMany([new CreateIndexModel<BsonDocument>(keys.Ascending("Key").Ascending("Field"), unique), Ttl()]);
        Counters.Indexes.CreateMany([new CreateIndexModel<BsonDocument>(keys.Ascending("Key")), Ttl()]);
        AggregatedCounters.Indexes.CreateOne(Ttl());
        Locks.Indexes.CreateOne(Ttl());
    }

    private static CreateIndexModel<BsonDocument> Ttl()
    {
        return new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ExpireAt"), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero });
    }
}
