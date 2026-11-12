using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    /// <summary>The set that lists the chains a retry or a replacement left jobs to delete in, scored by the time of the last attempt.</summary>
    internal const string CleanupSetKey = "chains:cleanup";

    /// <summary>
    /// The set that lists the chains with a step that waits for several steps, scored by the time a server looks at
    /// them next: such a step is started after the last of those steps is committed, by a process that may stop first.
    /// </summary>
    internal const string WatchSetKey = "chains:joins";

    /// <summary>The lock one server holds while it maintains the chains.</summary>
    internal const string MaintenanceLockResource = "lock:chains:maintenance";

    /// <summary>How many listed chains one maintenance run looks at, at most.</summary>
    internal const int MaxListedPerRun = 1000;

    /// <summary>How many chains with jobs left to delete one maintenance run finishes, at most.</summary>
    internal const int MaxCleanupsPerRun = 50;

    /// <summary>How many chains with steps that wait for several steps one maintenance run looks at, at most.</summary>
    internal const int MaxWatchedPerRun = 200;

    private const int ListPageSize = 100;
    private const string ConditionNotMetReason = "Continuation condition was not met";

    // The longest a chain whose joins still wait is left alone between two looks
    private static readonly TimeSpan MaxWatchInterval = TimeSpan.FromHours(4);

    /// <summary>What a maintenance run did.</summary>
    /// <param name="Ran">False when another server is doing it.</param>
    /// <param name="MoreToDo">Whether the run stopped at one of its limits with work left: the next run needn't wait.</param>
    internal readonly record struct MaintenanceRun(bool Ran, bool MoreToDo);

    /// <summary>
    /// One maintenance run, by one server at a time: removes from the list of chains the ones whose record expired
    /// (where records expire), finishes what interrupted retries and replacements left to do, and starts the
    /// waiting steps that were to start long ago and never were told. <paramref name="listPosition"/> is the score
    /// in the list of chains where the run before stopped, and receives where this one stops (0 to start over).
    /// </summary>
    internal static MaintenanceRun Maintain(JobStorage storage, IBackgroundJobClient client, ref double listPosition)
    {
        using IStorageConnection connection = storage.GetConnection();

        IDisposable maintenanceLock;
        try
        {
            maintenanceLock = connection.AcquireDistributedLock(MaintenanceLockResource, TimeSpan.Zero);
        }
        catch (DistributedLockTimeoutException)
        {
            return new MaintenanceRun(false, false);
        }

        using (maintenanceLock)
        {
            int unlisted = 0;
            try
            {
                // Nothing expires without the extended API, which the walk needs too
                if (connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.ExtendedApi))
                {
                    (listPosition, unlisted) = PruneList(storage, storageConnection, listPosition);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The rest is still done; the list is walked again from the same place
                Logger.WarnException("The chains whose record expired couldn't be removed from the list of chains; the next maintenance run tries again.", ex);
            }

            int finished = FinishCleanups(storage, client, connection, out bool moreCleanups);
            int started = RedriveWatched(storage, client, connection, out bool moreWatched);

            if (unlisted > 0 || finished > 0 || started > 0)
            {
                Logger.Info($"Chain maintenance: {unlisted} chains whose record expired left the list, {finished} interrupted changes of chains were finished, {started} waiting steps that were never told to start were started or skipped.");
            }

            return new MaintenanceRun(true, listPosition > 0 || moreCleanups || moreWatched);
        }
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
            if (from > oldest)
            {
                return (0, unlisted);
            }

            List<string> page = pageSize > 1
                ? connection.GetFirstByLowestScoreFromSet(SetKey, from, oldest, pageSize)
                : connection.GetFirstByLowestScoreFromSet(SetKey, from, oldest) is { } single ? [single] : [];

            int expired = Unlist(connection, page, out double last);
            examined += page.Count;
            unlisted += expired;

            // Every chain old enough was looked at: the next run starts over
            if (page.Count < pageSize)
            {
                return (0, unlisted);
            }

            if (expired > 0 || last > from)
            {
                // The chains kept at the score the next page starts from come back with it
                from = Math.Max(last, from);
                continue;
            }

            // A whole page of chains kept, all created in the second the page starts at: the next page would be
            // the same. The rest of that second is read at once, as far as one run goes, and the walk steps to the
            // next second (scores are whole seconds). A storage that reads one chain at a time can't read the rest
            // of the second: the chains created in the same second as one still recorded stay listed until that
            // one's record expires too.
            if (pageSize > 1)
            {
                HashSet<string> seen = [.. page];
                List<string> second = [.. connection.GetFirstByLowestScoreFromSet(SetKey, from, from, MaxListedPerRun).Where(x => !seen.Contains(x))];

                unlisted += Unlist(connection, second, out _);
                examined += second.Count;
            }

            from += 1;
        }

        return (from, unlisted);
    }

    /// <summary>
    /// Removes from the list the chains of a page that have no record any more; <paramref name="last"/> receives
    /// the latest creation time, as a score, of the ones kept (0 when none says).
    /// </summary>
    private static int Unlist(JobStorageConnection connection, List<string> page, out double last)
    {
        List<string> expired = [];
        last = 0;

        foreach (string chainId in page)
        {
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
        }

        return expired.Count;
    }

    /// <summary>
    /// The entries of a set that are due, lowest score first, at most <paramref name="max"/>. A storage that reads
    /// one entry at a time is asked again after each: the caller removes the entry or gives it the score of now,
    /// so the next one comes up. There, only the entries due a second ago are read, so one given a new score waits
    /// for the next run, and the reading stops at an entry that comes up twice.
    /// </summary>
    private static IEnumerable<string> TakeDue(JobStorage storage, IStorageConnection connection, string key, double until, int max)
    {
        if (connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest))
        {
            foreach (string value in storageConnection.GetFirstByLowestScoreFromSet(key, 0, until, max))
            {
                yield return value;
            }

            yield break;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < max; i++)
        {
            string? value = connection.GetFirstByLowestScoreFromSet(key, 0, until - 1);
            if (value == null || !seen.Add(value))
            {
                yield break;
            }

            yield return value;
        }
    }

    /// <summary>Gives an entry of a set a new score, or removes it. Best effort: it comes up again as it is.</summary>
    private static void Rescore(IStorageConnection connection, string key, string value, DateTime? next)
    {
        try
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            if (next.HasValue)
            {
                transaction.AddToSet(key, value, JobHelper.ToTimestamp(next.Value));
            }
            else
            {
                transaction.RemoveFromSet(key, value);
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The entry '{value}' of the set '{key}' couldn't be updated; it is looked at again as it is.", ex);
        }
    }

    private static string? ReadField(JobStorage storage, IStorageConnection connection, string chainId, string field)
    {
        return connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.ExtendedApi)
            ? storageConnection.GetValueFromHash(KeyPrefix + chainId, field)
            : connection.GetAllEntriesFromHash(KeyPrefix + chainId)?.GetValueOrDefault(field);
    }

    /// <summary>
    /// Finishes what retries and replacements left to do in the chains listed for it, the ones tried longest ago
    /// first. A chain that is changing right now, or that fails, goes to the end of the list, so it doesn't keep
    /// the others from their turn.
    /// </summary>
    private static int FinishCleanups(JobStorage storage, IBackgroundJobClient client, IStorageConnection connection, out bool more)
    {
        DateTime now = JobClock.UtcNow;
        int finished = 0;
        int taken = 0;

        foreach (string chainId in TakeDue(storage, connection, CleanupSetKey, JobHelper.ToTimestamp(now), MaxCleanupsPerRun))
        {
            taken++;

            try
            {
                using (connection.AcquireDistributedLock(GetLockResource(chainId), TimeSpan.Zero))
                {
                    // Only the field: the chain's definition, with the types of its jobs, isn't loaded where the
                    // storage reads one field
                    ChainCleanup? cleanup = ChainCleanup.Parse(ReadField(storage, connection, chainId, CleanupField));

                    if (cleanup == null)
                    {
                        // Finished meanwhile, or the chain was deleted or its record expired
                        Rescore(connection, CleanupSetKey, chainId, null);
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
                Rescore(connection, CleanupSetKey, chainId, now);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"What a change of chain '{chainId}' left to do couldn't be finished; a later maintenance run tries again.", ex);
                Rescore(connection, CleanupSetKey, chainId, now);
            }
        }

        more = taken >= MaxCleanupsPerRun;
        return finished;
    }

    /// <summary>
    /// Looks at the chains with steps that wait for several steps, the ones due first, and starts or skips the steps
    /// that were decided long ago and never told (<c>RedriveLostDecisions</c>). A chain with such steps still
    /// waiting is looked at again later, after a time that grows with its age; the others leave the list.
    /// </summary>
    private static int RedriveWatched(JobStorage storage, IBackgroundJobClient client, IStorageConnection connection, out bool more)
    {
        DateTime now = JobClock.UtcNow;
        int started = 0;
        int taken = 0;

        foreach (string chainId in TakeDue(storage, connection, WatchSetKey, JobHelper.ToTimestamp(now), MaxWatchedPerRun))
        {
            taken++;
            DateTime? next = now + DecisionWindow;

            try
            {
                using (connection.AcquireDistributedLock(GetLockResource(chainId), TimeSpan.Zero))
                {
                    // Null for a chain that was deleted or whose record expired: nothing to watch
                    JobChainInfo? info = ReadSummary(connection, chainId);
                    next = null;

                    if (info != null)
                    {
                        started += RedriveLostDecisions(client, connection, info);

                        // The steps just started are counted as waiting once more, and looked at again
                        if (info.HasWaitingJoins())
                        {
                            next = now + WatchInterval(now - info.CreatedAt);
                        }
                    }
                }
            }
            catch (DistributedLockTimeoutException)
            {
                // The chain is being retried, replaced or deleted: looked at again soon
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Chain '{chainId}' couldn't be checked for steps that were never told to start; a later maintenance run tries again.", ex);
            }

            Rescore(connection, WatchSetKey, chainId, next);
        }

        more = taken >= MaxWatchedPerRun;
        return started;
    }

    // A young chain is looked at often, an old one that still waits (behind a failure, for a step scheduled far
    // ahead) less and less
    private static TimeSpan WatchInterval(TimeSpan age)
    {
        TimeSpan shortest = DecisionWindow + DecisionWindow;
        return age < shortest ? shortest : age > MaxWatchInterval ? MaxWatchInterval : age;
    }

    /// <summary>
    /// Starts, or skips, the waiting steps of a chain that the steps they wait for decided longer ago than
    /// <see cref="DecisionWindow"/> and that were never told: the process that was to tell them stopped between the
    /// commit of the last of those steps and the decision, or the decision failed. Each is decided again from the
    /// stored states, with the rules of the continuation filters, and changed only from the Awaiting state.
    /// </summary>
    /// <returns>The number of steps started or skipped.</returns>
    internal static int RedriveLostDecisions(IBackgroundJobClient client, IStorageConnection connection, JobChainInfo info)
    {
        int redriven = 0;

        foreach (JobChainStepInfo step in info.NeverToldSteps())
        {
            try
            {
                if (Redrive(client, connection, step.JobId))
                {
                    redriven++;
                    Logger.Warn($"Step '{step.Key}' of chain '{info.ChainId}', background job '{step.JobId}', was still waiting although the steps before it finished more than {DecisionWindow.TotalMinutes:0} minutes ago: the process that was to start it stopped, or couldn't. It was decided again now.");
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Step '{step.Key}' of chain '{info.ChainId}', background job '{step.JobId}', was never told to start and couldn't be started now; it is tried again later.", ex);
            }
        }

        return redriven;
    }

    /// <summary>
    /// The same as <see cref="RedriveLostDecisions(IBackgroundJobClient, IStorageConnection, JobChainInfo)"/> for a
    /// chain read here, unless it is being retried, replaced or deleted right now.
    /// </summary>
    internal static int RedriveLostDecisions(JobStorage storage, IBackgroundJobClient client, string chainId)
    {
        using IStorageConnection connection = storage.GetConnection();

        try
        {
            using (connection.AcquireDistributedLock(GetLockResource(chainId), TimeSpan.Zero))
            {
                JobChainInfo? info = ReadSummary(connection, chainId);
                return info == null ? 0 : RedriveLostDecisions(client, connection, info);
            }
        }
        catch (DistributedLockTimeoutException)
        {
            return 0;
        }
    }

    // Decides a waiting step again from the stored states of the steps it waits for, as the filter that would
    // have told it does, and changes it only from the Awaiting state: whoever decides it first wins
    private static bool Redrive(IBackgroundJobClient client, IStorageConnection connection, string jobId)
    {
        StateData? state = connection.GetStateData(jobId);
        if (state?.Data == null || !IsAwaiting(state.Name))
        {
            return false;
        }

        HashSet<string> finalStates = ContinuationsSupportAttribute.KnownFinalStates;
        FanInContinuationsSupportAttribute.Decision decision;
        IState? nextState = null;

        if (AwaitingJobsState.FromStateData(state.Data) is { } group)
        {
            decision = FanInContinuationsSupportAttribute.Evaluate(
                group.Mode,
                group.Options,
                group.ParentIds,
                parentId => FanInContinuationsSupportAttribute.GetParentState(connection, parentId),
                finalStates);
            nextState = group.NextState;
        }
        else
        {
            if (!state.Data.TryGetValue("ParentId", out string? parentId) ||
                !state.Data.TryGetValue("Options", out string? serializedOptions) ||
                !Enum.TryParse(serializedOptions, out JobContinuationOptions options))
            {
                return false;
            }

            decision = ContinuationsSupportAttribute.Decide(connection.GetStateData(parentId)?.Name, options, finalStates);

            if (decision == FanInContinuationsSupportAttribute.Decision.Start && state.Data.TryGetValue("NextState", out string? serializedNextState))
            {
                nextState = SerializationHelper.Deserialize<IState>(serializedNextState, SerializationOption.TypedInternal);
            }
        }

        if (decision == FanInContinuationsSupportAttribute.Decision.Undecided)
        {
            return false;
        }

        IState newState = decision == FanInContinuationsSupportAttribute.Decision.Start
            ? nextState ?? new EnqueuedState()
            : new DeletedState { Reason = ConditionNotMetReason };

        return client.ChangeState(jobId, newState, AwaitingState.StateName);
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
            Rescore(connection, CleanupSetKey, chainId, JobClock.UtcNow);
        }

        if (remaining != null)
        {
            Logger.Warn(
                $"Chain '{chainId}' changed, but {remaining.Delete.Count} of its replaced jobs are still to delete" +
                (remaining.Release != null ? $" and its step '{remaining.Release}' is still held" : string.Empty) +
                ". They can't run: the chain's record doesn't list them. The next change of the chain or a server's chain maintenance finishes it.");
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
                if (!TryDeleteReplaced(client, connection, jobId, cleanup.Reason, ref deleted))
                {
                    remaining.Add(jobId);
                }
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
            if (state == null || !HasNotStarted(state))
            {
                return true;
            }

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
