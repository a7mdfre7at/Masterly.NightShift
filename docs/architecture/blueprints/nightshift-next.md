# Masterly.NightShift Next — architecture blueprint

Status: all slices implemented (see `docs/whats-new-next.md` for the user-facing summary). This document is the design the work follows. Each section names the public API it adds,
the storage keys it writes, how it degrades on storages that lack a feature, and how it is tested. Sections are
ordered the way they are implemented; later sections build on earlier ones.

Hard constraints every section respects:

- Every library package still targets `netstandard2.0;net8.0;net10.0`. New BCL usage is gated with
  `#if NET8_0_OR_GREATER` or polyfilled.
- No new members on `IStorageConnection`, `IWriteOnlyTransaction`, `IMonitoringApi`, `IRecurringJobManager`,
  `IBackgroundJobClient`. New capabilities go through base classes, `JobStorageFeatures`, new `V3` interfaces, or
  extension methods, so third-party storages keep compiling.
- No new runtime package dependencies for `Masterly.NightShift.Core` beyond what it already needs: Cronos, Microsoft.CSharp and, on `netstandard2.0` only, System.Text.Json.
- Public API breaks are caught by package validation; intentional ones go to `CompatibilitySuppressions.xml` and
  `docs/breaking-changes.md`.
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
| Generated | `Masterly.NightShift.SourceGenerator` output | Direct typed call compiled into the consumer's assembly | When the consumer references the `Masterly.NightShift.SourceGenerator` analyzer |
| Compiled | `CompiledJobInvoker` | `Expression.Lambda<Func<object, object[], object>>(...).Compile()` once per method | Default fallback where dynamic code is supported |
| Reflection | `ReflectionJobInvoker` | `MethodInfo.Invoke` | Dynamic code unsupported, or `UseReflectionJobInvocation()` |

Public API (`Masterly.NightShift.Common`):

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

**Source generator** (`src/Masterly.NightShift.SourceGenerator`, Roslyn 4.8, netstandard2.0, shipped as an analyzer in the
`Masterly.NightShift.Core` package under `analyzers/dotnet/cs`). It discovers job methods in two ways:

1. Every expression lambda passed to a Masterly.NightShift client API (`Enqueue`, `Schedule`, `ContinueJobWith`,
   `AddOrUpdate`, `Job.FromExpression`, their generic and `Task` variants, and any method in the `Masterly.NightShift`
   namespace with an `Expression<...>` parameter).
2. Every public method of a type marked `[GenerateJobInvokers]`, for jobs enqueued from another assembly.

It emits one `JobInvoker` subclass per distinct method into `Masterly.NightShift.Generated.NightShiftJobInvokers`, plus a
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
- **RateLimitAttribute** (sliding window): set `ratelimit:{key}:{perSeconds}` with members `{timestamp}:{jobId}`; entries
  older than the window are trimmed; over the limit the job is rescheduled to the moment the oldest entry expires.
- **CircuitBreakerAttribute**: an `IElectStateFilter` counts failures in `circuit:{key}:failures` (counter with
  window expiry); at the threshold it writes hash `circuit:{key}` = `{State=Open, OpenedAt}`. `OnPerforming` of
  an open circuit reschedules to `OpenedAt + OpenSeconds`. The first job after that becomes the probe (chosen
  under the circuit lock, id in the `Probe` field); other jobs are rescheduled while it runs; its success resets,
  its failure re-opens. Open circuits are listed on the Insights page and can be reset from the dashboard.

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
(`{"t":..,"l":"Info","m":".."}` / `{"t":..,"c":"name"}`), trimmed to `JobConsole.DefaultMaxLines`
(default 500); checkpoints also go to `job:{id}:checkpoints` (one entry per name, untrimmed), which `Resume` reads.
The first write sets the `Console` job parameter. `JobConsoleStateFilter` (global `IApplyStateFilter`) expires both
lists together with the job and persists them when the job is persisted (only for jobs with the `Console`
parameter, and only where the storage has `ExtendedApi`), and clears the checkpoints when a succeeded or deleted job
is requeued. Writes go through a short-lived connection of their own, one at a time.

Dashboard: `POST /jobs/live` with `jobs[]` returns, per job, `{state, serverId, startedAt, elapsedMs, progress,
lines (tail), checkpoints}`. The job details page polls it while the job is Processing and renders a progress
bar, a live console and a checkpoint timeline, then reloads when the state changes. The processing page shows a
progress bar and the elapsed time per row, updated live.

---

## 6. Insights: per-job-type statistics and anomaly hints

`JobInsightsAttribute` (global server filter, added by `UseJobInsights()`) records, per `Type.Method` (bounded to 80 characters with a hash): counters `insights:{key}:ok`,
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

## 9. Game changers (second wave)

