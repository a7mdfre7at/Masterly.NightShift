// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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

using System;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Makes sure a job that already finished is not executed a second time. The first final state applied to
    /// the job (Succeeded or Deleted), as it stands once every election filter has run, is recorded in the
    /// <see cref="CompletionParameter"/> job parameter, which is written at once, before the state change commits.
    /// A Succeeded state is recorded without the job's return value. When a worker later picks the job up again, for
    /// example because the storage connection dropped while the Succeeded state was being saved and the job came
    /// back from the queue, the job is moved straight to the recorded state instead of being run, whatever other
    /// election filters made of the Processing state.
    /// </summary>
    /// <remarks>
    /// <para>Only the repeated execution is prevented. Explicit state changes are honoured: a Delete moves a
    /// finished job to Deleted, and moving a finished job out of its final state (Requeue from the dashboard,
    /// for example) forgets the recorded completion, so the job runs once more and its next final state is
    /// recorded anew.</para>
    /// <para>It protects against a lost final state, not against a crash inside the job method: when the
    /// process dies before the job finishes, nothing is recorded and the job runs again as usual. Creating the
    /// job is not affected.</para>
    /// </remarks>
    public class IdempotentCompletionAttribute : JobFilterAttribute, IElectStateFilter, IApplyStateFilter
    {
        /// <summary>The job parameter that holds the recorded final state.</summary>
        public const string CompletionParameter = "Completion";

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(IdempotentCompletionAttribute));

        /// <summary>
        /// Initializes a new instance of the <see cref="IdempotentCompletionAttribute"/> class, with
        /// <see cref="JobFilterAttribute.Order"/> 0.
        /// </summary>
        public IdempotentCompletionAttribute()
        {
            Order = 0;
        }

        /// <inheritdoc />
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (String.IsNullOrEmpty(context.CurrentState)) return;

            var serializedState = context.GetJobParameter<string>(CompletionParameter, allowStale: true);
            var completed = !String.IsNullOrEmpty(serializedState);

            // A worker picked the job up again. An election filter with a lower order may already have replaced the
            // Processing state (LatencyTimeout with Deleted, for example); the recorded completion still wins.
            if (context.CandidateState is ProcessingState || context.TraversedStates.Any(static x => x is ProcessingState))
            {
                // The job finished before: don't run it again, restore the recorded final state instead
                if (completed)
                {
                    context.CandidateState = SerializationHelper.Deserialize<IState>(serializedState, SerializationOption.TypedInternal);
                }

                return;
            }

            // A final state is recorded once the election is over, see OnStateApplied
            if (context.CandidateState.IsFinal)
            {
                return;
            }

            // Leaving a final state (Requeue, for example) is an explicit request to run the job again. A job whose
            // final state was lost is still Processing in storage, and keeps its recorded completion.
            if (completed && IsFinalState(context.CurrentState, serializedState))
            {
                context.SetJobParameter<string>(CompletionParameter, null);
            }
        }

        /// <summary>
        /// Records the first final state applied to the job. Called after every election filter has run, so the
        /// recorded state is the one actually applied, such as the Deleted state <see cref="AutomaticRetryAttribute"/>
        /// elects when the attempts are used up. A failure to record is logged and never fails the state change.
        /// </summary>
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (String.IsNullOrEmpty(context.OldStateName) || !context.NewState.IsFinal) return;

            // Only the first final state is recorded; a later one, such as a Delete, is applied as it is
            if (!String.IsNullOrEmpty(context.GetJobParameter<string>(CompletionParameter, allowStale: true))) return;

            try
            {
                var serializedState = SerializationHelper.Serialize(WithoutResult(context.NewState), SerializationOption.TypedInternal);
                context.Connection.SetJobParameter(
                    context.BackgroundJob.Id,
                    CompletionParameter,
                    SerializationHelper.Serialize(serializedState, SerializationOption.User));
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException(
                    $"The completion of background job '{context.BackgroundJob.Id}' ({context.NewState.Name}) could not be recorded; a repeated pickup will run it again.",
                    ex);
            }
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }

        // The return value isn't needed to restore the state, and may not be serializable at all (a cycle, a stream)
        private static IState WithoutResult(IState state)
        {
            return state is SucceededState succeeded && succeeded.Result != null
                ? new SucceededState(null, succeeded.Latency, succeeded.PerformanceDuration) { Reason = succeeded.Reason }
                : state;
        }

        private static bool IsFinalState(string currentState, string serializedState)
        {
            if (SucceededState.StateName.Equals(currentState, StringComparison.OrdinalIgnoreCase)
                || DeletedState.StateName.Equals(currentState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A custom final state is recognized when it is the recorded one
            var recorded = SerializationHelper.Deserialize<IState>(serializedState, SerializationOption.TypedInternal);
            return recorded != null && recorded.Name.Equals(currentState, StringComparison.OrdinalIgnoreCase);
        }
    }
}