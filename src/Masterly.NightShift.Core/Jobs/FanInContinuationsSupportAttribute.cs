using System.Diagnostics;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>
/// Drives fan-in continuations: jobs created in <see cref="AwaitingJobsState"/> wait for a whole set of parents
/// (<see cref="FanInMode.All"/>) or for the first of them (<see cref="FanInMode.Any"/>). Registered globally
/// by default, next to <see cref="ContinuationsSupportAttribute"/>.
/// </summary>
/// <remarks>
/// Each parent keeps the identifiers of its fan-in children in the <c>FanInContinuations</c> job parameter.
/// When a parent reaches a final state, its children are re-evaluated once that state is committed, or once the
/// commit throws, because it may have gone through: the
/// committed states of the child's parents are read and, once the condition is decided, the child moves to its
/// next state or is deleted with the reason "Continuation condition was not met". Because every parent reads
/// the others only after its own commit, when the last parents finish at the same moment at least one of them
/// sees all of them finished. The child is changed under its own lock and only from the Awaiting state, so it
/// starts once even when several parents decide it. A parent spends at most a few seconds deciding its children;
/// a child whose own Awaiting state is still being stored then is left to decide itself, which it does once that
/// state is committed.
/// <para>
/// A child may wait for longer than the jobs of the parents that finished first are kept. So a parent that
/// finishes (succeeds, is deleted, or fails for good) writes what it finished as for each of its children, in the
/// transaction of that state change: one field, the parent's id, in the hash <c>fan-in:{child}</c>
/// (<see cref="GetOutcomesKey"/>). That costs the same for each parent however many the child waits for, and is
/// never lost while the state is stored. A parent whose job is gone then counts as what it finished as. While the
/// job of a parent exists, its state decides, so a parent that was requeued counts as not finished, and replaces
/// its field when it finishes again. Only a parent of which nothing was written counts as deleted once its job is
/// gone, so it can't block the child forever. The hash goes with the state change that takes the child out of
/// the Awaiting state, whoever makes it; a field a parent writes for a child decided meanwhile is removed by that
/// parent once its state is committed.
/// </para>
/// </remarks>
public sealed class FanInContinuationsSupportAttribute : JobFilterAttribute, IElectStateFilter, IApplyStateFilter
{
    /// <summary>The job parameter, on a parent, holding the identifiers of its fan-in children.</summary>
    public const string ParameterName = "FanInContinuations";

    /// <summary>
    /// The job parameter on a fan-in continuation that says what its parents finished as is kept, in the hash it
    /// names (<see cref="GetOutcomesKey"/>): one field a parent, the parent's id, with the name of its state.
    /// </summary>
    public const string OutcomesParameterName = "FanInOutcomes";

    private const string OutcomesKeyPrefix = "fan-in:";

    private const int MaxDecideAttempts = 3;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ContinuationInvalidTimeout = TimeSpan.FromMinutes(15);

    // How long the write of what a parent finished as, for a child stored after it finished, waits for the
    // parent's lock: a parent is locked only while its state changes, which writes it anyway
    private static readonly TimeSpan OutcomeLockTimeout = TimeSpan.FromSeconds(5);

