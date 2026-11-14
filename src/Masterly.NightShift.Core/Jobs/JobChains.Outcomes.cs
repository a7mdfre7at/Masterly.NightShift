using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    private const string StepOutcomesKeyPrefix = "chain-outcomes:";

    /// <summary>
    /// The hash that keeps what the steps of a chain finished as: one field a job, the job's id, with the name of the
    /// state it finished in (Succeeded, Deleted, or Failed for good). The step's own state change writes it, so it is
    /// never lost while that state is stored; it lives as long as the chain's record, and goes with the chain. A step
    /// whose job expired still counts as what it finished as, for a retry of the chain (the new steps after a group
    /// wait for the steps beside the retried one) and for the dashboard.
    /// </summary>
    internal static string GetStepOutcomesKey(string chainId) => StepOutcomesKeyPrefix + chainId;

    /// <summary>
    /// In the transaction of a state change of a step that finishes: what it finished as is kept for its chain
    /// (<see cref="GetStepOutcomesKey"/>), in one field of a hash that belongs to the chain, so no other chain waits
    /// for it; once that is committed, the hash is given the time to live of the chain's record. A job is known as a
    /// step by its parameters: a storage that doesn't load them with the job costs a read for each job that finishes.
    /// Bookkeeping: logged, never thrown.
    /// </summary>
    internal static void KeepOutcomeOfStep(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // A failure that is retried is elected away before: a Failed state applied is a failure for good
        string state = context.NewState.Name;
        if (!IsSettledState(state))
        {
            return;
        }

        try
        {
            // Only a step of a chain that was recorded: the jobs of a creation or a retry that failed are deleted
            // without leaving anything behind
            string? serialized;
            IReadOnlyDictionary<string, string>? parameters = context.BackgroundJob.ParametersSnapshot;
            if (parameters != null)
            {
                if (!parameters.TryGetValue(ChainIdParameter, out serialized) || !parameters.ContainsKey(ChainRecordedParameter))
                {
                    return;
                }
            }
            else
            {
                serialized = context.Connection.GetJobParameter(context.BackgroundJob.Id, ChainIdParameter);
                if (string.IsNullOrEmpty(serialized) || string.IsNullOrEmpty(context.Connection.GetJobParameter(context.BackgroundJob.Id, ChainRecordedParameter)))
                {
                    return;
                }
            }

            string? chainId = string.IsNullOrEmpty(serialized) ? null : SerializationHelper.Deserialize<string>(serialized, SerializationOption.User);
            if (string.IsNullOrWhiteSpace(chainId))
            {
                return;
            }

            transaction.SetRangeInHash(GetStepOutcomesKey(chainId), [new KeyValuePair<string, string>(context.BackgroundJob.Id, state)]);

            // Not in this transaction: the steps of a group finish side by side, and each would hold every field of
            // the hash until its commit. The hash goes with the chain when it leaves the list, should this be lost.
            if (context.AfterCommit != null && context.Storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                JobStorage storage = context.Storage;
                IStorageConnection connection = context.Connection;
                context.RunAfterCommit(() => KeepStepOutcomesWithRecord(storage, connection, chainId));
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"What background job '{context.BackgroundJob.Id}' finished as couldn't be kept for its chain. Should its job expire, the chain can't be retried from a step beside it.", ex);
        }
    }

    /// <summary>What the steps of a chain finished as, by job, as they kept it (<see cref="GetStepOutcomesKey"/>).</summary>
    internal static Dictionary<string, string> ReadStepOutcomes(IStorageConnection connection, string chainId)
    {
        Dictionary<string, string>? outcomes = connection.GetAllEntriesFromHash(GetStepOutcomesKey(chainId));
        return outcomes == null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(outcomes, StringComparer.Ordinal);
    }

    /// <summary>
    /// The time to live of what the steps of a chain finished as, for a write that gives the record its time to live
    /// (<paramref name="recordExpiresIn"/>): a little longer, so it never goes before the record.
    /// </summary>
    private static TimeSpan StepOutcomesExpiry(TimeSpan recordExpiresIn) => recordExpiresIn + RecordRefreshSlack;

    /// <summary>
    /// Once the outcome of a step is committed: what the steps finished as is kept as long as the chain's record,
    /// and at least as long as a record written now. Each write that keeps the record longer keeps it as long
    /// (<see cref="ExtendRecord"/>, <see cref="ChainRecord.Write"/>). Best effort: an entry whose time to live was
    /// never set goes with the chain when the chain leaves the list, or with the next write that keeps the record.
    /// </summary>
    private static void KeepStepOutcomesWithRecord(JobStorage storage, IStorageConnection connection, string chainId)
    {
        try
        {
            if (connection is not JobStorageConnection storageConnection || !storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                return;
            }

            // The record's own time, not what this process remembers of it: another process may have kept it longer
            DateTime now = JobClock.UtcNow;
            DateTime recordExpiry = now + storageConnection.GetHashTtl(GetKey(chainId));
            DateTime atLeast = now + JobChain.RecordRetention;

            ExpireStepOutcomes(connection, chainId, StepOutcomesExpiry((recordExpiry > atLeast ? recordExpiry : atLeast) - now));
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"What the steps of chain '{chainId}' finished as couldn't be given the time to live of its record; the next write that keeps the record longer does it.", ex);
        }
    }

    private static void ExpireStepOutcomes(IStorageConnection connection, string chainId, TimeSpan expireIn)
    {
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        if (transaction is not JobStorageTransaction storageTransaction)
        {
            return;
        }

        storageTransaction.ExpireHash(GetStepOutcomesKey(chainId), expireIn);
        transaction.Commit();
    }

    /// <summary>
    /// What the steps beside a retried step finished as, by job, when steps after their group are created again and
    /// wait for them: from what the chain kept (<see cref="GetStepOutcomesKey"/>), which counts for a step whose job
    /// is gone when the new steps are created. <c>null</c> when nothing waits for the group again.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The job of a step beside the retried one is gone, and nothing was kept of what it finished as: checked before
    /// anything changes.
    /// </exception>
    private static Dictionary<string, string>? ReadOutcomesBeside(IStorageConnection connection, ChainRecord record, string stepKey, IReadOnlyCollection<int> retried)
    {
        (int level, int index) = ParseKey(stepKey);
        if (!retried.Any(next => next != level && record.Definition.Levels[next].Parent == level))
        {
            return null;
        }

        Dictionary<string, string>? kept = null;
        Dictionary<string, string>? outcomes = null;

        for (int i = 0; record.Jobs.TryGetValue(Key(level, i), out string? jobId); i++)
        {
            if (i == index)
            {
                continue;
            }

            kept ??= ReadStepOutcomes(connection, record.ChainId);
            if (kept.TryGetValue(jobId, out string? outcome))
            {
                (outcomes ??= new Dictionary<string, string>(StringComparer.Ordinal))[jobId] = outcome;
            }
            else if (connection.GetJobData(jobId) == null)
            {
                throw new InvalidOperationException(
                    $"Chain '{record.ChainId}' can't be retried from step '{stepKey}': the steps after its group wait for step '{Key(level, i)}' too, whose background job '{jobId}' expired, and what it finished as isn't known. Nothing was changed.");
            }
        }

        return outcomes;
    }
}
