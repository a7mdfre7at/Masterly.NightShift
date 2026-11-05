<p align="center"><img src="https://raw.githubusercontent.com/a7mdfre7at/Masterly.NightShift/main/assets/readme-banner.png" alt="Masterly.NightShift" width="100%"></p>

# Masterly.NightShift

Reliable background jobs for .NET: fire-and-forget, delayed, recurring and continuation jobs, persisted to a storage you already run, with a built-in dashboard. No Windows Service or Task Scheduler needed.

[![Build](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/build.yml) [![CodeQL Analysis](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql-analysis.yml/badge.svg?branch=main)](https://github.com/a7mdfre7at/Masterly.NightShift/actions/workflows/codeql-analysis.yml) [![Nuget](https://img.shields.io/nuget/v/Masterly.NightShift?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift) ![GitHub](https://img.shields.io/github/license/a7mdfre7at/Masterly.NightShift?style=flat-square)

**Documentation:** <https://a7mdfre7at.github.io/Masterly.NightShift/>

## Give a Star! :star:

If you like or are using this project please give it a star. Thanks!

## Installation

Install the [Masterly.NightShift NuGet package](https://www.nuget.org/packages/Masterly.NightShift). It brings in the core library, SQL Server storage and the ASP.NET Core integration.

```
dotnet add package Masterly.NightShift
```

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

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")));
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

// Continuation: runs after the parent succeeds
string parent = BackgroundJob.Enqueue(() => BuildReport(7));
BackgroundJob.ContinueJobWith(parent, () => EmailReport(7));

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

The full list, with examples, is in [docs/whats-new-next.md](docs/whats-new-next.md) and on the [documentation site](https://a7mdfre7at.github.io/Masterly.NightShift/).

## Building from source

You need the .NET 10 SDK (see `global.json`).

```
dotnet build Masterly.NightShift.slnx -c Release
dotnet test tests/Masterly.NightShift.Core.Tests -f net10.0
dotnet pack Masterly.NightShift.slnx -c Release
```

The SQL Server tests need a reachable SQL Server: set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` placeholder for the database name, for example `Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;`.

## Contributing

Bug reports and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). To report a security problem privately, see [SECURITY.md](SECURITY.md).

## License

Masterly.NightShift is released under the [MIT license](LICENSE.txt). Parts of the source derive from earlier work under other licenses; see [NOTICE.md](NOTICE.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
