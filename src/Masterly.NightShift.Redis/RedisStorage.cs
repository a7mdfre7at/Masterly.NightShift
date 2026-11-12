using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Shared;
using StackExchange.Redis;

namespace Masterly.NightShift.Redis;

/// <summary>Options of <see cref="RedisStorage"/>.</summary>
public sealed class RedisStorageOptions
{
    /// <summary>
    /// The prefix of every key. It must hold a hash tag, a non-empty name in braces: it puts all keys in one hash
    /// slot, so a transaction runs as one script on one node of a cluster. Change it to keep several applications
    /// apart in one database. <c>{nightshift}:</c> by default.
    /// </summary>
    public string Prefix { get; set; } = "{nightshift}:";

    /// <summary>
    /// Allows a <see cref="Prefix"/> without a hash tag. Only a standalone server (or Sentinel) works with one:
    /// on a cluster, the keys of a transaction would land in different slots. <c>false</c> by default.
    /// </summary>
    public bool AllowPrefixWithoutHashTag { get; set; }

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

    /// <summary>Whether workers keep their fetched jobs invisible while they run. Tests turn it off to let a fetch go stale.</summary>
    internal bool KeepFetchedJobsAlive { get; set; } = true;

    internal RedisStorageOptions Copy() => (RedisStorageOptions)MemberwiseClone();

    /// <summary>Whether <paramref name="prefix"/> holds a hash tag: a non-empty name between the first '{' and the next '}'.</summary>
    internal static bool HasHashTag(string prefix)
    {
        int open = prefix.IndexOf('{', StringComparison.Ordinal);
        int close = open < 0 ? -1 : prefix.IndexOf('}', open + 1);
        return close > open + 1;
    }
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
    private readonly RedisChannel _channel;
    private readonly Action<RedisChannel, RedisValue> _onEnqueued;
    private TaskCompletionSource _enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    /// <summary>A storage on a new connection, from a StackExchange.Redis configuration string such as <c>localhost:6379</c>.</summary>
    /// <param name="configuration">The configuration string.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults. The storage keeps a copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> are invalid; nothing was connected.</exception>
    public RedisStorage([NotNull] string configuration, [CanBeNull] RedisStorageOptions? options = null)
        : this(Validate(options), configuration ?? throw new ArgumentNullException(nameof(configuration)))
    {
    }

    /// <summary>A storage on a connection the application owns; disposing the storage leaves it open.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults. The storage keeps a copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> are invalid.</exception>
    public RedisStorage([NotNull] IConnectionMultiplexer connection, [CanBeNull] RedisStorageOptions? options = null)
        : this(connection ?? throw new ArgumentNullException(nameof(connection)), Validate(options), ownsConnection: false)
    {
    }

    // The options are validated before connecting, so invalid ones never leave a connection open
    private RedisStorage(RedisStorageOptions settings, string configuration)
        : this(ConnectionMultiplexer.Connect(configuration), settings, ownsConnection: true)
    {
    }

    private RedisStorage(IConnectionMultiplexer connection, RedisStorageOptions settings, bool ownsConnection)
    {
        Connection = connection;
        Settings = settings;
        Prefix = new RedisKey(settings.Prefix);
        _ownsConnection = ownsConnection;
        _channel = RedisChannel.Literal(settings.Prefix + "enqueued");
        _onEnqueued = (_, _) => Signal();

        try
        {
            Connection.GetSubscriber().Subscribe(_channel, _onEnqueued);
        }
        catch
        {
            if (ownsConnection)
            {
                Connection.Dispose();
            }

            throw;
        }
    }

    /// <summary>A copy of the options the storage was created with; changing it changes nothing.</summary>
    public RedisStorageOptions Options => Settings.Copy();

    /// <summary>The connection the storage uses.</summary>
    public IConnectionMultiplexer Connection { get; }

    /// <summary>The options the storage uses, copied when it was created.</summary>
    internal RedisStorageOptions Settings { get; }

    internal RedisKey Prefix { get; }

