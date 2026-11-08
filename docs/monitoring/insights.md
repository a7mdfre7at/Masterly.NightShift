# Insights

Insights are statistics Masterly.NightShift keeps in the job storage so the dashboard can answer "how is this job doing?": run and failure counts, recent durations and their percentiles, how long jobs wait in each queue and how fast each queue drains, and hourly totals for the last week. Turn them on with `UseJobInsights()` when you want the Insights page, queue health, finish-time estimates for running jobs, and the per-method numbers on the `/metrics` endpoint.

## Quick example

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
    .UseJobInsights());

builder.Services.AddNightShiftServer();
```

Open the dashboard's **Insights** page after a few jobs have run.

## How it works

There are three recorders. `UseJobInsights()` adds the first two as global filters; the third is always on.

| Recorder | Kind | Records | Needs |
|---|---|---|---|
| `JobInsightsAttribute` | server filter | per job method: succeeded and failed counts, the last 200 durations, the last run | `UseJobInsights()` or the attribute on a class or method |
| `QueueInsightsFilter` | apply-state filter | per queue: how long each job waited before a worker took it, and how many were taken per minute | `UseJobInsights()` |
| `StatisticsHistoryAttribute` | election filter | succeeded, failed and deleted counts per day and per hour | nothing: it is in `GlobalJobFilters` by default |

**Per method.** When a job finishes (succeeded or failed), `JobInsightsAttribute` opens its own write transaction and updates the method's records. If that write fails, the failure is logged as a warning and the job's outcome is not touched: a statistic never fails a job or makes it retry. The method key is `{Type}.{Method}`, with the type's name without its namespace and generic types written out (for example `Repository<Order>.Purge`). A key longer than 80 characters (`JobInsightsAttribute.MaxKeyLength`) keeps the start of the type name and the method name, with eight hex digits of a hash of the whole key between them (`{start of the type name}~{hash}.{Method}`), so the storage keys built from it fit SQL Server's 100-character key columns. The duration is measured from the moment the job method is called to the filter's `OnPerformed`, so it is the execution time: it leaves out the time spent waiting in the queue, and the time `[Throttle]`, `[RateLimit]` or `[DisableConcurrentExecution]` spent waiting for a slot or a lock. Storage keys:

| Key | Type | Content |
|---|---|---|
| `insights` | set | every recorded method key |
| `insights:{key}:ok`, `insights:{key}:failed` | counters | succeeded and failed runs |
| `insights:{key}:durations` | list | the last 200 durations in milliseconds |
| `insights:{key}` | hash | `LastRunAt`, `LastDurationMs`, `LastJobId`, `LastServerId`, `LastFailed`, `Invoker` |

**Per queue.** When a job moves from Enqueued to Processing, `QueueInsightsFilter` reads the Enqueued state it is leaving (one extra state read per job start) and takes its queue and `EnqueuedAt` time. Once the state change is committed, it records the wait in a short transaction of its own. A failing write is logged as a warning and lost; it never fails the state change. Storage keys:

| Key | Type | Content |
|---|---|---|
| `queue-insights` | set | every queue with statistics |
| `queue:{name}:latency` | list | the last 200 waits in milliseconds |
| `queue:{name}:started:{yyyyMMddHHmm}` | counter, expires after 1 hour | jobs taken in that minute (UTC) |
| `queue:{name}:insights` | hash | `LastStartedAt` |

**Daily and hourly history.** `StatisticsHistoryAttribute` runs during state election, after `AutomaticRetry` (its order is 30, `AutomaticRetry`'s is 20). So a failed attempt that will be retried is not counted as failed; only the final outcome is. It increments `stats:succeeded:yyyy-MM-dd`, `stats:failed:...` and `stats:deleted:...` (kept for one month) and the same keys with `-HH` appended (kept for eight days, `StatisticsHistoryAttribute.HourlyCounterLifetime`). All dates are UTC. The home page's history graphs and the Insights heatmap read these counters.

### Where the dashboard shows them

| Page | What it shows | Source |
|---|---|---|
| **Insights** (`/insights`) | Summary tiles (methods, runs, failure rate, invoker tiers), "needs attention" anomalies, a 7 x 24 heatmap of the last week, circuit breakers (with a reset button), and a table per method: runs, failed, median, p95, max, trend sparkline, invoker, last run | per method, hourly history |
| **Queues** | Per queue: median and p95 wait, jobs per minute, "clears in" estimate, stalled flag | per queue |
| **Processing jobs** | A predicted end and progress bar for jobs that report no progress, and a "longer than usual" flag | per method |
| **Wallboard** (`/wallboard`) | Queue drain times, predicted ends of running jobs | per method, per queue |
| **Job details** | In the diagnosis of a running job: its usual duration (p95), and a warning when it runs well past it | per method |
| **Catalog**, **Recurring jobs > Forecast** | Typical durations, used to size runs on the forecast timeline | per method |
| `/metrics` | Per-method and per-queue series, see [Prometheus endpoint](#prometheus-endpoint) | per method, per queue |

Without `UseJobInsights()` these pages still work; they just have no statistics to show. The heatmap and history graphs work regardless.

## Options / API

### Configuration

| Member | Meaning |
|---|---|
| `IGlobalConfiguration.UseJobInsights()` | Adds `JobInsightsAttribute` and `QueueInsightsFilter` to the global filters. |
| `[JobInsights]` | Apply to a class or method to record only those jobs. Queue statistics are not recorded this way. |

### `JobInsightsAttribute`

| Member | Type | Meaning |
|---|---|---|
| `IndexKey` | `const string` = `"insights"` | The set listing every recorded method key. |
| `DurationSampleSize` | `const int` = `200` | How many recent durations are kept per method. |
| `GetKey(Job job)` | `static string` | The key under which a job's statistics are stored: `Type.Method`, at most `MaxKeyLength` characters. |
| `MaxKeyLength` | `const int` = `80` | The longest key `GetKey` returns. |
| `ReadAll(IStorageConnection connection)` | `static IReadOnlyList<JobInsight>` | Every method's statistics, most recently run first. Empty when the connection is not a `JobStorageConnection`. |
| `Read(JobStorageConnection connection, string key)` | `static JobInsight?` | One method's statistics, or `null` when nothing was recorded. |

### `JobInsight`

| Property | Type | Meaning |
|---|---|---|
| `Key` | `string` | The method key, `Type.Method`. |
| `Succeeded`, `Failed` | `long` | Runs that succeeded and failed, since recording started. |
| `Total` | `long` | `Succeeded + Failed`. |
| `FailureRate` | `double` | `Failed / Total`, 0 to 1. |
| `Durations` | `long[]` | The recent durations in milliseconds (up to 200), oldest first. |
| `AverageMs`, `MedianMs`, `P95Ms`, `MaxMs` | `long` | Mean, median, 95th percentile and maximum of `Durations`. 0 when there are none. |
| `RecentMedianMs` | `long` | Median of the last `RecentSampleSize` (10) runs. |
| `EarlierMedianMs` | `long` | Median of the runs before those 10. |
| `LastRunAt` | `DateTime?` | When the method last ran (UTC). |
| `LastDurationMs` | `long` | How long the last run took. |
| `LastJobId`, `LastServerId` | `string?` | The last job and the server that performed it. |
| `LastFailed` | `bool` | Whether the last run failed. |
| `Invoker` | `string?` | The `JobInvokerKind` of the last run: `Generated`, `Compiled` or `Reflection`. See [Source-generated invokers](../jobs/source-generated-invokers.md). |
| `IsSlowingDown` | `bool` | At least 20 samples, the recent median is at least 1 second and at least twice the earlier median. |
| `HasHighFailureRate` | `bool` | At least 10 runs and a failure rate of 20% or more. |
| `LastRunWasSlow` | `bool` | At least 10 samples, the last run took at least 1 second and more than three times the p95 of the runs before it. |
| `IsAnomalous` | `bool` | Any of the three above. These are the methods listed under "needs attention" on the Insights page. |
| `EstimateCompletion(DateTime startedAt)` | `DateTime?` | `startedAt` plus the median duration, or `null` with fewer than 3 samples. |

### `QueueInsights` (static) and `QueueHealth`

| Member | Type | Meaning |
|---|---|---|
| `QueueInsights.IndexKey` | `const string` = `"queue-insights"` | The set listing every queue with statistics. |
| `QueueInsights.LatencySampleSize` | `const int` = `200` | How many recent waits are kept per queue. |
| `QueueInsights.Read(IStorageConnection connection, string queue)` | `QueueHealth?` | One queue's statistics, or `null` when nothing was recorded or the connection is not a `JobStorageConnection`. |
| `QueueInsights.ReadAll(IStorageConnection connection)` | `IReadOnlyDictionary<string, QueueHealth>` | Every queue with statistics. Keys are case-insensitive. |
| `QueueInsights.Record(IWriteOnlyTransaction transaction, string queue, TimeSpan latency, DateTime now)` | method | Records one job start. `QueueInsightsFilter` calls it; you rarely need to. |

| `QueueHealth` member | Type | Meaning |
|---|---|---|
| `Queue` | `string` | The queue name. |
| `Samples` | `int` | How many recent starts the percentiles are based on (up to 200). |
| `MedianLatencyMs`, `P95LatencyMs` | `long` | Median and 95th percentile of the time jobs waited before a worker took them. |
| `StartedLast5Minutes` | `long` | Jobs taken from the queue in the last five whole minutes, the five minutes before the current one. Jobs taken in the current, unfinished minute are counted once it ends. |
| `PerMinute` | `double` | `StartedLast5Minutes / 5`: the average rate over those five minutes. |
| `LastStartedAt` | `DateTime?` | When a worker last took a job from the queue (UTC). |
| `EstimateDrain(long length)` | `TimeSpan?` | How long a backlog of `length` jobs needs at the current rate. `TimeSpan.Zero` for an empty queue, `null` when nothing moves. |
| `IsStalled(long length)` | `bool` | The queue holds jobs (`length > 0`) and no worker took a job from it in the last five minutes. |

The queue length is not part of the statistics. Get it from the monitoring API (`storage.GetMonitoringApi().Queues()`), as in the examples below. When the monitoring API derives from `JobStorageMonitor` and `storage.HasFeature(JobStorageFeatures.Monitoring.QueueCounts)` is `true` (SQL Server), `QueueCounts()` returns the same lengths without reading each queue's first jobs.

### `StatisticsHistoryAttribute`

| Member | Meaning |
|---|---|
| `HourlyCounterLifetime` | `static readonly TimeSpan` = 8 days. How long the hourly counters live. |

Registered globally by default with `Order = 30`. There is nothing to configure.

## More examples

### Record only some job classes

When a few job types matter and the rest are high-volume noise, skip `UseJobInsights()` and put the attribute on the classes you care about:

```csharp
[JobInsights]
public class InvoiceService
{
    public void GenerateMonthly(int customerId) { }
}
```

### Read the statistics from code

```csharp
using Masterly.NightShift;

