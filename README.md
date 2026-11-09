# Masterly.NightShift

**Background jobs that work the night shift.**

Fire-and-forget, delayed, recurring and continuation jobs for .NET 8 and later. They are stored in the SQL Server you already run, retried when they fail, and shown live in a built-in dashboard.

![Masterly.NightShift: reliable background jobs for .NET](assets/readme-banner.png)

[![Nuget](https://img.shields.io/nuget/v/Masterly.NightShift?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift) ![Nuget](https://img.shields.io/nuget/dt/Masterly.NightShift?style=flat-square) ![GitHub last commit](https://img.shields.io/github/last-commit/a7mdfre7at/Masterly.NightShift?style=flat-square) ![GitHub](https://img.shields.io/github/license/a7mdfre7at/Masterly.NightShift?style=flat-square) [![Build](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml) [![CodeQL Analysis](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql-analysis.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql-analysis.yml) [![Publish to NuGet](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/publish.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/publish.yml) [![Docs](https://img.shields.io/badge/docs-github.io-145a6b?style=flat-square)](https://a7mdfre7at.github.io/Masterly.NightShift/)

**Full documentation: [a7mdfre7at.github.io/Masterly.NightShift](https://a7mdfre7at.github.io/Masterly.NightShift/)**

## Why NightShift

Your web request should answer in milliseconds. The PDF report, the welcome e-mail, the nightly clean-up and the call to that slow partner API shouldn't hold it up. Hand them to NightShift: it stores each job, runs it on a background worker, retries it when it throws, and keeps it safe through restarts, deploys and crashed servers. You don't need a Windows Service, a cron box or a message broker.

| | |
|---|---|
| **Durable** | Jobs live in your database, so they survive restarts and deploys. When a server dies, another one picks up its jobs. |
| **Self-healing** | Automatic retries with growing, jittered delays, plus retry-later, in-process retries and circuit breakers for a dependency that is down. |
| **Under control** | Throttles, rate limits, one-at-a-time locks, duplicate prevention, timeouts, deadlines, run windows and blackouts, across every server. |
| **Readable** | Times and schedules in plain English: `"tomorrow at 9am"`, `"every 10 minutes from 9am to 5pm on weekdays"`. |
| **Observable** | A live dashboard with insights, failure clusters, lineage and a "why isn't it running?" diagnosis, plus OpenTelemetry, alerts, health checks and Prometheus metrics. |
| **Fast** | A source generator writes direct calls to your job methods, so they aren't invoked through reflection. |
| **Familiar** | It started from the source of Hangfire 1.8. The API will feel at home, and your existing Hangfire SQL Server data keeps working. See [Migrating from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire). |

```csharp
jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));                       // now, in the background
jobs.Schedule<ReminderService>(x => x.SendTrialEnding(accountId), "tomorrow at 9am"); // later
recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6)); // every day
jobs.ContinueJobWith<EmailService>(chargeId, x => x.SendReceipt(orderId));    // after another job
```

**Everything it can do**

- Run a method in the background now, later, on a CRON schedule, or after other jobs finish. See [Jobs](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs) and [Recurring jobs](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs).
- Wait for a whole set of jobs, or create jobs as a group. See [Fan-in and batches](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/fan-in-and-batches).
- Write times and schedules in plain English, such as `"tomorrow at 9am"` or `"every 10 minutes from 9am to 5pm on weekdays"`. See [Delayed jobs](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/delayed-jobs) and [CRON and schedules](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/cron-and-schedules).
- Retry with back-off, retry later, or stop calling a dependency that is down with a circuit breaker. See [Retries](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/retries).
- Limit how many jobs run at once, throttle, rate-limit and prevent duplicates, across every server. See [Concurrency](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/concurrency).
- Set timeouts, deadlines, run windows and blackouts. See [Timing](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/timing).
- Show a job's progress and log lines live, and resume after a failure from a checkpoint. See [Progress, console and checkpoints](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/progress-and-console).
- Watch, search and manage jobs in the dashboard, with insights, failure clusters, lineage and a "why isn't it running?" diagnosis. See [Dashboard](https://a7mdfre7at.github.io/Masterly.NightShift/docs/dashboard).
- Stop starting new jobs before a deploy, for the whole system, a queue or one server. See [Drain mode](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/drain-mode).
- Trace and measure every job with OpenTelemetry, send alerts to Slack, Teams or a webhook, and expose health and Prometheus endpoints. See [Monitoring](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring).
- Call job methods without reflection: a source generator emits direct calls. See [Source-generated invokers](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/source-generated-invokers).

---

## Installation

ASP.NET Core apps install the meta-package. Worker Services and console apps take only the packages they use. All five packages ship together with the same version number.

| Package | What it is for | Extra dependencies |
|---------|----------------|--------------------|
| `Masterly.NightShift` | Meta-package for ASP.NET Core apps: Core, SqlServer and AspNetCore | The three packages it installs |
| `Masterly.NightShift.Core` | Jobs, the server, filters and the dashboard | `Cronos` |
| `Masterly.NightShift.SqlServer` | SQL Server storage | `Dapper`, `Microsoft.Data.SqlClient` |
| `Masterly.NightShift.NetCore` | `Microsoft.Extensions.*` integration: dependency injection, logging, hosted services | `Microsoft.Extensions.*` abstractions |
| `Masterly.NightShift.AspNetCore` | Dashboard middleware, endpoint mapping and health checks | ASP.NET Core |

Every package targets `net8.0` and `net10.0`, so it runs on .NET 8 and later.

```bash
dotnet add package Masterly.NightShift
```

See [Installation](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/installation) for which packages to pick for your kind of app.

---

## Quick Start

This example adds background jobs to an ASP.NET Core app that stores them in SQL Server and serves the dashboard.

**1. Add the storage, a server and the dashboard.** `AddNightShift` configures the storage, `AddNightShiftServer` starts the workers that run jobs, and `MapNightShiftDashboard` serves the dashboard at `/nightshift`.

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));

builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapNightShiftDashboard();   // served at /nightshift
```

**2. Create jobs.** A job is a call to one of your methods. Its arguments are stored, so the job runs even if the process that created it has gone away.

```csharp
// Fire-and-forget: runs as soon as a worker is free
app.MapPost("/users/{id:int}/welcome", (int id, IBackgroundJobClient jobs) =>
{
    jobs.Enqueue<EmailService>(email => email.SendWelcome(id));
    return Results.Accepted();
});

// Delayed and recurring jobs
var jobs = app.Services.GetRequiredService<IBackgroundJobClient>();
jobs.Schedule<ReminderService>(x => x.SendTrialEnding(42), "tomorrow at 9am");

var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6));

