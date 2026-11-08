# Monitoring

Masterly.NightShift gives you several ways to watch your background jobs beyond the dashboard's job lists: traces and metrics for your observability stack through OpenTelemetry, alerts posted to a webhook or your own sink when something goes wrong, an audit log of dashboard actions and a live feed of every state change, per-method and per-queue statistics behind the Insights page and the Prometheus `/metrics` endpoint, and diagnostic logging through `Microsoft.Extensions.Logging` or another provider. The audit log, daily statistics and logging are on by default; telemetry, alerts, Insights and the activity feed are turned on with one configuration call each. Health checks for ASP.NET Core and the `/health` and `/metrics` endpoints tell probes and scrapers how the cluster is doing.

| Page | What it covers |
|---|---|
| [OpenTelemetry](opentelemetry.md) | `UseTelemetry()` (`net8.0`+), the `Masterly.NightShift` activity source and meter, span names and attributes, instruments, trace propagation from the creating request to the job, SDK setup. |
| [Alerts and webhooks](alerts-and-webhooks.md) | Alerts for failed jobs, missed deadlines and opened circuits; `UseWebhookAlerts` with JSON, Slack, Discord or Teams payloads; writing an `IAlertSink`. |
| [Audit log and activity feed](audit-log-and-activity.md) | `AuditLog` (who did what on the dashboard) and `ActivityFeed` (every state transition, turned on with `UseActivityFeed()`), what they store, what they cost, and how to read, size or turn them off. |
| [Insights](insights.md) | `UseJobInsights()`: per-method run counts and durations, per-queue wait times and drain estimates, daily and hourly history, anomaly detection, the Prometheus `/metrics` endpoint. |
| [Health checks and probes](health-checks.md) | `AddNightShift()` for ASP.NET Core health checks (cluster or this process), `MapNightShiftHealth` and `MapNightShiftMetrics` outside the dashboard, what `/health` checks and costs. |
| [Logging](logging.md) | How Masterly.NightShift logs, categories and levels, other log providers, turning logging off, logging from jobs, `[LogInvocation]`. |

## The most common setup

An ASP.NET Core app that exports traces and metrics, posts alerts to Slack, and records statistics for the Insights page. Logging to the app's `ILoggerFactory` needs no code.

```csharp
using Masterly.NightShift;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
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
