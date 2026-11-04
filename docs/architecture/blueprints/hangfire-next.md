# Hangfire Next — architecture blueprint

Status: all slices implemented (see `docs/whats-new-next.md` for the user-facing summary). This document is the design the work follows. Each section names the public API it adds,
the storage keys it writes, how it degrades on storages that lack a feature, and how it is tested. Sections are
ordered the way they are implemented; later sections build on earlier ones.

Hard constraints every section respects:

- Every library package still targets `netstandard2.0;net8.0;net10.0`. New BCL usage is gated with
  `#if NET8_0_OR_GREATER` or polyfilled.
- No new members on `IStorageConnection`, `IWriteOnlyTransaction`, `IMonitoringApi`, `IRecurringJobManager`,
  `IBackgroundJobClient`. New capabilities go through base classes, `JobStorageFeatures`, new `V3` interfaces, or
  extension methods, so third-party storages keep compiling.
- No new runtime package dependencies for `Hangfire.Core` (Newtonsoft.Json stays the only one).
- Public API breaks are caught by package validation; intentional ones go to `CompatibilitySuppressions.xml` and
  `docs/breaking-changes-2.0.md`.
- All new storage data lives in the existing primitives (hash, set, list, counter, job parameters), so every storage
  supports every feature out of the box. Richer behaviour is unlocked when `JobStorageConnection` /
  `JobStorageTransaction` members are available (expiry of lists/hashes, set ranges).

---

## 1. Zero-reflection job invocation

**Problem.** `CoreBackgroundJobPerformer` performs every job with `MethodInfo.Invoke`: slow, allocates, boxes,
wraps exceptions in `TargetInvocationException`, and defeats trimming/AOT analysis.

**Design.** A `JobInvoker` abstraction with three tiers, resolved per method and cached:

| Tier | Class | How | When |
|---|---|---|---|
| Generated | `Hangfire.SourceGenerator` output | Direct typed call compiled into the consumer's assembly | When the consumer references the `Hangfire.SourceGenerator` analyzer |
| Compiled | `CompiledJobInvoker` | `Expression.Lambda<Func<object, object[], object>>(...).Compile()` once per method | Default fallback where dynamic code is supported |
| Reflection | `ReflectionJobInvoker` | `MethodInfo.Invoke` | Dynamic code unsupported, or `UseReflectionJobInvocation()` |

Public API (`Hangfire.Common`):

```csharp
public abstract class JobInvoker
{
    protected JobInvoker(Type type, string methodName, Type[] parameterTypes, Type[] genericArguments);
    public Type Type { get; }               // declaring type the invoker casts the instance to
    public string MethodName { get; }
    public Type[] ParameterTypes { get; }
    public Type ReturnType { get; }
    public bool IsStatic { get; }
    public MethodInfo Method { get; }       // resolved lazily, still needed by filters
    public JobInvokerKind Kind { get; }     // Generated | Compiled | Reflection
    public abstract object Invoke(object instance, object[] arguments);
}

public static class JobInvokerRegistry
{
    public static void Register(JobInvoker invoker);          // called by generated module initializers
    public static JobInvoker GetInvoker(MethodInfo method);   // never null; cached
    public static JobInvokerStatistics GetStatistics();       // counts per kind, for the dashboard
}

[AttributeUsage(Class | Interface | Struct)] public sealed class GenerateJobInvokersAttribute : Attribute;
```

Registry key is `(declaring type, method name, parameter types, generic arguments)`, so a job created from an
interface, a base class or a derived instance resolves to the same invoker. `Invoke` throws the job's exception
directly (no `TargetInvocationException`), which `HandleJobPerformanceException` already handles.

**Source generator** (`src/Hangfire.SourceGenerator`, Roslyn 4.8, netstandard2.0, shipped as an analyzer in the
`Hangfire.Core` package under `analyzers/dotnet/cs`). It discovers job methods in two ways:

1. Every expression lambda passed to a Hangfire client API (`Enqueue`, `Schedule`, `ContinueJobWith`,
   `AddOrUpdate`, `Job.FromExpression`, their generic and `Task` variants, and any method in the `Hangfire`
   namespace with an `Expression<...>` parameter).
2. Every public method of a type marked `[GenerateJobInvokers]`, for jobs enqueued from another assembly.

It emits one `JobInvoker` subclass per distinct method into `Hangfire.Generated.HangfireJobInvokers`, plus a
`[ModuleInitializer]` that registers them (with an internal `ModuleInitializerAttribute` polyfill when the target
framework lacks it). Methods it cannot call safely are skipped silently and fall back to the compiled tier: open
generics, inaccessible types or members, pointer/ref-struct parameters.