public static class InsightsReport
{
    public static void PrintSlowMethods(JobStorage storage)
    {
        using (var connection = storage.GetReadOnlyConnection())
        {
            foreach (JobInsight insight in JobInsightsAttribute.ReadAll(connection))
            {
                if (insight.IsAnomalous)
                {
                    Console.WriteLine(
                        $"{insight.Key}: {insight.Total} runs, {insight.FailureRate:P0} failed, " +
                        $"median {insight.MedianMs} ms, p95 {insight.P95Ms} ms, slowing down: {insight.IsSlowingDown}");
                }
            }
        }
    }

    public static void PrintQueues(JobStorage storage)
    {
        using (var connection = storage.GetReadOnlyConnection())
        {
            var lengths = storage.GetMonitoringApi().Queues().ToDictionary(q => q.Name, q => q.Length);

            foreach (KeyValuePair<string, QueueHealth> pair in QueueInsights.ReadAll(connection))
            {
                var length = lengths.TryGetValue(pair.Key, out var value) ? value : 0;
                QueueHealth health = pair.Value;

                Console.WriteLine(
                    $"{pair.Key}: waits median {health.MedianLatencyMs} ms / p95 {health.P95LatencyMs} ms, " +
                    $"{health.PerMinute:N1} per minute, clears in {health.EstimateDrain(length)?.ToString() ?? "never"}, " +
                    $"stalled: {health.IsStalled(length)}");
            }
        }
    }
}
```

### An ASP.NET Core health check for a queue

```csharp
using Masterly.NightShift;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class EmailQueueHealthCheck : IHealthCheck
{
    private readonly JobStorage _storage;

    public EmailQueueHealthCheck(JobStorage storage)
    {
        _storage = storage;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using (var connection = _storage.GetReadOnlyConnection())
        {
            var health = QueueInsights.Read(connection, "emails");
            if (health == null)
            {
                return Task.FromResult(HealthCheckResult.Healthy("No statistics yet."));
            }

            var length = _storage.GetMonitoringApi().Queues().FirstOrDefault(q => q.Name == "emails")?.Length ?? 0;

            if (health.IsStalled(length))
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"{length} e-mails waiting and none taken for five minutes."));
            }

            return health.P95LatencyMs > 60_000
                ? Task.FromResult(HealthCheckResult.Degraded($"E-mails wait {health.P95LatencyMs / 1000} s (p95)."))
                : Task.FromResult(HealthCheckResult.Healthy());
        }
    }
}
```

```csharp
builder.Services.AddHealthChecks().AddCheck<EmailQueueHealthCheck>("nightshift-emails");
```

`JobStorage` is registered in DI by `AddNightShift`.

### Estimate when a running job finishes

```csharp
using Masterly.NightShift.Storage;

