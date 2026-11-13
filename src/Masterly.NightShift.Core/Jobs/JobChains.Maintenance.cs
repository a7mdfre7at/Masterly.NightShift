using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    /// <summary>The set that lists the chains a retry or a replacement left jobs to delete in, scored by the time of the last attempt.</summary>
    internal const string CleanupSetKey = "chains:cleanup";

    /// <summary>
    /// The set of the chains a server is to look at again, scored by the time of that look. It is the index the
    /// chain maintenance reads, so a run costs what is due and not what is kept. A chain enters it when it is
    /// recorded and stays while a step of it waits for others (a waiting step is started by the process that
    /// finishes the last step it waits for, which may stop first) or its record is to be kept longer; each look
    /// says when the next one is.
    /// </summary>
    internal const string DueSetKey = "chains:due";

    /// <summary>
    /// The set of the chains left to expire, scored by the time they leave the list of chains: nothing of them is
    /// left to run or to run again, so their record expires, or their record is gone before its time. Only on a
    /// storage that expires records, where a listed chain is in this set or in <see cref="DueSetKey"/>.
    /// </summary>
    internal const string ExpirySetKey = "chains:expiring";

    /// <summary>
    /// The hash that says how far the chains listed by a build without <see cref="DueSetKey"/> have been given a
    /// place in it: the number of chains left to go through, or that it is done.
    /// </summary>
    internal const string IndexKey = "chains:index";

    /// <summary>The lock one server holds while it maintains the chains.</summary>
    internal const string MaintenanceLockResource = "lock:chains:maintenance";

    /// <summary>How many chains with jobs left to delete one maintenance run finishes, at most.</summary>
    internal const int MaxCleanupsPerRun = 50;

    /// <summary>How many chains that are due for a look one maintenance run looks at, at most.</summary>
    internal const int MaxLooksPerRun = 500;

    /// <summary>How many chains left to expire one maintenance run looks at, at most.</summary>
    internal const int MaxExpiringPerRun = 500;

    /// <summary>How many chains listed by an earlier build one maintenance run gives a place in the index, at most.</summary>
    internal const int MaxIndexedPerRun = 500;

    private const int IndexWriteSize = 100;
    private const string IndexedField = "Listed";
    private const string IndexedDone = "done";
    private const string ConditionNotMetReason = "Continuation condition was not met";

    // The set in which an earlier build listed the chains with a step that waits for several steps
    private const string LegacyWatchSetKey = "chains:joins";

    // The longest a chain is left alone between two looks while something of it runs: a step that was never told
    // to start is started within this time, and the chain doesn't look settled for longer
    private static readonly TimeSpan MaxWatchInterval = TimeSpan.FromMinutes(15);

    // How long after its record expires a chain left to expire is looked at, and leaves the list
    private static readonly TimeSpan ExpiryLookDelay = TimeSpan.FromMinutes(1);

    /// <summary>What a maintenance run did.</summary>
    /// <param name="Ran">False when another server is doing it.</param>
    /// <param name="MoreToDo">Whether the run stopped at one of its limits with work left: the next run needn't wait.</param>
    internal readonly record struct MaintenanceRun(bool Ran, bool MoreToDo);

    /// <summary>
    /// Where a look leaves a chain: in a set, to come up again at a time, or in none; and then, with
    /// <paramref name="Unlisted"/>, out of the list of chains too.
    /// </summary>
    private readonly record struct ChainPlacement(string? Set, DateTime At, bool Unlisted = false)
    {
        /// <summary>
        /// When the chain was created, for a chain to put back in the list of chains: its record was kept after it
        /// had no time left to live, when a server may have taken the chain out of the list.
        /// </summary>
        public DateTime? ListedAt { get; init; }

        /// <summary>The chain leaves the list of chains: it has no record any more.</summary>
        public static ChainPlacement Unlist => new(null, default, true);

        /// <summary>Nothing is left to do for the chain: no server looks at it again, and it stays listed.</summary>
        public static ChainPlacement Leave => new(null, default);
    }

    /// <summary>
    /// One maintenance run, by one server at a time. It finishes what interrupted retries and replacements left to
    /// do, and looks at the chains that are due for it (<see cref="DueSetKey"/>, <see cref="ExpirySetKey"/>): it
    /// starts the waiting steps that were to start long ago and never were told, keeps the record of a chain
    /// longer while a step of it is left to run or failed for good, and takes a chain whose record expired out of
    /// the list of chains (where records expire). A run reads the chains that are due and no others, a bounded
    /// number of them. <paramref name="indexed"/> says whether this process knows that every listed chain has a
    /// place in the index; until it does, the run also gives the chains listed by an earlier build theirs.
    /// </summary>
    internal static MaintenanceRun Maintain(JobStorage storage, IBackgroundJobClient client, ref bool indexed, CancellationToken stopping = default)
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
            int placed = 0;
            bool moreToIndex = false;

            if (!indexed)
            {
                try
                {
                    indexed = IndexListed(storage, connection, out placed);
                    moreToIndex = !indexed;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // The rest is still done; the list is gone through again from the same place
                    Logger.WarnException("The chains listed by an earlier build couldn't be given a place among the chains to look at; the next maintenance run tries again.", ex);
                }
            }

            int finished = FinishCleanups(storage, client, connection, out bool moreCleanups);

            Looks looks = new();
            bool moreExpiring = storage.HasFeature(JobStorageFeatures.ExtendedApi) &&
                                LookAtDue(storage, client, connection, ExpirySetKey, MaxExpiringPerRun, looks, stopping);
            bool moreDue = LookAtDue(storage, client, connection, DueSetKey, MaxLooksPerRun, looks, stopping);

            if (looks.Lost > 0)
            {
                Logger.Warn($"Chain maintenance: {looks.Lost} chains are listed but have no record, chain '{looks.FirstLost}' among them: the storage dropped the record before it was due to expire, or no server maintained the chains for longer than records are kept. Their steps still run; they can't be read, retried or replaced any more, and leave the list in {JobChain.RecordRetention.TotalDays:0.#} days.");
            }

            if (placed > 0 || finished > 0 || looks.Started > 0 || looks.Unlisted > 0)
            {
                Logger.Info($"Chain maintenance: {looks.Unlisted} chains whose record expired left the list, {finished} interrupted changes of chains were finished, {looks.Started} waiting steps that were never told to start were started or skipped, {placed} chains listed by an earlier build were given a look.");
            }

            return new MaintenanceRun(true, moreToIndex || moreCleanups || moreExpiring || moreDue);
        }
    }

    /// <summary>What the looks of a maintenance run did.</summary>
    private sealed class Looks
    {
        public int Started { get; set; }

        public int Unlisted { get; set; }

        public int Lost { get; set; }

        public string? FirstLost { get; set; }
    }

    /// <summary>
    /// Gives the chains listed by a build that kept no index of them a look by a server, now: up to
    /// <see cref="MaxIndexedPerRun"/> a run, from the newest to the oldest, writing in the storage how many are left
    /// to go through (<see cref="IndexKey"/>). So it is done once for a storage, by whichever servers maintain the
    /// chains, and costs nothing afterwards: a chain recorded by this build has its place from the start. A storage
    /// without ranges of sets is read whole, in one run.
    /// </summary>
    /// <returns>Whether every listed chain has its place.</returns>
    private static bool IndexListed(JobStorage storage, IStorageConnection connection, out int placed)
    {
        placed = 0;
        JobStorageConnection? ranged = storage.HasFeature(JobStorageFeatures.ExtendedApi) ? connection as JobStorageConnection : null;

        string? left = ranged != null
            ? ranged.GetValueFromHash(IndexKey, IndexedField)
            : connection.GetAllEntriesFromHash(IndexKey)?.GetValueOrDefault(IndexedField);

        if (left == IndexedDone)
        {
            return true;
        }

        List<string> chains;
        long remaining = 0;

        if (ranged != null)
        {
            // By position, from the end of the list: the chains recorded meanwhile come after, with their place
            long position = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed : ranged.GetSetCount(SetKey);
            int take = (int)Math.Min(position, MaxIndexedPerRun);
            remaining = position - take;

            chains = take > 0 ? ranged.GetRangeFromSet(SetKey, (int)remaining, (int)(position - 1)) : [];

            // A storage that can't look a chain up gives each a look, the ones that have their place too
            if (storage.HasFeature(JobStorageFeatures.Connection.GetSetContains))
            {
                chains.RemoveAll(chainId => ranged.GetSetContains(DueSetKey, chainId) || ranged.GetSetContains(ExpirySetKey, chainId));
            }
        }
        else
        {
            HashSet<string> listed = connection.GetAllItemsFromSet(SetKey);
            listed.ExceptWith(connection.GetAllItemsFromSet(DueSetKey));
            chains = [.. listed];
        }

        double now = JobHelper.ToTimestamp(JobClock.UtcNow);
        int written = 0;

        do
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();

            int until = Math.Min(chains.Count, written + IndexWriteSize);
            for (; written < until; written++)
            {
                transaction.AddToSet(DueSetKey, chains[written], now);
            }

            // How far it came is written with the last chains, so chains that couldn't be written are gone through again
            if (written == chains.Count)
            {
                transaction.SetRangeInHash(IndexKey, new Dictionary<string, string>
                {
                    [IndexedField] = remaining > 0 ? remaining.ToString(CultureInfo.InvariantCulture) : IndexedDone,
                });

                if (remaining == 0 && ranged != null && transaction is JobStorageTransaction storageTransaction)
                {
                    storageTransaction.RemoveSet(LegacyWatchSetKey);
                }
            }

            transaction.Commit();
        }
        while (written < chains.Count);

        placed = chains.Count;
        return remaining == 0;
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
    /// Looks at the chains of a set that are due, the ones due longest first, at most <paramref name="max"/>, and
    /// gives each its place for the next look (<see cref="Look"/>). A chain that is being retried, replaced or
    /// deleted right now is looked at again soon; one that can't be read, a little later, so it doesn't keep the
    /// others from their turn.
    /// </summary>
    /// <returns>Whether the run stopped at the limit: more chains may be due.</returns>
    private static bool LookAtDue(
        JobStorage storage,
        IBackgroundJobClient client,
        IStorageConnection connection,
        string setKey,
        int max,
        Looks looks,
        CancellationToken stopping)
    {
        DateTime now = JobClock.UtcNow;
        int taken = 0;

        foreach (string chainId in TakeDue(storage, connection, setKey, JobHelper.ToTimestamp(now), max))
        {
            // A server that stops leaves the rest to the next run, its own or another server's
            if (stopping.IsCancellationRequested)
            {
                return false;
            }

            taken++;
            ChainPlacement placement = new(setKey, now + DecisionWindow);

            try
            {
                using (connection.AcquireDistributedLock(GetLockResource(chainId), TimeSpan.Zero))
                {
                    placement = Look(storage, client, connection, chainId, leftToExpire: setKey == ExpirySetKey, now, looks);
                }
            }
            catch (DistributedLockTimeoutException)
            {
                // The chain is being retried, replaced or deleted: looked at again soon
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Chain '{chainId}' couldn't be looked at (for steps that were never told to start, and to keep its record); a later maintenance run tries again.", ex);
                placement = new ChainPlacement(setKey, now + MaxWatchInterval);
            }

            if (Place(connection, chainId, setKey, placement) && placement.Unlisted)
            {
                looks.Unlisted++;
            }
        }

        return taken >= max;
    }

    /// <summary>
    /// One look at a chain, under its lock: starts or skips its waiting steps that were decided long ago and never
    /// told (<c>RedriveLostDecisions</c>), keeps its record longer while the job of a step still exists in a state
    /// other than Succeeded or Deleted, and says where the chain goes next.
    /// <list type="bullet">
    /// <item><description>
    /// A step waits: looked at again after a few minutes while something of the chain runs, and otherwise after as
    /// long as the chain has stood still, so less and less often (<see cref="WatchInterval"/>).
    /// </description></item>
    /// <item><description>
    /// Its record expires: looked at again a while before (<see cref="KeepAhead"/>), at the latest, to keep it longer.
    /// </description></item>
    /// <item><description>
    /// Nothing of it is left to run or to run again: it is left to expire, and looked at a moment after its record
    /// does, to leave the list of chains. Where records don't expire, no server looks at it again.
    /// </description></item>
    /// <item><description>
    /// It has no record: it leaves the list of chains. Unless it wasn't left to expire, on a storage that expires
    /// records: its record is gone before its time, and it stays listed for as long as a record is kept, because
    /// its steps still run while it is (<see cref="JobChainStepFilter"/>).
    /// </description></item>
    /// </list>
    /// </summary>
    private static ChainPlacement Look(
        JobStorage storage,
        IBackgroundJobClient client,
        IStorageConnection connection,
        string chainId,
        bool leftToExpire,
        DateTime now,
        Looks looks)
    {
        JobStorageConnection? expiring = storage.HasFeature(JobStorageFeatures.ExtendedApi) ? connection as JobStorageConnection : null;
        JobChainInfo? info = ReadSummary(connection, chainId);

        if (info == null)
        {
            if (expiring == null || leftToExpire || !IsListed(storage, connection, chainId))
            {
                return ChainPlacement.Unlist;
            }

            looks.Lost++;
            looks.FirstLost ??= chainId;
            return new ChainPlacement(ExpirySetKey, now + JobChain.RecordRetention);
        }

        int redriven = RedriveLostDecisions(client, connection, info);
        looks.Started += redriven;

        if (!info.HasLiveSteps())
        {
            if (expiring == null)
            {
                return ChainPlacement.Leave;
            }

            // A record the storage still holds after its time is as good as gone
            TimeSpan timeToLive = expiring.GetHashTtl(GetKey(chainId));
            return timeToLive > TimeSpan.Zero
                ? new ChainPlacement(ExpirySetKey, now + timeToLive + ExpiryLookDelay)
                : ChainPlacement.Unlist;
        }

        DateTime? next = info.HasWaitingSteps() ? now + WatchInterval(info, now, redriven > 0) : null;
        bool lapsed = false;

        if (expiring != null)
        {
            // Kept until RecordRetention from now, or from the time a scheduled step is due. A record whose
            // expiry couldn't be read or written is tried again sooner than it could expire.
            DateTime? expireAt = ExtendRecordCore(storage, connection, chainId, info.LastDueAt() ?? now, recorded: true, out lapsed);
            DateTime keepAt = expireAt.HasValue ? expireAt.Value - KeepAhead() : now + MaxWatchInterval;

            if (next == null || keepAt < next.Value)
            {
                next = keepAt;
            }
        }

        if (next == null)
        {
            return ChainPlacement.Leave;
        }

        DateTime soonest = now + DecisionWindow;
        return new ChainPlacement(DueSetKey, next.Value < soonest ? soonest : next.Value) { ListedAt = lapsed ? info.CreatedAt : null };
    }

    /// <summary>
    /// How long until a server looks again at a chain with steps that wait. While something of it runs or is about
    /// to, or a step was just started here: a few minutes, <see cref="MaxWatchInterval"/> at most, so a step that
    /// is never told to start is started soon. Otherwise the chain stands still, behind a step that failed for good
    /// or until a scheduled step is due: it is looked at after as long as it has stood still, so less and less
    /// often, and no later than a moment after its next scheduled step is due. A step that leaves the state it
    /// finished in brings the look forward (<see cref="LookSoonAfterCommit"/>).
    /// </summary>
    private static TimeSpan WatchInterval(JobChainInfo info, DateTime now, bool redriven)
    {
        TimeSpan shortest = DecisionWindow + DecisionWindow;
        TimeSpan still = now - info.LastMovedAt();
        TimeSpan interval = still < shortest ? shortest : still;

        if (redriven || info.IsMoving(now))
        {
            return interval > MaxWatchInterval ? MaxWatchInterval : interval;
        }

        if (info.NextDueAt(now) is { } dueAt && dueAt - now + shortest < interval)
        {
            interval = dueAt - now + shortest;
        }

        return interval;
    }

    /// <summary>
    /// Writes where a look leaves a chain: its new place and time, and that it left the place it was in, or the
    /// list of chains. Best effort: a chain that couldn't be written comes up again as it is.
    /// </summary>
    /// <returns>Whether it was written.</returns>
    private static bool Place(IStorageConnection connection, string chainId, string from, ChainPlacement placement)
    {
        try
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();

            if (placement.Set != null)
            {
                transaction.AddToSet(placement.Set, chainId, JobHelper.ToTimestamp(placement.At));
            }

            if (placement.Set != from)
            {
                transaction.RemoveFromSet(from, chainId);
            }

            if (placement.Unlisted)
            {
                transaction.RemoveFromSet(SetKey, chainId);
                transaction.RemoveFromSet(from == DueSetKey ? ExpirySetKey : DueSetKey, chainId);
            }
            else if (placement.ListedAt is { } createdAt)
            {
                transaction.AddToSet(SetKey, chainId, JobHelper.ToTimestamp(createdAt));
            }

            transaction.Commit();

            if (placement.Unlisted)
            {
                ForgetExpiry(chainId);
            }

            return true;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"Chain '{chainId}' couldn't be given its next look; it is looked at again as it is.", ex);
            return false;
        }
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

            StateData? parent = connection.GetStateData(parentId);
            decision = ContinuationsSupportAttribute.Decide(parent?.Name, options, finalStates);

            if (decision == FanInContinuationsSupportAttribute.Decision.Start)
            {
                // With UseResultsInContinuations the filter hands the parent's result, or its exception, to the
                // continuation as it starts it: so does this, from the parent's stored state. A continuation
                // started without would run with the wrong input, so one whose parent's state can't be read any
                // more is left waiting, for a requeue. A step that waits for several steps is never handed any.
                if (GlobalJobFilters.Filters.Any(static x => x.Instance is ContinuationsSupportAttribute { PushResults: true }))
                {
                    if (parent?.Data == null)
                    {
                        Logger.Warn($"Background job '{jobId}' was never started after its parent '{parentId}' finished, and isn't started now, because results are passed to continuations (UseResultsInContinuations) and the parent's can't be read any more: requeue it once its input is right.");
                        return false;
                    }

                    ContinuationsSupportAttribute.HandOver(connection, jobId, parent);
                }

                if (state.Data.TryGetValue("NextState", out string? serializedNextState))
                {
                    nextState = SerializationHelper.Deserialize<IState>(serializedNextState, SerializationOption.TypedInternal);
                }
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
