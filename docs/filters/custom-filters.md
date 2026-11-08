# Custom filters

When the built-in attributes don't cover a rule you need, write your own filter. A filter is a class that implements one or more filter interfaces; NightShift calls it when a job is created, when a state is chosen and written, and when the job runs. This page shows a worked example of each kind, how filters are ordered, and how to register them as attributes or globally. It also covers interceptors, state handlers and filter providers.

## Quick example

A server filter that warns in the job's console when a run is slow:

```csharp
using System.Diagnostics;
using Masterly.NightShift;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

public sealed class SlowJobWarningAttribute : JobFilterAttribute, IServerFilter
{
    private const string StopwatchItem = "SlowJobWarning.Stopwatch";

    public SlowJobWarningAttribute(int thresholdSeconds)
    {
        ThresholdSeconds = thresholdSeconds;
    }

    public int ThresholdSeconds { get; }

    public void OnPerforming(PerformingContext context)
    {
        context.Items[StopwatchItem] = Stopwatch.StartNew();
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(StopwatchItem, out var value) && value is Stopwatch stopwatch
            && stopwatch.Elapsed.TotalSeconds > ThresholdSeconds)
        {
            context.Console.WriteLine(JobConsoleLevel.Warning, $"Took {stopwatch.Elapsed.TotalSeconds:N0} s, more than {ThresholdSeconds} s");
        }
    }
}
```

Use it on one class:

