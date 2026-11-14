using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift;

/// <summary>
/// The state of the processing switch: whether new work is held, since when, and why.
/// </summary>
public sealed class DrainModeStatus
{
    internal DrainModeStatus(bool active, DateTime? since, string? reason)
        : this(active, since, reason, EmptyQueues)
    {
    }

    internal DrainModeStatus(bool active, DateTime? since, string? reason, IReadOnlyDictionary<string, string?> pausedQueues)
        : this(active, since, reason, pausedQueues, EmptyQueues)
    {
    }

    internal DrainModeStatus(bool active, DateTime? since, string? reason, IReadOnlyDictionary<string, string?> pausedQueues, IReadOnlyDictionary<string, string?> pausedServers)
    {
        Active = active;
        Since = since;
        Reason = reason;
        PausedQueues = pausedQueues;
        PausedServers = pausedServers;
    }

    /// <summary>Servers paused on their own, with the reason given for each (may be <c>null</c>).</summary>
    public IReadOnlyDictionary<string, string?> PausedServers { get; }

    /// <summary>Whether the server holds the jobs it picks up, either by the global switch or on its own. Needs the full server ID.</summary>
    public bool IsServerPaused(string serverId)
    {
        return Active || (serverId != null && PausedServers.ContainsKey(serverId));
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyQueues = new Dictionary<string, string?>();

    /// <summary>Queues paused on their own, with the reason given for each (may be <c>null</c>).</summary>
    public IReadOnlyDictionary<string, string?> PausedQueues { get; }

    /// <summary>Whether the queue is held, either by the global switch or on its own.</summary>
    public bool IsQueuePaused(string queue)
    {
        return Active || (queue != null && PausedQueues.ContainsKey(queue));
    }

    /// <summary>Whether workers hold new jobs instead of performing them.</summary>
    public bool Active { get; }

    /// <summary>When the switch was turned on.</summary>
    public DateTime? Since { get; }

    /// <summary>The reason given when the switch was turned on.</summary>
    public string? Reason { get; }

    /// <summary>The inactive status.</summary>
    public static readonly DrainModeStatus Inactive = new(false, null, null);
}

/// <summary>
/// A cluster-wide switch that pauses processing without stopping servers: while it is on, every worker that
/// picks up a job hands it back to the Scheduled state for <see cref="HoldInterval"/> instead of running it, and
/// jobs that are already running finish normally. Turn it on before a deploy or a database maintenance window,
/// from the dashboard's Servers page or with <see cref="Enable"/>.
/// </summary>
/// <remarks>
/// Every write takes the distributed lock <c>lock:hangfire:control</c>, waits for it for at most 15 seconds, and
/// throws a <see cref="DistributedLockTimeoutException"/> when it can't get it: a resume removes its field by
/// writing the hash again, which must lose nothing another write made meanwhile.
/// </remarks>
public static class DrainMode
{
    /// <summary>The storage hash holding the switch.</summary>
    public const string HashKey = "hangfire:control";

    /// <summary>Hash fields holding paused queues: <c>Queue:{name}</c> = reason.</summary>
    public const string QueueFieldPrefix = "Queue:";

    /// <summary>
    /// Hash fields holding paused servers: <c>Server:{id}</c> = reason. A field longer than a storage keeps
    /// (100 characters, so an ID of more than 93 characters, which long host names give) is stored as
    /// <c>Server:#{SHA-256 of the lowercased ID}</c> = <c>{id}</c>, a line feed, then the reason.
    /// </summary>
    public const string ServerFieldPrefix = "Server:";

    // What a resumed queue or server field held in earlier builds, before a resume removed the field. Still read as
    // resumed, and dropped by the next rewrite of the hash; a reason equal to it is stored with a space added, so a
    // pause never reads as resumed
    private const string ResumedMarker = "[resumed]";

    // Written by earlier builds, still read as resumed
    private const string LegacyResumedMarker = "\u0000resumed";

