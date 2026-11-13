namespace Masterly.NightShift;

/// <summary>
/// A workflow of jobs created together: a first step, then steps that continue it one after the other, in parallel
/// branches, or after a group of branches (joins).
/// <code>
/// JobChainResult? chain = JobChain
///     .StartWith&lt;Orders&gt;(x => x.Process(orderId))
///     .Then&lt;Invoices&gt;(x => x.Render(orderId))
///     .ThenMany(branches => branches
///         .Branch&lt;Mailer&gt;(x => x.Send(orderId))
///         .Branch&lt;Erp&gt;(x => x.Post(orderId)))
///     .ThenAfterAll&lt;Notifier&gt;(x => x.Done(orderId))
///     .ThenAfterAnyFailed&lt;Ops&gt;(x => x.Alert(orderId))
///     .Enqueue();
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Each step is an ordinary job, created when the chain is: the first in the Enqueued or Scheduled state, the
/// others waiting for the steps before them as continuations (<c>ContinueJobWith</c>) or fan-in continuations
/// (<c>ContinueJobWithAll</c> and its join modes). A step after a single step continues it; a step after a group of
/// branches waits for the whole group. Consecutive join calls (<see cref="ThenAfterAll(System.Linq.Expressions.Expression{Action}, string)"/>,
/// <c>ThenAfterAny</c>, <c>ThenAfterAnyFailed</c>, <c>ThenAfterAnyFailedOrCanceled</c>, <c>ThenAfterAllCompleted</c>)
/// all wait for the same group, so a chain can have a success path and a failure path. A step that the outcome of
/// the steps before it doesn't satisfy is deleted with the reason "Continuation condition was not met".
/// </para>
/// <para>
/// Retries and timeouts of a step come from its method's attributes (<see cref="AutomaticRetryAttribute"/>,
/// <see cref="JobTimeoutAttribute"/>), as for any job. Each step carries the <c>ChainId</c> and <c>ChainStep</c> job
/// parameters, and the chain is recorded in the storage hash <c>chain:{id}</c>, kept for as long as a step is left to
/// run or failed for good, and for <see cref="RecordRetention"/> after that, so it can be read (<c>GetChain</c>), run
/// again from a step (<c>RetryChainFrom</c>), replaced before it starts (<c>ReplaceChain</c>) or deleted
/// (<c>DeleteChain</c>).
/// </para>
/// <para>
/// A step runs only when the record lists its job (<see cref="JobChainStepFilter"/>): the jobs of a chain whose
/// creation stopped halfway, of a deleted chain, and the jobs a retry or a replacement replaced are deleted instead
/// of running. The record is started before the first job is created, saying that the chain is being created, and
/// lists the steps once the last one is created; a first step that a worker takes before that waits for it. The
/// same holds while a chain is retried or replaced: none of its steps starts until the change is recorded or
/// given up, and a change that stops halfway leaves the chain as it was. Creating a chain may take as long as it
/// needs: nothing depends on the age of its jobs.
/// </para>
/// </remarks>
public sealed partial class JobChain
{
    /// <summary>The most steps a chain may have.</summary>
    public const int MaxSteps = 1000;

    /// <summary>The most levels (the first step, each <c>Then</c>, each group of branches or of joins) a chain may have.</summary>
    public const int MaxLevels = 50;

    /// <summary>The shortest time <see cref="RecordRetention"/> can be set to: a day.</summary>
    public static readonly TimeSpan MinRecordRetention = TimeSpan.FromDays(1);

    private static long _recordRetentionTicks = TimeSpan.FromDays(30).Ticks;

    /// <summary>
    /// How long the record of a chain is kept once none of its steps is left to run or to run again, on storages
    /// that expire hashes: 30 days unless <c>UseChainRetention</c> sets another time. It counts from the last state
    /// change of a step, or from the time a scheduled step is due. While the job of a step still exists in a state
    /// other than Succeeded or Deleted (a step that failed for good and is kept, a step that waits), the record is
    /// kept, however long that lasts. The jobs themselves expire as usual.
    /// </summary>
    public static TimeSpan RecordRetention
    {
        get => new(Volatile.Read(ref _recordRetentionTicks));
        internal set => Volatile.Write(ref _recordRetentionTicks, value.Ticks);
    }

    private readonly List<ChainLevel> _levels = [];

    private JobChain(Job first)
    {
        _levels.Add(new ChainLevel(-1, ChainLevelKind.Root, [new ChainStep(first, ChainLink.Root, JobContinuationOptions.OnlyOnSucceededState)]));
    }