```csharp
[SlowJobWarning(60)]
public class ExportService
{
    public Task ExportCustomers(string region, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

or on every job:

```csharp
GlobalConfiguration.Configuration.UseFilter(new SlowJobWarningAttribute(thresholdSeconds: 300));
```

Deriving from `JobFilterAttribute` (namespace `Masterly.NightShift.Common`) is what makes the class usable as an attribute and gives it the `Order` property. A class that only implements the interfaces can still be registered globally.

## How it works

The filter kinds and where they run:

| Interface | Methods | Runs in | Can |
|-----------|---------|---------|-----|
| `IClientFilter` | `OnCreating(CreatingContext)`, `OnCreated(CreatedContext)` | The process that creates the job (`Enqueue`, `Schedule`, a recurring trigger, a continuation) | Set job parameters, cancel creation, see the new job id |
| `IServerFilter` | `OnPerforming(PerformingContext)`, `OnPerformed(PerformedContext)` | The server, around the job method | Acquire and release resources, cancel the run, see the result or exception, mark an exception handled |
| `IElectStateFilter` | `OnStateElection(ElectStateContext)` | Wherever the job's state changes: client, server, dashboard | Replace the candidate state |
| `IApplyStateFilter` | `OnStateUnapplied(ApplyStateContext, IWriteOnlyTransaction)`, `OnStateApplied(...)` | Same as election, inside the state-change transaction | Add storage writes to the transaction, change the expiration |
| `IClientExceptionFilter` | `OnClientException(ClientExceptionContext)` | The creating process, when creation throws | Log, or swallow the exception so `Enqueue` returns `null` |
| `IServerExceptionFilter` | `OnServerException(ServerExceptionContext)` | The server, when performing throws | Log, or swallow the exception so the job succeeds |
| `IJobInterceptor` | `Intercept(JobInvocation)` | The server, around the method call, inside the server filters | Change arguments, call the method several times or not at all, replace the result |
| `IStateHandler` | `Apply(...)`, `Unapply(...)` | Same as apply filters, for one state name | Storage side effects of entering or leaving a state |

A job's life, with the filter calls in order:

1. **Creation.** `OnCreating` (every client filter), the job is stored and its initial state is elected and applied (election and apply filters run here too), then `OnCreated` (in reverse). If anything throws, client exception filters run.
2. **Fetch.** A worker moves the job from Enqueued to Processing: election and apply filters run. An election filter can turn Processing into something else, and then the job is not run (`LatencyTimeout`, `RunWindow`).
3. **Run.** `OnPerforming` (every server filter), then the interceptors, then the job method, then `OnPerformed` (in reverse). If anything throws and no filter handles it, server exception filters run.
4. **Result.** The worker proposes Succeeded, Failed or Deleted: election filters can change it (`AutomaticRetry` turns Failed into Scheduled), then apply filters and state handlers write it.

Election and apply filters run for every state change, including ones made from the dashboard (Requeue, Delete) and by `IBackgroundJobClient.ChangeState`.

## Order and execution

Filters are collected from the global collection (`JobFilterScope.Global`), from attributes on the job's class or interface (`Type`) and from attributes on the job method (`Method`). They are sorted by `Order` (lower first), then by scope: global, type, method. Then:

- `OnCreating`, `OnPerforming`, `OnStateElection`, `OnStateUnapplied` and `OnStateApplied` run in that order.
- `OnCreated` and `OnPerformed` run in reverse, so filters nest: the first filter to start is the last to finish.
- Exception filters run in reverse order.
- All `OnStateUnapplied` calls run before all `OnStateApplied` calls, and state handlers run after both.

`Order` defaults to `-1` (`JobFilter.DefaultOrder`); values below `-1` throw. Built-in orders worth knowing:

| Filter | Order |
|--------|-------|
| Most built-in filters | `-1` |
| `IdempotentCompletion` | `0` |
| `PreventDuplicates` | `10` |
| `RunWindow`, `Blackout` | `15` |
| `AutomaticRetry` | `20` |
| `Tag`, `StatisticsHistory` | `30` |
| `BatchSupport` | `40` |
| `ExpireAfter` | `50` |
| `ContinuationsSupport`, `FanInContinuationsSupport` | `1000`, `1001` |
| `Queue` | `Int32.MaxValue` |

So an election filter with an order below 20 sees a failure before `AutomaticRetry` turns it into a retry, and one above 20 sees the retry.

**Duplicates.** A `JobFilterAttribute` subclass allows one instance per job unless its `[AttributeUsage]` says `AllowMultiple = true`. When the same filter type is found more than once, only the one that sorts last is kept: with equal orders, a method attribute replaces a class attribute, which replaces a global filter; with different orders, the one with the higher order is kept. Two global filters of the same type and order keep the one added last. Filters that don't implement `IJobFilter` (which `JobFilterAttribute` does) are never de-duplicated.

You can override the order of a global filter when you add it: `GlobalJobFilters.Filters.Add(filter, order: 30)`. On an attribute, set it like any property: `[SlowJobWarning(60, Order = 5)]`.

**Sharing data.** `context.Items` is a dictionary that lives for one creation (shared by `OnCreating`, `OnCreated` and the exception filters) or one run (shared by `OnPerforming`, `OnPerformed`, the interceptors and the exception filters). After a run that didn't throw, the items are passed to the next state change as `ElectStateContext.CustomData` and `ApplyStateContext.CustomData`. To keep data with the job itself, use job parameters (`SetJobParameter` / `GetJobParameter<T>`).

## Client filter

Copies the current tenant into a job parameter when a job is created, and restores it when the job runs. One class can implement several interfaces.

```csharp
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

public interface ITenantContext
{
    string? TenantId { get; set; }
}

public sealed class AmbientTenantContext : ITenantContext
{
    private static readonly AsyncLocal<string?> Current = new AsyncLocal<string?>();

    public string? TenantId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

public sealed class TenantFilter : JobFilterAttribute, IClientFilter, IServerFilter
{
    private const string ParameterName = "TenantId";
    private readonly ITenantContext _tenant;

    public TenantFilter(ITenantContext tenant)
    {
        _tenant = tenant;
    }

    public void OnCreating(CreatingContext context)
    {
        if (_tenant.TenantId != null)
        {
            context.SetJobParameter(ParameterName, _tenant.TenantId);
        }
    }

    public void OnCreated(CreatedContext context)
    {
    }

    public void OnPerforming(PerformingContext context)
    {
        _tenant.TenantId = context.GetJobParameter<string>(ParameterName, allowStale: true);
    }

