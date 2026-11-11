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

namespace Masterly.NightShift.Client;

/// <summary>
/// Creates background jobs in the storage, running the client filters around the creation and applying the initial
/// state with the state machine.
/// </summary>
public class BackgroundJobFactory : IBackgroundJobFactory
{
    private readonly IJobFilterProvider _filterProvider;
    private readonly IBackgroundJobFactory _innerFactory;

    public BackgroundJobFactory()
        : this(JobFilterProviders.Providers)
    {
    }

    public BackgroundJobFactory([NotNull] IJobFilterProvider filterProvider)
        : this(filterProvider, new CoreBackgroundJobFactory(new StateMachine(filterProvider)))
    {
    }

    public int RetryAttempts
    {
        get
        {
            if (_innerFactory is CoreBackgroundJobFactory factory)
            {
                return factory.RetryAttempts;
            }

            return 0;
        }
        set
        {
            if (_innerFactory is CoreBackgroundJobFactory factory)
            {
                factory.RetryAttempts = value;
            }
        }
    }

    internal BackgroundJobFactory(
        [NotNull] IJobFilterProvider filterProvider,
        [NotNull] IBackgroundJobFactory innerFactory)
    {
        ArgumentNullException.ThrowIfNull(filterProvider);
        ArgumentNullException.ThrowIfNull(innerFactory);

        _filterProvider = filterProvider;
        _innerFactory = innerFactory;
    }

    public IStateMachine StateMachine => _innerFactory.StateMachine;

    public BackgroundJob? Create(CreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        JobFilterInfo filterInfo = GetFilters(context.Job);

        try
        {
            context.Factory = this;

            CreatedContext createdContext = CreateWithFilters(context, filterInfo.ClientFilters);
            return createdContext.BackgroundJob;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ClientExceptionContext exceptionContext = new(context, ex);

            InvokeExceptionFilters(exceptionContext, filterInfo.ClientExceptionFiltersReversed);
            if (!exceptionContext.ExceptionHandled)
            {
                throw;
            }

            return null;
        }
        finally
        {
            context.Factory = null;
        }
    }

    /// <summary>
    /// Creates the jobs of several contexts that share a storage and a connection. Every job gets the filter
    /// calls <see cref="Create"/> makes, in three passes: <c>OnCreating</c> for each job, the creation of the
    /// jobs no filter canceled, then <c>OnCreated</c> for each job.
    /// </summary>
    /// <returns>The created jobs, in the order given; <c>null</c> for a job that was canceled or whose exception a filter handled.</returns>
    internal BackgroundJob?[] CreateMany(IReadOnlyList<CreateContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        BulkEntry[] entries = new BulkEntry[contexts.Count];
        Exception? failure = null;

        try
        {
            // Pass 1: OnCreating. A filter that throws here stops the whole group before anything is created,
            // and the jobs that already passed are told so
            for (int i = 0; i < entries.Length && failure == null; i++)
            {
                entries[i] = new BulkEntry(contexts[i], this, GetFilters(contexts[i].Job));
                failure = RunCreating(entries[i]);

                if (failure != null)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (!entries[j].Finished) FinishWithException(entries[j], failure);
                    }
                }
            }

            if (failure != null)
            {
                failure.PreserveOriginalStackTrace();
                throw failure;
            }

            // Pass 2: the creation itself, in the state the filters left
            List<BulkEntry> pending = [.. entries.Where(static x => !x.Finished)];
            foreach (BulkEntry entry in pending)
            {
                entry.Context.InitialState = entry.PreContext.InitialState;
            }

            BackgroundJob?[] created = [];

            try
            {
                created = CreateInner(pending);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                bool rethrow = false;
                foreach (BulkEntry entry in pending)
                {
                    rethrow |= FinishWithException(entry, ex) != null;
                }

                if (rethrow) throw;
                pending.Clear();
            }

            // Pass 3: OnCreated, from the last filter to the first as Create does
            for (int i = 0; i < pending.Count; i++)
            {
                BulkEntry entry = pending[i];
                CreatedContext postContext = new(entry.Context, created[i], false, null);
                failure ??= Finish(entry, entry.Filters.Count - 1, postContext, null);
            }