    /// <summary>What deleting the chain with <c>DeleteChain</c> does while a step is processing.</summary>
    public ChainDeleteBehavior DeleteBehavior { get; private set; } = ChainDeleteBehavior.Cascade;

    /// <summary>The number of steps.</summary>
    public int StepCount => _levels.Sum(static x => x.Steps.Count);

    internal IReadOnlyList<ChainLevel> Levels => _levels;

    /// <summary>Sets <see cref="DeleteBehavior"/>.</summary>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain OnDelete(ChainDeleteBehavior behavior)
    {
        DeleteBehavior = Enum.IsDefined(behavior) ? behavior : throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Not a ChainDeleteBehavior value.");
        return this;
    }

    /// <summary>Adds a level of parallel branches, each continuing the level before it.</summary>
    /// <param name="branches">Adds the branches, at least one.</param>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain ThenMany([NotNull, InstantHandle] Action<JobChainBranches> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);

        JobChainBranches group = new();
        branches(group);

        if (group.Steps.Count == 0)
        {
            throw new ArgumentException("A group needs at least one branch.", nameof(branches));
        }

        foreach (ChainStep step in group.Steps)
        {
            CheckFanIn(step.Options, nameof(branches));
        }

        return AddLevel(ChainLevelKind.Branches, group.Steps);
    }

    /// <summary>Creates the chain with the first step enqueued, through <see cref="JobStorage.Current"/>.</summary>
    /// <returns>The chain's identifiers, or <c>null</c> when a client filter or a schedule policy didn't let the first step be created.</returns>
    public JobChainResult? Enqueue(ScheduleOptions? options = null) => Enqueue(new BackgroundJobClient(), options);

    /// <summary>Creates the chain with the first step enqueued.</summary>
    /// <param name="client">Creates the jobs. It must implement <see cref="IBackgroundJobClientV2"/>, as <see cref="BackgroundJobClient"/> does.</param>
    /// <param name="options">Options of the first step: its schedule policy, late-start limit and queue.</param>
    /// <returns>The chain's identifiers, or <c>null</c> when a client filter or a schedule policy didn't let the first step be created.</returns>
    /// <exception cref="InvalidOperationException">
    /// A filter canceled the creation of a later step, or this process stalled for so long while it created the
    /// chain that a server took it for abandoned and deleted one of its jobs; the steps already created are deleted.
    /// </exception>
    public JobChainResult? Enqueue([NotNull] IBackgroundJobClient client, ScheduleOptions? options = null)
    {
        return JobChains.Create(this, client, new EnqueuedState(), options, chainId: null);
    }

    /// <summary>Creates the chain with the first step due after a delay, through <see cref="JobStorage.Current"/>.</summary>
    /// <returns>The chain's identifiers, or <c>null</c> when a client filter or a schedule policy didn't let the first step be created.</returns>
    public JobChainResult? Schedule(TimeSpan delay, ScheduleOptions? options = null) => Schedule(new BackgroundJobClient(), delay, options);

    /// <inheritdoc cref="Enqueue(IBackgroundJobClient, ScheduleOptions)"/>
    public JobChainResult? Schedule([NotNull] IBackgroundJobClient client, TimeSpan delay, ScheduleOptions? options = null)
    {
        return JobChains.Create(this, client, new ScheduledState(delay), options, chainId: null);
    }

    /// <summary>Creates the chain with the first step due at a moment, through <see cref="JobStorage.Current"/>.</summary>
    /// <returns>The chain's identifiers, or <c>null</c> when a client filter or a schedule policy didn't let the first step be created.</returns>
    public JobChainResult? Schedule(DateTimeOffset enqueueAt, ScheduleOptions? options = null) => Schedule(new BackgroundJobClient(), enqueueAt, options);

    /// <inheritdoc cref="Enqueue(IBackgroundJobClient, ScheduleOptions)"/>
    public JobChainResult? Schedule([NotNull] IBackgroundJobClient client, DateTimeOffset enqueueAt, ScheduleOptions? options = null)
    {
        return JobChains.Create(this, client, new ScheduledState(enqueueAt.UtcDateTime), options, chainId: null);
    }

    private static JobChain Start(Job job) => new(job);

    private JobChain AddThen(Job job, JobContinuationOptions options)
    {
        CheckOptions(options, nameof(options));
        CheckFanIn(options, nameof(options));
        return AddLevel(ChainLevelKind.Then, [new ChainStep(job, ChainLink.Then, options)]);
    }

    // A step after a level of several steps is a fan-in continuation, which can't wait for every outcome
    private void CheckFanIn(JobContinuationOptions options, string parameterName)
    {
        if (_levels[^1].Steps.Count > 1)
        {
            JobChains.FanInFor(options, parameterName);
        }
    }

    // Consecutive joins wait for the same level: the one before the first of them
    private JobChain AddJoin(Job job, ChainLink link)
    {
        ChainLevel last = _levels[^1];
        if (last.Kind == ChainLevelKind.Joins)
        {
            CheckStepLimit(1);
            last.Steps.Add(new ChainStep(job, link, JobContinuationOptions.OnlyOnSucceededState));
            return this;
        }

        return AddLevel(ChainLevelKind.Joins, [new ChainStep(job, link, JobContinuationOptions.OnlyOnSucceededState)]);
    }

    private JobChain AddLevel(ChainLevelKind kind, List<ChainStep> steps)
    {
        if (_levels.Count >= MaxLevels)
        {
            throw new InvalidOperationException($"A chain has at most {MaxLevels} levels.");
        }

        CheckStepLimit(steps.Count);

        _levels.Add(new ChainLevel(_levels.Count - 1, kind, steps));
        return this;
    }

    private void CheckStepLimit(int adding)
    {
        if (StepCount + adding > MaxSteps)
        {
            throw new InvalidOperationException($"A chain has at most {MaxSteps} steps.");
        }
    }

    internal static void CheckOptions(JobContinuationOptions options, string parameterName)
    {
        const JobContinuationOptions all = JobContinuationOptions.OnlyOnSucceededState | JobContinuationOptions.OnlyOnDeletedState |
                                           JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnProcessingState;
        if ((options & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, options, "Not a combination of JobContinuationOptions flags.");
        }
    }

    /// <summary>Rebuilds a chain from its definition, as <see cref="JobChains.ToDefinition"/> wrote it.</summary>
    internal static JobChain FromLevels(IReadOnlyList<ChainLevel> levels, ChainDeleteBehavior deleteBehavior)
    {
        JobChain chain = new(levels[0].Steps[0].Job) { DeleteBehavior = deleteBehavior };
        chain._levels.Clear();
        chain._levels.AddRange(levels);
        return chain;
    }
}

