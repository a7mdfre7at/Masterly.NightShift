# Retries

A job that throws is retried automatically, with growing pauses between attempts. This page explains the default policy and how to change it per job or globally, how a job can ask to run again at a given moment (`RetryLaterException`), how to retry quickly inside the same worker (`RetryInProcess`), and how to stop hammering a dependency that keeps failing (`CircuitBreaker`).

## Quick example

```csharp
using Masterly.NightShift;

public class EmailService
{
    [AutomaticRetry(Attempts = 3)]
    public void SendWelcome(int userId)
    {
        // Throws when the mail server is down.
    }
}

jobs.Enqueue<EmailService>(x => x.SendWelcome(42));
```

If `SendWelcome` throws, the job is scheduled again up to three times. After the third retry fails, it stays in the Failed state, where you can see the exception and requeue it from the dashboard.

## The default retry policy

`AutomaticRetryAttribute` is in `GlobalJobFilters.Filters` from the start, so every job has it:

- **10 retries** (`AutomaticRetryAttribute.DefaultRetryAttempts`), so a job runs at most 11 times.
- **Back-off:** before retry number `n` (1 to 10) the job waits `(n - 1)^4 + 15 + r * n` seconds, where `r` is a random whole number from 0 to 29. The random part spreads retries out, so many jobs that failed together don't all come back at the same second.
- **After the last retry** the job stays **Failed** (`OnAttemptsExceeded = AttemptsExceededAction.Fail`). Failed jobs don't expire unless you give them a retention period ([Failed jobs](timing.md#failed-jobs)); they wait for you to requeue or delete them.

| Retry | Wait before it |
|-------|----------------|
| 1 | 15 to 44 s |
| 2 | 16 to 74 s |
| 3 | 31 to 118 s |
| 4 | 1.6 to 3.5 min |
| 5 | 4.5 to 7 min |
| 6 | 11 to 14 min |
| 7 | 22 to 25 min |
| 8 | 40 to 44 min |
| 9 | 69 to 73 min |
| 10 | 1 h 50 min to 1 h 54 min |

All ten retries together take about 4.3 to 4.7 hours, plus the time the jobs themselves run and up to 15 seconds per retry for the scheduler to notice the job is due (`SchedulePollingInterval`, see [Server options](../server/server-options.md)).

## How it works

`AutomaticRetryAttribute` is an election filter (`IElectStateFilter`) and an apply filter (`IApplyStateFilter`), with `Order = 20`.

1. The worker catches the exception and proposes the **Failed** state.
2. During election, the filter reads the `RetryCount` job parameter, adds one and, while the count is within `Attempts`, replaces the candidate with **Scheduled** (or **Enqueued** when the delay is zero). The reason reads `Retry attempt 3 of 10: <message>`, with a message longer than 50 characters cut to its first 49 and `…`. The exception is still recorded in the job's history, because Failed is kept as a traversed state.
3. When the scheduled time comes, the delayed job scheduler enqueues the job again and a worker picks it up.
4. While a job is scheduled for a retry, the apply filter keeps its id in the `retries` set, which is what the dashboard's **Retries** page lists. The id leaves the set when the job leaves the Scheduled or Failed state.
5. When attempts are exhausted, the job either stays Failed (the filter logs an error) or, with `AttemptsExceededAction.Delete`, moves to **Deleted** with the reason "Exceeded the maximum number of retry attempts." (or "Retries were disabled for this job." when `Attempts` is 0).

Retries survive restarts: the counter lives in the `RetryCount` job parameter, and the waiting job is an ordinary Scheduled job in storage.

## AutomaticRetryAttribute

Apply it to a method, a class or interface, or register it globally. A method attribute replaces the global one for that job.