    public void OnPerformed(PerformedContext context)
    {
        _tenant.TenantId = null;
    }
}
```

`allowStale: true` reads the parameters loaded with the job instead of asking the storage again; use it for values that don't change after creation. This filter takes a constructor dependency, so register an instance globally (see [Registering filters](#registering-filters)).

### Cancel creation

Set `CreatingContext.Canceled = true` and the job is not stored; `Enqueue` returns `null` and later filters' `OnCreating` is skipped. Filters that already ran see `CreatedContext.Canceled` in `OnCreated`.

```csharp
public sealed class MaintenanceModeFilter : JobFilterAttribute, IClientFilter
{
    private readonly Func<bool> _isInMaintenance;

    public MaintenanceModeFilter(Func<bool> isInMaintenance)
    {
        _isInMaintenance = isInMaintenance;
    }

    public void OnCreating(CreatingContext context)
    {
        if (_isInMaintenance() && context.Job.Type.Namespace?.EndsWith(".Marketing", StringComparison.Ordinal) == true)
        {
            context.Canceled = true;
        }
    }

    public void OnCreated(CreatedContext context)
    {
    }
}
```

## Server filter

The [quick example](#quick-example) is a server filter. To cancel a run from `OnPerforming`, set `PerformingContext.Canceled = true`: the job method doesn't run, the job moves to Deleted with the reason `Canceled by filter 'SlowJobWarningAttribute'` (the filter's type name), and outer filters still get `OnPerformed` with `Canceled = true`. In `OnPerformed`, `Exception` holds what the job threw; setting `ExceptionHandled = true` swallows it and the job succeeds.

## Election filter

An election filter looks at `context.CandidateState` and may replace it. Every replaced state is kept in `context.TraversedStates` and written to the job's history.

Send retries to their own queue, so they don't delay fresh work:

```csharp
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

public sealed class RetryQueueAttribute : JobFilterAttribute, IElectStateFilter
{
    public RetryQueueAttribute(string queue)
    {
        Queue = queue;
    }

    public string Queue { get; }

    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is EnqueuedState enqueued
            && context.GetJobParameter<int>("RetryCount") > 0)
        {
            enqueued.Queue = Queue;
        }
    }
}
```

A `[Queue]` attribute on the same job runs after this filter (its order is `Int32.MaxValue`) and wins.

Give up at once on bad input. `Order = 10` makes it run before `AutomaticRetry` (20), which then sees Deleted and leaves it alone:

```csharp
public sealed class DeleteOnValidationErrorAttribute : JobFilterAttribute, IElectStateFilter
{
    public DeleteOnValidationErrorAttribute()
    {
        Order = 10;
    }

    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is FailedState failed && failed.Exception is ArgumentException)
        {
            context.CandidateState = new DeletedState
            {
                Reason = $"Invalid input, not retried: {failed.Exception.Message}",
            };
        }
    }
}
```

`context.CurrentState` is the job's current state name (`null` while the job is being created). `context.Transaction` is the state-change transaction, if you need to write something with it.

## Apply filter

An apply filter adds writes to the state-change transaction, so they commit or roll back with the state. `OnStateUnapplied` runs for the state being left (`context.OldStateName`), `OnStateApplied` for the new one (`context.NewState`).

Keep a set of failed jobs per job type:

```csharp
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

public sealed class FailedByTypeFilter : JobFilterAttribute, IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is FailedState && context.BackgroundJob.Job != null)
        {
            transaction.AddToSet(GetKey(context), context.BackgroundJob.Id);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (FailedState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase)
            && context.BackgroundJob.Job != null)
        {
            transaction.RemoveFromSet(GetKey(context), context.BackgroundJob.Id);
        }
    }

    private static string GetKey(ApplyStateContext context) => $"failed-by-type:{context.BackgroundJob.Job!.Type.Name}";
}
```

`context.JobExpirationTimeout` controls how long a job in a final state is kept. Set it in `OnStateUnapplied`: all `OnStateUnapplied` calls run before any `OnStateApplied`, so every apply filter that reads it, such as the global `JobConsoleStateFilter` that expires the console lines, sees your value whatever the filter order. That is how [`ExpireAfter`](timing.md#expireafterattribute) works. A value set in `OnStateApplied` still applies to the job itself, but filters that ran before yours have already used the old one. Don't do slow I/O (HTTP calls, e-mail) in an apply filter: it runs while the transaction is being built, and the state may still roll back.

## Exception filters

Client exception filters run when creating a job throws (for example the storage is unreachable). Setting `ExceptionHandled = true` swallows the exception and `Enqueue` returns `null`.

```csharp
using Masterly.NightShift.Client;
using Microsoft.Extensions.Logging;

