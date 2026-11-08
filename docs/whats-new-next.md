# What's new: Masterly.NightShift Next

The features below come on top of Hangfire 1.8.25 in Masterly.NightShift 1.0. Each one works with every storage, because
they only use the hash, set, list, counter and job-parameter primitives every storage already implements. The
design and the storage layout of each feature are in `architecture/blueprints/nightshift-next.md`.

## Reflection-free job invocation

Masterly.NightShift no longer calls job methods through `MethodInfo.Invoke`. Every method is performed by a `JobInvoker`,
resolved once and cached:

| Tier | When | How |
|---|---|---|
| Generated | The job's assembly references `Masterly.NightShift.Core` (the source generator ships inside the package) | A direct, typed call emitted at compile time |
| Compiled | No generated invoker, dynamic code supported | A delegate compiled from an expression tree, once per method |
| Reflection | `UseCompiledJobInvokers(false)`, or dynamic code unsupported | `MethodInfo.Invoke` |

Nothing to configure. The generator finds the methods used in `Enqueue`, `Schedule`, `ContinueJobWith`,
`AddOrUpdate` and `Job.FromExpression` lambdas. For job classes that other assemblies enqueue, mark the class:

```csharp
[GenerateJobInvokers]
public class ReportJobs { public Task Build(int reportId) { ... } }
```

The Insights page shows how many methods each server performs with each tier. To skip generation for one project,
set the MSBuild property `NightShiftDisableJobInvokerGeneration` to `true`.

## Interceptors (AOP)

Interceptors wrap the method call itself. They see the activated instance, can replace arguments, call the method
several times or not at all, and run inside the performer, on the job's synchronization context.

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
        invocation.Context.Console.WriteLine($"took {stopwatch.ElapsedMilliseconds} ms");
        return result;
    }
}
```

Apply them as attributes on the class or method, or globally with `GlobalConfiguration.Configuration.UseJobInterceptor(...)`.
Built-in: `[JobTimeout(seconds)]`, `[RetryInProcess(attempts)]`, `[LogInvocation]`.

## Concurrency control

```csharp
[Throttle("tenant-{0}", MaxConcurrency = 2)]                 // distributed semaphore, {0} = first argument
[RateLimit("sms", limit: 100, perSeconds: 60)]                // sliding window
[CircuitBreaker("payments", failureThreshold: 5, windowSeconds: 60, openSeconds: 120)]
public Task Send(string tenant, Message message) { ... }
```

When `[Throttle]` or `[RateLimit]` won't let a job run, their `Action` decides: `Reschedule` (default: back to the
schedule, the worker moves on), `Wait` (hold the worker, fail after `WaitTimeoutSeconds`) or `Skip` (delete the job).
Several `[RateLimit]` attributes may be combined, for example 10 per second and 1000 per hour.
`[CircuitBreaker]` has no `Action`: while its circuit is open, jobs are always rescheduled. Open circuits are listed on
the Insights page and can be reset there.

## Recurring jobs from the dashboard

Pause, resume and edit recurring jobs without redeploying. The editor accepts CRON or plain English:

```csharp
RecurringJob.AddOrUpdate("digest", () => SendDigest(), Cron.FromText("weekdays at 7:30"));
RecurringJob.Pause("digest");
RecurringJob.UpdateSchedule("digest", Cron.FromText("every 2 hours between 8 and 18 on weekdays"));
```

A paused job keeps its definition, stays paused across redeploys, can still be triggered by hand, and resumes
from now without catching up. Managers from other packages that don't implement `IRecurringJobManagerV3` throw
`NotSupportedException` from these calls.

Recurring jobs can have a start and an end date: `RecurringJobOptions.StartAt` and `EndAt` (`DateTimeOffset?`). No run
is created for occurrences outside the window, misfire handling respects it, a job past its end stays registered
without a next run, and manual triggers still work. The window is in the recurring job manifest (`startAt`, `endAt`)
and in `RecurringJobDto`. Servers of earlier versions ignore it, so it is enforced once every server is upgraded. See
[Time zones and misfires](recurring-jobs/time-zones-and-misfires.md).

## Live monitoring

```csharp
using Masterly.NightShift.Server;