| Property | Type | Default | Meaning |
|----------|------|---------|---------|
| `Attempts` | `int` | `10` | Maximum number of retries. `0` disables retries. Negative values throw. |
| `DelaysInSeconds` | `int[]` | `null` | Fixed delays: retry `n` waits `DelaysInSeconds[n - 1]` seconds; retries past the end of the array use the last value. When set, it wins over `DelayInSecondsByAttemptFunc`; set it back to `null` to use the function again. An empty array or a negative number throws `ArgumentException`. |
| `DelayInSecondsByAttemptFunc` | `Func<long, int>` | The formula above | Computes the delay in seconds from the retry number (starting at 1). Can't be set in attribute syntax; use it on a filter you register in code. |
| `OnAttemptsExceeded` | `AttemptsExceededAction` | `Fail` | `Fail` leaves the job in the Failed state. `Delete` moves it to the Deleted state. |
| `OnlyOn` | `Type[]` | `null` | Retry only when the exception is one of these types or derives from one. Other exceptions fail the job at once. |
| `ExceptOn` | `Type[]` | `null` | Never retry these exception types (and their subtypes); they fail the job at once. |
| `LogEvents` | `bool` | `true` | Log a warning for every retry, and an error when attempts are exhausted. |
| `MaxRetryLaterRequests` | `int` | `100` | How many times the job may throw `RetryLaterException` before that exception is treated as an ordinary failure. `0` turns the special handling off. |
| `Order` | `int` | `20` | Position among election filters. |

Related members: `AutomaticRetryAttribute.DefaultRetryAttempts` (10), `DefaultMaxRetryLaterRequests` (100) and `RetryLaterCountParameter` (`"RetryLaterCount"`).

`AttemptsExceededAction` values:

