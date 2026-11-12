using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>Reads, reruns, replaces and deletes stored <see cref="JobChain"/>s.</summary>
public static partial class JobChains
{
    /// <summary>The job parameter with a step's chain.</summary>
    public const string ChainIdParameter = "ChainId";

    /// <summary>The job parameter with a step's key, <c>level.index</c>.</summary>
    public const string ChainStepParameter = "ChainStep";

    /// <summary>The set that lists the chains, scored by creation time.</summary>
    public const string SetKey = "chains";

    private const string KeyPrefix = "chain:";
    private const string ChainDeletedReason = "The chain was deleted";
    private const string ChainReplacedReason = "The chain was replaced";
    private const string ChainRetriedReason = "Chain retried from this step";
    private const string ChainRetryFailedReason = "The retry of the chain failed";
    private const string ChainNotCreatedReason = "The chain couldn't be created";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);
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
        /// <summary>Reads a chain with the current state of each step, or <c>null</c> when it isn't recorded (any more).</summary>
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
        /// nothing changes: the jobs created are deleted and the step goes back to its state.
        /// </summary>
        /// <param name="stepJobId">The current job of the step to run again.</param>
        /// <returns>The chain's jobs after the retry.</returns>
        /// <exception cref="InvalidOperationException">
        /// The job isn't a current step of a recorded chain, a step of the chain hasn't finished yet, the step changed
        /// meanwhile, or a job the new steps wait for expired.
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
        /// deleted. When the new chain can't be created, the old one is kept as it was.
        /// </summary>
        /// <returns>The new chain's jobs.</returns>
        /// <exception cref="InvalidOperationException">The chain isn't recorded, or it started.</exception>
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
                ?? throw new InvalidOperationException($"Chain '{chainId}' isn't recorded any more: its record is kept for {JobChain.RecordRetention.TotalDays} days.");

            string stepKey = record.Jobs.FirstOrDefault(x => x.Value == stepJobId).Key
                ?? throw new InvalidOperationException($"Background job '{stepJobId}' isn't a current step of chain '{chainId}'; a retry replaced it.");

            if (!record.ToInfo(connection).IsSettled)
            {
                throw new InvalidOperationException($"Chain '{chainId}' hasn't finished: a step is still waiting or running.");
            }

            (int level, int index) = ParseKey(stepKey);
            HashSet<int> retried = [level];
            for (int next = level + 1; next < record.Levels.Count; next++)
            {
                if (retried.Contains(record.Levels[next].Parent)) retried.Add(next);
            }

            // The step is held first: the new steps wait for it, and nothing after it starts or is skipped before the
            // retry is complete
            StateData? stepState = connection.GetStateData(stepJobId);
            string heldId = Hold(client, record, stepKey, stepJobId, stepState);

            Dictionary<string, string> jobs = new(record.Jobs, StringComparer.Ordinal) { [stepKey] = heldId };
            List<string> created = [];
            ChainRecord updated;

            try
            {
                CreateLevels(client, record.Levels, jobs, chainId, level + 1, retried.Contains, created);

                updated = record with { Jobs = jobs, Generation = record.Generation + 1 };
                updated.Write(storage, connection);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                DeleteCreated(client, connection, created, chainId, ChainRetryFailedReason);
                if (stepState != null)
                {
                    Release(client, stepJobId, Restore(stepState), chainId);
                }
                else
                {
                    DeleteCreated(client, connection, [heldId], chainId, ChainRetryFailedReason);
                }

                throw;
            }

            // The old steps after it wait for it too, and would run beside the new ones: deleted before it is
            // released, the deepest first. Should this fail, the step runs when its hold ends.
            ChainRecord waiting = record with { Jobs = record.Jobs.Where(x => ParseKey(x.Key).Level != level && retried.Contains(ParseKey(x.Key).Level)).ToDictionary() };
            int replaced = DeleteSteps(client, waiting, excludeRoot: false, "Replaced by a retry of the chain", IsAwaiting, connection);

            Release(client, heldId, new EnqueuedState { Reason = ChainRetriedReason }, chainId);

            Logger.Info($"Chain '{chainId}' retried from step '{stepKey}' (generation {updated.Generation}): {created.Count} steps created after it, {replaced} waiting steps deleted.");
            return updated.ToResult();
        }
    }

    // Holds the step to retry, or creates it again, held, when its job expired; returns the held job
    private static string Hold(IBackgroundJobClientV2 client, ChainRecord record, string stepKey, string stepJobId, StateData? stepState)
    {
        string chainId = record.ChainId;

        if (stepState != null)
        {
            if (!client.ChangeState(stepJobId, Hold(ChainRetriedReason), stepState.Name))
            {
                throw new InvalidOperationException($"Background job '{stepJobId}' of chain '{chainId}' changed meanwhile; try again.");
            }

            return stepJobId;
        }

        (int level, int index) = ParseKey(stepKey);
        string recreated = Create(client, record.Levels[level].Steps[index].Job, Hold(ChainRetriedReason), chainId, stepKey, null);
        Logger.Warn($"Step '{stepKey}' of chain '{chainId}' was created again as background job '{recreated}' to retry it: its job '{stepJobId}' expired.");
        return recreated;
    }

    internal static JobChainResult Replace(JobStorage storage, IBackgroundJobClientV2 client, string chainId, JobChain chain)
    {
        using IStorageConnection connection = storage.GetConnection();
        using (connection.AcquireDistributedLock(GetLockResource(chainId), LockTimeout))
        {
            ChainRecord record = ChainRecord.Read(connection, chainId)
                ?? throw new InvalidOperationException($"Chain '{chainId}' isn't recorded.");

            string rootId = record.Jobs[Key(0, 0)];
            StateData? root = connection.GetStateData(rootId);
            bool scheduled = ScheduledState.StateName.Equals(root?.Name, StringComparison.OrdinalIgnoreCase);
            if (root == null || (!scheduled && !EnqueuedState.StateName.Equals(root.Name, StringComparison.OrdinalIgnoreCase)) ||
                record.Jobs.Where(x => x.Key != Key(0, 0)).Any(x => !AwaitingState.StateName.Equals(connection.GetStateData(x.Value)?.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Chain '{chainId}' has started, so it can't be replaced.");
            }

            IState rootState = scheduled && root.Data.TryGetValue("EnqueueAt", out string? enqueueAt)
                ? new ScheduledState(JobHelper.DeserializeDateTime(enqueueAt))
                : new EnqueuedState();

            // The new first step keeps the old one's schedule options, and its queue unless the new chain gives one
            Dictionary<string, object> rootParameters = ReadRootParameters(connection, rootId);
            string? rootQueue = chain.Levels[0].Steps[0].Job.Queue == null ? ReadQueue(connection, rootId) : null;

            // The old first step is held, from the state read: if a worker took it meanwhile, nothing changes. Held,
            // it neither runs nor decides its continuations while the new chain is created.
            if (!client.ChangeState(rootId, Hold("The chain is being replaced"), root.Name))
            {
                throw new InvalidOperationException($"Chain '{chainId}' has started, so it can't be replaced.");
            }

            JobChainResult? result;
            try
            {
                result = Create(chain, client, rootState, rootParameters, rootQueue, chainId, record.Generation + 1, record.CreatedAt);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Release(client, rootId, Restore(root), chainId);
                throw;
            }

            if (result == null)
            {
                Release(client, rootId, Restore(root), chainId);
                throw new InvalidOperationException($"The first step of the new chain '{chainId}' wasn't created: a client filter canceled it.");
            }

            // The old steps, the deepest first and the first step last, so deleting one never starts another
            int deleted = DeleteSteps(client, record, excludeRoot: true, ChainReplacedReason, IsAwaiting, connection);
            if (client.ChangeState(rootId, new DeletedState { Reason = ChainReplacedReason }, ScheduledState.StateName)) deleted++;

            Logger.Info($"Chain '{chainId}' replaced (generation {record.Generation + 1}): {result.Steps.Count} steps created, {deleted} old jobs deleted.");
            return result;
        }
    }

    private static Dictionary<string, object> ReadRootParameters(IStorageConnection connection, string rootId)
    {
        Dictionary<string, object> parameters = new(StringComparer.Ordinal);

        foreach (string name in (string[])[ScheduleOptions.PolicyParameter, ScheduleOptions.WhenBlockedParameter])
        {
            if (ReadParameter<string>(connection, rootId, name) is { } value) parameters[name] = value;
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
            if (record == null) return -1;

            bool restrict = record.DeleteBehavior == ChainDeleteBehavior.Restrict;
            if (restrict && record.Jobs.Values.Any(x => IsProcessing(connection.GetStateData(x)?.Name)))
            {
                throw new InvalidOperationException($"Chain '{chainId}' can't be deleted while a step is processing: it is Restrict.");
            }

            // A Restrict chain never loses a processing step, even one that starts meanwhile. A second pass deletes
            // the steps that moved on between their read and their deletion (a waiting step its parent enqueued).
            Func<string, bool> canDelete = restrict ? static state => !IsProcessing(state) : static _ => true;
            int deleted = DeleteSteps(client, record, excludeRoot: false, ChainDeletedReason, canDelete, connection);

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
                transaction.Commit();
            }

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

        return Create(
            chain,
            clientV2,
            rootState,
            rootOptions?.ToParameters() ?? new Dictionary<string, object>(StringComparer.Ordinal),
            rootOptions?.Queue,
            chainId ?? Guid.NewGuid().ToString("N"),
            1,
            JobClock.UtcNow);
    }

    private static JobChainResult? Create(
        JobChain chain,
        IBackgroundJobClientV2 client,
        IState rootState,
        Dictionary<string, object> rootParameters,
        string? rootQueue,
        string chainId,
        int generation,
        DateTime createdAt)
    {
        IReadOnlyList<ChainLevel> levels = chain.Levels;
        Dictionary<string, string> jobs = new(StringComparer.Ordinal);

        if (rootQueue != null && !client.Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
        {
            throw new NotSupportedException("The current storage can't keep the queue of a job. Use the QueueAttribute instead of ScheduleOptions.Queue.");
        }

        Job rootJob = rootQueue != null ? WithQueue(levels[0].Steps[0].Job, rootQueue) : levels[0].Steps[0].Job;
        string? rootId = CreateOrNull(client, rootJob, rootState, chainId, Key(0, 0), rootParameters);
        if (rootId == null) return null;

        jobs[Key(0, 0)] = rootId;
        List<string> created = [rootId];
        ChainRecord record = new(chainId, levels, jobs, generation, createdAt, chain.DeleteBehavior);

        using IStorageConnection connection = client.Storage.GetConnection();
        try
        {
            CreateLevels(client, levels, jobs, chainId, 1, static _ => true, created);
            record.Write(client.Storage, connection);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Not a chain without its later steps or its record: delete what was created, the last steps first so
            // nothing starts, and the first step only if no worker took it meanwhile
            DeleteCreated(client, connection, created, chainId, ChainNotCreatedReason);
            throw;
        }

        return record.ToResult();
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

    private static List<string> ParentJobs(IReadOnlyList<ChainLevel> levels, Dictionary<string, string> jobs, int parentLevel)
    {
        List<string> parents = [];
        for (int i = 0; i < levels[parentLevel].Steps.Count; i++)
        {
            parents.Add(jobs[Key(parentLevel, i)]);
        }

        return parents;
    }

    // A step after a single step is an ordinary continuation; after a group, a fan-in continuation of the group
    internal static IState StateFor(ChainStep step, IReadOnlyList<string> parents)
    {
        const JobContinuationOptions failed = JobContinuationOptions.OnlyOnFailedState;
        const JobContinuationOptions deleted = JobContinuationOptions.OnlyOnDeletedState;
        const JobContinuationOptions succeeded = JobContinuationOptions.OnlyOnSucceededState;

        if (parents.Count == 1)
        {
            JobContinuationOptions options = step.Link switch
            {
                ChainLink.AfterAnyFailed => failed,
                ChainLink.AfterAnyFailedOrCanceled => failed | deleted,
                ChainLink.AfterAllCompleted => succeeded | deleted | failed,
                ChainLink.AfterAll or ChainLink.AfterAny => succeeded,
                _ => step.Options,
            };

            return new AwaitingState(parents[0], new EnqueuedState(), options);
        }

        (FanInMode mode, JobContinuationOptions fanInOptions) = step.Link switch
        {
            ChainLink.AfterAny => (FanInMode.Any, succeeded),
            ChainLink.AfterAnyFailed => (FanInMode.AnyFailed, succeeded),
            ChainLink.AfterAnyFailedOrCanceled => (FanInMode.AnyFailedOrCanceled, succeeded),
            ChainLink.AfterAllCompleted => (FanInMode.AllCompleted, succeeded),
            ChainLink.AfterAll => (FanInMode.All, succeeded),
            _ => FanInFor(step.Options, nameof(step)),
        };

        return new AwaitingJobsState(parents, new EnqueuedState(), fanInOptions, mode);
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

        if (options == JobContinuationOptions.OnAnyFinishedState || (onFailure && onSuccess)) return (FanInMode.AllCompleted, succeeded);
        if (onFailure) return options.HasFlag(JobContinuationOptions.OnlyOnDeletedState) ? (FanInMode.AnyFailedOrCanceled, succeeded) : (FanInMode.AnyFailed, succeeded);
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