public async Task Import(string file, IJobConsole console)   // injected, pass null when enqueueing
{
    console.Checkpoint("download");
    await Download(file);
    for (var i = 0; i < rows.Count; i++)
    {
        if (i % 100 == 0) console.SetProgress(100.0 * i / rows.Count, $"row {i}");
    }
    console.WriteLine(JobConsoleLevel.Warning, "3 rows skipped");
}
```

The job details page shows the progress bar, console and checkpoint timeline live, with the elapsed time, and
the Processing page shows every job's progress. `PerformContext.Console` gives filters and interceptors the same
console, and the console is safe to use from several threads. Console data expires with the job. Writes made after
the job finished, or after its cancellation token found it deleted or requeued, are dropped. See
[Progress, console and checkpoints](jobs/progress-and-console.md).

## Insights

`GlobalConfiguration.Configuration.UseJobInsights()` records runs, failures and the last 200 durations per method.
The Insights page shows failure rates, median/p95/max, a trend sparkline, the invoker tier, and flags a last run
that took more than three times the p95.

## Smaller additions

- `Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays()` builds CRON expressions fluently and validates them;
  calls can come in any order, and conflicting calls throw.
- `[Deadline("00:10:00")]` records the moment a job should be done by. The Processing page and the job details
  page flag overdue jobs, and a job that finishes late keeps a "finished N after its deadline" notice. Nothing is
  canceled; use `[JobTimeout]` for that.
- The Servers page shows how many methods this process performs with generated, compiled and reflection invokers.
- `storage.GetJobState(jobId)` reads a job's current state (name, reason, data, creation time) in one call, and
  `storage.TryGetResult<T>(jobId, out var result)` reads what a succeeded job returned.
- `storage.RequeueFailedJobs(filter)` requeues failed jobs in bulk, optionally filtered and bounded by a time budget,
  like "Requeue all" on the Failed page.
- `/health` and `/metrics` read queue lengths with one query per queue on SQL Server, instead of also loading each
  queue's first jobs (`JobStorageFeatures.Monitoring.QueueCounts`).

## Game changers

### Job Catalog: run any method from the dashboard

The source generator already knows every job method in the loaded assemblies, so the new **Catalog** page lists
them: signature, parameters (the ones the server injects are marked), the filters and interceptors on each, and
the Insights numbers. **Run…** opens a form built from the parameters and creates the job right away, after a
delay, or as a recurring job with a schedule written as CRON or plain English. No code, no redeploy. Types that
are not reachable through generated invokers can be added with `JobCatalog.Add<T>()`.

### Fan-in continuations

```csharp
var parts = new[] { Enqueue(... "eu"), Enqueue(... "us"), Enqueue(... "apac") };
BackgroundJob.ContinueJobWithAll(parts, () => Merge());        // waits for every parent
BackgroundJob.ContinueJobWithAny(parts, () => NotifyFirst());  // starts on the first one
```

A fan-in job waits in the `Awaiting` state (`AwaitingJobsState`) for a set of parents. `JobContinuationOptions`
decide which outcomes count: with the default, one deleted parent deletes the continuation with the reason
"Continuation condition was not met". `Failed` is not a finished state, so a failed parent blocks the continuation
until it is requeued (and finishes) or deleted. Parents that disappear from the storage count as deleted. The
continuation is decided right after a parent's state change is committed, so parents that finish at the same moment
still start it, and only once. Parents list their fan-in children on the job details page; the child's history
shows all parents. `null` parent ids (creations canceled by a filter) are skipped.

### Duplicate prevention

```csharp
[PreventDuplicates]                       // fingerprint = method + all arguments
public Task Reindex(string index) { ... }

