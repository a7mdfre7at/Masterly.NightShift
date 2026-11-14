using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Monitoring;
using Masterly.NightShift.Storage.Shared;
using StackExchange.Redis;

namespace Masterly.NightShift.Redis;

internal sealed class RedisConnection(RedisStorage storage) : JobStorageConnection, ILeaseStore
{
    private readonly ReentrantLocks _locks = new();

    private IDatabase Database => storage.Database;

    public override IWriteOnlyTransaction CreateWriteTransaction() => new RedisTransaction(storage, this);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _locks.Acquire(resource, () => LeaseLock.Acquire(this, resource, timeout, storage.Settings.DistributedLockLifetime));
    }

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(parameters);

        StoredJob stored = StoredJob.Create(0, job, parameters, createdAt, StorageValues.ExpireAt(createdAt, expireIn));
        DateTime expireAt = stored.ExpireAt!.Value; // Set by Create

        List<RedisValue> arguments =
        [
            stored.InvocationData,
            stored.Arguments ?? string.Empty,
            RedisValues.Ticks(createdAt),
            RedisValues.Ticks(expireAt),
            RedisValues.TimeToLive(expireAt),
            stored.Parameters.Count,
        ];

        foreach (KeyValuePair<string, string?> parameter in stored.Parameters)
        {
            arguments.Add(parameter.Key);
            arguments.Add(RedisValues.Encode(parameter.Value));
        }

        return ((long)storage.Evaluate(RedisScripts.CreateJob, [.. arguments])).ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override string? GetQueueEntryId(IFetchedJob fetchedJob)
    {
        ArgumentNullException.ThrowIfNull(fetchedJob);
        return fetchedJob is RedisFetchedJob ours ? ours.Entry : null;
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queues);
        if (queues.Length == 0)
        {
            throw new ArgumentException("Queue array must be non-empty.", nameof(queues));
        }

        using QueueSignals.Listener listener = storage.Signals.Listen(queues);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Before the look, so a job enqueued while the script runs wakes the wait that follows
            listener.Reset();
            string token = Guid.NewGuid().ToString("N");

            // The script reads the time from the server, so clients with skewed clocks agree on what is stale.
            // Cancellation is checked between scripts, never during one: a reply lost to it would leave an entry
            // taken that no worker runs before the invisibility timeout.
            RedisValue[] arguments = [(long)storage.Settings.InvisibilityTimeout.TotalMilliseconds, token, .. queues.Select(static x => (RedisValue)x)];
            RedisResult result = storage.Evaluate(RedisScripts.Fetch, arguments);

            if (!result.IsNull)
            {
                RedisResult[] taken = (RedisResult[])result!; // A table of entry and queue
                return new RedisFetchedJob(storage, (string)taken[0]!, (string)taken[1]!, token); // Entries and queue names are never null
            }

            listener.Wait(storage.Settings.FetchPollInterval, cancellationToken);
        }
    }

    public override void SetJobParameter(string id, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        if (!StorageValues.TryParseJobId(id, out long jobId))
        {
            return;
        }

        storage.Evaluate(RedisScripts.Apply, "param", jobId, name, RedisValues.Encode(value));
    }

    public override string? GetJobParameter(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        if (!StorageValues.TryParseJobId(id, out long jobId))
        {
            return null;
        }

        return RedisValues.Decode(Database.HashGet(storage.Key($"job:{jobId}:params"), name));
    }

    public override JobData? GetJobData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (!StorageValues.TryParseJobId(jobId, out long id))
        {
            return null;
        }

        StoredJob? job = ReadJob(id);
        if (job == null)
        {
            return null;
        }

        job.Parameters = RedisValues.ToParameters([.. Database.HashGetAll(storage.Key($"job:{id}:params")).SelectMany(static x => new[] { RedisResult.Create(x.Name), RedisResult.Create(x.Value) })]);
        return job.ToJobData();
    }

    public override StateData? GetStateData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (!StorageValues.TryParseJobId(jobId, out long id))
        {
            return null;
        }

        StoredState? state = RedisValues.DeserializeState(Database.HashGet(storage.Key($"job:{id}"), "State"));
        return state == null ? null : new StateData { Name = state.Name, Reason = state.Reason, Data = state.ReadData() };
    }

    public override void AnnounceServer(string serverId, ServerContext context)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(context);

        storage.Evaluate(RedisScripts.AnnounceServer, serverId, StorageValues.SerializeServer(context));
    }

    public override void RemoveServer(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);

        ITransaction transaction = Database.CreateTransaction();
        _ = transaction.HashDeleteAsync(storage.Key("servers"), serverId);
        _ = transaction.SortedSetRemoveAsync(storage.Key("servers:heartbeat"), serverId);
        transaction.Execute();
    }

    public override void Heartbeat(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);

        // One script checks and beats, so a server removed in between isn't brought back half
        if ((long)storage.Evaluate(RedisScripts.Heartbeat, serverId) == 0)
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

        long timeOutMilliseconds = (long)Math.Min(timeOut.TotalMilliseconds, long.MaxValue / 2);
        return (int)(long)storage.Evaluate(RedisScripts.RemoveTimedOutServers, timeOutMilliseconds);
    }

    public override HashSet<string> GetAllItemsFromSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return [.. Database.SortedSetRangeByRank(SetKey(key)).Select(static x => x.ToString())];
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

        return [.. Database.SortedSetRangeByScore(SetKey(key), fromScore, toScore, take: count).Select(static x => x.ToString())];
    }

    public override long GetSetCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Database.SortedSetLength(SetKey(key));
    }

    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        long total = 0;
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            total += Database.SortedSetLength(SetKey(key));
            if (total >= limit)
            {
                return limit;
            }
        }

        return total;
    }

    public override bool GetSetContains(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return Database.SortedSetScore(SetKey(key), value).HasValue;
    }

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!RedisValues.TryGetRange(startingFrom, endingAt, out long start, out long stop))
        {
            return [];
        }

        return [.. Database.SortedSetRangeByRank(SetKey(key), start, stop).Select(static x => x.ToString())];
    }

    public override TimeSpan GetSetTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive(SetKey(key));
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyValuePairs);

        HashEntry[] fields = [.. keyValuePairs.Select(static x => new HashEntry(x.Key, x.Value ?? string.Empty))];
        if (fields.Length > 0)
        {
            Database.HashSet(HashKey(key), fields);
        }
    }

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        HashEntry[] fields = Database.HashGetAll(HashKey(key));
        return fields.Length == 0 ? null : fields.ToDictionary(static x => x.Name.ToString(), static x => x.Value.ToString(), StringComparer.Ordinal);
    }

    public override string? GetValueFromHash(string key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        return Database.HashGet(HashKey(key), name);
    }

    public override long GetHashCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Database.HashLength(HashKey(key));
    }

    public override TimeSpan GetHashTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive(HashKey(key));
    }

    public override long GetListCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Database.ListLength(ListKey(key));
    }

    public override List<string> GetAllItemsFromList(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return [.. Database.ListRange(ListKey(key)).Select(static x => x.ToString())];
    }

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!RedisValues.TryGetRange(startingFrom, endingAt, out long start, out long stop))
        {
            return [];
        }

        return [.. Database.ListRange(ListKey(key), start, stop).Select(static x => x.ToString())];
    }

    public override TimeSpan GetListTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive(ListKey(key));
    }

    public override long GetCounter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RedisValue value = Database.StringGet(storage.Key($"counter:{key}"));
        return value.HasValue ? (long)value : 0;
    }

    public override DateTime GetUtcDateTime()
    {
        RedisResult[] time = (RedisResult[])storage.Evaluate(RedisScripts.Time)!; // TIME answers seconds and microseconds
        long seconds = (long)time[0];
        long microseconds = (long)time[1];
        return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(microseconds * 10);
    }

    /// <summary>Releases the locks whose handles weren't disposed, as SQL Server does when a connection closes.</summary>
    public override void Dispose()
    {
        _locks.ReleaseAll();
        base.Dispose();
    }

    bool ILeaseStore.TryTake(string resource, string owner, DateTime expireAt)
    {
        return Database.StringSet(LockKey(resource), owner, TimeSpan.FromMilliseconds(RedisValues.TimeToLive(expireAt)), When.NotExists);
    }

    bool ILeaseStore.TryRenew(string resource, string owner, DateTime expireAt)
    {
        return (long)Database.ScriptEvaluate(RedisScripts.RenewLock, [LockKey(resource)], [owner, RedisValues.TimeToLive(expireAt)]) == 1;
    }

    void ILeaseStore.Release(string resource, string owner)
    {
        try
        {
            Database.ScriptEvaluate(RedisScripts.ReleaseLock, [LockKey(resource)], [owner]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The lease runs out by itself; another owner takes the lock once it does
            LogProvider.GetLogger(typeof(RedisConnection)).WarnException($"Couldn't release the lock on '{resource}'.", ex);
        }
    }

    internal StoredJob? ReadJob(long id)
    {
        HashEntry[] fields = Database.HashGetAll(storage.Key($"job:{id}"));
        return RedisValues.ToJob(id, [.. fields.SelectMany(static x => new[] { RedisResult.Create(x.Name), RedisResult.Create(x.Value) })]);
    }

    private TimeSpan TimeToLive(RedisKey key)
    {
        TimeSpan? ttl = Database.KeyTimeToLive(key);
        return ttl ?? TimeSpan.FromSeconds(-1);
    }

    private RedisKey SetKey(string key) => storage.Key($"set:{key}");

    private RedisKey HashKey(string key) => storage.Key($"hash:{key}");

    private RedisKey ListKey(string key) => storage.Key($"list:{key}");

    private RedisKey LockKey(string resource) => storage.Key($"lock:{resource}");
}

