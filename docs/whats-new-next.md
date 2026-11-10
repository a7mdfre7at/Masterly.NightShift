# What's new in Masterly.NightShift

Masterly.NightShift 1.0 is Hangfire 1.8.25, renamed, plus the features on this page. Use it to see what you get on top of Hangfire, with a short example of each feature and a link to its full page. Coming from Hangfire, read [Migrating from Hangfire](migrating-from-hangfire.md) first; for what behaves differently, see [Breaking changes](breaking-changes.md).

Most features are opt-in: an attribute on a job method, a configuration call, or a button on the dashboard. All of them work with any storage, because they store their data in the hash, set, list, counter and job-parameter primitives every storage already implements. A few need the storage's extended API, which SQL Server has; their pages say so. The design and storage layout of each feature are in the [architecture blueprint](architecture/blueprints/nightshift-next.md).

| Area | Features |
|---|---|
| [Running jobs](#running-jobs) | Reflection-free invocation, interceptors, retry later, progress and console, resumable jobs, redacted arguments |
| [Creating and finding jobs](#creating-and-finding-jobs) | Job classes, fan-in continuations, batches, duplicate prevention, tags and notes, plain-English times, job state from code |
| [Controlling when jobs run](#controlling-when-jobs-run) | Throttle, rate limit, circuit breaker, timeouts, deadlines, run windows, retention, drain mode |
| [Recurring jobs](#recurring-jobs) | Pause, resume and edit, start and end dates, CRON builder and plain English, exact intervals, manifests, jobs declared in code |
| [Monitoring](#monitoring) | Insights, queue health, Prometheus, health checks, OpenTelemetry, alerts, activity feed, audit log, logging scope |
| [Dashboard](#dashboard) | New shell and pages, Basic authentication, job catalog, job diagnosis, lineage, failure clusters, wallboard, timeline, exports, maintenance |
| [Hosting](#hosting) | Keep-alive, server options from configuration |
| [For developers](#for-developers) | Testing guide, web sample |

## Running jobs

### Reflection-free job invocation

Job methods are no longer called through `MethodInfo.Invoke`. Every method is performed by a `JobInvoker`, resolved once and cached:

| Tier | When | How |
|---|---|---|
| Generated | The job's assembly references `Masterly.NightShift.Core` (the source generator ships inside the package) | A direct, typed call emitted at compile time |
| Compiled | No generated invoker, dynamic code supported | A delegate compiled from an expression tree, once per method |
| Reflection | `UseCompiledJobInvokers(false)`, or dynamic code unsupported | `MethodInfo.Invoke` |

There is nothing to configure. The generator finds the methods used in `Enqueue`, `Schedule`, `ContinueJobWith`, `AddOrUpdate` and `Job.FromExpression` lambdas. For job classes that other assemblies enqueue, mark the class:

```csharp
[GenerateJobInvokers]
public class ReportJobs
{
    public Task Build(int reportId) { ... }
}
```

The Servers and Insights pages show how many methods are performed with each tier. To skip generation for one project, set the MSBuild property `NightShiftDisableJobInvokerGeneration` to `true`. See [Source-generated invokers](jobs/source-generated-invokers.md).

### Interceptors

Interceptors wrap the method call itself. They see the activated instance, can replace arguments, call the method several times or not at all, and run inside the performer, on the job's synchronization context.

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

Apply them as attributes on the class or method, or globally with `GlobalConfiguration.Configuration.UseJobInterceptor(...)`. Built-in interceptors: `[JobTimeout(seconds)]`, `[RetryInProcess(attempts)]` and `[LogInvocation]`. See [Interceptors](filters/custom-filters.md#interceptors).

### Retry later

```csharp
if (response.StatusCode == HttpStatusCode.TooManyRequests)
{
    throw new RetryLaterException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1), "Rate limited by the partner API");
}
```

A job can say when it wants to run again. `AutomaticRetryAttribute` schedules it for the requested delay or moment instead of its back-off, without using up a retry attempt. The reason shows in the job history ("Retry later (3 of 100): Rate limited…") and on the Retries page. After `MaxRetryLaterRequests` (100) the exception counts as an ordinary failure; the `RetryLaterCount` job parameter keeps the tally. Insights doesn't count such a run as a success or a failure, and telemetry reports it with the outcome `retry_later`. See [RetryLaterException](filters/retries.md#retrylaterexception).

### Progress, console and checkpoints

```csharp
using Masterly.NightShift.Server;

public async Task Import(string file, IJobConsole console)   // injected; pass null when enqueueing
{
    var rows = await Download(file);
    for (var i = 0; i < rows.Count; i++)
    {
        if (i % 100 == 0) console.SetProgress(100.0 * i / rows.Count, $"row {i}");
        await Save(rows[i]);
    }

    console.WriteLine(JobConsoleLevel.Warning, "3 rows skipped");
}
```

The job details page shows the progress bar, the console and the checkpoint timeline live, with the elapsed time, and the Processing page shows every job's progress. `PerformContext.Console` gives filters and interceptors the same console, and the console is safe to use from several threads. Console data expires with the job. See [Progress, console and checkpoints](jobs/progress-and-console.md).

### Resumable jobs

```csharp
public void Import(string file, IJobConsole console)
{
    console.Resume("download", () => Download(file));
    console.Resume("parse", () => Parse(file));
    console.Resume("save", () => Save());
}
```

Checkpoints survive retries: `Resume(name, step)` skips a step whose checkpoint an earlier attempt recorded, and records the checkpoint when the step finishes. A job that crashed in step three restarts at step three. Retrying or requeuing a failed job keeps its checkpoints; requeuing a succeeded or deleted job starts again from the first step. See [Checkpoints and resumable jobs](jobs/progress-and-console.md#checkpoints-and-resumable-jobs).

### Redacted arguments

```csharp
public Task Connect(string host, [Redacted] string password) { ... }
```

A parameter marked `[Redacted]` is shown as `"••••••"` wherever the dashboard renders arguments, **Copy as C#** leaves it out, and **Run again with changes** asks for it anew. The value is still stored with the job: redaction protects dashboard viewers, not the storage. See [RedactedAttribute](filters/behavior.md#redactedattribute).

## Creating and finding jobs

### Job classes

```csharp
public sealed class NightlyCleanup : IJob
{
    public Task RunAsync(PerformContext context, CancellationToken cancellationToken) { ... }
}

public sealed class SendInvoiceHandler : IJobHandler<SendInvoice>
{
    public Task HandleAsync(SendInvoice args, PerformContext context, CancellationToken cancellationToken) { ... }
}

builder.Services.AddNightShiftJobHandlers(typeof(Program).Assembly);

jobs.Enqueue<NightlyCleanup>();
jobs.Enqueue(new SendInvoice(42));
```

The caller only names the class or passes the arguments. The stored job is an ordinary call of the class, so attributes on it apply and the dashboard shows its name. See [Job classes](jobs/typed-handlers.md).

### Fan-in continuations

```csharp
var parts = new[]
{
    BackgroundJob.Enqueue(() => Export("eu")),
    BackgroundJob.Enqueue(() => Export("us")),
    BackgroundJob.Enqueue(() => Export("apac")),
};
BackgroundJob.ContinueJobWithAll(parts, () => Merge());        // waits for every parent
BackgroundJob.ContinueJobWithAny(parts, () => NotifyFirst());  // starts on the first one
```

A fan-in job waits in the `Awaiting` state for a set of parents. `JobContinuationOptions` decide which outcomes count: with the default, one deleted parent deletes the continuation. `Failed` is not a finished state, so a failed parent blocks the continuation until it is requeued (and finishes) or deleted. The continuation is decided right after a parent's state change commits, so parents that finish at the same moment still start it, and only once. `null` parent ids (creations canceled by a filter) are skipped. See [Fan-in and batches](jobs/fan-in-and-batches.md).

### Batches

```csharp
var batchId = BatchJob.StartNew(batch =>
{
    foreach (var region in regions)
    {
        batch.Enqueue(() => Export(region));
    }
}, "Regional exports");

BatchJob.ContinueWith(batchId, () => Merge());   // runs once every export has finished
```

Jobs created inside `StartNew` belong to the batch. The **Batches** page shows every batch with a progress bar (succeeded, failed, running, pending); a batch's page lists its jobs and continuations and has **Requeue failed** and **Delete unfinished**. From code, `BatchJob.RequeueFailed(batchId)` and `BatchJob.Cancel(batchId)` do the same and return a `BulkJobResult`. A batch's bookkeeping expires `BatchJob.ExpireIn` (seven days) after creation on storages with the extended API. See [Fan-in and batches](jobs/fan-in-and-batches.md).

### Duplicate prevention

```csharp
[PreventDuplicates]                       // fingerprint = method + all arguments
public Task Reindex(string index) { ... }

[PreventDuplicates("reindex:{0}")]        // fingerprint = template over the arguments
public Task ReindexByName(string index, bool full) { ... }
```

A second copy is refused while the first is still pending (`Enqueue` returns `null`), or replaces it with `OnDuplicate = DuplicateHandling.ReplaceExisting`. The check happens at creation time, under a distributed lock, so duplicates never reach a queue. The fingerprint is released when the job reaches a final state or fails for good (unless `ReleaseOnFailure = false`). See [PreventDuplicatesAttribute](filters/concurrency.md#preventduplicatesattribute).

### Tags and notes

```csharp
[Tag("customer:{0}"), Tag("export")]
public Task Export(string customer, IJobConsole console) { ... }

JobTags.Add(jobId, "priority:high");   // after the fact
```

A tag is a word or a template over the arguments, lower-cased. The **Tags** page lists the tags with their job counts, each tag lists its jobs newest first, the details page shows a job's tags, and the command palette searches tags (`tag:contoso`). Every job page also has a Notes section, where operators write what they found or **Acknowledge** a failed job; `JobNotes` reads and writes them from code. See [Tags and notes](jobs/tags-and-notes.md).

### Schedule in plain English

```csharp
BackgroundJob.Schedule(() => SendReport(), "tomorrow at 9am");
BackgroundJob.Schedule(() => Cleanup(), "in 2 hours");
BackgroundJob.Schedule(() => Close(), "end of month");
```

`TimeText.Parse` understands relative phrases, times of day, weekdays, "next week", "end of day/month" and ISO dates, in `TimeText.DefaultTimeZone` (the machine's local zone by default). The job catalog's "later" field takes the same phrases. See [Delayed jobs](jobs/delayed-jobs.md).

### Job state and results from code

```csharp
var state = JobStorage.Current.GetJobState(jobId);              // name, reason, data, creation time
if (JobStorage.Current.TryGetResult<int>(jobId, out var count)) { ... }
JobStorage.Current.RequeueFailedJobs(job => job.ExceptionType == typeof(TimeoutException).FullName);
```

`GetJobState` reads a job's current state in one call, `TryGetResult<T>` reads what a succeeded job returned, and `RequeueFailedJobs` requeues failed jobs in bulk, optionally filtered and bounded by a time budget, like **Requeue all** on the Failed page. See [Managing jobs](jobs/managing-jobs.md#a-jobs-state-and-result).

## Controlling when jobs run

### Concurrency control

```csharp
[Throttle("tenant-{0}", MaxConcurrency = 2)]                 // distributed semaphore, {0} = first argument
[RateLimit("sms", limit: 100, perSeconds: 60)]                // sliding window
[CircuitBreaker("payments", failureThreshold: 5, windowSeconds: 60, openSeconds: 120)]
public Task Send(string tenant, Message message) { ... }
```

When `[Throttle]` or `[RateLimit]` won't let a job run, their `Action` decides: `Reschedule` (the default: back to the schedule, and the worker moves on), `Wait` (hold the worker, fail after `WaitTimeoutSeconds`) or `Skip` (delete the job). Several `[RateLimit]` attributes can be combined, for example 10 per second and 1000 per hour. While a `[CircuitBreaker]` circuit is open, jobs are rescheduled; open circuits are listed on the Insights page and can be reset there. See [Concurrency filters](filters/concurrency.md) and [CircuitBreakerAttribute](filters/retries.md#circuitbreakerattribute).

### Timeouts, deadlines and retention

```csharp
[JobTimeout(300)]                          // cancels the job's token after 5 minutes
[Deadline("00:10:00")]                     // flags the job when it isn't done within 10 minutes
[ExpireAfter(days: 30)]                    // keeps finished jobs for a month
public Task Rebuild(CancellationToken token) { ... }
```

`[Deadline]` cancels nothing: the Processing and job details pages flag overdue jobs, and a job that finishes late keeps a "finished N after its deadline" notice. `[ExpireAfter]` replaces the storage-wide retention for one method; `IncludeFailed = true`, or `UseFailedJobRetention(TimeSpan.FromDays(30))` for every job, lets final failures expire too. See [Timing filters](filters/timing.md).

### Run windows and blackouts

`[RunWindow("22:00-06:00", "Europe/Berlin")]` lets a job start only at night, `[RunWindow("09:00-17:00", WeekdaysOnly = true)]` only in office hours, and `[Blackout("02:00-04:00")]` never during a maintenance window. A job picked up at the wrong time is rescheduled for the window's next start, with the reason in its history. See [RunWindowAttribute and BlackoutAttribute](filters/timing.md#runwindowattribute-and-blackoutattribute).

### Drain mode: pause processing without stopping servers

```csharp
using var connection = JobStorage.Current.GetConnection();
DrainMode.Enable(connection, "deploy");                          // every server
DrainMode.PauseQueue(connection, "emails", "smtp outage");       // one queue
DrainMode.PauseServer(connection, serverId, "deploying");        // one server, by its full id
```

The Servers page has a **Pause processing** switch, the Queues page has **Pause** per queue and the Servers page **Pause** per server. While paused, workers hand new jobs back to the Scheduled state for 30 seconds (`DrainMode.HoldInterval`) and look again; running jobs finish. A banner lists what is paused, the health endpoint reports it, and the job diagnosis explains a held job. Use it before a deploy or a database maintenance window. See [Drain mode](server/drain-mode.md).

## Recurring jobs

### Pause, resume and edit

```csharp
RecurringJob.AddOrUpdate("digest", () => SendDigest(), Cron.FromText("weekdays at 7:30"));
RecurringJob.Pause("digest");
RecurringJob.UpdateSchedule("digest", Cron.FromText("every 2 hours between 8 and 18 on weekdays"));
```

The Recurring Jobs page has the same **Pause**, **Resume** and **Edit**, without redeploying. A paused job keeps its definition, stays paused across redeploys, can still be triggered by hand, and resumes from now without catching up. Managers from other packages that don't implement `IRecurringJobManagerV3` throw `NotSupportedException` from these calls. See [Pausing and updating](recurring-jobs/pausing-and-updating.md).

### Start and end dates

`RecurringJobOptions.StartAt` and `EndAt` (`DateTimeOffset?`) give a recurring job a window. No run is created for occurrences outside it, misfire handling respects it, a job past its end stays registered without a next run, and manual triggers still work. See [Time zones and misfires](recurring-jobs/time-zones-and-misfires.md).

### CRON builder, plain English and descriptions

```csharp
var cron = Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays().ToString();
var text = Cron.FromText("every 2 hours between 8 and 18 on weekdays");
var description = Cron.Describe("*/15 9-17 * * 1-5");
```

`CronBuilder` builds and validates expressions; its calls can come in any order, and conflicting calls throw. `Cron.FromText` turns plain English into CRON, and `Cron.Describe` turns CRON into plain English; the dashboard shows the description under the schedule editor and as tooltips. See [CRON and schedules](recurring-jobs/cron-and-schedules.md).

### Exact intervals

`Cron.Every(TimeSpan.FromMinutes(15))` returns the expression for an interval CRON can keep evenly, and throws for one it can't, such as 7 or 90 minutes. See [An exact interval](recurring-jobs/cron-and-schedules.md#an-exact-interval).

### Recurring jobs declared in code

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Add<ReportService>("daily-report", service => service.BuildDaily(), Cron.Daily(2))
    .AddHandler("purge-sessions", new PurgeSessions(Days: 30), Cron.Daily(3)));
```

The jobs are registered when the host starts, and a declaration that can't work stops the start. `RemoveUndeclared` cleans up the ones that left the code. See [Recurring jobs declared in code](recurring-jobs/recurring-jobs-in-code.md).

### Recurring job manifests

```csharp
var json = RecurringJobManifest.Export();             // every recurring job as JSON
RecurringJobManifest.Apply(json, prune: true);        // register them, remove the ones not listed
```

Schedules can live in source control and be promoted between environments. The Recurring Jobs page has **Export** and **Import…**. Applying is idempotent: an unchanged entry keeps its next run. Entries that fail are reported and don't stop the others; by default only methods the application already uses as jobs are accepted. See [Recurring job manifest](recurring-jobs/recurring-job-manifest.md).

## Monitoring

### Insights and queue health

```csharp
GlobalConfiguration.Configuration.UseJobInsights();
```

Insights records runs, failures and the last 200 durations per method. The Insights page shows failure rates, median, p95 and max, a trend sparkline and the invoker tier, opens with the methods whose behaviour changed (a slower median, a failure rate of 20% or more, an unusually slow last run) and a 7 × 24 heatmap of the last week. The Queues page adds, per queue, the median and p95 wait, jobs taken per minute and how long the backlog needs to clear, and flags stalled queues. See [Insights](monitoring/insights.md).

### Prometheus metrics

`GET /metrics` under the dashboard exposes the dashboard's numbers in the Prometheus text format: jobs by state, queue lengths, servers, recurring jobs and late ones, the paused flag, the health status, per-method run counts and duration percentiles, and per-queue wait percentiles and throughput. See [Prometheus endpoint](monitoring/insights.md#prometheus-endpoint).

### Health checks and probes

```csharp
builder.Services.AddHealthChecks().AddNightShift(o => o.Scope = NightShiftHealthCheckScope.Process);
```

The health check reports whether this process's server is alive (for liveness probes) or whether the cluster can process jobs. The dashboard's `GET /health` answers `200` with `healthy` or `degraded`, or `503` with `unhealthy`, and lists what it found: storage latency, live and stale servers, queues nobody listens to, paused processing, late or failing recurring jobs. `/ping` answers `pong` without touching the storage. `app.MapNightShiftHealth(...)` and `app.MapNightShiftMetrics(...)` serve the same data outside the dashboard. See [Health checks and probes](monitoring/health-checks.md).

### OpenTelemetry

```csharp
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString)
    .UseTelemetry());

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(NightShiftTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(NightShiftTelemetry.MeterName));
```

`UseTelemetry()` emits a span when a job is created and one when it is processed, in the same trace as the request that created it, plus the `nightshift.job.created`, `nightshift.job.duration` and `nightshift.job.active` metrics. It costs nothing when no listener is attached. See [OpenTelemetry](monitoring/opentelemetry.md).

### Alerts and webhooks

```csharp
GlobalConfiguration.Configuration.UseWebhookAlerts("https://hooks.slack.com/...", o => o.Format = WebhookFormat.Slack);
```

An alert is raised when a job finally fails (its retries are used up), a `[Deadline]` is missed or a circuit breaker opens. `WebhookAlertSink` posts it without blocking the job, as `Json` (the default), `Slack` (also Mattermost), `Discord` or `Teams` (an Adaptive Card). `UseAlerts(IAlertSink)` plugs in anything else: e-mail, PagerDuty, a metric. See [Alerts and webhooks](monitoring/alerts-and-webhooks.md).

### Activity feed and audit log

`UseActivityFeed()` records every state transition across the cluster; the home page shows them as they happen, and the **Timeline** page draws what every worker did in the last fifteen minutes from them. The feed is written after each state change commits, so it never slows down or fails one. Turn it on in every process that changes job states.

Every action taken through the dashboard is recorded in the audit log: who, from where, what, on which jobs, and the outcome. The **Audit** page shows the last thousand. It is on by default; `AuditLog.Enabled = false` turns it off. See [Audit log and activity feed](monitoring/audit-log-and-activity.md).

### Job id on every log entry

Servers added with `AddNightShiftServer` run each job inside `ILogger.BeginScope` with `JobId`, `JobName` and, when the job has them, `RecurringJobId` and `RetryCount`, so the entries the job's own `ILogger<T>` writes carry them. `services.ConfigureNightShiftJobLogging(o => o.BeginScope = false)` turns it off. See [Logging](monitoring/logging.md#job-logging-scope).

## Dashboard

The dashboard is redesigned, in light and dark, with a left sidebar that shows live counts for every job state, a command palette (`Ctrl+K`; type `>` for actions such as `> pause processing` or `> requeue #4711`), keyboard shortcuts for triage on every job list (`?` lists them), a theme switcher, and translations in 15 languages. See [Dashboard pages](dashboard/pages.md) for every page.

| Feature | Where | What it does |
|---|---|---|
| Job catalog | **Catalog** page | Lists every job method the source generator found, and runs one now, later or as a recurring job from a form built from its parameters. `JobCatalog.Add<T>()` adds more. See [Job catalog](jobs/job-catalog.md) |
| Run again with changes | Job details | The catalog's form, pre-filled with the job's arguments: fix one and run it again |
| Copy as C# | Job details | The `BackgroundJob.Enqueue<T>(x => x.Method(args))` statement that re-creates the job |
| Diagnosis | Job details | Answers "why isn't it running?": no server on the queue, processing paused, a parent still waiting, a dead server, retries used up |
| Lineage | Job details | Draws the continuations and fan-in parents and children around a job, coloured by state |
| Time in each state | Job details | A bar showing how long the job waited, ran and sat in retries |
| Notes and acknowledgements | Job details, Failed page | Notes for the next person; acknowledged failures carry a badge |
| ETA | Processing page | Predicts the remaining time from past runs, and flags a job that runs longer than usual |
| Failure clusters | Failed page | Groups the latest failures by exception type and method, with requeue and delete per cluster |
| Delete all, Requeue all, Live | Every job list | Act on the whole list, not only the selected rows; keep the list reloading |
| Exports | Every job list, job details | **Download CSV** (up to 10,000 rows) and **Export JSON** for one job |
| Schedule forecast | Recurring Jobs > Forecast | Every upcoming run for the next 24 hours or 7 days, with the times where many jobs start together |
| Late recurring jobs | Recurring Jobs page | A **Late** badge on a job whose run is more than a minute overdue |
| Wallboard | `/wallboard` | Large numbers and a status for a wall display, refreshed every ten seconds |
| Timeline | `/timeline` | One lane per worker for the last fifteen minutes (needs the activity feed) |
| Watchlist | Any job page | Follows chosen jobs in the sidebar and notifies when they finish |
| Maintenance | Servers page | **Clear all jobs** and **Reset storage**, through the new `JobStorage.Purge`, on storages that support it |

Dashboard extensions can give their navigation items an icon with `MenuItem.Icon`. See [Dashboard customization](dashboard/customization.md).

### Basic authentication

`new BasicAuthDashboardAuthorizationFilter(userName, password)` protects the dashboard of a host with no sign-in of its own. It answers the browser's prompt, compares in constant time and refuses plain HTTP outside loopback. See [Basic authentication](dashboard/authorization.md#basic-authentication).

## Hosting

### Keeping the host alive

IIS stops an idle worker process after 20 minutes, and the server inside it stops too. The application pool settings (`startMode="AlwaysRunning"`, preload, idle timeout 0) or a Windows Service remain the real fix. From the inside, Masterly.NightShift can request a URL of its own on a timer:

```csharp
services.AddNightShiftKeepAlive();                                                           // requests its own /nightshift/ping every 5 minutes
services.AddNightShiftKeepAlive(o => o.Url = "https://jobs.example.com/nightshift/ping");   // behind IIS: the public URL
```

Without DI, pass `new KeepAliveProcess("https://...")` as an additional process to `BackgroundJobServer`. The Servers page shows the process name, whether it runs under IIS, its uptime and the keep-alive status, and warns when an IIS-hosted process has no keep-alive. See [Keep-alive](server/keep-alive.md).

### Server options from configuration

```csharp
builder.Services.AddNightShiftServer(builder.Configuration.GetSection("NightShift:Server"));
```

Reads `ServerName`, `WorkerCount`, `Queues` and the intervals from `appsettings.json` or any other configuration source. `SqlServerStorageOptions` bind with the standard `Bind` call. See [Read the options from configuration](server/server-options.md#read-the-options-from-configuration).

## For developers

- **Testing guide.** [Testing](jobs/testing.md) shows how to unit-test code that creates jobs, the job classes themselves and filters, and how to run an integration test against LocalDB.
- **Web sample.** `samples/WebSample` maps the dashboard behind an authorization policy with antiforgery, and turns on the activity feed, insights and health checks. `dotnet run --project samples/WebSample` starts it on `http://localhost:5000`; open `/nightshift`, and the Development-only `/login` signs you in as an operator. It uses LocalDB unless `ConnectionStrings:NightShift` is set, and creates the database on first run.

## See also

- [Migrating from Hangfire](migrating-from-hangfire.md)
- [Breaking changes](breaking-changes.md)
- [Roadmap](roadmap.md)