Built on the primitives above; storage layout per feature:

| Feature | Code | Storage |
|---|---|---|
| Job Catalog | `JobCatalog`, `JobCatalogPage`, `JobCatalogRunDispatcher` (`POST /catalog/run`) | none; reads `JobInvokerRegistry.GetGeneratedInvokers()` plus `JobCatalog.Add<T>()` |
| Fan-in continuations | `AwaitingJobsState` (name `Awaiting`, data `ParentIds`, `Mode`, `Options`, `NextState`), `FanInContinuationsSupportAttribute`, `ContinueJobWithAll/Any` | parent job parameter `FanInContinuations` = JSON list of child ids; the `awaiting` set through the existing handler |
| Duplicate prevention | `PreventDuplicatesAttribute` (client + apply-state filter) | hash `fingerprint:{hash}` (`JobId`, `Reservation`, `ReservedAt`, `CreatedAt`), expiring; job parameter `Fingerprint`; lock `fingerprint:{hash}` |
| Drain mode | `DrainMode`, `DrainModeFilter` (elect filter: Processing → Scheduled +30s), `DrainModeDispatcher` (`/maintenance/pause|resume`) | hash `hangfire:control` (`Drain`, `DrainSince`, `DrainReason`), cached 2 s per process |
| Activity feed | `ActivityFeed`, `ActivityFeedFilter` (apply-state), `ActivityDispatcher` (`GET /activity`) | list `activity`, newest first; off by default (`UseActivityFeed()`); queued after commit and written in batches by a background writer, trimmed to `MaxEntries` (1000) every 100 entries |
| Alerts | `Alerts`, `AlertsFilter` (Failed state), `WebhookAlertSink`, raises from `DeadlineAttribute` and `CircuitBreakerAttribute` | none; HTTP POST in the background, bounded in-flight |
| Schedule forecast | `ScheduleForecast`, `ForecastPage` (`/recurring/forecast`) | reads recurring job hashes and `insights:*` durations |
| ETA | `JobInsight.EstimateCompletion`, Processing page | reads `insights:{key}:durations` |
| Copy as code | `JobCodeRenderer`, job details page | none |
| Anomalies | `JobInsight.IsSlowingDown`, `HasHighFailureRate`, `IsAnomalous` | reads the last 200 durations |

## 10. Game changers (third wave)

Diagnosis, lineage and clusters are dashboard-only readers over existing data; tags, retry-later and manifests
add small, opt-in writes.

| Feature | Code | Storage |
|---|---|---|
| Diagnosis | `JobDiagnostics` (internal, `Dashboard`), job details page; findings carry a stable `Code` | reads servers, queues, `hangfire:control`, parent `JobData`, `insights:*`, circuits |
| Lineage | `JobLineage` (BFS, 4 levels, 40 nodes), `JobLineageRenderer` (inline SVG) | reads `Awaiting` state data and the `Continuations` / `FanInContinuations` parameters through `IMonitoringApi.JobDetails` |
| Tags | `TagAttribute` (client filter), `JobTags`, `TagsPage` (`/tags`), `TaggedJobsPage` (`/tags/{tag}`), `/tags/forget/{tag}`, `/tags/forget-expired/{tag}`, palette `tags` results | set `tags` (names), set `tags:{tag}` (job ids scored by creation time), job parameter `Tags` (JSON array); expired ids hidden by the tag page and removed with `POST /tags/forget-expired/{tag}` |
| Failure clusters | `FailureClusters` (latest 500 via `FailedJobs`, groups of ≥2, top 8), Failed jobs page, batch requeue/delete through the existing `/jobs/failed/*` commands | none |
| Retry later | `RetryLaterException`, `AutomaticRetryAttribute.TryRetryLater`, `MaxRetryLaterRequests` | job parameter `RetryLaterCount`; `retries` set via the "Retry later" reason prefix |
| Redaction | `RedactedAttribute`, `JobMethodCallRenderer`, `JobCodeRenderer`, `JobCatalog.FormatArguments` | none (display only) |
| Manifests | `RecurringJobManifest` (`Export`, `Parse`, `Apply` with prune), `RecurringJobManifestDispatcher` (`GET /recurring/export`, `POST /recurring/import`) | the recurring job hashes, through `IRecurringJobManager` |
| Health | `DashboardHealth.Check`, `HealthDispatcher` (`GET /health`, 503 when unhealthy) | reads statistics, servers, queues, `hangfire:control`, `recurring-jobs` set scores (full scan as fallback); cached 5 s |
| Late recurring jobs | `DashboardHealth.IsLate` (next run or error retry > 1 min ago, not paused), Recurring Jobs page badge and summary | reads recurring job hashes |
| Run again with changes | `JobCatalog.CreateEntry(Job)`, `JobCatalog.FormatArguments`, `/catalog/run` with `source`, `_JobRunner` partial shared with the Catalog page | reads the source job's `JobData` |