[PreventDuplicates("reindex:{0}")]        // fingerprint = template over the arguments
public Task ReindexByName(string index, bool full) { ... }
```

A second copy is refused while the first is still pending (`Enqueue` returns `null`), or replaces it with
`OnDuplicate = DuplicateHandling.ReplaceExisting`. The check happens at creation time, under a distributed lock,
so duplicates never reach a queue. The fingerprint is released when the job reaches a final state or fails for good
(unless `ReleaseOnFailure = false`), and the owner's state is checked on every attempt, so a lost release can't
block forever. The job carries a `Fingerprint`
parameter.

### Drain mode: pause processing without stopping servers

The Servers page has a **Pause processing** switch (with an optional reason) and every page shows a banner while
it is on. Workers hand new jobs back to the Scheduled state for 30 seconds (`DrainMode.HoldInterval`) and look
again; running jobs finish. Turn it on before a deploy or a database maintenance window, from the dashboard or
with `DrainMode.Enable(connection, "deploy")`. The switch lives in the `hangfire:control` hash and is read
once every two seconds per process and storage.

### Live activity feed

The home page shows every state transition across the whole cluster as it happens: time, state, job, method,
server and reason, newest first. The feed is off by default: turn it on with `UseActivityFeed()` in every process
that changes job states. `ActivityFeedFilter` then queues each transition, and a background writer adds them to the
`activity` list (the last 1000) after each state change commits, so the feed never slows down or fails a state
change.

### Alerts and webhooks

```csharp
GlobalConfiguration.Configuration.UseWebhookAlerts("https://hooks.slack.com/...", o => o.Format = WebhookFormat.Slack);
```

An alert is raised when a job finally fails (its retries are exhausted), a `[Deadline]` is missed or a circuit
breaker opens. `WebhookAlertSink` posts them without blocking the job, in one of four formats (`WebhookFormat`):
`Json` (the alert as JSON, the default), `Slack` (a Slack or Mattermost `text` message), `Discord` (a Discord
`content` message) or `Teams` (a Microsoft Teams Adaptive Card), with optional headers and a bound on in-flight
requests. `UseAlerts(IAlertSink)`
plugs in anything else: e-mail, PagerDuty, a metric.

### Schedule forecast

**Recurring jobs → Forecast** draws every upcoming run of every recurring job for the next 24 hours or 7 days
on one timeline, each run sized by the median duration from Insights. Slots where several jobs start within the
same five minutes are highlighted and listed as hot spots, so collisions can be spread out before they happen.

### ETA for running jobs

Jobs that never report progress still get a bar on the Processing page: the remaining time is predicted from the
median of past runs of the same method, and a job running past its p95 is flagged "longer than usual".

### Copy as code

The job details page has **Copy as C#**: the exact `BackgroundJob.Enqueue<T>(x => x.Method(args))` statement
that re-creates the job, with arguments written as literals (or `FromJson<T>("...")` for complex ones).

### Insights: needs attention

The Insights page opens with the methods whose behaviour changed: a recent median at least twice the earlier
one, a failure rate of 20% or more, or an unusually slow last run.

## Game changers, third wave

### Diagnosis: why isn't it running?

The job details page answers the question every operator asks. For an enqueued job: is any live server
listening to its queue (and which queues are served), is processing paused, how long has it waited and behind
how many jobs. For a scheduled one: when it is due, whether that moment has passed without a server enqueuing
it, and which retry it is. For an awaiting one: what each parent is doing, whether a parent vanished or finished
in a state that fails the continuation's condition, how many fan-in parents are done. For a processing one:
whether its server is alive and whether the run is already longer than the method's p95. For a failed one: why
it won't be retried. Open circuits are reported in every state. Each finding is one sentence with a link to the
page that fixes it, and a severity, so the panel reads like a checklist instead of a log.

### Lineage

Continuations form graphs, and the details page now draws them: the jobs this one waited for (and theirs, up to
four levels), the continuations waiting for it, fan-in parents and children, each node coloured by state and
linked to its own page, each edge labelled with its condition ("after success", "all of", "any of"). It is built
from data the storage already has (`Awaiting` state data and the `Continuations`/`FanInContinuations` job
parameters), capped at 40 jobs.

### Tags

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;

[Tag("customer:{0}"), Tag("export")]
public Task Export(string customer, IJobConsole console) { ... }

JobTags.Add(jobId, "priority:high");   // after the fact
```

Jobs can finally be found by what they are about. A tag is a word or a template over the arguments, lower-cased.
The **Tags** page lists the tags in name order with their job counts, a page at a time, each tag lists its jobs newest first (expired ones are
hidden, and a button removes them from the tag), the details page shows a job's tags as chips, and the command palette
searches tags too (`tag:contoso`). Storage: set `tags` for the names, set `tags:{tag}` for the job ids scored by
creation time, job parameter `Tags`.

