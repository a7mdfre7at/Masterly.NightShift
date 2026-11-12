using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    /// <summary>The set that lists the chains a retry or a replacement left jobs to delete in, scored by the time of the last attempt.</summary>
    internal const string CleanupSetKey = "chains:cleanup";

    /// <summary>The lock one server holds while it maintains the chains.</summary>
    internal const string MaintenanceLockResource = "lock:chains:maintenance";

    /// <summary>How many listed chains one maintenance run looks at, at most.</summary>
    internal const int MaxListedPerRun = 1000;

    /// <summary>How many chains with jobs left to delete one maintenance run finishes, at most.</summary>
    internal const int MaxCleanupsPerRun = 50;

    private const int ListPageSize = 100;

    /// <summary>
    /// One maintenance run, by one server at a time: removes from the list of chains the ones whose record expired,
    /// and finishes what interrupted retries and replacements left to do. <paramref name="listPosition"/> is the
    /// score in the list where the run before stopped, and receives where this one stops (0 to start over).
    /// </summary>
    /// <returns>False when another server is doing it, or the storage has neither expiring hashes nor ranges of sets.</returns>
    internal static bool Maintain(JobStorage storage, IBackgroundJobClient client, ref double listPosition)
    {
        if (!storage.HasFeature(JobStorageFeatures.ExtendedApi)) return false;

        using IStorageConnection connection = storage.GetConnection();
        if (connection is not JobStorageConnection storageConnection) return false;

        IDisposable maintenanceLock;
        try
        {
            maintenanceLock = connection.AcquireDistributedLock(MaintenanceLockResource, TimeSpan.Zero);
        }
        catch (DistributedLockTimeoutException)
        {
            return false;
        }

        using (maintenanceLock)
        {
            int unlisted = 0;
            try
            {
                (listPosition, unlisted) = PruneList(storage, storageConnection, listPosition);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The interrupted changes are still finished; the list is walked again from the same place
                Logger.WarnException("The chains whose record expired couldn't be removed from the list of chains; the next maintenance run tries again.", ex);
            }

            int finished = FinishCleanups(storage, client, storageConnection);

            if (unlisted > 0 || finished > 0)
            {
                Logger.Info($"Chain maintenance: {unlisted} chains whose record expired left the list, {finished} interrupted changes of chains were finished.");
            }
        }

        return true;
    }

    /// <summary>
    /// Removes from the list the chains whose record expired, walking the list by creation time from
    /// <paramref name="from"/>: a chain recorded longer than <see cref="JobChain.RecordRetention"/> (scheduled far
    /// ahead, retried, still running) is stepped over, so it never keeps the ones after it listed. Only chains
    /// created longer ago than the retention are read: a younger chain is listed for as long as it is recorded, and
    /// the steps of one whose record was lost early still run because it is listed. The walk stops after
    /// <see cref="MaxListedPerRun"/> chains, and goes on from there next time.
    /// </summary>
    private static (double From, int Unlisted) PruneList(JobStorage storage, JobStorageConnection connection, double from)
    {
        double oldest = JobHelper.ToTimestamp(JobClock.UtcNow - JobChain.RecordRetention);
        int pageSize = storage.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest) ? ListPageSize : 1;
        int examined = 0;
        int unlisted = 0;

        while (examined < MaxListedPerRun)
        {
            if (from > oldest) return (0, unlisted);

            List<string> page = pageSize > 1
                ? connection.GetFirstByLowestScoreFromSet(SetKey, from, oldest, pageSize)
                : connection.GetFirstByLowestScoreFromSet(SetKey, from, oldest) is { } single ? [single] : [];

            List<string> expired = [];
            double last = from;

            foreach (string chainId in page)
            {
                examined++;

                string key = KeyPrefix + chainId;
                string? createdAt = connection.GetValueFromHash(key, CreatedAtField);

                if (createdAt == null && connection.GetHashCount(key) == 0)
                {
                    expired.Add(chainId);
                }
                else if (createdAt != null && JobHelper.DeserializeNullableDateTime(createdAt) is { } created)
                {
                    // The list is scored by creation time: the walk goes on from the last chain it keeps
                    last = Math.Max(last, JobHelper.ToTimestamp(created));
                }
            }

            if (expired.Count > 0)
            {
                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                foreach (string chainId in expired)
                {
                    transaction.RemoveFromSet(SetKey, chainId);
                }

                transaction.Commit();
                unlisted += expired.Count;
            }

            // Every chain old enough was looked at: the next run starts over
            if (page.Count < pageSize) return (0, unlisted);

            // The chains kept at the score the next page starts from come back with it. When a page brought
            // nothing else, they fill it: the walk steps to the next second (scores are whole seconds).
            from = expired.Count == 0 && last <= from ? from + 1 : last;
        }

        return (from, unlisted);
    }

    /// <summary>
    /// Finishes what retries and replacements left to do in the chains listed for it, the ones tried longest ago
    /// first. A chain that is changing right now is left for the next run, and one that fails doesn't stop the others.
    /// </summary>
    private static int FinishCleanups(JobStorage storage, IBackgroundJobClient client, JobStorageConnection connection)
    {
        int finished = 0;

        foreach (string chainId in connection.GetRangeFromSet(CleanupSetKey, 0, MaxCleanupsPerRun - 1))
        {
            try
            {
                using (connection.AcquireDistributedLock(GetLockResource(chainId), TimeSpan.Zero))
                {
                    // Only the field: the chain's definition, with the types of its jobs, isn't loaded
                    ChainCleanup? cleanup = ChainCleanup.Parse(connection.GetValueFromHash(KeyPrefix + chainId, CleanupField));

                    if (cleanup == null)
                    {
                        // Finished meanwhile, or the chain was deleted or its record expired
                        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                        transaction.RemoveFromSet(CleanupSetKey, chainId);
                        transaction.Commit();
                    }
                    else if (CompleteCleanup(storage, client, connection, chainId, cleanup, heldJustNow: false, out _) == null)
                    {
                        finished++;
                    }
                }
            }
            catch (DistributedLockTimeoutException)
            {
                // The chain is being retried, replaced or deleted, which finishes it too
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"What a change of chain '{chainId}' left to do couldn't be finished; the next maintenance run tries again.", ex);
            }
        }

        return finished;
    }

    /// <summary>
    /// Does what a recorded change of a chain left to do and records the outcome: the chain leaves the list of
    /// chains to clean up when nothing is left, and moves to its end otherwise. Never throws: the change is recorded
    /// already, so a storage error here must not fail it. The jobs left can't run meanwhile, because the record
    /// doesn't list them.
    /// </summary>
    /// <returns>What is still left to do, <c>null</c> when nothing is.</returns>
    private static ChainCleanup? CompleteCleanup(
        JobStorage storage,
        IBackgroundJobClient client,
        IStorageConnection connection,
        string chainId,
        ChainCleanup cleanup,
        bool heldJustNow,
        out int deleted)
    {
        deleted = 0;
        ChainCleanup? remaining = cleanup;

        try
        {
            remaining = RunCleanup(client, connection, chainId, cleanup, heldJustNow, out deleted);

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();

            // The field exists since the change was recorded, so it keeps the expiry of the record
            transaction.SetRangeInHash(GetKey(chainId), new Dictionary<string, string> { [CleanupField] = ChainCleanup.Serialize(remaining) });

            if (remaining == null)
            {
                transaction.RemoveFromSet(CleanupSetKey, chainId);
            }
            else
            {
                transaction.AddToSet(CleanupSetKey, chainId, JobHelper.ToTimestamp(JobClock.UtcNow));
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"What the change of chain '{chainId}' left to do couldn't be recorded as done; it is checked again later.", ex);
        }

        if (remaining != null)
        {
            Logger.Warn(
                $"Chain '{chainId}' changed, but {remaining.Delete.Count} of its replaced jobs are still to delete" +
                (remaining.Release != null ? $" and its step '{remaining.Release}' is still held" : string.Empty) +
                ". They can't run: the chain's record doesn't list them. The next change of the chain or a server's chain maintenance finishes it" +
                (storage.HasFeature(JobStorageFeatures.ExtendedApi) ? "." : " (this storage has no chain maintenance: retry, replace or delete the chain to finish it)."));
        }

        return remaining;
    }

    /// <summary>
    /// Deletes the jobs a change left to delete, in their order (the deepest steps first), and enqueues the step it
    /// held. A job that fails is kept for later and doesn't stop the others. With <paramref name="heldJustNow"/>,
    /// the caller holds the step to release itself, under the chain's lock: the step is enqueued from the Scheduled
    /// state as it is. Later, it is enqueued only while its state still says the retry holds it, so a step that left
    /// the hold and was scheduled again for another reason (a retry of its own) is left alone.
    /// </summary>
    /// <returns>What is still left to do, <c>null</c> when nothing is.</returns>
    private static ChainCleanup? RunCleanup(IBackgroundJobClient client, IStorageConnection connection, string chainId, ChainCleanup cleanup, bool heldJustNow, out int deleted)
    {
        deleted = 0;
        List<string> remaining = [];

        foreach (string jobId in cleanup.Delete)
        {
            try
            {
                if (!TryDeleteReplaced(client, connection, jobId, cleanup.Reason, ref deleted)) remaining.Add(jobId);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Background job '{jobId}', replaced in chain '{chainId}', couldn't be deleted yet. It can't run: the chain's record doesn't list it.", ex);
                remaining.Add(jobId);
            }
        }

        string? release = cleanup.Release;
        if (release != null)
        {
            try
            {
                ReleaseHeld(client, connection, release, heldJustNow);
                release = null;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Background job '{release}' of chain '{chainId}' couldn't be enqueued after the retry of the chain; it runs once its hold ends, or when the chain maintenance enqueues it.", ex);
            }
        }

        return remaining.Count == 0 && release == null ? null : new ChainCleanup { Reason = cleanup.Reason, Delete = remaining, Release = release };
    }

    // A replaced job is deleted from a state where it hasn't started; one that finished, expired or runs is left
    private static bool TryDeleteReplaced(IBackgroundJobClient client, IStorageConnection connection, string jobId, string reason, ref int deleted)
    {
        // Twice: its state may change between the read and the deletion (a hold that ends, a parent that finishes)
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string? state = connection.GetStateData(jobId)?.Name;
            if (state == null || !HasNotStarted(state)) return true;

            if (client.ChangeState(jobId, new DeletedState { Reason = reason }, state))
            {
                deleted++;
                return true;
            }
        }

        return false;
    }

    // The step a retry held is enqueued, unless it left the hold already (the delayed job scheduler enqueued it)
    private static void ReleaseHeld(IBackgroundJobClient client, IStorageConnection connection, string jobId, bool heldJustNow)
    {
        if (!heldJustNow)
        {
            StateData? state = connection.GetStateData(jobId);
            if (state == null ||
                !ScheduledState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(state.Reason, ChainRetriedReason, StringComparison.Ordinal))
            {
                return;
            }
        }

        client.ChangeState(jobId, new EnqueuedState { Reason = ChainRetriedReason }, ScheduledState.StateName);
    }
}