public static DateTime? ExpectedEnd(JobStorage storage, string key, DateTime startedAtUtc)
{
    using (var connection = storage.GetReadOnlyConnection())
    {
        if (connection is not JobStorageConnection storageConnection) return null;

        return JobInsightsAttribute.Read(storageConnection, key)?.EstimateCompletion(startedAtUtc);
    }
}
```

Get the key of a job with `JobInsightsAttribute.GetKey(job)`.

## Prometheus endpoint

The dashboard serves `GET {dashboard path}/metrics` in the Prometheus text format (`text/plain; version=0.0.4`). It is built from storage queries, so it shows the whole cluster, not one process. The text is kept for 5 seconds per storage: several scrapers, or a scraper and a health probe, cost the storage no more than one. Each metric family is written as one group after its `# HELP` and `# TYPE` lines, as strict parsers require, and no two families share a name, also for OpenMetrics parsers, which name a counter's family without its `_total` suffix. The series:

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `nightshift_health_status` | gauge | | 0 healthy, 1 degraded, 2 unhealthy (the same checks as the dashboard's health endpoint) |
| `nightshift_servers` | gauge | | Servers that reported a heartbeat recently |
| `nightshift_processing_paused` | gauge | | 1 while processing is paused from the dashboard |
| `nightshift_jobs` | gauge | `state` = `enqueued`, `scheduled`, `processing`, `failed`, `awaiting`, `retries` | Jobs by state |
| `nightshift_jobs_finished_total` | counter | `state` = `succeeded`, `deleted` | Jobs that finished since the storage was created |
| `nightshift_recurring_jobs` | gauge | | Recurring jobs |
| `nightshift_recurring_jobs_late` | gauge | | Recurring jobs whose next run is more than a minute overdue |
| `nightshift_queue_length` | gauge | `queue` | Jobs waiting in each queue |
| `nightshift_queue_fetched` | gauge | `queue` | Jobs fetched and not yet acknowledged (when the storage reports it) |
| `nightshift_queue_latency_seconds` | gauge | `queue`, `quantile` = `0.5`, `0.95` | Wait before a worker took the job, over the last 200 starts (needs `UseJobInsights()`) |
| `nightshift_queue_started_per_minute` | gauge | `queue` | Jobs taken per minute (needs `UseJobInsights()`) |
| `nightshift_method_runs_total` | counter | `method`, `result` = `succeeded`, `failed` | Runs per job method (needs Insights) |
| `nightshift_method_duration_seconds` | gauge | `method`, `quantile` = `0.5`, `0.95` | Duration per job method, over the last 200 runs (needs Insights) |

The queue and method series need a storage with the extended API (`JobStorageFeatures.ExtendedApi`, which SQL Server has); with another storage they are left out and the other series stay.

The endpoint goes through the dashboard's authorization filters like every other dashboard route. With the default filter (local requests only) a remote Prometheus server is refused. Either give the scraper access through your dashboard authorization (see [Dashboard authorization](../dashboard/authorization.md)), or map the same text outside the dashboard with `app.MapNightShiftMetrics("/metrics/nightshift")` and limit it with endpoint conventions such as `RequireHost` (see [Health checks and probes](health-checks.md)). For per-process metrics pushed through OpenTelemetry, see [OpenTelemetry](opentelemetry.md).

## Things to know

- **Cost.** `JobInsightsAttribute` adds one write transaction with five commands after every job. `QueueInsightsFilter` adds one state read per job start and, after the state change, one transaction with five commands. On a system that runs many small jobs per second, consider `[JobInsights]` on selected classes instead of `UseJobInsights()`.
- **Hot rows.** Every run of one method writes the same duration list and hash, and every job start in one queue writes the same latency list. On SQL Server, adding to a list locks all the rows of that list for the length of the short transaction, so the runs of one busy method (or the starts of one busy queue) wait for each other there. The job and its state change don't: these writes happen outside the state transaction, and a write that fails (a deadlock, for example) is only logged. If one method runs hundreds of times per second, leave `[JobInsights]` off it.
- **Types with the same name share a key.** The key uses the type's name without its namespace, so `Billing.ReportJob.Run` and `Sales.ReportJob.Run` are counted together. Give the classes different names, or record only one of them with `[JobInsights]`.
- **Nothing expires the per-method records.** The counters, duration lists and hashes stay until the storage is cleared, and methods you deleted from your code stay in the `insights` set and on the Insights page.
- **Overloads share a key.** The key is the type and method name, without parameter types, so overloads of one method are counted together.
- **Canceled executions, retry-later requests and concurrency timeouts are not recorded.** A run is canceled when a server filter canceled it, a state change aborted it (the job was deleted while it ran, for example), or the server shut down while it ran. A run that threw `RetryLaterException` asked to run again later; it is neither a success nor a failure. A run whose `[Throttle]` or `[RateLimit]` gave up waiting for a slot (`ConcurrencyLimitException`), or whose `[DisableConcurrentExecution]` gave up waiting for its lock (`DistributedLockTimeoutException`), never called the method, so it isn't counted either, although the worker records the attempt as failed. The same exceptions thrown by the job method itself count as failed. Any other exception counts as failed even if the job will be retried, so `Failed` counts attempts, not jobs that reached the Failed state. That includes an `OperationCanceledException` the job threw while the server was not shutting down.
- **Queue waits are recorded under the queue the job waited in.** A job with its own queue (`Enqueue("emails", ...)`, a recurring job's queue) has `default` in its Enqueued state, and is recorded under its own queue, as the server picks it.
- **Queue waits are measured only for Enqueued to Processing.** The wait is the time since the job's Enqueued state was applied, so for a retried or scheduled job it starts when it was last enqueued.
- **Throughput trails by up to a minute.** Jobs per minute and the "clears in" estimate use the five whole minutes before the current one, so a burst shows up when its minute ends. A queue that started moving within the current minute is not reported as stalled, because `IsStalled` also looks at `LastStartedAt`.
- **Reading needs a `JobStorageConnection` and the extended API.** SQL Server provides both. With a storage that lacks either, the Insights page shows a "not supported" message and the readers return nothing instead of throwing.
- **`StatisticsHistoryAttribute` is always on.** Removing it from `GlobalJobFilters` leaves the home page history graphs and the heatmap empty.

## See also

- [Monitoring overview](README.md)
- [OpenTelemetry](opentelemetry.md)
- [Dashboard pages](../dashboard/pages.md)
- [Queues and workers](../server/queues-and-workers.md)
- [Retries](../filters/retries.md): circuit breakers shown on the Insights page
- [Source-generated invokers](../jobs/source-generated-invokers.md): the invoker column
