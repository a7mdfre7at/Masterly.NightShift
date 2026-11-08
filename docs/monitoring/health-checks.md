# Health checks and probes

Load balancers, Kubernetes probes, uptime monitors and Prometheus need to ask "is background processing working?" without signing in to the dashboard. Masterly.NightShift answers in three ways:

- **ASP.NET Core health checks.** `AddNightShift()` adds a check to `AddHealthChecks()`. It answers in plain words, never with exception text, so it is safe on an anonymous endpoint.
- **The dashboard's `/health` and `/metrics` routes.** A detailed JSON report and Prometheus text, behind the dashboard's authorization.
- **`MapNightShiftHealth` and `MapNightShiftMetrics`.** The same report and text as endpoints of their own, outside the dashboard's authorization, which you limit with endpoint conventions.

The health check and the endpoints are in the `Masterly.NightShift.AspNetCore` package.

## Quick example

A web app that runs a server: Kubernetes restarts the pod when its server stops sending heartbeats, and takes it out of the load balancer when the storage doesn't answer or no server in the cluster is alive.

```csharp
using Masterly.NightShift;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")));

builder.Services.AddNightShiftServer();

builder.Services.AddHealthChecks()
    .AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process, name: "nightshift-server", tags: ["live"])
    .AddNightShift(name: "nightshift-cluster", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
```

`samples/WebSample` in the repository is a complete app with these probes, the dashboard behind an authorization policy, and antiforgery. Run it with `dotnet run --project samples/WebSample`: it starts in Development on `http://localhost:5000`, where `/health/live` and `/health/ready` answer and the Development-only `/login` signs you in for `/nightshift`. It uses the LocalDB database `Masterly.NightShift.WebSample` unless `ConnectionStrings:NightShift` is set, and creates the database on first run if it doesn't exist.

## How it works

### The health check

`AddNightShift()` registers a check named `nightshift`. Each run makes one storage query, the list of servers with their last heartbeat, and compares the heartbeats with `HeartbeatTimeout` (one minute by default; servers send one every 30 seconds).

| Scope | Healthy when | Unhealthy (or `failureStatus`) when |
|---|---|---|
| `Cluster` (default) | The storage answers and at least one server anywhere sent a heartbeat within `HeartbeatTimeout`. | The storage doesn't answer, no server is registered, or no server sent a heartbeat recently. |
| `Process` | Every server running in this process sent a heartbeat within `HeartbeatTimeout`. | No server runs in this process (not started yet, or stopped), or one of them stopped sending heartbeats (the watchdog of another server may have removed it). When the storage doesn't answer, the result is `Degraded`, not the failure status: restarting the process wouldn't bring the database back, so a liveness probe keeps the pod running during an outage. |

The result's `Data` has the numbers: `servers` and `alive` for the cluster, `servers` and `silent` for the process.

**How the process scope knows its servers.** With `Scope = Process`, `AddNightShift` also registers a small `IBackgroundProcess`. `AddNightShiftServer` starts the `IBackgroundProcess` services of the container in every server it creates, so this process learns each server's id when the server starts, and forgets it when the server stops. It holds one thread per server, which only waits.

**Errors stay in the log.** Exception messages can name the database server, the database or the login. The check's description only says "The NightShift storage did not answer." and the result carries no exception. The error itself is logged as a warning by the logger `Masterly.NightShift.HealthChecks.NightShiftHealthCheck`.

**Which storage.** The one registered by `AddNightShift` (the `JobStorage` service), or `JobStorage.Current` without one. Set `NightShiftHealthCheckOptions.Storage` to check another one.

### The dashboard's `/health`

`GET {dashboard path}/health` answers a JSON report: `200` with `"status": "healthy"` or `"degraded"`, `503` with `"status": "unhealthy"`. The checks:

| Check | Unhealthy | Degraded |
|---|---|---|
| `storage` | The storage didn't answer. The detail says so and points to the log, where the error is. | |
| `servers` | No server is registered, or none sent a heartbeat within the dashboard's `ServerPossiblyAbortedThreshold`. | Some servers missed their heartbeat. |
| `queues` | | A queue holds jobs that no live server listens to. |
| `processing` | | Processing, a queue or a server is paused from the dashboard. |
| `recurring` | | A recurring job is more than a minute late: its next run, or the retry the scheduler set after an error, was due more than a minute ago and nobody started it (the Recurring Jobs page marks the same jobs as late). Or a recurring job is disabled by errors (no next run, with an error). The detail lists up to five ids. When the recurring jobs can't be read, the detail says so and the error is logged. |

