// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.States
{
#pragma warning disable 618
    /// <summary>
    /// What state handlers and <see cref="IApplyStateFilter"/> filters get while a new state is applied: the job, the
    /// old and new states, the storage and the transaction.
    /// </summary>
    public class ApplyStateContext : StateContext
#pragma warning restore 618
    {
        public ApplyStateContext(
            [NotNull] IWriteOnlyTransaction transaction, 
            [NotNull] ElectStateContext context)
            : this(context.Storage, context.Connection, transaction, context.BackgroundJob, context.CandidateState, context.CurrentState, context.Profiler, context.StateMachine, context.CustomData != null ? new Dictionary<string, object>(context.CustomData) : null)
        {
            // TODO: Add explicit JobExpirationTimeout parameter in 2.0, because it's unclear it isn't preserved
            AfterCommit = context.AfterCommit;
        }

        public ApplyStateContext(
            [NotNull] JobStorage storage,
            [NotNull] IStorageConnection connection,
            [NotNull] IWriteOnlyTransaction transaction,
            [NotNull] BackgroundJob backgroundJob,
            [NotNull] IState newState,
            [CanBeNull] string? oldStateName)
            : this(storage, connection, transaction, backgroundJob, newState, oldStateName, EmptyProfiler.Instance, null)
        {
        }

        internal ApplyStateContext(
            [NotNull] JobStorage storage,
            [NotNull] IStorageConnection connection,
            [NotNull] IWriteOnlyTransaction transaction,
            [NotNull] BackgroundJob backgroundJob,
            [NotNull] IState newState, 
            [CanBeNull] string? oldStateName,
            [NotNull] IProfiler profiler,
            [CanBeNull] IStateMachine? stateMachine,
            [CanBeNull] IReadOnlyDictionary<string, object>? customData = null)
        {
            BackgroundJob = backgroundJob ?? throw new ArgumentNullException(nameof(backgroundJob));
            Storage = storage ?? throw new ArgumentNullException(nameof(storage));
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            NewState = newState ?? throw new ArgumentNullException(nameof(newState));
            OldStateName = oldStateName;
            Profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
            StateMachine = stateMachine;
            CustomData = customData;
            JobExpirationTimeout = storage.JobExpirationTimeout;
        }

        [NotNull]
        public JobStorage Storage { get; }

        [NotNull]
        public IStorageConnection Connection { get; }

        [NotNull]
        public IWriteOnlyTransaction Transaction { get; }
        
        public override BackgroundJob BackgroundJob { get; }

        [CanBeNull]
        public string? OldStateName { get; }

        [NotNull]
        public IState NewState { get; }
        
        public TimeSpan JobExpirationTimeout { get; set; }

        /// <summary>
        /// Makes the job expire after <see cref="JobExpirationTimeout"/> although <see cref="NewState"/> isn't final.
        /// Set by <see cref="ExpireAfterAttribute.IncludeFailed"/> and <c>UseFailedJobRetention</c> for the
        /// <see cref="FailedState"/>. The next state change persists the job again, unless that state is final.
        /// </summary>
        internal bool ExpireInNonFinalState { get; set; }

        /// <summary>Whether the job expires after this state change: the new state is final, or <see cref="ExpireInNonFinalState"/> is set.</summary>
        internal bool JobExpires => NewState.IsFinal || ExpireInNonFinalState;

        /// <summary>
        /// Set by <see cref="BatchSupportAttribute"/> when it ran for this state change, so a state change made
        /// without filters can be told apart and still keeps the batch counters right.
        /// </summary>
        internal bool BatchCountersApplied { get; set; }

        [NotNull]
        internal IProfiler Profiler { get; }

        [CanBeNull]
        public IReadOnlyDictionary<string, object>? CustomData { get; }

        [CanBeNull]
        public IStateMachine? StateMachine { get; }

        /// <summary>
        /// Set by the code that commits this state change, see <see cref="PostCommitActions"/>. <c>null</c> when
        /// nobody runs actions after the commit.
        /// </summary>
        [CanBeNull]
        internal PostCommitActions? AfterCommit { get; set; }

        /// <summary>Runs the action once the state change is committed, or right away when that isn't known.</summary>
        internal void RunAfterCommit([NotNull] Action action) => PostCommitActions.RunAfterCommit(AfterCommit, action);

        public T? GetJobParameter<T>([NotNull] string name) => GetJobParameter<T>(name, allowStale: false);

        public T? GetJobParameter<T>([NotNull] string name, bool allowStale)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            try
            {
                string? value;

                if (allowStale && BackgroundJob.ParametersSnapshot != null)
                {
                    BackgroundJob.ParametersSnapshot.TryGetValue(name, out value);
                }
                else
                {
                    value = Connection.GetJobParameter(BackgroundJob.Id, name);                
                }

                return SerializationHelper.Deserialize<T>(value, SerializationOption.User);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not get a value of the job parameter `{name}`. See inner exception for details.", ex);
            }
        }
    }
}