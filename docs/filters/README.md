# Job filters

Filters are attributes and classes that run around a background job's life: when it is created, when a state change is chosen, when the change is written, and when the job runs. Retries, queues, concurrency limits, timeouts and run windows are all filters. You add one to a job by putting an attribute on its method or class, or to every job by registering it globally.

| Page | What it covers |
|------|----------------|
| [Retries](retries.md) | `AutomaticRetry` (the default retry policy), `AttemptsExceededAction`, `RetryLaterException`, `RetryInProcess`, `CircuitBreaker` |
| [Concurrency](concurrency.md) | `DisableConcurrentExecution`, `Throttle`, `RateLimit`, `ConcurrencyAction`, `PreventDuplicates`, `IdempotentCompletion` |
| [Timing](timing.md) | `JobTimeout`, `Deadline`, `LatencyTimeout`, `RunWindow`, `Blackout`, `ExpireAfter` |
| [Behavior](behavior.md) | `Queue`, `JobDisplayName`, `CaptureCulture`, `LogInvocation`, `Redacted` |
| [Custom filters](custom-filters.md) | Writing client, server, election, apply and exception filters, interceptors and state handlers, and registering them |

## Quick example

```csharp
using Masterly.NightShift;

public class EmailService
{
    [Queue("critical")]
    [AutomaticRetry(Attempts = 3)]
    public void SendPasswordReset(int userId)
    {
        // ...
    }
}

// In a controller or service that gets IBackgroundJobClient from DI:
jobs.Enqueue<EmailService>(x => x.SendPasswordReset(42));
```

The job goes to the `critical` queue and is retried at most three times instead of the default ten.

## How it works

A filter is an object that implements one or more filter interfaces. NightShift calls them at fixed points of the [job lifecycle](../getting-started/core-concepts.md):

| Interface | Namespace | Called | Typical use |
|-----------|-----------|--------|-------------|
| `IClientFilter` | `Masterly.NightShift.Client` | `OnCreating` before a job is stored, `OnCreated` after | Add job parameters, cancel creation (`PreventDuplicates`, `Deadline`, `CaptureCulture`) |
| `IServerFilter` | `Masterly.NightShift.Server` | `OnPerforming` before the job method runs, `OnPerformed` after | Locks, slots, context restore (`DisableConcurrentExecution`, `Throttle`, `RateLimit`) |
| `IElectStateFilter` | `Masterly.NightShift.States` | `OnStateElection` while the next state is chosen | Replace the candidate state (`AutomaticRetry` turns Failed into Scheduled, `Queue` sets the queue) |
| `IApplyStateFilter` | `Masterly.NightShift.States` | `OnStateUnapplied` / `OnStateApplied` inside the state-change transaction | Extra storage writes (`ExpireAfter`, the retries set) |
| `IClientExceptionFilter` | `Masterly.NightShift.Client` | When creating a job throws | Log or swallow creation errors |
| `IServerExceptionFilter` | `Masterly.NightShift.Server` | When performing a job throws | Log or swallow job errors |

Interceptors (`IJobInterceptor`, `JobInterceptorAttribute`, `AsyncJobInterceptorAttribute`) are a separate, newer mechanism. They wrap the call of the job method itself, inside the server filters. `JobTimeout`, `RetryInProcess` and `LogInvocation` are interceptors. See [Custom filters](custom-filters.md#interceptors).

### Where a filter applies

| Where | How | Scope |
|-------|-----|-------|
| One method | Attribute on the job method | `JobFilterScope.Method` |
| Every method of a type | Attribute on the class or interface | `JobFilterScope.Type` |
| Every job | `GlobalConfiguration.Configuration.UseFilter(...)`, `config.UseFilter(...)` inside `AddNightShift`, or `GlobalJobFilters.Filters.Add(...)` | `JobFilterScope.Global` |

Filters are read from the type and method in the job expression. For `Enqueue<IEmailService>(x => x.Send(...))` that is the interface and its method, so put the attributes on the interface, not on the class that implements it.

Most filter attributes can appear only once per job. When the same filter type is found more than once (globally, on the class and on the method), the one that sorts last wins: the one with the higher `Order`, and for the same `Order` the method attribute beats the class attribute, which beats the global one. That is how `[AutomaticRetry(Attempts = 0)]` on a method replaces the global retry filter: both have `Order` 20. Don't give an attribute a lower `Order` than the filter it should replace: `[AutomaticRetry(Attempts = 0, Order = 5)]` on a method loses to the global `AutomaticRetry`, and the job is retried ten times.

### Order

Within one kind of filter, filters run sorted by their `Order` property (lower first, `-1` by default), then by scope: global, type, method. `OnCreated` and `OnPerformed` run in reverse, like nested `using` blocks, and exception filters run in reverse order too. Some built-in filters set their own `Order`: `AutomaticRetry` 20, `PreventDuplicates` 10, `RunWindow` 15, `ExpireAfter` 50, `Queue` `Int32.MaxValue` (it always decides the queue last). Details and examples are in [Custom filters](custom-filters.md#order-and-execution).

### Filters registered by default

`GlobalJobFilters.Filters` starts with these filters, so every job gets them without any configuration:

| Filter | What it does |
|--------|--------------|
| `CaptureCultureAttribute` | Runs the job with the culture of the code that created it ([Behavior](behavior.md#capturecultureattribute)) |
| `AutomaticRetryAttribute` | Retries failed jobs up to 10 times ([Retries](retries.md)) |
| `StatisticsHistoryAttribute` | Records succeeded and failed counts for the dashboard graphs |
| `ContinuationsSupportAttribute` | Starts continuations when their parent finishes ([Continuations](../jobs/continuations.md)) |
| `FanInContinuationsSupportAttribute` | Starts fan-in continuations ([Fan-in and batches](../jobs/fan-in-and-batches.md)) |
| `JobConsoleStateFilter` | Expires a job's console lines together with the job |
| `DrainModeFilter` | Holds jobs while the server is draining ([Drain mode](../server/drain-mode.md)) |
| `ActivityFeedFilter` | Feeds the dashboard activity feed once it is turned on with `UseActivityFeed()` ([Audit log and activity](../monitoring/audit-log-and-activity.md)) |
| `BatchSupportAttribute` | Tracks batch jobs ([Fan-in and batches](../jobs/fan-in-and-batches.md)) |

Removing one of these, for example `GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>()`, turns that feature off for every job. Removing `DrainModeFilter` turns off drain mode only: `RunWindow`, `Throttle`, `RateLimit` and `CircuitBreaker` still put the jobs they hold back in their queue on their own.

## Things to know

- Register global filters in every process that creates jobs, runs a server or hosts the dashboard. Client filters run where `Enqueue` is called, server filters run on the server, and state filters run wherever a state changes, which includes the dashboard when someone clicks Requeue or Delete.
- One filter instance serves every job and every worker thread at the same time. Keep filters free of per-job fields; put per-job data in `context.Items` or in job parameters.

## See also

- [Retries](retries.md)
- [Concurrency](concurrency.md)
- [Timing](timing.md)
- [Behavior](behavior.md)
- [Custom filters](custom-filters.md)
- [Core concepts](../getting-started/core-concepts.md)
- [Global configuration](../configuration/global-configuration.md)