**Cost.** The report needs the statistics, the servers, the queues and the drain-mode hash. Late recurring jobs are found from the scores of the `recurring-jobs` set, which hold each job's next run: one query for the jobs due more than a minute ago, one for the jobs without a next run, and one hash field per job without a next run, at most 500 of each. A storage that can't query a set by score (it lacks `JobStorageFeatures.ExtendedApi` or `JobStorageFeatures.Connection.BatchedGetFirstByLowest`) falls back to reading every recurring job. A report is kept for 5 seconds per storage and `ServerPossiblyAbortedThreshold` (the only option it depends on), so probes that ask every second, several Prometheus servers, `/metrics` and the dashboard share one set of queries, even when `MapNightShiftHealth`, `MapNightShiftMetrics` and the dashboard are mapped with different `DashboardOptions` objects.

`/ping` answers `pong` without touching the storage: use it for a probe that only needs to know the web app is up.

### `MapNightShiftHealth` and `MapNightShiftMetrics`

Dashboard routes go through the dashboard's authorization filters, which a probe or a scraper usually can't pass. These two methods map the same `/health` report and `/metrics` text as endpoints of their own. They use the storage and `DashboardOptions` registered in the container unless you pass others, and the dashboard's authorization filters don't apply: add endpoint conventions to limit access.

```csharp
// Only requests to the internal host name reach them
app.MapNightShiftHealth("/health/nightshift").RequireHost("jobs.internal:8080");
app.MapNightShiftMetrics("/metrics/nightshift").RequireHost("jobs.internal:8080");
```

They need .NET 8 or later (`IEndpointRouteBuilder`). The report names queues, servers and late recurring job ids, and the metrics name job methods; when that matters, prefer the health check, which only says whether things work.

## Options / API

### `NightShiftHealthChecksBuilderExtensions`

| Member | Meaning |
|---|---|
| `AddNightShift(this IHealthChecksBuilder builder, Action<NightShiftHealthCheckOptions>? configure = null, string? name = null, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)` | Adds the check. `name` defaults to `"nightshift"`; `failureStatus` to `Unhealthy`. Add it more than once, with different names, for both scopes. Throws `ArgumentException` when `HeartbeatTimeout` isn't positive. |
| `DefaultName` | `"nightshift"`. |

### `NightShiftHealthCheckOptions`

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Scope` | `NightShiftHealthCheckScope` | `Cluster` | `Cluster` or `Process`, see above. |
| `HeartbeatTimeout` | `TimeSpan` | 1 minute | How old a server's last heartbeat may be for the server to count as alive. Keep it above the servers' `HeartbeatInterval` (30 seconds by default). |
| `Storage` | `JobStorage?` | `null` | The storage to check; `null` uses the registered one, or `JobStorage.Current`. |

### `NightShiftMonitoringEndpointRouteBuilderExtensions` (.NET 8 and later)

| Member | Meaning |
|---|---|
| `MapNightShiftHealth(this IEndpointRouteBuilder endpoints, string pattern, DashboardOptions? options = null, JobStorage? storage = null)` | Maps `GET {pattern}` to the dashboard's health report. |
| `MapNightShiftMetrics(this IEndpointRouteBuilder endpoints, string pattern, DashboardOptions? options = null, JobStorage? storage = null)` | Maps `GET {pattern}` to the dashboard's Prometheus text. See [Insights](insights.md#prometheus-endpoint) for the series. |

Both throw `InvalidOperationException` when `AddNightShift` wasn't called.

## More examples

### A worker service without a web server

A Worker Service has no HTTP endpoint for Kubernetes to call. Publish the health report another way, for example with an `IHealthCheckPublisher` that writes a file a probe can check, or add a small Kestrel endpoint. The check itself works the same: register it with `AddHealthChecks().AddNightShift(...)` in the worker's services.

### A degraded readiness instead of an unhealthy one

When jobs are only part of what the app does, report trouble with them as degraded, so the load balancer keeps sending requests:

```csharp
builder.Services.AddHealthChecks()
    .AddNightShift(failureStatus: HealthStatus.Degraded, tags: ["ready"]);
```

`MapHealthChecks` answers `200` for degraded by default.

## Things to know

- **The process scope needs servers started by `AddNightShiftServer`.** A `BackgroundJobServer` you create yourself doesn't run the container's `IBackgroundProcess` services, and neither does `AddNightShiftServer` when you pass it your own list of additional processes. The check then reports that no server runs in this process.
- **Before the server starts, the process check is unhealthy.** Give the liveness probe an initial delay longer than the server's start, or use a startup probe.
- **A stopped server is not alive.** During a graceful shutdown the server stops, so the process check turns unhealthy; that is what a liveness probe should see.
- **The checks run synchronously.** Each run makes one storage query (the server list). Probes every few seconds cost little; the dashboard's `/health` report is heavier and is cached instead.

## See also

- [Monitoring overview](README.md)
- [Insights](insights.md): the `/metrics` series
- [Dashboard authorization](../dashboard/authorization.md): `/health` and `/metrics` behind the dashboard's filters
- [Keep-alive](../server/keep-alive.md)
- [Server options](../server/server-options.md): `HeartbeatInterval`, `ServerTimeout`
