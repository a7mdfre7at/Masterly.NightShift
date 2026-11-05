// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

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
    /// When a parent reaches a final state, every child is re-evaluated: the states of all its parents are read
    /// and, once the condition is decided, the child moves to its next state or is deleted with the reason
    /// "Continuation condition was not met".
    /// </remarks>
    public sealed class FanInContinuationsSupportAttribute : JobFilterAttribute, IElectStateFilter
    {
        /// <summary>The job parameter, on a parent, holding the identifiers of its fan-in children.</summary>
        public const string ParameterName = "FanInContinuations";

        private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ContinuationStateFetchTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ContinuationInvalidTimeout = TimeSpan.FromMinutes(15);

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

            foreach (var parentId in awaiting.ParentIds)
            {
                using (connection.AcquireDistributedJobLock(parentId, LockTimeout))
                {
                    if (connection.GetJobData(parentId) == null)
                    {
                        throw new InvalidOperationException(
                            $"Can not add a fan-in continuation: parent background job '{parentId}' does not exist.");
                    }

                    var children = Parse(connection.GetJobParameter(parentId, ParameterName));
                    if (!children.Contains(childId, StringComparer.Ordinal))
                    {
                        children.Add(childId);
                        connection.SetJobParameter(parentId, ParameterName, SerializationHelper.Serialize(children));
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
            }
        }

        private void ExecuteChildrenIfExist(ElectStateContext context)
        {
            string? serialized = null;

            if (context.BackgroundJob.ParametersSnapshot == null ||
                !context.BackgroundJob.ParametersSnapshot.TryGetValue(ParameterName, out serialized))
            {
                serialized = context.Connection.GetJobParameter(context.BackgroundJob.Id, ParameterName);
            }

            var children = Parse(serialized);
            if (children.Count == 0) return;

            foreach (var childId in children.Distinct(StringComparer.Ordinal))
            {
                var childState = GetChildState(context, childId);
                if (childState == null || !AwaitingJobsState.StateName.Equals(childState.Name, StringComparison.OrdinalIgnoreCase)) continue;

                var awaiting = AwaitingJobsState.FromStateData(childState.Data);
                if (awaiting == null) continue;

                var decision = Evaluate(awaiting, parentId =>
                    String.Equals(parentId, context.BackgroundJob.Id, StringComparison.Ordinal)
                        ? context.CandidateState.Name // our own state isn't applied yet: use the candidate
                        : GetParentState(context.Connection, parentId));

                if (decision == Decision.Undecided) continue;

                IState nextState = decision == Decision.Start
                    ? awaiting.NextState
                    : new DeletedState { Reason = "Continuation condition was not met" };

                _stateChanger.ChangeState(new StateChangeContext(
                    context.Storage,
                    context.Connection,
                    childId,
                    nextState,
                    AwaitingJobsState.StateName));
            }
        }

        /// <summary>A parent that no longer exists counts as deleted, so it can't block the continuation forever.</summary>
        private static string? GetParentState(IStorageConnection connection, string parentId)
        {
            var state = connection.GetStateData(parentId);
            if (state != null) return state.Name;

            return connection.GetJobData(parentId) == null ? DeletedState.StateName : null;
        }

        private StateData? GetChildState(ElectStateContext context, string childId)
        {
            var started = Stopwatch.StartNew();
            var firstAttempt = true;

            while (true)
            {
                var childData = context.Connection.GetJobData(childId);
                if (childData == null)
                {
                    _logger.Warn($"Can not start fan-in continuation '{childId}' for background job '{context.BackgroundJob.Id}': continuation does not exist.");
                    return null;
                }

                var state = context.Connection.GetStateData(childId);
                if (state != null) return state;

                if (DateTime.UtcNow - childData.CreatedAt > ContinuationInvalidTimeout)
                {
                    _logger.Warn($"Fan-in continuation '{childId}' has been ignored: it was deemed to be aborted, because its state is still non-initialized.");
                    return null;
                }

                if (started.Elapsed >= ContinuationStateFetchTimeout)
                {
                    _logger.Warn($"Can not start fan-in continuation '{childId}' for background job '{context.BackgroundJob.Id}': timeout expired while trying to fetch continuation state.");
                    return null;
                }

                Thread.Sleep(firstAttempt ? 0 : 100);
                firstAttempt = false;
            }
        }

        internal enum Decision { Undecided, Start, Skip }

        /// <summary>
        /// Decides the outcome from the parents' current state names (<c>null</c> means "not finished yet").
        /// </summary>
        internal Decision Evaluate(AwaitingJobsState awaiting, Func<string, string?> getParentState)
        {
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