internal sealed class RedisTransaction(RedisStorage storage, RedisConnection connection) : RecordingTransaction
{
    protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    protected override void Apply(IReadOnlyList<StorageOperation> operations)
    {
        List<RedisValue> arguments = [];

        foreach (StorageOperation operation in operations)
        {
            switch (operation)
            {
                case ExpireJobOperation expire when expire.ExpireAt.HasValue:
                    arguments.AddRange(["expire-job", expire.JobId, RedisValues.TimeToLive(expire.ExpireAt.Value), RedisValues.Ticks(expire.ExpireAt.Value)]);
                    break;
                case ExpireJobOperation persist:
                    arguments.AddRange(["expire-job", persist.JobId, string.Empty, string.Empty]);
                    break;
                case AddJobStateOperation state:
                    arguments.AddRange(["state", state.JobId, state.SetCurrent ? "1" : "0", RedisValues.SerializeState(state.State), state.State.Name]);
                    break;
                case AddToQueueOperation queue:
                    arguments.AddRange(["queue", queue.Queue, queue.JobId]);
                    break;
                case RemoveFromQueueOperation removal:
                    RedisFetchedJob fetched = (RedisFetchedJob)removal.FetchedJob; // IsOwnFetchedJob refused the others
                    arguments.AddRange(["dequeue", fetched.Queue, fetched.Entry, fetched.Token]);
                    break;
                case SetJobParameterOperation parameter:
                    arguments.AddRange(["param", parameter.JobId, parameter.Name, RedisValues.Encode(parameter.Value)]);
                    break;
                case CounterOperation counter:
                    arguments.AddRange(["counter", counter.Key, counter.Value, counter.ExpireAt.HasValue ? RedisValues.TimeToLive(counter.ExpireAt.Value) : string.Empty]);
                    break;
                case AddToSetOperation add:
                    arguments.AddRange(["zadd", add.Key, add.Value, add.Score]);
                    break;
                case AddRangeToSetOperation range:
                    arguments.AddRange(["zadd-range", range.Key, range.Values.Count]);
                    arguments.AddRange(range.Values.Select(static x => (RedisValue)x));
                    break;
                case RemoveFromSetOperation remove:
                    arguments.AddRange(["zrem", remove.Key, remove.Value]);
                    break;
                case RemoveSetOperation removeSet:
                    arguments.AddRange(["del", "set", removeSet.Key]);
                    break;
                case InsertToListOperation insert:
                    arguments.AddRange(["lpush", insert.Key, insert.Value]);
                    break;
                case RemoveFromListOperation removeFromList:
                    arguments.AddRange(["lrem", removeFromList.Key, removeFromList.Value]);
                    break;
                case TrimListOperation trim:
                    // Positions count from the newest item as on SQL Server, where negative ones keep nothing; Redis
                    // would count them from the end, so the range is clamped first, and an empty one empties the list
                    arguments.AddRange(RedisValues.TryGetRange(trim.KeepStartingFrom, trim.KeepEndingAt, out long start, out long stop)
                        ? ["ltrim", trim.Key, start, stop]
                        : ["ltrim", trim.Key, 1, 0]);
                    break;
                case SetRangeInHashOperation hash:
                    arguments.AddRange(["hset", hash.Key, hash.Fields.Count]);
                    foreach (KeyValuePair<string, string> field in hash.Fields)
                    {
                        arguments.Add(field.Key);
                        arguments.Add(field.Value ?? string.Empty);
                    }

                    break;
                case RemoveHashOperation removeHash:
                    arguments.AddRange(["del", "hash", removeHash.Key]);
                    break;
                case ExpireKeyOperation expireKey:
                    arguments.AddRange(["expire", KindName(expireKey.Kind), expireKey.Key, expireKey.ExpireAt.HasValue ? RedisValues.TimeToLive(expireKey.ExpireAt.Value) : string.Empty]);
                    break;
                default:
                    throw new NotSupportedException($"Unknown storage operation {operation.GetType().Name}.");
            }
        }

        if (arguments.Count > 0)
        {
            storage.Evaluate(RedisScripts.Apply, [.. arguments]);
        }
    }

