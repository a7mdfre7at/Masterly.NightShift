namespace Masterly.NightShift;

/// <summary>A step of a stored chain and the job that runs it now.</summary>
/// <param name="Key">The step key, <c>level.index</c>.</param>
/// <param name="JobId">The step's current job; another one after <c>RetryChainFrom</c>.</param>
/// <param name="State">The job's state name, or <c>null</c> when the job expired.</param>
/// <param name="Level">The level of the step.</param>
/// <param name="ParentLevel">The level it waits for, -1 for the first step.</param>
/// <param name="Job">
/// The step's method call, as the chain defines it, or <c>null</c> when it wasn't read or its method can't be
/// loaded any more (renamed or removed since the chain was created).
/// </param>
public sealed record JobChainStepInfo(string Key, string JobId, string? State, int Level, int ParentLevel, Job? Job = null)
{
    /// <summary>How the step waits for its level's parent, for the dashboard's flowchart.</summary>
    internal ChainLink Link { get; init; }

    /// <summary>The continuation options of a <c>Then</c> or branch step.</summary>
    internal JobContinuationOptions Options { get; init; }

    /// <summary>The kind of the step's level: the first step, a <c>Then</c>, a group of branches or of joins.</summary>
    internal ChainLevelKind LevelKind { get; init; }

    /// <summary>When the step reached the Succeeded, Failed or Deleted state it is in, or <c>null</c> when that isn't known.</summary>
    internal DateTime? FinishedAt { get; init; }

    /// <summary>When a step in the Scheduled state is due, or <c>null</c> for another state, or when its state doesn't say.</summary>
    internal DateTime? DueAt { get; init; }

    /// <summary>
    /// For a step that waits for several steps, one of whose jobs is gone: what the steps it waits for finished
    /// as, by job, as they wrote it on this step's job (<see cref="FanInContinuationsSupportAttribute.OutcomesParameterName"/>).
    /// </summary>
    internal IReadOnlyDictionary<string, string>? Outcomes { get; init; }
}

