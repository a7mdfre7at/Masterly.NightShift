using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
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
/// The record says the chain is being created and lists no step yet, and that began less than
/// <see cref="QuietTime"/> ago: the creation of a chain takes milliseconds, and a worker that is idle fetches
/// the first step in the middle of it. Nothing is stored about the job: the worker keeps it for a moment (as long
/// as the creation has lasted, between <see cref="MinGiveBackPause"/> and <see cref="MaxGiveBackPause"/>), gives
/// it back to its queue, and it is fetched again, by then usually with the record that lists it. The step's
/// history shows nothing of it. Only for a job in the Enqueued state that a worker fetched; and not where the
/// clocks disagree about when the creation began.
/// </description></item>
/// <item><description>
/// The record says the chain is being created for longer than that, or being retried or replaced: the client that
/// does it wrote so before it created or changed the first job, and says it again while it works. The job, listed
/// or not, goes to the Scheduled state for a few seconds (<see cref="WaitingReasonFormat"/>), longer each time,
/// and is checked again; that isn't a retry. The client enqueues a first step held that way as soon as the chain
/// is recorded. A client that stops in the middle stops saying so, and two minutes later the record counts as it
/// stands.
/// </description></item>
/// <item><description>
/// The job was held for a retry of its chain (it carries <see cref="JobChains.ChainRetryParameter"/>), no client
/// says any more that it is at it, and the record still has the generation the retry started from: the retry was
/// never recorded. The job is put back into the state it was in before the retry, with its data
/// (<see cref="JobChains.RetryAbandonedReasonFormat"/>), and doesn't run: a retry happens completely or not at
/// all. Once the retry is recorded, the record's generation is past that one, and the job runs.
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
/// storage that expires records, a job without record runs instead when its chain's record expired: the job
/// carries the mark of a step that was recorded (<see cref="JobChains.ChainRecordedParameter"/>), which a job of a
/// chain that was never completely created doesn't, and is older than the time the mark says its record was kept
/// (the <see cref="JobChain.RecordRetention"/> of the process that wrote the record, not of this one). So does a
/// job whose chain is still listed, because the storage dropped the record before its time (a cap on expiration
/// times, an eviction).
/// </description></item>
/// </list>
/// <para>
/// Whether a job waits depends neither on its age nor on the clocks of the client and the server agreeing, where
/// the storage has a clock: "being changed" is written and read with it. On a storage without one (EF Core on
/// SQLite, or a storage that doesn't advertise a clock), it is written and read with the clocks of the processes,
/// which must agree to within the two minutes it lasts.
/// </para>
/// <para>
/// A job that isn't a step of a chain costs a dictionary lookup when the storage loads the job's parameters with it
/// (<see cref="BackgroundJob.ParametersSnapshot"/>, as the storages of this project do), and one parameter read
/// otherwise. A step costs one read of its chain's map of jobs, and a read of the storage's clock while the chain
/// is being changed. When the record can't be read, the job is neither run nor failed: it is scheduled again later
/// (<see cref="UnreadableReasonFormat"/>), after a longer time at each attempt, up to fifteen minutes. The filter
/// never throws: an exception here would fail the state change, and in the end the job.
/// </para>
/// <para>
/// A step this filter holds keeps its queue (the <c>HeldQueue</c> job parameter), and
/// <see cref="DrainModeFilter"/>, registered by default too, puts it back there when the hold ends.
/// </para>
/// <para>
/// The filter also keeps the note of a retry on the step the retry holds: it writes it with the state that holds
/// the step, where the storage writes a job parameter with a state, and takes it off when the step leaves the hold
/// any other way than towards a worker (it is deleted, put back, or finishes), so it never decides for a job that
/// isn't held for that retry any more. That costs a dictionary lookup at a state change, on storages that load
/// the job's parameters with the job.
/// </para>
/// <para>
/// Each state change of a step also keeps the chain's record for <see cref="JobChain.RecordRetention"/> longer (from
/// the time a Scheduled step is due), after the state change is committed. A process does it about once an hour
/// for a chain, not at all for a step created within the last hour (its record is new), and only on storages that
/// load the job's parameters with the job. A step that leaves the state it had finished in (a failed step that is
/// requeued, say) has a server look at its chain a few minutes later, also after the commit.
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

    /// <summary>
    /// How long after the creation of a chain began a worker that fetches its first step gives it back to its
    /// queue without a trace (see <see cref="JobGiveBack"/>) rather than holding it in the Scheduled state. Past
    /// it, the creation takes unusually long or its client has stopped, and the step is held as a step of a chain
    /// that is being changed is. Replaceable for tests.
    /// </summary>
    internal static TimeSpan QuietTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The shortest time a worker keeps a step it gives back before the step can be fetched again.</summary>
    internal static readonly TimeSpan MinGiveBackPause = TimeSpan.FromMilliseconds(10);

    /// <summary>The longest time a worker keeps a step it gives back before the step can be fetched again.</summary>
    internal static readonly TimeSpan MaxGiveBackPause = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan MinHoldDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxHoldDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinUnreadableDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxUnreadableDelay = TimeSpan.FromMinutes(15);

    // The jobs a retry is holding in the state change in progress: the election saw the state that holds them,
    // whatever state a later filter made of it (a schedule policy that moves it on), so the filter doesn't take
    // the note off that the same state change writes
    private static readonly ConditionalWeakTable<BackgroundJob, object> HeldNow = [];
    private static readonly object HeldMarker = new();

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

        // A retry takes its step out of the state it finished in: what the retry started from goes on the job in
        // the same write, where the storage can
        if (context.CandidateState is JobChains.RetryHold retryHold)
        {
            HeldNow.AddOrUpdate(context.BackgroundJob, HeldMarker);
            JobChains.MarkHeld(context, retryHold);
            return;
        }

        // The client of a retry that failed puts its step back: only while the step is still held for that retry.
        // Otherwise the state change fails, here, under the job's lock (the one case in which this filter throws)
        if (context.CandidateState is JobChains.RestoredState { HeldFor: { } heldFor })
        {
            JobChains.EnsureHeldFor(context, heldFor);
            return;
        }

        // Only a job about to run. A step held here is put back in its queue by DrainModeFilter, after the filters
        // that may still schedule it again: restoring the queue here, first, would forget it for those.
        if (context.CandidateState is not ProcessingState)
        {
            return;
        }

        BackgroundJob job = context.BackgroundJob;
        string? chainId = null;
        string? note = null;
        JobChains.RetryMarker? retry = null;
        Verdict verdict;
        TimeSpan holdDelay;

        try
        {
            if (!TryReadStep(job, context.Connection, out chainId, out string? stepKey))
            {
                return;
            }

            // A step held for a retry of its chain says so. Only on its way from the hold to a worker: on a job
            // picked up again while it was processing, the note is one that couldn't be taken off, and stale.
            if (IsOnTheWayToAWorker(context.CurrentState))
            {
                note = ReadParameter(job, context.Connection, JobChains.ChainRetryParameter);
                retry = JobChains.RetryMarker.Parse(note);
            }

            // Without a trace only for a job that waits in its queue and that a worker can put back there
            bool quietly = context.CanGiveBack && EnqueuedState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase);
            verdict = Check(context.Storage, context.Connection, job, chainId, stepKey, quietly, retry, out holdDelay);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Not run: the record may say the step was replaced. Not failed or deleted either: the step is checked
            // again, after a longer time at each attempt. Said once as a warning for a job, then quietly.
            TimeSpan delay = UnreadableDelay(job.Id, out bool first);
            string message = $"The chain of background job '{job.Id}' couldn't be checked, so the job is scheduled again in {delay.TotalSeconds:0} seconds instead of running. It is checked again, less and less often, until the chain can be read.";

            if (first)
            {
                Logger.WarnException(message, ex);
            }
            else
            {
                Logger.DebugException(message, ex);
            }

            Hold(context, string.Format(CultureInfo.InvariantCulture, UnreadableReasonFormat, chainId ?? "?"), delay);
            return;
        }

        if (!Unreadable.IsEmpty)
        {
            Unreadable.TryRemove(job.Id, out _);
        }

        // The step waits: whatever it says about a retry is decided when it comes up again
        if (verdict is Verdict.GiveBack or Verdict.Wait)
        {
            retry = null;
        }

        switch (verdict)
        {
            case Verdict.Run:
                break;

            case Verdict.PutBack:
                // Only for a step with the note of a retry
                PutBack(context, chainId, retry!, note);
                break;

            case Verdict.GiveBack:
                // Nothing is written, here or by the filters after this one, which don't run
                if (!context.TryGiveBack(holdDelay))
                {
                    Hold(context, WaitingReason(chainId), MinHoldDelay);
                }

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

        // The retry is decided: its note goes with this state change. Where the job's parameters are loaded
        // with the job, OnStateApplied does it, for whatever state is applied in the end; a step that is put
        // back had it done above.
        if (retry != null && verdict != Verdict.PutBack && job.ParametersSnapshot == null)
        {
            JobChains.ForgetRetryMarker(context.Storage, context.Connection, context.Transaction, context.AfterCommit, job.Id, note, beforeCommit: false);
        }
    }

    // The step goes back into the state it was in before the retry, and doesn't run. The note of the retry stays
    // until that is committed: a state change that fails is tried again, and decided the same way.
    private static void PutBack(ElectStateContext context, string chainId, JobChains.RetryMarker retry, string? note)
    {
        string jobId = context.BackgroundJob.Id;

        Logger.Warn($"Background job '{jobId}' doesn't run: it was held for a retry of chain '{chainId}' that was never recorded. The client that retried the chain stopped, or didn't confirm for {JobChains.LeaseTime.TotalSeconds:0} seconds that it was still at it. The job is put back into the {retry.State} state it was in, and the chain is as it was before the retry; retry it again if it is still wanted.");

        // Not by way of the Processing state: the job's history must not say that it ran
        context.ReplaceCandidateState(JobChains.RestoreAbandoned(retry, chainId));
        JobChains.ForgetRetryMarker(context.Storage, context.Connection, context.Transaction, context.AfterCommit, jobId, note, beforeCommit: false);

        // What the retry had created waits for this job and is listed nowhere: it is deleted, once this is
        // stored. Not before: a state change that fails leaves the step held, for a client that may still wake up.
        if (context.AfterCommit != null)
        {
            JobStorage storage = context.Storage;
            IStorageConnection connection = context.Connection;
            int generation = retry.Generation;
            context.AfterCommit.Add(() => JobChains.DeleteAbandoned(storage, connection, chainId, jobId, generation));
        }
    }

    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A job being created: its chain is recorded once every step is
        if (context.OldStateName == null)
        {
            return;
        }

        // A step put back into a state in which its job expires goes on expiring when it was to: the retention
        // filters, which ran before, would keep it for a whole retention from now, as a job that just finished
        if (context.NewState is JobChains.RestoredState { ExpireAt: { } expireAt } && context.JobExpires)
        {
            TimeSpan left = expireAt - JobClock.UtcNow;
            context.JobExpirationTimeout = left > JobChains.RestoredState.MinLifetime ? left : JobChains.RestoredState.MinLifetime;
        }

        IReadOnlyDictionary<string, string>? parameters = context.BackgroundJob.ParametersSnapshot;

        if (parameters != null && parameters.TryGetValue(JobChains.ChainRetryParameter, out string? retry) && !string.IsNullOrEmpty(retry))
        {
            KeepRetryMarkerWhileHeld(context, transaction, retry);
        }

        // Without work after the commit (the transaction is the caller's), the next state change does the rest
        if (context.AfterCommit == null)
        {
            return;
        }

        // A step that leaves the state it had finished in moves its chain on: a server looks at the chain soon
        if (JobChains.LeavesFinishedState(context.OldStateName, context.NewState))
        {
            JobChains.LookSoonAfterCommit(context, parameters);
        }

        // Only with the parameters at hand: this runs at every state change of every job
        if (parameters == null || !parameters.TryGetValue(JobChains.ChainIdParameter, out string? serialized))
        {
            return;
        }

        try
        {
            // A record is written after the jobs it lists are created, and kept for RecordRetention from then at
            // least: the record of a job this young expires late enough already. So a chain that runs at once
            // costs nothing.
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
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Bookkeeping never fails a state change
            Logger.DebugException($"The record of the chain of background job '{context.BackgroundJob.Id}' couldn't be kept longer; the next state change of one of its steps tries again.", ex);
        }
    }

    /// <inheritdoc />
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    /// <summary>
    /// The note of a retry stays on a job only while the job goes from the retry's hold towards a worker: between
    /// the Scheduled and the Enqueued state. Any other state change takes it off, so it never decides for a job
    /// that somebody deleted, requeued or finished since. A note on a job that isn't in one of the two states was
    /// never followed by a hold, or was left behind: it is stale whatever becomes of this state change, and goes
    /// at once. A note on a job that leaves the two states goes with the state change, and stays when that fails.
    /// </summary>
    private static void KeepRetryMarkerWhileHeld(ApplyStateContext context, IWriteOnlyTransaction transaction, string note)
    {
        // The state that holds the step wrote the note, also over an older one
        if (context.NewState is JobChains.RetryHold || HeldNow.TryGetValue(context.BackgroundJob, out _))
        {
            return;
        }

        bool wasHeld = IsOnTheWayToAWorker(context.OldStateName);
        if (wasHeld && IsOnTheWayToAWorker(context.NewState.Name))
        {
            return;
        }

        JobChains.ForgetRetryMarker(context.Storage, context.Connection, transaction, context.AfterCommit, context.BackgroundJob.Id, note, beforeCommit: !wasHeld);
    }

    private static bool IsOnTheWayToAWorker(string? stateName)
    {
        return ScheduledState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase) ||
               EnqueuedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);
    }

    private static Verdict Check(
        JobStorage storage,
        IStorageConnection connection,
        BackgroundJob job,
        string chainId,
        string stepKey,
        bool quietly,
        JobChains.RetryMarker? retry,
        out TimeSpan holdDelay)
    {
        holdDelay = TimeSpan.Zero;
        int? generation = null;

        // With the note of a retry, the record's generation decides too: both are read in one go
        JobChains.StepRecord record = retry == null
            ? JobChains.ReadStep(storage, connection, chainId, stepKey)
            : JobChains.ReadStepAndGeneration(connection, chainId, stepKey, out generation);

        if (record.Lease is { } lease)
        {
            // A client is creating, retrying or replacing the chain, or was: told with the clock the lease was
            // written with
            DateTime now = JobChains.StorageUtcNow(storage, connection);
            if (lease.IsLive(now))
            {
                // A chain that is being created, for a moment only: its first step goes back to its queue as it
                // is. Not a chain that is being retried or replaced (its record lists steps), which lasts longer
                // and has jobs of the old and of the new steps coming due; and not when the lease began in the
                // future, by the clock it is read with: then the clocks disagree, and nothing bounds the wait.
                TimeSpan lasted = now - lease.Since;
                if (quietly && !record.HasSteps && lasted >= TimeSpan.Zero && lasted < QuietTime)
                {
                    holdDelay = GiveBackPause(lasted);
                    return Verdict.GiveBack;
                }

                holdDelay = HoldDelay(lease, now);
                return Verdict.Wait;
            }

            if (lease.Until > now)
            {
                Logger.Warn($"The record of chain '{chainId}' says the chain is being changed until {lease.Until:u}, much later than a change is ever announced for: it is taken for damaged, and the chain isn't held for it.");
            }
        }

        // Nobody is changing the chain (any more), and its record is the one the retry that held this step
        // started from: that retry was never recorded. Had it been, the generation would be past it; and a record
        // that is gone, or was written anew, decides as for any step.
        if (retry != null && record.HasSteps && generation == retry.Generation)
        {
            return Verdict.PutBack;
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

        // A record outlives the steps it lists by the retention it was written with at least: without one, a job
        // that old is a step of a chain whose record expired, and runs as it always did. Only a job that was
        // listed once: it is marked when the record that lists it is written, so a job of a chain that was never
        // completely created (scheduled far ahead, say, by a client that stopped) doesn't run for being old. The
        // mark says that retention, so this process's own setting doesn't decide for a record another one wrote.
        if (JobChains.MarkedRetention(ReadMark(job, connection)) is { } retention && JobClock.UtcNow - job.CreatedAt > retention)
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

    // As long as the creation has lasted so far, within bounds: the step is looked at again after about twice
    // the time, so a creation that takes a few milliseconds costs one or two looks, and one that takes seconds a
    // few each second until QuietTime has passed
    private static TimeSpan GiveBackPause(TimeSpan lasted)
    {
        return lasted < MinGiveBackPause ? MinGiveBackPause : lasted > MaxGiveBackPause ? MaxGiveBackPause : lasted;
    }

    private static string? ReadMark(BackgroundJob job, IStorageConnection connection)
    {
        return ReadParameter(job, connection, JobChains.ChainRecordedParameter);
    }

    private static string? ReadParameter(BackgroundJob job, IStorageConnection connection, string name)
    {
        return job.ParametersSnapshot != null
            ? job.ParametersSnapshot.GetValueOrDefault(name)
            : connection.GetJobParameter(job.Id, name);
    }

    // Twice as long at each attempt in a row, as far as this process remembers them
    private static TimeSpan UnreadableDelay(string jobId, out bool first)
    {
        if (Unreadable.Count >= MaxUnreadable)
        {
            Unreadable.Clear();
        }

        int attempts = Unreadable.AddOrUpdate(jobId, 1, static (_, count) => count + 1);
        double seconds = MinUnreadableDelay.TotalSeconds * Math.Pow(2, Math.Min(attempts - 1, 10));

        first = attempts == 1;
        return seconds >= MaxUnreadableDelay.TotalSeconds ? MaxUnreadableDelay : TimeSpan.FromSeconds(seconds);
    }

    // Never throws: the step is held whether or not its queue could be kept. A step whose queue was given only by
    // its Enqueued state then goes to the default queue when the hold ends, which is logged.
    private static void Hold(ElectStateContext context, string reason, TimeSpan delay)
    {
        try
        {
            HeldQueue.Remember(context);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"The queue of background job '{context.BackgroundJob.Id}' couldn't be kept while the job is held: unless the job has a queue of its own, it goes to the default queue when the hold ends.", ex);
        }

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
        GiveBack,
        Wait,
        PutBack,
        NotCurrent,
        NotRecorded,
    }
}