    protected override bool IsOwnFetchedJob(FetchedJobBase fetchedJob) => fetchedJob is RedisFetchedJob ours && ours.Storage == storage;

    private static string KindName(KeyKind kind) => kind switch
    {
        KeyKind.Set => "set",
        KeyKind.List => "list",
        _ => "hash",
    };
}

internal sealed class RedisFetchedJob(RedisStorage storage, string entry, string queue, string token)
    : FetchedJobBase(StorageValues.FormatJobId(RedisValues.JobIdOfEntry(entry)), queue, storage.Settings.KeepFetchedJobsAlive ? storage.Settings.InvisibilityTimeout / 5 : null)
{
    public RedisStorage Storage { get; } = storage;

    public string Entry { get; } = entry;

    public string Token { get; } = token;

    protected override void Remove() => Act("remove");

    protected override void ReturnToQueue() => Act("requeue");

    protected override void KeepAlive() => Act("keepalive");

    private void Act(string action)
    {
        Storage.Evaluate(RedisScripts.Fetched, action, Queue, Entry, Token);
    }
}

internal sealed class RedisMonitoringApi(RedisStorage storage) : SharedMonitoringApi
{
    private IDatabase Database => storage.Database;

    protected override long CountJobs(string stateName) => Database.SortedSetLength(StateKey(stateName));