/// <summary>The parallel branches of a <see cref="JobChain.ThenMany"/> level.</summary>
public sealed partial class JobChainBranches
{
    internal List<ChainStep> Steps { get; } = [];

    private JobChainBranches AddBranch(Job job, JobContinuationOptions options)
    {
        JobChain.CheckOptions(options, nameof(options));
        Steps.Add(new ChainStep(job, ChainLink.Then, options));
        return this;
    }
}

/// <summary>What <c>DeleteChain</c> does with a chain that has a step processing.</summary>
public enum ChainDeleteBehavior
{
    /// <summary>Deletes every step that hasn't finished, the processing ones included.</summary>
    Cascade = 0,

    /// <summary>Refuses to delete the chain while a step is processing.</summary>
    Restrict = 1,
}

/// <summary>The jobs of a chain that was created.</summary>
/// <param name="ChainId">The chain's identifier.</param>
/// <param name="RootId">The first step's job.</param>
/// <param name="Steps">The job of each step, by step key: <c>level.index</c>, <c>0.0</c> for the first step.</param>
public sealed record JobChainResult(string ChainId, string RootId, IReadOnlyDictionary<string, string> Steps);

/// <summary>How a step waits for the level before it.</summary>
internal enum ChainLink
{
    Root,
    Then,
    AfterAll,
    AfterAny,
    AfterAnyFailed,
    AfterAnyFailedOrCanceled,
    AfterAllCompleted,
}

internal enum ChainLevelKind
{
    Root,
    Then,
    Branches,
    Joins,
}

internal sealed record ChainStep(Job Job, ChainLink Link, JobContinuationOptions Options);

/// <summary>A level of a chain: its steps all wait for every step of the level <see cref="Parent"/>.</summary>
internal sealed class ChainLevel(int parent, ChainLevelKind kind, List<ChainStep> steps)
{
    /// <summary>The index of the level the steps wait for, -1 for the first level.</summary>
    public int Parent { get; } = parent;

    public ChainLevelKind Kind { get; } = kind;

    /// <summary>The steps; joins added one after the other go to the same level.</summary>
    public List<ChainStep> Steps { get; } = steps;
}
