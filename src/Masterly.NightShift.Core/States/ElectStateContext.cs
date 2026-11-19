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

using Masterly.NightShift.Profiling;

namespace Masterly.NightShift.States;

/// <summary>
/// What <see cref="IElectStateFilter"/> filters get while the next state of a job is chosen; setting <see
/// cref="CandidateState"/> changes it.
/// </summary>
public class ElectStateContext
{
    private readonly IList<IState> _traversedStates = [];
    private IState _candidateState;

    public ElectStateContext([NotNull] ApplyStateContext applyContext)
        : this(applyContext, null)
    {
    }

    public ElectStateContext([NotNull] ApplyStateContext applyContext, [CanBeNull] StateMachine? stateMachine)
    {
        ArgumentNullException.ThrowIfNull(applyContext);

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
        LookAgain = applyContext.LookAgain;
    }

    public BackgroundJob BackgroundJob { get; }

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

            if (!ReferenceEquals(_candidateState, value))
            {
                _traversedStates.Add(_candidateState);
                _candidateState = value;
            }
        }
    }

    /// <summary>
    /// Sets the candidate state without counting the one it replaces among the <see cref="TraversedStates"/>,
    /// which are written to the job's history: for a filter that finds that the job never was a candidate for
    /// that state, so its history doesn't say it was (a job that is put back into the state it was in instead of
    /// being processed never was Processing).
    /// </summary>
    internal void ReplaceCandidateState([NotNull] IState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _candidateState = state;
    }

    [CanBeNull]
    public string? CurrentState { get; }

    [NotNull]
    public IState[] TraversedStates => [.. _traversedStates];

    [NotNull]
    internal IProfiler Profiler { get; }

    [CanBeNull]
    public IDictionary<string, object>? CustomData { get; }

    [CanBeNull]
    public StateMachine? StateMachine { get; }

    /// <summary>The <see cref="ApplyStateContext.AfterCommit"/> of the state change being elected.</summary>
    [CanBeNull]
    internal PostCommitActions? AfterCommit { get; }

    /// <summary>
    /// The <see cref="ApplyStateContext.LookAgain"/> of the state change being elected, while it is on offer: to
    /// the first election filter only (see <see cref="WithdrawLookAgain"/>).
    /// </summary>
    [CanBeNull]
    internal JobLookAgain? LookAgain { get; private set; }

    /// <summary>
    /// Ends the offer to look at the job again, once the first election filter has run without taking it: a worker
    /// waits with a job only before any other filter saw it about to change state, so no filter is ever left with
    /// something it did for a state change that then didn't happen.
    /// </summary>
    internal void WithdrawLookAgain()
    {
        if (LookAgain is { Requested: false })
        {
            LookAgain = null;
        }
    }

    /// <summary>
    /// Whether the worker that fetched the job and is about to process it can be asked to look at it again in a
    /// moment instead: it offers that, and no filter asked yet.
    /// </summary>
    internal bool CanLookAgain => LookAgain is { Requested: false };

    /// <summary>Whether a filter asked the worker to look again: no filter runs after it, and nothing is stored.</summary>
    internal bool IsLookingAgain => LookAgain is { Requested: true };

    /// <summary>
    /// Has the worker keep the job, wait for <paramref name="pause"/> and change its state again, with nothing
    /// stored about this state change: no state, no history entry, no parameter, and no filter after this one
    /// runs. See <see cref="JobLookAgain"/>.
    /// </summary>
    /// <returns>
    /// False when the state change doesn't come from a worker, or the worker doesn't offer it (its pool has
    /// waited enough, or this job has been waited for long enough): the filter decides otherwise.
    /// </returns>
    internal bool TryLookAgain(TimeSpan pause)
    {
        if (LookAgain == null)
        {
            return false;
        }

        LookAgain.Request(pause);
        return true;
    }

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
        ArgumentException.ThrowIfNullOrEmpty(name);
        Connection.SetJobParameter(BackgroundJob.Id, name, SerializationHelper.Serialize(value, SerializationOption.User));
    }

    public T? GetJobParameter<T>([NotNull] string name) => GetJobParameter<T>(name, allowStale: false);

    public T? GetJobParameter<T>([NotNull] string name, bool allowStale)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

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
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            throw new InvalidOperationException(
                $"Could not get a value of the job parameter `{name}`. See inner exception for details.", ex);
        }
    }
}