    protected override IReadOnlyList<StoredJob> GetJobs(string stateName, int from, int count, bool descending)
    {
        if (count <= 0)
        {
            return [];
        }

        RedisValue[] ids = Database.SortedSetRangeByRank(StateKey(stateName), from, from + count - 1, descending ? Order.Descending : Order.Ascending);
        return ReadJobs([.. ids.Select(static x => (long)x)]);
    }

    protected override IReadOnlyList<StoredJob> GetJobs(IReadOnlyCollection<long> ids) => ReadJobs(ids);

    protected override StoredJob? GetJobWithHistory(long id)
    {
        RedisResult[] parts = (RedisResult[])storage.Evaluate(RedisScripts.ReadJobDetails, id)!; // Three tables
        StoredJob? job = RedisValues.ToJob(id, (RedisResult[])parts[0]!);
        if (job == null)
        {
            return null;
        }

        job.Parameters = RedisValues.ToParameters((RedisResult[])parts[1]!);
        job.History = [.. ((RedisResult[])parts[2]!).Select(static x => RedisValues.DeserializeState((string?)x)!).Reverse()]; // Every history entry is a state
        return job;
    }

    protected override IReadOnlyList<string> GetQueues()
    {
        // One script reads every queue that has an entry, instead of three commands per queue ever used
        return [.. ((RedisResult[])storage.Evaluate(RedisScripts.ReadQueues)!).Select(static x => (string)x!)]; // A table of queue names
    }

