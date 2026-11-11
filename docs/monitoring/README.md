# Monitoring

Masterly.NightShift gives you several ways to watch your background jobs beyond the dashboard's job lists: traces and metrics for your observability stack through OpenTelemetry, alerts posted to a webhook or your own sink when something goes wrong, an audit log of dashboard actions and a live feed of every state change, per-method and per-queue statistics behind the Insights page and the Prometheus `/metrics` endpoint, and diagnostic logging through `Microsoft.Extensions.Logging` or another provider. Health checks for ASP.NET Core and the `/health` and `/metrics` endpoints tell probes and scrapers how the cluster is doing.

Some features are on by default; the others take one call each. Most must be turned on in every process where the work happens:

| Feature | On by default | Turn on with | Call it in |
|---|---|---|---|
| Logging | yes, with `AddNightShift` | `Use...LogProvider()` without `AddNightShift` | every process |
| Job id on log entries (logging scope) | yes, with `AddNightShiftServer` | nothing to do | |
| Audit log | yes | nothing to do | |
| Daily and hourly job counts (home page graphs) | yes | nothing to do | |
| Traces and metrics | no | `UseTelemetry()` | every process that creates or performs jobs |
| Alerts | no | `UseWebhookAlerts(url)` or `UseAlerts(sink)` | every process that performs jobs |
| Exception handlers | no | `AddNightShiftJobExceptionHandler<T>()` or `UseJobExceptionHandler(handler)` | every process that performs jobs |
| Insights | no | `UseJobInsights()` | every process that performs jobs |
| Activity feed | no | `UseActivityFeed()` | every process that changes job states |
| Health check | no | `AddHealthChecks().AddNightShift()` | the process that answers probes |
| `/health`, `/metrics` outside the dashboard | no | `MapNightShiftHealth`, `MapNightShiftMetrics` | the web app |

| Page | What it covers |
|---|---|
| [OpenTelemetry](opentelemetry.md) | `UseTelemetry()`, the `Masterly.NightShift` activity source and meter, span names and attributes, instruments, trace propagation from the creating request to the job, SDK setup. |
| [Alerts and webhooks](alerts-and-webhooks.md) | Alerts for failed jobs, missed deadlines and opened circuits; `UseWebhookAlerts` with JSON, Slack, Discord or Teams payloads; writing an `IAlertSink`. |
| [Exception handlers](exception-handlers.md) | `IJobExceptionHandler`: every failed attempt and canceled run in one place, for an error tracker; `AddNightShiftJobExceptionHandler<T>()` with scoped services, `UseJobExceptionHandler`. |
| [Audit log and activity feed](audit-log-and-activity.md) | `AuditLog` (who did what on the dashboard) and `ActivityFeed` (every state transition, turned on with `UseActivityFeed()`), what they store, what they cost, and how to read, size or turn them off. |
| [Insights](insights.md) | `UseJobInsights()`: per-method run counts and durations, per-queue wait times and drain estimates, daily and hourly history, anomaly detection, the Prometheus `/metrics` endpoint. |
| [Health checks and probes](health-checks.md) | `AddNightShift()` for ASP.NET Core health checks (cluster or this process), `MapNightShiftHealth` and `MapNightShiftMetrics` outside the dashboard, what `/health` checks and costs. |
| [Logging](logging.md) | How Masterly.NightShift logs, categories and levels, other log providers, turning logging off, logging from jobs, the `JobId`/`JobName` logging scope (`ConfigureNightShiftJobLogging`), `[LogInvocation]`. |

## The most common setup

An ASP.NET Core app that exports traces and metrics, posts alerts to Slack, and records statistics for the Insights page. Logging to the app's `ILoggerFactory` needs no code.

```csharp
using Masterly.NightShift;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")!)
    .UseTelemetry()
    .UseJobInsights()
    .UseWebhookAlerts(builder.Configuration["Alerts:SlackWebhookUrl"]!, options =>
    {
        options.Format = WebhookFormat.Slack;
        options.Events = AlertKind.JobFailed | AlertKind.CircuitOpened;
    }));

builder.Services.AddNightShiftServer();

// Needs the OpenTelemetry.Extensions.Hosting and OpenTelemetry.Exporter.OpenTelemetryProtocol packages
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(NightShiftTelemetry.ActivitySourceName)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(NightShiftTelemetry.MeterName)
        .AddOtlpExporter());

var app = builder.Build();

app.MapNightShiftDashboard();

app.Run();
```

The `!` tells the compiler the configuration value is present; `UseWebhookAlerts` throws if it is `null`.

## See also

- [Dashboard pages](../dashboard/pages.md): Insights, Audit, Timeline, Wallboard and the home page's live activity
- [Testing](../jobs/testing.md): unit and integration tests for code that creates jobs
- [Global configuration](../configuration/global-configuration.md): every `Use...` method
- [Troubleshooting](../troubleshooting.md)
