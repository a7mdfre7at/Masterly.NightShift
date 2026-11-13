# Dashboard pages

A tour of every page in the dashboard: what it shows and what you can do there. Use it to find where to look when a job is stuck, failing or missing.

## Quick example

Every job has its own page at `/nightshift/jobs/details/{jobId}`. Log that link when you queue work, so you can follow a job from your app's logs:

```csharp
using Masterly.NightShift;

public class ReportsController
{
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(IBackgroundJobClient jobs, ILogger<ReportsController> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    public string? QueueDailyReport()
    {
        var jobId = _jobs.Enqueue<ReportService>(x => x.BuildDaily());
        _logger.LogInformation("Daily report queued, see /nightshift/jobs/details/{JobId}", jobId);
        return jobId;
    }
}
```

## How it works

Pages are rendered on the server from the storage's monitoring API and other storage reads, each time you open them. Paths below are relative to the dashboard (for example `/jobs/failed` is `/nightshift/jobs/failed` with the default path). The counters in the sidebar and the realtime graph are refreshed by polling `/stats` every `StatsPollingInterval` milliseconds; list pages can refresh themselves with the **Live** toggle in the list toolbar. The toggle is remembered per page in the browser, and a list doesn't refresh while its tab is hidden. A live refresh asks for the list alone (`?list-refresh=1`): the page is rendered without the layout, and the Failed page skips its failure clusters.

