using System.Globalization;
using System.Text.Json.Serialization;
namespace Masterly.NightShift;

public static partial class JobChains
{
    private const string DefinitionField = "Definition";
    private const string JobsField = "Jobs";
    private const string GenerationField = "Generation";
    private const string CreatedAtField = "CreatedAt";
    private const string DeleteBehaviorField = "DeleteBehavior";
    private const string CleanupField = "Cleanup";

    // The state data that says when a step finished
    private static readonly string[] FinishedAtFields = ["FailedAt", "SucceededAt", "DeletedAt"];

    /// <summary>
    /// Reads a chain for a list: each step's state, and the method call of the first step only, so no other job type
    /// is loaded. <c>null</c> when the chain isn't recorded (any more).
    /// </summary>
    internal static JobChainInfo? ReadSummary(IStorageConnection connection, string chainId)
    {
        Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(chainId));
        if (hash == null || !hash.TryGetValue(DefinitionField, out string? definition) || !hash.TryGetValue(JobsField, out string? serializedJobs))
        {
            return null;
        }

        ChainDefinition dto = SerializationHelper.Deserialize<ChainDefinition>(definition, SerializationOption.Internal)
            ?? throw new FormatException($"The record of chain '{chainId}' is empty.");
        Dictionary<string, string> jobs = SerializationHelper.Deserialize<Dictionary<string, string>>(serializedJobs, SerializationOption.Internal) ?? [];

        List<JobChainStepInfo> steps = [];
        for (int level = 0; level < dto.Levels.Count; level++)
        {
            LevelDefinition levelDefinition = dto.Levels[level];
            for (int i = 0; i < levelDefinition.Steps.Count; i++)
            {
                string key = Key(level, i);
                if (!jobs.TryGetValue(key, out string? jobId))
                {
                    continue;
                }

                StepDefinition step = levelDefinition.Steps[i];
                Job? job = level == 0 ? TryReadJob(step.Job) : null;
                StateData? state = connection.GetStateData(jobId);
                steps.Add(new JobChainStepInfo(key, jobId, state?.Name, level, levelDefinition.Parent, job)
                {
                    Link = step.Link,
                    Options = step.Options,
                    LevelKind = levelDefinition.Kind,
                    FinishedAt = ReadFinishedAt(state),
                });
            }
        }

