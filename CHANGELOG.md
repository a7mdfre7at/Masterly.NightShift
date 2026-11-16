# Changelog

All notable changes to the Masterly.NightShift packages. Each version's section, without its heading, becomes the body of that version's GitHub Release, so it opens with a one-line summary.

## [1.0.0]

Initial release of Masterly.NightShift: background jobs, recurring schedules and workflows for .NET 8 and later, kept in SQL Server, Entity Framework Core, Redis, MongoDB or memory, with a dashboard to run and watch them. It is built on Hangfire 1.8 and reads the data Hangfire wrote to SQL Server.

### Packages

- `Masterly.NightShift`: the meta-package, with Core, SqlServer and AspNetCore.
- `Masterly.NightShift.Core`: jobs, scheduling, the server, filters and the dashboard.
- Storages:
  - `Masterly.NightShift.SqlServer`;
  - `Masterly.NightShift.EntityFrameworkCore`, for any EF Core relational provider;
  - `Masterly.NightShift.Redis`, on StackExchange.Redis;
  - `Masterly.NightShift.MongoDB`, for replica sets;
  - `Masterly.NightShift.InMemory`, for development and tests.

  One conformance suite holds all of them, SQL Server included, to the same behaviour.
- `Masterly.NightShift.NetCore`: dependency injection and hosted servers.
- `Masterly.NightShift.AspNetCore`: dashboard endpoints and health checks.
- Targets `net8.0` and `net10.0`.
- Licensed `MIT AND LGPL-3.0-or-later`: code written for NightShift is MIT, and code derived from Hangfire stays LGPL. Core, SqlServer, NetCore and AspNetCore carry both license texts; the InMemory, Redis, MongoDB and EntityFrameworkCore storages and the meta-package hold no Hangfire-derived code and are MIT.

### Jobs and workflows

- **Kinds of job:** fire-and-forget, delayed and recurring jobs.
- **Ways to define one:**
  - lambda expressions;
  - job classes: `IJob`, `IJobHandler<TArgs>`, `client.Enqueue<TJob>()`;
  - named job functions: `[JobFunction]`, `AddNightShiftFunction`, `EnqueueFunction`. Their payload is checked when the job is created.

  Jobs are invoked without reflection.
- **Many at once:** `EnqueueMany` creates many jobs in a few storage round trips, and runs the client filters for each one.
- **Continuations:** on success, on failure, when the parent starts, or on any outcome.
- **Fan-in joins over several parents:**
  - `ContinueJobWithAll`, `ContinueJobWithAny`, `ContinueJobWithJoin` and `FanInMode`;
  - parent results passed to the children with `UseResultsInContinuations`.
- **Batches:** `BatchJob`, with cancel and requeue-failed.
- **Chains (`JobChain`):**
  - workflows of steps, parallel branches, and joins on success or failure;
  - created together and recorded, so a chain can be retried from a step, replaced or deleted;
  - recurring chains with `RecurringJob.AddOrUpdateChain`.
- **Execution control:**
  - automatic retries with delays and exception filters;
  - `[JobTimeout]` and `[SkipIfLate]`;
  - `TerminateJobException`, which skips, cancels or fails a job without retries;
  - concurrency limits, throttling and rate limits: `[Throttle]`, `[RateLimit]`, `[DisableConcurrentExecution]`;
  - `[PreventDuplicates]`, circuit breakers and run windows;
  - long-running workers: `[LongRunning]`;
  - `IJobExceptionHandler`.
- **Inside a running job:**
  - progress, a console and checkpoints;
  - `PerformContext`, with when the job was due, how late it started, its retry count and its parents;
  - tags and notes.
- **Waiting for a result:** `WaitForCompletionAsync` and `TryGetResult<T>`.

### Scheduling

- **Recurring jobs declared in code** with `AddNightShiftRecurringJobs` and `RecurringJobSet`:
  - applied at start-up;
  - schedules may come from configuration (`%Section:Key%`);
  - a set retires or removes the jobs it no longer declares.
- **Recurring job options:**
  - start and end dates;
  - `SkipAfter` for runs that start too late;
  - `MisfireHandlingMode`;
  - `UseDefaultRecurringJobTimeZone`;
  - `Cron.Every(TimeSpan)`.