Actions are `POST` requests to command routes. They are hidden for [read-only](authorization.md#read-only-dashboards) users and recorded in the [audit log](../monitoring/audit-log-and-activity.md). Actions that change a job's state (requeue, delete, enqueue now) go through the normal state machine, with a reason such as "Triggered via Dashboard UI", so filters and continuations behave as if your code made the change. When an action fails, a red bar at the top of the page shows the status the server answered with and its message.

List pages show 10, 20, 50, 100, 500 or 1000 rows per page; a URL can't ask for more than 1000.

Some pages depend on optional features. Wait times and throughput need [insights](../monitoring/insights.md) (`UseJobInsights()`), the live activity and the Timeline need the [activity feed](../monitoring/audit-log-and-activity.md) (`UseActivityFeed()`), and a few pages need a storage that supports extra queries (SQL Server supports them all). When something is missing, the page says so.

## Options / API

| Menu item | Path | What it is for |
|---|---|---|
| (brand / Home) | `/` | Overview: metrics, live graphs, activity |
| Jobs | `/jobs/enqueued` and the sub-items below | Jobs by state |
| Jobs > Enqueued | `/jobs/enqueued` | Queues and the jobs waiting in them |
| Jobs > Scheduled | `/jobs/scheduled` | Delayed jobs and pending retries |
| Jobs > Processing | `/jobs/processing` | Jobs running now |
| Jobs > Succeeded | `/jobs/succeeded` | Finished jobs |
| Jobs > Failed | `/jobs/failed` | Failed jobs, grouped by cause |
| Jobs > Deleted | `/jobs/deleted` | Deleted jobs |
| Jobs > Awaiting | `/jobs/awaiting` | Continuations waiting for their parent |
| Batches | `/batches` | Batches and their progress |
| Chains | `/chains` | Job chains, their steps, retry from a step |
| Retries | `/retries` | Jobs waiting for an automatic retry |
| Recurring Jobs | `/recurring` | Recurring jobs: trigger, pause, edit, import, export; `/recurring/details/{id}` for one job and its runs |
| Policies | `/policies` | Schedule policies (business calendars): edit, preview |
| Timeline | `/timeline` | What every worker did in the last 15 minutes |
| Insights | `/insights` | Duration and failure statistics per method, circuit breakers |
| Catalog | `/catalog` | Every job method in your code, ready to run |
| Tags | `/tags` | Jobs grouped by tag |
| Servers | `/servers` | Servers, workers, pausing processing, maintenance |
| Audit | `/audit` | Who did what in the dashboard |
| About | `/about` | The configuration in effect, global filters, job functions, monitors |
| Wallboard | `/wallboard` | A full-screen status board |

The sidebar shows counters next to Jobs, Retries, Recurring Jobs, Servers and each job state. The Jobs counter shows enqueued jobs, or turns red with the number of failed jobs when there are any.

## More examples

### Layout: on every page

- **Sidebar**: navigation with counters, the **Watching** list (jobs you watch, see the job page below; kept in the browser, one list per dashboard path), the theme switcher (system, light, dark) and the "Back to site" link (`DashboardOptions.AppPath`).
- **Top bar**: a live indicator, a **Refresh** button and **Search** (`Ctrl+K`). Search finds a job by its exact id, recurring jobs whose id contains the text, and tags containing it; start with `tag:` to search tags only.
- **Banners**: when processing is paused (drain mode) a banner says so, with the reason, the time it started and a **Resume** button. Paused queues and paused servers get their own banner. A pause left behind by a server that has stopped is not in the banner; see Servers below.
- **Footer**: version, the storage description (unless `DisplayStorageConnectionString` is `false`), the storage's clock where the storage has one (SQL Server, Redis, MongoDB, the in-memory storage, and Entity Framework Core on [a database it reads the clock of](../storage/entity-framework-core.md#the-databases-clock)), your app's clock (with a warning when they differ by more than 30 seconds), and how long the page took to render.

Keyboard shortcuts (press `?` to see them on lists):

| Keys | Action |
|---|---|
| `Ctrl+K` or `/` | Open search |
| `g` then `j`, `p`, `f`, `r`, `s`, `i`, `c`, `t`, `b`, `a`, `h` | Go to Enqueued, Processing, Failed, Recurring, Servers, Insights, Catalog, Tags, Batches, Audit, Home |
| `j` / `k` | Next / previous row in a job list |
| `x` | Select the current row |
| `Enter` | Open the current row's job |
| `r` | Run the list's main action (Requeue, Enqueue now, Trigger now) on the selection, after a confirmation |
| `d` | Delete the selection, after the delete button's confirmation |
| `?` | List the shortcuts |

The letter typed after `g` only navigates: `g` then `r` opens Recurring and doesn't requeue the selection.

The single-key shortcuts (`/`, `?`, `g`, `j`, `k`, `x`, `r`, `d`) can be turned off, for speech input or to avoid accidental key presses: clear **Single-key shortcuts** in the `?` list, or run `> turn single-key shortcuts off` from search. The choice is kept in the browser. `Ctrl+K` always works, and `> turn single-key shortcuts on` turns them back on.

Search and the shortcut list are dialogs that screen readers announce. Search is a combobox: the arrow keys move through the results, the number of results is announced, and closing it returns the focus where it was.

### Home (Overview)

- **Metrics**: tiles added with `UseDashboardMetric(...)`. `UseSqlServerStorage` adds SQL Server tiles (schema version, connections, transactions, data and log file sizes). Without any, this row is empty. See [Customization](customization.md#adding-a-metric-to-the-home-page).
- **Realtime graph**: succeeded, failed and deleted jobs per poll interval while the page is open.
- **Live activity**: every state change across the cluster, newest first, as it happens. Click a job to open it.
- **History graph**: succeeded, failed and (when the storage supports it) deleted jobs per hour for the last day, or per day for the last week.

### Jobs > Enqueued (Queues)

One row per queue: length, fetched jobs, wait time (median and p95 over the last 200 starts), jobs started per minute, an estimate of when the queue will be empty, and the next few jobs. A queue with waiting jobs that no worker has touched for five minutes is marked **stalled**. Wait and throughput need insights.

- **Pause** / **Resume** a single queue. Its jobs are held while the other queues keep flowing.
- Click a queue to see its jobs (`/jobs/enqueued/{queue}`): **Delete selected**, **Delete all** and **Download CSV**.
- Click the fetched count to see jobs fetched by a worker but not yet acknowledged (`/jobs/enqueued/fetched/{queue}`), for storages that report them: **Requeue** and **Delete**.

### Jobs > Scheduled

Delayed jobs and when they will be enqueued. Actions: **Enqueue now**, **Delete selected**, **Delete all**, **Download CSV**.

### Jobs > Processing

Running jobs with their server, start time, reported progress, an ETA based on the method's usual duration (from insights), a "longer than usual" warning, and deadlines. A job whose server stopped reporting heartbeats is flagged "Looks like the job was aborted". Actions: **Requeue**, **Delete selected**, **Delete all**, **Download CSV**.

Deleting a processing job asks the running code to stop through its cancellation token; see [Cancellation](../jobs/cancellation.md).

### Jobs > Succeeded

Finished jobs with the time they finished, the latency (from creation until processing started), the duration and the total duration. Actions: **Requeue**, **Delete all**, **Download CSV**.

### Jobs > Failed

- **Failure clusters**: the latest 500 failures grouped by exception and method (up to 8 groups of 2 or more), with first and last occurrence. **Requeue all** or **Delete all** for a whole group.
- The list shows the exception type and message; **More details** opens the stack trace. Jobs with an acknowledging note show **Acknowledged** (on pages of more than 100 rows, only the first 100 rows are checked for notes).
- Actions: **Requeue**, **Delete selected**, **Delete all**, **Requeue all**, **Download CSV**. **Requeue all** puts every job that is still failed back in its queue, like **Delete all** does for deleting.

Failed jobs don't expire unless you set a retention period ([Failed jobs](../filters/timing.md#failed-jobs)), so you can requeue them later. Delete the ones you don't need.

### Jobs > Deleted

Deleted jobs, with the exception when they were deleted after a failure. Actions: **Requeue**, **Download CSV**.

### Jobs > Awaiting

Continuations waiting for their parent: the parent job, the continuation options and how long it has waited. Actions: **Enqueue** (start it without waiting), **Delete selected**, **Delete all**. If the storage can't list awaiting jobs, the page explains that continuations still work. See [Continuations](../jobs/continuations.md).

### Batches

Every batch with a progress bar and its succeeded, failed and pending counts. Open a batch (`/batches/{id}`) to see its jobs, a page at a time, and the continuations that start when it finishes. On a storage without the extended API, the batch page shows its first 500 jobs and says how many more there are. Actions on a batch: **Requeue failed** and **Delete unfinished**, within the same limits as **Delete all** (see [Things to know](#things-to-know)). See [Fan-in and batches](../jobs/fan-in-and-batches.md).

### Chains

Every [job chain](../jobs/chains.md), newest first (on a storage that can't read ranges of a set, up to 100 of them), with its first step, the number of steps, when it was created and its status. A chain stays in the list while a step of it is left to run or failed for good, and for the [chain retention](../jobs/chains.md#things-to-know) after that. The statuses: **Waiting** (no step has started yet), **Running**, **Done** or **Finished with failures**.

Open a chain (`/chains/{id}`) to see it as a flowchart, top to bottom. The header shows its status, its generation (how many times it was retried), its delete behavior and how many jobs and groups it has. In the flowchart:

- **Each step is a card** in its state's color: the method and its arguments (the values the server supplies, such as `IJobConsole` and cancellation tokens, are left out; each argument is cut to 64 characters, and all of them to 200), the state, the **start condition** (*On success*, *On failure*, *All succeeded*, *Any failed* and so on) with whether it was met, the progress the job reported, and the job number, when it started, how long it ran and how many times it was retried. The method links to the job's page. A step that won't run (its condition wasn't met, or it waits after a step that failed for good) is dimmed and marked **Not run**.
- **Parallel branches** (`ThenMany`) sit in a **Parallel group** box with the group's status, how many of its jobs are still to finish, and how many succeeded and failed. The steps after a group that react to its outcome (`ThenAfterAll`, `ThenAfterAnyFailed`...) sit in an **Outcomes** box.
- **Links** are drawn in the accent color where a condition was met, in the failure color where a step ran because something failed, and dashed to a step that won't run.
- **Zoom** out, in, or to fit the width, and switch to **full screen** (Esc leaves it). The flowchart fits the page's width when it opens.

The first 200 steps are drawn, whole levels at a time; the others are listed below the flowchart. **Chain logs**, under it, merges the console lines of every step in time order (the newest 500), each linked to its step; it opens by itself when a step wrote something. Actions:

- **Retry from here** on a failed or deleted step, once the chain has settled (nothing is running or waiting to run): the step runs again with everything after it, and the steps before it aren't re-run. It calls `RetryChainFrom`.
- **Delete** while the chain is still running: deletes the steps that haven't finished, following the chain's delete behavior (`Restrict` refuses while a step is processing).

A step whose method was renamed or removed since the chain was created shows its step key (`1.0`) in place of the method, without arguments; **Retry from here** is refused, with the reason shown, when the retry would have to create such a step again.

A chain step's job page links to its chain next to the job's tags. Its history can show a short `Scheduled` state with the reason `Waiting for chain ... to be recorded`, when a worker took the step while its chain was being retried or replaced, or while its creation took more than 5 seconds; a first step held this way while its chain was created is then `Enqueued` with the reason `The chain is recorded`. A first step taken during a creation that takes milliseconds, as most do, shows nothing of it. A step held by a retry that was abandoned shows `Scheduled`, `Enqueued` and the state it was in before, with the reason `The retry of chain ... was abandoned`. A job that its chain's record doesn't list is deleted with a reason that says so: see [When a step runs](../jobs/chains.md#when-a-step-runs).

### Retries

Jobs that failed and are waiting for an automatic retry, with their current state, the reason and when the retry is due. Actions: **Enqueue now** (retry immediately), **Delete selected**, **Delete all**. See [Retries](../filters/retries.md).

### Recurring Jobs

Every recurring job with its schedule, time zone, job, next and last execution, and when it was created. Badges show jobs that are **Paused**, **Late** (the next run, or the retry the scheduler set after an error, was due more than a minute ago and nobody started it), or disabled (the CRON expression is invalid or never fires). A job with a start or end date (`RecurringJobOptions.StartAt` and `EndAt`) shows **Starts** and the date until it starts, **Ends** and the date under its next run while it is active, and **Ended** once its end date has passed and it has no next execution. A summary at the top warns when jobs are late; when the list has more than one page, it counts the late jobs of the page you see and says so (the `/health` endpoint checks them all).

- **Trigger now**, **Pause**, **Resume**, **Remove** for the selected jobs, and **Remove all**. **Trigger now** builds the job from its type, so the dashboard's process needs the job assemblies: a recurring job whose type it can't load is reported by id with the load error, and the other selected jobs are still triggered.
- **Edit** a job's schedule: a CRON expression or plain English ("every 15 minutes", "weekdays at 9:30"), the time zone, the queue, and how missed occurrences are handled. The editor previews the next five runs before you save, leaving out runs before the job's start date or after its end date. Clear the queue to put the job back in the `default` queue. The time zone list holds the zones of the dashboard's host; a job whose zone isn't in it (an IANA id on Windows, a Windows id on Linux, or an id only your `TimeZoneResolver` knows) gets its zone added to the list, and the preview uses it. Open `/recurring?edit={id}` to go straight to a job's editor, whichever page of the list the job is on.
- **Export** downloads every recurring job as a JSON manifest; **Import** applies a manifest (pasted or from a file), optionally removing jobs that aren't in it. Entries for methods the application doesn't already use as jobs are reported as errors unless `DashboardOptions.AllowAnyMethodInManifestImport` is set. For read-only users, Export leaves out the values of `[Redacted]` parameters. See [Recurring job manifest](../recurring-jobs/recurring-job-manifest.md).
- **Forecast** (`/recurring/forecast`): when your recurring jobs will run in the next 24 hours or 7 days (`?window=7d`), each run sized by the method's median duration from insights (runs before a job's start date or after its end date are left out), and **hot spots** where several jobs start within the same five minutes. At most 2,000 runs of one job are projected: a job that runs more often in the window (every minute over 7 days, for example) shows "≥2000×", its band reaches the end of the window (or the job's end date), it is left out of the hot spots, and the run total says "≥".

Click a job's id to open its details page (`/recurring/details/{id}`): the schedule with its description, the job, time zone, queue, misfire handling, [schedule policy](../jobs/schedule-policies.md) and skip-after window, its next runs, and its last 20 runs (the storage keeps 50; [`GetRecurringJobRuns`](../recurring-jobs/creating-recurring-jobs.md#runs-and-upcoming-runs) reads them all) with their state, scheduled time, duration, retries, server and reason. A job declared with a [recurring job set](../recurring-jobs/recurring-jobs-in-code.md) shows a **From code** badge (changes made in the dashboard are replaced the next time the app starts), and a job the set no longer declares shows **Retired**. **Trigger now**, **Pause** and **Resume** act on that job.

A removed recurring job comes back if your app registers it again at startup.

### Policies

[Schedule policies](../jobs/schedule-policies.md) as cards: the time zone, allowed days and time windows, excluded dates, date providers, the policies it requires or is blocked by, and its revision. **New policy** and **Edit** open an editor with the same fields (windows as `09:00-17:00`, dates as `2026-12-24` or `12-25` for every year). Saving sends the revision you opened: when someone else saved the policy in the meantime, the save is refused (`409`) and you reload. **Delete** works the same way. Saving reschedules the recurring jobs that use the policy.

**Preview** checks the next 20 occurrences of a CRON expression or plain-English schedule against a policy and marks each one **Allowed** or **Blocked**, with the reason. A date provider that isn't registered in the dashboard's process blocks every date of a policy that uses it, as on a server.

### Timeline

A lane per worker showing what it ran in the last 15 minutes, built from the activity feed, with held or retried runs marked. Use it to spot idle workers, long runs and bursts.

### Insights

Needs `UseJobInsights()` or `[JobInsights]` on your methods; see [Insights](../monitoring/insights.md).

- **Summary**: methods tracked, runs recorded, failure rate, and which invokers (generated, compiled, reflection) ran the methods on this server.
- **Needs attention**: methods whose behavior changed: a high failure rate, a run much slower than usual, or a slowdown trend.
- **Methods table**: runs, failures, median, p95 and max duration, a trend sparkline, the last run and the invoker.
- **Circuit breakers**: circuits that are open, half-open, recently closed or have failures within their window, with their state, the failures within the window and since when, with **Reset** (which closes the circuit and removes it from the list until it fails again). See [Retries](../filters/retries.md).
- **Activity, last 7 days**: a heatmap of finished jobs per hour (UTC), red where failures outnumber successes.

### Catalog

Every job method found in your code (discovered by the source generator, or added with `JobCatalog.Add<T>()`), grouped by type, with its parameters, filters and run statistics. **Run...** opens a form with one field per parameter (parameters supplied by the server, such as a cancellation token, are filled in for you) and creates the job now, later (a number of minutes or a phrase such as "in 2 hours") or as a recurring job. See [Job catalog](../jobs/job-catalog.md).

### Tags

A tag cloud and a table of tags with their job counts, in name order and a page at a time. The filter box (press Enter) keeps the tags whose name contains the text, ignoring case, across all pages (`/tags?filter=billing`); paging then goes through the matches. Open a tag (`/tags/{tag}`) to list its jobs. Jobs that have expired leave their ids in the tag: the page hides them and says how many it hid, and **Remove them from the tag** forgets the ones on that page. **Forget** removes a tag without touching its jobs. See [Tags and notes](../jobs/tags-and-notes.md).

### Servers

- **Servers table**: name, workers, queues, start time and last heartbeat. Servers that missed their heartbeat for longer than `ServerPossiblyAbortedThreshold` are marked **Possibly aborted**; they are removed automatically after the server timeout. **Pause** a single server to make it stop taking jobs: its workers stop fetching within a few seconds, the jobs stay in their queues for the other servers, and its running jobs finish (see [Drain mode](../server/drain-mode.md#a-paused-server-stops-fetching)); **Resume** it.
- **Pauses left by stopped servers**: a server gets a new id every time it starts, so a pause outlives the server it was set on. Pauses whose server is no longer registered are listed here with **Clear**. They have no effect: the banner and `/health` ignore them.
- **This process**: the process hosting the dashboard, its uptime, an IIS warning when it is hosted by IIS without keep-alive, and the [keep-alive](../server/keep-alive.md) status.
- **Processing**: **Pause processing** for the whole cluster ([drain mode](../server/drain-mode.md)), with an optional reason, and **Resume processing**.
- **Maintenance** (only when the storage supports purging): **Clear all jobs** removes every job with its history, queues, counters and statistics but keeps recurring job definitions, servers, the audit log and drain mode (with paused queues and servers); **Reset storage** removes everything but the audit log, so paused processing resumes. Neither can be undone. See [Purging data](../storage/schema-and-upgrades.md#purging-data).
- **Job invokers in this process**: how many methods ran through generated, compiled and reflection invokers. See [Source-generated invokers](../jobs/source-generated-invokers.md).

### Audit

Who did what from the dashboard: time, user, action, targets and detail. Users who can change things also see the IP address of each entry; read-only users see it only when `DashboardOptions.DisplayAuditIpAddressesToReadOnlyUsers` is `true`. Choose how many entries to show (50, 100, 250 or 1000). Empty when `AuditLog.Enabled` is `false`. See [Audit log and activity](../monitoring/audit-log-and-activity.md).

### About

What this application configured, as the dashboard's process sees it: the storage, the data compatibility level and the options turned on (default recurring time zone, stale restart limit, argument compression, [deployment fencing](../server/reliability.md#deployment-fencing) with this process's deployment and the order it was given, followed in brackets by the current deployment when it is another one, date providers), the global filters in the order they run, the registered [job functions](../jobs/job-functions.md), and the [job monitors](../monitoring/job-monitors.md) with their status. Servers in other processes may be configured differently.

### Wallboard

A full-screen status board for a TV or a second screen, with no navigation. It is always dark, whatever `DarkModeEnabled` says, because a dark screen is easier to read from across a room. It reloads every 10 seconds and shows the time in the viewer's time zone (in UTC, marked so, when scripts are off), the overall health ("All systems go", "Needs attention", "Processing is down"), the success rate over the last hour, jobs running now with their progress, the queues, and anything that needs attention (including open circuit breakers). Storages count finished jobs per clock hour, so "the last hour" is the current hour plus the share of the previous hour that falls within the last 60 minutes, as if its jobs were spread evenly.

### Job details

Open any job id to see `/jobs/details/{jobId}`:

- **Header**: the job's display name, plus warnings when its server is gone or stopped reporting (the job may be aborted), or when the job has finished and will expire.
- **Code**: the method call with its arguments and the queue. Parameters marked `[Redacted]` are masked. When the dashboard can't load the job's method, it shows what the storage knows instead (type, method, the load error); the raw arguments and payload are shown only to users who aren't read-only, because `[Redacted]` can't be read without the method (see [Read-only dashboards](authorization.md#read-only-dashboards)).
- **Toolbar**: **Watch** (follow the job in the sidebar and get a browser notification when it finishes), **Export JSON** (method, arguments, history, parameters, console and notes in one file), **Run again with changes** (a new job from this one with edited arguments, now, later or on a schedule), and **Copy as C#** (the statement that creates the job again).
- **Tags, batch and chain**: links to the job's tags, to its batch and to its chain.
- **Diagnosis**: why the job is where it is and what would move it, for example "No running server listens to queue reports", "Retry 3 of 10 is pending" or "Parent 42 has failed and will not be retried".
- **Notes**: comments for the next person, and **Acknowledge** to mark a failure as looked at.
- **Lineage**: the jobs this one waited for and the ones waiting for it.
- **Live**: for running jobs, progress, console lines and checkpoints written through the job console, updated while you watch. See [Progress, console and checkpoints](../jobs/progress-and-console.md).
- **Parameters**, **Continuations** and **Fan-in continuations**.
- Sections added by your own [job details renderers](customization.md#adding-a-section-to-the-job-page).
- **Time in each state**: a bar showing how long the job spent in each state.
- **State**: the full history, newest first, with each state's reason and data. **Requeue** and **Delete** are shown once the job has more than one state. A state whose data this dashboard can't read (a continuation's next state of a type it doesn't have, for example) is shown as raw data or by its type name instead of breaking the page.

### Endpoints for tools

These routes are meant for scripts and monitoring. They go through the dashboard's authorization filters like the pages (see [Letting health probes in](authorization.md#letting-health-probes-in)).

| Route | Method | Answers |
|---|---|---|
| `/ping` | `GET` | `200 pong`, without touching the storage. Used by [keep-alive](../server/keep-alive.md) and liveness probes. |
| `/health` | `GET` | JSON health report: storage reachable, servers alive, queues served, processing not paused (pauses left by servers that are no longer registered don't count), no late or broken recurring jobs. `200` for healthy or degraded, `503` when jobs can't be processed. Reused for 5 seconds; storage errors are logged and the report only names the failed check. |
| `/metrics` | `GET` | The same numbers in Prometheus text format: jobs by state, queue lengths, servers, recurring jobs, paused flag, health, and per-method and per-queue statistics from insights. Reused for 5 seconds. `MapNightShiftHealth`/`MapNightShiftMetrics` serve both outside the dashboard; see [Health checks and probes](../monitoring/health-checks.md). |
| `/stats` | `POST` | Current values of the metrics named in the `metrics[]` form fields, as JSON. Used by the page itself. A `GET` answers `405`. |
| `/activity?count=50&since={unix ms}` | `GET` | The newest activity feed entries (up to 200). |
| `/jobs/live?jobs=1,2,3` | `GET` or `POST` | Current state, progress and console (the last 300 entries) of up to 100 jobs. With `console=0` the console isn't read and `entries` is left out; the Processing page's rows ask for that, and don't poll while the tab is hidden. |
| `/search?q=...` | `GET` | The search results as JSON. |
| `/jobs/export/{jobId}` | `GET` | One job as JSON. Arguments marked `[Redacted]` are masked; when the method can't be loaded, the raw arguments are left out. |
| `/jobs/{failed,succeeded,scheduled,processing,deleted}/export`, `/jobs/enqueued/export/{queue}` | `GET` | A job list as CSV, up to 10,000 rows, sent 500 rows at a time. Columns: `Id`, `State`, `Job`, `Time` (UTC), `Seconds` (duration for succeeded jobs, time since start for processing and enqueued ones, delay for scheduled ones), `Server` (processing jobs), `ExceptionType` and `ExceptionMessage` (failed jobs, the message cut at 1,000 characters). |
| `/recurring/export` | `GET` | The recurring job manifest as JSON. |
| `/recurring/preview?cron=...&timezone=...` | `GET` | The next five occurrences of a CRON expression or plain-English schedule, as the schedule editor shows them. Optional `start` and `end` (Unix milliseconds) leave out runs outside a job's start and end dates. Always answers `200`; a problem is reported in the `error` field. |
| `/maintenance/pause`, `/maintenance/resume` | `GET` | The current drain mode status as JSON. A `POST` turns drain mode on or off, like the buttons on the Servers page. |
| `/recurring/trigger-one/{id}`, `/recurring/pause-one/{id}`, `/recurring/resume-one/{id}` | `POST` | Trigger, pause or resume one recurring job. |
| `/chains/retry/{stepJobId}`, `/chains/delete/{chainId}` | `POST` | Retry a settled chain from a step, or delete a running chain's unfinished steps. `204` when done; `409` with `{ "error": ... }` when another change of the same chain holds its lock; `422` with `{ "error": ... }` when refused or for invalid input. |
| `/policies/save`, `/policies/delete/{name}/{revision}` | `POST` | Save a schedule policy from the editor's form fields, or delete it. `204` when done; `409` when the revision changed or another save or delete holds the lock; `422` with `{ "error": ... }` for a bad value, such as a number that isn't one or overflows. |

## Things to know

- "Delete all", "Requeue all" and the maintenance buttons work on everything in the list or storage, not only the page you see. They ask for confirmation, but there is no undo.
- One click of "Delete all", "Requeue all", "Remove all" (recurring jobs), "Requeue failed" or "Delete unfinished" (a batch) changes at most 100,000 jobs and works for at most about 20 seconds, so a proxy in front of the dashboard doesn't time out first. Then it says how many jobs it changed and how many may remain; click again to go on. It skips jobs that are no longer in the list's state, such as the deleted jobs that SQL Server still lists in a queue until a worker fetches them. These routes answer `{ processed, remaining, complete }`.
- Ids that contain `/` work in every page and button: the scripts send them encoded, and the dashboard turns `%2F` back into `/`.
- Actions you take are applied by your servers: requeued jobs run only when a server listening to their queue is running.
- Pages only read. Opening a page never changes the storage; every change is a `POST` from a button.
- The dashboard formats dates in the browser, in the viewer's time zone and the dashboard's language. See [Customization](customization.md#dates-and-times).

## See also

- [Setting up the dashboard](setup.md)
- [Authorization](authorization.md)
- [Customization](customization.md)
- [Managing jobs](../jobs/managing-jobs.md)
- [Drain mode](../server/drain-mode.md)
- [Monitoring](../monitoring/README.md)