    // What the states read in a round keep for a parent whose job is gone: what it counts as depends on the child
    private const string Gone = "\0gone";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(FanInContinuationsSupportAttribute));

    // How long a finished parent may spend deciding its children, waiting for a child's state to be stored and
    // between retries included. It runs on the thread that changed the parent's state (a worker, usually), which
    // can't observe shutdown here, so it is kept short. Replaceable for tests.
    internal static TimeSpan DecisionRoundBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether a parent writes what it finished as for its children. Replaceable for tests, which measure its cost.</summary>
    internal static bool KeepsOutcomes { get; set; } = true;
    private static readonly TimeSpan ChildStatePollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILog _logger = LogProvider.For<FanInContinuationsSupportAttribute>();
    private readonly HashSet<string> _knownFinalStates;
    private readonly IBackgroundJobStateChanger _stateChanger;

    /// <summary>Creates the filter with the built-in final states and a default state changer.</summary>
    public FanInContinuationsSupportAttribute()
        : this([SucceededState.StateName, DeletedState.StateName], new BackgroundJobStateChanger())
    {
    }

    /// <summary>Creates the filter.</summary>
    public FanInContinuationsSupportAttribute(
        [NotNull] HashSet<string> knownFinalStates,
        [NotNull] IBackgroundJobStateChanger stateChanger)
    {
        _knownFinalStates = knownFinalStates ?? throw new ArgumentNullException(nameof(knownFinalStates));
        _stateChanger = stateChanger ?? throw new ArgumentNullException(nameof(stateChanger));

        // After ContinuationsSupportAttribute, so both see the final candidate state.
        Order = 1001;
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateState is AwaitingJobsState awaiting)
        {
            RegisterChild(context, awaiting);
        }
        else if (_knownFinalStates.Contains(context.CandidateState.Name) ||
                 FailedState.StateName.Equals(context.CandidateState.Name, StringComparison.OrdinalIgnoreCase))
        {
            // A final failure (no retry left: this filter runs after AutomaticRetryAttribute) finishes a parent for
            // the modes that count failures; the other modes leave the child undecided
            ExecuteChildrenIfExist(context);
        }
    }

    /// <summary>Reads the fan-in children of a job, in registration order.</summary>
    public static IReadOnlyList<string> ReadChildren([NotNull] IStorageConnection connection, [NotNull] string jobId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobId);

        return Parse(connection.GetJobParameter(jobId, ParameterName));
    }

    internal static List<string> Parse(string? serialized)
    {
        if (string.IsNullOrEmpty(serialized))
        {
            return [];
        }

        return SerializationHelper.Deserialize<List<string>>(serialized) ?? [];
    }

    private void RegisterChild(ElectStateContext context, AwaitingJobsState awaiting)
    {
        IStorageConnection connection = context.Connection;
        string childId = context.BackgroundJob.Id;

        // Every parent is checked before the child is registered on any, so a missing parent doesn't leave the
        // child's id on the parents before it, which would then wait for a job that is never created
        foreach (string parentId in awaiting.ParentIds)
        {
            EnsureParentExists(connection, parentId);
        }

        foreach (string parentId in awaiting.ParentIds)
        {
            using (connection.AcquireDistributedJobLock(parentId, LockTimeout))
            {
                // Checked again under the lock: the parent may have expired since
                EnsureParentExists(connection, parentId);

                List<string> children = Parse(connection.GetJobParameter(parentId, ParameterName));
                if (!children.Contains(childId, StringComparer.Ordinal))
                {
                    children.Add(childId);
                    connection.SetJobParameter(parentId, ParameterName, SerializationHelper.SerializeInternal(children));
                }
            }
        }

        // A parent may have finished before our registration became visible to it: decide right away.
        Decision decision = Evaluate(awaiting, parentId => GetParentState(connection, parentId));

        if (decision != Decision.Undecided)
        {
            context.CandidateState = decision == Decision.Start
                ? awaiting.NextState
                : new DeletedState { Reason = "Continuation condition was not met" };

            return;
        }

        // It waits: what its parents finish as is kept for it, and goes with the state change that ends the wait
        MarkKeepsOutcomes(context, childId);

        if (context.AfterCommit != null)
        {
            // A parent that finishes now, before this state is committed, may find this child without a state
            // and give up on it after a short wait. Decide again once the Awaiting state is committed: either
            // that parent saw it, or this check sees that parent finished. Also when the commit throws: it may
            // have gone through (a lost reply), and a retry finds the state stored and doesn't come back here.
            JobStorage storage = context.Storage;
            context.AfterCommit.AddRegardless(() => DecideChildAfterItsCommit(storage, connection, childId));
        }
    }

    /// <summary>The hash that keeps what the parents of a fan-in continuation finished as, by parent.</summary>
    internal static string GetOutcomesKey(string childId) => OutcomesKeyPrefix + childId;

    // With the state that has the child wait, where the storage writes a parameter with a state
    private static void MarkKeepsOutcomes(ElectStateContext context, string childId)
    {
        string key = GetOutcomesKey(childId);

        if (context.Transaction is JobStorageTransaction transaction && context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            transaction.SetJobParameter(childId, OutcomesParameterName, key);
        }
        else
        {
            context.Connection.SetJobParameter(childId, OutcomesParameterName, key);
        }
    }

    private static void EnsureParentExists(IStorageConnection connection, string parentId)
    {
        if (connection.GetJobData(parentId) == null)
        {
            throw new InvalidOperationException(
                $"Can not add a fan-in continuation: parent background job '{parentId}' does not exist.");
        }
    }

    private void DecideChildAfterItsCommit(JobStorage storage, IStorageConnection connection, string childId)
    {
        // Without a state, the commit failed: nothing to decide, the creation is retried or abandoned
        StateData? childState = connection.GetStateData(childId);
        if (childState == null || !AwaitingJobsState.StateName.Equals(childState.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AwaitingJobsState? awaiting = AwaitingJobsState.FromStateData(childState.Data);
        if (awaiting == null)
        {
            return;
        }

        // The parents that finished before this child was stored never tell it: their outcome is written here,
        // should the child still wait when their jobs are gone
        List<string>? finished = null;
        RecordedOutcomes recorded = new(connection, childId);
        Decision decision = Evaluate(awaiting, parentId =>
        {
            string? state = recorded.StateOf(parentId, ReadParentState(connection, parentId));
            if (state != null && IsOutcome(state, _knownFinalStates))
            {
                (finished ??= []).Add(parentId);
            }

            return state;
        });

        if (decision == Decision.Undecided)
        {
            if (finished != null && KeepsOutcomes)
            {
                RecordOutcomes(connection, childId, finished, _knownFinalStates, OutcomeLockTimeout);
            }

            return;
        }

        ChangeChildState(storage, connection, childId, awaiting, decision);
    }

    private void ExecuteChildrenIfExist(ElectStateContext context)
    {
        // The job is locked here, and a child registers itself under the same lock, so the list is complete:
        // a child that registers after the commit finds this job finished and decides by itself. The parameters
        // snapshot is read under the same lock, so a snapshot without the parameter means there are no children.
        string? serialized = null;

        IReadOnlyDictionary<string, string>? snapshot = context.BackgroundJob.ParametersSnapshot;
        if (snapshot != null)
        {
            snapshot.TryGetValue(ParameterName, out serialized);
        }
        else
        {
            serialized = context.Connection.GetJobParameter(context.BackgroundJob.Id, ParameterName);
        }

        List<string> children = [.. Parse(serialized).Distinct(StringComparer.Ordinal)];
        if (children.Count == 0)
        {
            return;
        }

        JobStorage storage = context.Storage;
        IStorageConnection connection = context.Connection;
        string parentId = context.BackgroundJob.Id;

        if (context.AfterCommit != null)
        {
            // Decided once this state is committed, from committed states only. Deciding now, before the
            // commit, lets the last two parents finishing together each see the other one unfinished. Also
            // when the commit throws: it may have gone through (a lost reply), and the caller's retry then finds
            // this job finished already and changes nothing. If it really failed, this job isn't finished in
            // storage and nothing is decided.
            context.AfterCommit.AddRegardless(() => DecideChildrenAfterCommit(storage, connection, parentId, children));
            return;
        }

        // Nobody runs actions after this commit (the transaction belongs to the caller, or a custom state
        // changer applies the state): decide now, counting this job in its candidate state.
        Dictionary<string, string?> states = new(StringComparer.Ordinal)
        {
            { parentId, context.CandidateState.Name },
        };

        Stopwatch round = Stopwatch.StartNew();

        foreach (string childId in children)
        {
            DecideChild(storage, connection, parentId, childId, states, round, afterCommit: false);
        }
    }

    private void DecideChildrenAfterCommit(JobStorage storage, IStorageConnection connection, string parentId, List<string> children)
    {
        Dictionary<string, string?> states = new(StringComparer.Ordinal);
        Stopwatch round = Stopwatch.StartNew();

        // When the commit failed, or the job was requeued meanwhile, it isn't finished in storage: every child
        // would be undecided, so don't wait for their states
        if (!IsFinishedInStorage(connection, parentId, states))
        {
            return;
        }

        foreach (string childId in children)
        {
            // The parent's state change is committed, so nothing retries this: retry a failing child a few
            // times, within the round's time budget, and don't let it stop the other children.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    DecideChild(storage, connection, parentId, childId, states, round);
                    break;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    TimeSpan delay = TimeSpan.FromMilliseconds(500 * attempt);

                    if (attempt >= MaxDecideAttempts || round.Elapsed + delay > DecisionRoundBudget)
                    {
                        _logger.ErrorException(
                            $"Could not decide fan-in continuation '{childId}' after background job '{parentId}' finished. It stays Awaiting until another of its parents finishes, or until it is requeued or deleted.",
                            ex);
                        break;
                    }

                    _logger.DebugException(
                        $"Attempt {attempt} of {MaxDecideAttempts} to decide fan-in continuation '{childId}' after background job '{parentId}' finished failed, see inner exception for details.",
                        ex);

                    states.Clear();
                    Thread.Sleep(delay);
                }
            }
        }
    }

    private bool IsFinishedInStorage(IStorageConnection connection, string parentId, Dictionary<string, string?> states)
    {
        try
        {
            string? state = ReadParentState(connection, parentId, states);
            return state != null && (IsGone(state) || IsOutcome(state, _knownFinalStates));
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Unknown: let the children be decided, with their retries
            _logger.DebugException($"Could not read the state of background job '{parentId}' before deciding its fan-in continuations.", ex);
            states.Clear();
            return true;
        }
    }

    // 'afterCommit': the parent's state is committed, with what it finished as written for each of its children
    private void DecideChild(
        JobStorage storage,
        IStorageConnection connection,
        string parentId,
        string childId,
        Dictionary<string, string?> states,
        Stopwatch round,
        bool afterCommit = true)
    {
        StateData? childState = GetChildState(connection, parentId, childId, round, out bool childGone);
        if (childState == null || !AwaitingJobsState.StateName.Equals(childState.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Decided meanwhile, or gone: the field this parent wrote for it isn't removed by anything else
            if (afterCommit && KeepsOutcomes && (childGone || childState != null))
            {
                ForgetOutcomes(connection, childId);
            }

            return;
        }

        AwaitingJobsState? awaiting = AwaitingJobsState.FromStateData(childState.Data);
        if (awaiting == null)
        {
            return;
        }

        RecordedOutcomes recorded = new(connection, childId);
        Decision decision = Evaluate(awaiting, id => recorded.StateOf(id, ReadParentState(connection, id, states)), parentId);
        if (decision == Decision.Undecided)
        {
            return;
        }

        ChangeChildState(storage, connection, childId, awaiting, decision);
    }

    private void ChangeChildState(JobStorage storage, IStorageConnection connection, string childId, AwaitingJobsState awaiting, Decision decision)
    {
        IState nextState = decision == Decision.Start
            ? awaiting.NextState
            : new DeletedState { Reason = "Continuation condition was not met" };

        // Under the child's lock and only from Awaiting: when two parents decide the same child, one wins
        _stateChanger.ChangeState(new StateChangeContext(
            storage,
            connection,
            childId,
            nextState,
            AwaitingJobsState.StateName));
    }

    /// <summary>
    /// Reads a parent's state once per decision round; children often share parents. <see cref="Gone"/> for a
    /// parent whose job no longer exists.
    /// </summary>
    private static string? ReadParentState(IStorageConnection connection, string parentId, Dictionary<string, string?> states)
    {
        if (!states.TryGetValue(parentId, out string? state))
        {
            state = ReadParentState(connection, parentId);
            states[parentId] = state;
        }

        return state;
    }

    /// <summary>
    /// The name of a parent's state, <c>null</c> for a parent that has none yet, and <see cref="Gone"/> for one
    /// whose job no longer exists.
    /// </summary>
    private static string? ReadParentState(IStorageConnection connection, string parentId)
    {
        StateData? state = connection.GetStateData(parentId);
        if (state != null)
        {
            return state.Name;
        }

        return connection.GetJobData(parentId) == null ? Gone : null;
    }

    /// <summary>
    /// What a parent counts as for a child that isn't stored yet: its state, and deleted once its job no longer
    /// exists.
    /// </summary>
    internal static string? GetParentState(IStorageConnection connection, string parentId)
    {
        string? state = ReadParentState(connection, parentId);
        return IsGone(state) ? DeletedState.StateName : state;
    }

    /// <summary>
    /// What a parent counts as for a caller that must not decide from what it doesn't know (a server that decides
    /// a waiting step of a chain again): the state of its job while the job exists; once it is gone, the outcome
    /// the parent wrote for the child when it finished, which <paramref name="written"/> reads; and not finished
    /// (<c>null</c>) when it wrote none.
    /// </summary>
    internal static string? GetKnownParentState(IStorageConnection connection, string parentId, Func<Dictionary<string, string>> written)
    {
        string? state = ReadParentState(connection, parentId);
        if (!IsGone(state))
        {
            return state;
        }

        return written().TryGetValue(parentId, out string? outcome) ? outcome : null;
    }

    /// <summary>What the parents of a waiting child finished as, as they wrote it, by parent.</summary>
    internal static Dictionary<string, string> ReadOutcomes(IStorageConnection connection, string childId)
    {
        Dictionary<string, string>? outcomes = connection.GetAllEntriesFromHash(GetOutcomesKey(childId));
        return outcomes == null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(outcomes, StringComparer.Ordinal);
    }

    /// <summary>Whether a state is one a parent finishes in: a final state, or a failure for good.</summary>
    internal static bool IsOutcome(string stateName, HashSet<string> knownFinalStates)
    {
        return knownFinalStates.Contains(stateName) || IsFailed(stateName);
    }

    /// <summary>
    /// Writes what the given parents finished as for a child, for the ones that are finished: each under the
    /// parent's lock, from the state it has then, one field each. For a child stored after some of its parents
    /// finished, which their state changes didn't know of, and for a server that finds one missing. A parent
    /// whose lock is busy is left out: it is changing state, and that change writes what it finishes as. Removes
    /// the hash again when the child stopped waiting meanwhile. Bookkeeping: never throws.
    /// </summary>
    /// <returns>Whether every one that is finished was written.</returns>
    internal static bool RecordOutcomes(
        IStorageConnection connection,
        string childId,
        IReadOnlyList<string> parentIds,
        HashSet<string> knownFinalStates,
        TimeSpan lockTimeout)
    {
        string key = GetOutcomesKey(childId);
        bool all = true;

        foreach (string parentId in parentIds)
        {
            try
            {
                using (connection.AcquireDistributedJobLock(parentId, lockTimeout))
                {
                    string? state = ReadParentState(connection, parentId);
                    if (state == null || IsGone(state) || !IsOutcome(state, knownFinalStates))
                    {
                        continue;
                    }

                    using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                    transaction.SetRangeInHash(key, [new KeyValuePair<string, string>(parentId, state)]);
                    transaction.Commit();
                }
            }
            catch (DistributedLockTimeoutException)
            {
                // The parent is changing state: that change writes what it finishes as
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                all = false;
                Logger.WarnException(
                    $"What background job '{parentId}' finished as couldn't be written for fan-in continuation '{childId}'. Should the continuation still wait when the job is gone, it counts as deleted then; for a step of a chain, a server that looks at the chain writes it again.",
                    ex);
            }
        }

        try
        {
            // Decided meanwhile: what was written goes, as with the state change that decided it
            if (!AwaitingJobsState.StateName.Equals(connection.GetJobData(childId)?.State, StringComparison.OrdinalIgnoreCase))
            {
                ForgetOutcomes(connection, childId);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"Whether fan-in continuation '{childId}' still waits couldn't be read.", ex);
        }

        return all;
    }

    // Removes what the parents of a child that doesn't wait any more wrote for it. Best effort.
    private static void ForgetOutcomes(IStorageConnection connection, string childId)
    {
        try
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.RemoveHash(GetOutcomesKey(childId));
            transaction.Commit();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"What the parents of fan-in continuation '{childId}' finished as couldn't be removed.", ex);
        }
    }

    /// <summary>
    /// In the transaction of a state change: a parent that finishes writes what it finished as for each of its
    /// children, and a child that stops waiting takes what its parents wrote with it. Bookkeeping: a write that
    /// can't be queued is logged, never thrown.
    /// </summary>
    void IApplyStateFilter.OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transaction);

        try
        {
            string jobId = context.BackgroundJob.Id;

            if (KeepsOutcomes && IsOutcome(context.NewState.Name, _knownFinalStates))
            {
                KeyValuePair<string, string>[] outcome = [new(jobId, context.NewState.Name)];
                foreach (string childId in ReadChildren(context).Distinct(StringComparer.Ordinal))
                {
                    transaction.SetRangeInHash(GetOutcomesKey(childId), outcome);
                }
            }

            if (AwaitingJobsState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase) &&
                !AwaitingJobsState.StateName.Equals(context.NewState.Name, StringComparison.OrdinalIgnoreCase) &&
                KeepsOutcomesOf(context))
            {
                transaction.RemoveHash(GetOutcomesKey(jobId));
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _logger.WarnException($"What background job '{context.BackgroundJob.Id}' finished as couldn't be kept for its fan-in continuations, or theirs couldn't be removed.", ex);
        }
    }

    void IApplyStateFilter.OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    // The children of a job, from the parameters loaded with it where the storage loads them
    private static List<string> ReadChildren(ApplyStateContext context)
    {
        IReadOnlyDictionary<string, string>? snapshot = context.BackgroundJob.ParametersSnapshot;
        if (snapshot != null)
        {
            return snapshot.TryGetValue(ParameterName, out string? serialized) ? Parse(serialized) : [];
        }

        return Parse(context.Connection.GetJobParameter(context.BackgroundJob.Id, ParameterName));
    }

    // Whether the job was registered as a fan-in continuation, whose parents keep what they finished as for it
    private static bool KeepsOutcomesOf(ApplyStateContext context)
    {
        IReadOnlyDictionary<string, string>? snapshot = context.BackgroundJob.ParametersSnapshot;
        return snapshot != null
            ? snapshot.ContainsKey(OutcomesParameterName)
            : !string.IsNullOrEmpty(context.Connection.GetJobParameter(context.BackgroundJob.Id, OutcomesParameterName));
    }

    private static bool IsGone(string? state) => ReferenceEquals(state, Gone);

    /// <summary>What the parents of a waiting child wrote for it, read once, and only when the job of a parent turns out to be gone.</summary>
    private sealed class RecordedOutcomes(IStorageConnection connection, string childId)
    {
        private Dictionary<string, string>? _outcomes;

        /// <summary>What a parent counts as for the child, from what was read of the parent's job.</summary>
        public string? StateOf(string parentId, string? read)
        {
            if (!IsGone(read))
            {
                return read;
            }

            _outcomes ??= ReadOutcomes(connection, childId);
            return _outcomes.TryGetValue(parentId, out string? outcome) ? outcome : DeletedState.StateName;
        }
    }

    // 'gone' says that the child's job doesn't exist (any more)
    private StateData? GetChildState(IStorageConnection connection, string parentId, string childId, Stopwatch round, out bool gone)
    {
        bool firstAttempt = true;
        gone = false;

        while (true)
        {
            JobData? childData = connection.GetJobData(childId);
            if (childData == null)
            {
                gone = true;
                _logger.Warn($"Can not start fan-in continuation '{childId}' for background job '{parentId}': continuation does not exist.");
                return null;
            }

            StateData? state = connection.GetStateData(childId);
            if (state != null)
            {
                return state;
            }

            if (JobClock.UtcNow - childData.CreatedAt > ContinuationInvalidTimeout)
            {
                _logger.Warn($"Fan-in continuation '{childId}' has been ignored: it was deemed to be aborted, because its state is still non-initialized.");
                return null;
            }

            if (round.Elapsed >= DecisionRoundBudget)
            {
                _logger.Warn($"Fan-in continuation '{childId}' was not decided after background job '{parentId}' finished, because its state is still being stored. It decides itself once that state is committed (when its creator runs actions after the commit, as the default client does), or when another of its parents finishes.");
                return null;
            }

            Thread.Sleep(firstAttempt ? TimeSpan.Zero : ChildStatePollInterval);
            firstAttempt = false;
        }
    }

    internal enum Decision { Undecided, Start, Skip }

    /// <summary>
    /// Decides the outcome from the parents' current state names (<c>null</c> means "not finished yet").
    /// </summary>
    /// <param name="awaiting">The child's state.</param>
    /// <param name="getParentState">Reads a parent's state name.</param>
    /// <param name="finishedParentId">
    /// The parent that just finished and triggers the decision, or <c>null</c> when the child registers itself.
    /// A finished parent reads itself first, then the others from the last to the first (later parents are
    /// usually the ones still running), and stops at the first one that hasn't finished. That only postpones
    /// the decision: a parent that finished and decides the child alone (the first satisfying one in
    /// <see cref="FanInMode.Any"/>, the first unsatisfying one in <see cref="FanInMode.All"/>) did so when it
    /// finished, and the last parent to finish reads every parent. A registering child reads every parent,
    /// because parents that finished before it never decide it.
    /// </param>
    internal Decision Evaluate(AwaitingJobsState awaiting, Func<string, string?> getParentState, string? finishedParentId = null)
    {
        return Evaluate(awaiting.Mode, awaiting.Options, awaiting.ParentIds, getParentState, _knownFinalStates, finishedParentId);
    }

    /// <summary>
    /// The decision itself, from the fan-in's mode and options and the parents' state names alone: what this filter
    /// decides when a parent finishes, and what tells whether a waiting step of a chain is about to start.
    /// </summary>
    internal static Decision Evaluate(
        FanInMode mode,
        JobContinuationOptions options,
        IReadOnlyList<string> parentIds,
        Func<string, string?> getParentState,
        HashSet<string> knownFinalStates,
        string? finishedParentId = null)
    {
        // "Any" modes start at the first parent that satisfies; "all" modes skip at the first that doesn't
        bool anyMode = mode is FanInMode.Any or FanInMode.AnyFailed or FanInMode.AnyFailedOrCanceled;

        if (finishedParentId != null)
        {
            foreach (string parentId in FinishedParentFirst(parentIds, finishedParentId))
            {
                string? stateName = getParentState(parentId);
                if (stateName == null || !IsFinished(stateName, mode, knownFinalStates))
                {
                    return Decision.Undecided;
                }

                bool ok = Satisfies(stateName, mode, options, knownFinalStates);
                if (anyMode && ok)
                {
                    return Decision.Start;
                }

                if (!anyMode && !ok)
                {
                    return Decision.Skip;
                }
            }

            // Every parent finished: all of them satisfy ("all" modes) or none does ("any" modes)
            return anyMode ? Decision.Skip : Decision.Start;
        }

        int finished = 0;
        int satisfied = 0;

        foreach (string parentId in parentIds)
        {
            string? stateName = getParentState(parentId);
            if (stateName == null || !IsFinished(stateName, mode, knownFinalStates))
            {
                continue;
            }

            finished++;
            bool ok = Satisfies(stateName, mode, options, knownFinalStates);
            if (ok)
            {
                satisfied++;
            }

            if (anyMode && ok)
            {
                return Decision.Start;
            }

            if (!anyMode && !ok)
            {
                return Decision.Skip;
            }
        }

        if (finished < parentIds.Count)
        {
            return Decision.Undecided;
        }

        return anyMode
            ? (satisfied > 0 ? Decision.Start : Decision.Skip)
            : (satisfied == finished ? Decision.Start : Decision.Skip);
    }

    // The modes that count failures count a parent that failed for good as finished
    private static bool IsFinished(string stateName, FanInMode mode, HashSet<string> knownFinalStates)
    {
        return knownFinalStates.Contains(stateName) ||
               (mode is FanInMode.AnyFailed or FanInMode.AnyFailedOrCanceled or FanInMode.AllCompleted && IsFailed(stateName));
    }

    private static bool IsFailed(string stateName) => FailedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);

    private static bool Satisfies(string stateName, FanInMode mode, JobContinuationOptions options, HashSet<string> knownFinalStates)
    {
        return mode switch
        {
            FanInMode.AnyFailed => IsFailed(stateName),
            FanInMode.AnyFailedOrCanceled => IsFailed(stateName) || DeletedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase),
            FanInMode.AllCompleted => true,
            _ => Satisfies(stateName, options, knownFinalStates),
        };
    }

    private static IEnumerable<string> FinishedParentFirst(IReadOnlyList<string> parentIds, string finishedParentId)
    {
        bool found = false;
        for (int i = 0; i < parentIds.Count && !found; i++)
        {
            found = string.Equals(parentIds[i], finishedParentId, StringComparison.Ordinal);
        }

        if (found)
        {
            yield return finishedParentId;
        }

        for (int i = parentIds.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(parentIds[i], finishedParentId, StringComparison.Ordinal))
            {
                yield return parentIds[i];
            }
        }
    }

    private static bool Satisfies(string stateName, JobContinuationOptions options, HashSet<string> knownFinalStates)
    {
        if (options == JobContinuationOptions.OnAnyFinishedState)
        {
            return knownFinalStates.Contains(stateName);
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnSucceededState) &&
            SucceededState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return options.HasFlag(JobContinuationOptions.OnlyOnDeletedState) &&
               DeletedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);
    }
}