### Failure clusters

The Failed jobs page opens with the latest 500 failures grouped by exception type and method: "214 × SqlException
in Billing.Charge", first and last seen, the newest message, and two buttons per cluster: requeue all of them or
delete all of them. One deadlock no longer looks like 214 unrelated rows.

### Retry later

```csharp
if (response.StatusCode == HttpStatusCode.TooManyRequests)
{
    throw new RetryLaterException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1), "Rate limited by the partner API");
}
```

A job can say exactly when it wants to run again. `AutomaticRetryAttribute` honours `RetryLaterException` by
scheduling the job for the requested delay or moment instead of its back-off, without using up a retry attempt.
The reason shows in the job history ("Retry later (3 of 100): Rate limited…") and on the Retries page. A job
that keeps asking is still bounded: after `MaxRetryLaterRequests` (100) the exception counts as an ordinary
failure; the `RetryLaterCount` parameter keeps the tally. Insights doesn't count such a run as a success or a
failure, and telemetry reports it with the outcome `retry_later`, not as an error.

### Redacted arguments

```csharp
public Task Connect(string host, [Redacted] string password) { ... }
```

A parameter marked `[Redacted]` is shown as `"••••••"` everywhere the dashboard renders arguments, "Copy as C#"
leaves it out, and "Run again with changes" asks for it anew. The value is still stored with the job; redaction
protects dashboard viewers, not the storage.

### Recurring job manifests

```csharp
var json = RecurringJobManifest.Export();            // every recurring job as JSON
RecurringJobManifest.Apply(json, prune: true);        // register them, remove the ones not listed
```

Schedules can live in source control and be promoted between environments like any other configuration. The
Recurring Jobs page has **Export** (downloads the manifest) and **Import…** (paste or pick a file, optionally
prune, see what was added, updated, removed or failed). Jobs are written in the storage's portable format, so a
manifest from staging applies to production running the same code. Applying is idempotent: an unchanged entry
keeps its next run. An entry changes a job's paused state only when it has a `paused` field. Entries that fail (unknown type, a method the application doesn't already use as a job, bad CRON, unknown time zone,
unknown misfire handling) are reported and don't stop the others; `allowAnyMethod: true` (or
`DashboardOptions.AllowAnyMethodInManifestImport`) accepts any method.

### Health endpoint

`GET /health` answers for load balancers, Kubernetes probes and uptime monitors: `200` with `status: healthy` or
`degraded`, `503` with `status: unhealthy` when the storage is unreachable or no server is alive. The checks:
storage latency, live and stale servers, queues with jobs that no live server listens to, paused processing,
recurring jobs that are late or disabled by errors, plus the counts the home page shows. `/ping` remains the
storage-free liveness URL. Both are subject to the dashboard's authorization filters. The report is reused for five
seconds, and storage errors are logged instead of shown. `app.MapNightShiftHealth(...)` and
`app.MapNightShiftMetrics(...)` serve the same data outside the dashboard, and `AddHealthChecks().AddNightShift()`
plugs into ASP.NET Core health checks, per process or for the whole cluster (see `docs/monitoring/health-checks.md`).

### Late recurring jobs

A recurring job whose next run was due more than a minute ago and that nobody started gets a **Late** badge on
the Recurring Jobs page, a summary at the top of the page says how many are late and what to check, and the
health endpoint reports them as `degraded`. The usual cause, a server without the recurring job scheduler or
without access to the storage, used to be invisible until someone noticed a missing report.

### Run again with changes

The job details page has **Run again with changes…**: the same form the Catalog uses, pre-filled with the job's
own arguments. Fix the one argument that was wrong and run it now, later, or turn the job into a recurring one.
It works for any job, catalogued or not, and redacted arguments come back empty.

## Game changers, fourth wave

### Batches

```csharp
var batchId = BatchJob.StartNew(batch =>
{
    foreach (var region in regions) batch.Enqueue(() => Export(region));
}, "Regional exports");

BatchJob.ContinueWith(batchId, () => Merge());   // runs once every export has finished
```

