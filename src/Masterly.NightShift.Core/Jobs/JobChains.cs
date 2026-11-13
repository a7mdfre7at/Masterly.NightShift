using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>Reads, reruns, replaces and deletes stored <see cref="JobChain"/>s.</summary>
public static partial class JobChains
{
    /// <summary>The job parameter with a step's chain.</summary>
    public const string ChainIdParameter = "ChainId";

    /// <summary>The job parameter with a step's key, <c>level.index</c>.</summary>
    public const string ChainStepParameter = "ChainStep";

    /// <summary>
    /// The job parameter that marks a step of a chain that was recorded: set when the record that lists the step
    /// is written. A step without it belongs to a chain whose creation, retry or replacement never finished. Its
    /// value is the time that record is kept at least, in seconds (<see cref="JobChain.RecordRetention"/> in the
    /// process that wrote it); <c>true</c> on a step marked by an earlier build.
    /// </summary>
    public const string ChainRecordedParameter = "ChainRecorded";

    /// <summary>The set that lists the chains, scored by creation time.</summary>
    public const string SetKey = "chains";

    private const string KeyPrefix = "chain:";
    private const string ChainDeletedReason = "The chain was deleted";
    private const string ChainReplacedReason = "The chain was replaced";
    private const string ChainRetriedReason = "Chain retried from this step";
    private const string ChainRetryReplacedReason = "Replaced by a retry of the chain";
    private const string ChainRetryFailedReason = "The retry of the chain failed";
    private const string ChainNotCreatedReason = "The chain couldn't be created";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);

    // How long the write of a record waits for the lock of the job it is decided under. A job is locked only
    // while its state changes.
    private static readonly TimeSpan CommitLockTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long the write of a record may take, from the moment the job it is decided under is locked, before the
    /// lock is taken again to read that job afterwards: a lock a storage keeps by a lease doesn't outlive a holder
    /// that was frozen for longer than the lease, and the shortest of them lasts thirty seconds. Replaceable for
    /// tests.
    /// </summary>
    internal static TimeSpan SafeLockTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after the steps it waits for finished a waiting step whose condition they meet counts as about to
    /// start (<see cref="JobChainInfo.IsSettled"/>): it is told within seconds. Replaceable for tests.
    /// </summary>
    internal static TimeSpan DecisionWindow { get; set; } = TimeSpan.FromMinutes(5);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobChains));

    /// <summary>The hash that records a chain.</summary>
    public static string GetKey([NotNull] string chainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainId);
        return KeyPrefix + chainId;
    }

    /// <param name="storage">The storage.</param>
    extension([NotNull] JobStorage storage)
    {
        /// <summary>
        /// Reads a chain with the current state of each step, or <c>null</c> when it isn't recorded (any more). A
        /// step whose method can't be loaded any more (it was renamed or removed since the chain was created) has
        /// no <see cref="JobChainStepInfo.Job"/>.
        /// </summary>
        [CanBeNull]
        public JobChainInfo? GetChain([NotNull] string chainId)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentException.ThrowIfNullOrWhiteSpace(chainId);

            using IStorageConnection connection = storage.GetConnection();
            ChainRecord? record = ChainRecord.Read(connection, chainId);
            return record?.ToInfo(connection);
        }

        /// <summary>
        /// Runs a settled chain again from a step: the step's job is requeued (created again when it expired), and
        /// every step after it gets a new job, waiting for its parents as when the chain was created. The steps before
        /// it, and the branches beside it, keep their outcome and aren't run again. When a new step can't be created,
        /// nothing changes: the jobs created are deleted and the step goes back to its state. Once the retry is
        /// recorded it has succeeded: replaced jobs that can't be deleted right away are deleted later (they can't
        /// run meanwhile), and that is logged, not thrown. Should the process stop before the retry is recorded,
        /// the retry never happened: a server puts the step back into the state it was in, a little over two
        /// minutes later, without running it, and the jobs the retry had created never run. A waiting step of the
        /// chain that was to start long ago and never was told is started first, and the retry refused: the chain
        /// then finishes by itself.
        /// </summary>
        /// <param name="stepJobId">The current job of the step to run again.</param>
        /// <returns>The chain's jobs after the retry.</returns>
        /// <exception cref="InvalidOperationException">
        /// The job isn't a current step of a recorded chain, a step of the chain hasn't finished yet, the step changed
        /// meanwhile, a job the new steps wait for expired, a step that must be created again names a method that
        /// can't be loaded any more, or this process stalled for so long that a server took the retry for abandoned
        /// and put its step back or deleted one of its jobs. In each case nothing of the retry is left to run.
        /// </exception>
        [NotNull]
        public JobChainResult RetryChainFrom([NotNull] string stepJobId)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepJobId);

            return Retry(storage, new BackgroundJobClient(storage), stepJobId);
        }

        /// <summary>
        /// Replaces a chain that hasn't started: its first step is still enqueued or scheduled and the others are
        /// waiting. The new chain is created under the same identifier, enqueued or scheduled for the same moment as
        /// the old first step and with its schedule policy, late-start limit and queue; then the old steps are
        /// deleted. The old first step keeps its state and its time until then, and no step of the chain starts
        /// meanwhile: when the new chain can't be created, or the process stops before it is recorded, the old one
        /// is kept as it was and runs when it was to run. Once the new chain is recorded the replacement has
        /// succeeded: old steps that can't be deleted right away are deleted later (they can't run meanwhile), and
        /// that is logged, not thrown.
        /// </summary>
        /// <returns>The new chain's jobs.</returns>
        /// <exception cref="InvalidOperationException">
        /// The chain isn't recorded, it started, or this process stalled for so long that a server took the
        /// replacement for abandoned and deleted one of its jobs: the old chain is then kept as it was.
        /// </exception>
        [NotNull]
        public JobChainResult ReplaceChain([NotNull] string chainId, [NotNull] JobChain chain)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentException.ThrowIfNullOrWhiteSpace(chainId);
            ArgumentNullException.ThrowIfNull(chain);

            return Replace(storage, new BackgroundJobClient(storage), chainId, chain);
        }

        /// <summary>
        /// Deletes the steps of a chain that haven't finished (waiting, scheduled, enqueued, failed and, unless the chain
        /// is <see cref="ChainDeleteBehavior.Restrict"/>, processing) and its record. Returns the number of jobs deleted,
        /// or -1 when the chain isn't recorded.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The chain is <see cref="ChainDeleteBehavior.Restrict"/> and a step is processing, or a step started while
        /// the chain was being deleted and still runs: the steps that could be deleted are, and the record is kept.
        /// </exception>
        public int DeleteChain([NotNull] string chainId)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentException.ThrowIfNullOrWhiteSpace(chainId);

            return Delete(storage, new BackgroundJobClient(storage), chainId);
        }
    }

    /// <summary>
    /// Whether a recorded chain still has something to do by itself, read without loading the methods of its steps.
    /// False for a chain that isn't recorded (any more).
    /// </summary>
    internal static bool IsRunning(JobStorage storage, string chainId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return ReadSummary(connection, chainId) is { IsSettled: false };
    }

    /// <summary>
    /// The identifier of the chain a job creates: always the same for the same job, and as long as a random
    /// one.
    /// </summary>
    internal static string IdForJob(string jobId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"chain-of-job:{jobId}"));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    internal static bool IsSettledState(string? state)
    {
        return state == null ||
               SucceededState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
               DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
               FailedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);
    }

    internal static JobChainResult Retry(JobStorage storage, IBackgroundJobClientV2 client, string stepJobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        string chainId = ReadChainId(connection, stepJobId)
            ?? throw new InvalidOperationException($"Background job '{stepJobId}' isn't a step of a chain.");

        using (connection.AcquireDistributedLock(GetLockResource(chainId), LockTimeout))
        {
            ChainRecord record = ChainRecord.Read(connection, chainId)
                ?? throw new InvalidOperationException($"Chain '{chainId}' isn't recorded any more: its record is kept while a step of it is left to run or failed for good, and for {JobChain.RecordRetention.TotalDays:0.#} days after.");

            // What a change before this one left to do comes first: a step it still holds isn't settled
            record = FinishPendingCleanup(client, connection, record);

            string stepKey = record.Jobs.FirstOrDefault(x => x.Value == stepJobId).Key
                ?? throw new InvalidOperationException($"Background job '{stepJobId}' isn't a current step of chain '{chainId}'; a retry replaced it.");

            // A step that was to start long ago and never was told is started rather than replaced: the chain
            // then goes on by itself, and isn't settled
            JobChainInfo info = record.ToInfo(connection);
            if (RedriveLostDecisions(client, connection, info) > 0)
            {
                info = record.ToInfo(connection);
            }

            if (!info.IsSettled)
            {
                throw new InvalidOperationException($"Chain '{chainId}' hasn't finished: a step is still waiting or running.");
            }

            (int level, int index) = ParseKey(stepKey);
            HashSet<int> retried = [level];
            for (int next = level + 1; next < record.Definition.Levels.Count; next++)
            {
                if (retried.Contains(record.Definition.Levels[next].Parent))
                {
                    retried.Add(next);
                }
            }

            // The methods of the steps to create are loaded before anything changes: one that was renamed or removed
            // since the chain was created refuses the retry, clearly, and leaves the chain as it is. The other
            // steps' methods aren't needed.
            Dictionary<int, ChainLevel> levels = [];
            foreach (int next in retried.Where(x => x != level).Order())
            {
                levels[next] = record.Definition.ReadLevel(next, chainId);
            }

            StateData? stepState = connection.GetStateData(stepJobId);
            Job? stepJob = stepState == null ? record.Definition.ReadJob(level, index, chainId) : null;

            // What the step says about this retry while it is held: the generation the retry starts from, and the
            // state to put the step back into should the retry never be recorded
            DateTime? stepExpiry = stepState == null ? null : ReadExpiry(storage, stepJobId);
            RetryMarker? marker = stepState == null ? null : RetryMarker.For(record.Generation, stepState, stepExpiry);

            // From here no step of the chain starts until the retry is recorded or given up. Should this process
            // stop before, the lease lapses and the record still lists the old steps: nothing is lost.
            ChainLease lease = ChainLease.Take(storage, connection, record);
            List<string> created = [];
            ChainRecord updated = record;
            ChainCleanup? cleanup = null;
            string? heldId = null;
            string? refused = null;
            bool putBack = false;
            bool recorded = false;

            try
            {
                // The step leaves its state first: the new steps wait for it, and a new step that reacts to a
                // failure doesn't start from the old one. The hold lasts a little longer than the lease: should
                // this process stop, a server puts the step back into its state when the hold ends, and nothing
                // runs; the record still lists the old steps after it.
                string held = heldId = Hold(storage, client, connection, chainId, stepKey, stepJobId, stepState, stepJob, marker);
                Dictionary<string, string> jobs = new(record.Jobs, StringComparer.Ordinal) { [stepKey] = held };

                CreateLevels(
                    client,
                    record.Definition.Levels.Count,
                    next => levels[next],
                    jobs,
                    chainId,
                    level + 1,
                    retried.Contains,
                    created,
                    lease,
                    () => EnsureRetryCanGoOn(connection, chainId, held, marker, created));

                // The old steps after it wait for it too: the record stops listing them, so they can't run
                // beside the new ones, and they are deleted, the deepest first. Written with the record, so a
                // retry interrupted from here on is finished later.
                cleanup = new ChainCleanup
                {
                    Reason = ChainRetryReplacedReason,
                    Delete =
                    [
                        .. record.Cleanup?.Delete ?? [],
                        .. DeepestFirst(record.Jobs.Where(x => ParseKey(x.Key).Level != level && retried.Contains(ParseKey(x.Key).Level))),
                    ],
                    Release = held,
                };

                updated = record with { Jobs = jobs, Generation = record.Generation + 1, Cleanup = cleanup };
                Stopwatch locked;

                // Under the step's lock: it is still held, so nothing after it started, and nothing does
                // before the record is written
                using (connection.AcquireDistributedJobLock(held, CommitLockTimeout))
                {
                    locked = Stopwatch.StartNew();
                    EnsureStillHeld(connection, chainId, held, marker);

                    // A lease that lapsed is renewed, and what a server may have done meanwhile is looked at
                    if (lease.HasLapsed())
                    {
                        lease.Renew();
                        EnsureNoneRefused(connection, chainId, created, "retried");
                    }

                    WriteCommitted(connection, storage, updated, latestStart: null, [.. created, held]);
                    recorded = true;
                }

                // A process frozen while it wrote the record may have lost its lock and its lease: a server may
                // have taken the retry for abandoned meanwhile, put the step back into the state it was in, and
                // deleted the new steps after it, which the record didn't list yet. The retry is taken back then.
                if (locked.Elapsed > SafeLockTime)
                {
                    refused = FirstRefused(connection, chainId, created);
                    putBack = WasPutBack(connection, chainId, held);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType() && !recorded)
            {
                bool nothingLeft = DeleteCreated(client, connection, created, chainId, ChainRetryFailedReason);
                if (heldId != null)
                {
                    nothingLeft &= TakeBackHeld(client, connection, chainId, heldId, stepState, stepExpiry);
                }

                lease.GiveUp(nothingLeft);
                throw;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Only the release of the step's lock, or the last look at the new steps, can fail once the record
                // is written: the retry is recorded
                Logger.WarnException($"Chain '{chainId}' was retried, but its new steps couldn't be read again afterwards.", ex);
            }

            if (refused != null || putBack)
            {
                // The record goes back first, so the new steps aren't listed while they are deleted. The step
                // goes back into its state, unless a server did that already.
                lease.GiveUp(nothingLeft: false);
                DeleteCreated(client, connection, created, chainId, ChainRetryFailedReason);
                TakeBackHeld(client, connection, chainId, heldId!, stepState, stepExpiry); // Set before the record is written

                throw new InvalidOperationException(
                    $"Chain '{chainId}' wasn't retried: this process was frozen while it recorded the retry, for so long that a server took the retry for abandoned" +
                    (putBack ? $" and put background job '{heldId}' back into the state it was in" : string.Empty) +
                    (refused != null ? $"{(putBack ? ", and" : " and")} deleted the new step '{refused}', which the record didn't list yet" : string.Empty) +
                    ". The retry was taken back, so the chain is as it was; retry it again.");
            }

            // The retry is recorded: nothing fails it from here on. Should the step not be released, a worker
            // takes it when its hold ends and runs it, because the record says the retry happened; or a server's
            // chain maintenance releases it before.
            CompleteCleanup(storage, client, connection, chainId, cleanup!, heldJustNow: true, out int replaced); // Set before the record is written

            Logger.Info($"Chain '{chainId}' retried from step '{stepKey}' (generation {updated.Generation}): {created.Count} steps created after it, {replaced} waiting steps deleted.");
            return updated.ToResult();
        }
    }

    // Under the step's lock. The step is no longer held when a server took the retry for abandoned (this process
    // didn't say for LeaseTime that it was at it) and put the step back, or when somebody changed the job.
    private static void EnsureStillHeld(IStorageConnection connection, string chainId, string heldId, RetryMarker? marker)
    {
        if (!IsStillHeld(connection, heldId, marker))
        {
            throw new InvalidOperationException(
                $"Chain '{chainId}' wasn't retried: background job '{heldId}' is no longer held for the retry. The retry didn't confirm for {LeaseTime.TotalSeconds:0} seconds that it was still at it (a step took that long to create, or the process was paused), so a server took it for abandoned and put the step back into the state it was in; or the job was changed meanwhile. The chain is as it was; retry it again.");
        }
    }

    // After the lease lapsed while the retry created its steps: the step is still held, and no server deleted a
    // step created so far for not being listed
    private static void EnsureRetryCanGoOn(IStorageConnection connection, string chainId, string heldId, RetryMarker? marker, List<string> created)
    {
        using (connection.AcquireDistributedJobLock(heldId, CommitLockTimeout))
        {
            EnsureStillHeld(connection, chainId, heldId, marker);
        }

        EnsureNoneRefused(connection, chainId, created, "retried");
    }

    // Holds the step to retry, or creates it again, held, when its job expired; returns the held job
    private static string Hold(
        JobStorage storage,
        IBackgroundJobClientV2 client,
        IStorageConnection connection,
        string chainId,
        string stepKey,
        string stepJobId,
        StateData? stepState,
        Job? stepJob,
        RetryMarker? marker)
    {
        if (stepState != null)
        {
            Hold(storage, client, connection, chainId, stepJobId, stepState, marker!); // Built by the caller for a step whose job exists
            return stepJobId;
        }

        // No marker: the record doesn't list this job until the retry is recorded, so a server deletes it, as any
        // job the record doesn't list, should the retry be abandoned
        string recreated = Create(client, stepJob!, Hold(ChainRetriedReason), chainId, stepKey, null); // Loaded by the caller when the step's job expired
        Logger.Warn($"Step '{stepKey}' of chain '{chainId}' was created again as background job '{recreated}' to retry it: its job '{stepJobId}' expired.");
        return recreated;
    }

    // Undoes the hold of a retry that isn't recorded: the step goes back into the state it was in, and a job that
    // was created again for it is deleted. Returns whether nothing of the retry is left in that job.
    private static bool TakeBackHeld(IBackgroundJobClient client, IStorageConnection connection, string chainId, string heldId, StateData? stepState, DateTime? stepExpiry)
    {
        if (stepState == null)
        {
            return DeleteCreated(client, connection, [heldId], chainId, ChainRetryFailedReason);
        }

        PutBack(client, connection, heldId, stepState, stepExpiry, chainId);
        return true;
    }

    internal static JobChainResult Replace(JobStorage storage, IBackgroundJobClientV2 client, string chainId, JobChain chain)
    {
        using IStorageConnection connection = storage.GetConnection();
        using (connection.AcquireDistributedLock(GetLockResource(chainId), LockTimeout))
        {
            ChainRecord record = ChainRecord.Read(connection, chainId)
                ?? throw new InvalidOperationException($"Chain '{chainId}' isn't recorded.");

            record = FinishPendingCleanup(client, connection, record);

            string rootId = record.Jobs[Key(0, 0)];
            StateData? root = connection.GetStateData(rootId);
            if (root == null || !HasNotStarted(root.Name) || IsAwaiting(root.Name) ||
                record.Jobs.Where(x => x.Key != Key(0, 0)).Any(x => !IsAwaiting(connection.GetStateData(x.Value)?.Name)))
            {
                throw new InvalidOperationException($"Chain '{chainId}' has started, so it can't be replaced.");
            }

            // A first step scheduled only until an earlier change of the chain is over (JobChainStepFilter) is an
            // enqueued one
            bool scheduled = ScheduledState.StateName.Equals(root.Name, StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(root.Reason, JobChainStepFilter.WaitingReason(chainId), StringComparison.Ordinal);

            IState rootState = scheduled && root.Data.TryGetValue("EnqueueAt", out string? enqueueAt)
                ? new ScheduledState(JobHelper.DeserializeDateTime(enqueueAt))
                : new EnqueuedState();

            // The new first step keeps the old one's schedule options, and its queue unless the new chain gives one
            Dictionary<string, object> rootParameters = ReadRootParameters(connection, rootId);
            string? rootQueue = chain.Levels[0].Steps[0].Job.Queue == null ? ReadQueue(connection, rootId) : null;

            // From here no step of the chain starts until the replacement is recorded or given up. The old first
            // step keeps its state and its time: should this process stop before the new record is written, the
            // lease lapses, the record still lists the old steps, and the old chain runs when it was to run.
            ChainLease lease = ChainLease.Take(storage, connection, record);

            // The old steps, the deepest first and the first step last, so deleting one never starts another. The
            // new record stops listing them, so they can't run, and says they are to delete, so a replacement
            // interrupted once it is written is finished later.
            ChainCleanup cleanup = new()
            {
                Reason = ChainReplacedReason,
                Delete = [.. record.Cleanup?.Delete ?? [], .. DeepestFirst(record.Jobs)],
            };

            // Whatever fails gives the lease up: the old chain is kept as it was
            JobChainResult result = CreateJobs(chain, client, connection, lease, rootState, rootParameters, rootQueue, chainId, record.Generation + 1, record.CreatedAt, cleanup, rootId)
                ?? throw new InvalidOperationException($"The first step of the new chain '{chainId}' wasn't created: a client filter canceled it.");

            // The new chain is recorded: nothing fails the replacement from here on
            CompleteCleanup(storage, client, connection, chainId, cleanup, heldJustNow: false, out int deleted);

            Logger.Info($"Chain '{chainId}' replaced (generation {record.Generation + 1}): {result.Steps.Count} steps created, {deleted} old jobs deleted.");
            return result;
        }
    }

    private static Dictionary<string, object> ReadRootParameters(IStorageConnection connection, string rootId)
    {
        Dictionary<string, object> parameters = new(StringComparer.Ordinal);

        foreach (string name in (string[])[ScheduleOptions.PolicyParameter, ScheduleOptions.WhenBlockedParameter])
        {
            if (ReadParameter<string>(connection, rootId, name) is { } value)
            {
                parameters[name] = value;
            }
        }

        if (ReadParameter<long?>(connection, rootId, SkipIfLateAttribute.WindowParameter) is { } window)
        {
            parameters[SkipIfLateAttribute.WindowParameter] = window;
        }

        return parameters;
    }

    private static T? ReadParameter<T>(IStorageConnection connection, string jobId, string name)
    {
        string? value = connection.GetJobParameter(jobId, name);
        return string.IsNullOrEmpty(value) ? default : SerializationHelper.Deserialize<T>(value, SerializationOption.User);
    }

    private static string? ReadQueue(IStorageConnection connection, string jobId)
    {
        try
        {
            return connection.GetJobData(jobId)?.Job?.Queue;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The queue of background job '{jobId}' couldn't be read, so the replacing chain uses its own.", ex);
            return null;
        }
    }

    internal static int Delete(JobStorage storage, IBackgroundJobClient client, string chainId)
    {
        using IStorageConnection connection = storage.GetConnection();
        using (connection.AcquireDistributedLock(GetLockResource(chainId), LockTimeout))
        {
            ChainRecord? record = ChainRecord.Read(connection, chainId);
            if (record == null)
            {
                return -1;
            }

            bool restrict = record.DeleteBehavior == ChainDeleteBehavior.Restrict;
            if (restrict && record.Jobs.Values.Any(x => IsProcessing(connection.GetStateData(x)?.Name)))
            {
                throw new InvalidOperationException($"Chain '{chainId}' can't be deleted while a step is processing: it is Restrict.");
            }

            // The jobs a retry or a replacement left to delete go first: once the record is gone, nothing lists them
            int deleted = 0;
            if (record.Cleanup != null &&
                RunCleanup(client, connection, chainId, new ChainCleanup { Reason = ChainDeletedReason, Delete = record.Cleanup.Delete }, heldJustNow: false, out deleted) is { } left)
            {
                throw new InvalidOperationException(
                    $"Chain '{chainId}' can't be deleted yet: {left.Delete.Count} jobs that an earlier retry or replacement left to delete couldn't be deleted. {deleted} jobs were deleted; the chain is kept, delete it again.");
            }

            // A Restrict chain never loses a processing step, even one that starts meanwhile. A second pass deletes
            // the steps that moved on between their read and their deletion (a waiting step its parent enqueued).
            Func<string, bool> canDelete = restrict ? static state => !IsProcessing(state) : static _ => true;
            deleted += DeleteSteps(client, record, excludeRoot: false, ChainDeletedReason, canDelete, connection);

            List<string> live = LiveSteps(record, connection, out _);
            if (live.Count > 0)
            {
                deleted += DeleteSteps(client, record, excludeRoot: false, ChainDeletedReason, canDelete, connection);
                live = LiveSteps(record, connection, out bool processing);

                if (live.Count > 0)
                {
                    Logger.Warn($"Chain '{chainId}' (generation {record.Generation}) was deleted only in part: {deleted} jobs deleted, steps {string.Join(", ", live)} still run, so its record is kept.");
                    throw new InvalidOperationException(restrict && processing
                        ? $"Chain '{chainId}' can't be deleted while a step is processing: it is Restrict, and a step started while it was being deleted. {deleted} jobs were deleted; the chain is kept."
                        : $"Chain '{chainId}' was deleted only in part: {deleted} jobs were deleted, but steps {string.Join(", ", live)} started meanwhile. The chain is kept; delete it again.");
                }
            }

            using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
            {
                transaction.RemoveHash(GetKey(chainId));
                transaction.RemoveFromSet(SetKey, chainId);
                transaction.RemoveFromSet(DueSetKey, chainId);
                transaction.RemoveFromSet(ExpirySetKey, chainId);
                if (record.Cleanup != null)
                {
                    transaction.RemoveFromSet(CleanupSetKey, chainId);
                }

                transaction.Commit();
            }

            ForgetExpiry(chainId);

            Logger.Info($"Chain '{chainId}' (generation {record.Generation}) deleted: {deleted} jobs deleted.");
            return deleted;
        }
    }

    /// <summary>Creates the jobs of a chain and records it.</summary>
    internal static JobChainResult? Create(JobChain chain, IBackgroundJobClient client, IState rootState, ScheduleOptions? rootOptions, string? chainId)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (client is not IBackgroundJobClientV2 clientV2)
        {
            throw new NotSupportedException("A chain needs a client that implements IBackgroundJobClientV2, as BackgroundJobClient does.");
        }

        JobStorage storage = clientV2.Storage;
        if (rootOptions?.Queue != null && !storage.HasFeature(JobStorageFeatures.JobQueueProperty))
        {
            throw new NotSupportedException("The current storage can't keep the queue of a job. Use the QueueAttribute instead of ScheduleOptions.Queue.");
        }

        chainId ??= Guid.NewGuid().ToString("N");
        using IStorageConnection connection = storage.GetConnection();

        // The record comes before the first job, with nothing in it but the lease: every job of the chain finds
        // it, and waits while it lasts. It lists no step until the last one is created, so nothing of a chain
        // whose creation stops halfway ever runs.
        ChainLease lease = ChainLease.TakeForNewChain(storage, connection, chainId, (rootState as ScheduledState)?.EnqueueAt);

        return CreateJobs(
            chain,
            clientV2,
            connection,
            lease,
            rootState,
            rootOptions?.ToParameters() ?? new Dictionary<string, object>(StringComparer.Ordinal),
            rootOptions?.Queue,
            chainId,
            1,
            JobClock.UtcNow,
            cleanup: null,
            replacedRootId: null);
    }

    /// <summary>
    /// Creates a chain under an identifier chosen by the caller, once: when a chain is recorded under it already,
    /// that one is returned and nothing is created. For work that may be done again after it succeeded (a job that
    /// creates a chain and is run again because its process stopped before it finished).
    /// </summary>
    internal static JobChainResult? CreateOnce(JobChain chain, IBackgroundJobClient client, string chainId)
    {
        ArgumentNullException.ThrowIfNull(client);
        JobStorage storage = (client as IBackgroundJobClientV2)?.Storage
            ?? throw new NotSupportedException("A chain needs a client that implements IBackgroundJobClientV2, as BackgroundJobClient does.");

        using IStorageConnection connection = storage.GetConnection();
        using (connection.AcquireDistributedLock(GetLockResource(chainId), LockTimeout))
        {
            if (ChainRecord.Read(connection, chainId) is { } existing)
            {
                Logger.Info($"Chain '{chainId}' is recorded already (generation {existing.Generation}): it isn't created again.");
                return existing.ToResult();
            }

            return Create(chain, client, new EnqueuedState(), null, chainId);
        }
    }

    /// <summary>
    /// Creates the jobs of a chain under the lease of its record and writes the record that lists them, the point
    /// from which the chain exists. Whatever fails before deletes the jobs created and gives the lease up. Nothing
    /// fails after, except the last look at the first step, which takes the chain back when a server deleted that
    /// step while this process was frozen.
    /// </summary>
    /// <returns><c>null</c> when a client filter canceled the first step: nothing was created.</returns>
    private static JobChainResult? CreateJobs(
        JobChain chain,
        IBackgroundJobClientV2 client,
        IStorageConnection connection,
        ChainLease lease,
        IState rootState,
        Dictionary<string, object> rootParameters,
        string? rootQueue,
        string chainId,
        int generation,
        DateTime createdAt,
        ChainCleanup? cleanup,
        string? replacedRootId)
    {
        IReadOnlyList<ChainLevel> levels = chain.Levels;
        Dictionary<string, string> jobs = new(StringComparer.Ordinal);
        string change = replacedRootId != null ? "replaced" : "created";
        List<string> created = [];
        ChainRecord? record = null;
        StateData? root = null;
        string? rootId = null;
        bool recorded = false;
        bool lost = false;

        try
        {
            if (rootQueue != null && !client.Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
            {
                throw new NotSupportedException("The current storage can't keep the queue of a job. Use the QueueAttribute instead of ScheduleOptions.Queue.");
            }

            Job rootJob = rootQueue != null ? WithQueue(levels[0].Steps[0].Job, rootQueue) : levels[0].Steps[0].Job;
            string? first = rootId = CreateOrNull(client, rootJob, rootState, chainId, Key(0, 0), rootParameters);
            if (first == null)
            {
                lease.GiveUp(nothingLeft: true);
                return null;
            }

            jobs[Key(0, 0)] = first;
            created.Add(first);
            record = new ChainRecord(chainId, ChainDefinition.From(levels, chain.DeleteBehavior), jobs, generation, createdAt) { Cleanup = cleanup };

            CreateLevels(client, levels.Count, level => levels[level], jobs, chainId, 1, static _ => true, created, lease, () => EnsureNoneRefused(connection, chainId, created, change));
            Stopwatch locked;

            // The decision is taken under the lock of the first step, the only step that can be due before the
            // chain is recorded, and of the first step it replaces: a worker that looks at one of them either did
            // before, and what it decided is read here, or does after, and reads the record written here.
            using (replacedRootId != null ? connection.AcquireDistributedJobLock(replacedRootId, CommitLockTimeout) : null)
            using (connection.AcquireDistributedJobLock(first, CommitLockTimeout))
            {
                locked = Stopwatch.StartNew();

                if (replacedRootId != null)
                {
                    string? replacedState = connection.GetStateData(replacedRootId)?.Name;
                    if (replacedState == null || !HasNotStarted(replacedState) || IsAwaiting(replacedState))
                    {
                        throw new InvalidOperationException($"Chain '{chainId}' has started, so it can't be replaced.");
                    }
                }

                root = connection.GetStateData(first);
                if (IsRefused(chainId, root))
                {
                    throw Abandoned(chainId, first, change);
                }

                // A lease that lapsed is renewed, and what a server may have done to the other jobs meanwhile is
                // looked at: one of them deleted for not being listed would leave a chain that can't finish
                if (lease.HasLapsed())
                {
                    lease.Renew();
                    EnsureNoneRefused(connection, chainId, created.Skip(1), change);
                }

                WriteCommitted(connection, client.Storage, record, (rootState as ScheduledState)?.EnqueueAt, created);
                recorded = true;
            }

            // A process frozen while it wrote the record may have lost its lock and its lease, and a server may
            // have deleted the first step for a chain it took for abandoned: it is read once more
            lost = IsRefused(chainId, ReadAgain(connection, first, relock: locked.Elapsed > SafeLockTime));
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType() && !recorded)
        {
            // Not a chain without its later steps or its record: delete what was created, the last steps first so
            // nothing starts. The record lists none of them, so a step a worker took meanwhile doesn't run: it
            // waits while the lease lasts, and is deleted afterwards. A job that can't be deleted keeps the record
            // started for the chain, without any step, so it is deleted when it comes due.
            lease.GiveUp(DeleteCreated(client, connection, created, chainId, ChainNotCreatedReason));
            throw;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Only the release of a job's lock, or the last read of the first step, can fail once the record is
            // written: the chain exists
            Logger.WarnException($"Chain '{chainId}' was recorded, but its first step couldn't be read again afterwards.", ex);
        }

        if (lost)
        {
            // The record goes first, so none of the steps is listed while they are deleted
            lease.GiveUp(nothingLeft: false);
            if (DeleteCreated(client, connection, created, chainId, ChainNotCreatedReason) && replacedRootId == null)
            {
                lease.GiveUp(nothingLeft: true);
            }

            throw Abandoned(chainId, rootId!, change); // Created, or the chain wasn't recorded
        }

        SettleFirstStep(client, connection, chainId, rootId!, root); // Created, or the chain wasn't recorded
        return record!.ToResult(); // Built before the chain is recorded
    }

    private static StateData? ReadAgain(IStorageConnection connection, string jobId, bool relock)
    {
        using (relock ? connection.AcquireDistributedJobLock(jobId, CommitLockTimeout) : null)
        {
            return connection.GetStateData(jobId);
        }
    }

    /// <summary>
    /// Writes the record that makes a chain, or a change of it, exist. A write that throws may have gone through
    /// (a lost reply): the record is read again, and when it is the one written, the write counts as done, so the
    /// steps of a recorded chain aren't deleted as if it had failed.
    /// </summary>
    private static void WriteCommitted(IStorageConnection connection, JobStorage storage, ChainRecord record, DateTime? latestStart, IReadOnlyCollection<string> recorded)
    {
        try
        {
            record.Write(storage, connection, latestStart, recorded);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType() && IsWritten(connection, record))
        {
            Logger.WarnException($"Writing the record of chain '{record.ChainId}' (generation {record.Generation}) threw, but the record is written: the chain is taken as recorded.", ex);
        }
    }

    private static bool IsWritten(IStorageConnection connection, ChainRecord record)
    {
        try
        {
            Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(record.ChainId));
            if (hash == null || !hash.ContainsKey(DefinitionField) || ReadGeneration(hash) != record.Generation)
            {
                return false;
            }

            Dictionary<string, string> jobs = ParseJobs(hash.GetValueOrDefault(JobsField), out ChainLeaseTime? lease);
            return lease == null && jobs.Count == record.Jobs.Count && record.Jobs.All(x => jobs.GetValueOrDefault(x.Key) == x.Value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return false;
        }
    }

    /// <summary>
    /// Once the chain is recorded, with the state its first step was in then: a first step that a worker took
    /// before, and held until the record is written, is enqueued at once rather than when its hold ends; and the
    /// record of a chain whose first step is scheduled later than asked (a schedule policy moved it) is kept that
    /// much longer. Best effort: the hold ends by itself.
    /// </summary>
    private static void SettleFirstStep(IBackgroundJobClientV2 client, IStorageConnection connection, string chainId, string rootId, StateData? root)
    {
        try
        {
            if (root == null || !ScheduledState.StateName.Equals(root.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(root.Reason, JobChainStepFilter.WaitingReason(chainId), StringComparison.Ordinal))
            {
                client.ChangeState(rootId, new EnqueuedState { Reason = JobChainStepFilter.RecordedReason }, ScheduledState.StateName);
            }
            else if (root.Data != null && root.Data.TryGetValue("EnqueueAt", out string? enqueueAt) &&
                     JobHelper.DeserializeNullableDateTime(enqueueAt) is { } latestStart &&
                     MayNeedExtending(chainId, latestStart))
            {
                ExtendRecord(client.Storage, connection, chainId, latestStart);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The first step '{rootId}' of chain '{chainId}' couldn't be enqueued after the chain was recorded; it runs when its hold ends.", ex);
        }
    }

    private static Job WithQueue(Job job, string queue) => new(job.Type, job.Method, job.Args, queue);

    private static string Create(IBackgroundJobClientV2 client, Job job, IState state, string chainId, string key, Dictionary<string, object>? parameters)
    {
        return CreateOrNull(client, job, state, chainId, key, parameters)
            ?? throw new InvalidOperationException($"Step '{key}' of chain '{chainId}' wasn't created: a client filter canceled it.");
    }

    private static string? CreateOrNull(IBackgroundJobClientV2 client, Job job, IState state, string chainId, string key, Dictionary<string, object>? parameters)
    {
        parameters ??= new Dictionary<string, object>(StringComparer.Ordinal);
        parameters[ChainIdParameter] = chainId;
        parameters[ChainStepParameter] = key;
        return client.Create(job, state, parameters);
    }

    // The jobs of the steps of a level, in order: the ones the map lists for it
    private static List<string> ParentJobs(Dictionary<string, string> jobs, int parentLevel)
    {
        List<string> parents = [];
        for (int i = 0; jobs.TryGetValue(Key(parentLevel, i), out string? jobId); i++)
        {
            parents.Add(jobId);
        }

        return parents;
    }

    // A step after a single step is an ordinary continuation; after a group, a fan-in continuation of the group
    internal static IState StateFor(ChainStep step, IReadOnlyList<string> parents)
    {
        if (parents.Count == 1)
        {
            return new AwaitingState(parents[0], new EnqueuedState(), OptionsAfterOneStep(step.Link, step.Options));
        }

        (FanInMode mode, JobContinuationOptions fanInOptions) = ConditionAfterGroup(step.Link, step.Options, nameof(step));
        return new AwaitingJobsState(parents, new EnqueuedState(), fanInOptions, mode);
    }

    /// <summary>
    /// What the states of the steps a step waits for decide for it, as the filters that start continuations and
    /// fan-in continuations decide it: it starts, it is skipped, or it keeps waiting. Undecided for a step whose
    /// way of waiting isn't known (an info built without the chain's definition).
    /// </summary>
    internal static FanInContinuationsSupportAttribute.Decision Decide(JobChainStepInfo step, IReadOnlyList<JobChainStepInfo> parents)
    {
        if (step.Link == ChainLink.Root || parents.Count == 0)
        {
            return FanInContinuationsSupportAttribute.Decision.Undecided;
        }

        HashSet<string> finalStates = ContinuationsSupportAttribute.KnownFinalStates;

        if (parents.Count == 1)
        {
            return ContinuationsSupportAttribute.Decide(parents[0].State, OptionsAfterOneStep(step.Link, step.Options), finalStates);
        }

        // A group can't be waited for to start, and no chain is created that way
        if (step.Link == ChainLink.Then && step.Options.HasFlag(JobContinuationOptions.OnlyOnProcessingState))
        {
            return FanInContinuationsSupportAttribute.Decision.Undecided;
        }

        Dictionary<string, string?> states = new(StringComparer.Ordinal);
        foreach (JobChainStepInfo parent in parents)
        {
            states[parent.JobId] = parent.State;
        }

        (FanInMode mode, JobContinuationOptions fanInOptions) = ConditionAfterGroup(step.Link, step.Options, nameof(step));
        return FanInContinuationsSupportAttribute.Evaluate(mode, fanInOptions, [.. states.Keys], id => states[id], finalStates);
    }

    /// <summary>The continuation options of a step that waits for a single step.</summary>
    private static JobContinuationOptions OptionsAfterOneStep(ChainLink link, JobContinuationOptions options)
    {
        const JobContinuationOptions failed = JobContinuationOptions.OnlyOnFailedState;
        const JobContinuationOptions deleted = JobContinuationOptions.OnlyOnDeletedState;
        const JobContinuationOptions succeeded = JobContinuationOptions.OnlyOnSucceededState;

        return link switch
        {
            ChainLink.AfterAnyFailed => failed,
            ChainLink.AfterAnyFailedOrCanceled => failed | deleted,
            ChainLink.AfterAllCompleted => succeeded | deleted | failed,
            ChainLink.AfterAll or ChainLink.AfterAny => succeeded,
            _ => options,
        };
    }

    /// <summary>The fan-in mode and options of a step that waits for a group of several steps.</summary>
    private static (FanInMode Mode, JobContinuationOptions Options) ConditionAfterGroup(ChainLink link, JobContinuationOptions options, string parameterName)
    {
        const JobContinuationOptions succeeded = JobContinuationOptions.OnlyOnSucceededState;

        return link switch
        {
            ChainLink.AfterAny => (FanInMode.Any, succeeded),
            ChainLink.AfterAnyFailed => (FanInMode.AnyFailed, succeeded),
            ChainLink.AfterAnyFailedOrCanceled => (FanInMode.AnyFailedOrCanceled, succeeded),
            ChainLink.AfterAllCompleted => (FanInMode.AllCompleted, succeeded),
            ChainLink.AfterAll => (FanInMode.All, succeeded),
            _ => FanInFor(options, parameterName),
        };
    }

    /// <summary>
    /// How a <c>Then</c> or a branch waits for a level of several steps, as close as a fan-in gets to its options
    /// for a single step: <c>OnlyOnFailedState</c> starts it once a step failed for good, with
    /// <c>OnlyOnDeletedState</c> also once one was deleted; failure with success, or any finished state, once every
    /// step finished whatever the outcome; otherwise once every step finished in a state the options accept.
    /// </summary>
    /// <exception cref="ArgumentException">The options include <c>OnlyOnProcessingState</c>, which a fan-in can't wait for.</exception>
    internal static (FanInMode Mode, JobContinuationOptions Options) FanInFor(JobContinuationOptions options, string parameterName)
    {
        const JobContinuationOptions succeeded = JobContinuationOptions.OnlyOnSucceededState;

        if (options.HasFlag(JobContinuationOptions.OnlyOnProcessingState))
        {
            throw new ArgumentException("A step after a group of several steps can't wait for one of them to start: OnlyOnProcessingState needs a single step before it.", parameterName);
        }

        bool onFailure = options.HasFlag(JobContinuationOptions.OnlyOnFailedState);
        bool onSuccess = options.HasFlag(succeeded);

        if (options == JobContinuationOptions.OnAnyFinishedState || (onFailure && onSuccess))
        {
            return (FanInMode.AllCompleted, succeeded);
        }

        if (onFailure)
        {
            return options.HasFlag(JobContinuationOptions.OnlyOnDeletedState) ? (FanInMode.AnyFailedOrCanceled, succeeded) : (FanInMode.AnyFailed, succeeded);
        }

        return (FanInMode.All, options);
    }

    private static string? ReadChainId(IStorageConnection connection, string jobId)
    {
        string? value = connection.GetJobParameter(jobId, ChainIdParameter);
        return string.IsNullOrEmpty(value) ? null : SerializationHelper.Deserialize<string>(value, SerializationOption.User);
    }

    private static string GetLockResource(string chainId) => $"lock:{KeyPrefix}{chainId}";

    internal static string Key(int level, int index) => string.Create(CultureInfo.InvariantCulture, $"{level}.{index}");

    private static (int Level, int Index) ParseKey(string key)
    {
        int dot = key.IndexOf('.', StringComparison.Ordinal);
        return (int.Parse(key.AsSpan(0, dot), CultureInfo.InvariantCulture), int.Parse(key.AsSpan(dot + 1), CultureInfo.InvariantCulture));
    }

    /// <summary>The definition of a chain as JSON, to store it (recurring chains, the chain record).</summary>
    internal static string ToDefinition(JobChain chain)
    {
        return SerializationHelper.Serialize(ChainDefinition.From(chain.Levels, chain.DeleteBehavior), SerializationOption.Internal);
    }

    /// <summary>Reads a chain written by <see cref="ToDefinition"/>.</summary>
    internal static JobChain FromDefinition(string definition)
    {
        ChainDefinition dto = SerializationHelper.Deserialize<ChainDefinition>(definition, SerializationOption.Internal)
            ?? throw new FormatException("The chain definition is empty.");
        return JobChain.FromLevels(dto.ToLevels(), dto.DeleteBehavior);
    }
}
