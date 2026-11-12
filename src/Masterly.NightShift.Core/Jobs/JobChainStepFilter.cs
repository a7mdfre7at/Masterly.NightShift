using System.Collections.Concurrent;
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
/// <item><description>
/// The record says the chain is being created, retried or replaced: the client that does it wrote so before it
/// created or changed the first job, and says it again while it works. The job, listed or not, goes to the Scheduled
/// state for a few seconds (<see cref="WaitingReasonFormat"/>), longer each time, and is checked again; that isn't a
/// retry. The client enqueues a first step held that way as soon as the chain is recorded. A client that stops in
/// the middle stops saying so, and two minutes later the record counts as it stands.
/// </description></item>
/// <item><description>The record lists the job for its step: it runs.</description></item>
/// <item><description>
/// The record lists another job for the step, or none: the job was replaced by <c>RetryChainFrom</c> or
/// <c>ReplaceChain</c>, or belongs to a change that was abandoned. It is deleted
/// (<see cref="NotCurrentReasonFormat"/>).
/// </description></item>
/// <item><description>
/// The record lists no step at all (the creation of the chain stopped halfway), or there is no record and the chain
/// isn't in the list of chains (it was deleted): the job is deleted (<see cref="NotRecordedReasonFormat"/>). On a
/// storage that expires records, a job without record that is older than <see cref="JobChain.RecordRetention"/>
/// runs instead, because its chain's record expired; so does a job whose chain is still listed, because the storage
/// dropped the record before its time (a cap on expiration times, an eviction).
/// </description></item>
/// </list>
/// <para>
/// Nothing depends on the age of the job, nor on the clocks of the client and the server agreeing where the storage
/// has a clock: "being changed" is written and read with it.
/// </para>
/// <para>
/// A job that isn't a step of a chain costs a dictionary lookup when the storage loads the job's parameters with it
/// (<see cref="BackgroundJob.ParametersSnapshot"/>, as the storages of this project do), and one parameter read
/// otherwise. A step costs one read of its chain's map of jobs, and a read of the storage's clock while the chain
/// is being changed. When the record can't be read, the job is neither run nor failed: it is scheduled again later
/// (<see cref="UnreadableReasonFormat"/>), after a longer time at each attempt, up to fifteen minutes.
/// </para>
/// <para>
/// A step this filter holds keeps its queue (the <c>HeldQueue</c> job parameter), and
/// <see cref="DrainModeFilter"/>, registered by default too, puts it back there when the hold ends.
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

    /// <summary>The reason of a step held while its chain is being created, retried or replaced; <c>{0}</c> is the chain.</summary>
    public const string WaitingReasonFormat = "Waiting for chain {0} to be recorded";

    /// <summary>The reason of a step held because its chain's record couldn't be read; <c>{0}</c> is the chain.</summary>
    public const string UnreadableReasonFormat = "Waiting: the record of chain {0} couldn't be read";

    /// <summary>The reason a step is enqueued with once its chain is recorded, after it waited for that.</summary>
    internal const string RecordedReason = "The chain is recorded";

    // How many jobs whose chain couldn't be read a process remembers at most; past it, they are all forgotten
    private const int MaxUnreadable = 10_000;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobChainStepFilter));

    private static readonly TimeSpan MinHoldDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxHoldDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinUnreadableDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxUnreadableDelay = TimeSpan.FromMinutes(15);

    // How many times in a row this process couldn't read the chain of a job: the job waits longer each time
    private static readonly ConcurrentDictionary<string, int> Unreadable = new(StringComparer.Ordinal);

    /// <summary>The reason a step waiting for its chain to be recorded is held with.</summary>
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

        // Only a job about to run. A step held here is put back in its queue by DrainModeFilter, after the filters
        // that may still schedule it again: restoring the queue here, first, would forget it for those.
        if (context.CandidateState is not ProcessingState)
        {
            return;
        }

        BackgroundJob job = context.BackgroundJob;
        string? chainId = null;
        Verdict verdict;
        TimeSpan holdDelay;

        try
        {
            if (!TryReadStep(job, context.Connection, out chainId, out string? stepKey))
            {
                return;
            }

            verdict = Check(context.Storage, context.Connection, job, chainId, stepKey, out holdDelay);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Not run: the record may say the step was replaced. Not failed or deleted either: the step is checked
            // again, after a longer time at each attempt
            TimeSpan delay = UnreadableDelay(job.Id);
            Logger.WarnException($"The chain of background job '{job.Id}' couldn't be checked, so the job is scheduled again in {delay.TotalSeconds:0} seconds instead of running.", ex);
            Hold(context, string.Format(CultureInfo.InvariantCulture, UnreadableReasonFormat, chainId ?? "?"), delay);
            return;
        }

        if (!Unreadable.IsEmpty)
        {
            Unreadable.TryRemove(job.Id, out _);
        }

        switch (verdict)
        {
            case Verdict.Run:
                break;

            case Verdict.Wait:
                Hold(context, WaitingReason(chainId), holdDelay);
                break;

            case Verdict.NotCurrent:
                Logger.Warn($"Background job '{job.Id}' doesn't run: the record of chain '{chainId}' lists another job for its step. It was replaced by a retry or a replacement of the chain, or belongs to one that was abandoned, and is deleted.");
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

    private static Verdict Check(JobStorage storage, IStorageConnection connection, BackgroundJob job, string chainId, string stepKey, out TimeSpan holdDelay)
    {
        holdDelay = TimeSpan.Zero;
        JobChains.StepRecord record = JobChains.ReadStep(storage, connection, chainId, stepKey);

        if (record.Lease is { } lease)
        {
            // A client is creating, retrying or replacing the chain, or was: told with the clock the lease was
            // written with
            DateTime now = JobChains.StorageUtcNow(storage, connection);
            if (lease.Until > now)
            {
                holdDelay = HoldDelay(lease, now);
                return Verdict.Wait;
            }
        }

        if (string.Equals(record.JobId, job.Id, StringComparison.Ordinal))
        {
            return Verdict.Run;
        }

        if (record.Recorded)
        {
            // A record that lists no step is all there is of a chain whose creation stopped halfway
            return record.HasSteps ? Verdict.NotCurrent : Verdict.NotRecorded;
        }

        // Where records never expire, a chain without one was deleted
        if (!storage.HasFeature(JobStorageFeatures.ExtendedApi))
        {
            return Verdict.NotRecorded;
        }

        // A record outlives the steps it lists by RecordRetention at least: without one, a job that old is a step
        // of a chain whose record expired, and runs as it always did
        if (JobClock.UtcNow - job.CreatedAt > JobChain.RecordRetention)
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

    // Longer the longer the change has lasted, so a chain that takes long to create, or a client that stopped,
    // doesn't fill the step's history; and not much past the moment the lease lapses unless it is renewed
    private static TimeSpan HoldDelay(JobChains.ChainLeaseTime lease, DateTime now)
    {
        TimeSpan lasted = now - lease.Since;
        TimeSpan delay = lasted < MinHoldDelay ? MinHoldDelay : lasted > MaxHoldDelay ? MaxHoldDelay : lasted;
        TimeSpan untilLapsed = lease.Until - now + TimeSpan.FromSeconds(1);

        if (delay > untilLapsed)
        {
            delay = untilLapsed > MinHoldDelay ? untilLapsed : MinHoldDelay;
        }

        return delay;
    }

    // Twice as long at each attempt in a row, as far as this process remembers them
    private static TimeSpan UnreadableDelay(string jobId)
    {
        if (Unreadable.Count >= MaxUnreadable)
        {
            Unreadable.Clear();
        }

        int attempts = Unreadable.AddOrUpdate(jobId, 1, static (_, count) => count + 1);
        double seconds = MinUnreadableDelay.TotalSeconds * Math.Pow(2, Math.Min(attempts - 1, 10));

        return seconds >= MaxUnreadableDelay.TotalSeconds ? MaxUnreadableDelay : TimeSpan.FromSeconds(seconds);
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