            if (failure != null)
            {
                failure.PreserveOriginalStackTrace();
                throw failure;
            }

            BackgroundJob?[] result = new BackgroundJob?[entries.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = entries[i].Result;
            }

            return result;
        }
        finally
        {
            foreach (CreateContext context in contexts)
            {
                context.Factory = null;
            }
        }
    }

    private BackgroundJob?[] CreateInner(List<BulkEntry> pending)
    {
        if (_innerFactory is CoreBackgroundJobFactory coreFactory)
        {
            return coreFactory.CreateMany([.. pending.Select(static x => x.Context)]);
        }

        // Another inner factory: one job at a time
        BackgroundJob?[] created = new BackgroundJob?[pending.Count];
        for (int i = 0; i < created.Length; i++)
        {
            created[i] = _innerFactory.Create(pending[i].Context);
        }

        return created;
    }

    // Calls OnCreating of each filter. Returns the exception a filter threw and nobody handled, or null.
    private static Exception? RunCreating(BulkEntry entry)
    {
        for (int level = 0; level < entry.Filters.Count; level++)
        {
            try
            {
                entry.PreContext.Profiler.InvokeMeasured(
                    new KeyValuePair<IClientFilter, CreatingContext>(entry.Filters[level], entry.PreContext),
                    InvokeOnCreating,
                    static ctx => $"OnCreating for {ctx.Value.Job.Type.FullName}.{ctx.Value.Job.Method.Name}");
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // As in Create: the filters before this one see the exception in OnCreated
                return Finish(entry, level - 1, null, ex);
            }

            if (entry.PreContext.Canceled)
            {
                // As in Create: the filters before this one see the canceled creation in OnCreated
                return Finish(entry, level - 1, new CreatedContext(entry.PreContext, null, true, null), null);
            }
        }

        return null;
    }

    private static Exception? FinishWithException(BulkEntry entry, Exception exception)
    {
        return Finish(entry, entry.Filters.Count - 1, null, exception);
    }

    // Calls OnCreated of the filters from the given level down to the first, passing a result or an exception
    // outward exactly as the nested calls of Create do, then the exception filters when an exception is left.
    // Returns the exception nobody handled, or null.
    private static Exception? Finish(BulkEntry entry, int fromLevel, CreatedContext? postContext, Exception? exception)
    {
        for (int level = fromLevel; level >= 0; level--)
        {
            IClientFilter filter = entry.Filters[level];

            if (exception != null)
            {
                postContext = new CreatedContext(entry.PreContext, null, false, exception);
            }

            try
            {
                postContext!.Profiler.InvokeMeasured( // Set above, or passed in when there is no exception
                    new KeyValuePair<IClientFilter, CreatedContext>(filter, postContext),
                    InvokeOnCreated,
                    static ctx => $"OnCreated for job {ctx.Value.BackgroundJob?.Id ?? "(null)"}");

                if (exception != null && postContext.ExceptionHandled)
                {
                    exception = null;
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                exception = ex;
            }
        }

        entry.Finished = true;

        if (exception != null)
        {
            ClientExceptionContext exceptionContext = new(entry.Context, exception);

            try
            {
                InvokeExceptionFilters(exceptionContext, entry.FilterInfo.ClientExceptionFiltersReversed);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return ex;
            }

            return exceptionContext.ExceptionHandled ? null : exception;
        }

        entry.Result = postContext?.BackgroundJob;
        return null;
    }

    private sealed class BulkEntry
    {
        public BulkEntry(CreateContext context, BackgroundJobFactory factory, JobFilterInfo filterInfo)
        {
            context.Factory = factory;

            Context = context;
            FilterInfo = filterInfo;
            PreContext = new CreatingContext(context);

            foreach (IClientFilter filter in filterInfo.ClientFilters)
            {
                Filters.Add(filter);
            }
        }

        public CreateContext Context { get; }
        public JobFilterInfo FilterInfo { get; }
        public CreatingContext PreContext { get; }
        public List<IClientFilter> Filters { get; } = [];
        public bool Finished { get; set; }
        public BackgroundJob? Result { get; set; }
    }

    private JobFilterInfo GetFilters(Job job)
    {
        return new JobFilterInfo(_filterProvider.GetFilters(job));
    }

    private CreatedContext CreateWithFilters(
        CreateContext context,
        JobFilterInfo.FilterCollection<IClientFilter> filters)
    {
        CreatingContext preContext = new(context);
        JobFilterInfo.FilterCollection<IClientFilter>.Enumerator enumerator = filters.GetEnumerator();

        return InvokeNextClientFilter(ref enumerator, _innerFactory, context, preContext);
    }

    private static CreatedContext InvokeNextClientFilter(
        ref JobFilterInfo.FilterCollection<IClientFilter>.Enumerator enumerator,
        IBackgroundJobFactory innerFactory,
        CreateContext context,
        CreatingContext preContext)
    {
        if (enumerator.MoveNext())
        {
            return InvokeClientFilter(ref enumerator, innerFactory, context, preContext);
        }

        // A filter may have replaced the state the job is created in
        context.InitialState = preContext.InitialState;

        BackgroundJob? backgroundJob = innerFactory.Create(context);
        return new CreatedContext(context, backgroundJob, false, null);
    }

    private static CreatedContext InvokeClientFilter(
        ref JobFilterInfo.FilterCollection<IClientFilter>.Enumerator enumerator,
        IBackgroundJobFactory innerFactory,
        CreateContext context,
        CreatingContext preContext)
    {
        IClientFilter filter = enumerator.Current;

        preContext.Profiler.InvokeMeasured(
            new KeyValuePair<IClientFilter, CreatingContext>(filter, preContext),
            InvokeOnCreating,
            static ctx => $"OnCreating for {ctx.Value.Job.Type.FullName}.{ctx.Value.Job.Method.Name}");

        if (preContext.Canceled)
        {
            return new CreatedContext(preContext, null, true, null);
        }

        bool wasError = false;
        CreatedContext postContext;
        try
        {
            postContext = InvokeNextClientFilter(ref enumerator, innerFactory, context, preContext);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            wasError = true;
            postContext = new CreatedContext(preContext, null, false, ex);

            postContext.Profiler.InvokeMeasured(
                new KeyValuePair<IClientFilter, CreatedContext>(filter, postContext),
                InvokeOnCreated,
                static ctx => $"OnCreated for job {ctx.Value.BackgroundJob?.Id ?? "(null)"}");

            if (!postContext.ExceptionHandled)
            {
                throw;
            }
        }

        if (!wasError)
        {
            postContext.Profiler.InvokeMeasured(
                new KeyValuePair<IClientFilter, CreatedContext>(filter, postContext),
                InvokeOnCreated,
                static ctx => $"OnCreated for job {ctx.Value.BackgroundJob?.Id ?? "(null)"}");
        }

        return postContext;
    }

    private static void InvokeOnCreating(KeyValuePair<IClientFilter, CreatingContext> x)
    {
        try
        {
            x.Key.OnCreating(x.Value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ex.PreserveOriginalStackTrace();
            throw;
        }
    }

    private static void InvokeOnCreated(KeyValuePair<IClientFilter, CreatedContext> x)
    {
        try
        {
            x.Key.OnCreated(x.Value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ex.PreserveOriginalStackTrace();
            throw;
        }
    }

    private static void InvokeExceptionFilters(
        ClientExceptionContext context, JobFilterInfo.ReversedFilterCollection<IClientExceptionFilter> filters)
    {
        foreach (IClientExceptionFilter filter in filters)
        {
            context.Profiler.InvokeMeasured(
                new KeyValuePair<IClientExceptionFilter, ClientExceptionContext>(filter, context),
                InvokeOnClientException,
                static ctx => $"OnClientException for {ctx.Value.Job.Type.FullName}.{ctx.Value.Job.Method.Name}");
        }
    }

    private static void InvokeOnClientException(KeyValuePair<IClientExceptionFilter, ClientExceptionContext> x)
    {
        try
        {
            x.Key.OnClientException(x.Value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ex.PreserveOriginalStackTrace();
            throw;
        }
    }
}