Jobs created inside `StartNew` belong to the batch; the new **Batches** page shows every batch with a
progress bar (succeeded, failed, running, pending), the batch page lists its jobs and continuations and has
"requeue failed" and "delete unfinished" for the whole group. `ContinueWith` is a fan-in continuation over the
batch's jobs, so `JobContinuationOptions` decide whether a deleted job cancels it; a failed job blocks it until the
job is requeued or deleted ("requeue failed" on the batch page). Counters live in the storage (`batch:{id}:*`) and
are kept by `BatchSupportAttribute`, a new default global filter. On storages with the extended API, such as SQL
Server, a batch's bookkeeping expires `BatchJob.ExpireIn` (seven days) after creation; the Batches page never writes,
and `StartNew` removes expired batches from the `batches` index.

From code, `BatchJob.RequeueFailed(batchId)` and `BatchJob.Cancel(batchId)` do what the batch page's buttons do, and
return a `BulkJobResult` (processed, remaining, complete).

### Per-queue pause

The Queues page has **Pause** and **Resume** per queue. Workers hand jobs of a paused queue back for 30 seconds
and move on to the other queues, so one misbehaving integration can be stopped without stopping everything.
A banner lists the paused queues, the health endpoint reports them, and the job diagnosis explains a held job.
`DrainMode.PauseQueue(connection, "emails", "smtp outage")` does the same from code.

### Prometheus metrics

`GET /metrics` exposes the dashboard's numbers in the Prometheus text format: jobs by state, queue lengths,
servers, recurring jobs and late ones, the paused flag, the health status, per-method run counts and duration
percentiles, per-queue wait percentiles and throughput. Point a scraper at it and the Grafana board and alert
rules follow.

### Audit log

Every action taken through the dashboard is recorded: who (the authenticated user, from `HttpContext.User`),
from where, what (requeue, delete, pause, import, a job run from the Catalog, a recurring job edited) and on
which jobs, with the outcome. The **Audit** page shows the last thousand. `AuditLog.Enabled = false` turns it
off; hosts outside ASP.NET Core override `DashboardContext.GetUserName()` to supply the user.

### Resumable jobs

```csharp
using Masterly.NightShift.Server;

public void Import(string file, IJobConsole console)
{
    console.Resume("download", () => Download(file));
    console.Resume("parse", () => Parse(file));
    console.Resume("save", () => Save());
}
```

Checkpoints now survive retries: `IJobConsole.Checkpoints` lists what earlier attempts reached and
`Resume(name, step)` skips a step whose checkpoint is already recorded, then records the checkpoint when the
step finishes. A job that crashed in step three restarts at step three. Checkpoints are stored apart from the
console lines, so a long console doesn't push them out. Retrying or requeuing a failed job keeps its checkpoints;
requeuing a succeeded or deleted job starts again from the first step.

### Retention per method

`[ExpireAfter(days: 30)]` keeps a method's finished jobs for a month, `[ExpireAfter("00:10:00")]` drops chatty
ones after ten minutes, instead of the storage-wide default.

