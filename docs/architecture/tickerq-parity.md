# TickerQ parity

What NightShift takes from TickerQ, phase by phase. The sources are TickerQ's open-source docs
(<https://tickerq.net/docs>) and its commercial 5.x docs (<https://tickerq.net/docs/5.x>). TickerQ 5.x is
source-available under a commercial license, so every feature is built from its documented behaviour; no TickerQ
code is copied.

Status:
- **Done**: NightShift already does it.
- **Enhance**: NightShift does part of it; the phase adds the rest.
- **Port**: new in NightShift.

Every phase follows the rules in `CLAUDE.md`: storage writes only through existing primitives, no new members on the
storage or client interfaces, `HasFeature` checks before optional members, no hot keys inside state transactions,
dashboard writes through POST commands with the read-only check, strings in all 15 locales, and docs pages in
`docs/` with `website/sidebars.js` and the section's `README.md` table updated.

## Testing rules for every phase

- Every public member gets unit tests: happy path, each error path (null and invalid arguments), and the edge
  cases (empty, zero, already in the target state, concurrent callers where it matters).
- Filters are tested through `ElectStateContextMock` / `PerformContextMock` and, where the behaviour depends on the
  whole pipeline (filters, performer, worker, state machine), end to end on SQL Server in
  `tests/Masterly.NightShift.SqlServer.Tests`.
- Dashboard pages are rendered with `PageRenderer`; dispatchers are tested for the read-only check and bad input.
- NetCore and AspNetCore additions are tested in `tests/Masterly.NightShift.AspNetCore.Tests`.
- A phase is done when `dotnet build Masterly.NightShift.slnx -c Release` has no warnings, the Core, SqlServer and
  AspNetCore tests pass on `net8.0` and `net10.0`, and `npm run build` in `website/` passes.

## Phase 1: Job execution control (done)

`TerminateJobException(Canceled)` is NightShift's `context.RequestCancellation()`: the job ends itself as canceled, without a retry.

| TickerQ | Status | NightShift |
|---|---|---|
| `TickerFunctionContext.ScheduledFor` | Port | `PerformContext.ScheduledFor`: the intended run time of a delayed job (new `ScheduledFor` parameter written at creation) or of a recurring job's occurrence (`Time` parameter); `null` for a fire-and-forget job. |
| `TickerFunctionContext.IsDue` (on-time vs late) | Port | `PerformContext.Lateness`: how long after `ScheduledFor` (or after creation) the run started. `PerformContext.RecurringJobId`. |
| Late starts `WhenMissed(SkipAfter(window))` | Port | `[SkipIfLate("00:05:00")]`: a run that starts later than the window is deleted with the reason `Skipped: ...`, without running or using a retry. Checked once per job, so a manual requeue or a retry runs. |
| `TerminateExecutionException` (skip, fail without retry), `context.RequestCancellation()` | Port | `TerminateJobException(reason, TerminateJobOutcome.Skipped / Canceled / Failed)`: Skipped and Canceled delete the job (`Skipped: ...`, `Canceled: ...`), Failed fails it without retries. Telemetry reports `skipped`. |
| Timeout cancellation is terminal and uses no retry | Enhance | `[JobTimeout(seconds, OnTimeout = JobTimeoutAction.Retry / Fail / Cancel)]`. |
| `ITickerExceptionHandler` (`HandleExceptionAsync`, `HandleCanceledExceptionAsync`) | Port | `IJobExceptionHandler` with `OnJobException` (every failed attempt) and `OnJobCanceled`; `UseJobExceptionHandler(handler)`, and `AddNightShiftJobExceptionHandler<T>()` in DI (a scope per call). A handler that throws is logged and never changes the job. |
| `GetOutcomeAsync` | Done | `storage.GetJobState(id)`, `storage.TryGetResult<T>(id, out result)`. |
| `WaitForCompletionAsync(id, timeout)` | Port | `storage.WaitForCompletionAsync(id, timeout, ct)` and `JobStateInfo.IsFinal`. |

## Phase 2: Scheduling (done)

Seed retirement is opt-in (`RetireUndeclared`), and only touches jobs a set declared. The dashboard badge for declared jobs comes with the recurring job details page in Phase 8.

| TickerQ | Status | NightShift |
|---|---|---|
| `SeedCronTickers` | Enhance | `RecurringJobSet` / `AddNightShiftRecurringJobs` with `%Section:Key%` schedules exist. Add: a removed declaration is paused ("retired") instead of deleted when asked, a **From code** badge on the dashboard. |
| `SeedTimeTickers` (one-time startup work, once per key) | Port | `RecurringJobSet.Once("key", ...)` / `AddNightShiftStartupJobs`: created once per key (hash `seeded-once`). |
| Per-schedule `WhenMissed(SkipAfter)` | Port | `RecurringJobOptions.SkipAfter` (stored with the recurring job, copied to the job) and `ScheduleOptions.SkipAfter` for delayed jobs, using the Phase 1 check. |
| Cron catch-up `RunLatest` | Enhance | `MisfireHandlingMode.RunLatestWithin`: only the newest missed slot inside `SkipAfter`. |
| Default cron time zone | Port | `UseDefaultRecurringJobTimeZone(tz)`. |
| Cron occurrence history | Port | Run history per recurring job (last 50 runs, including slots skipped by a policy or overlap) and `RecurringJobHistory.Read`. |
| `GetNextOccurrencesAsync` | Enhance | `RecurringJob.GetNextOccurrences(id, count)` from code. |
| `ITickerClock` | Done | `JobClock`, `UseTimeProvider`. |

## Phase 3: Schedule policies (business calendars) (done)

Policies are checked when a job is created and when the delayed job scheduler enqueues it, not when a worker picks it up, so a manual enqueue bypasses them; a job already created whose due time a changed policy now blocks is skipped rather than rejected. The policies page is part of Phase 8.

| TickerQ | Status | NightShift |
|---|---|---|
| Named, stored, revisioned calendars | Enhance | `[RunWindow]` and `[Blackout]` exist. `SchedulePolicy`: `AllowWeekdays`, `AllowBetween`, `ExcludeDate`, `ExcludeAnnualDate`, `ExcludeDatesFrom(IScheduleDateProvider)`, `Require`/`ExceptWhen`, time zone; stored in hash `schedule-policy:{name}` with a revision; `SaveSchedulePolicy`, `UpdateSchedulePolicy(expectedRevision)`, `DeleteSchedulePolicy(expectedRevision)`, `EvaluateSchedulePolicy`, `FindNextAllowed`, `PreviewSchedulePolicy`. |
| `WhenBlocked`: Reject, Skip, MoveForward | Port | For fire-and-forget and delayed jobs (`ScheduleOptions.Policy`, `SchedulePolicyFilter`); blocked recurring occurrences are left out (`RecurringJobOptions.Policy`). Enqueue now and requeue bypass the policy. |
| `SeedSchedulePolicies` | Port | `RecurringJobSet.AddPolicy`, saved at start-up before the jobs; date providers with `UseScheduleDateProvider` / `AddNightShiftScheduleDateProvider<T>`. |
| Policies page with a cron preview | Port | Dashboard page (Phase 8). |

## Phase 4: Continuations and chains (done)

Per-step retries and timeouts come from each method's attributes, as for any job; a step's options are its continuation options and its queue. Delete behaviours are `Cascade` and `Restrict` (refuses while a step is processing). The chain page and builder are part of Phase 8.

| TickerQ | Status | NightShift |
|---|---|---|
| `RunCondition.OnFailure`, `InProgress` | Port | `JobContinuationOptions.OnlyOnFailedState`, `OnlyOnProcessingState`; non-matching continuations stay Awaiting when the parent fails. |
| `ThenMany` joins: AfterAll, AnyFailed, AnyFailedOrCancelled, AllCompleted | Enhance | `FanInMode.AnyFailed`, `AnyFailedOrCanceled`, `AllCompleted` and `ContinueJobWithJoin(parentIds, mode, ...)`. |
| `AddChainAsync` builder | Port | `JobChain.StartWith(...).Then(...).ThenMany(...).ThenAfterAll(...)`, recorded in `chain:{id}`; `storage.GetChain`. |
| `RetryFromAsync` | Port | `storage.RetryChainFrom(stepJobId)`: reruns a settled chain from a step without re-running its ancestors. |
| Chain delete behaviour, `ReplaceChainAsync` | Port | `ChainDeleteBehavior.Cascade` / `Restrict`, `storage.DeleteChain`, `storage.ReplaceChain`. |
| Recurring chains | Port | `RecurringJob.AddOrUpdateChain(id, chain, cron)`. |
| `TickerFunctionContext.ParentId` | Port | `PerformContext.ParentId` for continuations and chain steps. |
| Parent result to child | Done | `UseResultsInContinuations()`, `[FromResult]`. |

## Phase 5: Functions (done)

A method function's job is an ordinary job of the method; a delegate function's job calls `DelegateJobFunctionRunner`, so every server must register the delegate.

| TickerQ | Status | NightShift |
|---|---|---|
| `ITickerFunction<TRequest>`, `MapTicker<T>()` | Done | `IJob`, `IJobHandler<TArgs>`, `AddNightShiftJobHandler<T>()`. |
| `[TickerFunction("name")]`, schedule by name | Port | `[JobFunction("name")]`, `JobFunctions.Add`/`AddAssembly`, `EnqueueFunction(name, payload)` / `ScheduleFunction` / `AddOrUpdateFunction`, with the payload checked at creation. |
| Delegate registration `MapTicker("name", handler)` | Port | `services.AddNightShiftFunction(name, handler)` and `AddNightShiftFunction<TPayload>`; `AddNightShiftFunctions(assemblies)`. |
| `CancelPendingAsync<TFunction>()`, `DeleteAsync(filter)` | Port | `storage.DeletePendingJobs<T>()`, `DeletePendingFunctionJobs(name)`, `DeletePendingJobs(filter)`, `GetPendingJobs()`. |
| Add, reschedule, run now, rerun, batch add | Done | `Schedule`, `Reschedule`, `Requeue`, `Trigger`, `EnqueueMany`. |

## Phase 6: Reliability and cluster (done)

The restart limit is opt-in (`UseStaleRestartLimit`), not 3 by default: a graceful shutdown puts running jobs back too, so a default limit would fail long jobs during rolling deployments. Fencing uses a `Lease` parameter written only when a job is picked up again, so normal processing costs nothing. Deployment fencing pauses old servers through drain mode; manual epochs aren't ported (deploy with a new id instead).

| TickerQ | Status | NightShift |
|---|---|---|
| `OnStale` / `MaxStaleRestarts` | Port | `UseStaleRestartLimit(n)` (`StaleJobFilter`): a job picked up again more than n times after its server stopped fails for good. |
| Lease fencing | Enhance | The worker that takes a job over holds its `Lease`; a former owner's late state change is refused. |
| Reconciliation epoch | Port | Opt-in `UseDeploymentFencing(id)`: servers of an older deployment pause themselves. |
| Manual start, start/stop/restart | Port | `StartMode = ServerStartMode.Manual` and `IBackgroundJobServerControl`. |
| `LongRunning` priority | Port | `[LongRunning]` and a separate worker pool (`LongRunningWorkerCount`). |
| Per-status retention | Enhance | `UseJobRetention(o => ...)` per state; `[ExpireAfter]` wins. |
| Payload compression | Port | Opt-in `UseArgumentCompression(minLength)`; reading always decompresses. |
| Application namespace, node id, queue-only mode, shutdown drain | Done | Storage schema/prefix, `ServerName`, no server, `StopTimeout`. |

## Phase 7: Observability and alerts (done)

Connections, rules and templates are configured in code (`UseWebhookAlerts`, `AlertRule`, `WebhookAlertOptions.Template`), so secrets stay in the application's configuration; a dashboard page to manage them isn't ported. Delivery history is in memory per sink.

| TickerQ | Status | NightShift |
|---|---|---|
| Execution logs (captured `ILogger` lines) | Enhance | `AddNightShiftJobLogs(o => ...)`: level, category exclusions, redaction, size limits, lifecycle lines. |
| Telemetry lifecycle counters | Enhance | `nightshift.job.completed` (by outcome), `retried`, `stale_restarts`, the `nightshift.job.lateness` histogram, skip and cancel span events. |
| Monitors: daily deadline, long-running, repeated failures, recovery | Port | `DailyDeadlineMonitor`, `LongRunningMonitor`, `RepeatedFailuresMonitor` with `UseJobMonitor`; `MonitorIncident` and `MonitorRecovered` alerts; `JobMonitors.GetStatus`. |
| Alert rules with function filters, templates, delivery history | Port | `AlertRule` (kinds, method wildcards, cooldown), `WebhookAlertOptions.Template`, `RetryDelays`, `Deliveries`, `Alert.Id`. |
| Slack, Teams, webhooks | Done | `WebhookAlertSink`. |

## Phase 8: Dashboard (done)

Dashboard commands added in this phase answer `204`, or `409`/`422` with `{ "error": ... }`, so the page shows why a command was refused (a chain that hasn't settled, a policy someone else saved).

| TickerQ | Status | NightShift |
|---|---|---|
| Cron occurrence history | Port | `/recurring/details/{id}`: definition, next runs, the last 20 runs (the storage keeps 50), **From code** and **Retired** badges, trigger, pause and resume. |
| Chain view | Port | `/chains` and `/chains/{id}`: a flowchart like TickerQ's, top to bottom, parallel branches in a group box with its status and counts, joins in an outcomes box, a card per step (state, arguments, start condition and whether it was met, progress, timing, retries, **Retry from here** on a failed or deleted step of a settled chain), links colored by the path taken, zoom and full screen. Chain logs merge every step's console. Waiting, Running, Done and Finished with failures as the aggregate status. Not ported: the "Investigate with AI" button. |
| Schedule policies page | Port | `/policies`: cards, an editor with optimistic revisions, a preview of the next 20 occurrences marked allowed or blocked. |
| About page | Port | `/about`: effective configuration, global filters, job functions, monitors. |
| Visual chain builder, drafts and chain templates | Not ported | A drag-and-drop editor is a large client-side application, and chains are code: a chain built in the browser would need its steps' methods and arguments typed in by hand, which the Catalog's run form already does for one job. Saved chains are `RecurringJob.AddOrUpdateChain` or a method that builds the chain. |
| Test run (no job created) | Not ported | It runs job code inside the web process that hosts the dashboard, outside the server's workers, filters and limits, with the dashboard user's rights. **Run...** in the Catalog creates a real job instead, which is visible, audited and runs where jobs run. |
| Read-only users, host auth, diagnostics, search, live view | Done | `IsReadOnlyFunc`, authorization filters, `/health`, command palette, live polling. |

## Phase 9: Storage packages (done)

The four storages share an internal layer compiled into each package (`src/Shared/Storage`): a transaction that records its writes and applies them all at once, the monitoring API built on a few queries, lease locks and fetched jobs kept alive by a timer. Each package depends only on Core's public API, as a third-party storage would. Staging reserves the job's id with a committed insert that expires by itself, so client filters see the real id; the state, queue entry and every other write of the creation become tracked changes of the application's context.

| TickerQ | Status | NightShift |
|---|---|---|
| Conformance suite | Port | `tests/Shared/StorageConformance/StorageConformanceFacts.cs`: 46 tests (primitives, queues, locks, monitoring, purge, end to end with a server), compiled into each storage's test project. SQL Server passes it except two features it doesn't have (parameters in transactions, timelines before aggregation). |
| In-memory store | Port | `Masterly.NightShift.InMemory`: one lock, copies on read, wake-ups on enqueue, `MaxExpirationTime`. |
| EF Core store and transactional scheduling (`StageAsync`) | Port | `Masterly.NightShift.EntityFrameworkCore`: a context of its own or the application's (`ApplyNightShiftModel`); `context.NightShift()` stages jobs that commit or roll back with `SaveChanges`. EF Core 8 on `net8.0`, 10 on `net10.0`. |
| Redis store | Port | `Masterly.NightShift.Redis`: one Lua script per transaction, pub/sub wake-ups, keys in one hash slot. |
| MongoDB store | Port | `Masterly.NightShift.MongoDB`: replica set transactions, TTL indexes, merged counters. |

## Not ported

| TickerQ | Why |
|---|---|
| AI assistant | Needs a language-model provider: a new dependency and a way for job data to leave the system. It would be a separate package, not a port. |
| TickerQ Hub, remote JS SDK | A hosted service and SDKs in other languages, not a library feature. The shared storage is NightShift's way to share jobs between applications. |
| Licensing | NightShift is MIT/LGPL; a license gate would only add a way for jobs to stop silently. |
| SignalR push | The dashboard in Core is host-agnostic, and live polling already refreshes pages. TickerQ's SignalR messages are only refresh hints too. A push channel in the ASP.NET Core package stays an option if polling load becomes a problem. |
| Native AOT | NightShift jobs are serialized method calls, resolved by type name and read through reflection-based JSON. AOT would need source-generated JSON contexts for every argument and state type, and it wouldn't speed up job throughput, which the database decides. |
| Request contract validation | Expression jobs are checked by the compiler. Named functions (Phase 5) bring a JSON payload back, and that phase checks it at creation. |
