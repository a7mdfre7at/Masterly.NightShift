using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Shared;
using StackExchange.Redis;

namespace Masterly.NightShift.Redis;

/// <summary>Options of <see cref="RedisStorage"/>.</summary>
public sealed class RedisStorageOptions
{
    /// <summary>
    /// The prefix of every key. Keep the braces: they make it a hash tag, so all keys land in one hash slot and
    /// a transaction runs as one script on one node of a cluster. Change it to keep several applications apart
    /// in one database. <c>{nightshift}:</c> by default.
    /// </summary>
    public string Prefix { get; set; } = "{nightshift}:";

    /// <summary>The database number, or -1 (the default) for the connection's default database.</summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// How long a fetched job stays invisible to other workers after its worker stopped keeping it alive. A
    /// worker keeps its job alive while it runs. Thirty minutes by default.
    /// </summary>
    public TimeSpan InvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a worker waits before it looks at its queues again. Enqueuing a job wakes the waiting workers
    /// through a pub/sub channel, so this only matters when a message is lost. Fifteen seconds by default.
    /// </summary>
    public TimeSpan FetchPollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often expired jobs are removed from the state lists. One minute by default.</summary>
    public TimeSpan ExpirationCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a distributed lock outlives a process that died holding it. A live holder renews it every third
    /// of this time. Thirty seconds by default.
    /// </summary>
    public TimeSpan DistributedLockLifetime { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// A job storage on Redis (5.0 or later, standalone, Sentinel or cluster), through StackExchange.Redis. Each
/// transaction is applied by one Lua script, so it is atomic; workers are woken through pub/sub when jobs are
/// enqueued. Dispose the storage to close a connection it opened itself.
/// </summary>
public sealed class RedisStorage : JobStorage, IDisposable
{
    private readonly Dictionary<string, bool> _features = StorageRules.Features(typeof(RedisFetchedJob));
    private readonly bool _ownsConnection;
    private TaskCompletionSource _enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A storage on a new connection, from a StackExchange.Redis configuration string such as <c>localhost:6379</c>.</summary>
    /// <param name="configuration">The configuration string.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <c>null</c>.</exception>
    public RedisStorage([NotNull] string configuration, [CanBeNull] RedisStorageOptions? options = null)
        : this(ConnectionMultiplexer.Connect(configuration ?? throw new ArgumentNullException(nameof(configuration))), options, ownsConnection: true)
    {
    }

    /// <summary>A storage on a connection the application owns; disposing the storage leaves it open.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <c>null</c>.</exception>
    public RedisStorage([NotNull] IConnectionMultiplexer connection, [CanBeNull] RedisStorageOptions? options = null)
        : this(connection, options, ownsConnection: false)
    {
    }

    private RedisStorage(IConnectionMultiplexer connection, RedisStorageOptions? options, bool ownsConnection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        options ??= new RedisStorageOptions();
        if (string.IsNullOrEmpty(options.Prefix)) throw new ArgumentException("The key prefix can't be empty.", nameof(options));
        if (options.InvisibilityTimeout <= TimeSpan.Zero) throw new ArgumentException("The invisibility timeout must be positive.", nameof(options));
        if (options.FetchPollInterval <= TimeSpan.Zero) throw new ArgumentException("The fetch poll interval must be positive.", nameof(options));
        if (options.ExpirationCheckInterval <= TimeSpan.Zero) throw new ArgumentException("The expiration check interval must be positive.", nameof(options));
        if (options.DistributedLockLifetime < TimeSpan.FromSeconds(1)) throw new ArgumentException("The lock lifetime must be at least a second.", nameof(options));

        Connection = connection;
        Options = options;
        Prefix = new RedisKey(options.Prefix);
        _ownsConnection = ownsConnection;

        Connection.GetSubscriber().Subscribe(RedisChannel.Literal(options.Prefix + "enqueued"), (_, _) => Signal());
    }

    /// <summary>The options the storage was created with.</summary>
    public RedisStorageOptions Options { get; }

    /// <summary>The connection the storage uses.</summary>
    public IConnectionMultiplexer Connection { get; }

    internal RedisKey Prefix { get; }

    internal IDatabase Database => Connection.GetDatabase(Options.Database);

    internal Task Enqueued => Volatile.Read(ref _enqueued).Task;

    /// <inheritdoc />
    public override IMonitoringApi GetMonitoringApi() => new RedisMonitoringApi(this);

    /// <inheritdoc />
    public override IStorageConnection GetConnection() => new RedisConnection(this);

    /// <inheritdoc />
    public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
    {
        yield return new RedisExpirationManager(this);
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
        long removed = 0;

        foreach (IServer server in Connection.GetServers().Where(static x => !x.IsReplica && x.IsConnected))
        {
            List<RedisKey> doomed = [];
            foreach (RedisKey key in server.Keys(Database.Database, Options.Prefix + "*", pageSize: 1000))
            {
                if (!KeptByPurge(key.ToString()[Options.Prefix.Length..], options)) doomed.Add(key);
                if (doomed.Count == 1000)
                {
                    removed += Database.KeyDelete([.. doomed]);
                    doomed.Clear();
                }
            }

            if (doomed.Count > 0) removed += Database.KeyDelete([.. doomed]);
        }

        LogProvider.GetLogger(typeof(RedisStorage)).Info($"The Redis job storage was purged: {removed} keys deleted.");
    }

    /// <inheritdoc />
    public override void WriteOptionsToLog(ILog logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Info("Using the following options for the Redis job storage:");
        logger.Info($"    Prefix: '{Options.Prefix}', database: {Options.Database}.");
        logger.Info($"    Invisibility timeout: {Options.InvisibilityTimeout}.");
        logger.Info($"    Fetch poll interval: {Options.FetchPollInterval}.");
        logger.Info($"    Expiration check interval: {Options.ExpirationCheckInterval}.");
        logger.Info($"    Distributed lock lifetime: {Options.DistributedLockLifetime}.");
    }

    /// <summary>Stops listening for enqueued jobs, and closes the connection if the storage opened it.</summary>
    public void Dispose()
    {
        Connection.GetSubscriber().Unsubscribe(RedisChannel.Literal(Options.Prefix + "enqueued"));
        if (_ownsConnection) Connection.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => $"Redis storage ({Options.Prefix})";

    internal RedisResult Evaluate(string script, params RedisValue[] arguments)
    {
        return Database.ScriptEvaluate(script, [Prefix], arguments);
    }

    internal RedisKey Key(string name) => Prefix.Append(name);

    private void Signal()
    {
        Interlocked.Exchange(ref _enqueued, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private static bool KeptByPurge(string key, StoragePurgeOptions options)
    {
        return key.StartsWith("set:", StringComparison.Ordinal) ? StorageRules.PurgeKeepsSet(key[4..], options)
            : key.StartsWith("hash:", StringComparison.Ordinal) ? StorageRules.PurgeKeepsHash(key[5..], options)
            : key.StartsWith("list:", StringComparison.Ordinal) ? StorageRules.PurgeKeepsList(key[5..], options)
            : key.StartsWith("server", StringComparison.Ordinal) ? options.KeepServers
            : key.StartsWith("lock:", StringComparison.Ordinal)
              || key is "job-id" or "queue-entry-id";
    }
}

/// <summary>How values are written to Redis.</summary>
internal static class RedisValues
{
    /// <summary>Stands for <c>null</c> in a job parameter, which Redis can't store.</summary>
    public const string Null = "\u0000null";

    public static string Ticks(DateTime value) => value.Ticks.ToString(CultureInfo.InvariantCulture);

    public static DateTime FromTicks(string value) => new(long.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Utc);

    public static long UnixMilliseconds(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>The milliseconds left before <paramref name="expireAt"/>, at least one.</summary>
    public static long TimeToLive(DateTime expireAt)
    {
        double left = (expireAt - JobClock.UtcNow).TotalMilliseconds;
        return left >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)left);
    }

    public static string? Encode(string? value) => value ?? Null;

    public static string? Decode(string? value) => value == Null ? null : value;

    public static string SerializeState(StoredState state)
    {
        return SerializationHelper.Serialize(new RedisStateRecord
        {
            Name = state.Name,
            Reason = state.Reason,
            CreatedAt = state.CreatedAt.Ticks,
            Data = state.Data,
        });
    }

    public static StoredState? DeserializeState(string? json)
    {
        RedisStateRecord? record = json != null ? SerializationHelper.Deserialize<RedisStateRecord>(json) : null;
        return record == null ? null : new StoredState(record.Name, record.Reason, new DateTime(record.CreatedAt, DateTimeKind.Utc), record.Data);
    }

    /// <summary>A job from its hash fields, given as name/value pairs.</summary>
    public static StoredJob? ToJob(long id, RedisResult[] fields)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int i = 0; i + 1 < fields.Length; i += 2)
        {
            values[(string)fields[i]!] = (string)fields[i + 1]!; // Hash fields and values are never null
        }

        if (!values.TryGetValue("InvocationData", out string? payload)) return null;

        return new StoredJob
        {
            Id = id,
            InvocationData = payload,
            Arguments = values.TryGetValue("Arguments", out string? arguments) && arguments.Length > 0 ? arguments : null,
            CreatedAt = FromTicks(values["CreatedAt"]),
            ExpireAt = values.TryGetValue("ExpireAt", out string? expireAt) ? FromTicks(expireAt) : null,
            StateName = values.GetValueOrDefault("StateName"),
            State = DeserializeState(values.GetValueOrDefault("State")),
        };
    }

    public static Dictionary<string, string?> ToParameters(RedisResult[] fields)
    {
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal);
        for (int i = 0; i + 1 < fields.Length; i += 2)
        {
            parameters[(string)fields[i]!] = Decode((string?)fields[i + 1]); // Hash fields are never null
        }

        return parameters;
    }

    /// <summary>The job id of a queue entry, written as <c>entry:job</c>.</summary>
    public static long JobIdOfEntry(string entry) => long.Parse(entry.AsSpan(entry.IndexOf(':', StringComparison.Ordinal) + 1), CultureInfo.InvariantCulture);
}

/// <summary>A state as JSON in Redis.</summary>
internal sealed class RedisStateRecord
{
    public string Name { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public long CreatedAt { get; set; }

    public string? Data { get; set; }
}
