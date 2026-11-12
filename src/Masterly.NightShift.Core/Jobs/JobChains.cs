using System.Globalization;
using System.Text.Json.Serialization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>A step of a stored chain and the job that runs it now.</summary>
/// <param name="Key">The step key, <c>level.index</c>.</param>
/// <param name="JobId">The step's current job; another one after <c>RetryChainFrom</c>.</param>
/// <param name="State">The job's state name, or <c>null</c> when the job expired.</param>
/// <param name="Level">The level of the step.</param>
/// <param name="ParentLevel">The level it waits for, -1 for the first step.</param>
/// <param name="Job">The step's method call, as the chain defines it.</param>
public sealed record JobChainStepInfo(string Key, string JobId, string? State, int Level, int ParentLevel, Job? Job = null)
{
    /// <summary>How the step waits for its level's parent, for the dashboard's flowchart.</summary>
    internal ChainLink Link { get; init; }

    /// <summary>The continuation options of a <c>Then</c> or branch step.</summary>
    internal JobContinuationOptions Options { get; init; }

    /// <summary>The kind of the step's level: the first step, a <c>Then</c>, a group of branches or of joins.</summary>
    internal ChainLevelKind LevelKind { get; init; }
}

/// <summary>A stored chain, as <c>GetChain</c> reads it.</summary>
/// <param name="ChainId">The chain's identifier.</param>
/// <param name="Generation">1, raised by each <c>RetryChainFrom</c> and <c>ReplaceChain</c>.</param>
/// <param name="CreatedAt">When the chain was created, UTC.</param>
/// <param name="DeleteBehavior">What <c>DeleteChain</c> does while a step is processing.</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record JobChainInfo(string ChainId, int Generation, DateTime CreatedAt, ChainDeleteBehavior DeleteBehavior, IReadOnlyList<JobChainStepInfo> Steps)
{
    /// <summary>
    /// Whether the chain has nothing left to do by itself: every step finished (succeeded, deleted, skipped steps
    /// included, failed for good, or expired), or waits after a step that failed for good, which only a requeue or
    /// <c>RetryChainFrom</c> moves on.
    /// </summary>
    public bool IsSettled => Steps.All(IsSettledStep);

    private bool IsSettledStep(JobChainStepInfo step)
    {
        if (JobChains.IsSettledState(step.State)) return true;

        return AwaitingState.StateName.Equals(step.State, StringComparison.OrdinalIgnoreCase) &&
               Steps.Any(x => x.Level == step.ParentLevel && FailedState.StateName.Equals(x.State, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The number of steps that failed for good.</summary>
    public int FailedCount => Steps.Count(static x => FailedState.StateName.Equals(x.State, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads, reruns, replaces and deletes stored <see cref="JobChain"/>s.</summary>
public static class JobChains
{
    /// <summary>The job parameter with a step's chain.</summary>
    public const string ChainIdParameter = "ChainId";

    /// <summary>The job parameter with a step's key, <c>level.index</c>.</summary>
    public const string ChainStepParameter = "ChainStep";

    /// <summary>The set that lists the chains, scored by creation time.</summary>
    public const string SetKey = "chains";

    private const string KeyPrefix = "chain:";
    private const string DefinitionField = "Definition";
    private const string JobsField = "Jobs";
    private const string GenerationField = "Generation";
    private const string CreatedAtField = "CreatedAt";
    private const string DeleteBehaviorField = "DeleteBehavior";
    private const string ChainDeletedReason = "The chain was deleted";
    private const string ChainReplacedReason = "The chain was replaced";

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
        /// it, and the branches beside it, keep their outcome and aren't run again.
        /// </summary>
        /// <param name="stepJobId">The current job of the step to run again.</param>
        /// <returns>The chain's jobs after the retry.</returns>
        /// <exception cref="InvalidOperationException">
        /// The job isn't a current step of a recorded chain, a step of the chain hasn't finished yet, or a job the new
        /// steps wait for expired.
        /// </exception>
        [NotNull]
        public JobChainResult RetryChainFrom([NotNull] string stepJobId)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepJobId);

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

                IBackgroundJobClientV2 client = new BackgroundJobClient(storage);
                (int level, int index) = ParseKey(stepKey);
                Dictionary<string, string> jobs = new(record.Jobs, StringComparer.Ordinal);

                HashSet<int> retried = [level];
                for (int next = level + 1; next < record.Levels.Count; next++)
                {
                    if (retried.Contains(record.Levels[next].Parent)) retried.Add(next);
                }

                // The steps after it that still wait (after a failure) would run when the step succeeds, beside the
                // new ones: delete them first, the deepest first
                ChainRecord waiting = record with { Jobs = record.Jobs.Where(x => ParseKey(x.Key).Level != level && retried.Contains(ParseKey(x.Key).Level)).ToDictionary() };
                DeleteSteps(client, waiting, excludeRoot: false, "Replaced by a retry of the chain", [AwaitingState.StateName], connection);

                // The step first, so the new steps after it wait for it
                if (!client.ChangeState(stepJobId, new EnqueuedState { Reason = "Chain retried from this step" }, null))
                {
                    ChainStep step = record.Levels[level].Steps[index];
                    jobs[stepKey] = Create(client, step.Job, new EnqueuedState { Reason = "Chain retried from this step" }, chainId, stepKey, null);
                }

                for (int next = level + 1; next < record.Levels.Count; next++)
                {
                    ChainLevel chainLevel = record.Levels[next];
                    if (!retried.Contains(next)) continue;

                    List<string> parents = ParentJobs(record.Levels, jobs, chainLevel.Parent);

                    for (int i = 0; i < chainLevel.Steps.Count; i++)
                    {
                        // A parent that expired makes the creation throw InvalidOperationException
                        string key = Key(next, i);
                        jobs[key] = Create(client, chainLevel.Steps[i].Job, StateFor(chainLevel.Steps[i], parents), chainId, key, null);
                    }
                }

                record = record with { Jobs = jobs, Generation = record.Generation + 1 };
                record.Write(storage, connection);
                return record.ToResult();
            }
        }

        /// <summary>
        /// Replaces a chain that hasn't started: its first step is still enqueued or scheduled and the others are
        /// waiting. The old steps are deleted, and the new chain is created under the same identifier, enqueued or
        /// scheduled for the same moment as the old first step.
        /// </summary>
        /// <returns>The new chain's jobs.</returns>
        /// <exception cref="InvalidOperationException">The chain isn't recorded, or it started.</exception>
        [NotNull]
        public JobChainResult ReplaceChain([NotNull] string chainId, [NotNull] JobChain chain)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentException.ThrowIfNullOrWhiteSpace(chainId);
            ArgumentNullException.ThrowIfNull(chain);

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

                IBackgroundJobClientV2 client = new BackgroundJobClient(storage);

                // The first step first, and only from the state read: if a worker took it meanwhile, nothing changes
                if (!client.ChangeState(rootId, new DeletedState { Reason = ChainReplacedReason }, root.Name))
                {
                    throw new InvalidOperationException($"Chain '{chainId}' has started, so it can't be replaced.");
                }

                DeleteSteps(client, record, excludeRoot: true, ChainReplacedReason, [AwaitingState.StateName], connection);

                IState rootState = scheduled && root.Data.TryGetValue("EnqueueAt", out string? enqueueAt)
                    ? new ScheduledState(JobHelper.DeserializeDateTime(enqueueAt))
                    : new EnqueuedState();

                return Create(chain, client, rootState, null, chainId, record.Generation + 1, record.CreatedAt)
                    ?? throw new InvalidOperationException($"The first step of the new chain '{chainId}' wasn't created: a client filter canceled it.");
            }
        }

        /// <summary>
        /// Deletes the steps of a chain that haven't finished (waiting, scheduled, enqueued, failed and, unless the chain
        /// is <see cref="ChainDeleteBehavior.Restrict"/>, processing) and its record. Returns the number of jobs deleted,
        /// or -1 when the chain isn't recorded.
        /// </summary>
        /// <exception cref="InvalidOperationException">The chain is <see cref="ChainDeleteBehavior.Restrict"/> and a step is processing.</exception>
        public int DeleteChain([NotNull] string chainId)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentException.ThrowIfNullOrWhiteSpace(chainId);

            using IStorageConnection connection = storage.GetConnection();
            using (connection.AcquireDistributedLock(GetLockResource(chainId), LockTimeout))
            {
                ChainRecord? record = ChainRecord.Read(connection, chainId);
                if (record == null) return -1;

                if (record.DeleteBehavior == ChainDeleteBehavior.Restrict &&
                    record.Jobs.Values.Any(x => ProcessingState.StateName.Equals(connection.GetStateData(x)?.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Chain '{chainId}' can't be deleted while a step is processing: it is Restrict.");
                }

                int deleted = DeleteSteps(new BackgroundJobClient(storage), record, excludeRoot: false, ChainDeletedReason, null, connection);

                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                transaction.RemoveHash(GetKey(chainId));
                transaction.RemoveFromSet(SetKey, chainId);
                transaction.Commit();

                return deleted;
            }
        }
    }

    internal static bool IsSettledState(string? state)
    {
        return state == null ||
               SucceededState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
               DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
               FailedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates the jobs of a chain and records it.</summary>
    internal static JobChainResult? Create(JobChain chain, IBackgroundJobClient client, IState rootState, ScheduleOptions? rootOptions, string? chainId)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (client is not IBackgroundJobClientV2 clientV2)
        {
            throw new NotSupportedException("A chain needs a client that implements IBackgroundJobClientV2, as BackgroundJobClient does.");
        }

        return Create(chain, clientV2, rootState, rootOptions, chainId ?? Guid.NewGuid().ToString("N"), 1, JobClock.UtcNow);
    }

    private static JobChainResult? Create(
        JobChain chain,
        IBackgroundJobClientV2 client,
        IState rootState,
        ScheduleOptions? rootOptions,
        string chainId,
        int generation,
        DateTime createdAt)
    {
        IReadOnlyList<ChainLevel> levels = chain.Levels;
        Dictionary<string, string> jobs = new(StringComparer.Ordinal);
        Dictionary<string, object> rootParameters = rootOptions?.ToParameters() ?? new Dictionary<string, object>(StringComparer.Ordinal);

        if (rootOptions?.Queue != null && !client.Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
        {
            throw new NotSupportedException("The current storage can't keep the queue of a job. Use the QueueAttribute instead of ScheduleOptions.Queue.");
        }

        Job rootJob = rootOptions?.Queue != null ? WithQueue(levels[0].Steps[0].Job, rootOptions.Queue) : levels[0].Steps[0].Job;
        string? rootId = CreateOrNull(client, rootJob, rootState, chainId, Key(0, 0), rootParameters);
        if (rootId == null) return null;

        jobs[Key(0, 0)] = rootId;

        try
        {
            for (int level = 1; level < levels.Count; level++)
            {
                List<string> parents = ParentJobs(levels, jobs, levels[level].Parent);
                for (int i = 0; i < levels[level].Steps.Count; i++)
                {
                    ChainStep step = levels[level].Steps[i];
                    jobs[Key(level, i)] = Create(client, step.Job, StateFor(step, parents), chainId, Key(level, i), null);
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Not a chain without its later steps: delete what was created, the last steps first so nothing starts
            foreach (string jobId in jobs.Values.Reverse())
            {
                try
                {
                    client.ChangeState(jobId, new DeletedState { Reason = "The chain couldn't be created" }, null);
                }
                catch (Exception deleteException) when (deleteException.IsCatchableExceptionType())
                {
                    Logger.WarnException($"Background job '{jobId}' of chain '{chainId}' couldn't be deleted after the chain failed to be created.", deleteException);
                }
            }

            throw;
        }

        ChainRecord record = new(chainId, levels, jobs, generation, createdAt, chain.DeleteBehavior);
        using (IStorageConnection connection = client.Storage.GetConnection())
        {
            record.Write(client.Storage, connection);
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
            _ => (FanInMode.All, step.Options),
        };

        return new AwaitingJobsState(parents, new EnqueuedState(), fanInOptions, mode);
    }

    // The deepest steps first, so deleting a step never starts the continuations after it
    private static int DeleteSteps(IBackgroundJobClient client, ChainRecord record, bool excludeRoot, string reason, string[]? onlyFrom, IStorageConnection connection)
    {
        int deleted = 0;

        foreach (KeyValuePair<string, string> job in record.Jobs.OrderByDescending(static x => ParseKey(x.Key).Level).ThenByDescending(static x => ParseKey(x.Key).Index))
        {
            if (excludeRoot && job.Key == Key(0, 0)) continue;

            string? state = connection.GetStateData(job.Value)?.Name;
            if (state == null || SucceededState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
                DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (onlyFrom != null && !onlyFrom.Contains(state, StringComparer.OrdinalIgnoreCase)) continue;

            if (client.ChangeState(job.Value, new DeletedState { Reason = reason }, state)) deleted++;
        }

        return deleted;
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

    internal sealed record ChainRecord(
        string ChainId,
        IReadOnlyList<ChainLevel> Levels,
        IReadOnlyDictionary<string, string> Jobs,
        int Generation,
        DateTime CreatedAt,
        ChainDeleteBehavior DeleteBehavior)
    {
        public static ChainRecord? Read(IStorageConnection connection, string chainId)
        {
            Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(chainId));
            if (hash == null || !hash.TryGetValue(DefinitionField, out string? definition) || !hash.TryGetValue(JobsField, out string? jobs)) return null;

            ChainDefinition dto = SerializationHelper.Deserialize<ChainDefinition>(definition, SerializationOption.Internal)
                ?? throw new FormatException($"The record of chain '{chainId}' is empty.");

            return new ChainRecord(
                chainId,
                dto.ToLevels(),
                SerializationHelper.Deserialize<Dictionary<string, string>>(jobs, SerializationOption.Internal) ?? [],
                hash.TryGetValue(GenerationField, out string? generation) ? int.Parse(generation, CultureInfo.InvariantCulture) : 1,
                hash.TryGetValue(CreatedAtField, out string? createdAt) ? JobHelper.DeserializeDateTime(createdAt) : DateTime.MinValue,
                dto.DeleteBehavior);
        }

        public void Write(JobStorage storage, IStorageConnection connection)
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetRangeInHash(GetKey(ChainId), new Dictionary<string, string>
            {
                [DefinitionField] = SerializationHelper.Serialize(ChainDefinition.From(Levels, DeleteBehavior), SerializationOption.Internal),
                [JobsField] = SerializationHelper.Serialize(Jobs, SerializationOption.Internal),
                [GenerationField] = Generation.ToString(CultureInfo.InvariantCulture),
                [CreatedAtField] = JobHelper.SerializeDateTime(CreatedAt),
                [DeleteBehaviorField] = DeleteBehavior.ToString(),
            });
            transaction.AddToSet(SetKey, ChainId, JobHelper.ToTimestamp(CreatedAt));

            // The record lasts a while after the chain; a storage without expiring hashes keeps it
            if (transaction is JobStorageTransaction storageTransaction && storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                storageTransaction.ExpireHash(GetKey(ChainId), JobChain.RecordRetention);
            }

            transaction.Commit();
        }

        public JobChainResult ToResult() => new(ChainId, Jobs[Key(0, 0)], Jobs);

        public JobChainInfo ToInfo(IStorageConnection connection)
        {
            List<JobChainStepInfo> steps = [];
            for (int level = 0; level < Levels.Count; level++)
            {
                for (int i = 0; i < Levels[level].Steps.Count; i++)
                {
                    string key = Key(level, i);
                    if (!Jobs.TryGetValue(key, out string? jobId)) continue;

                    ChainStep step = Levels[level].Steps[i];
                    steps.Add(new JobChainStepInfo(key, jobId, connection.GetStateData(jobId)?.Name, level, Levels[level].Parent, step.Job) { Link = step.Link, Options = step.Options, LevelKind = Levels[level].Kind });
                }
            }

            return new JobChainInfo(ChainId, Generation, CreatedAt, DeleteBehavior, steps);
        }
    }

    /// <summary>The stored form of a chain's levels: each step's method call as the storage keeps jobs.</summary>
    internal sealed class ChainDefinition
    {
        [JsonPropertyName("v")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("deleteBehavior")]
        public ChainDeleteBehavior DeleteBehavior { get; set; }

        [JsonPropertyName("levels")]
        public List<LevelDefinition> Levels { get; set; } = [];

        public static ChainDefinition From(IReadOnlyList<ChainLevel> levels, ChainDeleteBehavior deleteBehavior)
        {
            return new ChainDefinition
            {
                DeleteBehavior = deleteBehavior,
                Levels = [.. levels.Select(static level => new LevelDefinition
                {
                    Parent = level.Parent,
                    Kind = level.Kind,
                    Steps = [.. level.Steps.Select(static step => new StepDefinition
                    {
                        Job = InvocationData.SerializeJob(step.Job).SerializePayload()!, // A job always has a payload
                        Link = step.Link,
                        Options = step.Options,
                    })],
                })],
            };
        }

        public List<ChainLevel> ToLevels()
        {
            if (Levels.Count == 0 || Levels[0].Steps.Count != 1) throw new FormatException("A chain definition starts with a single step.");

            return [.. Levels.Select(static level => new ChainLevel(
                level.Parent,
                level.Kind,
                [.. level.Steps.Select(static step => new ChainStep(InvocationData.DeserializePayload(step.Job).DeserializeJob(), step.Link, step.Options))]))];
        }
    }

    internal sealed class LevelDefinition
    {
        [JsonPropertyName("parent")]
        public int Parent { get; set; }

        [JsonPropertyName("kind")]
        public ChainLevelKind Kind { get; set; }

        [JsonPropertyName("steps")]
        public List<StepDefinition> Steps { get; set; } = [];
    }

    internal sealed class StepDefinition
    {
        [JsonPropertyName("job")]
        public string Job { get; set; } = string.Empty;

        [JsonPropertyName("link")]
        public ChainLink Link { get; set; }

        [JsonPropertyName("options")]
        public JobContinuationOptions Options { get; set; }
    }
}
