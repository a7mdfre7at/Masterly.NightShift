using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    private const string CleanupField = "Cleanup";

    /// <summary>
    /// The hash of the times records were written to be kept for, one field each, in seconds: the longest a step
    /// may have been marked with (<see cref="ChainRecordedParameter"/>). It keeps a chain whose record was lost
    /// listed for as long as any of its steps may count on its record (<see cref="LostRetention"/>).
    /// </summary>
    internal const string RetentionsKey = "chains:retentions";

    // The retentions this process has written in each storage's RetentionsKey: written once each
    private static readonly ConditionalWeakTable<JobStorage, ConcurrentDictionary<long, byte>> WrittenRetentions = [];

    // The state data that says when a step finished
    private static readonly string[] FinishedAtFields = ["FailedAt", "SucceededAt", "DeletedAt"];

    /// <summary>
    /// Reads a chain for a list, or to tell whether it is settled: each step's state, and the method call of the
    /// first step only, so no other job type is loaded. <c>null</c> when the chain isn't recorded (any more).
    /// </summary>
    internal static JobChainInfo? ReadSummary(IStorageConnection connection, string chainId)
    {
        return ChainRecord.Read(connection, chainId)?.ToInfo(connection, everyMethod: false);
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
                return JobHelper.DeserializeNullableDateTime(value) ?? DateTime.MinValue;
            }
            catch (FormatException)
            {
                // A time nobody can read counts as long ago: a step that waits for this one isn't about to be
                // told for good
                return DateTime.MinValue;
            }
        }

        // A finished state that doesn't say when: long ago, for the same reason
        return state.Name != null && IsSettledState(state.Name) ? DateTime.MinValue : null;
    }

    /// <summary>
    /// What the steps a waiting step waits for wrote on it when they finished, read only where it decides
    /// something: for a step that waits for several steps, one of whose jobs is gone. <c>null</c> otherwise.
    /// <paramref name="before"/> are the steps of the levels before it, which the level it waits for is one of.
    /// </summary>
    private static Dictionary<string, string>? ReadOutcomes(IStorageConnection connection, string jobId, StateData? state, List<JobChainStepInfo> before, int parentLevel)
    {
        if (!IsAwaiting(state?.Name))
        {
            return null;
        }

        int parents = 0;
        bool gone = false;

        foreach (JobChainStepInfo step in before)
        {
            if (step.Level == parentLevel)
            {
                parents++;
                gone |= step.State == null;
            }
        }

        if (parents < 2 || !gone)
        {
            return null;
        }

        try
        {
            return FanInContinuationsSupportAttribute.ReadOutcomes(connection, jobId);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The chain is still read: the steps that are gone count as not finished
            Logger.DebugException($"What the steps before background job '{jobId}' finished as couldn't be read.", ex);
            return null;
        }
    }

    /// <summary>When a step in the Scheduled state is due, or <c>null</c> for another state or a time nobody can read.</summary>
    private static DateTime? ReadDueAt(StateData? state)
    {
        if (state?.Data == null ||
            !ScheduledState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) ||
            !state.Data.TryGetValue("EnqueueAt", out string? enqueueAt))
        {
            return null;
        }

        try
        {
            return JobHelper.DeserializeNullableDateTime(enqueueAt);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// How long a chain whose record was lost is kept listed: the longest time any process wrote a record to be
    /// kept for (<see cref="RetentionsKey"/>), and this process's at least, so no step that counts on its record is
    /// taken for one of a chain that was deleted. The longest time allowed when that can't be read.
    /// </summary>
    internal static TimeSpan LostRetention(IStorageConnection connection)
    {
        TimeSpan longest = JobChain.RecordRetention;
        Dictionary<string, string>? written;

        try
        {
            written = connection.GetAllEntriesFromHash(RetentionsKey);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException("The times records were written to be kept for couldn't be read; a chain whose record was lost is kept listed the longest time allowed.", ex);
            written = null;
        }

        if (written == null || written.Count == 0)
        {
            return TimeSpan.FromDays(ExpireAfterAttribute.MaxDays);
        }

        foreach (string field in written.Keys)
        {
            if (MarkedRetention(field) is { } retention && retention > longest)
            {
                longest = retention;
            }
        }

        return longest;
    }

    /// <summary>
    /// The value of the mark a recorded step carries (<see cref="ChainRecordedParameter"/>): the time, in seconds,
    /// the record that lists it is kept at least, as the process that writes the record is set.
    /// </summary>
    internal static string RecordedMark(TimeSpan retention)
    {
        return ((long)retention.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How long the record that listed a marked step was kept at least: what the mark says, or what this process
    /// is set to for a mark that doesn't say (one written by an earlier build). <c>null</c> for a step without mark.
    /// </summary>
    internal static TimeSpan? MarkedRetention(string? mark)
    {
        if (string.IsNullOrEmpty(mark))
        {
            return null;
        }

        return long.TryParse(mark, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) &&
               seconds > 0 && seconds <= TimeSpan.FromDays(ExpireAfterAttribute.MaxDays).TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : JobChain.RecordRetention;
    }

    /// <summary>
    /// What the chain's record says about a step, reading only the record's map of jobs when the storage can: the
    /// job it lists for the step, and whether the chain is being created or changed. A storage without the extended
    /// API reads the whole record, definition included.
    /// </summary>
    internal static StepRecord ReadStep(JobStorage storage, IStorageConnection connection, string chainId, string stepKey)
    {
        string key = GetKey(chainId);
        string? serialized = connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.ExtendedApi)
            ? storageConnection.GetValueFromHash(key, JobsField)
            : connection.GetAllEntriesFromHash(key)?.GetValueOrDefault(JobsField);

        if (serialized == null)
        {
            return default;
        }

        Dictionary<string, string> jobs = ParseJobs(serialized, out ChainLeaseTime? lease);
        return new StepRecord(true, jobs.Count > 0, jobs.GetValueOrDefault(stepKey), lease);
    }

    /// <summary>What a chain's record says about a step.</summary>
    /// <param name="Recorded">Whether the chain has a record, even one only started.</param>
    /// <param name="HasSteps">Whether the record lists steps: false for a chain whose creation hasn't finished.</param>
    /// <param name="JobId">The job the record lists for the step, if any.</param>
    /// <param name="Lease">Set while the chain is being created or changed; it may have lapsed.</param>
    internal readonly record struct StepRecord(bool Recorded, bool HasSteps, string? JobId, ChainLeaseTime? Lease);

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

    /// <summary>
    /// A chain's record. The definition stays in its stored form, each step's method call serialized: reading,
    /// settling and deleting a chain need no job type, so a chain whose steps' methods were renamed or removed
    /// since can still be read and deleted. A method is loaded only where a job is created from it.
    /// </summary>
    internal sealed record ChainRecord(
        string ChainId,
        ChainDefinition Definition,
        IReadOnlyDictionary<string, string> Jobs,
        int Generation,
        DateTime CreatedAt)
    {
        /// <summary>What a retry or a replacement of the chain left to do, see <see cref="ChainCleanup"/>.</summary>
        public ChainCleanup? Cleanup { get; init; }

        public ChainDeleteBehavior DeleteBehavior => Definition.DeleteBehavior;

        public static ChainRecord? Read(IStorageConnection connection, string chainId)
        {
            Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(chainId));
            if (hash == null || !hash.TryGetValue(DefinitionField, out string? definition) || !hash.TryGetValue(JobsField, out string? serializedJobs))
            {
                return null;
            }

            // A record that lists no first step is what is left of a chain that wasn't created after all
            Dictionary<string, string> jobs = ParseJobs(serializedJobs, out _);
            if (!jobs.ContainsKey(Key(0, 0)))
            {
                return null;
            }

            ChainDefinition dto = SerializationHelper.Deserialize<ChainDefinition>(definition, SerializationOption.Internal)
                ?? throw new FormatException($"The record of chain '{chainId}' is empty.");

            return new ChainRecord(chainId, dto, jobs, ReadGeneration(hash), ReadCreatedAt(hash))
            {
                Cleanup = ChainCleanup.Parse(hash.GetValueOrDefault(CleanupField)),
            };
        }

        /// <summary>
        /// Writes the record, with what is left to clean up and without the lease of the client that created or
        /// changed the chain, and lists the chain: the point from which the chain, or its change, exists. The
        /// record is kept for <see cref="JobChain.RecordRetention"/> after
        /// <paramref name="latestStart"/>, the latest moment a step is known to start, or after now; each state
        /// change of a step keeps it longer (<see cref="ExtendRecord"/>), and so does a server for as long as a step
        /// is left to run or failed for good. The chain is given its first look by a server in the same write
        /// (<see cref="DueSetKey"/>). The jobs in <paramref name="recorded"/>, the ones this write lists for the
        /// first time, are marked as steps of a recorded chain, with the time the record is kept
        /// (<see cref="ChainRecordedParameter"/>).
        /// </summary>
        public void Write(JobStorage storage, IStorageConnection connection, DateTime? latestStart, IReadOnlyCollection<string> recorded)
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
            WriteFields(transaction);
            transaction.AddToSet(SetKey, ChainId, JobHelper.ToTimestamp(CreatedAt));

            // A server looks at the chain again. Soon, when steps wait for others: a waiting step is started by
            // the process that finishes the last step it waits for, which may stop first. Otherwise only before
            // the record expires, to keep it when the step failed for good.
            if (Definition.HasSeveralSteps)
            {
                transaction.AddToSet(DueSetKey, ChainId, JobHelper.ToTimestamp(now + DecisionWindow));
            }
            else if (expires)
            {
                transaction.AddToSet(DueSetKey, ChainId, JobHelper.ToTimestamp(expireAt - KeepAhead()));
            }

            // A chain retried or replaced after everything of it had finished was left to expire
            if (Generation > 1)
            {
                transaction.RemoveFromSet(ExpirySetKey, ChainId);
            }

            if (Cleanup != null)
            {
                transaction.AddToSet(CleanupSetKey, ChainId, JobHelper.ToTimestamp(now));
            }

            // With the record where the storage can, so no job is marked of a chain that isn't recorded
            JobStorageTransaction? marking = storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter) ? transaction as JobStorageTransaction : null;
            TimeSpan retention = JobChain.RecordRetention;
            string mark = RecordedMark(retention);
            if (marking != null)
            {
                foreach (string jobId in recorded)
                {
                    marking.SetJobParameter(jobId, ChainRecordedParameter, mark);
                }
            }

            // The record lasts a while after the chain; a storage without expiring hashes keeps it
            JobStorageTransaction? expiring = expires ? transaction as JobStorageTransaction : null;
            expiring?.ExpireHash(GetKey(ChainId), expireAt - now);

            // What the steps finished as lives as long: a retry or a replacement keeps the steps that finished
            if (Generation > 1)
            {
                expiring?.ExpireHash(GetStepOutcomesKey(ChainId), StepOutcomesExpiry(expireAt - now));
            }

            // The time the marks say, among those any process wrote, once for this process
            ConcurrentDictionary<long, byte> written = WrittenRetentions.GetOrCreateValue(storage);
            long seconds = (long)retention.TotalSeconds;
            bool newRetention = !written.ContainsKey(seconds);
            if (newRetention)
            {
                transaction.SetRangeInHash(RetentionsKey, [new KeyValuePair<string, string>(mark, JobHelper.SerializeDateTime(now))]);
            }

            transaction.Commit();

            if (newRetention)
            {
                written.TryAdd(seconds, 0);
            }

            if (expiring != null)
            {
                RememberExpiry(ChainId, expireAt);
            }

            if (marking == null)
            {
                MarkRecorded(connection, recorded, mark);
            }
        }

        /// <summary>
        /// Writes every field of the record, and nothing else. Every field each time: a field written alone later
        /// would stay behind when the others expire.
        /// </summary>
        public void WriteFields(IWriteOnlyTransaction transaction)
        {
            transaction.SetRangeInHash(GetKey(ChainId), new Dictionary<string, string>
            {
                [DefinitionField] = SerializationHelper.Serialize(Definition, SerializationOption.Internal),
                [JobsField] = SerializeJobs(Jobs, null),
                [GenerationField] = Generation.ToString(CultureInfo.InvariantCulture),
                [CreatedAtField] = JobHelper.SerializeDateTime(CreatedAt),
                [DeleteBehaviorField] = DeleteBehavior.ToString(),
                [CleanupField] = ChainCleanup.Serialize(Cleanup),
            });
        }

        // After the record, on a storage that can't write a job parameter in a transaction: a job this misses
        // (the process stops here) is deleted rather than run once its record expired, never the other way round
        private void MarkRecorded(IStorageConnection connection, IReadOnlyCollection<string> recorded, string mark)
        {
            try
            {
                foreach (string jobId in recorded)
                {
                    connection.SetJobParameter(jobId, ChainRecordedParameter, mark);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"The steps of chain '{ChainId}' couldn't all be marked as recorded. A step that isn't marked is deleted, not run, should it still be waiting when the chain's record has expired.", ex);
            }
        }

        public JobChainResult ToResult() => new(ChainId, Jobs[Key(0, 0)], Jobs);

        /// <summary>
        /// The chain with the state of each step. With <paramref name="everyMethod"/>, each step's method call is
        /// loaded, and is <c>null</c> for a step whose method can't be loaded any more; without, only the first
        /// step's, so no other job type is loaded.
        /// </summary>
        public JobChainInfo ToInfo(IStorageConnection connection, bool everyMethod = true)
        {
            List<JobChainStepInfo> steps = [];

            // Read once, and only for a chain with a step whose job is gone
            Dictionary<string, string>? outcomes = null;
            for (int level = 0; level < Definition.Levels.Count; level++)
            {
                LevelDefinition levelDefinition = Definition.Levels[level];
                for (int i = 0; i < levelDefinition.Steps.Count; i++)
                {
                    string key = Key(level, i);
                    if (!Jobs.TryGetValue(key, out string? jobId))
                    {
                        continue;
                    }

                    StepDefinition step = levelDefinition.Steps[i];
                    Job? job = everyMethod || level == 0 ? TryReadJob(step.Job) : null;
                    StateData? state = connection.GetStateData(jobId);
                    steps.Add(new JobChainStepInfo(key, jobId, state?.Name, level, levelDefinition.Parent, job)
                    {
                        Link = step.Link,
                        Options = step.Options,
                        LevelKind = levelDefinition.Kind,
                        FinishedAt = ReadFinishedAt(state),
                        DueAt = ReadDueAt(state),
                        Outcomes = ReadOutcomes(connection, jobId, state, steps, levelDefinition.Parent),
                        FinishedAs = state == null ? (outcomes ??= ReadStepOutcomes(connection, ChainId)).GetValueOrDefault(jobId) : null,
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

        /// <summary>
        /// The identifier of the retry that holds <see cref="Release"/> (<see cref="RetryMarker.Id"/>): the step is
        /// enqueued only while it is held for that retry, or was put back for it. <c>null</c> for a step created
        /// again for the retry, which carries no note.
        /// </summary>
        [JsonPropertyName("releaseRetry")]
        public string? ReleaseRetry { get; set; }

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

        /// <summary>Whether the chain has steps after the first one: steps that wait for others.</summary>
        [JsonIgnore]
        public bool HasSeveralSteps => Levels.Count > 1;

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

        /// <summary>
        /// Loads the steps of one level, to create jobs from them.
        /// </summary>
        /// <exception cref="InvalidOperationException">The method of a step can't be loaded any more: it was renamed or removed.</exception>
        public ChainLevel ReadLevel(int level, string chainId)
        {
            LevelDefinition definition = Levels[level];
            List<ChainStep> steps = [];

            for (int i = 0; i < definition.Steps.Count; i++)
            {
                steps.Add(new ChainStep(ReadJob(level, i, chainId), definition.Steps[i].Link, definition.Steps[i].Options));
            }

            return new ChainLevel(definition.Parent, definition.Kind, steps);
        }

        /// <summary>Loads the method call of one step, to create a job from it.</summary>
        /// <exception cref="InvalidOperationException">The method can't be loaded any more: it was renamed or removed.</exception>
        public Job ReadJob(int level, int index, string chainId)
        {
            try
            {
                return InvocationData.DeserializePayload(Levels[level].Steps[index].Job).DeserializeJob();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new InvalidOperationException(
                    $"Step '{Key(level, index)}' of chain '{chainId}' can't be created again: its method can't be loaded any more ({ex.Message}). " +
                    "The chain can still be read and deleted; to run this work again, create a new chain.",
                    ex);
            }
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
