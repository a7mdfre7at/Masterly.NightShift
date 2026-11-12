namespace Masterly.NightShift;

/// <summary>A step of a stored chain and the job that runs it now.</summary>
/// <param name="Key">The step key, <c>level.index</c>.</param>
/// <param name="JobId">The step's current job; another one after <c>RetryChainFrom</c>.</param>
/// <param name="State">The job's state name, or <c>null</c> when the job expired.</param>
/// <param name="Level">The level of the step.</param>
/// <param name="ParentLevel">The level it waits for, -1 for the first step.</param>
/// <param name="Job">The step's method call, as the chain defines it.</param>
public sealed record JobChainStepInfo(string Key, string JobId, string? State, int Level, int ParentLevel, Job? Job = null)
{
    /// <summary>How the step waits for its level's parent, for the dashboard's flowchart.</summary>
    internal ChainLink Link { get; init; }

    /// <summary>The continuation options of a <c>Then</c> or branch step.</summary>
    internal JobContinuationOptions Options { get; init; }

    /// <summary>The kind of the step's level: the first step, a <c>Then</c>, a group of branches or of joins.</summary>
    internal ChainLevelKind LevelKind { get; init; }
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
    /// included, failed for good, or expired), or waits behind a step that failed for good, directly or through
    /// steps that wait too, which only a requeue or <c>RetryChainFrom</c> moves on.
    /// </summary>
    public bool IsSettled
    {
        get
        {
            HashSet<int> blocked = BlockedLevels();
            return Steps.All(step => JobChains.IsSettledState(step.State) || (IsAwaiting(step.State) && blocked.Contains(step.Level)));
        }
    }

    /// <summary>The number of steps that failed for good.</summary>
    public int FailedCount => Steps.Count(static x => IsFailed(x.State));

    /// <summary>
    /// The levels whose waiting steps can't start by themselves: the level they wait for has a step that failed for
    /// good, or is blocked itself and still has a step waiting.
    /// </summary>
    private HashSet<int> BlockedLevels()
    {
        SortedDictionary<int, int> parents = [];
        HashSet<int> failed = [];
        HashSet<int> waiting = [];

        foreach (JobChainStepInfo step in Steps)
        {
            parents.TryAdd(step.Level, step.ParentLevel);
            if (IsFailed(step.State)) failed.Add(step.Level);
            if (IsAwaiting(step.State)) waiting.Add(step.Level);
        }

        // A level always comes after the one it waits for, so the levels are decided in order
        HashSet<int> blocked = [];
        foreach ((int level, int parent) in parents)
        {
            if (parent < 0) continue;
            if (failed.Contains(parent) || (blocked.Contains(parent) && waiting.Contains(parent))) blocked.Add(level);
        }

        return blocked;
    }

    private static bool IsFailed(string? state) => FailedState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);

    private static bool IsAwaiting(string? state) => AwaitingState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);
}
