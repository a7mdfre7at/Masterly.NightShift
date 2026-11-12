using System.Globalization;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Lets a step of a <see cref="JobChain"/> run only when its chain's record lists it, and keeps that record for as
/// long as the chain has steps to run. Registered globally by default, before every other filter.
/// </summary>
/// <remarks>
/// <para>
/// When a worker is about to process a job that carries the chain parameters
/// (<see cref="JobChains.ChainIdParameter"/> and <see cref="JobChains.ChainStepParameter"/>), the record of its
/// chain decides:
/// </para>
/// <list type="bullet">
/// <item><description>The record lists the job for its step: it runs.</description></item>
/// <item><description>
/// The job is younger than <see cref="CreationGrace"/> and the record doesn't list it, or there is no record yet:
/// the chain is being created, retried or replaced, by a client that writes the record once every job exists. The
/// job goes to the Scheduled state for a few seconds (<see cref="WaitingReasonFormat"/>), longer each time, and is
/// checked again; that isn't a retry. The client enqueues a first step held that way as soon as the record is
/// written, so a chain starts without waiting for the hold to end.
/// </description></item>
/// <item><description>
/// The record lists another job for the step, or none: the job was replaced by <c>RetryChainFrom</c> or
/// <c>ReplaceChain</c>, or was never part of the chain. It is deleted (<see cref="NotCurrentReasonFormat"/>).
/// </description></item>
/// <item><description>
/// There is no record, and the chain isn't in the list of chains: it was deleted, or its creation never finished.
/// The job is deleted (<see cref="NotRecordedReasonFormat"/>). On a storage that expires records, a job older than
/// <see cref="JobChain.RecordRetention"/> runs instead, because its chain's record expired; so does a job whose
/// chain is still listed, because the storage dropped the record before its time (a cap on expiration times, an
/// eviction).
/// </description></item>
/// </list>
/// <para>
/// A job that isn't a step of a chain costs a dictionary lookup when the storage loads the job's parameters with it
/// (<see cref="BackgroundJob.ParametersSnapshot"/>, as the storages of this project do), and one parameter read
/// otherwise. A step costs one read of its chain's record. When the record can't be read, the job is neither run
/// nor failed: it is scheduled again a little later (<see cref="UnreadableReasonFormat"/>).
/// </para>
/// <para>
/// Each state change of a step also keeps the chain's record for <see cref="JobChain.RecordRetention"/> longer (from
/// the time a Scheduled step is due), after the state change is committed. A process does it about once an hour
/// for a chain, not at all for a step created within the last hour (its record is new), and only on storages that
/// load the job's parameters with the job.
/// </para>
/// </remarks>
public sealed class JobChainStepFilter : IElectStateFilter, IApplyStateFilter
{
    /// <summary>The reason of a step deleted because its chain's record lists another job for it; <c>{0}</c> is the chain.</summary>
    public const string NotCurrentReasonFormat = "Not a current step of chain {0}";

    /// <summary>The reason of a step deleted because its chain has no record; <c>{0}</c> is the chain.</summary>
    public const string NotRecordedReasonFormat = "Chain {0} isn't recorded: it was deleted or never completely created";

    /// <summary>The reason of a step held until its chain is recorded; <c>{0}</c> is the chain.</summary>
    public const string WaitingReasonFormat = "Waiting for chain {0} to be recorded";

