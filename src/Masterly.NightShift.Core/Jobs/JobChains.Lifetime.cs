using System.Collections.Concurrent;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    // How many chains' expiry times a process remembers at most; past it, they are all forgotten and read again
    private const int MaxKnownExpiries = 10_000;

    // The mark an earlier build put on the steps of the chains it recorded (ChainRecordedParameter)
    private const string EarlierBuildMark = "true";

    // The chains of steps marked by an earlier build that this process made sure have a place among the chains to
    // look at; past the limit, they are all forgotten and looked up again
    private static readonly ConcurrentDictionary<string, byte> SeenFromEarlierBuild = new(StringComparer.Ordinal);

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
    /// How long before the record of a chain expires a server looks at the chain, to keep the record when a step
    /// is left to run or failed for good: a quarter of <see cref="JobChain.RecordRetention"/>, so servers that are
    /// all stopped for less than that lose no record.
    /// </summary>
    internal static TimeSpan KeepAhead() => JobChain.RecordRetention / 4;

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
    /// to run. It never shortens the record's life, and does nothing on a storage that doesn't expire hashes, nor
    /// for a chain that isn't recorded: one whose record has no time left to live, unless the caller has just read
    /// the record (<paramref name="recorded"/>), which a storage may still hold a moment after it expired.
    /// Bookkeeping: a storage error is logged, never thrown.
    /// </summary>
    /// <returns>When the record expires now, or <c>null</c> when that isn't known: it doesn't expire, isn't recorded, or the storage failed.</returns>
    internal static DateTime? ExtendRecord(JobStorage storage, IStorageConnection connection, string chainId, DateTime latestStart, bool recorded = false)
    {
        return ExtendRecordCore(storage, connection, chainId, latestStart, recorded, out _);
    }

    /// <summary>
    /// The same; <paramref name="lapsed"/> says whether the record had no time left to live before: a server may
    /// have taken its chain out of the list of chains.
    /// </summary>
    private static DateTime? ExtendRecordCore(JobStorage storage, IStorageConnection connection, string chainId, DateTime latestStart, bool recorded, out bool lapsed)
    {
        lapsed = false;

        try
        {
            if (connection is not JobStorageConnection storageConnection || !storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                return null;
            }

            string key = GetKey(chainId);
            DateTime now = JobClock.UtcNow;
            DateTime desired = RecordExpiry(now, latestStart);

            // Negative for a chain that isn't recorded (any more): nothing to extend, and nothing to remember,
            // because its record may still be written
            TimeSpan timeToLive = storageConnection.GetHashTtl(key);
            lapsed = timeToLive <= TimeSpan.Zero;

            if (timeToLive < TimeSpan.Zero && !recorded)
            {
                return null;
            }

            DateTime current = now + timeToLive;
            if (desired - current <= RecordRefreshSlack)
            {
                RememberExpiry(chainId, current);
                return current;
            }

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            if (transaction is not JobStorageTransaction storageTransaction)
            {
                return current;
            }

            storageTransaction.ExpireHash(key, desired - now);
            transaction.Commit();

            RememberExpiry(chainId, desired);
            return desired;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"The record of chain '{chainId}' couldn't be kept longer; it is tried again at the next state change of one of its steps, or when a server looks at the chain.", ex);
            return null;
        }
    }

    /// <summary>
    /// Whether a state change takes a job out of a state it had finished in: Failed, Succeeded or Deleted. Somebody
    /// requeued or deleted it, or a retry of its chain holds it. Few state changes are of that kind.
    /// </summary>
    internal static bool LeavesFinishedState(string? oldStateName, IState newState)
    {
        return oldStateName != null && IsSettledState(oldStateName) && !oldStateName.Equals(newState.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Has a server look soon at the chain of a step that left the state it had finished in, once that is committed:
    /// the chain moves on, and its next look may be far off (a chain that only waited behind a failure, or one left
    /// to expire). <paramref name="parameters"/> are the job's parameters when the storage loaded them with the
    /// job; without them, the chain is read after the commit, and its record kept longer then, which the caller
    /// does only with the parameters at hand. Nothing is done for a job outside a chain.
    /// </summary>
    internal static void LookSoonAfterCommit(ApplyStateContext context, IReadOnlyDictionary<string, string>? parameters)
    {
        string? serialized = null;
        if (context.AfterCommit == null || (parameters != null && !parameters.TryGetValue(ChainIdParameter, out serialized)))
        {
            return;
        }

        JobStorage storage = context.Storage;
        IStorageConnection connection = context.Connection;
        string jobId = context.BackgroundJob.Id;

        // After the commit, and in a write of its own: the set is shared by every chain, and a state change
        // mustn't wait for it, nor fail with it
        context.RunAfterCommit(() => LookSoon(storage, connection, jobId, serialized, keepRecord: parameters == null));
    }

    /// <summary>
    /// Asks for a look at the chain of a job by a server a few minutes from now (<see cref="DecisionWindow"/>, the
    /// time after which a waiting step that wasn't started counts as never told). In an entry of its own
    /// (<see cref="SoonSetKey"/>), not at the chain's place among the chains to look at: a look in progress writes
    /// that place from what it read before this state change, and would put the chain weeks off again. Best
    /// effort: without it the chain is looked at when it was to be, later.
    /// </summary>
    private static void LookSoon(JobStorage storage, IStorageConnection connection, string jobId, string? serializedChainId, bool keepRecord)
    {
        try
        {
            serializedChainId ??= connection.GetJobParameter(jobId, ChainIdParameter);
            if (string.IsNullOrEmpty(serializedChainId))
            {
                return;
            }

            string? chainId = SerializationHelper.Deserialize<string>(serializedChainId, SerializationOption.User);
            if (string.IsNullOrWhiteSpace(chainId))
            {
                return;
            }

            // The look moves the chain to where it belongs then, also one that was left to expire
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.AddToSet(SoonSetKey, SoonEntry(chainId), JobHelper.ToTimestamp(JobClock.UtcNow + DecisionWindow));
            transaction.Commit();

            if (keepRecord)
            {
                ExtendRecord(storage, connection, chainId, JobClock.UtcNow);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The chain of background job '{jobId}' couldn't be given an early look after the job left the state it had finished in; a server looks at it when it was to.", ex);
        }
    }

    /// <summary>
    /// For a step of a chain that a process of an earlier build recorded (its mark says <c>true</c>): once the state
    /// change is committed, makes sure the chain has a place among the chains to look at, which that build didn't
    /// give it. Such a process may still run beside this build, after the list of chains was gone through. Once a
    /// chain per process; nothing for a step this build recorded, which costs a dictionary lookup.
    /// </summary>
    internal static void PlaceChainOfEarlierBuildAfterCommit(ApplyStateContext context, IReadOnlyDictionary<string, string> parameters, string serializedChainId)
    {
        if (!parameters.TryGetValue(ChainRecordedParameter, out string? mark) ||
            !string.Equals(mark, EarlierBuildMark, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        JobStorage storage = context.Storage;
        IStorageConnection connection = context.Connection;
        context.RunAfterCommit(() => PlaceChainOfEarlierBuild(storage, connection, serializedChainId));
    }

    /// <summary>
    /// Asks for a look at a chain an earlier build recorded, which gives it its place, unless it has one; and has
    /// the list of chains gone through again when it had none. Bookkeeping: never throws.
    /// </summary>
    private static void PlaceChainOfEarlierBuild(JobStorage storage, IStorageConnection connection, string serializedChainId)
    {
        string? chainId = null;

        try
        {
            chainId = SerializationHelper.Deserialize<string>(serializedChainId, SerializationOption.User);
            if (string.IsNullOrWhiteSpace(chainId))
            {
                return;
            }

            if (SeenFromEarlierBuild.Count >= MaxKnownExpiries)
            {
                SeenFromEarlierBuild.Clear();
            }

            if (!SeenFromEarlierBuild.TryAdd(chainId, 0))
            {
                return;
            }

            // Where the storage can't tell cheaply, the look is asked for anyway: it costs one look
            bool canTell = connection is JobStorageConnection && storage.HasFeature(JobStorageFeatures.Connection.GetSetContains);
            if (canTell && HasPlace((JobStorageConnection)connection, chainId))
            {
                return;
            }

            using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
            {
                transaction.AddToSet(SoonSetKey, SoonEntry(chainId), JobHelper.ToTimestamp(JobClock.UtcNow));
                transaction.Commit();
            }

            // Recorded after the list was gone through, maybe with others that no server of this build touched
            if (canTell)
            {
                GoThroughTheListAgain(storage, connection);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            if (chainId != null)
            {
                SeenFromEarlierBuild.TryRemove(chainId, out _);
            }

            Logger.DebugException($"Chain '{chainId}', recorded by an earlier build, couldn't be given a place among the chains to look at; the next state change of one of its steps tries again.", ex);
        }
    }

    private static void RememberExpiry(string chainId, DateTime expireAt)
    {
        if (KnownExpiries.Count >= MaxKnownExpiries)
        {
            KnownExpiries.Clear();
        }

        KnownExpiries[chainId] = expireAt;
    }

    internal static void ForgetExpiry(string chainId) => KnownExpiries.TryRemove(chainId, out _);
}