| Value | Result |
|-------|--------|
| `Fail` (default) | The job stays Failed. It never expires, unless you set a [retention for failed jobs](timing.md#failed-jobs). Use it when someone should look at the failure. |
| `Delete` | The job moves to Deleted and expires like a finished job. Use it for work that is pointless once it is late, such as a newsletter or a cache warm-up. |

## More examples

### Disable retries for one job

```csharp
public class EmailService
{
    [AutomaticRetry(Attempts = 0)]
    public void SendPasswordReset(int userId)
    {
        // A reset link that arrives an hour late is worse than none.
    }
}
```

### Change the default for every job

Remove the default instance and register your own, in the place where you configure NightShift:

```csharp
using Masterly.NightShift;

builder.Services.AddNightShift(config =>
{
    config.UseSqlServerStorage(builder.Configuration.GetConnectionString("Jobs"));

    GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>();
    config.UseFilter(new AutomaticRetryAttribute { Attempts = 5 });
});
```

Without the `Remove` call this still works, because of two global filters of the same type the one added last wins, but removing the default keeps `GlobalJobFilters.Filters` easy to read. Without DI, call the same two lines on `GlobalConfiguration.Configuration` at startup.

### Fixed delays

```csharp
public class InvoiceService
{
    // 10 s, 1 min, 10 min, then 1 h before the fourth and last retry.
    [AutomaticRetry(Attempts = 4, DelaysInSeconds = new[] { 10, 60, 600, 3600 })]
    public void SendToAccounting(int invoiceId)
    {
    }
}
```

### Your own back-off function

```csharp
GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>();
GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute
{
    Attempts = 6,
    // 30 s, 1 min, 2 min, 4 min, 8 min, 16 min, capped at an hour.
    DelayInSecondsByAttemptFunc = attempt => (int)Math.Min(30 * Math.Pow(2, attempt - 1), 3600),
});
```

### Retry only transient errors

```csharp
using System.Net.Http;

public class PartnerSync
{
    // Network trouble is worth retrying; anything else is a bug and fails at once.
    [AutomaticRetry(Attempts = 8, OnlyOn = new[] { typeof(HttpRequestException), typeof(TimeoutException) })]
    public Task PushOrders(int batchId) => Task.CompletedTask;

    // Bad input won't get better by retrying.
    [AutomaticRetry(ExceptOn = new[] { typeof(ArgumentException), typeof(InvalidOperationException) })]
    public Task PullCatalog(string supplier) => Task.CompletedTask;
}
```

### Delete instead of failing

```csharp
public class EmailService
{
    [AutomaticRetry(Attempts = 5, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public void SendNewsletter(int userId)
    {
    }
}
```

## RetryLaterException

Throw `RetryLaterException` from a job to say "not now, try again at this time". `AutomaticRetryAttribute` schedules the job for exactly that moment instead of using its back-off, and the request **does not use up a retry attempt**. Typical cases: an API answered `429 Too Many Requests` with a `Retry-After` header, a partner system is in its maintenance window, a resource is being rebuilt.

```csharp
using System.Net;
using System.Net.Http;
using Masterly.NightShift;

public class ShippingService
{
    private readonly HttpClient _http;

    public ShippingService(HttpClient http)
    {
        _http = http;
    }

    public async Task SyncTracking(string trackingNumber, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync($"https://carrier.example.com/track/{trackingNumber}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RetryLaterException(
                response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1),
                "Carrier API rate limit");
        }

        response.EnsureSuccessStatusCode();
    }
}
```

Ask for a moment instead of a delay:

```csharp
public class ReportService
{
    public void BuildMonthly(int year, int month)
    {
        var nightlyRunStarts = DateTime.UtcNow.Date.AddDays(1).AddHours(2);
        if (WarehouseIsReloading())
        {
            throw new RetryLaterException(nightlyRunStarts, "The data warehouse is being reloaded");
        }

        // ...
    }

    private static bool WarehouseIsReloading() => false;
}
```

| Constructor | Meaning |
|-------------|---------|
| `RetryLaterException(TimeSpan delay)` | Run again after `delay`. Negative delays, and delays longer than `RetryLaterException.MaxDelay` (365 days), throw `ArgumentOutOfRangeException`. |
| `RetryLaterException(TimeSpan delay, string? reason)` | The same, with a reason shown in the dashboard. |
| `RetryLaterException(DateTime retryAt)` | Run again at `retryAt`. A UTC or local time is converted to UTC; an `Unspecified` kind is taken as UTC. |
| `RetryLaterException(DateTime retryAt, string? reason)` | The same, with a reason. |

| Property | Type | Meaning |
|----------|------|---------|
| `Delay` | `TimeSpan?` | The requested delay, when one was given. |
| `RetryAt` | `DateTime?` | The requested moment (UTC), when one was given. |
| `Reason` | `string?` | The reason, also used as the exception message. |

How it is handled: the filter adds one to the `RetryLaterCount` job parameter and schedules the job with the reason `Retry later (3 of 100): <reason>`. A moment in the past enqueues the job at once. Once the job has asked more than `MaxRetryLaterRequests` times (100 by default), the exception counts as an ordinary failure and the usual retry rules apply, so a job that keeps asking still ends eventually. Without `AutomaticRetryAttribute` (for example after you removed it globally), the exception is just a failure.

## RetryInProcessAttribute

`[RetryInProcess]` retries the job method right away, inside the same worker, when it throws. Nothing is written to storage between attempts and the job never enters the Failed state. Use it for short, transient errors such as a deadlock or a dropped connection, and keep `AutomaticRetry` for longer outages.

```csharp
using System.Data.Common;
using Masterly.NightShift;

public class OrderRepository
{
    // Up to 3 more attempts after the first failure, waiting 500 ms, 1 s, then 1.5 s.
    [RetryInProcess(3, DelayMilliseconds = 500, ExceptionTypes = new[] { typeof(DbException) })]
    public void MarkShipped(int orderId)
    {
    }

    [RetryInProcess(2)]
    public Task ArchiveAsync(int orderId) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `attempts` (constructor) | `int` | required | Additional attempts after the first failure. `0` means no retries; negative values throw. |
| `DelayMilliseconds` | `int` | `200` | Base delay. Retry `n` waits `n * DelayMilliseconds`, so the delays grow linearly, up to about 24.8 days (`Int32.MaxValue` milliseconds, the longest wait on every framework). From 0 (no delay) to `RetryInProcessAttribute.MaxDelayMilliseconds` (3,600,000, one hour); other values throw `ArgumentOutOfRangeException` when the attribute is read. |
| `ExceptionTypes` | `Type[]` | empty | Exception types (and subtypes) worth retrying. Empty means every exception except `RetryLaterException` and `ConcurrencyLimitException`. |
| `Order` | `int` | `0` | Position among interceptors; lower runs outermost. |

How it works: `RetryInProcessAttribute` is an interceptor (`AsyncJobInterceptorAttribute`), so it wraps only the call of the job method; server filters such as `Throttle` run once around all attempts. `OperationCanceledException` and `JobAbortedException` are never retried. With an empty `ExceptionTypes`, neither are `RetryLaterException` and `ConcurrencyLimitException`: they ask for a later attempt, which an immediate retry would ignore, so they leave the interceptor at once and `RetryLaterException` reschedules the job as asked. List them in `ExceptionTypes` to retry them in place anyway. For a synchronous job method the worker thread waits out the delay itself, so every attempt runs on the worker thread, like the first one; for an asynchronous method the delay is awaited. The waits end early when the server shuts down. Each failed attempt is logged as a warning. When the last attempt fails, the exception leaves the interceptor and the normal flow continues: the job fails, and `AutomaticRetry` takes over.

Apply it to a method, or to a class or interface for all its methods. Interceptor attributes are not de-duplicated: an attribute on the class and another on the method both run, nested.

## CircuitBreakerAttribute

`[CircuitBreaker]` stops running jobs against a resource that keeps failing. After `failureThreshold` failures within `windowSeconds`, the circuit **opens**: for `openSeconds`, every job on that resource is put back to the Scheduled state instead of running, so it doesn't use up its retries against a dead dependency. When the open period ends, a single job is let through as the **probe** (**half-open**), and the other jobs keep being rescheduled. The probe decides: its success **closes** the circuit and forgets the failures, its failure opens it again for another `openSeconds`.

```csharp
using Masterly.NightShift;

public class PaymentService
{
    // Both methods share the "payments" circuit: 5 failures in 60 s stop both for 2 minutes.
    [CircuitBreaker("payments", failureThreshold: 5, windowSeconds: 60, openSeconds: 120)]
    public Task Capture(int orderId) => Task.CompletedTask;

    [CircuitBreaker("payments", 5, 60, 120)]
    public Task Refund(int orderId) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `resource` (constructor) | `string?` | `null` | Resource template. `{0}`, `{1}` ... are replaced with the job's arguments (a `[Redacted]` one with a hash of its value), and the result is lower-cased. A result longer than 83 characters (the most that fits the key `circuit:{resource}:failures`) is shortened to its start and a hash ([Resource templates](concurrency.md#resource-templates)). `null` means the job's method: the full name of its type, the method's name and its parameter types, lower-cased, such as `contoso.geo.geocodingservice.geocode(system.int32)`. So each job method (each overload, each same-named type in another namespace) gets its own circuit. |
| `failureThreshold` (constructor) | `int` | required | Failures within the window that open the circuit. Must be greater than zero. |
| `windowSeconds` (constructor) | `int` | required | Length of the sliding window in which failures are counted. Must be greater than zero. |
| `openSeconds` (constructor) | `int` | required | How long the circuit stays open. Must be greater than zero. |
| `OnlyOn` | `Type[]` | empty | Exception types (and subtypes) whose failures count, such as `new[] { typeof(HttpRequestException), typeof(TimeoutException) }`. Empty means every exception that `ExceptOn` doesn't exclude. |
| `ExceptOn` | `Type[]` | `ConcurrencyLimitException`, `DistributedLockTimeoutException`, `RetryLaterException` | Exception types (and subtypes) whose failures never count, for any job and for the probe. The defaults are failures in which the dependency was never called. Set it to an empty array to count them too. |
| `Order` | `int` | `-1` | Filter order. |

There are two constructors: `CircuitBreaker(failureThreshold, windowSeconds, openSeconds)` and `CircuitBreaker(resource, failureThreshold, windowSeconds, openSeconds)`.

How it works: the attribute is a server filter and an election filter.

- Only failures that count open the circuit. By default every exception counts except three, in which the dependency was never called: `ConcurrencyLimitException` (a `Throttle` or `RateLimit` wait that timed out), `DistributedLockTimeoutException` (a lock that wasn't granted, such as the one of `DisableConcurrentExecution` or the circuit's own lock) and `RetryLaterException` (the job asked to run later). So six jobs waiting for a `[DisableConcurrentExecution]` lock don't open the circuit of a healthy service. Change the list with `ExceptOn`, or count only some exceptions with `OnlyOn`. A failure that doesn't count isn't recorded, and when the probe fails that way the circuit stays half-open: the next job becomes the probe, or the same job when it runs again.
- On every **Failed** election that counts (including failures that `AutomaticRetry` then turns into a retry) it records `{unix seconds}:{job id}` in the set `circuit:{resource}:failures`, drops entries older than the window, lists the resource in the set `circuits`, and stores the window in the `WindowSeconds` field of the hash `circuit:{resource}`. When the live count reaches the threshold, it writes `State = Open`, `OpenedAt` and `OpenUntil` (when the open period ends) to the hash. All of this happens under the distributed lock `circuit:{resource}:lock` and is committed at once, before the job's own state change: failures that happen at the same moment on several servers are all counted, and the circuit opens, and alerts, once. A state change that is retried after an error (the worker retries it with the same Failed state) doesn't count the failure again, and finds the circuit already open.
- In `OnPerforming`, while the circuit is open, it moves the job back to **Scheduled** for the end of the open period plus up to a second of random jitter, with a reason like `Circuit 'payments' is open (5 failures); retry at 14:32:10 UTC`. A rescheduled job keeps its id and does not use up a retry attempt.
- The first job that starts after the open period becomes the probe. Under the distributed lock `circuit:{resource}:lock` it reads the circuit again, so only one job wins even when several servers pick up jobs at the same moment, and writes `State = HalfOpen` with its job id in the `Probe` field.
- While the probe is in the Processing state, every other job on the resource is rescheduled for a tenth of `openSeconds` (at least 5 seconds, at most `openSeconds`, plus jitter), so the backlog starts soon after the probe succeeds, with a reason like `Circuit 'payments' is half-open and job '42' is probing it; retry at 14:34:10 UTC`.
- If the probe stops running without deciding, because it was deleted or another filter rescheduled it, the next job takes its place. If the probe's server crashes, the job comes back with the same id and runs again as the probe.
- A storage error while recording the probe or rescheduling a job is tried again twice (after 200 ms and 1 s), so a passing error doesn't fail a job that never ran. Both writes are safe to repeat. If all three attempts fail, the run fails with that error and the job's retry policy decides; with `[AutomaticRetry(Attempts = 0)]` the job ends Failed without having run.
- The probe's **Succeeded** election closes the circuit and clears the failures. Its failure opens the circuit again, including a failure that `AutomaticRetry` turns into a retry, unless the failure doesn't count (see above). Jobs that started before the circuit opened and finish while it is half-open don't change its state; their failures are still recorded.
- When the circuit opens, a `CircuitOpened` alert is raised if alert sinks are configured ([Alerts and webhooks](../monitoring/alerts-and-webhooks.md)). It is sent once the job's own state change is over and its lock released, so a slow sink doesn't hold up the state change, and it is sent even when that state change fails, because the open circuit is already stored.
- A closed circuit leaves the `circuits` set, and its hash is removed, at the first successful run that comes when it has had no failure for `windowSeconds` and changed state at least `windowSeconds` and `openSeconds` ago. This is housekeeping: if the storage fails, a warning is logged, the job still succeeds, and a later successful run tries again. So the dashboard lists circuits that are open, half-open, recently closed, or have recent failures, and per-value templates such as `"partner-{0}"` don't fill the storage. When the storage supports `JobStorageFeatures.ExtendedApi` (SQL Server does), the failure set expires a window after the last failure, and the hash of a closed circuit a day after that period; an open or half-open circuit is kept. A resource that never runs again keeps its entry in `circuits` (and, without the extended API, its hash); reset it to remove it.

Open circuits are listed on the dashboard's Insights page, where you can reset them ([Insights](../monitoring/insights.md)). From code:

```csharp
using Masterly.NightShift;

public static class CircuitAdmin
{
    public static string Describe(JobStorage storage)
    {
        using (var connection = storage.GetConnection())
        {
            var circuit = CircuitBreakerAttribute.ReadCircuit(connection, "payments");
            return $"{circuit.Resource}: {circuit.State}, {circuit.Failures} failure(s), since {circuit.OpenedAt:u}";
        }
    }

    public static void Close(JobStorage storage)
    {
        using (var connection = storage.GetConnection())
        {
            CircuitBreakerAttribute.Reset(connection, "payments");
        }
    }
}
```

`CircuitState.State` is one of `CircuitBreakerAttribute.ClosedState` (`"Closed"`), `OpenState` (`"Open"`) or `HalfOpenState` (`"HalfOpen"`). `CircuitState.OpenedAt` is when the circuit last changed state, and `CircuitState.ProbeJobId` is the id of the probe while the circuit is half-open (`null` otherwise). `CircuitState.OpenUntil` is when the open period of an open circuit ends (`null` when it isn't open, or was opened by an earlier version): after that the circuit is still `Open` in storage until the next job comes and becomes the probe. `CircuitState.Failures` counts the failures within the circuit's window.

`Reset` closes the circuit, forgets its failures and removes it from the `circuits` set, so it disappears from the dashboard until it fails again.

While the probe runs, each other job on the resource is rescheduled every tenth of `openSeconds` (5 seconds at least), which adds a Scheduled and an Enqueued state to its history each time. With a slow probe and many waiting jobs, choose an `openSeconds` long enough that this stays small.

### Protect one job method

```csharp
public class GeocodingService
{
    // No resource: the circuit belongs to GeocodingService.Geocode(int) alone.
    [CircuitBreaker(failureThreshold: 10, windowSeconds: 300, openSeconds: 600)]
    public Task Geocode(int addressId) => Task.CompletedTask;
}
```

### Count only failures to reach the dependency

```csharp
using System.Net.Http;

public class PartnerClient
{
    // A validation error in the job doesn't say anything about the partner's health: only network failures and
    // timeouts count. The default ExceptOn still leaves out lock and slot timeouts.
    [CircuitBreaker("partner", 5, 60, 300, OnlyOn = new[] { typeof(HttpRequestException), typeof(TimeoutException) })]
    public Task SendOrder(int orderId) => Task.CompletedTask;
}
```

`DistributedLockTimeoutException` derives from `TimeoutException`, and `ExceptOn` is checked after `OnlyOn`, so a lock that times out still doesn't count here.

## Which one should I use?

| Situation | Use |
|-----------|-----|
| Any failure might be temporary; try again later | `AutomaticRetry` (on by default) |
| The job knows when it may run again (`Retry-After`, a maintenance window) | Throw `RetryLaterException` |
| Short glitches that usually clear in a second (deadlocks, connection resets) | `RetryInProcess`, with `AutomaticRetry` behind it |
| A shared dependency is down and every job against it would fail | `CircuitBreaker` |
| Failures that retrying can't fix (bad input) | `AutomaticRetry` with `ExceptOn`, or `Attempts = 0` |

They combine. This job retries database glitches in place, stops when the ERP system keeps failing, and otherwise retries five times with back-off:

```csharp
public class ImportService
{
    [RetryInProcess(2, ExceptionTypes = new[] { typeof(DbException) })]
    [CircuitBreaker("erp", 5, 60, 300)]
    [AutomaticRetry(Attempts = 5)]
    public Task ImportOrders(string erpBatch) => Task.CompletedTask;
}
```

## Things to know

- Even with retries disabled, a job can run more than once: NightShift guarantees at-least-once processing, so a job interrupted by a crash or a shutdown is picked up again. Make jobs safe to repeat, or see [IdempotentCompletion](concurrency.md#idempotentcompletionattribute).
- A job that times out with [`JobTimeout`](timing.md#jobtimeoutattribute) fails with a `TimeoutException` and is retried like any other failure.
- `OnlyOn` and `ExceptOn` (of `AutomaticRetry` and of `CircuitBreaker`) look at the exception that failed the job, thrown by the job method or by a filter, not at a wrapper.
- The circuit breaker's failure window and open period use the clock of each server that records or checks them. Keep the clocks of all servers in sync (NTP), or failures recorded by a server whose clock is behind leave the window early.
- [`LatencyTimeout`](timing.md#latencytimeoutattribute) and [`Deadline`](timing.md#deadlineattribute) count from the job's creation, so long retry back-offs count against them.

## See also

- [Concurrency](concurrency.md)
- [Timing](timing.md)
- [Custom filters](custom-filters.md)
- [Managing jobs](../jobs/managing-jobs.md)
- [Dashboard pages](../dashboard/pages.md)
- [Alerts and webhooks](../monitoring/alerts-and-webhooks.md)
