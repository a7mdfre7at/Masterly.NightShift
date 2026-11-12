using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    // How long a step stays held while a chain changes: long enough for the change, short enough that a process
    // stopping in the middle of it doesn't hold the step for good (the delayed job scheduler enqueues it then)
    private static readonly TimeSpan HoldTime = TimeSpan.FromHours(1);

    /// <summary>A state that keeps a step from running, and from deciding its continuations, while a chain changes.</summary>
    private static ScheduledState Hold(string reason) => new(JobClock.UtcNow + HoldTime) { Reason = reason };

    /// <summary>The state a held step goes back to when the change fails: the one it was in.</summary>
    private static IState Restore(StateData state)
    {
        if (EnqueuedState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new EnqueuedState(state.Data.TryGetValue("Queue", out string? queue) ? queue : null) { Reason = state.Reason };
        }

        if (ScheduledState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) && state.Data.TryGetValue("EnqueueAt", out string? enqueueAt))
        {
            return new ScheduledState(JobHelper.DeserializeDateTime(enqueueAt)) { Reason = state.Reason };
        }

        return new RestoredState(state);
    }

    // Creates the steps of the levels from 'from' on that 'include' selects, level by level. 'jobs' has the job of
    // each step, read for the parents and written for the steps created; 'created' receives each job created, in
    // order, so a failure can delete them.
    private static void CreateLevels(
        IBackgroundJobClientV2 client,
        IReadOnlyList<ChainLevel> levels,
        Dictionary<string, string> jobs,
        string chainId,
        int from,
        Func<int, bool> include,
        List<string> created)
    {
        for (int level = from; level < levels.Count; level++)
        {
            if (!include(level)) continue;

            List<string> parents = ParentJobs(levels, jobs, levels[level].Parent);
            for (int i = 0; i < levels[level].Steps.Count; i++)
            {
                // A parent that expired makes the creation throw InvalidOperationException
                ChainStep step = levels[level].Steps[i];
                string key = Key(level, i);
                string jobId = Create(client, step.Job, StateFor(step, parents), chainId, key, null);
                jobs[key] = jobId;
                created.Add(jobId);
            }
        }
    }

    /// <summary>
    /// Deletes the jobs a failed change created, the last first so deleting one never starts another, each only
    /// from a state where it hasn't started. A job a worker took meanwhile doesn't run where
    /// <see cref="JobChainStepFilter"/> is in place: it waits for the record, and is deleted for lack of one. It is
    /// read again when it changed between its read and its deletion (a worker that put it on hold).
    /// </summary>
    private static void DeleteCreated(IBackgroundJobClient client, IStorageConnection connection, List<string> created, string chainId, string reason)
    {
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
                    if (!deleted) state = connection.GetStateData(jobId)?.Name;
                }

                if (!deleted && state != null && !DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"Background job '{jobId}' of chain '{chainId}' started before the change of the chain failed, so it wasn't deleted.");
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Background job '{jobId}' of chain '{chainId}' couldn't be deleted after the change of the chain failed.", ex);
            }
        }
    }

    /// <summary>Moves a held step out of its hold; a step the delayed job scheduler enqueued meanwhile is left as it is.</summary>
    private static void Release(IBackgroundJobClient client, string jobId, IState state, string chainId)
    {
        try
        {
            if (!client.ChangeState(jobId, state, ScheduledState.StateName))
            {
                Logger.Warn($"Background job '{jobId}' of chain '{chainId}' left its hold meanwhile, so it wasn't moved to {state.Name}.");
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Background job '{jobId}' of chain '{chainId}' couldn't be moved to {state.Name}; it runs once its hold ends.", ex);
        }
    }

    /// <summary>
    /// Does what an earlier retry or replacement of the chain left to do, before the chain changes again. What
    /// still can't be done stays in the record returned, for the next record written to carry it over.
    /// </summary>
    private static ChainRecord FinishPendingCleanup(IBackgroundJobClient client, IStorageConnection connection, ChainRecord record)
    {
        return record.Cleanup == null
            ? record
            : record with { Cleanup = RunCleanup(client, connection, record.ChainId, record.Cleanup, out _) };
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
            if (excludeRoot && job.Key == Key(0, 0)) continue;

            string? state = connection.GetStateData(job.Value)?.Name;
            if (state == null || SucceededState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase) ||
                DeletedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!canDelete(state)) continue;

            if (client.ChangeState(job.Value, new DeletedState { Reason = reason }, state)) deleted++;
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

    private static bool IsProcessing(string? state) => ProcessingState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A state stored before, applied again with its data: a failed or deleted step a failed retry puts back. Its
    /// handlers and filters go by its name, as for the original state; the filters that count outcomes
    /// (<see cref="StatisticsHistoryAttribute"/>) leave it out, because the step was counted when it reached the
    /// state the first time.
    /// </summary>
    internal sealed class RestoredState(StateData state) : IState
    {
        public string Name { get; } = state.Name;

        public string? Reason { get; } = state.Reason;

        public bool IsFinal { get; } = SucceededState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase) ||
                                       DeletedState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase);

        public bool IgnoreJobLoadException => false;

        public Dictionary<string, string> SerializeData() => new(state.Data, StringComparer.Ordinal);
    }
}
