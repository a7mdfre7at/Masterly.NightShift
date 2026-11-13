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

// TODO: Merge this class with BackgroundJobStateChanger in 2.0.0
/// <summary>
/// Applies a new state to a job: runs the election filters, then the apply filters and the state handlers of the
/// old and new states.
/// </summary>
public class StateMachine : IStateMachine
{
    private readonly IJobFilterProvider _filterProvider;

    public StateMachine([NotNull] IJobFilterProvider filterProvider)
        : this(filterProvider, new CoreStateMachine())
    {
    }

    internal StateMachine(
        [NotNull] IJobFilterProvider filterProvider,
        [NotNull] IStateMachine innerStateMachine)
    {
        ArgumentNullException.ThrowIfNull(filterProvider);
        ArgumentNullException.ThrowIfNull(innerStateMachine);

        _filterProvider = filterProvider;
        InnerStateMachine = innerStateMachine;
    }

    public IStateMachine InnerStateMachine { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1725:Parameter names should match base declaration", Justification = "Parameter name changed to avoid ambiguity and errors in the method's implementation.")]
    public IState ApplyState(ApplyStateContext initialContext)
    {
        ArgumentNullException.ThrowIfNull(initialContext);

        JobFilterInfo filterInfo = GetFilters(initialContext.BackgroundJob.Job);
        JobFilterInfo.FilterCollection<IElectStateFilter> electFilters = filterInfo.ElectStateFilters;
        JobFilterInfo.FilterCollection<IApplyStateFilter> applyFilters = filterInfo.ApplyStateFilters;

        // Electing a a state
        ElectStateContext electContext = new(initialContext, this);

        foreach (IElectStateFilter filter in electFilters)
        {
            electContext.Profiler.InvokeMeasured(
                new KeyValuePair<IElectStateFilter, ElectStateContext>(filter, electContext),
                InvokeOnStateElection,
                static ctx => $"OnStateElection for job {ctx.Value.BackgroundJob.Id}");

            // A filter had the job given back to its queue: no filter after it runs, and nothing is written.
            // The state returned wasn't applied; the caller that offered it (BackgroundJobStateChanger) drops
            // the transaction.
            if (electContext.IsGivenBack)
            {
                return electContext.CandidateState;
            }
        }

        foreach (IState state in electContext.TraversedStates)
        {
            initialContext.Transaction.AddJobState(electContext.BackgroundJob.Id, state);
        }

        // Applying the elected state
        ApplyStateContext context = new(initialContext.Transaction, electContext)
        {
            JobExpirationTimeout = initialContext.JobExpirationTimeout
        };

        foreach (IApplyStateFilter filter in applyFilters)
        {
            context.Profiler.InvokeMeasured(
                new KeyValuePair<IApplyStateFilter, ApplyStateContext>(filter, context),
                InvokeOnStateUnapplied,
                static ctx => $"OnStateUnapplied for job {ctx.Value.BackgroundJob.Id}");
        }

        foreach (IApplyStateFilter filter in applyFilters)
        {
            context.Profiler.InvokeMeasured(
                new KeyValuePair<IApplyStateFilter, ApplyStateContext>(filter, context),
                InvokeOnStateApplied,
                static ctx => $"OnStateApplied for job {ctx.Value.BackgroundJob.Id}");
        }

        return InnerStateMachine.ApplyState(context);
    }

    private static void InvokeOnStateElection(KeyValuePair<IElectStateFilter, ElectStateContext> x)
    {
        try
        {
            x.Key.OnStateElection(x.Value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ex.PreserveOriginalStackTrace();
            throw;
        }
    }

    private static void InvokeOnStateApplied(KeyValuePair<IApplyStateFilter, ApplyStateContext> x)
    {
        try
        {
            x.Key.OnStateApplied(x.Value, x.Value.Transaction);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ex.PreserveOriginalStackTrace();
            throw;
        }
    }

    private static void InvokeOnStateUnapplied(KeyValuePair<IApplyStateFilter, ApplyStateContext> x)
    {
        try
        {
            x.Key.OnStateUnapplied(x.Value, x.Value.Transaction);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ex.PreserveOriginalStackTrace();
            throw;
        }
    }

    private JobFilterInfo GetFilters(Job? job)
    {
        return new JobFilterInfo(_filterProvider.GetFilters(job));
    }
}