using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    /// <summary>A state that takes a step out of the state it finished in, so nothing reacts to that state, while a retry is prepared.</summary>
    private static ScheduledState Hold(string reason) => new(JobClock.UtcNow + HoldTime) { Reason = reason };

    /// <summary>
    /// The state a held step goes back to when the change fails: the one it was in. With
    /// <paramref name="expireAt"/>, the time its job was to expire before it was held, the job expires then again,
    /// not a whole retention later. With <paramref name="heldFor"/>, the identifier of the retry that held the job,
    /// the state is applied only while the job still carries the note of that retry.
    /// </summary>
    private static IState Restore(StateData state, DateTime? expireAt = null, string? heldFor = null)
    {
        if (EnqueuedState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new EnqueuedState(state.Data.TryGetValue("Queue", out string? queue) ? queue : null) { Reason = state.Reason };
        }

        if (ScheduledState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) && state.Data.TryGetValue("EnqueueAt", out string? enqueueAt))
        {
            return new ScheduledState(JobHelper.DeserializeDateTime(enqueueAt)) { Reason = state.Reason };
        }

        return new RestoredState(state) { ExpireAt = expireAt, HeldFor = heldFor };
    }

    // Creates the steps of the levels from 'from' on that 'include' selects, level by level. 'jobs' has the job of
    // each step, read for the parents and written for the steps created; 'created' receives each job created, in
    // order, so a failure can delete them. 'levelAt' loads a level's steps when they are created, and no other.
    // 'whenLapsed' is called when the lease is found lapsed at a renewal: it throws when the chain can't go on.
    private static void CreateLevels(
        IBackgroundJobClientV2 client,
        int levelCount,
        Func<int, ChainLevel> levelAt,
        Dictionary<string, string> jobs,
        string chainId,
        int from,
        Func<int, bool> include,
        List<string> created,
        ChainLease lease,
        Action whenLapsed)
    {
        for (int level = from; level < levelCount; level++)
        {
            if (!include(level))
            {
                continue;
            }

            ChainLevel steps = levelAt(level);
            List<string> parents = ParentJobs(jobs, steps.Parent);

            for (int i = 0; i < steps.Steps.Count; i++)
            {
                // The client says it is still at it, now and then: creating a long chain may take a while. When
                // it didn't in time, a server may have taken the chain for abandoned: no point in going on then
                if (lease.RenewIfDue())
                {
                    whenLapsed();
                }

                // A parent that expired makes the creation throw InvalidOperationException
                ChainStep step = steps.Steps[i];
                string key = Key(level, i);
                string jobId = Create(client, step.Job, StateFor(step, parents), chainId, key, null);
                jobs[key] = jobId;
                created.Add(jobId);
            }
        }
    }

    /// <summary>
    /// Checks, after the lease lapsed and was renewed, that no server took the chain for abandoned meanwhile: none
    /// of the jobs was deleted because the record didn't list it. Each job is read under its lock, so a server that
    /// looked at it before the renewal has finished, and one that looks after it finds the lease.
    /// </summary>
    /// <exception cref="InvalidOperationException">A job was deleted, or is gone.</exception>
    private static void EnsureNoneRefused(IStorageConnection connection, string chainId, IEnumerable<string> jobIds, string change)
    {
        if (FirstRefused(connection, chainId, jobIds) is { } jobId)
        {
            throw Abandoned(chainId, jobId, change);
        }
    }

    /// <summary>The first of the jobs that a server deleted because the record didn't list it, or that is gone; each read under its lock.</summary>
    private static string? FirstRefused(IStorageConnection connection, string chainId, IEnumerable<string> jobIds)
    {
        foreach (string jobId in jobIds)
        {
            StateData? state;
            using (connection.AcquireDistributedJobLock(jobId, CommitLockTimeout))
            {
                state = connection.GetStateData(jobId);
            }

            if (IsRefused(chainId, state))
            {
                return jobId;
            }
        }

        return null;
    }

    // Deleted by a server because the record didn't list it, or as what an abandoned retry had created; or gone
    private static bool IsRefused(string chainId, StateData? state)
    {
        return state == null ||
               (DeletedState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) &&
                (JobChainStepFilter.IsRefusal(chainId, state.Reason) || string.Equals(state.Reason, RetryAbandonedReason(chainId), StringComparison.Ordinal)));
    }

    private static InvalidOperationException Abandoned(string chainId, string jobId, string change)
    {
        return new InvalidOperationException(
            $"Chain '{chainId}' wasn't {change}: its background job '{jobId}' was deleted before the chain was recorded. " +
            $"The client didn't confirm for {LeaseTime.TotalSeconds:0} seconds that it was still at it (a step took that long to create, or the process was paused), " +
            "so a server took the change for abandoned. Nothing of it is left; do it again.");
    }

    /// <summary>
    /// Deletes the jobs a failed change created, the last first so deleting one never starts another, each only
    /// from a state where it hasn't started. A job a worker took meanwhile doesn't run where
    /// <see cref="JobChainStepFilter"/> is in place: it waits while the lease lasts, and is deleted for not being
    /// listed. It is read again when it changed between its read and its deletion (a worker that put it on hold).
    /// </summary>
    /// <returns>Whether every job is deleted or gone.</returns>
    private static bool DeleteCreated(IBackgroundJobClient client, IStorageConnection connection, List<string> created, string chainId, string reason)
    {
        bool nothingLeft = true;

        for (int i = created.Count - 1; i >= 0; i--)
        {
            string jobId = created[i];
            try
            {
                string? state = connection.GetStateData(jobId)?.Name;
                bool deleted = false;

                for (int attempt = 0; attempt < 2 && !deleted && state != null && HasNotStarted(state); attempt++)
                {
                    deleted = client.ChangeState(jobId, new DeletedState { Reason = reason }, state);
                    if (!deleted)
                    {
                        state = connection.GetStateData(jobId)?.Name;
                    }
                }

                if (!deleted && state != null && !DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase))
                {
                    nothingLeft = false;
                    Logger.Warn($"Background job '{jobId}' of chain '{chainId}' is in the {state} state, so it wasn't deleted after the change of the chain failed.");
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                nothingLeft = false;
                Logger.WarnException($"Background job '{jobId}' of chain '{chainId}' couldn't be deleted after the change of the chain failed. The chain's record doesn't list it, so it is deleted when it comes due.", ex);
            }
        }

        return nothingLeft;
    }

    /// <summary>
    /// Does what an earlier retry or replacement of the chain left to do, before the chain changes again. What
    /// still can't be done stays in the record returned, for the next record written to carry it over.
    /// </summary>
    private static ChainRecord FinishPendingCleanup(IBackgroundJobClient client, IStorageConnection connection, ChainRecord record)
    {
        return record.Cleanup == null
            ? record
            : record with { Cleanup = RunCleanup(client, connection, record.ChainId, record.Cleanup, heldJustNow: false, out _) };
    }

    /// <summary>The jobs of the steps, the deepest steps first: deleted in that order, none starts the ones after it.</summary>
    private static IEnumerable<string> DeepestFirst(IEnumerable<KeyValuePair<string, string>> jobs)
    {
        return jobs
            .OrderByDescending(static x => ParseKey(x.Key).Level)
            .ThenByDescending(static x => ParseKey(x.Key).Index)
            .Select(static x => x.Value);
    }

    // The deepest steps first, so deleting a step never starts the continuations after it. Each step only from the
    // state it is read in, and only when canDelete accepts that state.
    private static int DeleteSteps(IBackgroundJobClient client, ChainRecord record, bool excludeRoot, string reason, Func<string, bool> canDelete, IStorageConnection connection)
    {
        int deleted = 0;

        foreach (KeyValuePair<string, string> job in record.Jobs.OrderByDescending(static x => ParseKey(x.Key).Level).ThenByDescending(static x => ParseKey(x.Key).Index))
        {
            if (excludeRoot && job.Key == Key(0, 0))
            {
                continue;
            }

            string? state = connection.GetStateData(job.Value)?.Name;
            if (state == null || SucceededState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
                DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!canDelete(state))
            {
                continue;
            }

            if (client.ChangeState(job.Value, new DeletedState { Reason = reason }, state))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>The keys of the steps still live: not succeeded, deleted or expired.</summary>
    private static List<string> LiveSteps(ChainRecord record, IStorageConnection connection, out bool processing)
    {
        List<string> live = [];
        processing = false;

        foreach (KeyValuePair<string, string> job in record.Jobs)
        {
            string? state = connection.GetStateData(job.Value)?.Name;
            if (state == null || SucceededState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
                DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            live.Add(job.Key);
            processing |= IsProcessing(state);
        }

        return live;
    }

    private static bool HasNotStarted(string state)
    {
        return AwaitingState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
               EnqueuedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
               ScheduledState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAwaiting(string? state) => AwaitingState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);

    private static bool IsProcessing(string? state) => ProcessingState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A state stored before, applied again with its data: a step that a retry which failed, or was abandoned,
    /// puts back into the state it was in. It isn't a new event. Its handlers and the filters that keep things in
    /// step with the state go by its name, as for the original state: the job expires again as it did, and its
    /// counters are right. The filters that react to an outcome go by the type of the state, or leave it out by
    /// name: no automatic retry, no alert, no monitor, no outcome counted (<see cref="StatisticsHistoryAttribute"/>),
    /// no entry in the activity feed, because the step was told about when it reached the state the first time.
    /// A continuation that started then isn't waiting any more, so it doesn't start again.
    /// </summary>
    internal sealed class RestoredState(StateData state) : IState
    {
        /// <summary>
        /// How long a job that is put back shortly before, or after, the time it was to expire is kept at least:
        /// long enough to see what became of it.
        /// </summary>
        internal static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);

        /// <summary>
        /// When the job was to expire before it left this state, if it was: it expires then again
        /// (<see cref="JobChainStepFilter"/> sets the time left), where it would otherwise be kept for a whole
        /// retention from now, as a job that finishes is.
        /// </summary>
        public DateTime? ExpireAt { get; init; }

        /// <summary>
        /// The identifier of the retry whose hold this state ends, when the client of that retry asks for it: the
        /// state is applied only while the job still carries the note of that retry, which
        /// <see cref="JobChainStepFilter"/> checks under the job's lock. Otherwise the state change fails, and the
        /// job is left to whoever moved it since: a server that put it back, somebody who requeued it, another
        /// retry that holds it.
        /// </summary>
        public string? HeldFor { get; init; }

        public string Name { get; } = state.Name;

        public string? Reason { get; } = state.Reason;

        public bool IsFinal { get; } = SucceededState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) ||
                                       DeletedState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase);

        public bool IgnoreJobLoadException => false;

        public Dictionary<string, string> SerializeData() => new(state.Data, StringComparer.Ordinal);
    }
}
