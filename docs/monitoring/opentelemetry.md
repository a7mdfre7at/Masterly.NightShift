# OpenTelemetry

`UseTelemetry()` makes Masterly.NightShift emit traces and metrics through the standard .NET `ActivitySource` and `Meter` APIs, so OpenTelemetry (or any other listener) can export them. Use it when you want to see a background job in the same trace as the web request that created it, and to chart job counts, durations and concurrency in your metrics backend.

Telemetry is available on `net8.0` and later only. The method does not exist in the `netstandard2.0` build that .NET Framework apps use.

## Quick example

Turn telemetry on in the Masterly.NightShift configuration, then subscribe to the `Masterly.NightShift` source and meter in the OpenTelemetry SDK.

```csharp
using Masterly.NightShift;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
    .UseTelemetry());

builder.Services.AddNightShiftServer();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("checkout-api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(NightShiftTelemetry.ActivitySourceName)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(NightShiftTelemetry.MeterName)
        .AddOtlpExporter());
```

The OpenTelemetry part needs these packages in your app (Masterly.NightShift itself does not reference OpenTelemetry):

- `OpenTelemetry.Extensions.Hosting` (`AddOpenTelemetry`, `WithTracing`, `WithMetrics`)
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` (`AddOtlpExporter`), or any other exporter
- `OpenTelemetry.Instrumentation.AspNetCore` (`AddAspNetCoreInstrumentation`), optional, for the request spans that jobs join

A job enqueued while handling a request now shows up in that request's trace:

```csharp
public class CheckoutController
{
    private readonly IBackgroundJobClient _jobs;

