using System.Collections.Concurrent;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    // How many chains' expiry times a process remembers at most; past it, they are all forgotten and read again
    private const int MaxKnownExpiries = 10_000;

    // When each chain's record expires, as far as this process knows: what it wrote or read last. It spares the
    // storage a read at each state change of a step, and is never ahead of the storage by more than nothing: a
    // record only ever gets a later expiry.
    private static readonly ConcurrentDictionary<string, DateTime> KnownExpiries = new(StringComparer.Ordinal);

    /// <summary>
    /// How much later than its current expiry a record must be due to expire before it is written again: a process
    /// extends the record of a running chain about once in this time, not at each state change. So a record is
    /// kept for <see cref="JobChain.RecordRetention"/> after the last state change of a step, less this at most.
    /// Replaceable for tests.
    /// </summary>
    internal static TimeSpan RecordRefreshSlack { get; set; } = TimeSpan.FromHours(1);

    /// <summary>When a record expires that is written or extended at <paramref name="now"/> for a step that starts at <paramref name="latestStart"/>.</summary>
    internal static DateTime RecordExpiry(DateTime now, DateTime latestStart)
    {
        return (latestStart > now ? latestStart : now) + JobChain.RecordRetention;
    }

    /// <summary>
    /// Whether the record of a chain may need a later expiry for a step that starts at
    /// <paramref name="latestStart"/>: not when this process knows it expires late enough already. No storage call.
    /// </summary>
    internal static bool MayNeedExtending(string chainId, DateTime latestStart)
    {
        DateTime desired = RecordExpiry(JobClock.UtcNow, latestStart);
        return !KnownExpiries.TryGetValue(chainId, out DateTime known) || desired - known > RecordRefreshSlack;
    }

    /// <summary>
    /// Keeps the record of a chain until <see cref="JobChain.RecordRetention"/> after <paramref name="latestStart"/>
    /// (a step's state change, or the time it is scheduled for), so the record lives as long as the chain has steps
    /// to run. It never shortens the record's life, and does nothing for a chain that isn't recorded or on a storage
    /// that doesn't expire hashes. Bookkeeping: a storage error is logged, never thrown.
    /// </summary>
    internal static void ExtendRecord(JobStorage storage, IStorageConnection connection, string chainId, DateTime latestStart)
    {
        try
        {
            if (connection is not JobStorageConnection storageConnection || !storage.HasFeature(JobStorageFeatures.ExtendedApi)) return;

            string key = GetKey(chainId);
            DateTime now = JobClock.UtcNow;
            DateTime desired = RecordExpiry(now, latestStart);

            // Negative for a chain that isn't recorded (any more): nothing to extend, and nothing to remember,
            // because its record may still be written
            TimeSpan timeToLive = storageConnection.GetHashTtl(key);
            if (timeToLive < TimeSpan.Zero) return;

            DateTime current = now + timeToLive;
            if (desired - current <= RecordRefreshSlack)
            {
                RememberExpiry(chainId, current);
                return;
            }

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            if (transaction is not JobStorageTransaction storageTransaction) return;

            storageTransaction.ExpireHash(key, desired - now);
            transaction.Commit();

            RememberExpiry(chainId, desired);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"The record of chain '{chainId}' couldn't be kept longer; the next state change of one of its steps tries again.", ex);
        }
    }

    private static void RememberExpiry(string chainId, DateTime expireAt)
    {
        if (KnownExpiries.Count >= MaxKnownExpiries) KnownExpiries.Clear();
        KnownExpiries[chainId] = expireAt;
    }

    internal static void ForgetExpiry(string chainId) => KnownExpiries.TryRemove(chainId, out _);
}
