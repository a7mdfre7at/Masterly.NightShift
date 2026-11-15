<div align="center">

<img src="assets/readme-banner.png" alt="Masterly.NightShift: reliable background jobs for .NET" width="100%" />

# Masterly.NightShift

### Background jobs that work the night shift.

Fire-and-forget, delayed, recurring and continuation jobs, plus whole workflows, for **.NET 8 and later**.<br/>
Stored in the database you already run, retried when they fail, and shown live in a built-in dashboard.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift?style=flat-square&logo=nuget&label=nuget)](https://www.nuget.org/packages/Masterly.NightShift)
[![Downloads](https://img.shields.io/nuget/dt/Masterly.NightShift?style=flat-square&label=downloads)](https://www.nuget.org/packages/Masterly.NightShift)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010-512bd4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/a7mdfre7at/Masterly.NightShift?style=flat-square)](#license)
[![Docs](https://img.shields.io/badge/docs-github.io-145a6b?style=flat-square)](https://a7mdfre7at.github.io/Masterly.NightShift/)
<br/>
[![Build](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml)
[![CodeQL](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql.yml)
[![Publish](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/publish.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/publish.yml)
[![Last commit](https://img.shields.io/github/last-commit/a7mdfre7at/Masterly.NightShift?style=flat-square)](https://github.com/a7mdfre7at/Masterly.NightShift/commits/master)

**[📖 Documentation](https://a7mdfre7at.github.io/Masterly.NightShift/)** &nbsp;·&nbsp;
**[🚀 Quick start](#-quick-start)** &nbsp;·&nbsp;
**[✨ Features](#-what-it-can-do)** &nbsp;·&nbsp;
**[🔁 From Hangfire](#-nightshift-and-hangfire)** &nbsp;·&nbsp;
**[📊 Benchmarks](#-benchmarks)**

</div>

<br/>

```csharp
jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));                               // now, in the background
jobs.Schedule<ReminderService>(x => x.SendTrialEnding(accountId), "tomorrow at 9am"); // later, in plain English
recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6)); // every day
jobs.ContinueJobWith<EmailService>(chargeId, x => x.SendReceipt(orderId));            // after another job
```

---

## 🌙 Why NightShift

Your web request should answer in milliseconds. The PDF report, the welcome e-mail, the nightly clean-up and the call to that slow partner API shouldn't hold it up.

Hand them to NightShift. It stores each job, runs it on a background worker, retries it when it throws, and keeps it safe through restarts, deploys and crashed servers. You don't need a Windows Service, a cron box or a message broker.

<table>
<tr>
<td width="33%" valign="top">

#### 🛡️ Durable
Jobs live in your database, so they survive restarts and deploys. When a server dies, another one picks up its jobs.

</td>
<td width="33%" valign="top">

#### ♻️ Self-healing
Automatic retries with growing, jittered delays, plus retry-later, in-process retries and circuit breakers for a dependency that is down.

</td>
<td width="33%" valign="top">

#### 🎛️ Under control
Throttles, rate limits, one-at-a-time locks, duplicate prevention, timeouts, deadlines, run windows and blackouts, across every server.

</td>
</tr>
<tr>
<td valign="top">

#### 🗣️ Readable
Times and schedules in plain English: `"tomorrow at 9am"`, `"every 10 minutes from 9am to 5pm on weekdays"`.

</td>
<td valign="top">

#### 🔭 Observable
A live dashboard with insights, failure clusters, lineage and a "why isn't it running?" diagnosis, plus OpenTelemetry, alerts, health checks and Prometheus.

</td>
<td valign="top">

#### ⚡ Lean
A source generator writes direct calls to your job methods instead of reflection, and System.Text.Json allocates over 80% less memory per job.

</td>
</tr>
</table>

> [!TIP]
> **Coming from Hangfire?** NightShift started from the source of Hangfire 1.8. The API will feel at home, and your existing Hangfire SQL Server data keeps working. Start with [Migrating from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire).

---

## 📦 Installation

For an ASP.NET Core app, one package is enough:

```bash
dotnet add package Masterly.NightShift
```

It installs Core, SQL Server storage and the ASP.NET Core dashboard. Worker Services and console apps take only the packages they use.

<details>
<summary><b>All nine packages</b> (they ship together, with the same version)</summary>
<br/>

| Package | What it is for | Extra dependencies |
|---------|----------------|--------------------|
| `Masterly.NightShift` | Meta-package for ASP.NET Core apps: Core, SqlServer and AspNetCore | The three packages it installs |
| `Masterly.NightShift.Core` | Jobs, the server, filters and the dashboard | `Cronos` |
| `Masterly.NightShift.SqlServer` | SQL Server storage | `Dapper`, `Microsoft.Data.SqlClient` |
| `Masterly.NightShift.EntityFrameworkCore` | Storage on any EF Core relational provider; jobs that commit with your `SaveChanges` | `Microsoft.EntityFrameworkCore.Relational` (8.x on `net8.0`, 10.x on `net10.0`) |
| `Masterly.NightShift.Redis` | Redis storage | `StackExchange.Redis` |
| `Masterly.NightShift.MongoDB` | MongoDB storage (replica set) | `MongoDB.Driver` |
| `Masterly.NightShift.InMemory` | In-memory storage for development and tests | None |
| `Masterly.NightShift.NetCore` | `Microsoft.Extensions.*` integration: dependency injection, logging, hosted services | `Microsoft.Extensions.*` abstractions |
| `Masterly.NightShift.AspNetCore` | Dashboard middleware, endpoint mapping and health checks | ASP.NET Core |

Every package targets `net8.0` and `net10.0`, so it runs on .NET 8 and later. See [Installation](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/installation) for which packages your kind of app needs.

</details>

---

## 🚀 Quick start

Three steps take an ASP.NET Core app from nothing to jobs running in the dashboard.

### 1 · Add the storage, a server and the dashboard

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));   // configures the storage

builder.Services.AddNightShiftServer();          // starts the workers that run jobs

var app = builder.Build();

app.MapNightShiftDashboard();                    // served at /nightshift
```

### 2 · Create jobs

A job is a call to one of your methods. Its arguments are stored, so the job runs even if the process that created it has gone away.

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

### 3 · Open the dashboard

Run the app and browse to **`/nightshift`** to watch the jobs run.

> [!IMPORTANT]
> The dashboard answers local requests only until you set up [authorization](https://a7mdfre7at.github.io/Masterly.NightShift/docs/dashboard/authorization).

<details>
<summary><b>Worker Service or console app?</b></summary>
<br/>

The server runs in any long-lived .NET process. In a Worker Service, install `Masterly.NightShift.NetCore` and `Masterly.NightShift.SqlServer`:

```csharp
using Masterly.NightShift;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")!));

builder.Services.AddNightShiftServer(options => options.WorkerCount = 4);

builder.Build().Run();
```

See [Quick start: console and Worker Service](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-worker), including how to show the dashboard for a worker from a small web app.

</details>

<details>
<summary><b>Jobs that commit with your data (EF Core)</b></summary>
<br/>

With the Entity Framework Core storage, a job can be staged in your own context, so it is created only if your `SaveChanges` commits:

```csharp
db.Orders.Add(order);
db.NightShift().Enqueue<IEmailSender>(x => x.SendConfirmation(order.Id));
await db.SaveChangesAsync(); // the order and the job, or neither
```

See [Entity Framework Core storage](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/entity-framework-core).

</details>

<details>
<summary><b>A workflow of jobs (chains)</b></summary>
<br/>

A chain runs steps one after the other, in parallel branches, or after a group of branches. It is recorded, so you can follow its progress and run it again from the step that failed:

```csharp
JobChain
    .StartWith<OrderService>(x => x.Process(orderId))
    .Then<InvoiceService>(x => x.Render(orderId))
    .ThenMany(branches => branches
        .Branch<Mailer>(x => x.SendInvoice(orderId))
        .Branch<ErpClient>(x => x.Post(orderId)))
    .ThenAfterAll<Notifier>(x => x.OrderDone(orderId))
    .ThenAfterAnyFailed<Ops>(x => x.Alert(orderId))
    .Enqueue();
```

See [Chains](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/chains).

</details>

**Where next?** Read [Core concepts](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/core-concepts) for the mental model behind jobs, states, servers and queues.

---

## ✨ What it can do

| | Capability | Guide |
|---|---|---|
| 🧵 | Run a method now, later, on a CRON schedule, or after other jobs finish | [Jobs](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs) · [Recurring jobs](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs) |
| 🔗 | Build workflows: sequences, parallel branches, joins and recurring chains | [Chains](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/chains) |
| 📥 | Create thousands of jobs in a few database round trips with `EnqueueMany` | [Fire-and-forget](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/fire-and-forget#many-jobs-at-once) |
| 🧺 | Wait for a whole set of jobs, or create jobs as a group | [Fan-in and batches](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/fan-in-and-batches) |
| 🗓️ | Plain-English times and schedules, and business calendars shared by every server | [Delayed jobs](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/delayed-jobs) · [Schedule policies](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/schedule-policies) |
| ♻️ | Retry with back-off, retry later, or stop calling a dependency that is down | [Retries](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/retries) |
| 🚦 | Limit how many jobs run at once, throttle, rate-limit and prevent duplicates | [Concurrency](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/concurrency) |
| ⏱️ | Timeouts, deadlines, run windows and blackouts | [Timing](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/timing) |
| 📈 | Live progress and log lines, and resuming after a failure from a checkpoint | [Progress and console](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/progress-and-console) |
| 🏷️ | Create jobs by name from code that doesn't reference the method, or from a job class | [Job functions](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/job-functions) · [Job classes](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/typed-handlers) |
| 🖥️ | Watch, search and manage jobs, with insights, failure clusters, lineage and diagnosis | [Dashboard](https://a7mdfre7at.github.io/Masterly.NightShift/docs/dashboard) |
| 🚧 | Stop starting new jobs before a deploy, for the whole system, a queue or one server | [Drain mode](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/drain-mode) |
| 📡 | OpenTelemetry traces and metrics, alerts to Slack, Teams or a webhook, health and Prometheus endpoints | [Monitoring](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring) |
| 🧬 | Call job methods without reflection: a source generator emits direct calls | [Source-generated invokers](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/source-generated-invokers) |

### 🗄️ Storage

Pick the database you already run. Every storage passes the same conformance suite.

| SQL Server | Entity Framework Core | Redis | MongoDB | In memory |
|:---:|:---:|:---:|:---:|:---:|
| The reference storage | PostgreSQL, MySQL, SQLite and any relational provider | StackExchange.Redis | Replica set | Development and tests |
| [Guide](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/sql-server) | [Guide](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/entity-framework-core) | [Guide](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/redis) | [Guide](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/mongodb) | [Guide](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/in-memory) |

---

## 🔁 NightShift and Hangfire

NightShift is a remastered fork of [Hangfire](https://www.hangfire.io/) 1.8.25. It keeps Hangfire's programming model and reads your existing Hangfire SQL Server data. It was refactored for .NET 8 and later, and features that Hangfire leaves to paid add-ons or community packages are built in.

| | Hangfire 1.8.25 | NightShift 1.0 |
|---|---|---|
| **Platforms** | .NET Framework 4.5.1 and later, .NET Standard | .NET 8 and later only |
| **Calling your job method** | `MethodInfo.Invoke` | Source-generated direct call, with compiled and reflection fallbacks |
| **JSON** | Newtonsoft.Json | System.Text.Json |
| **Batches and fan-in** | Paid (Hangfire Pro) | ✅ Included |
| **Semaphores and rate limits** | Paid (Hangfire Ace) | ✅ Included: `[Throttle]`, `[RateLimit]` |
| **Circuit breaker, duplicate prevention** | Not included | ✅ Included: `[CircuitBreaker]`, `[PreventDuplicates]` |
| **Timeouts, deadlines, run windows** | `[LatencyTimeout]` only | ✅ Included: `[JobTimeout]`, `[Deadline]`, `[RunWindow]`, `[Blackout]` |
| **Progress, console, resume after failure** | Community packages | ✅ Included |
| **OpenTelemetry, Prometheus, health checks, alerts** | Community packages | ✅ Included |
| **Dashboard** | Jobs, queues, servers, retries, recurring jobs | The same, plus insights, failure clusters, lineage and job diagnosis |
| **Storage** | SQL Server, paid Redis, many community storages | SQL Server, Entity Framework Core (PostgreSQL, MySQL, SQLite...), Redis, MongoDB and in memory, all passing one conformance suite |
| **Support and track record** | Paid support available; more than a decade in production | Community support through GitHub; 1.0 is the first release |

**✅ Choose NightShift** for batches, throttling, circuit breakers and full monitoring without a subscription or extra packages, on a smaller dependency set for modern .NET, with your existing Hangfire SQL Server data still working.

**↩️ Stay on Hangfire** if you run on .NET Framework or .NET 7 and earlier, depend on third-party Hangfire extensions, or need a vendor with paid support.

NightShift is an independent project, not affiliated with or endorsed by Hangfire OÜ. See [NightShift compared with Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/compared-with-hangfire) for the full comparison.

---

## 📊 Benchmarks

Measured on .NET 10 against Hangfire 1.8.25. The SQL Server rows used SQL Server 2022 in a container, where a round trip takes about 0.7 ms.

| Operation | Hangfire | NightShift |
|---|---|---|
| Call the job method | 17.0 ns | 3.5 ns |
| Serialize a job | 2.38 µs, 10.97 KB | 1.59 µs, 1.84 KB |
| Deserialize a job | 3.34 µs, 17.32 KB | 3.04 µs, 2.08 KB |
| Run an empty job through all filters | 0.58 µs, 1.62 KB | 0.27 µs, 0.63 KB |
| `Enqueue` in a loop, SQL Server | 174 jobs/s | 218 jobs/s |
| `Enqueue` from 64 callers at once | 1,523 jobs/s | 3,179 jobs/s |
| Create many jobs at once | No bulk API | 4,116 jobs/s with `EnqueueMany` |
| Process from SQL Server | 562 to 648 jobs/s | 606 to 794 jobs/s; 974 to 1,000 with `AllowDelayedDurability` |

NightShift needs half the round trips to create a job, shares them between concurrent callers, and creates many jobs at once with `EnqueueMany`. A loop of single `Enqueue` calls gains the least, because each call waits for the database to store its job. Running a job allocates about 60% less memory, and serializing one over 80% less.

[Benchmarks](https://a7mdfre7at.github.io/Masterly.NightShift/docs/benchmarks) has the .NET 8 numbers, what they mean, and how to run them yourself.

---

## 📚 Documentation

The full guide lives at **[a7mdfre7at.github.io/Masterly.NightShift](https://a7mdfre7at.github.io/Masterly.NightShift/)**, and the same pages are in [docs/](docs/README.md).

| | Topic | Start with |
|---|---|---|
| 🚀 | **Getting started** | [Installation](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/installation) · [ASP.NET Core](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-aspnetcore) · [Worker Service](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-worker) · [Core concepts](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/core-concepts) |
| 🧵 | **Jobs** | [Fire-and-forget](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/fire-and-forget) · [Delayed](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/delayed-jobs) · [Continuations](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/continuations) · [Chains](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/chains) · [Fan-in and batches](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/fan-in-and-batches) · [Arguments](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/job-arguments) · [Cancellation](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/cancellation) · [Testing](https://a7mdfre7at.github.io/Masterly.NightShift/docs/jobs/testing) |
| 🗓️ | **Recurring jobs** | [Creating](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/creating-recurring-jobs) · [CRON and schedules](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/cron-and-schedules) · [Pausing and updating](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/pausing-and-updating) · [Time zones and misfires](https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/time-zones-and-misfires) |
| 🎛️ | **Filters** | [Retries](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/retries) · [Concurrency](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/concurrency) · [Timing](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/timing) · [Behavior](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/behavior) · [Custom filters](https://a7mdfre7at.github.io/Masterly.NightShift/docs/filters/custom-filters) |
| ⚙️ | **Running** | [Server options](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/server-options) · [Queues and workers](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/queues-and-workers) · [Drain mode](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/drain-mode) · [Dependency injection](https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/dependency-injection) · [Storage](https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage) |
| 🔭 | **Operating** | [Dashboard](https://a7mdfre7at.github.io/Masterly.NightShift/docs/dashboard) · [OpenTelemetry](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring/opentelemetry) · [Alerts and webhooks](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring/alerts-and-webhooks) · [Health checks](https://a7mdfre7at.github.io/Masterly.NightShift/docs/monitoring/health-checks) · [Configuration](https://a7mdfre7at.github.io/Masterly.NightShift/docs/configuration) |
| 📘 | **Reference** | [Troubleshooting](https://a7mdfre7at.github.io/Masterly.NightShift/docs/troubleshooting) · [Compared with Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/compared-with-hangfire) · [Migrating from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire) · [Changes from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/changes-from-hangfire) · [Roadmap](https://a7mdfre7at.github.io/Masterly.NightShift/docs/roadmap) · [Changelog](CHANGELOG.md) |

---

## 🛠️ Building from source

You need the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet build Masterly.NightShift.slnx -c Release
dotnet test tests/Masterly.NightShift.Core.Tests -f net10.0
```

<details>
<summary><b>Test prerequisites and the documentation site</b></summary>
<br/>

The SQL Server tests need a reachable SQL Server. Set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` placeholder for the database name, for example:

```text
Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;
```

The Redis and MongoDB tests run when `NightShift_Redis_Configuration` and `NightShift_Mongo_ConnectionString` are set, and are skipped otherwise.

The documentation site lives in `website/` and needs Node.js 20 or later:

```bash
cd website
npm ci
npm start       # live preview
npm run build   # fails on any broken link
```

</details>

## 🤝 Contributing

Bug reports and pull requests are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md). To report a security problem privately, see [SECURITY.md](SECURITY.md).

If NightShift helps you, please ⭐ the repository: it helps others find it.

## License

The code written for Masterly.NightShift is under the [MIT license](LICENSE.txt). Parts of the source are derived from Hangfire and stay under the GNU LGPL v3 or later ([LICENSE-LGPL-3.0.txt](LICENSE-LGPL-3.0.txt), with the [GPL v3](LICENSE-GPL-3.0.txt) it builds on), so the packages are licensed `MIT AND LGPL-3.0-or-later`. See [NOTICE.md](NOTICE.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<div align="center">
<br/>
<sub>Built for the jobs that run while everyone else sleeps. 🌙</sub>
</div>