**Tests.** `JobInvokerRegistryFacts`, `CompiledJobInvokerFacts`; `CoreBackgroundJobPerformerFacts` runs on every
tier; the test project references the generator as an analyzer so every lambda in the suite is generated and
exercised, plus `GeneratedJobInvokerFacts` asserts that specific methods resolve to `JobInvokerKind.Generated`.

---

## 2. AOP: job interceptors

Filters (`IServerFilter`) are before/after hooks. Interceptors add true *around* advice with access to the
instance, mutable arguments and the ability to call the method several times or not at all.

```csharp
public interface IJobInterceptor { object Intercept(JobInvocation invocation); }

public sealed class JobInvocation
{
    public PerformContext Context { get; }
    public Job Job { get; }
    public object Instance { get; }
    public object[] Arguments { get; }           // mutable
    public Type ReturnType { get; }
    public bool ReturnsTaskLike { get; }
    public object Proceed();                     // next interceptor, finally the job method
    public Task<object> ProceedAsync();          // awaits task-like results, returns the unwrapped value
}

[AttributeUsage(Class | Interface | Method, AllowMultiple = true, Inherited = true)]
public abstract class JobInterceptorAttribute : Attribute, IJobInterceptor { public int Order { get; set; } }

public abstract class AsyncJobInterceptorAttribute : JobInterceptorAttribute
{
    protected abstract Task<object> InterceptAsync(JobInvocation invocation);   // result re-shaped to the method's return type
}

public static class GlobalJobInterceptors { public static IList<IJobInterceptor> Interceptors { get; } }
```

Interceptors are discovered from attributes on the type and the method (cached per `MethodInfo`) plus the global
list, ordered by `Order`, and run inside `CoreBackgroundJobPerformer` around the invoker call, so they execute
on the job's synchronization context and see the activated instance.

Built-in interceptors:

- `[JobTimeout(seconds)]` — cancels the job's `CancellationToken`/`IJobCancellationToken` arguments after the
  timeout and fails the job with `TimeoutException`.
- `[RetryInProcess(attempts, delayMilliseconds, exceptionTypes...)]` — re-invokes the method in place for transient
  failures, without a storage round trip or a Failed state entry.
- `[LogInvocation]` — writes start/finish/exception lines to the job console (section 5).

---

## 3. Flexible concurrency control

All built on the existing distributed lock + set primitives, so every storage supports them. Keys are templates
like `DisableConcurrentExecution`: `{0}`..`{n}` are job arguments; the default key is `Type.Method`.

```csharp
public enum ConcurrencyAction { Wait, Skip, Reschedule }

[Throttle("tenant-{0}", MaxConcurrency = 3, Action = ConcurrencyAction.Reschedule, RetryDelaySeconds = 30)]
[RateLimit("sms", Limit = 100, PerSeconds = 60)]
[CircuitBreaker("payments", FailureThreshold = 5, WindowSeconds = 60, OpenSeconds = 120)]
```