app.Run();
```

**3. Open the dashboard.** Run the app and browse to `/nightshift` to watch the jobs run. The dashboard is open to local requests only until you set up [authorization](https://a7mdfre7at.github.io/Masterly.NightShift/docs/dashboard/authorization).

**Next steps**

- For Worker Services and console apps, see [Quick start: console and Worker Service](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-worker).
- For the mental model behind jobs, states, servers and queues, see [Core concepts](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/core-concepts).

---

## Documentation

| Topic | Guide |
|-------|-------|
| Getting started | [Installation](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/installation), [Quick start: ASP.NET Core](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-aspnetcore), [Quick start: Worker Service](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-worker), [Core concepts](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/core-concepts) |
| Jobs | [Fire-and-forget](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/fire-and-forget), [Delayed](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/delayed-jobs), [Continuations](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/continuations), [Fan-in and batches](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/fan-in-and-batches), [Arguments](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/job-arguments), [Cancellation](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/cancellation), [Testing](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/testing) |
| Recurring jobs | [Creating](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/creating-recurring-jobs), [CRON and schedules](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/cron-and-schedules), [Pausing and updating](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/pausing-and-updating), [Time zones and misfires](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/time-zones-and-misfires) |
| Filters | [Retries](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/retries), [Concurrency](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/concurrency), [Timing](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/timing), [Behavior](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/behavior), [Custom filters](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/custom-filters) |
| Running | [Server options](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/server-options), [Queues and workers](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/queues-and-workers), [Drain mode](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/drain-mode), [Dependency injection](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/dependency-injection), [SQL Server storage](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/sql-server) |
| Operating | [Dashboard](https://a7mdfre7at.github.io/Masterly.NightShift/docs/dashboard), [OpenTelemetry](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring/opentelemetry), [Alerts and webhooks](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring/alerts-and-webhooks), [Health checks](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring/health-checks), [Configuration](https://a7mdfre7at.github.io/Masterly.NightShift/docs/configuration) |
| Reference | [Troubleshooting](https://a7mdfre7at.github.io/Masterly.NightShift/docs/troubleshooting), [Migrating from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire), [Breaking changes](https://a7mdfre7at.github.io/Masterly.NightShift/docs/breaking-changes), [Roadmap](https://a7mdfre7at.github.io/Masterly.NightShift/docs/roadmap), [Changelog](https://github.com/a7mdfre7at/Masterly.NightShift/blob/master/CHANGELOG.md) |

The same pages are in [docs/](docs/README.md) in this repository.

If this library helps you, please give it a star on GitHub.

---

## Building from source

You need the .NET 10 SDK (see `global.json`).

```bash
dotnet build Masterly.NightShift.slnx -c Release
dotnet test tests/Masterly.NightShift.Core.Tests -f net10.0
```

The SQL Server tests need a reachable SQL Server: set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` placeholder for the database name, for example `Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;`.

The documentation site lives in `website/` and needs Node.js 20 or later:

```bash
cd website
npm ci
npm start       # live preview
npm run build   # fails on any broken link
```

Bug reports and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). To report a security problem privately, see [SECURITY.md](SECURITY.md).

---

## License

The code written for Masterly.NightShift is under the [MIT license](LICENSE.txt). Parts of the source are derived from Hangfire and stay under the GNU LGPL v3 or later ([LICENSE-LGPL-3.0.txt](LICENSE-LGPL-3.0.txt), with the [GPL v3](LICENSE-GPL-3.0.txt) it builds on), so the packages are licensed `MIT AND LGPL-3.0-or-later`. See [NOTICE.md](NOTICE.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