    // SQL Server keeps hash field names of up to 100 characters and cuts longer ones silently
    private const int MaxFieldLength = 100;
    private const char HashedServerMarker = '#';
    private const char HashedServerIdSeparator = '\n';
    private const int HashedServerFieldLength = 7 + 1 + 64; // "Server:" + "#" + SHA-256 in hex

    /// <summary>
    /// The distributed lock every write of the hash takes. A storage can't remove one field of a hash, so a resume
    /// writes the hash again without it; under this lock, no write made meanwhile is lost.
    /// </summary>
    internal const string LockResource = "lock:" + HashKey;

    /// <summary>How long a write waits for <see cref="LockResource"/>.</summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    // One cache per storage, so two storages in one process never share an answer
    private static readonly ConditionalWeakTable<JobStorage, CacheSlot> StorageCaches = [];

    // For the overloads that don't name the storage
    private static readonly CacheSlot SharedCache = new();

    // Raised by InvalidateCache: an answer read before is no longer used
    private static long _cacheGeneration;

    /// <summary>Replaceable for tests.</summary>
    internal static Func<DateTime> Clock { get; set; } = static () => JobClock.UtcNow;

    /// <summary>How long a held job waits in the Scheduled state before a worker looks at it again. Default 30 seconds.</summary>
    public static TimeSpan HoldInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a process trusts the last answer before asking the storage again. Default 2 seconds, so the
    /// switch costs one small read every couple of seconds per process rather than one per job.
    /// </summary>
    public static TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Reads the switch directly from the storage.</summary>
    public static DrainModeStatus Read([NotNull] IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return Parse(connection.GetAllEntriesFromHash(HashKey));
    }

    private static DrainModeStatus Parse(Dictionary<string, string>? hash)
    {
        if (hash == null)
        {
            return DrainModeStatus.Inactive;
        }

        Dictionary<string, string?> pausedQueues = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> pausedServers = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in hash)
        {
            if (IsResumed(pair.Value))
            {
                continue;
            }

            if (pair.Key.StartsWith(QueueFieldPrefix, StringComparison.Ordinal) && pair.Key.Length > QueueFieldPrefix.Length)
            {
                pausedQueues[pair.Key[QueueFieldPrefix.Length..]] = string.IsNullOrEmpty(pair.Value) ? null : pair.Value;
            }
            else if (pair.Key.StartsWith(ServerFieldPrefix, StringComparison.Ordinal) && pair.Key.Length > ServerFieldPrefix.Length)
            {
                string serverId = ReadServerPause(pair.Key, pair.Value, out string? serverReason);
                pausedServers[serverId] = serverReason;
            }
        }

        bool active = hash.TryGetValue("Drain", out string? drain) && drain == "1";
        if (!active && pausedQueues.Count == 0 && pausedServers.Count == 0)
        {
            return DrainModeStatus.Inactive;
        }

        DateTime? since = active && hash.TryGetValue("DrainSince", out string? sinceValue) ? JobHelper.DeserializeNullableDateTime(sinceValue) : null;
        string? reason = active && hash.TryGetValue("DrainReason", out string? reasonValue) && reasonValue.Length > 0 ? reasonValue : null;

        return new DrainModeStatus(active, since, reason, pausedQueues, pausedServers);
    }

    /// <summary>
    /// Makes one server stop taking jobs: its workers fetch nothing while it is paused, so the jobs stay in their
    /// queues for the servers that may work, and its running jobs finish. The server notices within the time the
    /// answer is cached (<see cref="CacheDuration"/>), and a resume within a few seconds more.
    /// </summary>
    /// <remarks>
    /// A worker that was waiting for a job when the server was paused takes one more: that job isn't run but goes
    /// to the Scheduled state for <see cref="HoldInterval"/> and is enqueued again after that, for any server. The
    /// time it was held doesn't count as lateness (<see cref="Server.DrainModeFilter.ServerHoldParameter"/>).
    /// </remarks>
    /// <param name="connection">An open storage connection.</param>
    /// <param name="serverId">
    /// The full server ID, as shown on the dashboard's Servers page and in <c>IMonitoringApi.Servers()</c>:
    /// <c>{name}:{process id}:{guid}</c>, for example <c>web-1:4242:0f8fad5b-d9cb-469f-a165-70867728950e</c>.
    /// The server name alone doesn't match. The ID is new every time the server starts, so the pause doesn't
    /// survive a restart. A pause left for a server that is gone is cleared with
    /// <see cref="ResumeServer(IStorageConnection, string)"/> and the old ID, or with <see cref="ClearStaleServerPauses"/>.
    /// </param>
    /// <param name="reason">An optional reason, shown on the dashboard and in the held jobs' history.</param>
    public static void PauseServer([NotNull] IStorageConnection connection, [NotNull] string serverId, [CanBeNull] string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("A server id is required.", nameof(serverId));
        }