    internal IDatabase Database => Connection.GetDatabase(Settings.Database);

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
            foreach (RedisKey key in server.Keys(Database.Database, Settings.Prefix + "*", pageSize: 1000))
            {
                if (!KeptByPurge(key.ToString()[Settings.Prefix.Length..], options))
                {
                    doomed.Add(key);
                }

                if (doomed.Count == 1000)
                {
                    removed += Database.KeyDelete([.. doomed]);
                    doomed.Clear();
                }
            }

            if (doomed.Count > 0)
            {
                removed += Database.KeyDelete([.. doomed]);
            }
        }

        LogProvider.GetLogger(typeof(RedisStorage)).Info($"The Redis job storage was purged: {removed} keys deleted.");
    }

    /// <inheritdoc />
    public override void WriteOptionsToLog(ILog logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Info("Using the following options for the Redis job storage:");
        logger.Info($"    Prefix: '{Settings.Prefix}', database: {Settings.Database}.");
        logger.Info($"    Invisibility timeout: {Settings.InvisibilityTimeout}.");
        logger.Info($"    Fetch poll interval: {Settings.FetchPollInterval}.");
        logger.Info($"    Expiration check interval: {Settings.ExpirationCheckInterval}.");
        logger.Info($"    Distributed lock lifetime: {Settings.DistributedLockLifetime}.");
    }

    /// <summary>
    /// Stops listening for enqueued jobs, and closes the connection if the storage opened it. Other storages on
    /// the same connection, even with the same prefix, keep listening.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsConnection)
        {
            Connection.Dispose();
            return;
        }

        Connection.GetSubscriber().Unsubscribe(_channel, _onEnqueued);
    }

    /// <inheritdoc />
    public override string ToString() => $"Redis storage ({Settings.Prefix})";

    /// <summary>
    /// Runs a script. StackExchange.Redis sends a script's text once (SCRIPT LOAD) and its SHA1 afterwards
    /// (EVALSHA), loading it again if the server lost it, so a script isn't sent with every call.
    /// </summary>
    internal RedisResult Evaluate(string script, params RedisValue[] arguments)
    {
        return Database.ScriptEvaluate(script, [Prefix], arguments);
    }

    internal RedisKey Key(string name) => Prefix.Append(name);

    /// <summary>A validated copy of the options, so the storage never sees later changes to them.</summary>
    private static RedisStorageOptions Validate(RedisStorageOptions? options)
    {
        RedisStorageOptions settings = options?.Copy() ?? new RedisStorageOptions();
        if (string.IsNullOrEmpty(settings.Prefix))
        {
            throw new ArgumentException("The key prefix can't be empty.", nameof(options));
        }

        if (!settings.AllowPrefixWithoutHashTag && !RedisStorageOptions.HasHashTag(settings.Prefix))
        {
            throw new ArgumentException(
                $"The key prefix '{settings.Prefix}' has no hash tag, such as '{{nightshift}}:': on a cluster, a transaction's keys would land in different slots. Set AllowPrefixWithoutHashTag to use it on a standalone server.",
                nameof(options));
        }

        if (settings.InvisibilityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The invisibility timeout must be positive.", nameof(options));
        }

        if (settings.FetchPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The fetch poll interval must be positive.", nameof(options));
        }

        if (settings.ExpirationCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The expiration check interval must be positive.", nameof(options));
        }

        if (settings.DistributedLockLifetime < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("The lock lifetime must be at least a second.", nameof(options));
        }

        return settings;
    }

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

    /// <summary>The milliseconds left before <paramref name="expireAt"/>, at least one.</summary>
    public static long TimeToLive(DateTime expireAt)
    {
        double left = (expireAt - JobClock.UtcNow).TotalMilliseconds;
        return left >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)left);
    }

    /// <summary>
    /// The Redis range (LRANGE, ZRANGE, LTRIM) of positions numbered as SQL Server numbers them: from 0, so a
    /// negative position comes before the first item instead of counting from the end. <c>false</c> when the range
    /// holds no position from 0 on, such as one that ends before it starts.
    /// </summary>
    public static bool TryGetRange(int startingFrom, int endingAt, out long start, out long stop)
    {
        start = Math.Max(0, startingFrom);
        stop = endingAt;
        return stop >= start;
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

        if (!values.TryGetValue("InvocationData", out string? payload))
        {
            return null;
        }

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
