<p align="center"><img src="https://raw.githubusercontent.com/a7mdfre7at/Masterly.NightShift/main/assets/readme-banner.png" alt="Masterly.NightShift" width="100%"></p>

# Masterly.NightShift

Reliable background jobs for .NET: fire-and-forget, delayed, recurring and continuation jobs, persisted to a storage you already run, with a built-in dashboard. No Windows Service or Task Scheduler needed.

[![Build](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml) [![CodeQL Analysis](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql-analysis.yml/badge.svg?branch=main)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql-analysis.yml) [![Nuget](https://img.shields.io/nuget/v/Masterly.NightShift?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift) ![GitHub](https://img.shields.io/github/license/a7mdfre7at/Masterly.NightShift?style=flat-square)

**Documentation:** [docs/README.md](docs/README.md), with a [quick start for ASP.NET Core](docs/getting-started/quick-start-aspnetcore.md) and one for [console apps and worker services](docs/getting-started/quick-start-worker.md).

## Give a Star! :star:

If you like or are using this project please give it a star. Thanks!

## Installation

Install the [Masterly.NightShift NuGet package](https://www.nuget.org/packages/Masterly.NightShift). It brings in the core library, SQL Server storage and the ASP.NET Core integration. The SQL Server storage also needs a SQL client package, which you choose; on .NET 8 and later use `Microsoft.Data.SqlClient`:

```
dotnet add package Masterly.NightShift
dotnet add package Microsoft.Data.SqlClient
```

See [Installation](docs/getting-started/installation.md) for which package to pick, for .NET Framework, and for the NU1902/NU1903 warnings `Microsoft.Data.SqlClient` brings with it.

| Package | What it is |
|---|---|
| `Masterly.NightShift` | Meta-package: Core + SqlServer + AspNetCore |
| `Masterly.NightShift.Core` | Jobs, the server, filters, the dashboard |
| `Masterly.NightShift.SqlServer` | SQL Server storage |
| `Masterly.NightShift.NetCore` | `Microsoft.Extensions.*` integration (DI, hosted service) |
| `Masterly.NightShift.AspNetCore` | Dashboard middleware and endpoint mapping |

Targets: `netstandard2.0` (.NET Framework 4.6.2+), `net8.0` and `net10.0`.

## Quick start

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString));
builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapNightShiftDashboard();   // served at /nightshift

app.Run();
```

Create jobs from anywhere:

```csharp
// Fire-and-forget: runs as soon as a worker is free
BackgroundJob.Enqueue(() => Console.WriteLine("Hello from a background job"));

// Delayed
BackgroundJob.Schedule(() => SendReminder(42), TimeSpan.FromHours(1));

// Continuation: runs after the parent succeeds. Creating a job returns null when a filter cancels it.
string? parent = BackgroundJob.Enqueue(() => BuildReport(7));
if (parent != null)
{
    BackgroundJob.ContinueJobWith(parent, () => EmailReport(7));
}

// Recurring
RecurringJob.AddOrUpdate("nightly-cleanup", () => Cleanup(), Cron.Daily());
```

Job arguments are serialized to the storage, so the job runs even if the process that created it has gone away. A job that throws is retried automatically.

## What you get

- **Job types:** fire-and-forget, delayed, recurring, continuations (including fan-in), batches.
- **Reliable execution:** automatic retries, deadlines and timeouts, rate limits, throttling, circuit breakers, duplicate prevention, run windows and blackouts, retry-later.
- **Fast invocation:** a source generator emits direct calls, so job methods are not invoked through reflection.
- **Dashboard:** live monitoring, search, insights, a job diagnosis ("why isn't it running?"), lineage, failure clusters, a wallboard, a timeline, an activity heatmap, exports and a command palette.
- **Operations:** drain mode, per-queue and per-server pause, health and Prometheus endpoints, alerts and webhooks, an audit log, a keep-alive for IIS-hosted servers.
- **Observability:** OpenTelemetry-compatible traces and metrics through `UseTelemetry()`.

## Documentation

| Section | What's in it |
|---|---|
| [Getting started](docs/getting-started/README.md) | Installation, quick starts, the core concepts |
| [Jobs](docs/jobs/README.md) | Fire-and-forget, delayed, continuations, fan-in and batches, arguments, cancellation |
| [Recurring jobs](docs/recurring-jobs/README.md) | CRON and plain-English schedules, pausing, time zones, misfires |
| [Filters](docs/filters/README.md) | Retries, concurrency limits, timeouts and run windows, writing your own |
| [Server](docs/server/README.md) | Server options, queues and workers, drain mode, keep-alive, dependency injection |
| [Storage](docs/storage/README.md) | SQL Server setup and options, schema upgrades, writing a custom storage |
| [Dashboard](docs/dashboard/README.md) | Setup, authorization, every page, customization |
| [Monitoring](docs/monitoring/README.md) | OpenTelemetry, alerts and webhooks, audit log, insights, logging |
| [Configuration](docs/configuration/README.md) | Global configuration, JSON serialization, compatibility levels |

Moving from Hangfire? Start with [Migrating from Hangfire](docs/migrating-from-hangfire.md). Something not working? See [Troubleshooting](docs/troubleshooting.md).

## Building from source

You need the .NET 10 SDK (see `global.json`).

```
dotnet build Masterly.NightShift.slnx -c Release
dotnet test tests/Masterly.NightShift.Core.Tests -f net10.0
dotnet pack src/Masterly.NightShift.Core -c Release --no-build -o artifacts
```

Pack the other packages the same way: `src/Masterly.NightShift.SqlServer`, `src/Masterly.NightShift.NetCore`, `src/Masterly.NightShift.AspNetCore` and the meta-package `src/Masterly.NightShift`.

The SQL Server tests need a reachable SQL Server: set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` placeholder for the database name, for example `Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;`.

## Contributing

Bug reports and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). To report a security problem privately, see [SECURITY.md](SECURITY.md).

## License

The code written for Masterly.NightShift is under the [MIT license](LICENSE.txt). Parts of the source are derived from Hangfire and stay under the GNU LGPL v3 or later ([LICENSE-LGPL-3.0.txt](LICENSE-LGPL-3.0.txt), with the [GPL v3](LICENSE-GPL-3.0.txt) it builds on), so the packages are licensed `MIT AND LGPL-3.0-or-later`. See [NOTICE.md](NOTICE.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
