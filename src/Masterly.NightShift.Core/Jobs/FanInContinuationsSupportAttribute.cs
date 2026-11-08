#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
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
    /// </remarks>
    public sealed class FanInContinuationsSupportAttribute : JobFilterAttribute, IElectStateFilter
    {
        /// <summary>The job parameter, on a parent, holding the identifiers of its fan-in children.</summary>
        public const string ParameterName = "FanInContinuations";

        private const int MaxDecideAttempts = 3;

        private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ContinuationInvalidTimeout = TimeSpan.FromMinutes(15);

        // How long a finished parent may spend deciding its children, waiting for a child's state to be stored and
        // between retries included. It runs on the thread that changed the parent's state (a worker, usually), which
        // can't observe shutdown here, so it is kept short. Replaceable for tests.
        internal static TimeSpan DecisionRoundBudget { get; set; } = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ChildStatePollInterval = TimeSpan.FromMilliseconds(100);

        private readonly ILog _logger = LogProvider.For<FanInContinuationsSupportAttribute>();
        private readonly HashSet<string> _knownFinalStates;
        private readonly IBackgroundJobStateChanger _stateChanger;

        /// <summary>Creates the filter with the built-in final states and a default state changer.</summary>
        public FanInContinuationsSupportAttribute()
            : this(new HashSet<string> { SucceededState.StateName, DeletedState.StateName }, new BackgroundJobStateChanger())
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
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.CandidateState is AwaitingJobsState awaiting)
            {
                RegisterChild(context, awaiting);
            }
            else if (_knownFinalStates.Contains(context.CandidateState.Name))
            {
                ExecuteChildrenIfExist(context);
            }
        }

        /// <summary>Reads the fan-in children of a job, in registration order.</summary>
        public static IReadOnlyList<string> ReadChildren([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            return Parse(connection.GetJobParameter(jobId, ParameterName));
        }

        internal static List<string> Parse(string? serialized)
        {
            if (String.IsNullOrEmpty(serialized)) return new List<string>();
            return SerializationHelper.Deserialize<List<string>>(serialized) ?? new List<string>();
        }

        private void RegisterChild(ElectStateContext context, AwaitingJobsState awaiting)
        {
            var connection = context.Connection;
            var childId = context.BackgroundJob.Id;

            // Every parent is checked before the child is registered on any, so a missing parent doesn't leave the
            // child's id on the parents before it, which would then wait for a job that is never created
            foreach (var parentId in awaiting.ParentIds)
            {
                EnsureParentExists(connection, parentId);
            }

            foreach (var parentId in awaiting.ParentIds)
            {
                using (connection.AcquireDistributedJobLock(parentId, LockTimeout))
                {
                    // Checked again under the lock: the parent may have expired since
                    EnsureParentExists(connection, parentId);

                    var children = Parse(connection.GetJobParameter(parentId, ParameterName));
                    if (!children.Contains(childId, StringComparer.Ordinal))
                    {
                        children.Add(childId);
                        connection.SetJobParameter(parentId, ParameterName, SerializationHelper.SerializeInternal(children));
                    }
                }
            }

            // A parent may have finished before our registration became visible to it: decide right away.
            var decision = Evaluate(awaiting, parentId => GetParentState(connection, parentId));

            if (decision != Decision.Undecided)
            {
                context.CandidateState = decision == Decision.Start
                    ? awaiting.NextState
                    : new DeletedState { Reason = "Continuation condition was not met" };

                return;
            }

            if (context.AfterCommit != null)
            {
                // A parent that finishes now, before this state is committed, may find this child without a state
                // and give up on it after a short wait. Decide again once the Awaiting state is committed: either
                // that parent saw it, or this check sees that parent finished. Also when the commit throws: it may
                // have gone through (a lost reply), and a retry finds the state stored and doesn't come back here.
                var storage = context.Storage;
                context.AfterCommit.AddRegardless(() => DecideChildAfterItsCommit(storage, connection, childId));
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
            var childState = connection.GetStateData(childId);
            if (childState == null || !AwaitingJobsState.StateName.Equals(childState.Name, StringComparison.OrdinalIgnoreCase)) return;

            var awaiting = AwaitingJobsState.FromStateData(childState.Data);
            if (awaiting == null) return;

            var decision = Evaluate(awaiting, parentId => GetParentState(connection, parentId));
            if (decision == Decision.Undecided) return;

            ChangeChildState(storage, connection, childId, awaiting, decision);
        }

        private void ExecuteChildrenIfExist(ElectStateContext context)
        {
            // The job is locked here, and a child registers itself under the same lock, so the list is complete:
            // a child that registers after the commit finds this job finished and decides by itself. The parameters
            // snapshot is read under the same lock, so a snapshot without the parameter means there are no children.
            string? serialized = null;

            var snapshot = context.BackgroundJob.ParametersSnapshot;
            if (snapshot != null)
            {
                snapshot.TryGetValue(ParameterName, out serialized);
            }
            else
            {
                serialized = context.Connection.GetJobParameter(context.BackgroundJob.Id, ParameterName);
            }

            var children = Parse(serialized).Distinct(StringComparer.Ordinal).ToList();
            if (children.Count == 0) return;

            var storage = context.Storage;
            var connection = context.Connection;
            var parentId = context.BackgroundJob.Id;

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
            var states = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                { parentId, context.CandidateState.Name },
            };

            var round = Stopwatch.StartNew();

            foreach (var childId in children)
            {
                DecideChild(storage, connection, parentId, childId, states, round);
            }
        }

        private void DecideChildrenAfterCommit(JobStorage storage, IStorageConnection connection, string parentId, List<string> children)
        {
            var states = new Dictionary<string, string?>(StringComparer.Ordinal);
            var round = Stopwatch.StartNew();

            // When the commit failed, or the job was requeued meanwhile, it isn't finished in storage: every child
            // would be undecided, so don't wait for their states
            if (!IsFinishedInStorage(connection, parentId, states)) return;

            foreach (var childId in children)
            {
                // The parent's state change is committed, so nothing retries this: retry a failing child a few
                // times, within the round's time budget, and don't let it stop the other children.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        DecideChild(storage, connection, parentId, childId, states, round);
                        break;
                    }
                    catch (Exception ex) when (ex.IsCatchableExceptionType())
                    {
                        var delay = TimeSpan.FromMilliseconds(500 * attempt);

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
                var state = GetParentState(connection, parentId, states);
                return state != null && _knownFinalStates.Contains(state);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Unknown: let the children be decided, with their retries
                _logger.DebugException($"Could not read the state of background job '{parentId}' before deciding its fan-in continuations.", ex);
                states.Clear();
                return true;
            }
        }

        private void DecideChild(JobStorage storage, IStorageConnection connection, string parentId, string childId, Dictionary<string, string?> states, Stopwatch round)
        {
            var childState = GetChildState(connection, parentId, childId, round);
            if (childState == null || !AwaitingJobsState.StateName.Equals(childState.Name, StringComparison.OrdinalIgnoreCase)) return;

            var awaiting = AwaitingJobsState.FromStateData(childState.Data);
            if (awaiting == null) return;

            var decision = Evaluate(awaiting, id => GetParentState(connection, id, states), parentId);
            if (decision == Decision.Undecided) return;

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

        /// <summary>Reads a parent's state once per decision round; children often share parents.</summary>
        private static string? GetParentState(IStorageConnection connection, string parentId, Dictionary<string, string?> states)
        {
            if (!states.TryGetValue(parentId, out var state))
            {
                state = GetParentState(connection, parentId);
                states[parentId] = state;
            }

            return state;
        }

        /// <summary>A parent that no longer exists counts as deleted, so it can't block the continuation forever.</summary>
        private static string? GetParentState(IStorageConnection connection, string parentId)
        {
            var state = connection.GetStateData(parentId);
            if (state != null) return state.Name;

            return connection.GetJobData(parentId) == null ? DeletedState.StateName : null;
        }

        private StateData? GetChildState(IStorageConnection connection, string parentId, string childId, Stopwatch round)
        {
            var firstAttempt = true;

            while (true)
            {
                var childData = connection.GetJobData(childId);
                if (childData == null)
                {
                    _logger.Warn($"Can not start fan-in continuation '{childId}' for background job '{parentId}': continuation does not exist.");
                    return null;
                }

                var state = connection.GetStateData(childId);
                if (state != null) return state;

                if (DateTime.UtcNow - childData.CreatedAt > ContinuationInvalidTimeout)
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
            if (finishedParentId != null)
            {
                foreach (var parentId in FinishedParentFirst(awaiting.ParentIds, finishedParentId))
                {
                    var stateName = getParentState(parentId);
                    if (stateName == null || !_knownFinalStates.Contains(stateName)) return Decision.Undecided;

                    var ok = Satisfies(stateName, awaiting.Options);
                    if (awaiting.Mode == FanInMode.Any && ok) return Decision.Start;
                    if (awaiting.Mode == FanInMode.All && !ok) return Decision.Skip;
                }

                // Every parent finished: all of them satisfy (All) or none does (Any)
                return awaiting.Mode == FanInMode.All ? Decision.Start : Decision.Skip;
            }

            var finished = 0;
            var satisfied = 0;

            foreach (var parentId in awaiting.ParentIds)
            {
                var stateName = getParentState(parentId);
                if (stateName == null || !_knownFinalStates.Contains(stateName)) continue;

                finished++;
                var ok = Satisfies(stateName, awaiting.Options);
                if (ok) satisfied++;

                if (awaiting.Mode == FanInMode.Any && ok) return Decision.Start;
                if (awaiting.Mode == FanInMode.All && !ok) return Decision.Skip;
            }

            if (finished < awaiting.ParentIds.Count) return Decision.Undecided;

            return awaiting.Mode == FanInMode.All
                ? (satisfied == finished ? Decision.Start : Decision.Skip)
                : (satisfied > 0 ? Decision.Start : Decision.Skip);
        }

        private static IEnumerable<string> FinishedParentFirst(IReadOnlyList<string> parentIds, string finishedParentId)
        {
            var found = false;
            for (var i = 0; i < parentIds.Count && !found; i++)
            {
                found = String.Equals(parentIds[i], finishedParentId, StringComparison.Ordinal);
            }

            if (found) yield return finishedParentId;

            for (var i = parentIds.Count - 1; i >= 0; i--)
            {
                if (!String.Equals(parentIds[i], finishedParentId, StringComparison.Ordinal)) yield return parentIds[i];
            }
        }

        private bool Satisfies(string stateName, JobContinuationOptions options)
        {
            if (options == JobContinuationOptions.OnAnyFinishedState)
            {
                return _knownFinalStates.Contains(stateName);
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
}