## 11. Game changers (fourth wave)

| Feature | Code | Storage |
|---|---|---|
| Batches | `BatchJob` (`StartNew` with an `AsyncLocal` current batch, `ContinueWith` = fan-in over the batch), `IBatchBuilder`, `JobBatch`, `BatchSupportAttribute` (default global client + apply-state filter), `BatchesPage`, `BatchDetailsPage`, `BatchCommands` | set `batches`; hash `batch:{id}` (`Description`, `CreatedAt`); set `batch:{id}:jobs`; counters `batch:{id}:total`, `batch:{id}:state:{name}` (expiring); set `batch:{id}:continuations`; job parameters `BatchId`, `BatchContinuationOf` |
| Per-queue pause | `DrainMode.PauseQueue/ResumeQueue/GetStatus`, `DrainModeStatus.PausedQueues`, `DrainModeFilter` (one state read when any queue is paused, and one per hold of a job without a queue of its own), `QueuePauseDispatcher`, Queues page, layout banner | fields `Queue:{name}` of `hangfire:control` |
| Prometheus | `PrometheusDispatcher` (`GET /metrics`), built on `DashboardHealth`, `JobInsightsAttribute.ReadAll`, `QueueInsights.ReadAll` | none |
| Audit log | `AuditLog` (`Record`, `Read`, `Describe`), `DashboardContext.GetUserName`, recording in the command, batch, maintenance, drain, queue, catalog, recurring update and manifest dispatchers, `AuditPage` | list `audit`, newest first, trimmed to 1000 |
| Resumable jobs | `IJobConsole.Checkpoints` / `HasCheckpoint`, `JobConsoleExtensions.Resume/ResumeAsync`, lazy read of `job:{id}:checkpoints` (falling back to checkpoints older builds left in `job:{id}:console`) in `JobConsole` | the console list and `job:{id}:checkpoints` |
| Retention | `ExpireAfterAttribute` (apply-state filter setting `JobExpirationTimeout` on final states) | none |
| Run windows | `RunWindowAttribute`, `BlackoutAttribute` (elect filters: Processing → Scheduled at the next allowed moment) | none |
| Queue health | `QueueInsights`, `QueueHealth`, `QueueInsightsFilter` (registered by `UseJobInsights`), Queues page columns | set `queue-insights`; list `queue:{name}:latency` (200); counters `queue:{name}:started:{yyyyMMddHHmm}` (1 h); hash `queue:{name}:insights` |
| Plain-English scheduling | `TimeText.Parse/TryParse`, `BackgroundJob.Schedule(..., string when)` and client extension overloads, Catalog "later" field | none |

## 12. Dashboard (fifth wave)

| Feature | Code | Storage |
|---|---|---|
| Wallboard | `WallboardPage` (`/wallboard`, no layout, meta refresh 10 s), built on `DashboardHealth`, `QueueInsights`, `JobInsight.EstimateCompletion`, circuits | reads only |
| Timeline | `ClusterTimeline` (spans from Processing → next transition per job, lanes per server/worker), `TimelinePage` (`/timeline`) | reads the `activity` list; `ActivityEntry.WorkerId` added |
| Heatmap | `ActivityHeatmap` (7 × 24 `GetCounter` reads), Insights page | hourly `stats:*` counters now kept 8 days |
| Notes | `JobNotes` (`Add`, `Read`, `GetAcknowledgement`), `JobNotesDispatcher` (`POST /jobs/notes/add/{id}`), job details and Failed pages | job parameter `Notes` (JSON array, 50 max) |
| Watchlist | `nightshift.Watchlist` (localStorage `nightshift.watch`, polls `GET /jobs/live?jobs=`), `.js-watch-toggle` on the job page, toasts + Notification API | none (per browser) |
| Keyboard triage | `nightshift.Shortcuts` (j/k/x/Enter/r/d, `?` overlay) | none |
| Palette commands | `CommandPalette._commands` (`>` prefix) posting to the existing action routes | none |
| Per-server pause | `DrainMode.PauseServer/ResumeServer`, `DrainModeStatus.PausedServers`, `DrainModeFilter` (checks the candidate `ProcessingState.ServerId`), `ServerPauseDispatcher`, Servers page | fields `Server:{id}` of `hangfire:control` |
| Exports | `JobExportDispatcher` (`/jobs/export/{id}`, JSON), `JobListExportDispatcher` (CSV, 10,000 rows, formula characters neutralised), `.js-download` | reads only |
| State bar | job details page, from the state history | none |

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

Every slice ends with `dotnet build Masterly.NightShift.slnx -c Release` (warnings are errors) and
`dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0`.