    public CheckoutController(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public void PlaceOrder(int orderId)
    {
        // Runs inside the ASP.NET Core request span: the "create" span and the job's
        // "process" span join the request's trace.
        _jobs.Enqueue<OrderService>(x => x.ConfirmOrder(orderId));
    }
}
```

## How it works

`UseTelemetry()` adds one global filter (`Diagnostics/TelemetryFilter`, internal) that is a client filter, a server filter and an apply-state filter:

1. **Creating a job.** When a job is created (`Enqueue`, `Schedule`, `ContinueJobWith`, a recurring job firing, and so on), the filter starts a `create {Type}.{Method}` span of kind `Producer`. Then it stores the trace context in two job parameters: `TraceParent` (the W3C `traceparent` value) and `TraceState` (only when the trace has a `tracestate`). The span ends when the job has been written to storage.
2. **Performing a job.** When a worker moves the job from Enqueued to Processing, the filter notes the queue of the Enqueued state, which is the queue the worker took the job from. When the worker then performs the job, the filter reads `TraceParent`/`TraceState` back and starts a `process {Type}.{Method}` span of kind `Consumer` whose parent is the stored context. The span is the current `Activity` while your job method runs, so spans your own code creates (HttpClient, EF Core, your own `ActivitySource`) become its children. It ends after the job method and the server filters inside it have finished.
3. **Metrics.** The same filter counts created jobs, measures how long each execution took, and tracks how many jobs are running right now.

The trace context is stored even when nobody listens to the `Masterly.NightShift` source: if there is a W3C `Activity.Current` when the job is created (for example the ASP.NET Core request span), its id is saved, so the job joins the caller's trace as soon as a listener is attached on the server side.

When nothing listens, the cost is close to zero. The filter checks `ActivitySource.HasListeners()` and each instrument's `Enabled` flag before doing any work. It reads the Enqueued state (one storage query per job start) and the job parameters on the server only when someone listens to the source.

Every attempt of a job is its own `process` span. A job that is retried three times produces three `process` spans, all children of the same `create` span.

## Options / API

| Member | Type | Meaning |
|---|---|---|
| `IGlobalConfiguration.UseTelemetry()` | extension method (`net8.0`+) | Adds the telemetry filter to `GlobalJobFilters`. Returns the configuration for chaining. |
| `NightShiftTelemetry.ActivitySourceName` | `const string` = `"Masterly.NightShift"` | Name of the `ActivitySource`. Pass it to `AddSource`. |
| `NightShiftTelemetry.MeterName` | `const string` = `"Masterly.NightShift"` | Name of the `Meter`. Pass it to `AddMeter`. |

The source and the meter both report the version of the `Masterly.NightShift.Core` assembly as their version.

### Spans

| Span name | Kind | Started when | Ends when |
|---|---|---|---|
| `create {Type}.{Method}` | `Producer` | a client filter sees the job being created | the job is stored (or creation failed or was canceled) |
| `process {Type}.{Method}` | `Consumer` | a worker starts performing the job | the job method and the inner server filters finished |

`{Type}` is the short name of the job's type (no namespace), `{Method}` the method name, for example `process EmailService.SendWelcome`.

### Span attributes

| Attribute | On | Value |
|---|---|---|
| `messaging.system` | both | always `nightshift` |
| `messaging.operation.name` | both | `create` or `process` |
| `messaging.operation.type` | both | `create` or `process` |
| `messaging.destination.name` | both | the queue. On `create`: for a job created in the Enqueued state, the queue it is added to, after state election, so a queue chosen by `[Queue]` is reported; for other initial states (`Scheduled`, `Awaiting`, ...), the queue passed when creating the job (the `queue` argument of `Schedule`, ...). On `process`: the queue the worker took the job from. Not set when unknown. |
| `messaging.message.id` | both | the background job id. On `create` it is added when the job has been stored. |
| `nightshift.job.name` | both | `{Type}.{Method}`, the same value as in the span name |
| `code.function.name` | both | `{Namespace.Type}.{Method}`, the full type name and the method |
| `nightshift.job.state` | `create` | the initial state: `Enqueued`, `Scheduled`, `Awaiting`, ... |
| `nightshift.job.outcome` | `process` | `succeeded`, `failed`, `canceled` or `retry_later` (see below) |
| `error.type` | both, on failure | the full type name of the exception |

On failure the span status is set to `Error` with the exception message, and an `exception` event is added (`exception.type`, `exception.message`, `exception.stacktrace`). Apps on .NET 10 get the package's `net10.0` build, which adds it with `Activity.AddException`. Apps on .NET 8 and .NET 9 get the `net8.0` build (there is no `net9.0` build), which adds the same event by hand. Exceptions thrown by the job method reach server filters wrapped in a `JobPerformanceException`; the span reports the original exception instead.

The outcome of a `process` span is:

| Outcome | When |
|---|---|
| `succeeded` | the job method returned, or a server filter marked its exception as handled |
| `canceled` | a server filter canceled the execution, a state change aborted the job while it ran (it was deleted or requeued, for example), or the server shut down while it ran and the job stopped with an `OperationCanceledException`; the job is enqueued again after a shutdown |
| `retry_later` | the job threw `RetryLaterException` to ask to run again later; the span status is not `Error` |
| `failed` | any other exception, including an `OperationCanceledException` the job threw while the server was not shutting down (a timeout of its own, for example); the span status is `Error`. That includes an attempt whose method never ran because `[Throttle]`, `[RateLimit]` or `[DisableConcurrentExecution]` gave up waiting for a slot or a lock, because the worker records that attempt as failed too: `error.type` (`Masterly.NightShift.ConcurrencyLimitException`, `Masterly.NightShift.Storage.DistributedLockTimeoutException`) tells it apart, and its duration includes the wait. Insights leaves such an attempt out |

A `create` span is marked as failed when creating the job threw an exception that no client filter handled, for example because the storage was down.

### Instruments

| Name | Type | Unit | Tags | Meaning |
|---|---|---|---|---|
| `nightshift.job.created` | `Counter<long>` | `{job}` | `nightshift.job.name`, `nightshift.job.state`, `error.type` (has a value only when creation failed) | Background jobs created. Jobs whose creation was canceled by a client filter are not counted. |
| `nightshift.job.duration` | `Histogram<double>` | `s` | `nightshift.job.name`, `nightshift.job.outcome`, `error.type` (has a value only when the outcome is `failed`) | Duration of each job execution, including the server filters that run inside the telemetry filter. |
| `nightshift.job.active` | `UpDownCounter<long>` | `{job}` | `nightshift.job.name` | Jobs being performed right now. |

In the package's `net10.0` build, which apps on .NET 10 get, the histogram advises these bucket boundaries, in seconds, because background jobs run from milliseconds to hours: `0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 900, 3600`. Apps on .NET 8 and .NET 9 get the `net8.0` build (there is no `net9.0` build), which creates the histogram without advice, so the SDK uses its default boundaries; see [Histogram buckets on .NET 8 and .NET 9](#histogram-buckets-on-net-8-and-net-9).

`nightshift.job.name` has one value per job method, so the number of series stays bounded. The job id is only on spans, never on metrics.

## More examples

### A console app or Worker Service without the generic host's DI

The OpenTelemetry SDK can be built by hand. Keep the providers alive for as long as the server runs, and dispose them at the end so the last data is flushed. This needs `OpenTelemetry` and an exporter package (here `OpenTelemetry.Exporter.Console`).

```csharp
using Masterly.NightShift;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using TracerProvider tracerProvider = Sdk.CreateTracerProviderBuilder()
    .ConfigureResource(resource => resource.AddService("report-worker"))
    .AddSource(NightShiftTelemetry.ActivitySourceName)
    .AddConsoleExporter()
    .Build();

using MeterProvider meterProvider = Sdk.CreateMeterProviderBuilder()
    .ConfigureResource(resource => resource.AddService("report-worker"))
    .AddMeter(NightShiftTelemetry.MeterName)
    .AddConsoleExporter()
    .Build();

GlobalConfiguration.Configuration
    .UseSqlServerStorage(@"Server=.\sqlexpress;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;")
    .UseTelemetry();

using (var server = new BackgroundJobServer())
{
    Console.WriteLine("Press Enter to stop.");
    Console.ReadLine();
}
```

### Separate web and worker processes

When the web app only creates jobs and a separate process performs them, call `UseTelemetry()` in both. The web app's filter writes `TraceParent` when the job is created; the worker's filter reads it and starts the `process` span. Register `AddSource(NightShiftTelemetry.ActivitySourceName)` in both if you want the `create` spans as well; the worker needs it to produce `process` spans at all. If only the worker calls `UseTelemetry()`, jobs created by the web app have no stored trace context and each execution starts a new trace.

### Histogram buckets on .NET 8 and .NET 9

The package's `net8.0` build, which apps on .NET 8 and .NET 9 use, creates the histogram without bucket advice: the advice API needs .NET 9, and the package has no `net9.0` build. The SDK then uses its default boundaries, which top out at 10 seconds. Set the boundaries with a view. It does no harm on .NET 10, where the view replaces the advice:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(NightShiftTelemetry.MeterName)
        .AddView("nightshift.job.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = new double[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 900, 3600 }
        })
        .AddOtlpExporter());
```

### Sampling

The `process` span's parent is the stored context, including its "sampled" flag. With a parent-based sampler, a job is traced exactly when the request that created it was traced:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1)))
        .AddAspNetCoreInstrumentation()
        .AddSource(NightShiftTelemetry.ActivitySourceName)
        .AddOtlpExporter());
