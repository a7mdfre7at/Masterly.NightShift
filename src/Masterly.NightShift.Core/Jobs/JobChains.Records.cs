using System.Globalization;
using System.Text.Json.Serialization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    private const string DefinitionField = "Definition";
    private const string JobsField = "Jobs";
    private const string GenerationField = "Generation";
    private const string CreatedAtField = "CreatedAt";
    private const string DeleteBehaviorField = "DeleteBehavior";

    // How many chains whose record expired one record write removes from the list at most
    private const int MaxPrunedPerWrite = 20;

    /// <summary>
    /// Reads a chain for a list: each step's state, and the method call of the first step only, so no other job type
    /// is loaded. <c>null</c> when the chain isn't recorded (any more).
    /// </summary>
    internal static JobChainInfo? ReadSummary(IStorageConnection connection, string chainId)
    {
        Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(chainId));
        if (hash == null || !hash.TryGetValue(DefinitionField, out string? definition) || !hash.TryGetValue(JobsField, out string? serializedJobs)) return null;

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
                if (!jobs.TryGetValue(key, out string? jobId)) continue;

                StepDefinition step = levelDefinition.Steps[i];
                Job? job = level == 0 ? TryReadJob(step.Job) : null;
                steps.Add(new JobChainStepInfo(key, jobId, connection.GetStateData(jobId)?.Name, level, levelDefinition.Parent, job)
                {
                    Link = step.Link,
                    Options = step.Options,
                    LevelKind = levelDefinition.Kind,
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
                ReadGeneration(hash),
                ReadCreatedAt(hash),
                dto.DeleteBehavior);
        }

        /// <summary>
        /// Writes the record and lists the chain. Chains listed whose record expired are removed from the list at
        /// the same time, a few at each write.
        /// </summary>
        public void Write(JobStorage storage, IStorageConnection connection)
        {
            List<string> expired = FindExpiredChains(storage, connection);

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

            foreach (string chainId in expired)
            {
                transaction.RemoveFromSet(SetKey, chainId);
            }

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

        /// <summary>
        /// The listed chains created before <see cref="JobChain.RecordRetention"/> whose record is gone. A chain
        /// written again later (retried, replaced) keeps its record longer, and stays listed until it expires too.
        /// Bookkeeping: a storage error leaves the list as it is.
        /// </summary>
        private List<string> FindExpiredChains(JobStorage storage, IStorageConnection connection)
        {
            try
            {
                double before = JobHelper.ToTimestamp(JobClock.UtcNow - JobChain.RecordRetention);
                List<string> candidates;

                if (connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest))
                {
                    candidates = storageConnection.GetFirstByLowestScoreFromSet(SetKey, 0, before, MaxPrunedPerWrite);
                }
                else
                {
                    string? oldest = connection.GetFirstByLowestScoreFromSet(SetKey, 0, before);
                    candidates = oldest != null ? [oldest] : [];
                }

                return [.. candidates.Where(x => x != ChainId && !IsRecorded(storage, connection, x))];
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.DebugException("The chains whose record expired couldn't be read; they stay listed until the next chain is recorded.", ex);
                return [];
            }
        }

        private static bool IsRecorded(JobStorage storage, IStorageConnection connection, string chainId)
        {
            return connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.ExtendedApi)
                ? storageConnection.GetHashCount(GetKey(chainId)) > 0
                : connection.GetAllEntriesFromHash(GetKey(chainId)) != null;
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