public sealed class ClientErrorLoggingFilter : IClientExceptionFilter
{
    private readonly ILogger _logger;

    public ClientErrorLoggingFilter(ILogger<ClientErrorLoggingFilter> logger)
    {
        _logger = logger;
    }

    public void OnClientException(ClientExceptionContext filterContext)
    {
        _logger.LogError(
            filterContext.Exception,
            "Could not create background job {JobType}.{JobMethod}",
            filterContext.Job.Type.Name,
            filterContext.Job.Method.Name);
    }
}

public sealed class IgnoreClientErrorsAttribute : JobFilterAttribute, IClientExceptionFilter
{
    public void OnClientException(ClientExceptionContext filterContext)
    {
        filterContext.ExceptionHandled = true;
    }
}
```

Server exception filters run when performing a job throws and no server filter handled it. Setting `ExceptionHandled = true` makes the job succeed with a `null` result, so use it sparingly. Cancellation because of server shutdown and `JobAbortedException` (the job was deleted or changed while running) don't reach these filters.

```csharp
using Masterly.NightShift.Server;
using Microsoft.Extensions.Logging;

public sealed class ServerErrorLoggingFilter : IServerExceptionFilter
{
    private readonly ILogger _logger;

    public ServerErrorLoggingFilter(ILogger<ServerErrorLoggingFilter> logger)
    {
        _logger = logger;
    }

    public void OnServerException(ServerExceptionContext filterContext)
    {
        _logger.LogError(
            filterContext.Exception,
            "Background job {JobId} failed on server {ServerId}",
            filterContext.BackgroundJob.Id,
            filterContext.ServerId);
    }
}
```

## Interceptors

Interceptors wrap the call of the job method itself. Unlike a server filter, an interceptor runs inside the performer, on the job's synchronization context, with the activated instance (`invocation.Instance`) and the converted arguments (`invocation.Arguments`) at hand. It can change arguments, call the method several times or not at all, and replace the result. `JobTimeout`, `RetryInProcess` and `LogInvocation` are interceptors.

Interceptors form a chain: each one calls `invocation.Proceed()` (or `await invocation.ProceedAsync()`) to run the next one, and the last call runs the method.

Each interceptor gets its own `JobInvocation`, and proceeding from it always runs the step after that interceptor. So an asynchronous interceptor can `await` something before it proceeds, or proceed again after a failed attempt, and the chain continues from there instead of starting over. All the steps share one `Arguments` array, so an argument an interceptor replaces is what the inner interceptors and the method get.

### Asynchronous interceptor attribute

Derive from `AsyncJobInterceptorAttribute` (namespace `Masterly.NightShift.Common`) when you need code after the method finishes, including for async methods. `ProceedAsync` awaits the method's task and returns its unwrapped value (`null` for `void` and `Task`); return that value, and the base class shapes it back into the method's own return type.

```csharp
using System.Diagnostics;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

public sealed class MeasureAttribute : AsyncJobInterceptorAttribute
{
    protected override async Task<object?> InterceptAsync(JobInvocation invocation)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await invocation.ProceedAsync();
        invocation.Context.Console.WriteLine($"{invocation.Job.Method.Name} took {stopwatch.ElapsedMilliseconds} ms");
        return result;
    }
}
```

For a synchronous job method, the worker thread waits for `InterceptAsync` to finish. Until the first `await` that doesn't complete at once, `InterceptAsync` runs on the worker thread, and so does everything it calls, the job method included; after such an await (a `Task.Delay`, for example), the rest runs on a thread-pool thread while the worker thread waits. So an interceptor that proceeds again after a delay, like a retry, calls a synchronous method on a thread-pool thread, where thread-bound state (a thread-static, a COM apartment) isn't the worker's. Check `invocation.ReturnsTaskLike` and wait synchronously for a synchronous method, as `RetryInProcess` does, when that matters.

An interceptor can wait before it proceeds. This one lets one job at a time through, per server process:

```csharp
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

