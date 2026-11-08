# Roadmap

Work that is planned but not in 1.0. Nothing here is scheduled yet. Sizes are rough and relative: **S** is small, **M** medium, **L** large.

## E1: Planned after 1.0

| ID | Item | Area | Size | Status |
|---|---|---|---|---|
| [F-1.1](#f-11-async-job-creation-api) | Async job creation API | Client, storage | L | Not started |
| [F-1.2](#f-12-parent-results-in-fan-in-and-batch-continuations) | Parent results in fan-in and batch continuations | Continuations | M | Not started |
| [F-1.3](#f-13-opentelemetry-queue-metrics) | OpenTelemetry queue metrics | Monitoring | M | Not started |
| [F-1.4](#f-14-configuration-calls-that-add-duplicates) | Configuration calls that add duplicates | Configuration | S | Not started |
| [F-1.5](#f-15-cheaper-decisions-for-very-large-fan-ins) | Cheaper decisions for very large fan-ins | Continuations | M | Not started |
| [F-1.6](#f-16-continuations-that-react-to-a-final-failure) | Continuations that react to a final failure | Continuations | M | Not started |

### F-1.1: Async job creation API

**Today.** Creating a job is synchronous: `Create`, `Enqueue`, `Schedule`, `ContinueJobWith` and `RecurringJobManager.AddOrUpdate` block the calling thread while the storage writes. In an ASP.NET Core request handler, each call holds a thread-pool thread for the length of a database round trip.

**Why it isn't done yet.** An async API is only worth having if the storage writes are async too, and the storage contract is synchronous. Adding members to `IStorageConnection` or `IWriteOnlyTransaction` would break every storage that lives in another repository. The async members have to go on the `JobStorageConnection` and `JobStorageTransaction` base classes, behind a new `JobStorage.HasFeature` flag, with a synchronous fallback for storages that don't support them.

**What it involves.**
- Async versions of the client methods above, on `IBackgroundJobClient`, `IRecurringJobManager` and the static facades.
- Async members on the storage base classes, and a feature flag for them.
- An async implementation in `Masterly.NightShift.SqlServer`.
- A decision on client filters (`IClientFilter`), which are synchronous: either add async filter interfaces, or keep running the existing filters synchronously inside the async path.

**Until then.** Use the synchronous methods. At moderate volume the cost is one short database call per job.

### F-1.2: Parent results in fan-in and batch continuations

**Today.** `[FromResult]` only works in continuations created with `ContinueJobWith`, which have a single parent. When that parent succeeds, its result is copied into the continuation's `AntecedentResult` job parameter. Continuations created with `ContinueJobWithAll`, `ContinueJobWithAny` or a batch continuation get no result, so a `[FromResult]` parameter receives nothing.

**Open design questions.**
- What a job with many parents receives: a list of results, or results keyed by parent job ID.
- For `ContinueJobWithAny`, whether the result comes from the parent that finished first.
- Size limits. Results are stored in job parameters, so a continuation with hundreds of parents could store a lot of data.
- The serialized form must respect the configured [compatibility level](configuration/compatibility-levels.md).
- Results must be read after the deciding parent's state change commits, which is when fan-in continuations are already decided.

**Until then.** Have each parent store its result yourself, for example in a table keyed by job ID, and pass the parent job IDs to the continuation as arguments.

### F-1.3: OpenTelemetry queue metrics

**Today.** The `Masterly.NightShift` meter that [`UseTelemetry()`](monitoring/opentelemetry.md) turns on reports what the current process does: `nightshift.job.created`, `nightshift.job.duration` and `nightshift.job.active`. Queue lengths and queue wait times exist only on the dashboard's Prometheus [`/metrics` endpoint](monitoring/insights.md#prometheus-endpoint) and on the Insights page. An app that exports only through OpenTelemetry (OTLP) can't see a growing backlog without also scraping `/metrics`.

**What it involves.**
- An observable gauge for the length of each queue, read from storage when a collector asks. Reads are cached for a few seconds, as the health report is, so several collectors don't multiply the queries.
- A histogram of queue wait time, from Enqueued to Processing, recorded when a worker starts a job. The telemetry filter already reads the Enqueued state at that point when someone listens.
- Both follow the existing telemetry rules: no work when nothing listens, and .NET 8 or later only.
- Queue names become metric tags, so their number must stay bounded.

**Until then.** Scrape the dashboard's `/metrics` endpoint, or call `MapNightShiftMetrics` (see [Health checks and probes](monitoring/health-checks.md)).

### F-1.4: Configuration calls that add duplicates

**Today.** Two configuration methods add something new on every call:
- `UseWebhookAlerts(url)` adds another webhook sink each time. Called twice with the same URL, for example from two places in an app's startup code, it sends every alert twice. Calling it with different URLs is intended and must keep working.
- `UseDefaultCulture(...)` adds another culture filter each time, as Hangfire's did. Every job then runs all of them, and which culture wins depends on filter order.

`UseActivityFeed`, `UseAlerts`, `UseJobInsights`, `UseTelemetry` and `UseResultsInContinuations` already add their filters only once.

**What it involves.**
- Ignore a second `UseWebhookAlerts` call with the same URL and format.
- Make `UseDefaultCulture` replace the culture filter it added before.
- Record both behaviour changes in [Breaking changes](breaking-changes.md).

**Until then.** Call each method once.

### F-1.5: Cheaper decisions for very large fan-ins

**Today.** When a parent of a `ContinueJobWithAll`, `ContinueJobWithAny` or batch continuation finishes, it reads the continuation's whole state data, which lists every parent's ID, and parses it. Each parent's read grows with the number of parents, so the total work grows with the square of it. A batch of 10,000 jobs with a continuation moves roughly 1–2 GB from the storage to the workers over the life of the batch.

**What it involves.**
- Stop sending the whole parent list through every parent's decision. For `ContinueJobWithAll`, for example, keep a count of finished parents per continuation.
- The count must be updated only from committed states, once per parent even when a state change is retried, and must stay correct when a parent is requeued or deleted.
- Data written by earlier versions must still be decided correctly.

**Until then.** Split very large batches into several smaller ones, each with its own continuation. When one step must run after all of them, start it with `ContinueJobWithAll` over those continuations. See [Fan-in and batches](jobs/fan-in-and-batches.md).

### F-1.6: Continuations that react to a final failure

**Today.** A continuation runs when its parent succeeds (`OnlyOnSucceededState`) or reaches any final state (`OnAnyFinishedState`). Failed isn't a final state, so nothing runs when a job has used up its retries and stays Failed, unless it is set to be deleted then (`AttemptsExceededAction.Delete`). In the same way, a batch with one failed job never finishes: its continuation waits, and `JobBatch.IsStuck` is the only signal.

**What it involves.**
- A continuation option that runs only when the parent ends Failed after its last attempt, for compensation or notification steps ("tell the customer the import failed").
- A batch continuation that runs once every job has either finished or failed for good, next to the current one that waits for success.
- A rolling upgrade must stay safe. The continuation options are stored with each awaiting job, so a new value would break servers that don't know it yet. The new behaviour has to be stored in a way older servers ignore, or be turned on by a compatibility level.

**Until then.** Set `AttemptsExceededAction.Delete` on the parent and continue with `OnAnyFinishedState`, or check `JobBatch.IsStuck` from a recurring job.