    protected override IReadOnlyList<long> GetEnqueuedJobIds(string queue, int from, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        // Entries are pushed on the left and taken from the right, so the oldest is last
        RedisValue[] entries = Database.ListRange(storage.Key($"queue:{queue}"), -(from + count), -(from + 1));
        return [.. Enumerable.Reverse(entries).Select(static x => RedisValues.JobIdOfEntry(x.ToString()))];
    }

    protected override IReadOnlyList<long> GetFetchedJobIds(string queue, int from, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        RedisValue[] entries = Database.SortedSetRangeByRank(storage.Key($"queue:{queue}:fetched"), from, from + count - 1);
        return [.. entries.Select(static x => RedisValues.JobIdOfEntry(x.ToString()))];
    }

    protected override (long Enqueued, long Fetched) GetQueueCounts(string queue)
    {
        // One script reads both, in one round trip
        RedisResult[] counts = (RedisResult[])storage.Evaluate(RedisScripts.CountQueue, queue)!; // Two numbers
        return ((long)counts[0], (long)counts[1]);
    }

    protected override IReadOnlyList<ServerDto> GetServers()
    {
        Dictionary<string, DateTime> heartbeats = Database.SortedSetRangeByRankWithScores(storage.Key("servers:heartbeat"))
            .ToDictionary(static x => x.Element.ToString(), static x => DateTime.UnixEpoch.AddMilliseconds(x.Score), StringComparer.Ordinal);

        return [.. Database.HashGetAll(storage.Key("servers")).Select(x =>
        {
            StoredServerData data = StorageValues.DeserializeServer(x.Value);
            return new ServerDto
            {
                Name = x.Name.ToString(),
                Heartbeat = heartbeats.TryGetValue(x.Name.ToString(), out DateTime heartbeat) ? heartbeat : null,
                Queues = data.Queues ?? [],
                StartedAt = data.StartedAt ?? DateTime.MinValue,
                WorkersCount = data.WorkerCount,
            };
        })];
    }

    protected override IReadOnlyDictionary<string, long> GetCounters(IReadOnlyCollection<string> keys)
    {
        string[] names = [.. keys];
        RedisValue[] values = Database.StringGet([.. names.Select(x => storage.Key($"counter:{x}"))]);
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            if (values[i].HasValue)
            {
                result[names[i]] = (long)values[i];
            }
        }

        return result;
    }

    protected override long GetSetCount(string key) => Database.SortedSetLength(storage.Key($"set:{key}"));

    private List<StoredJob> ReadJobs(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        RedisResult[] rows = (RedisResult[])storage.Evaluate(RedisScripts.ReadJobs, [.. ids.Select(static x => (RedisValue)x)])!; // Id, fields pairs
        List<StoredJob> jobs = [];
        for (int i = 0; i + 1 < rows.Length; i += 2)
        {
            StoredJob? job = RedisValues.ToJob((long)rows[i], (RedisResult[])rows[i + 1]!); // Each job's fields are a table
            if (job != null)
            {
                jobs.Add(job);
            }
        }

        return jobs;
    }

    private RedisKey StateKey(string stateName) => storage.Key($"state:{stateName.ToLowerInvariant()}");
}

/// <summary>Removes expired jobs from the state lists; Redis removes their keys itself.</summary>
internal sealed class RedisExpirationManager(RedisStorage storage) : IBackgroundProcess
{
    private const int BatchSize = 1000;
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(RedisExpirationManager));

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            long removed;
            do
            {
                removed = (long)storage.Evaluate(RedisScripts.RemoveExpiredJobs, BatchSize);
            }
            while (removed == BatchSize && !context.IsStopping);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.ErrorException("Couldn't remove expired jobs from the Redis storage.", ex);
        }

        context.Wait(storage.Settings.ExpirationCheckInterval);
    }

    public override string ToString() => "Redis expiration manager";
}