public sealed class OneAtATimeAttribute : AsyncJobInterceptorAttribute
{
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    protected override async Task<object?> InterceptAsync(JobInvocation invocation)
    {
        await Gate.WaitAsync(invocation.Context.CancellationToken.ShutdownToken);
        try
        {
            return await invocation.ProceedAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
```

### Synchronous interceptor attribute

Derive from `JobInterceptorAttribute` and override `Intercept`. `Proceed()` returns the method's raw result: for an async method that is the task, not awaited. Whatever you return must have the method's return type.

```csharp
public sealed class TrimStringsAttribute : JobInterceptorAttribute
{
    public override object? Intercept(JobInvocation invocation)
    {
        for (var i = 0; i < invocation.Arguments.Length; i++)
        {
            if (invocation.Arguments[i] is string text)
            {
                invocation.Arguments[i] = text.Trim();
            }
        }

        return invocation.Proceed();
    }
}
```

### Global interceptor

Implement `IJobInterceptor` (namespace `Masterly.NightShift.Server`) and register an instance with `UseJobInterceptor` or `GlobalJobInterceptors.Add`:

```csharp
using Masterly.NightShift.Server;
using Microsoft.Extensions.Logging;

public sealed class JobLogScopeInterceptor : IJobInterceptor
{
    private readonly ILogger _logger;

    public JobLogScopeInterceptor(ILogger<JobLogScopeInterceptor> logger)
    {
        _logger = logger;
    }

    public object? Intercept(JobInvocation invocation)
    {
        using (_logger.BeginScope(new Dictionary<string, object> { ["JobId"] = invocation.Context.BackgroundJob.Id }))
        {
            return invocation.Proceed();
        }
    }
}
```

An async job method starts inside the scope and keeps it for its whole run, even though `Intercept` returns at its first `await`.

### JobInvocation

| Member | Type | Meaning |
|--------|------|---------|
| `Context` | `PerformContext` | The perform context: job, parameters, console, cancellation token, connection. |
| `Job` | `Job` | The job being performed. |
| `Invoker` | `JobInvoker` | How the method is called (`Kind`: `Generated`, `Compiled` or `Reflection`), its parameter types and return type. |
| `Instance` | `object?` | The activated instance; `null` for static methods. |
| `Arguments` | `object?[]` | Arguments converted to the parameter types, with the server's substitutions (cancellation tokens, `PerformContext`) applied. Elements may be replaced before `Proceed`. |
| `ReturnType` | `Type` | The method's return type. |
| `ReturnsTaskLike` | `bool` | Whether the method returns a `Task`, `ValueTask` or another awaitable. |
| `Proceed()` | `object?` | Runs the next interceptor or the method; returns the raw result. May be called more than once, and after an `await`: it always continues with the step after the current interceptor. |
| `ProceedAsync()` | `Task<object?>` | Like `Proceed`, but awaits a task-like result and returns the unwrapped value. |

### Interceptor order

Global interceptors run first (outermost), in registration order. Then attribute interceptors, sorted by `JobInterceptorAttribute.Order` (lower is outermost, default `0`, any `int`), then class before method, then declaration order. Interceptor attributes allow multiple instances and are **not** de-duplicated: an attribute on the class and the same attribute on the method both run. All interceptors run inside the server filters, so `OnPerforming` has already run when the first interceptor starts.

## State handlers

A state handler (`IStateHandler`, namespace `Masterly.NightShift.States`) is called for one state name: `Apply` when a job enters that state and `Unapply` when it leaves it, inside the same transaction, after the apply filters. NightShift's own handlers for Succeeded, Scheduled, Enqueued, Deleted and Awaiting are in `GlobalStateHandlers.Handlers`; a storage can add more through `JobStorage.GetStateHandlers()`. Use a handler when you introduce your own state type, or to add a storage side effect to a built-in state for every job.

Count succeeded jobs per job type:

```csharp
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

public sealed class SucceededPerTypeHandler : IStateHandler
{
    public string StateName => SucceededState.StateName;

    public void Apply(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.BackgroundJob.Job != null)
        {
            transaction.IncrementCounter($"stats:succeeded:{context.BackgroundJob.Job.Type.Name}");
        }
    }

    public void Unapply(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.BackgroundJob.Job != null)
        {
            transaction.DecrementCounter($"stats:succeeded:{context.BackgroundJob.Job.Type.Name}");
        }
    }
}

GlobalStateHandlers.Handlers.Add(new SucceededPerTypeHandler());
```

All handlers registered for a state run, in registration order. Removing a built-in handler breaks the feature it implements (for example, without the Enqueued handler jobs never reach a queue).

## Filter providers

A filter provider (`IJobFilterProvider`, namespace `Masterly.NightShift.Common`) decides which filters apply to a job at run time, without attributes. `JobFilterProviders.Providers` holds the providers; by default it contains `GlobalJobFilters.Filters` and the `JobFilterAttributeFilterProvider` that reads attributes. Add your own with `UseFilterProvider`.

Put every job from a `*.Reports` namespace on the `reports` queue:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Common;

public sealed class ReportsQueueFilterProvider : IJobFilterProvider
{
    private static readonly QueueAttribute ReportsQueue = new QueueAttribute("reports");

    public IEnumerable<JobFilter> GetFilters(Job job)
    {
        if (job.Type.Namespace?.EndsWith(".Reports", StringComparison.Ordinal) == true)
        {
            yield return new JobFilter(ReportsQueue, JobFilterScope.Type, null);
        }
    }
}
```

`new JobFilter(instance, scope, order)` takes the order from the instance when you pass `null`. The filters from all providers are sorted and de-duplicated together, as described in [Order and execution](#order-and-execution).

## Registering filters

| What | How |
|------|-----|
| A filter attribute on one method or type | Put the attribute on it: `[SlowJobWarning(60)]` |
| A filter for every job | `GlobalConfiguration.Configuration.UseFilter(filter)`, `config.UseFilter(filter)` inside `AddNightShift`, or `GlobalJobFilters.Filters.Add(filter)` / `Add(filter, order)` |
| Remove a global filter | `GlobalJobFilters.Filters.Remove(instance)`, `Remove<T>()` or `Remove(Type)`; `Clear()` removes all, including the defaults |
| An interceptor for every job | `config.UseJobInterceptor(interceptor)` or `GlobalJobInterceptors.Add(interceptor)`; `GlobalJobInterceptors.Remove` and `Clear` undo it |
| A state handler | `GlobalStateHandlers.Handlers.Add(handler)` |
| A filter provider | `config.UseFilterProvider(provider)` (adds to `JobFilterProviders.Providers`) |

`GlobalJobFilters.Filters.Add` accepts any object that implements at least one of the six filter interfaces and throws `InvalidOperationException` otherwise. Interceptors are not filters: register them with `UseJobInterceptor`, not `UseFilter`.

### With dependency injection

Filters are single instances, not resolved from the container per job. To give a filter its dependencies, use the `AddNightShift` overload that passes the service provider and register an instance:

```csharp
using Masterly.NightShift;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

builder.Services.AddSingleton<ITenantContext, AmbientTenantContext>();

builder.Services.AddNightShift((provider, config) => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Jobs"))
    .UseFilter(new TenantFilter(provider.GetRequiredService<ITenantContext>()))
    .UseFilter(new ServerErrorLoggingFilter(provider.GetRequiredService<ILogger<ServerErrorLoggingFilter>>()))
    .UseFilterProvider(new ReportsQueueFilterProvider())
    .UseJobInterceptor(new JobLogScopeInterceptor(provider.GetRequiredService<ILogger<JobLogScopeInterceptor>>())));

builder.Services.AddNightShiftServer();
```

Only inject singletons this way. For scoped services (a `DbContext`), resolve them inside the job, which the job activator creates in its own scope; see [Dependency injection](../server/dependency-injection.md).

### Without dependency injection

```csharp
GlobalConfiguration.Configuration
    .UseFilter(new SlowJobWarningAttribute(thresholdSeconds: 300))
    .UseFilter(new FailedByTypeFilter());

GlobalJobFilters.Filters.Add(new RetryQueueAttribute("retries"), order: 30);
GlobalJobInterceptors.Add(new MeasureAttribute());
GlobalStateHandlers.Handlers.Add(new SucceededPerTypeHandler());
```

## Context reference

| Context | Main members |
|---------|--------------|
| `CreateContext` (base of the client contexts) | `Job`, `InitialState` (may be `null`), `Parameters`, `Items`, `Storage`, `Connection` |
| `CreatingContext` | `Canceled` (set to cancel), `SetJobParameter(name, value)`, `GetJobParameter<T>(name)` |
| `CreatedContext` | `BackgroundJob` and `JobId` (`null` when canceled or failed), `Canceled`, `Exception`, `ExceptionHandled`, `SetJobParameter(name, value)` |
| `ClientExceptionContext` | `Exception`, `ExceptionHandled` |
| `PerformContext` (base of the server contexts) | `BackgroundJob`, `JobId`, `Job`, `CreatedAt`, `CancellationToken`, `Connection`, `Storage`, `ServerId`, `Items`, `Console`, `SetJobParameter(name, value)`, `GetJobParameter<T>(name, allowStale)` |
| `PerformingContext` | `Canceled` (set to skip the run) |
| `PerformedContext` | `Result`, `Canceled`, `Exception`, `ExceptionHandled` |
| `ServerExceptionContext` | `Exception`, `ExceptionHandled` |
| `ElectStateContext` | `BackgroundJob`, `CandidateState` (get and set), `CurrentState`, `TraversedStates`, `CustomData`, `Storage`, `Connection`, `Transaction`, `SetJobParameter<T>(name, value)`, `GetJobParameter<T>(name, allowStale)` |
| `ApplyStateContext` | `BackgroundJob`, `OldStateName`, `NewState`, `JobExpirationTimeout` (get and set), `CustomData`, `Storage`, `Connection`, `Transaction`, `GetJobParameter<T>(name, allowStale)` |

`ElectStateContext.SetJobParameter` writes to storage at once, not through the transaction.

## Things to know

- **Filters are shared.** One instance serves all jobs on all worker threads at the same time, and attribute instances are cached per method. Don't keep per-job state in fields; use `context.Items` or job parameters.
- **Register in every process.** Client filters must be registered where jobs are created, server filters and interceptors on the servers, and election and apply filters everywhere states change, which includes the dashboard and the servers. Registering the same global filters at startup in every process is the simple rule.
- **Attributes are found on the job expression's type and method.** For `Enqueue<IEmailService>(...)` that is the interface. Attributes are inherited from base classes and overridden methods.
- **Exceptions in filters fail the operation.** An exception from `OnCreating` makes creation fail (client exception filters then run). An exception from `OnPerforming` fails the job like an exception from the job. An exception from an election or apply filter makes the state change fail.
- **Keep filters fast.** Election and apply filters run on every state change of every job they apply to. Avoid storage reads you don't need, and avoid network calls entirely.
- **Changing a filter changes running jobs.** Filters are looked up when they run, not when a job is created, so a deployed change applies to jobs already in storage.

## See also

- [Filters overview](README.md)
- [Retries](retries.md)
- [Concurrency](concurrency.md)
- [Timing](timing.md)
- [Behavior](behavior.md)
- [Global configuration](../configuration/global-configuration.md)
- [Dependency injection](../server/dependency-injection.md)
- [Custom storage](../storage/custom-storage.md)
- [Contributor architecture](../contributing/architecture.md)