    /// <summary>The reason of a step held because its chain's record couldn't be read; <c>{0}</c> is the chain.</summary>
    public const string UnreadableReasonFormat = "Waiting: the record of chain {0} couldn't be read";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobChainStepFilter));

    private static readonly TimeSpan MinHoldDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxHoldDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnreadableHoldDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long after its creation a job that its chain's record doesn't list is held rather than deleted: the time
    /// a client may take to create, retry or replace a chain before it writes the record. Replaceable for tests.
    /// </summary>
    internal static TimeSpan CreationGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The reason a step is enqueued with once its chain is recorded, after it waited for that.</summary>
    internal const string RecordedReason = "The chain is recorded";

    /// <summary>The reason a step waiting for its chain's record is held with.</summary>
    internal static string WaitingReason(string chainId) => string.Format(CultureInfo.InvariantCulture, WaitingReasonFormat, chainId);

    /// <summary>Whether a deleted step's reason says this filter deleted it: its chain's record didn't list it.</summary>
    internal static bool IsRefusal(string chainId, string? reason)
    {
        return string.Equals(reason, string.Format(CultureInfo.InvariantCulture, NotCurrentReasonFormat, chainId), StringComparison.Ordinal) ||
               string.Equals(reason, string.Format(CultureInfo.InvariantCulture, NotRecordedReasonFormat, chainId), StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateState is EnqueuedState enqueuedState)
        {
            // A step held here goes back to the queue it was held from
            HeldQueue.Restore(context, enqueuedState);
            return;
        }

        if (context.CandidateState is not ProcessingState)
        {
            return;
        }

        BackgroundJob job = context.BackgroundJob;
        string? chainId = null;
        string? stepKey = null;
        Verdict verdict;

        try
        {
            if (!TryReadStep(job, context.Connection, out chainId, out stepKey))
            {
                return;
            }

            verdict = Check(context.Storage, context.Connection, job, chainId, stepKey);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Not run: the record may say the step was replaced. Not failed either: the step is checked again
            Logger.WarnException($"The chain of background job '{job.Id}' couldn't be checked, so the job is scheduled again in {UnreadableHoldDelay.TotalSeconds:0} seconds instead of running.", ex);
            Hold(context, string.Format(CultureInfo.InvariantCulture, UnreadableReasonFormat, chainId ?? "?"), UnreadableHoldDelay);
            return;
        }

        switch (verdict)
        {
            case Verdict.Run:
                break;

            case Verdict.Wait:
                Hold(context, WaitingReason(chainId), HoldDelay(JobClock.UtcNow - job.CreatedAt));
                ReleaseOnceHeldIfRecorded(context, chainId, stepKey);
                break;

            case Verdict.NotCurrent:
                Logger.Warn($"Background job '{job.Id}' doesn't run: the record of chain '{chainId}' lists another job for its step. It was replaced by a retry or a replacement of the chain, and is deleted.");
                context.CandidateState = new DeletedState { Reason = string.Format(CultureInfo.InvariantCulture, NotCurrentReasonFormat, chainId) };
                break;

            default:
                Logger.Warn($"Background job '{job.Id}' doesn't run: its chain '{chainId}' isn't recorded. The chain was deleted, or its creation never finished. The job is deleted.");
                context.CandidateState = new DeletedState { Reason = string.Format(CultureInfo.InvariantCulture, NotRecordedReasonFormat, chainId) };
                break;
        }
    }

    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A job being created: its chain is recorded once every step is. And without work after the commit (the
        // transaction is the caller's), the next state change does it.
        if (context.OldStateName == null || context.AfterCommit == null)
        {
            return;
        }

        // Only with the parameters at hand: this runs at every state change of every job
        IReadOnlyDictionary<string, string>? parameters = context.BackgroundJob.ParametersSnapshot;
        if (parameters == null || !parameters.TryGetValue(JobChains.ChainIdParameter, out string? serialized))
        {
            return;
        }

        // A record is written after the jobs it lists are created, and kept for RecordRetention from then at least:
        // the record of a job this young expires late enough already. So a chain that runs at once costs nothing.
        DateTime latestStart = context.NewState is ScheduledState scheduled ? scheduled.EnqueueAt : JobClock.UtcNow;
        if (latestStart - context.BackgroundJob.CreatedAt <= JobChains.RecordRefreshSlack)
        {
            return;
        }

        if (!context.Storage.HasFeature(JobStorageFeatures.ExtendedApi))
        {
            return;
        }

        string? chainId = TryDeserialize(serialized);
        if (string.IsNullOrWhiteSpace(chainId) || !JobChains.MayNeedExtending(chainId, latestStart))
        {
            return;
        }

        // After the commit: the record is shared by the steps of the chain, which change state side by side
        JobStorage storage = context.Storage;
        IStorageConnection connection = context.Connection;
        context.RunAfterCommit(() => JobChains.ExtendRecord(storage, connection, chainId, latestStart));
    }

    /// <inheritdoc />
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    private static Verdict Check(JobStorage storage, IStorageConnection connection, BackgroundJob job, string chainId, string stepKey)
    {
        (bool recorded, string? current) = JobChains.ReadStepJob(storage, connection, chainId, stepKey);
        if (string.Equals(current, job.Id, StringComparison.Ordinal))
        {
            return Verdict.Run;
        }

        TimeSpan age = JobClock.UtcNow - job.CreatedAt;

        // A client creates the jobs of a chain, of a retry or of a replacement first and writes the record last, so
        // a job this young may be about to be listed. It isn't waited for here: the job is locked meanwhile, and
        // the client needs that lock to create the steps that continue the job.
        if (age < CreationGrace)
        {
            return Verdict.Wait;
        }

        if (recorded)
        {
            return Verdict.NotCurrent;
        }

        // Where records never expire, a chain without one was deleted or never recorded
        if (!storage.HasFeature(JobStorageFeatures.ExtendedApi))
        {
            return Verdict.NotRecorded;
        }

        // A record outlives the steps it lists by RecordRetention at least: without one, a job that old is a step
        // of a chain whose record expired, and runs as it always did
        if (age > JobChain.RecordRetention)
        {
            return Verdict.Run;
        }

        // A chain is listed from the moment it is recorded until it is deleted. Listed without a record, it lost
        // the record before its time (a storage that caps expiration times or evicts keys, clocks that disagree):
        // its steps run rather than being deleted for it.
        if (JobChains.IsListed(storage, connection, chainId))
        {
            Logger.Warn($"Chain '{chainId}' is listed but has no record: the storage dropped it before it was due to expire. Its step, background job '{job.Id}', runs; the chain can't be read, retried or replaced any more.");
            return Verdict.Run;
        }

        return Verdict.NotRecorded;
    }

    /// <summary>
    /// The record may be written between its read and the moment the hold is stored, after the client looked for a
    /// held first step: then nobody would enqueue the step before its hold ends. So the record is read once more
    /// when the hold is stored, and a step it lists is enqueued at once.
    /// </summary>
    private static void ReleaseOnceHeldIfRecorded(ElectStateContext context, string chainId, string stepKey)
    {
        // Without work after the commit, the hold ends by itself
        if (context.AfterCommit == null)
        {
            return;
        }

        JobStorage storage = context.Storage;
        IStorageConnection connection = context.Connection;
        string jobId = context.BackgroundJob.Id;

        context.AfterCommit.Add(() =>
        {
            if (!string.Equals(JobChains.ReadStepJob(storage, connection, chainId, stepKey).JobId, jobId, StringComparison.Ordinal))
            {
                return;
            }

            StateData? state = connection.GetStateData(jobId);
            if (state == null ||
                !ScheduledState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(state.Reason, WaitingReason(chainId), StringComparison.Ordinal))
            {
                return;
            }

            new BackgroundJobStateChanger().ChangeState(new StateChangeContext(
                storage,
                connection,
                jobId,
                new EnqueuedState { Reason = RecordedReason },
                ScheduledState.StateName));
        });
    }

    // Longer each time, so a chain whose creation takes long, or never finishes, doesn't fill its step's history;
    // and not past the grace by more than a moment, so a step that is to be deleted is deleted then
    private static TimeSpan HoldDelay(TimeSpan age)
    {
        TimeSpan delay = age < MinHoldDelay ? MinHoldDelay : age > MaxHoldDelay ? MaxHoldDelay : age;
        TimeSpan untilDecided = CreationGrace - age + TimeSpan.FromSeconds(1);

        if (delay > untilDecided)
        {
            delay = untilDecided > MinHoldDelay ? untilDecided : MinHoldDelay;
        }

        return delay;
    }

    private static void Hold(ElectStateContext context, string reason, TimeSpan delay)
    {
        HeldQueue.Remember(context);
        context.CandidateState = new ScheduledState(JobClock.UtcNow + delay) { Reason = reason };
    }

    /// <summary>
    /// Reads the chain and the step of a job from the parameters loaded with it; a storage that doesn't load them
    /// costs one parameter read for a job outside a chain, two for a step.
    /// </summary>
    private static bool TryReadStep(BackgroundJob job, IStorageConnection connection, [NotNullWhen(true)] out string? chainId, [NotNullWhen(true)] out string? stepKey)
    {
        chainId = null;
        stepKey = null;

        IReadOnlyDictionary<string, string>? parameters = job.ParametersSnapshot;
        string? serializedChain;
        string? serializedStep;

        if (parameters != null)
        {
            if (!parameters.TryGetValue(JobChains.ChainIdParameter, out serializedChain))
            {
                return false;
            }

            parameters.TryGetValue(JobChains.ChainStepParameter, out serializedStep);
        }
        else
        {
            serializedChain = connection.GetJobParameter(job.Id, JobChains.ChainIdParameter);
            if (string.IsNullOrEmpty(serializedChain))
            {
                return false;
            }

            serializedStep = connection.GetJobParameter(job.Id, JobChains.ChainStepParameter);
        }

        chainId = TryDeserialize(serializedChain);
        stepKey = TryDeserialize(serializedStep);

        return !string.IsNullOrWhiteSpace(chainId) && !string.IsNullOrWhiteSpace(stepKey);
    }

    // Parameters of these names that aren't strings were set by something else: not a step of a chain
    private static string? TryDeserialize(string? serialized)
    {
        if (string.IsNullOrEmpty(serialized))
        {
            return null;
        }

        try
        {
            return SerializationHelper.Deserialize<string>(serialized, SerializationOption.User);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    private enum Verdict
    {
        Run,
        Wait,
        NotCurrent,
        NotRecorded,
    }
}