        return new JobChainInfo(chainId, ReadGeneration(hash), ReadCreatedAt(hash), dto.DeleteBehavior, steps);
    }

    private static Job? TryReadJob(string payload)
    {
        try
        {
            return InvocationData.DeserializePayload(payload).DeserializeJob();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    private static int ReadGeneration(Dictionary<string, string> hash)
    {
        return hash.TryGetValue(GenerationField, out string? generation) ? int.Parse(generation, CultureInfo.InvariantCulture) : 1;
    }

    private static DateTime ReadCreatedAt(Dictionary<string, string> hash)
    {
        return hash.TryGetValue(CreatedAtField, out string? createdAt) ? JobHelper.DeserializeDateTime(createdAt) : DateTime.MinValue;
    }

    /// <summary>When a step reached the Succeeded, Failed or Deleted state it is in, or <c>null</c> when its state doesn't say.</summary>
    private static DateTime? ReadFinishedAt(StateData? state)
    {
        if (state?.Data == null)
        {
            return null;
        }

        foreach (string field in FinishedAtFields)
        {
            if (!state.Data.TryGetValue(field, out string? value))
            {
                continue;
            }

            try
            {
                return JobHelper.DeserializeNullableDateTime(value);
            }
            catch (FormatException)
            {
                // A state written by something else: when it was reached isn't known
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The job the chain's record lists for a step, reading only the record's jobs when the storage can.
    /// <c>Recorded</c> is false when the chain has no record (any more).
    /// </summary>
    internal static (bool Recorded, string? JobId) ReadStepJob(JobStorage storage, IStorageConnection connection, string chainId, string stepKey)
    {
        string key = GetKey(chainId);
        string? serialized = connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.ExtendedApi)
            ? storageConnection.GetValueFromHash(key, JobsField)
            : connection.GetAllEntriesFromHash(key)?.GetValueOrDefault(JobsField);

        if (serialized == null)
        {
            return (false, null);
        }

        Dictionary<string, string>? jobs = SerializationHelper.Deserialize<Dictionary<string, string>>(serialized, SerializationOption.Internal);
        return (true, jobs?.GetValueOrDefault(stepKey));
    }

    /// <summary>
    /// Whether the chain is in the list of chains: it is from the moment it is recorded until it is deleted, or
    /// until its record expired and a server unlisted it. So a listed chain without record lost its record early.
    /// </summary>
    internal static bool IsListed(JobStorage storage, IStorageConnection connection, string chainId)
    {
        return connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.Connection.GetSetContains)
            ? storageConnection.GetSetContains(SetKey, chainId)
            : connection.GetAllItemsFromSet(SetKey).Contains(chainId);
    }

    internal sealed record ChainRecord(
        string ChainId,
        IReadOnlyList<ChainLevel> Levels,
        IReadOnlyDictionary<string, string> Jobs,
        int Generation,
        DateTime CreatedAt,
        ChainDeleteBehavior DeleteBehavior)
    {
        /// <summary>What a retry or a replacement of the chain left to do, see <see cref="ChainCleanup"/>.</summary>
        public ChainCleanup? Cleanup { get; init; }

        public static ChainRecord? Read(IStorageConnection connection, string chainId)
        {
            Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(chainId));
            if (hash == null || !hash.TryGetValue(DefinitionField, out string? definition) || !hash.TryGetValue(JobsField, out string? jobs))
            {
                return null;
            }

            ChainDefinition dto = SerializationHelper.Deserialize<ChainDefinition>(definition, SerializationOption.Internal)
                ?? throw new FormatException($"The record of chain '{chainId}' is empty.");

            return new ChainRecord(
                chainId,
                dto.ToLevels(),
                SerializationHelper.Deserialize<Dictionary<string, string>>(jobs, SerializationOption.Internal) ?? [],
                ReadGeneration(hash),
                ReadCreatedAt(hash),
                dto.DeleteBehavior)
            {
                Cleanup = ChainCleanup.Parse(hash.GetValueOrDefault(CleanupField)),
            };
        }

        /// <summary>
        /// Writes the record, with what is left to clean up, and lists the chain: the point from which the chain,
        /// or its change, exists. The record is kept for <see cref="JobChain.RecordRetention"/> after
        /// <paramref name="latestStart"/>, the latest moment a step is known to start, or after now; each state
        /// change of a step keeps it longer (<see cref="ExtendRecord"/>).
        /// </summary>
        public void Write(JobStorage storage, IStorageConnection connection, DateTime? latestStart = null)
        {
            DateTime now = JobClock.UtcNow;
            DateTime expireAt = RecordExpiry(now, latestStart ?? now);
            bool expires = storage.HasFeature(JobStorageFeatures.ExtendedApi);

            // A record written again (a retry, a replacement) never expires sooner than it did
            if (expires && Generation > 1 && connection is JobStorageConnection storageConnection)
            {
                DateTime current = now + storageConnection.GetHashTtl(GetKey(ChainId));
                if (current > expireAt)
                {
                    expireAt = current;
                }
            }

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();

            // Every field each time: a field written alone later would stay behind when the others expire
            transaction.SetRangeInHash(GetKey(ChainId), new Dictionary<string, string>
            {
                [DefinitionField] = SerializationHelper.Serialize(ChainDefinition.From(Levels, DeleteBehavior), SerializationOption.Internal),
                [JobsField] = SerializationHelper.Serialize(Jobs, SerializationOption.Internal),
                [GenerationField] = Generation.ToString(CultureInfo.InvariantCulture),
                [CreatedAtField] = JobHelper.SerializeDateTime(CreatedAt),
                [DeleteBehaviorField] = DeleteBehavior.ToString(),
                [CleanupField] = ChainCleanup.Serialize(Cleanup),
            });
            transaction.AddToSet(SetKey, ChainId, JobHelper.ToTimestamp(CreatedAt));

            if (Cleanup != null)
            {
                transaction.AddToSet(CleanupSetKey, ChainId, JobHelper.ToTimestamp(now));
            }

            // The record lasts a while after the chain; a storage without expiring hashes keeps it
            JobStorageTransaction? expiring = expires ? transaction as JobStorageTransaction : null;
            expiring?.ExpireHash(GetKey(ChainId), expireAt - now);

            transaction.Commit();

            if (expiring != null)
            {
                RememberExpiry(ChainId, expireAt);
            }
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
                    if (!Jobs.TryGetValue(key, out string? jobId))
                    {
                        continue;
                    }

                    ChainStep step = Levels[level].Steps[i];
                    StateData? state = connection.GetStateData(jobId);
                    steps.Add(new JobChainStepInfo(key, jobId, state?.Name, level, Levels[level].Parent, step.Job)
                    {
                        Link = step.Link,
                        Options = step.Options,
                        LevelKind = Levels[level].Kind,
                        FinishedAt = ReadFinishedAt(state),
                    });
                }
            }

            return new JobChainInfo(ChainId, Generation, CreatedAt, DeleteBehavior, steps);
        }
    }

    /// <summary>
    /// What a retry or a replacement of a chain still has to do once its new record is written: the jobs the new
    /// record no longer lists, to delete (the deepest steps first), and the held step to enqueue. It is written with
    /// the record, so a change interrupted after that point is finished later, by the next change of the chain or by
    /// a server's <see cref="JobChainMaintenanceProcess"/>. The jobs can't run meanwhile: the record doesn't list them.
    /// </summary>
    internal sealed class ChainCleanup
    {
        /// <summary>The reason the jobs are deleted with.</summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("delete")]
        public List<string> Delete { get; set; } = [];

        /// <summary>The step a retry holds, enqueued once the retry is recorded.</summary>
        [JsonPropertyName("release")]
        public string? Release { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Delete.Count == 0 && Release == null;

        public static string Serialize(ChainCleanup? cleanup)
        {
            return cleanup == null || cleanup.IsEmpty ? string.Empty : SerializationHelper.Serialize(cleanup, SerializationOption.Internal);
        }

        public static ChainCleanup? Parse(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized))
            {
                return null;
            }

            ChainCleanup? cleanup = SerializationHelper.Deserialize<ChainCleanup>(serialized, SerializationOption.Internal);
            return cleanup == null || cleanup.IsEmpty ? null : cleanup;
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
            if (Levels.Count == 0 || Levels[0].Steps.Count != 1)
            {
                throw new FormatException("A chain definition starts with a single step.");
            }

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