- **Run history:** each recurring job keeps its last 50 runs, and its next runs can be listed.
- **Schedule policies:** business calendars (`SchedulePolicy`).
  - **Rules:** allowed weekdays, daily windows, excluded dates and dates from `IScheduleDateProvider`, other policies required or excepted, and a time zone.
  - **Storage:** stored by name with a revision.
  - **When a job is blocked:** it is moved forward, skipped or rejected.
  - **Recurring jobs:** a blocked occurrence is left out.

### Reliability

- **Restarts:**
  - `UseStaleRestartLimit` fails a job that keeps being picked up again after its server stopped;
  - lease fencing refuses the late state change of a job's former owner.
- **Pausing:**
  - drain mode pauses the whole cluster, a queue or one server;
  - `UseDeploymentFencing` pauses the servers of an older deployment;
  - `BackgroundJobServerOptions.StartMode = Manual` and `IBackgroundJobServerControl` start and stop hosted servers from code.
- **Data:**
  - `UseJobRetention` sets retention per state;
  - `UseArgumentCompression` stores long arguments compressed.
- **Clocks:** schedules and heartbeats are judged by the storage's clock where it has one.

### Storages

- **SQL Server:**
  - a commit is one round trip, and creating a job takes two;
  - callers creating jobs at the same time are written in groups;
  - `AllowDelayedDurability` lets fetches and moves to Processing skip the wait for the disk.
- **Entity Framework Core:** `context.NightShift()` stages jobs in your context, so they commit or roll back with `SaveChanges`, your transaction or a `TransactionScope`.
- **Redis and MongoDB:** each transaction is one Lua script on Redis and one session transaction on MongoDB.
- **For third-party storages:** optional capabilities are advertised through `JobStorage.HasFeature`, so a storage implements only what it can.

### Dashboard

- **Look:** Material Design 3 on the NightShift brand, in light and dark themes. It uses the documentation site's fonts and serves them itself, so it makes no external requests.
- **Languages:** 16.
- **Pages:**
  - the job lists, each job's page, and the queues;
  - recurring jobs, with a schedule editor and a forecast of the next runs;
  - chains, drawn as flowcharts, and batches and tags;
  - schedule policies;
  - a catalog of the job methods, with a form to run any of them;
  - insights, a timeline, a wallboard and the servers;
  - an audit log and an about page.
- **Working in it:**
  - details and editors open in side sheets;
  - row actions appear on hover and keyboard focus;
  - live refresh, a command palette, read-only mode and deep links.
- **Accessibility:** works by keyboard and screen reader, at phone, tablet and desktop widths.
- **Extensions:**
  - shared page helpers on `HtmlHelper`;
  - script helpers on `NightShift`;
  - colour tokens, and a list of the classes extensions can rely on.

### Observability and integration

- **Telemetry:**
  - OpenTelemetry-compatible spans and metrics (`UseTelemetry`);
  - Prometheus metrics (`MapNightShiftMetrics`);
  - health checks: `MapNightShiftHealth`, and `AddHealthChecks().AddNightShift()`.
- **Alerts:**
  - webhooks for Slack, Microsoft Teams, Discord and plain JSON, with rules, cooldowns and retries;
  - job monitors for daily deadlines, long runs and repeated failures.
- **Logs:**
  - `AddNightShiftJobLogs` writes a job's `ILogger` lines into its console;
  - every job runs in a logging scope with its id and name.
- **History:** insights per method and queue, an audit log, and an opt-in activity feed.
- **Hosting:**
  - `AddNightShift` and `AddNightShiftServer` on `IHostApplicationBuilder`;
  - server options from configuration;
  - `UseTimeProvider`;
  - `BasicAuthDashboardAuthorizationFilter`.

### Coming from Hangfire

- **Your data:** NightShift reads what Hangfire wrote to SQL Server: the `HangFire` schema, stored job types, and arguments serialized by Newtonsoft.Json. All JSON now goes through System.Text.Json.
- **What differs:**
  - the APIs Hangfire marked obsolete are removed;
  - the dashboard runs on Bootstrap 5, jQuery 4 and Chart.js 4;
  - SQL Server uses `Microsoft.Data.SqlClient`.

  Each difference is listed in [Changes from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/changes-from-hangfire).
- **Moving over:** see [Migrating from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire).
- **Speed:** a job runs through its filters in about half the time, with 60% less memory, than in Hangfire 1.8.25. See the [benchmarks](https://a7mdfre7at.github.io/Masterly.NightShift/docs/benchmarks).

The full documentation is at https://a7mdfre7at.github.io/Masterly.NightShift/docs.
