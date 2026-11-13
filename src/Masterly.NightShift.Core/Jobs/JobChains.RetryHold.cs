using System.Globalization;
using System.Text.Json.Serialization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    /// <summary>
    /// The job parameter a step carries while a retry of its chain holds it: what the retry started from, see
    /// <see cref="RetryMarker"/>. It goes when the step is let run, put back, or leaves the hold another way.
    /// </summary>
    internal const string ChainRetryParameter = "ChainRetry";

    /// <summary>
    /// The reason of a step put back into the state it was in, because the retry of its chain that held it was
    /// never recorded; <c>{0}</c> is the chain.
    /// </summary>
    internal const string RetryAbandonedReasonFormat = "The retry of chain {0} was abandoned";

    // How much longer than the lease of the retry lasts its step is held: when the hold ends, a client that
    // stopped at the start of the retry no longer counts as being at it, so the step is put back at once
    private const int HoldMarginSeconds = 15;

    // How long the removal of a marker after a commit waits for the job's lock, on the thread that changed the
    // job's state: a job is locked only while its state changes. Past it the marker stays, and goes at the job's
    // next state change.
    private static readonly TimeSpan ForgetLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the step a retry starts from stays out of its state before a server looks at it: as long as the
    /// retry's lease, and a little more. It doesn't bound the retry: a step that comes up while its retry is still
    /// being prepared waits (<see cref="JobChainStepFilter"/>), and one whose retry was never recorded is put back
    /// into the state it was in, without running.
    /// </summary>
    private static TimeSpan HoldTime => LeaseTime + TimeSpan.FromSeconds(HoldMarginSeconds);

    /// <summary>The reason of a step put back because the retry that held it was abandoned.</summary>
    internal static string RetryAbandonedReason(string chainId)
    {
        return string.Format(CultureInfo.InvariantCulture, RetryAbandonedReasonFormat, chainId);
    }

    /// <summary>
    /// The state that takes a step out of the state it finished in while a retry of its chain is prepared, so
    /// nothing reacts to that state meanwhile. A Scheduled state that carries what goes on the job with it:
    /// <see cref="JobChainStepFilter"/> writes <see cref="Marker"/> as the <see cref="ChainRetryParameter"/> in the
    /// transaction of the state change, where the storage can.
    /// </summary>
    internal sealed class RetryHold(DateTime enqueueAt, string marker) : ScheduledState(enqueueAt)
    {
        /// <summary>The serialized <see cref="RetryMarker"/> of the retry.</summary>
        public string Marker { get; } = marker;

        /// <summary>Whether the marker is written, or will be with the state.</summary>
        public bool Marked { get; set; }
    }

    /// <summary>
    /// What a step held for a retry says about it (<see cref="ChainRetryParameter"/>): which retry holds it, the
    /// generation of the chain's record when the retry began, and the state the step was in. A retry that is
    /// recorded raises the generation; so when the step comes up and the record still has this generation, and no
    /// client says it is at it any more, the retry was abandoned, and the step is put back into that state.
    /// </summary>
    internal sealed class RetryMarker
    {
        /// <summary>Tells this retry from any other of the same step.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("generation")]
        public int Generation { get; set; }

        /// <summary>The name of the state the step was in.</summary>
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("data")]
        public Dictionary<string, string> Data { get; set; } = [];

        /// <summary>When the job was to expire before the retry held it, if it was and that could be read.</summary>
        [JsonPropertyName("expireAt")]
        public DateTime? ExpireAt { get; set; }

        public static RetryMarker For(int generation, StateData state, DateTime? expireAt)
        {
            return new RetryMarker
            {
                Id = Guid.NewGuid().ToString("N"),
                Generation = generation,
                State = state.Name,
                Reason = state.Reason,
                Data = state.Data == null ? [] : new Dictionary<string, string>(state.Data, StringComparer.Ordinal),
                ExpireAt = expireAt,
            };
        }

        public string Serialize() => SerializationHelper.Serialize(this, SerializationOption.Internal);

        /// <summary>Reads a marker; <c>null</c> for none, or for one that can't be read.</summary>
        public static RetryMarker? Parse(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized))
            {
                return null;
            }

            try
            {
                RetryMarker? marker = SerializationHelper.Deserialize<RetryMarker>(serialized, SerializationOption.Internal);
                return marker == null || string.IsNullOrEmpty(marker.State) ? null : marker;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return null;
            }
        }

        public StateData ToStateData() => new() { Name = State, Reason = Reason, Data = new Dictionary<string, string>(Data ?? [], StringComparer.Ordinal) };
    }

    /// <summary>
    /// The state a step held for an abandoned retry goes back to: the one it was in, with its data, and a reason
    /// that says why it is there again.
    /// </summary>
    internal static IState RestoreAbandoned(RetryMarker marker, string chainId)
    {
        StateData state = marker.ToStateData();
        state.Reason = RetryAbandonedReason(chainId);
        return Restore(state, marker.ExpireAt);
    }

    /// <summary>
    /// When a job is to expire, or <c>null</c> when it isn't, or when that can't be read: a step that is put back
    /// then expires as any job that reaches its state, a whole retention from then.
    /// </summary>
    private static DateTime? ReadExpiry(JobStorage storage, string jobId)
    {
        try
        {
            return storage.GetMonitoringApi()?.JobDetails(jobId)?.ExpireAt;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The time background job '{jobId}' expires couldn't be read; should it be put back into its state, it is kept as long as a job that reaches that state.", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes the marker of a retry with the state that holds its step, in the transaction of that state change,
    /// where the storage can. Elsewhere the client wrote it before, see <c>Hold</c>.
    /// </summary>
    internal static void MarkHeld(ElectStateContext context, RetryHold hold)
    {
        if (hold.Marked ||
            context.Transaction is not JobStorageTransaction transaction ||
            !context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            return;
        }

        transaction.SetJobParameter(context.BackgroundJob.Id, ChainRetryParameter, hold.Marker);
        hold.Marked = true;
    }

    /// <summary>
    /// Takes the marker of a retry off a job, with a state change of that job: in its transaction where the storage
    /// can. Elsewhere right away (<paramref name="beforeCommit"/>: the marker is stale whatever becomes of the
    /// state change), or once the state change is committed (the marker must outlive a state change that fails);
    /// without anything to run after the commit it is left, and goes at the job's next state change. After the
    /// commit the job's lock is released, and a new retry may hold the job again by then: the marker goes only
    /// while it is still <paramref name="decidedOn"/>, the one the state change saw. Never throws.
    /// </summary>
    internal static void ForgetRetryMarker(
        JobStorage storage,
        IStorageConnection connection,
        IWriteOnlyTransaction transaction,
        PostCommitActions? afterCommit,
        string jobId,
        string? decidedOn,
        bool beforeCommit)
    {
        try
        {
            if (transaction is JobStorageTransaction storageTransaction && storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
            {
                storageTransaction.SetJobParameter(jobId, ChainRetryParameter, null);
            }
            else if (beforeCommit)
            {
                connection.SetJobParameter(jobId, ChainRetryParameter, null);
            }
            else if (!string.IsNullOrEmpty(decidedOn))
            {
                afterCommit?.Add(() => ForgetRetryMarker(connection, jobId, decidedOn));
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The note of a retry of its chain couldn't be taken off background job '{jobId}'; it goes at the job's next state change.", ex);
        }
    }

    /// <summary>
    /// Takes the marker of a retry off a job, in a write of its own, under the job's lock and only while the job
    /// still carries that marker (<paramref name="decidedOn"/>: the marker as it is stored, or the identifier of
    /// its retry): a marker written since belongs to another retry, which holds the job now. Never throws.
    /// </summary>
    private static void ForgetRetryMarker(IStorageConnection connection, string jobId, string decidedOn)
    {
        try
        {
            using (connection.AcquireDistributedJobLock(jobId, ForgetLockTimeout))
            {
                if (IsMarker(connection.GetJobParameter(jobId, ChainRetryParameter), decidedOn))
                {
                    connection.SetJobParameter(jobId, ChainRetryParameter, null);
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The note of a retry of its chain couldn't be taken off background job '{jobId}'; it goes at the job's next state change.", ex);
        }
    }

    /// <summary>
    /// Whether the marker a job carries is the one a decision was taken on: the same text, or the marker of the
    /// retry with that identifier.
    /// </summary>
    internal static bool IsMarker(string? stored, string decidedOn)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        return string.Equals(stored, decidedOn, StringComparison.Ordinal) ||
               string.Equals(RetryMarker.Parse(stored)?.Id, RetryMarker.Parse(decidedOn)?.Id ?? decidedOn, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks, for the state that puts a step back from the hold of a retry (<see cref="RestoredState.HeldFor"/>),
    /// that the job still carries the marker of that retry. Called while the state is elected, so under the job's
    /// lock, with the job's parameters as they are then.
    /// </summary>
    /// <exception cref="RetryHoldLostException">
    /// The job isn't held for that retry any more: the state change fails, and nothing of it is stored.
    /// </exception>
    internal static void EnsureHeldFor(ElectStateContext context, string retryId)
    {
        BackgroundJob job = context.BackgroundJob;
        string? stored = job.ParametersSnapshot != null
            ? job.ParametersSnapshot.GetValueOrDefault(ChainRetryParameter)
            : context.Connection.GetJobParameter(job.Id, ChainRetryParameter);

        if (!string.Equals(RetryMarker.Parse(stored)?.Id, retryId, StringComparison.Ordinal))
        {
            throw new RetryHoldLostException(job.Id);
        }
    }

    /// <summary>
    /// A step wasn't put back from the hold of a retry, because it isn't held for that retry any more: a server
    /// put it back already, somebody changed it, or another retry holds it now.
    /// </summary>
    internal sealed class RetryHoldLostException(string jobId)
        : InvalidOperationException($"Background job '{jobId}' is no longer held for the retry that tried to put it back.");

    /// <summary>
    /// Deletes the jobs an abandoned retry created for the steps right after the one it held: they wait for that
    /// step, and no record lists them, so nothing else would ever delete the ones that wait for a success that
    /// doesn't come. The steps after those go with them, as the continuations of any deleted job do. Under the
    /// chain's lock, without waiting for it, and only while the record is still the one the retry started from and
    /// nobody is changing the chain: a new retry of the chain holds that lock from before it creates a job, so
    /// none of its jobs is ever taken for one of these. Best effort: a job left behind never runs, because the
    /// record doesn't list it.
    /// </summary>
    internal static void DeleteAbandoned(JobStorage storage, IStorageConnection connection, string chainId, string stepJobId, int generation)
    {
        try
        {
            using (connection.AcquireDistributedLock(GetLockResource(chainId), TimeSpan.Zero))
            {
                Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(chainId));
                if (hash == null || !hash.ContainsKey(DefinitionField) || ReadGeneration(hash) != generation)
                {
                    return;
                }

                Dictionary<string, string> jobs = ParseJobs(hash.GetValueOrDefault(JobsField), out ChainLeaseTime? lease);
                if (lease is { } changing && changing.IsLive(StorageUtcNow(storage, connection)))
                {
                    return;
                }

                // What the record lists, or still has to delete or release, isn't the abandoned retry's
                HashSet<string> listed = new(jobs.Values, StringComparer.Ordinal);
                if (ChainCleanup.Parse(hash.GetValueOrDefault(CleanupField)) is { } cleanup)
                {
                    listed.UnionWith(cleanup.Delete);
                }

                List<string> waiting = [];
                foreach (ContinuationsSupportAttribute.Continuation continuation in
                         ContinuationsSupportAttribute.DeserializeContinuations(connection.GetJobParameter(stepJobId, ContinuationsSupportAttribute.ContinuationsParameter)))
                {
                    waiting.Add(continuation.JobId);
                }

                waiting.AddRange(FanInContinuationsSupportAttribute.ReadChildren(connection, stepJobId));

                BackgroundJobClient client = new(storage);
                int deleted = 0;

                foreach (string jobId in waiting.Distinct(StringComparer.Ordinal))
                {
                    if (string.IsNullOrEmpty(jobId) || listed.Contains(jobId) ||
                        !string.Equals(ReadChainId(connection, jobId), chainId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string? state = connection.GetStateData(jobId)?.Name;
                    if (state != null && HasNotStarted(state) &&
                        client.ChangeState(jobId, new DeletedState { Reason = RetryAbandonedReason(chainId) }, state))
                    {
                        deleted++;
                    }
                }

                if (deleted > 0)
                {
                    Logger.Info($"The retry of chain '{chainId}' was abandoned: {deleted} of the jobs it had created after background job '{stepJobId}' were deleted, and the jobs that waited for them with them.");
                }
            }
        }
        catch (DistributedLockTimeoutException)
        {
            // The chain is being retried, replaced or deleted, or looked at: what is left never runs
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"The jobs an abandoned retry of chain '{chainId}' had created couldn't be deleted. They never run: the chain's record doesn't list them.", ex);
        }
    }

    /// <summary>
    /// What the chain's record says about a step, with the generation of the record, read in one go: for a step
    /// that carries the marker of a retry, which is decided by both.
    /// </summary>
    internal static StepRecord ReadStepAndGeneration(IStorageConnection connection, string chainId, string stepKey, out int? generation)
    {
        generation = null;

        Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(GetKey(chainId));
        if (hash == null || !hash.TryGetValue(JobsField, out string? serialized) || serialized == null)
        {
            return default;
        }

        Dictionary<string, string> jobs = ParseJobs(serialized, out ChainLeaseTime? lease);

        // A record that was only started, for a chain that is being created, has no generation yet
        if (hash.ContainsKey(DefinitionField))
        {
            generation = ReadGeneration(hash);
        }

        return new StepRecord(true, jobs.Count > 0, jobs.GetValueOrDefault(stepKey), lease);
    }

    /// <summary>
    /// Holds the step a retry starts from: takes it out of its state, with the marker that says what the retry
    /// started from. Where the storage writes a job parameter with a state, both are one write. Elsewhere the
    /// marker comes first, under the job's lock and only while the job is still in the state the retry read: so
    /// no step is ever held without it, and a marker written for a step that somebody else moved meanwhile was
    /// seen by that state change, which took it off.
    /// </summary>
    /// <exception cref="InvalidOperationException">The step changed since the retry read it.</exception>
    private static void Hold(JobStorage storage, IBackgroundJobClientV2 client, IStorageConnection connection, string chainId, string stepJobId, StateData stepState, RetryMarker marker)
    {
        string serialized = marker.Serialize();
        RetryHold hold = new(JobClock.UtcNow + HoldTime, serialized) { Reason = ChainRetriedReason };

        if (!storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            using (connection.AcquireDistributedJobLock(stepJobId, CommitLockTimeout))
            {
                if (!string.Equals(connection.GetStateData(stepJobId)?.Name, stepState.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw Changed(chainId, stepJobId);
                }

                connection.SetJobParameter(stepJobId, ChainRetryParameter, serialized);
                hold.Marked = true;
            }
        }

        if (!client.ChangeState(stepJobId, hold, stepState.Name))
        {
            throw Changed(chainId, stepJobId);
        }

        // A client whose filters leave out JobChainStepFilter, which writes the marker with the state
        if (!hold.Marked)
        {
            connection.SetJobParameter(stepJobId, ChainRetryParameter, serialized);
        }
    }

    private static InvalidOperationException Changed(string chainId, string stepJobId)
    {
        return new InvalidOperationException($"Background job '{stepJobId}' of chain '{chainId}' changed meanwhile; try again.");
    }

    /// <summary>
    /// Whether the step a retry holds is still held for it, read under the job's lock: it is in the Scheduled
    /// state, or in the Enqueued state (its hold ended, and no worker took it yet), and still carries the marker
    /// of this retry. A worker that takes it afterwards decides by the record this retry is about to write. A
    /// step created again for the retry carries no marker: it is held while it is in the Scheduled state.
    /// </summary>
    private static bool IsStillHeld(IStorageConnection connection, string heldId, RetryMarker? marker)
    {
        string? state = connection.GetStateData(heldId)?.Name;

        if (marker == null)
        {
            return ScheduledState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);
        }

        if (!ScheduledState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) &&
            !EnqueuedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(RetryMarker.Parse(connection.GetJobParameter(heldId, ChainRetryParameter))?.Id, marker.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a server took the retry for abandoned and dealt with its step: put it back into the state it was
    /// in, or deleted the job created again for it, which the record didn't list. Read under the job's lock.
    /// </summary>
    private static bool WasPutBack(IStorageConnection connection, string chainId, string heldId)
    {
        StateData? state;
        using (connection.AcquireDistributedJobLock(heldId, CommitLockTimeout))
        {
            state = connection.GetStateData(heldId);
        }

        return IsRefused(chainId, state) || string.Equals(state?.Reason, RetryAbandonedReason(chainId), StringComparison.Ordinal);
    }

    /// <summary>
    /// Puts the step of a retry that failed back into the state it was in, from its hold, and only while the job
    /// is still held for this retry: it carries the marker of this retry, read under the job's lock, and the state
    /// change itself is refused, under that lock, when it doesn't any more (<see cref="RestoredState.HeldFor"/>).
    /// A step that left the hold another way is left as it is: a server put it back already, somebody deleted,
    /// requeued or scheduled it, it waits for its chain after it was requeued, or another retry holds it now. A
    /// step whose hold ended meanwhile waits in its queue with the note of the retry: it is left there, and the
    /// worker that takes it puts it back, because the retry isn't recorded (moved from here, it would leave its
    /// place in the queue behind).
    /// </summary>
    private static void PutBack(IBackgroundJobClient client, IStorageConnection connection, string jobId, StateData stepState, DateTime? expireAt, string chainId, RetryMarker marker)
    {
        IState state = Restore(stepState, expireAt, marker.Id);

        try
        {
            string? current = ReadHeld(connection, jobId, marker, out bool held);

            if (held && ScheduledState.StateName.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                if (client.ChangeState(jobId, state, ScheduledState.StateName))
                {
                    // Also where no filter did it with the state change; only this retry's own
                    ForgetRetryMarker(connection, jobId, marker.Id);
                    return;
                }

                current = ReadHeld(connection, jobId, marker, out held);
            }

            if (held && EnqueuedState.StateName.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"The hold of background job '{jobId}' of chain '{chainId}' ended while the retry was at it: the job waits in its queue, and the worker that takes it moves it back to {state.Name} instead of running it.");
                return;
            }

            Logger.Warn($"Background job '{jobId}' of chain '{chainId}' left the hold of the retry meanwhile (it is in the {current ?? "expired"} state{(held ? string.Empty : ", and isn't held for this retry any more")}), so it wasn't moved back to {state.Name}.");
        }
        catch (BackgroundJobClientException ex) when (ex.InnerException is RetryHoldLostException)
        {
            // Between the read above and the state change: the state change saw it under the job's lock
            Logger.Warn($"Background job '{jobId}' of chain '{chainId}' left the hold of the retry meanwhile, so it wasn't moved back to {state.Name}.");
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Background job '{jobId}' of chain '{chainId}' couldn't be moved back to {state.Name}; a server does it once its hold ends.", ex);
        }
    }

    // The state of a job, and whether it still carries the marker of a retry, both read under the job's lock
    private static string? ReadHeld(IStorageConnection connection, string jobId, RetryMarker marker, out bool held)
    {
        using (connection.AcquireDistributedJobLock(jobId, CommitLockTimeout))
        {
            string? state = connection.GetStateData(jobId)?.Name;
            held = state != null &&
                   string.Equals(RetryMarker.Parse(connection.GetJobParameter(jobId, ChainRetryParameter))?.Id, marker.Id, StringComparison.Ordinal);
            return state;
        }
    }
}
