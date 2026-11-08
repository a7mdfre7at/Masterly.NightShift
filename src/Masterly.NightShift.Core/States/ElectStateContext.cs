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
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.States
{
#pragma warning disable 618
    /// <summary>
    /// What <see cref="IElectStateFilter"/> filters get while the next state of a job is chosen; setting <see
    /// cref="CandidateState"/> changes it.
    /// </summary>
    public class ElectStateContext : StateContext
#pragma warning restore 618
    {
        private readonly IList<IState> _traversedStates = new List<IState>();
        private IState _candidateState;

        public ElectStateContext([NotNull] ApplyStateContext applyContext)
            : this(applyContext, null)
        {
        }

        public ElectStateContext([NotNull] ApplyStateContext applyContext, [CanBeNull] StateMachine? stateMachine)
        {
            if (applyContext == null) throw new ArgumentNullException(nameof(applyContext));
            
            BackgroundJob = applyContext.BackgroundJob;
            _candidateState = applyContext.NewState;

            Storage = applyContext.Storage;
            Connection = applyContext.Connection;
            Transaction = applyContext.Transaction;
            CurrentState = applyContext.OldStateName;
            Profiler = applyContext.Profiler;
            CustomData = applyContext.CustomData?.ToDictionary(static x => x.Key, static x => x.Value);
            StateMachine = stateMachine;
            AfterCommit = applyContext.AfterCommit;
        }
        
        public override BackgroundJob BackgroundJob { get; }

        [NotNull]
        public JobStorage Storage { get; }

        [NotNull]
        public IStorageConnection Connection { get; }

        [NotNull]
        public IWriteOnlyTransaction Transaction { get; }

        [NotNull]
        public IState CandidateState
        {
            get => _candidateState;
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value), "The CandidateState property can not be set to null.");
                }

                if (_candidateState != value)
                {
                    _traversedStates.Add(_candidateState);
                    _candidateState = value;
                }
            }
        }

        [CanBeNull]
        public string? CurrentState { get; }

        [NotNull]
        public IState[] TraversedStates => _traversedStates.ToArray();

        [NotNull]
        internal IProfiler Profiler { get; }

        [CanBeNull]
        public IDictionary<string, object>? CustomData { get; }
        
        [CanBeNull]
        public StateMachine? StateMachine { get; }

        /// <summary>The <see cref="ApplyStateContext.AfterCommit"/> of the state change being elected.</summary>
        [CanBeNull]
        internal PostCommitActions? AfterCommit { get; }

        /// <summary>Runs the action once the state change is committed, or right away when that isn't known.</summary>
        internal void RunAfterCommit([NotNull] Action action) => PostCommitActions.RunAfterCommit(AfterCommit, action);

        /// <summary>
        /// Runs the action once the commit of the state change is over, whether it succeeded or threw (so after the
        /// job's lock is released), or right away when that isn't known. For work about something already stored on
        /// its own, which a retried state change wouldn't do again.
        /// </summary>
        internal void RunAfterCommitAttempt([NotNull] Action action) => PostCommitActions.RunAfterCommitAttempt(AfterCommit, action);

        public void SetJobParameter<T>([NotNull] string name, T value)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            Connection.SetJobParameter(BackgroundJob.Id, name, SerializationHelper.Serialize(value, SerializationOption.User));
        }

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