/// <summary>A stored chain, as <c>GetChain</c> reads it.</summary>
/// <param name="ChainId">The chain's identifier.</param>
/// <param name="Generation">1, raised by each <c>RetryChainFrom</c> and <c>ReplaceChain</c>.</param>
/// <param name="CreatedAt">When the chain was created, UTC.</param>
/// <param name="DeleteBehavior">What <c>DeleteChain</c> does while a step is processing.</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record JobChainInfo(string ChainId, int Generation, DateTime CreatedAt, ChainDeleteBehavior DeleteBehavior, IReadOnlyList<JobChainStepInfo> Steps)
{
    /// <summary>
    /// Whether the chain has nothing left to do by itself: every step finished (succeeded, deleted, skipped steps
    /// included, failed for good, or expired), or waits behind a step that failed for good or expired, directly or
    /// through steps that wait too, which only a requeue or <c>RetryChainFrom</c> moves on. A waiting step that the
    /// steps it waits for already decide (it starts, or is skipped) is about to be told, so the chain isn't settled
    /// until it was; a step still not told five minutes after they finished never will be by them, and counts as
    /// settled, so a chain is never unsettled for good. A server starts such a step later (the chain maintenance
    /// looks at every chain that has steps waiting), and the chain then runs again.
    /// </summary>
    public bool IsSettled
    {
        get
        {
            ILookup<int, JobChainStepInfo>? levels = null;
            List<int>? undecided = null;
            HashSet<int>? neverTold = null;

            foreach (JobChainStepInfo step in Steps)
            {
                if (JobChains.IsSettledState(step.State))
                {
                    continue;
                }

                if (!IsAwaiting(step.State))
                {
                    return false;
                }

                levels ??= Steps.ToLookup(static x => x.Level);
                switch (Classify(step, levels))
                {
                    case Waiting.AboutToBeTold:
                        return false;

                    case Waiting.NeverTold:
                        (neverTold ??= []).Add(step.Level);
                        break;

                    default:
                        (undecided ??= []).Add(step.Level);
                        break;
                }
            }

            if (undecided == null)
            {
                return true;
            }

            // Nothing decides these yet: they wait for steps still to finish, unless one of them can't
            HashSet<int> blocked = BlockedLevels(neverTold);
            return undecided.TrueForAll(blocked.Contains);
        }
    }

    /// <summary>
    /// The waiting steps that the steps they wait for decided longer ago than <see cref="JobChains.DecisionWindow"/>
    /// and that still wait: the process that was to start or skip them stopped, or couldn't.
    /// </summary>
    internal List<JobChainStepInfo> NeverToldSteps()
    {
        List<JobChainStepInfo> steps = [];
        ILookup<int, JobChainStepInfo>? levels = null;

        foreach (JobChainStepInfo step in Steps)
        {
            if (!IsAwaiting(step.State))
            {
                continue;
            }

            levels ??= Steps.ToLookup(static x => x.Level);
            if (Classify(step, levels) == Waiting.NeverTold)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    /// <summary>
    /// Whether a step still waits for the steps before it. Such a step is started by the process that finishes the
    /// last of them, which may stop first: a server looks at the chain again for as long as one waits.
    /// </summary>
    internal bool HasWaitingSteps()
    {
        foreach (JobChainStepInfo step in Steps)
        {
            if (IsAwaiting(step.State))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the job of a step still exists in a state other than Succeeded or Deleted: a step that failed for
    /// good and is kept, or one that waits, is scheduled, enqueued or processing. The chain's record is kept for as
    /// long as there is one, so the chain can be read and run again from that step.
    /// </summary>
    internal bool HasLiveSteps()
    {
        foreach (JobChainStepInfo step in Steps)
        {
            if (step.State != null &&
                !SucceededState.StateName.Equals(step.State, StringComparison.OrdinalIgnoreCase) &&
                !DeletedState.StateName.Equals(step.State, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether something of the chain runs at <paramref name="now"/> or is about to: a step is enqueued or
    /// processing, a scheduled step is due, or a waiting step is about to be told to start. A chain that isn't
    /// moving stays as it is until a scheduled step comes due (<see cref="NextDueAt"/>) or somebody moves one of
    /// its steps.
    /// </summary>
    internal bool IsMoving(DateTime now)
    {
        ILookup<int, JobChainStepInfo>? levels = null;

        foreach (JobChainStepInfo step in Steps)
        {
            if (JobChains.IsSettledState(step.State))
            {
                continue;
            }

            if (IsAwaiting(step.State))
            {
                levels ??= Steps.ToLookup(static x => x.Level);
                if (Classify(step, levels) == Waiting.AboutToBeTold)
                {
                    return true;
                }
            }
            else if (!IsScheduled(step.State) || step.DueAt == null || step.DueAt.Value <= now)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>When the first of the steps scheduled for later than <paramref name="now"/> is due, or <c>null</c> when none is.</summary>
    internal DateTime? NextDueAt(DateTime now)
    {
        DateTime? next = null;

        foreach (JobChainStepInfo step in Steps)
        {
            if (IsScheduled(step.State) && step.DueAt is { } dueAt && dueAt > now && (next == null || dueAt < next.Value))
            {
                next = dueAt;
            }
        }

        return next;
    }

    /// <summary>When the last of the scheduled steps is due, or <c>null</c> when no step is scheduled.</summary>
    internal DateTime? LastDueAt()
    {
        DateTime? last = null;

        foreach (JobChainStepInfo step in Steps)
        {
            if (IsScheduled(step.State) && step.DueAt is { } dueAt && (last == null || dueAt > last.Value))
            {
                last = dueAt;
            }
        }

        return last;
    }

    /// <summary>When a step of the chain last finished, as far as the states of the steps say, or when the chain was created.</summary>
    internal DateTime LastMovedAt()
    {
        DateTime last = CreatedAt;

        foreach (JobChainStepInfo step in Steps)
        {
            if (step.FinishedAt is { } finishedAt && finishedAt > last)
            {
                last = finishedAt;
            }
        }

        return last;
    }

    /// <summary>The number of steps that failed for good.</summary>
    public int FailedCount => Steps.Count(static x => IsFailed(x.State));

    /// <summary>
    /// The levels whose waiting steps can't start by themselves: the level they wait for has a step that failed for
    /// good or whose job expired (a failed job kept for a while expires too, and nothing starts what waits for it),
    /// has a step that was never told to start (<paramref name="neverTold"/>), or is blocked itself and still has
    /// a step waiting.
    /// </summary>
    private HashSet<int> BlockedLevels(HashSet<int>? neverTold)
    {
        SortedDictionary<int, int> parents = [];
        HashSet<int> failed = [];
        HashSet<int> waiting = [];

        foreach (JobChainStepInfo step in Steps)
        {
            parents.TryAdd(step.Level, step.ParentLevel);
            if (IsFailed(step.State) || step.State == null)
            {
                failed.Add(step.Level);
            }

            if (IsAwaiting(step.State))
            {
                waiting.Add(step.Level);
            }
        }

        // A level always comes after the one it waits for, so the levels are decided in order
        HashSet<int> blocked = [];
        foreach ((int level, int parent) in parents)
        {
            if (parent < 0)
            {
                continue;
            }

            if (failed.Contains(parent) || neverTold?.Contains(parent) == true || (blocked.Contains(parent) && waiting.Contains(parent)))
            {
                blocked.Add(level);
            }
        }

        return blocked;
    }

    /// <summary>
    /// Whether the states of the steps a waiting step waits for decide it already. It then starts, or is skipped,
    /// as soon as the step that finished last has told it, a moment after that step's state is stored. Past
    /// <see cref="JobChains.DecisionWindow"/> that moment is over: the step was never told.
    /// </summary>
    private static Waiting Classify(JobChainStepInfo step, ILookup<int, JobChainStepInfo> levels)
    {
        List<JobChainStepInfo> parents = [.. levels[step.ParentLevel]];

        if (JobChains.Decide(step, parents) == FanInContinuationsSupportAttribute.Decision.Undecided)
        {
            return Waiting.Undecided;
        }

        // A step whose job is gone finished long ago: it decides only through what it wrote on this step
        DateTime? decidedAt = parents.Max(static x => x.State == null ? DateTime.MinValue : x.FinishedAt);
        return decidedAt == null || JobClock.UtcNow - decidedAt.Value < JobChains.DecisionWindow ? Waiting.AboutToBeTold : Waiting.NeverTold;
    }

    private enum Waiting
    {
        Undecided,
        AboutToBeTold,
        NeverTold,
    }

    private static bool IsFailed(string? state) => FailedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);

    private static bool IsAwaiting(string? state) => AwaitingState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);

    private static bool IsScheduled(string? state) => ScheduledState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);
}
