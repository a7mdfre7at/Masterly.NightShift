using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Shared;

namespace Masterly.NightShift.InMemory;

/// <summary>Options of <see cref="InMemoryStorage"/>.</summary>
public sealed class InMemoryStorageOptions
{
    /// <summary>
    /// How long a worker waits for a job before it looks at its queues again. Enqueuing a job wakes the waiting
    /// workers at once, so this only matters for jobs whose fetcher stopped. One second by default.
    /// </summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a fetched job stays invisible to other workers after its worker stopped keeping it alive. A
    /// worker keeps its job alive while it runs. Thirty minutes by default.
    /// </summary>
    public TimeSpan InvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often expired jobs and keys are removed. One minute by default.</summary>
    public TimeSpan ExpirationCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest anything is kept after it is set to expire, whatever the job's or key's own expiration says,
    /// to bound the memory a busy process uses. <c>null</c> (the default) keeps the expirations as set.
    /// </summary>
    public TimeSpan? MaxExpirationTime { get; set; }

    /// <summary>How long a distributed lock outlives a holder that stopped renewing it. One minute; tests shorten it.</summary>
    internal TimeSpan DistributedLockLifetime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Whether workers keep their fetched jobs invisible while they run. Tests turn it off to let a fetch go stale.</summary>
    internal bool KeepFetchedJobsAlive { get; set; } = true;

    internal InMemoryStorageOptions Copy() => (InMemoryStorageOptions)MemberwiseClone();
}

/// <summary>
/// A job storage that keeps everything in the process's memory, for development and tests. Nothing survives the
/// process, and only servers in the same process share its jobs. Everything is behind one lock, so reads are
/// linearizable and transactions are applied all or none.
/// </summary>
public sealed class InMemoryStorage : JobStorage
{
    private readonly Dictionary<string, bool> _features = StorageRules.Features(typeof(InMemoryFetchedJob));

    /// <summary>A storage with the default options.</summary>
    public InMemoryStorage()
        : this(new InMemoryStorageOptions())
    {
    }

    /// <summary>A storage with the given options.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">An interval in <paramref name="options"/> isn't positive.</exception>
    /// <remarks>The storage keeps a copy of <paramref name="options"/>: changing them afterwards changes nothing.</remarks>
    public InMemoryStorage([NotNull] InMemoryStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Settings = options.Copy();
        if (Settings.QueuePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The queue poll interval must be positive.", nameof(options));
        }

        if (Settings.InvisibilityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The invisibility timeout must be positive.", nameof(options));
        }

        if (Settings.ExpirationCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The expiration check interval must be positive.", nameof(options));
        }

        if (Settings.MaxExpirationTime <= TimeSpan.Zero)
        {
            throw new ArgumentException("The max expiration time must be positive when set.", nameof(options));
        }

        if (Settings.DistributedLockLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException("The lock lifetime must be positive.", nameof(options));
        }

        Database = new InMemoryDatabase(Settings);
    }

    /// <summary>A copy of the options the storage was created with; changing it changes nothing.</summary>
    public InMemoryStorageOptions Options => Settings.Copy();

    /// <summary>The options the storage uses, copied when it was created.</summary>
    internal InMemoryStorageOptions Settings { get; }

    /// <summary>Reads see every committed write: everything is behind one lock.</summary>
    public override bool LinearizableReads => true;

    internal InMemoryDatabase Database { get; }

    /// <inheritdoc />
    public override IMonitoringApi GetMonitoringApi() => new InMemoryMonitoringApi(Database);

    /// <inheritdoc />
    public override IStorageConnection GetConnection() => new InMemoryConnection(this);

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
        long removed = Database.Purge(options);
        LogProvider.GetLogger(typeof(InMemoryStorage)).Info($"The in-memory job storage was purged: {removed} items removed.");
    }

    /// <inheritdoc />
    public override void WriteOptionsToLog(ILog logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Info("Using the following options for the in-memory job storage:");
        logger.Info($"    Queue poll interval: {Settings.QueuePollInterval}.");
        logger.Info($"    Invisibility timeout: {Settings.InvisibilityTimeout}.");
        logger.Info($"    Expiration check interval: {Settings.ExpirationCheckInterval}.");
        logger.Info($"    Max expiration time: {Settings.MaxExpirationTime?.ToString() ?? "none"}.");
    }

    /// <inheritdoc />
    public override string ToString() => "In-memory storage";
}

internal sealed class InMemoryConnection(InMemoryStorage storage) : JobStorageConnection
{
    private readonly InMemoryDatabase _database = storage.Database;
    private readonly ReentrantLocks _locks = new();

