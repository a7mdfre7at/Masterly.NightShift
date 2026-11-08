# Masterly.NightShift documentation

Masterly.NightShift runs background jobs in .NET applications: fire-and-forget, delayed, recurring and continuation jobs. Jobs are stored in a database you already run (SQL Server ships in the box), so they survive restarts and deploys, and a built-in dashboard shows what is happening.

**New here?** Read [Core concepts](getting-started/core-concepts.md), then follow the [ASP.NET Core quick start](getting-started/quick-start-aspnetcore.md) or the [console and Worker Service quick start](getting-started/quick-start-worker.md). You'll have a job running and visible in the dashboard in a few minutes.

**Coming from Hangfire?** Start with [Migrating from Hangfire](migrating-from-hangfire.md). Your existing SQL Server data keeps working.

## A first look

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));
builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapNightShiftDashboard();   // served at /nightshift

app.MapPost("/users/{id:int}/welcome", (int id, IBackgroundJobClient jobs) =>
{
    jobs.Enqueue<EmailService>(email => email.SendWelcome(id));
    return Results.Accepted();
});

app.Run();
```

`AddNightShift` configures the storage, `AddNightShiftServer` starts the workers that run jobs, and `Enqueue` stores a call to `EmailService.SendWelcome(id)` that a worker runs as soon as it is free. If it throws, it is retried automatically.

## Find what you need

### [Getting started](getting-started/README.md)

| Page | What it covers |
|---|---|
| [Installation](getting-started/installation.md) | Packages, target frameworks, the SQL client package |
| [Quick start: ASP.NET Core](getting-started/quick-start-aspnetcore.md) | From an empty web app to a job in the dashboard |
| [Quick start: console and Worker Service](getting-started/quick-start-worker.md) | Generic Host apps, and plain console or .NET Framework apps without DI |
| [Core concepts](getting-started/core-concepts.md) | Jobs, states, storage, servers, workers, queues, filters: the mental model |

### [Jobs](jobs/README.md)

| Page | What it covers |
|---|---|
| [Fire-and-forget jobs](jobs/fire-and-forget.md) | Run something now, in the background |
| [Delayed jobs](jobs/delayed-jobs.md) | Run something later, including plain-English times like "tomorrow at 9am" |
| [Continuations](jobs/continuations.md) | Run a job after another one finishes, and pass its result along |
| [Fan-in and batches](jobs/fan-in-and-batches.md) | Wait for a whole set of jobs; create jobs as a group |
| [Job arguments](jobs/job-arguments.md) | What you can pass, how it's serialized, how the job's class is created |
| [Cancellation](jobs/cancellation.md) | Cancellation tokens, shutdown, deleting a running job |
| [Progress, console and checkpoints](jobs/progress-and-console.md) | Show a job's progress and log lines live; resume after a failure |
| [Tags and notes](jobs/tags-and-notes.md) | Label jobs so you can find them; attach notes |
| [Job catalog](jobs/job-catalog.md) | Let operators start chosen jobs from the dashboard |
| [Managing jobs](jobs/managing-jobs.md) | Delete, requeue, reschedule, change state, query jobs |
| [Source-generated invokers](jobs/source-generated-invokers.md) | How job methods are called without reflection |
| [Testing](jobs/testing.md) | Unit tests for code that creates jobs, job classes and filters; integration tests against LocalDB |

### [Recurring jobs](recurring-jobs/README.md)

| Page | What it covers |
|---|---|
| [Creating recurring jobs](recurring-jobs/creating-recurring-jobs.md) | `AddOrUpdate`, options, triggering and removing |
| [CRON and schedules](recurring-jobs/cron-and-schedules.md) | CRON expressions, the `Cron` helpers, `CronBuilder`, plain-English schedules |
| [Pausing and updating](recurring-jobs/pausing-and-updating.md) | Pause, resume and change a schedule without redeploying |
| [Time zones and misfires](recurring-jobs/time-zones-and-misfires.md) | Daylight saving changes, downtime, missed runs |
| [Recurring job manifest](recurring-jobs/recurring-job-manifest.md) | Export and import recurring jobs as JSON |

### [Filters](filters/README.md)

| Page | What it covers |
|---|---|
| [Retries](filters/retries.md) | Automatic retries, retry-later, in-process retries, circuit breakers |
| [Concurrency](filters/concurrency.md) | One at a time, throttling, rate limits, duplicate prevention |
| [Timing](filters/timing.md) | Timeouts, deadlines, run windows and blackouts, how long data is kept |
| [Behavior](filters/behavior.md) | Queues, display names, culture, invocation logging, hiding secrets |
| [Custom filters](filters/custom-filters.md) | Writing your own filters, interceptors and state handlers |

### [Server](server/README.md)

| Page | What it covers |
|---|---|
| [Server options](server/server-options.md) | Every `BackgroundJobServerOptions` property |
| [Queues and workers](server/queues-and-workers.md) | Worker counts, queues, several servers, graceful shutdown |
| [Drain mode](server/drain-mode.md) | Stop starting new jobs, for the whole system, a queue or a server |
| [Keep-alive](server/keep-alive.md) | Keep IIS-hosted servers from being shut down when idle |
| [Dependency injection](server/dependency-injection.md) | What `AddNightShift` and `AddNightShiftServer` register; job activation and scopes |

### [Storage](storage/README.md)

| Page | What it covers |
|---|---|
| [SQL Server storage](storage/sql-server.md) | Setup, connection strings, SQL client packages, permissions |
| [SQL Server options](storage/sql-server-options.md) | Every `SqlServerStorageOptions` property and when to change it |
| [Schema and upgrades](storage/schema-and-upgrades.md) | Tables, schema versions, automatic and manual install, purging |
| [Custom storage](storage/custom-storage.md) | Writing a storage for another database |

### [Dashboard](dashboard/README.md)

| Page | What it covers |
|---|---|
| [Setup](dashboard/setup.md) | Adding the dashboard to your app; every `DashboardOptions` property |
| [Authorization](dashboard/authorization.md) | Who may see the dashboard, read-only access, a production checklist |
| [Pages](dashboard/pages.md) | Every page and what you can do on it |
| [Customization](dashboard/customization.md) | Your own pages, menu items, metrics, styles and languages |

### [Monitoring](monitoring/README.md)

| Page | What it covers |
|---|---|
| [OpenTelemetry](monitoring/opentelemetry.md) | Traces and metrics through `UseTelemetry()` |
| [Alerts and webhooks](monitoring/alerts-and-webhooks.md) | Alerts for failures and missed deadlines, sent to Slack, Teams or any URL |
| [Audit log and activity feed](monitoring/audit-log-and-activity.md) | Who did what in the dashboard; what happened recently |
| [Insights](monitoring/insights.md) | Per-method and per-queue statistics, history, the Prometheus endpoint |
| [Health checks and probes](monitoring/health-checks.md) | ASP.NET Core health checks, `/health` and `/metrics` for probes and scrapers |
| [Logging](monitoring/logging.md) | How the library logs, and how to control it |

### [Configuration](configuration/README.md)

| Page | What it covers |
|---|---|
| [Global configuration](configuration/global-configuration.md) | Every `Use*` and `Set*` configuration method |
| [Serialization](configuration/serialization.md) | How arguments and state are stored as JSON with System.Text.Json |
| [Compatibility levels](configuration/compatibility-levels.md) | Storage formats, and upgrading servers one at a time |

### Help and reference

| Page | What it covers |
|---|---|
| [Troubleshooting](troubleshooting.md) | Common problems, their causes and fixes |
| [Migrating from Hangfire](migrating-from-hangfire.md) | Package and API mapping, what changed, a step-by-step checklist |
| [What's new](whats-new-next.md) | Features added on top of Hangfire, with examples |
| [Breaking changes](breaking-changes.md) | Everything that behaves differently, and why |
| [Roadmap](roadmap.md) | Planned work that isn't in 1.0 yet |

### Working on the library

| Page | What it covers |
|---|---|
| [Contributing](contributing/README.md) | Building, testing, conventions, releasing |
| [Architecture](contributing/architecture.md) | How the source is organized and how a job flows through the code |
| [Architecture blueprint](architecture/blueprints/nightshift-next.md) | The design behind the features added on top of Hangfire |
| [Dashboard Razor plan](architecture/dashboard-razor-plan.md) | How the dashboard pages are compiled |