Failed jobs can expire too: `[ExpireAfter(days: 7, IncludeFailed = true)]` per method, or
`UseFailedJobRetention(TimeSpan.FromDays(30))` for every job. Only a final failure (retries used up) expires;
requeuing the job before then keeps it. See [Timing](filters/timing.md#failed-jobs).

### Run windows and blackouts

`[RunWindow("22:00-06:00", "Europe/Berlin")]` lets a job start only at night, `[RunWindow("09:00-17:00",
WeekdaysOnly = true)]` only in office hours, `[Blackout("02:00-04:00")]` never during the maintenance window.
A job picked up at the wrong time is rescheduled for the window's next start, with the reason in its history.

### Queue health

With `UseJobInsights()` the Queues page also shows, per queue, the median and p95 wait before a worker took the
job, jobs taken per minute, and **clears in**: how long the backlog needs at the current rate. A queue with
waiting jobs that nobody touched for five minutes is flagged stalled. One extra state read per job start.

### Schedule in plain English

```csharp
BackgroundJob.Schedule(() => SendReport(), "tomorrow at 9am");
BackgroundJob.Schedule(() => Cleanup(), "in 2 hours");
BackgroundJob.Schedule(() => Close(), "end of month");
```

`TimeText.Parse` understands relative phrases, times of day, weekdays, "next week", "end of day/month" and
ISO dates, in `TimeText.DefaultTimeZone` (the machine's local zone by default). The Catalog's "later" field
takes the same phrases.

## Dashboard, fifth wave

### Wallboard

`/wallboard` is the dashboard for a wall display: no navigation, large numbers, a status pill that is green,
amber or red, the queues with their drain times, the jobs running right now with their predicted end, and an
"Attention" list of everything the health checks found. It refreshes itself every ten seconds and is always
dark. Open it from the sidebar or with the palette command `> wallboard`.

### Timeline

`/timeline` draws what every worker in the cluster did in the last fifteen minutes: one lane per worker,
grouped by server, one bar per run, coloured by outcome (green succeeded, red failed, amber held or retried,
striped still running). It is rebuilt from the activity feed, which now records the worker id, so it costs
nothing extra.

### Activity heatmap

The Insights page opens with the last seven days hour by hour, a grid of 7 × 24 cells shaded by how many jobs
finished and red where failures outnumbered successes. The hourly counters that already existed are now kept
for eight days instead of one.

### Notes and acknowledgements

Every job page has a Notes section: write what you found out for the next person, or press **Acknowledge** on
a failed job. Acknowledged jobs carry a badge on the Failed jobs page, so the team sees what is already being
looked at. Notes live in the `Notes` job parameter, with the author from the authenticated user.

### Watchlist and notifications

**Watch** on a job page adds it to a sidebar panel that follows its state on every page. When a watched job
finishes, a toast appears and, if the browser allows it, a desktop notification. The list is per browser.

### Keyboard triage

On every job list, `j`/`k` move, `x` selects, `Enter` opens, `r` requeues and `d` deletes the selection. `?`
shows the overlay with every shortcut, including the existing `g` + letter page jumps and the palette.

### Palette commands

Type `>` in the command palette for actions: `> pause processing`, `> resume processing`, `> pause queue emails`,
`> resume server web-1`, `> requeue #4711`, `> delete #4711`, `> wallboard`, `> timeline`.

### Per-server pause

The Servers page has **Pause** per server: that server stops starting new jobs while its running jobs finish.
Each job it fetches is put back as Scheduled for the hold interval and enqueued again later, when any server can
take it. A soft drain of one machine before a deploy or while debugging it. From code, pass the full server ID as
the Servers page shows it (`name:pid:guid`), not just the machine name:
`DrainMode.PauseServer(connection, serverId, "deploying")`. The ID changes on every restart. A pause left by a stopped
server has no effect; the Servers page lists it with a **Clear** button, and from code
`DrainMode.ResumeServer(connection, oldServerId)` clears one and `DrainMode.ClearStaleServerPauses(storage)` clears
all of them.

### Exports

Every job list has **Download CSV** (up to 10,000 rows, with the exception type and message for failures, and
spreadsheet formula characters neutralised). The job page has **Export JSON**: method, arguments (redacted ones
masked), full state history, parameters, console and notes, ready to attach to a ticket.

### Time in each state

Above the state history, a bar shows how the job's life was spent: how long it waited, how long it ran, how
long it sat in retries, proportionally and in state colours.

## Keeping the host alive

IIS stops an idle worker process after 20 minutes, and the Masterly.NightShift server inside it stops too. The process
cannot restart itself, so the pool settings (`startMode="AlwaysRunning"`, preload, idle timeout 0) or a
Windows Service / worker host remain the real fix. What Masterly.NightShift can do from the inside:

```csharp
services.AddNightShiftKeepAlive();                                    // requests its own /nightshift/ping every 5 minutes
services.AddNightShiftKeepAlive(o => o.Url = "https://jobs.example.com/nightshift/ping");   // behind IIS: the public URL
new BackgroundJobServer(options, storage, new[] { new KeepAliveProcess("https://...") });  // without DI
```

The request counts as activity, so the idle timeout never fires; any answer counts, even an error page.
`/nightshift/ping` is a new route that answers `pong` without touching the storage, also usable for liveness checks. The Servers page shows the process name,
whether it runs under IIS, its uptime and the keep-alive status, and warns when an IIS-hosted process has no
keep-alive configured.

## Dashboard

A redesigned shell and design system, in light and dark:

- Left sidebar navigation with icons and live counts for every job state, so the duplicated per-page job-state
  sidebar is gone and every page uses the full width. Collapses behind a menu button on narrow screens.
- Top bar with the page title, a connection pulse that turns red when polling fails, and the search button.
- Theme switcher (system, light, dark) in the sidebar footer, remembered per browser; `?theme=dark` in the URL
  also selects a theme. Both themes come from one set of design tokens (`nightshift-ui.css`); the legacy
  `nightshift-dark.css` is now loaded through a `media` attribute that the switcher controls.
- Overview page with KPI tiles and chart cards; tables, buttons, pills, alerts, forms and modals restyled.
- Command palette (`Ctrl+K`, `/`, or the search button): pages, a job by id, recurring jobs by name.
  `g` then `j`, `p`, `f`, `r`, `s`, `i` or `h` jumps to a page.
- Every job list has **Refresh** (reloads the table in place) and **Live** (keeps reloading) buttons, and a
  **Delete all** button that empties the whole list, not just the selected page. The recurring jobs page has
  **Remove all**. A refresh button in the top bar reloads any page.
- **Maintenance** on the Servers page: **Clear all jobs** removes every job with its history, queues, counters,
  consoles and statistics but keeps recurring job definitions, servers, the audit log and drain mode; **Reset
  storage** removes everything but the audit log. Both call the new `JobStorage.Purge(StoragePurgeOptions)`
  (`KeepRecurringJobs`, `KeepServers`, `KeepAuditLog`, `KeepDrainMode`), implemented by `Masterly.NightShift.SqlServer`,
  which deletes in batches so large tables don't time out, and advertised
  through `JobStorageFeatures.Purge`; the buttons appear only when the storage supports it.
- Cron expressions are explained in plain English (`Cron.Describe`) under the schedule editor and as tooltips on
  the cron badges.
- The dashboard strings are translated in all 15 locales; a few strings added later are English only and fall back to
  English elsewhere.

`MenuItem.Icon` lets dashboard extensions give their navigation items a Glyphicon; items without one get a chevron.

## Operations and integration

- **ASP.NET Core health checks**: `builder.Services.AddHealthChecks().AddNightShift(o => o.Scope = NightShiftHealthCheckScope.Process)`
  reports whether this process's server is alive (for liveness probes) or whether the cluster can process jobs. Results
  never contain exception text. See `docs/monitoring/health-checks.md`.
- **Server options from configuration**: `builder.Services.AddNightShiftServer(builder.Configuration.GetSection("NightShift:Server"))`
  reads `ServerName`, `WorkerCount`, `Queues` and the intervals from `appsettings.json`. `SqlServerStorageOptions` bind
  with the standard `Bind` call.
- **Job id on every log entry**: servers added with `AddNightShiftServer` run each job inside an `ILogger.BeginScope`
  with `JobId`, `JobName` and, when the job has them, `RecurringJobId` and `RetryCount`, so the entries the job's own
  `ILogger<T>` writes (after an `await` too) carry them. `services.ConfigureNightShiftJobLogging(o => o.BeginScope = false)`
  turns it off. See `docs/monitoring/logging.md`.
- **Discord alerts**: `WebhookFormat.Discord` posts to Discord's own webhook URLs, without pinging anyone.
- **Requeue all failed**: the Failed page has a **Requeue all** button for the whole list, not just the selected rows.
- **Testing guide**: `docs/jobs/testing.md` shows how to unit-test code that creates jobs, the job classes themselves and
  filters, and how to run an integration test against LocalDB.
- **Web sample**: `samples/WebSample` maps the dashboard behind an authorization policy with antiforgery, the activity
  feed, insights and health checks. `dotnet run --project samples/WebSample` starts it in Development on
  `http://localhost:5000`; open `/nightshift` and the Development-only `/login` signs you in as an operator. It uses a
  LocalDB database unless `ConnectionStrings:NightShift` is set, and creates the database on first run if it doesn't
  exist (the samples do this with a small helper; the storage itself creates tables, not databases).