- **ThrottleAttribute** (semaphore): `OnPerforming` acquires `throttle:{key}:lock`, reads set `throttle:{key}`
  (member = job id, score = Unix time), drops stale members older than `StaleTimeout`, and admits the job if the
  count is below `MaxConcurrency`. Otherwise: `Wait` polls with backoff until `WaitTimeoutSeconds`; `Skip` cancels
  the invocation (job is Deleted); `Reschedule` moves the job to `ScheduledState(now + RetryDelay)` through the state
  changer and cancels the invocation (the Worker's Processing→final transition is then a no-op, by design).
  `OnPerformed` removes the member. `MaxConcurrency = 1` is a distributed mutex that never blocks a worker thread.
- **RateLimitAttribute** (sliding window): set `ratelimit:{key}` with members `{timestamp}:{jobId}`; entries
  older than the window are trimmed; over the limit the job is rescheduled to the moment the oldest entry expires.
- **CircuitBreakerAttribute**: an `IElectStateFilter` counts failures in `circuit:{key}:failures` (counter with
  window expiry); at the threshold it writes hash `circuit:{key}` = `{State=Open, OpenedAt}`. `OnPerforming` of
  an open circuit reschedules to `OpenedAt + OpenSeconds`. The first job after that runs half-open; success
  resets, failure re-opens. Open circuits are listed on the Insights page and can be reset from the dashboard.

Tests mock `IStorageConnection`/`IWriteOnlyTransaction` with Moq and assert the state transitions, set writes and
stale-entry cleanup.

---

## 4. Recurring jobs managed from the dashboard

- `RecurringJobEntity.Paused` persisted as hash field `Paused` (`"1"`). Pausing sets `NextExecution = null` so the
  job leaves the schedule (`recurring-jobs` score `-1`), resuming reschedules from now. Pause/resume never trigger
  misfire catch-up.
- `IRecurringJobManagerV3 : IRecurringJobManagerV2` adds `Pause`, `Resume`, `UpdateSchedule(id, cron, timeZone?,
  queue?, misfireHandling?)`; `RecurringJobManager` implements it, `RecurringJob` gets static facades, and extension
  methods on `IRecurringJobManager` dispatch to V3 or throw `NotSupportedException`.
- `RecurringJobDto.Paused`.
- `Cron.FromText("every 15 minutes")`: a natural-language parser for the common phrases (every N
  seconds/minutes/hours/days, daily/weekly/monthly at HH:mm, weekdays, named days), and `Cron.Describe()` for
  the dashboard preview of the next occurrences.
- Dashboard: `/recurring/pause` and `/recurring/resume` batch commands; `/recurring/update/{id}` form command
  (cron, time zone, queue, misfire) answering `204` or `422 application/json {error}`; `/recurring/preview`
  (`cron`, `tz`) returning the next 5 occurrences; an inline editor modal on the recurring jobs page with a live
  preview; a Paused badge.

---

## 5. Real-time monitoring: progress, console, checkpoints, elapsed time

```csharp
public interface IJobConsole
{
    void SetProgress(double percent, string message = null);
    void WriteLine(string message);              // Info
    void WriteLine(JobConsoleLevel level, string message);
    void Checkpoint(string name);                // "executed statements" timeline
}
PerformContext.Console  // IJobConsole; also injectable as a job method parameter (argument substitution)
```

Storage: progress is the job parameter `Progress` = `{"p":42.5,"m":"...","t":<unix ms>}` (one write per
update, no list growth). Console lines and checkpoints are entries in list `job:{id}:console`
(`{"t":..,"l":"Info","m":".."}` / `{"t":..,"c":"name"}`), trimmed to `JobConsoleOptions.MaxLines`
(default 500). `JobConsoleStateFilter` (global `IApplyStateFilter`) expires the list together with the job and
persists it when the job is persisted, where `JobStorageTransaction.ExpireList`/`PersistList` are available.

Dashboard: `POST /jobs/live` with `jobs[]` returns, per job, `{state, serverId, startedAt, elapsedMs, progress,
lines (tail), checkpoints}`. The job details page polls it while the job is Processing and renders a progress
bar, a live console and a checkpoint timeline, then reloads when the state changes. The processing page shows a
progress bar and the elapsed time per row, updated live.

---

## 6. Insights: per-job-type statistics and anomaly hints

`JobInsightsFilter` (global server + elect-state filter) records, per `Type.Method`: counters `insights:{key}:ok`,
`insights:{key}:failed`, list `insights:{key}:durations` (last 200 durations, ms), hash `insights:{key}` with
`LastRunAt`, `LastDurationMs`, `LastJobId`, `Invoker` (Generated/Compiled/Reflection), and set `insights` of all
keys. The Insights page shows runs, failure rate, average/p95/max duration, a sparkline, the invoker tier, open
circuits (section 3) and flags jobs whose last duration is more than 3x their p95.

---

## 7. Dashboard redesign

- Visual refresh on top of Bootstrap 3 using CSS custom properties: typography, cards, pills, table density,
  metric tiles, refined navbar; dark mode re-themed from the same tokens.
- Command palette (`Ctrl+K` / `/`): jump to pages, open a job by id, find recurring jobs by id (served by
  `GET /search?q=`), trigger actions.
- Live tables: the Processing and Enqueued pages refresh their rows in place at the stats polling interval while
  no row is selected.
- Keyboard shortcuts (`g j` jobs, `g r` recurring, `g s` servers, `?` help).

---

## 8. Smaller additions

- Invoker statistics in the Servers page footer.
- `Cron` builder: `Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays().ToString()`.
- `JobDeadline`: `[Deadline("00:10:00")]` marks the job parameter `DeadlineAt`; the processing page highlights
  overdue jobs.

---

## Delivery order

| Slice | Contents | Depends on |
|---|---|---|
| 1 | Job invokers (compiled + reflection), registry, performer change, source generator, analyzer packaging | — |
| 2 | Interceptors, built-ins | 1 |
| 3 | Throttle, RateLimit, CircuitBreaker | — |
| 4 | Recurring pause/resume/update, Cron.FromText, dashboard editor | — |
| 5 | Job console + live endpoint + details/processing UI | — |
| 6 | Insights filter + page | 1, 3, 5 |
| 7 | Dashboard redesign, command palette, live tables | 4, 5, 6 |
| 8 | Smaller additions | any |

Every slice ends with `dotnet build Hangfire.sln -c Release` (warnings are errors) and
`dotnet test tests/Hangfire.Core.Tests -f net8.0`.