        string id = serverId.Trim();
        string field = GetServerField(id);

        // A hashed field doesn't hold the ID, so the value starts with it
        string value = IsHashedServerField(field) ? id + HashedServerIdSeparator + reason : ToStoredReason(reason);

        Write(connection, [new KeyValuePair<string, string>(field, value)]);
    }

    /// <summary>
    /// Lets a paused server work again. Also clears the pause left for a server that has stopped: pass the old,
    /// full server ID. The pause's field is removed from the hash.
    /// </summary>
    public static void ResumeServer([NotNull] IStorageConnection connection, [NotNull] string serverId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("A server id is required.", nameof(serverId));
        }

        string field = GetServerField(serverId.Trim());
        RemoveFields(connection, hash => FieldsNamed(hash, field));
    }

    /// <summary>
    /// Clears the pause of every server that is no longer running, that is, no longer listed by the storage's
    /// <see cref="IMonitoringApi.Servers"/> (a server that stopped, or one the server watchdog removed after it
    /// died). Server IDs are new every time a server starts, so such a pause never applies again; it only keeps
    /// the dashboard banner and the health check reporting a pause. Pauses of running servers are kept.
    /// </summary>
    /// <param name="storage">The storage whose pauses are checked.</param>
    /// <returns>The IDs of the servers whose pause was cleared, empty when there was none.</returns>
    public static IReadOnlyList<string> ClearStaleServerPauses([NotNull] JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        using IStorageConnection connection = storage.GetConnection();
        List<string> stale = [];

        // The pauses are read before the servers, and under the lock: a server paused meanwhile waits for the
        // rewrite, and is never cleared by mistake
        RemoveFields(connection, hash =>
        {
            List<(string Field, string ServerId)> pauses = [.. ServerPauses(hash)];
            if (pauses.Count == 0)
            {
                return [];
            }

            HashSet<string> runningServers = new(StringComparer.OrdinalIgnoreCase);
            foreach (ServerDto server in storage.GetMonitoringApi().Servers())
            {
                runningServers.Add(server.Name);
            }

            HashSet<string> staleIds = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> fields = new(StringComparer.Ordinal);
            foreach ((string field, string serverId) in pauses)
            {
                if (runningServers.Contains(serverId))
                {
                    continue;
                }

                fields.Add(field);
                if (staleIds.Add(serverId))
                {
                    stale.Add(serverId);
                }
            }

            return fields;
        });

        return stale;
    }

    /// <summary>
    /// Gets the current status, using a process-wide cache shared by every caller that doesn't name the storage.
    /// With more than one storage in a process, use <see cref="GetStatus(JobStorage, IStorageConnection)"/>,
    /// which keeps one cache per storage.
    /// </summary>
    public static DrainModeStatus GetStatus([NotNull] IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return GetStatus(SharedCache, connection);
    }

    /// <summary>
    /// Gets the current status of <paramref name="storage"/>, using the cache this process keeps for that storage
    /// (see <see cref="CacheDuration"/>).
    /// </summary>
    /// <param name="storage">The storage the switch is read from; the cache is kept per storage.</param>
    /// <param name="connection">An open connection to <paramref name="storage"/>, used when the cache is out of date.</param>
    public static DrainModeStatus GetStatus([NotNull] JobStorage storage, [NotNull] IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(connection);

        return GetStatus(StorageCaches.GetValue(storage, static _ => new CacheSlot()), connection);
    }

    /// <summary>
    /// The status this process has cached for <paramref name="storage"/>, or <c>null</c> when it has none that is
    /// still good: the storage isn't asked. For a check made before every fetch, which opens a connection only
    /// when the answer must be read again.
    /// </summary>
    [CanBeNull]
    internal static DrainModeStatus? GetCachedStatus([NotNull] JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        CacheEntry? cached = Volatile.Read(ref StorageCaches.GetValue(storage, static _ => new CacheSlot()).Entry);
        return cached != null && IsGood(cached, Interlocked.Read(ref _cacheGeneration)) ? cached.Status : null;
    }

    private static bool IsGood(CacheEntry cached, long generation)
    {
        return cached.Generation == generation && CacheDuration > TimeSpan.Zero &&
               Stopwatch.GetTimestamp() - cached.Timestamp < CacheDuration.TotalSeconds * Stopwatch.Frequency;
    }

    private static DrainModeStatus GetStatus(CacheSlot slot, IStorageConnection connection)
    {
        long generation = Interlocked.Read(ref _cacheGeneration);
        CacheEntry? cached = Volatile.Read(ref slot.Entry);

        if (cached != null && IsGood(cached, generation))
        {
            return cached.Status;
        }

        DrainModeStatus status = Read(connection);

        // Tagged with the generation seen before the read, so a change made meanwhile isn't hidden
        Volatile.Write(ref slot.Entry, new CacheEntry(status, Stopwatch.GetTimestamp(), generation));
        return status;
    }

    /// <summary>Holds the jobs of one queue; the other queues keep flowing. Running jobs finish.</summary>
    public static void PauseQueue([NotNull] IStorageConnection connection, [NotNull] string queue, [CanBeNull] string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(queue))
        {
            throw new ArgumentException("A queue name is required.", nameof(queue));
        }

        Write(connection, [new KeyValuePair<string, string>(QueueFieldPrefix + queue.Trim().ToLowerInvariant(), ToStoredReason(reason))]);
    }

    /// <summary>Lets the jobs of a paused queue go again. Held jobs run as soon as their hold interval ends.</summary>
    public static void ResumeQueue([NotNull] IStorageConnection connection, [NotNull] string queue)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(queue))
        {
            throw new ArgumentException("A queue name is required.", nameof(queue));
        }

        string field = QueueFieldPrefix + queue.Trim().ToLowerInvariant();
        RemoveFields(connection, hash => FieldsNamed(hash, field));
    }

    /// <summary>
    /// Tells whether the switch is on, using the process-wide cache shared by every caller that doesn't name the
    /// storage. With more than one storage in a process, use <see cref="IsActive(JobStorage, IStorageConnection)"/>.
    /// </summary>
    public static bool IsActive([NotNull] IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return GetStatus(connection).Active;
    }

    /// <summary>Tells whether the switch of <paramref name="storage"/> is on, using the cache kept for that storage.</summary>
    public static bool IsActive([NotNull] JobStorage storage, [NotNull] IStorageConnection connection)
    {
        return GetStatus(storage, connection).Active;
    }

    /// <summary>Turns the switch on. Running jobs finish; new ones are held.</summary>
    public static void Enable([NotNull] IStorageConnection connection, [CanBeNull] string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Write(connection,
        [
            new KeyValuePair<string, string>("Drain", "1"),
            new KeyValuePair<string, string>("DrainSince", JobHelper.SerializeDateTime(Clock())),
            new KeyValuePair<string, string>("DrainReason", reason ?? string.Empty),
        ]);
    }

    /// <summary>Turns the switch off. Held jobs run as soon as their hold interval ends.</summary>
    public static void Disable([NotNull] IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Write(connection,
        [
            new KeyValuePair<string, string>("Drain", "0"),
            new KeyValuePair<string, string>("DrainReason", string.Empty),
        ]);
    }

    /// <summary>Forgets the cached answers of every storage, so the next check reads the storage.</summary>
    public static void InvalidateCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
    }

    /// <summary>
    /// The hash field of a server's pause: <c>Server:{id}</c>, or, when that is longer than a storage keeps,
    /// <c>Server:#</c> followed by the SHA-256 of the lowercased ID in hex.
    /// </summary>
    internal static string GetServerField([NotNull] string serverId)
    {
        string field = ServerFieldPrefix + serverId;
        if (field.Length <= MaxFieldLength)
        {
            return field;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(serverId.ToLowerInvariant());
        byte[] hash = SHA256.HashData(bytes);

        const string digits = "0123456789abcdef";
        StringBuilder builder = new(HashedServerFieldLength);
        builder.Append(ServerFieldPrefix).Append(HashedServerMarker);

        foreach (byte value in hash)
        {
            builder.Append(digits[value >> 4]).Append(digits[value & 0xF]);
        }

        return builder.ToString();
    }

    private static void Write(IStorageConnection connection, KeyValuePair<string, string>[] fields)
    {
        using (connection.AcquireDistributedLock(LockResource, LockTimeout))
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.SetRangeInHash(HashKey, fields);
            transaction.Commit();
        }

        InvalidateCache();
    }

    // Writes the hash again without the fields chosen from it, and without the resumed markers of earlier builds:
    // a storage can't remove one field of a hash. The hash is read, and written, under the lock every write takes,
    // so nothing written meanwhile is lost; one transaction removes the hash and writes it again, so a reader sees
    // it before or after, never in between
    private static void RemoveFields(IStorageConnection connection, Func<Dictionary<string, string>, ICollection<string>> choose)
    {
        using (connection.AcquireDistributedLock(LockResource, LockTimeout))
        {
            Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(HashKey);
            if (hash == null || hash.Count == 0)
            {
                return;
            }

            ICollection<string> removed = choose(hash);
            List<KeyValuePair<string, string>> kept = [.. hash.Where(pair => !removed.Contains(pair.Key) && !IsResumed(pair.Value))];
            if (kept.Count == hash.Count)
            {
                return;
            }

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.RemoveHash(HashKey);
            if (kept.Count > 0)
            {
                transaction.SetRangeInHash(HashKey, kept);
            }

            transaction.Commit();
        }

        InvalidateCache();
    }

    // The fields of the hash with that name, whatever the case: a server's ID may be given in another case
    private static HashSet<string> FieldsNamed(Dictionary<string, string> hash, string field)
    {
        return [.. hash.Keys.Where(key => string.Equals(key, field, StringComparison.OrdinalIgnoreCase))];
    }

    // The fields of the servers' pauses, with the server each one pauses
    private static IEnumerable<(string Field, string ServerId)> ServerPauses(Dictionary<string, string> hash)
    {
        foreach (KeyValuePair<string, string> pair in hash)
        {
            if (!IsResumed(pair.Value) && pair.Key.StartsWith(ServerFieldPrefix, StringComparison.Ordinal) && pair.Key.Length > ServerFieldPrefix.Length)
            {
                yield return (pair.Key, ReadServerPause(pair.Key, pair.Value, out _));
            }
        }
    }

    private static bool IsResumed(string? value)
    {
        return value is ResumedMarker or LegacyResumedMarker;
    }

    private static string ToStoredReason(string? reason)
    {
        return reason == ResumedMarker ? reason + " " : reason ?? string.Empty;
    }

    private static bool IsHashedServerField(string field)
    {
        return field.Length == HashedServerFieldLength && field[ServerFieldPrefix.Length] == HashedServerMarker;
    }

    private static string ReadServerPause(string field, string? value, out string? reason)
    {
        if (IsHashedServerField(field) && value != null)
        {
            // A long ID is kept at the start of the value, before the reason
            int separator = value.IndexOf(HashedServerIdSeparator);
            if (separator > 0)
            {
                reason = separator + 1 < value.Length ? value[(separator + 1)..] : null;
                return value[..separator];
            }
        }

        reason = string.IsNullOrEmpty(value) ? null : value;
        return field[ServerFieldPrefix.Length..];
    }

    private sealed class CacheSlot
    {
        public CacheEntry? Entry;
    }

    private sealed record CacheEntry(DrainModeStatus Status, long Timestamp, long Generation);
}