    public override IWriteOnlyTransaction CreateWriteTransaction() => new InMemoryTransaction(_database, this);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _locks.Acquire(resource, () => LeaseLock.Acquire(_database, resource, timeout, storage.Settings.DistributedLockLifetime));
    }

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(parameters);
        return StorageValues.FormatJobId(_database.CreateJob(job, parameters, createdAt, expireIn));
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queues);
        if (queues.Length == 0)
        {
            throw new ArgumentException("Queue array must be non-empty.", nameof(queues));
        }

        (long entryId, long jobId, string queue, string token) = _database.Fetch(queues, cancellationToken);
        return new InMemoryFetchedJob(_database, entryId, jobId, queue, token, storage.Settings);
    }

    public override void SetJobParameter(string id, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        if (StorageValues.TryParseJobId(id, out long jobId))
        {
            _database.SetJobParameter(jobId, name, value);
        }
    }

    public override string? GetJobParameter(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        return StorageValues.TryParseJobId(id, out long jobId) ? _database.GetJobParameter(jobId, name) : null;
    }

    public override JobData? GetJobData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return StorageValues.TryParseJobId(jobId, out long id) ? _database.GetJob(id, withHistory: false)?.ToJobData() : null;
    }

    public override StateData? GetStateData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return StorageValues.TryParseJobId(jobId, out long id) ? _database.GetJob(id, withHistory: false)?.ToStateData() : null;
    }

    public override void AnnounceServer(string serverId, ServerContext context)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(context);
        _database.AnnounceServer(serverId, StorageValues.SerializeServer(context));
    }

    public override void RemoveServer(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        _database.RemoveServer(serverId);
    }

    public override void Heartbeat(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        if (!_database.Heartbeat(serverId))
        {
            throw new BackgroundServerGoneException();
        }
    }

    public override int RemoveTimedOutServers(TimeSpan timeOut)
    {
        if (timeOut.Duration() != timeOut)
        {
            throw new ArgumentException("The `timeOut` value must be positive.", nameof(timeOut));
        }

        return _database.RemoveTimedOutServers(timeOut);
    }

    public override HashSet<string> GetAllItemsFromSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetAllItemsFromSet(key);
    }

    public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
    {
        return GetFirstByLowestScoreFromSet(key, fromScore, toScore, 1).FirstOrDefault();
    }

    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (count <= 0)
        {
            throw new ArgumentException("The value must be a positive number", nameof(count));
        }

        if (toScore < fromScore)
        {
            throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.", nameof(toScore));
        }

        return _database.GetFirstByLowestScoreFromSet(key, fromScore, toScore, count);
    }

    public override long GetSetCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetSetCount(key);
    }

    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return _database.GetSetCount(keys, limit);
    }

    public override bool GetSetContains(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _database.GetSetContains(key, value);
    }

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetRangeFromSet(key, startingFrom, endingAt);
    }

    public override TimeSpan GetSetTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetSetTtl(key);
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyValuePairs);
        _database.SetRangeInHash(key, keyValuePairs);
    }

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetAllEntriesFromHash(key);
    }

    public override string? GetValueFromHash(string key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        return _database.GetValueFromHash(key, name);
    }

    public override long GetHashCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetHashCount(key);
    }

    public override TimeSpan GetHashTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetHashTtl(key);
    }

    public override long GetListCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetListCount(key);
    }

    public override List<string> GetAllItemsFromList(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetAllItemsFromList(key);
    }

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetRangeFromList(key, startingFrom, endingAt);
    }

    public override TimeSpan GetListTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetListTtl(key);
    }

    public override long GetCounter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _database.GetCounter(key);
    }

    public override DateTime GetUtcDateTime() => JobClock.UtcNow;
}

internal sealed class InMemoryTransaction(InMemoryDatabase database, InMemoryConnection connection) : RecordingTransaction
{
    protected override void Apply(IReadOnlyList<StorageOperation> operations) => database.Apply(operations);

    protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    protected override bool IsOwnFetchedJob(FetchedJobBase fetchedJob) => fetchedJob is InMemoryFetchedJob ours && ours.Database == database;
}

internal sealed class InMemoryFetchedJob(InMemoryDatabase database, long entryId, long jobId, string queue, string token, InMemoryStorageOptions options)
    : FetchedJobBase(StorageValues.FormatJobId(jobId), queue, options.KeepFetchedJobsAlive ? options.InvisibilityTimeout / 5 : null)
{
    public InMemoryDatabase Database { get; } = database;

    public long EntryId { get; } = entryId;

    public string Token { get; } = token;

    protected override void Remove() => Database.UpdateFetched(Queue, EntryId, Token, FetchedAction.Remove);

    protected override void ReturnToQueue() => Database.UpdateFetched(Queue, EntryId, Token, FetchedAction.Requeue);

    protected override void KeepAlive() => Database.UpdateFetched(Queue, EntryId, Token, FetchedAction.KeepAlive);
}

internal sealed class InMemoryMonitoringApi(InMemoryDatabase database) : SharedMonitoringApi
{
    protected override long CountJobs(string stateName) => database.CountJobs(stateName);

    protected override IReadOnlyList<StoredJob> GetJobs(string stateName, int from, int count, bool descending) => database.GetJobs(stateName, from, count, descending);

    protected override IReadOnlyList<StoredJob> GetJobs(IReadOnlyCollection<long> ids) => database.GetJobs(ids);

    protected override StoredJob? GetJobWithHistory(long id) => database.GetJob(id, withHistory: true);

    protected override IReadOnlyList<string> GetQueues() => database.GetQueues();

    protected override IReadOnlyList<long> GetEnqueuedJobIds(string queue, int from, int count) => database.GetQueueJobIds(queue, fetched: false, from, count);

    protected override IReadOnlyList<long> GetFetchedJobIds(string queue, int from, int count) => database.GetQueueJobIds(queue, fetched: true, from, count);

    protected override (long Enqueued, long Fetched) GetQueueCounts(string queue) => database.GetQueueCounts(queue);

    protected override IReadOnlyList<Storage.Monitoring.ServerDto> GetServers() => database.GetServers();

    protected override IReadOnlyDictionary<string, long> GetCounters(IReadOnlyCollection<string> keys) => database.GetCounters(keys);

    protected override long GetSetCount(string key) => database.GetSetCount(key);
}