```

### Your own spans inside a job

`Activity.Current` is the `process` span while the job method runs, so a span you start becomes its child:

```csharp
using System.Diagnostics;

public class ReportService
{
    private static readonly ActivitySource Source = new ActivitySource("MyCompany.Reports");

    public void BuildDaily()
    {
        // Activity.Current is the job's "process" span here, so this span becomes its child.
        using (var activity = Source.StartActivity("load sales"))
        {
            activity?.SetTag("report.kind", "daily");
        }
    }
}
```

Add `AddSource("MyCompany.Reports")` to the tracing setup to export it.

### Listening without OpenTelemetry

The source and the meter are plain .NET APIs. In a test or a quick diagnostic you can listen with `ActivityListener` and `MeterListener` directly, with no extra packages:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Masterly.NightShift;

public static class RawListeners
{
    public static IDisposable ListenToSpans()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NightShiftTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Console.WriteLine(
                $"{activity.DisplayName} {activity.Kind} {activity.Duration.TotalMilliseconds:N0} ms " +
                $"trace={activity.TraceId} outcome={activity.GetTagItem("nightshift.job.outcome")}")
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    public static IDisposable ListenToMetrics()
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == NightShiftTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            Console.WriteLine($"{instrument.Name} = {value:N3} {instrument.Unit}"));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            Console.WriteLine($"{instrument.Name} += {value}"));

        listener.Start();
        return listener;
    }
}
```

## Things to know

- **Call it once, early.** `UseTelemetry()` adds a global filter. Put it in the `AddNightShift(...)` callback or next to your other `GlobalConfiguration` calls, in every process that creates or performs jobs.
- **Jobs created before telemetry was on** (or by a process without `UseTelemetry()`) have no `TraceParent` parameter. Their `process` span starts a new trace. The same happens when there was no `Activity` at creation time, for example for jobs created by the recurring job scheduler, unless the `create` span itself is listened to, in which case the job continues the `create` span's trace.
- **Only W3C ids are propagated.** An `Activity` using the legacy hierarchical id format is not stored. W3C is the default on modern .NET.
- **The span covers the server filters inside the telemetry filter**, not only the job method.
- **The queue on `process` spans comes from the Enqueued state** that the worker took the job from. If the filter was not listening when the job moved to Processing, or that state could not be read, the span falls back to the queue passed when creating the job, or has no `messaging.destination.name`. A job created with `Schedule` gets its queue when it is enqueued later, so its `create` span shows only a queue passed explicitly.
- **Retries:** each attempt is a separate `process` span and a separate `nightshift.job.duration` measurement. A failed attempt that will be retried has the outcome `failed`. An attempt that threw `RetryLaterException` has the outcome `retry_later`, even when the job has already asked more than `MaxRetryLaterRequests` times and `AutomaticRetry` handles it as a regular failure.
- **Job parameters:** `TraceParent` and `TraceState` are visible among the job's parameters on the dashboard's job details page. Do not use these names for your own parameters.
- **This is not the dashboard's `/metrics` endpoint.** The dashboard also serves Prometheus text at `/metrics`, built from storage queries (job counts by state, queue lengths, Insights data). See [Insights](insights.md#prometheus-endpoint). The `Meter` described here reports what the current process does; `/metrics` reports what is in the storage.

## See also

- [Monitoring overview](README.md)
- [Insights](insights.md): per-method statistics stored for the dashboard and `/metrics`
- [Logging](logging.md)
- [Custom filters](../filters/custom-filters.md): how client and server filters wrap a job
- [Global configuration](../configuration/global-configuration.md)
- [Dependency injection](../server/dependency-injection.md): `AddNightShift` and `AddNightShiftServer`
