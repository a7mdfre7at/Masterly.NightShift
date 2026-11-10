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

namespace Masterly.NightShift.States;

internal sealed class CoreStateMachine : IStateMachine
{
    private readonly Func<JobStorage, string?, StateHandlersCollection> _stateHandlersThunk;

    public CoreStateMachine()
        : this(GetStateHandlers)
    {
    }

    internal CoreStateMachine([NotNull] Func<JobStorage, string?, StateHandlersCollection> stateHandlersThunk)
    {
        ArgumentNullException.ThrowIfNull(stateHandlersThunk);
        _stateHandlersThunk = stateHandlersThunk;
    }

    public IState ApplyState(ApplyStateContext context)
    {
        foreach (IStateHandler handler in _stateHandlersThunk(context.Storage, context.OldStateName))
        {
            handler.Unapply(context, context.Transaction);
        }

        context.Transaction.SetJobState(context.BackgroundJob.Id, context.NewState);

        foreach (IStateHandler handler in _stateHandlersThunk(context.Storage, context.NewState.Name))
        {
            handler.Apply(context, context.Transaction);
        }

        // A state change made without filters (the worker's last resort, when every attempt to store a job's
        // final state failed) would leave the job counted in its old state in its batch
        BatchSupportAttribute.ApplyWithoutFilter(context, context.Transaction);

        // A filter may also expire a job in a non-final state (failed job retention)
        if (context.JobExpires)
        {
            context.Transaction.ExpireJob(context.BackgroundJob.Id, context.JobExpirationTimeout);
        }
        else
        {
            context.Transaction.PersistJob(context.BackgroundJob.Id);
        }

        return context.NewState;
    }

    private static StateHandlersCollection GetStateHandlers(JobStorage storage, string? stateName)
    {
        ICollection<IStateHandler> globalHandlers = GlobalStateHandlers.Handlers;

        return new StateHandlersCollection(
            globalHandlers as List<IStateHandler> ?? [.. globalHandlers],
            storage.GetStateHandlers(),
            stateName);
    }

    internal readonly struct StateHandlersCollection(
        List<IStateHandler> globalHandlers,
        IEnumerable<IStateHandler> storageHandlers,
        string? stateName)
    {
        public Enumerator GetEnumerator() => new(globalHandlers, storageHandlers, stateName);

        public ref struct Enumerator(List<IStateHandler> globalHandlers, IEnumerable<IStateHandler> storageHandlers, string? stateName)
        {
            private List<IStateHandler>.Enumerator _globalEnumerator = globalHandlers.GetEnumerator();
            private readonly IEnumerator<IStateHandler> _storageEnumerator = storageHandlers.GetEnumerator();
            private readonly string? _stateName = stateName;

            public bool MoveNext()
            {
                while (_globalEnumerator.MoveNext())
                {
                    IStateHandler current = _globalEnumerator.Current;
                    if (current.StateName.Equals(_stateName, StringComparison.OrdinalIgnoreCase))
                    {
                        Current = current;
                        return true;
                    }
                }

                while (_storageEnumerator.MoveNext())
                {
                    IStateHandler current = _storageEnumerator.Current;
                    if (current.StateName.Equals(_stateName, StringComparison.OrdinalIgnoreCase))
                    {
                        Current = current;
                        return true;
                    }
                }

                Current = default!; // Read only after MoveNext returned true
                return false;
            }

            public IStateHandler Current { get; private set; } = default!; // Read only after MoveNext returned true
        }
    }
}