using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Server;

/// <summary>
/// The workers of one server that run a job, counted in the server's process: a worker counts from the moment it
/// moves a job to Processing until it has finished that job's state change. One counter serves every worker pool
/// of the server (the normal pool, the long-running pool, gated ones, a worker added as a process), through the
/// server's <see cref="BackgroundServerContext"/>; a server that starts again with a new id gets a new counter.
/// </summary>
internal sealed class BusyWorkerCounter
{
    private int _count;

    /// <summary>The workers that run a job now.</summary>
    public int Current => Math.Max(0, Volatile.Read(ref _count));

    /// <summary>A worker moved a job to Processing.</summary>
    public void Enter()
    {
        Interlocked.Increment(ref _count);
    }

    /// <summary>A worker finished the state change that ended its job, or gave the job up.</summary>
    public void Leave()
    {
        Interlocked.Decrement(ref _count);
    }
}

/// <summary>
/// What each server reports of its busy workers, for the dashboard's Servers page: the hash <c>servers:busy</c>,
/// one field per server (its id, shortened past <see cref="MaxFieldLength"/>), written by the server alone after
/// each successful heartbeat and never inside a state transaction, so no two servers write the same row. The
/// value is <c>{busy};{taken at, Unix milliseconds};{heartbeat interval, milliseconds}</c>, the time by the
/// storage's clock where it has one (<see cref="StorageTime"/>), as the heartbeat. A storage can't remove one field
/// of a hash, so a field is removed by writing the hash again without it, under <see cref="LockResource"/>: when
/// its server stops, and by every server's watchdog for the servers whose report is older than the server timeout.
/// </summary>
internal static class ServerBusyWorkers
{
    /// <summary>The hash of the reports.</summary>
    public const string HashKey = "servers:busy";

    /// <summary>The longest hash field SQL Server stores.</summary>
    public const int MaxFieldLength = 100;

    /// <summary>
    /// How many of its heartbeat intervals a report may be away from its server's last heartbeat and still count:
    /// a server that stopped writing it (an earlier build) never shows a frozen count.
    /// </summary>
    public const int FreshIntervals = 2;

    private const string LockResource = "lock:servers:busy";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private const char Separator = ';';

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(ServerBusyWorkers));

    /// <summary>The field of a server in <see cref="HashKey"/>.</summary>
    public static string GetField([NotNull] string serverId)
    {
        return ConcurrencyControl.ShortenResource(serverId, MaxFieldLength);
    }

    /// <summary>The value of a report, see <see cref="ServerBusyWorkers"/>.</summary>
    public static string FormatValue(int busy, DateTime takenAt, TimeSpan heartbeatInterval)
    {
        return string.Join(
            Separator,
            busy.ToString(CultureInfo.InvariantCulture),
            JobHelper.ToMillisecondTimestamp(takenAt).ToString(CultureInfo.InvariantCulture),
            ((long)heartbeatInterval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Reads a report's value; <c>false</c> for one that isn't a report.</summary>
    public static bool TryParse([CanBeNull] string? value, out int busy, out DateTime takenAt, out TimeSpan heartbeatInterval)
    {
        busy = 0;
        takenAt = default;
        heartbeatInterval = default;

        string[] parts = value?.Split(Separator) ?? [];
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out busy) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long taken) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long interval))
        {
            busy = 0;
            return false;
        }

        takenAt = JobHelper.FromMillisecondTimestamp(taken);
        heartbeatInterval = TimeSpan.FromMilliseconds(interval);
        return true;
    }

    /// <summary>
    /// Whether a report was taken close enough to its server's last heartbeat to count: within
    /// <see cref="FreshIntervals"/> heartbeat intervals of it (at least a second).
    /// </summary>
    public static bool IsFresh(DateTime takenAt, TimeSpan heartbeatInterval, DateTime? heartbeat)
    {
        if (heartbeat == null)
        {
            return false;
        }

        TimeSpan allowed = TimeSpan.FromTicks(Math.Max(heartbeatInterval.Ticks * FreshIntervals, TimeSpan.TicksPerSecond));
        return (heartbeat.Value - takenAt).Duration() <= allowed;
    }

    /// <summary>Writes the server's report into its own field. Throws what the storage throws.</summary>
    public static void Write([NotNull] IStorageConnection connection, [NotNull] string serverId, string value)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(serverId);

        connection.SetRangeInHash(HashKey, [new KeyValuePair<string, string>(GetField(serverId), value)]);
    }

    /// <summary>Removes the field of a server that stops. Best effort: a field left behind is pruned later.</summary>
    public static void RemoveServer([NotNull] IStorageConnection connection, [NotNull] string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);

        string field = GetField(serverId);
        RemoveFields(connection, hash => hash.ContainsKey(field) ? [field] : []);
    }

    /// <summary>
    /// Removes the reports taken more than <paramref name="serverTimeout"/> before <paramref name="now"/>: their
    /// servers timed out, or were removed. Best effort, like <see cref="RemoveServer"/>.
    /// </summary>
    public static void RemoveStale([NotNull] IStorageConnection connection, DateTime now, TimeSpan serverTimeout)
    {
        RemoveFields(connection, hash =>
        [
            .. hash
                .Where(pair => !TryParse(pair.Value, out _, out DateTime takenAt, out _) || takenAt < now - serverTimeout)
                .Select(static pair => pair.Key),
        ]);
    }

    // Writes the hash again without the chosen fields, in one transaction, so a reader sees it before or after. A
    // server that writes its own field meanwhile may see it go back to its previous value, or go, until its next
    // heartbeat: the page then counts as it does without reports. The lock keeps two rewrites apart.
    private static void RemoveFields(IStorageConnection connection, Func<Dictionary<string, string>, List<string>> choose)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(HashKey);
        if (hash == null || choose(hash).Count == 0)
        {
            return;
        }

        try
        {
            using (connection.AcquireDistributedLock(LockResource, LockTimeout))
            {
                hash = connection.GetAllEntriesFromHash(HashKey);
                if (hash == null)
                {
                    return;
                }

                HashSet<string> removed = [.. choose(hash)];
                if (removed.Count == 0)
                {
                    return;
                }

                List<KeyValuePair<string, string>> kept = [.. hash.Where(pair => !removed.Contains(pair.Key))];
                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                transaction.RemoveHash(HashKey);
                if (kept.Count > 0)
                {
                    transaction.SetRangeInHash(HashKey, kept);
                }

                transaction.Commit();
            }
        }
        catch (DistributedLockTimeoutException ex)
        {
            Logger.DebugException($"The busy worker reports were not pruned: another server is writing '{HashKey}' again.", ex);
        }
    }
}
