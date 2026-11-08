# Timing

These filters deal with time: how long a job may run (`JobTimeout`), when it should be done by (`Deadline`), how long it may wait before it is pointless (`LatencyTimeout`), what time of day it may start (`RunWindow`, `Blackout`), and how long its data is kept after it finishes (`ExpireAfter`).

## Quick example

```csharp
using Masterly.NightShift;

public class ReportService
{
    // Canceled and failed if it runs longer than 10 minutes.
    [JobTimeout(600)]
    public async Task BuildDaily(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
    }
}

jobs.Enqueue<ReportService>(x => x.BuildDaily(CancellationToken.None));
```

Pass `CancellationToken.None` when you create the job; the server substitutes a real token when it runs it (see [Cancellation](../jobs/cancellation.md)).

## Which one should I use?

| Attribute | Counts from | What happens | Cancels the job? |
|-----------|-------------|--------------|------------------|
| `JobTimeout` | The start of the run | The job's cancellation token fires; the job fails with `TimeoutException` and is retried | Yes, if the method observes its token |
| `Deadline` | The job's creation | Nothing is stopped; late jobs are flagged in the dashboard and a late finish is recorded and alerted | No |
| `LatencyTimeout` | The job's creation | A job picked up too late is deleted instead of run | It never starts |
| `RunWindow` / `Blackout` | The clock | A job picked up at the wrong time of day is rescheduled for the next allowed moment | It never starts until allowed |
| `ExpireAfter` | The job's end | Changes how long a finished job is kept in storage; with `IncludeFailed`, also a failed one | No |

## JobTimeoutAttribute

Cancels a job that runs longer than the given number of seconds and fails it with a `TimeoutException` (message: `Timed out after 10 minutes (JobTimeout)`). `AutomaticRetry` then retries it like any other failure.

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `timeoutInSeconds` (constructor) | `int` | required | Maximum run time in seconds. From 1 to `JobTimeoutAttribute.MaxTimeoutInSeconds` (2,147,483 seconds, about 24.8 days); other values throw `ArgumentOutOfRangeException` when the attribute is read. |
| `Timeout` | `TimeSpan` | | The timeout as a `TimeSpan`. |
| `Order` | `int` | `0` | Position among interceptors; lower runs outermost. |

Related members: `JobTimeoutAttribute.TimeoutExceededParameter` (`"TimeoutExceeded"`) and `MaxTimeoutInSeconds` (`2147483`). The limit is the longest a timer accepts on .NET Framework (`Int32.MaxValue` milliseconds), and it applies on every framework so that a job behaves the same everywhere. For longer work, use a [`Deadline`](#deadlineattribute), which cancels nothing.

How it works: `JobTimeoutAttribute` is an interceptor (`AsyncJobInterceptorAttribute`), so it runs inside the performer around the method call, without a storage round trip. It creates a token that is canceled after the timeout, when the server shuts down, or when the token already passed to the method is canceled (an outer `JobTimeout`, for example), and passes it to the method in place of every `CancellationToken` parameter. An `IJobCancellationToken` parameter is wrapped so that `ThrowIfCancellationRequested()` and its `ShutdownToken` also see the timeout. When the method throws `OperationCanceledException` because this timeout expired (not because of shutdown, because the job was deleted, or because another timeout expired first), the interceptor throws `TimeoutException` instead.

The method must accept a cancellation parameter and observe it. A method without one can't be interrupted: it runs to the end and succeeds, and the overrun is recorded in the `TimeoutExceeded` job parameter. If the storage fails to record it, a warning is logged and the job still succeeds.

### With IJobCancellationToken

```csharp
public class ReportService
{
    [JobTimeout(1800)]
    public void ExportLedger(int year, IJobCancellationToken cancellationToken)
    {
        for (var month = 1; month <= 12; month++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportMonth(year, month);
        }
    }

    private static void ExportMonth(int year, int month) { }
}
```

### Pass the token down

```csharp
using System.IO;
using System.Net.Http;

public class ImageService
{
    private readonly HttpClient _http;

    public ImageService(HttpClient http)
    {
        _http = http;
    }

    [JobTimeout(120)]
    public async Task DownloadOriginal(string url, string path, CancellationToken cancellationToken)
    {
        var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }
}
```

## DeadlineAttribute

Gives a job a deadline counted from its creation. It is a service-level expectation, not a timeout: nothing is canceled. The dashboard's Processing page and job details page highlight jobs that are still waiting or running past their deadline, and a job that finishes late keeps a record of how late it was.

```csharp
using Masterly.NightShift;

public class OrderService
{
    // Order confirmations should be out within 10 minutes of the order.
    [Deadline("00:10:00")]
    public Task ConfirmOrder(int orderId) => Task.CompletedTask;

    // The same, in seconds.
    [Deadline(3600)]
    public Task SendInvoice(int orderId) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `timeout` (constructor) | `string` | required | Time after creation, parsed as a `TimeSpan` with the invariant culture: `"00:10:00"`, `"1.00:00:00"`. Must be greater than zero. |
| `timeoutInSeconds` (constructor) | `int` | required | The same, in seconds. Must be greater than zero. |
| `Timeout` | `TimeSpan` | | The parsed deadline. |
| `Order` | `int` | `-1` | Filter order. |

Related members: `DeadlineAttribute.DeadlineAtParameter` (`"DeadlineAt"`), `DeadlineMissedParameter` (`"DeadlineMissed"`) and `ReadDeadline(connection, jobId)`, which returns a job's deadline or `null`.

How it works: a client filter and an election filter. `OnCreating` stores creation time plus `Timeout` (UTC) in the `DeadlineAt` job parameter. When **Succeeded** is elected after that moment, the filter stores how late the job was (a `TimeSpan`) in `DeadlineMissed` and raises a `DeadlineMissed` alert if alert sinks are configured ([Alerts and webhooks](../monitoring/alerts-and-webhooks.md)). If the storage fails to store `DeadlineMissed`, a warning is logged and the job still succeeds; the alert is still raised. Jobs that end Failed or Deleted are not marked.

### Deadline plus a hard limit

```csharp
public class OrderService
{
    // Should be done within 5 minutes of the order; a single run may take at most 4.
    [Deadline("00:05:00")]
    [JobTimeout(240)]
    public Task ReserveStock(int orderId, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

## LatencyTimeoutAttribute

Deletes a job that waited too long before a worker picked it up. Use it for work that is useless when late: a flash-sale alert, a one-time code, a live notification.

```csharp
using Masterly.NightShift;

public class NotificationService
{
    // Not worth sending if it couldn't start within 5 minutes of being created.
    [LatencyTimeout(300)]
    public Task SendFlashSaleAlert(int userId, int saleId) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `timeoutInSeconds` (constructor) | `int` | required | Maximum time between creation and start, in seconds. `0` allowed; negative values throw. |
| `TimeoutInSeconds` | `int` | | The value as given. |
| `LogLevel` | `Masterly.NightShift.Logging.LogLevel` | `Debug` | Level of the log message written when a job is deleted. |
| `Order` | `int` | `-1` | Filter order. |

How it works: an election filter. When a worker tries to move the job to **Processing** and more than `TimeoutInSeconds` have passed since the job's creation time, the candidate becomes **Deleted** with the reason `Background job has exceeded latency timeout of 300 second(s)`, and the job never runs.

### Log deletions as warnings

In ASP.NET Core, `LogLevel` clashes with `Microsoft.Extensions.Logging.LogLevel`, so use an alias:

```csharp
using Masterly.NightShift;
using NightShiftLogLevel = Masterly.NightShift.Logging.LogLevel;

public class NotificationService
{
    [LatencyTimeout(60, LogLevel = NightShiftLogLevel.Warn)]
    [AutomaticRetry(Attempts = 0)]
    public Task SendOneTimeCode(int userId) => Task.CompletedTask;
}
```

## RunWindowAttribute and BlackoutAttribute

`[RunWindow]` lets a job start only inside a daily time window; `[Blackout]` keeps it from starting inside one. A job picked up at the wrong time goes back to the **Scheduled** state for the next allowed moment, with the reason in its history, so heavy work waits for the night and customer-facing work waits for office hours without any code in the job.

```csharp
using Masterly.NightShift;

public class WarehouseService
{
    // Only at night, Berlin time. Overnight windows (end before start) are fine.
    [RunWindow("22:00-06:00", "Europe/Berlin")]
    public Task RebuildAggregates() => Task.CompletedTask;

    // Only in office hours (UTC), Monday to Friday.
    [RunWindow("09:00-17:00", WeekdaysOnly = true)]
    public Task CallSupplier(int supplierId) => Task.CompletedTask;

    // Never during the nightly database maintenance (UTC).
    [Blackout("02:00-04:00")]
    public Task ImportOrders() => Task.CompletedTask;

    // Spare the trading day in New York; at the weekend it may run at any time.
    [Blackout("09:30-16:00", "America/New_York", WeekdaysOnly = true)]
    public Task RecalculatePortfolio(int portfolioId) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `window` (constructor) | `string` | required | `start-end`, each as `HH:mm`, `H:mm`, `HH:mm:ss` or a bare hour (`"22-6"`). The start is inclusive, the end exclusive. Start and end must differ. |
| `timeZoneId` (constructor) | `string?` | `null` (UTC) | Time zone the window is expressed in, looked up with `TimeZoneInfo.FindSystemTimeZoneById`. |
| `Window` | `string` | | The window as given. |
| `Start`, `End` | `TimeSpan` | | The parsed times of day. `End` is before `Start` for an overnight window. |
| `TimeZoneId` | `string` | `"UTC"` | The resolved time zone id. |
| `WeekdaysOnly` | `bool` | `false` | Limits the rule to Monday to Friday, in the window's time zone. `RunWindow`: also keeps the job off Saturdays and Sundays. `Blackout`: the blackout applies on weekdays only, and the job may start at any time at the weekend. The day is the one of the moment checked, so the hours after midnight of an overnight window belong to the next day: `[Blackout("22:00-06:00", WeekdaysOnly = true)]` blocks Friday 22:00 to midnight and Monday 00:00 to 06:00, but not Saturday morning. |
| `Order` | `int` | `15` | Filter order. |

How it works: an election filter. When the candidate state is **Processing**, the filter checks the current time in the window's time zone. If the job may not start, the candidate becomes **Scheduled** for the next allowed moment, with a reason such as `Outside the run window 22:00-06:00 (Europe/Berlin); runs at 2026-11-06 21:00 UTC` or `Blackout 02:00-04:00 (UTC); runs at ...`. Daylight-saving changes are handled: a window that opens inside the skipped hour starts at the end of the gap, and a window that opens inside the hour repeated when clocks go back starts at the first of the two moments that is still ahead (the daylight-saving one, then the standard one), so the job neither starts an hour late nor skips the night. `GetNextStart(utcNow, out bool allowedNow)` is public, so you can ask the same question from code:

```csharp
var window = new RunWindowAttribute("22:00-06:00", "Europe/Berlin");
DateTime next = window.GetNextStart(DateTime.UtcNow, out var allowedNow);
```

`RunWindow` and `Blackout` are different types, so you can put both on one method, for example a night window with a blackout for a backup inside it.

## ExpireAfterAttribute

Sets how long a job's data is kept after it finishes, per method, instead of the storage-wide default (`JobStorage.JobExpirationTimeout`, one day unless you changed it with `WithJobExpirationTimeout`).

```csharp
using Masterly.NightShift;

public class AuditService
{
    // Audit-relevant: keep for a month.
    [ExpireAfter(days: 30)]
    public Task RecordLogin(int userId) => Task.CompletedTask;

    // Chatty: drop after ten minutes.
    [ExpireAfter("00:10:00")]
    public Task PingHealthEndpoint() => Task.CompletedTask;

    // A week, written as d.hh:mm:ss.
    [ExpireAfter("7.00:00:00")]
    public Task ExportGdprReport(int userId) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `timeSpan` (constructor) | `string` | required | How long to keep finished jobs, as `hh:mm:ss` or `d.hh:mm:ss`. Must be positive and at most `ExpireAfterAttribute.MaxDays` (36,500 days, about 100 years); otherwise `ArgumentException`. |
| `days` (constructor) | `int` | required | The same, in days: from 1 to `MaxDays`; otherwise `ArgumentOutOfRangeException`. |
| `Duration` | `TimeSpan` | | The parsed duration. |
| `IncludeFailed` | `bool` | `false` | Also expires the job after `Duration` when it ends up **Failed**, that is when its retries are used up. |
| `Order` | `int` | `50` | Filter order. |

The limit exists because storages add the duration to the current time, and a longer one would overflow the date when the state change is saved.

How it works: an apply filter. When the new state is final (**Succeeded** or **Deleted**), it sets `ApplyStateContext.JobExpirationTimeout` in `OnStateUnapplied`, which runs before any filter's `OnStateApplied`. So everything that expires with the job uses the per-method value, whatever its filter order: the job's console lines (expired by the global `JobConsoleStateFilter`) and the job itself, which the state machine expires last.

### Failed jobs

By default a failed job never expires: it stays until someone retries or deletes it. Teams with flaky integrations can end up with thousands of them. Give them a retention period per method with `IncludeFailed`, or for every job with [`UseFailedJobRetention`](../configuration/global-configuration.md#filters-and-job-execution).

```csharp
using Masterly.NightShift;

// Every job: once its retries are used up, a failed job is kept for 30 days.
GlobalConfiguration.Configuration.UseFailedJobRetention(TimeSpan.FromDays(30));

public class PartnerSync
{
    // This method: succeeded and failed runs are both kept for a week.
    [AutomaticRetry(Attempts = 5)]
    [ExpireAfter(days: 7, IncludeFailed = true)]
    public Task PushOrders() => Task.CompletedTask;
}
```

- **Only a final failure expires.** While `AutomaticRetry` has attempts left, a failure becomes a retry (Scheduled), which never expires. The expiration is set when the job stays **Failed**, in the same transaction as that state.
- **Leaving Failed cancels it.** Requeue, reschedule or retry the job (dashboard or API) before it expires and it is kept again, like any job that isn't finished. Deleting it uses the normal expiration of deleted jobs.
- **The most specific setting wins.** `[ExpireAfter(..., IncludeFailed = true)]` on the method or class wins over `UseFailedJobRetention`. An `[ExpireAfter]` without `IncludeFailed` leaves failed jobs to `UseFailedJobRetention` (or keeps them, without it).
- **Everything goes.** The job, its history, parameters and console lines expire together. On SQL Server the Failed page and count read the job table, so an expired failed job disappears from both once the expiration manager deletes it; the `stats:failed` history counters are not affected.
- **Continuations and batches keep waiting.** A continuation of the failed job stays Awaiting when the parent expires, as it does while the parent stays failed (continuations start when the parent succeeds or is deleted). A batch keeps counting the job as failed. Retry or delete the job before it expires, or delete the continuation.
- **Other storages.** The job is expired with `IWriteOnlyTransaction.ExpireJob`, which every storage implements. A storage that keeps its own index of failed jobs and doesn't clean it up when a job expires may keep a stale entry there; check its documentation.

## Things to know

- **`LatencyTimeout` and `Deadline` count from creation.** For a job scheduled an hour ahead, an hour has already passed when it becomes due; `[LatencyTimeout(600)]` on it would delete it every time. Retry back-offs, rescheduling by [`Throttle`](concurrency.md#throttleattribute) or `RunWindow`, and time spent in a paused queue all count too.
- **`JobTimeout` needs a cancellation parameter.** Without one the job is not stopped, only marked with `TimeoutExceeded`.
- **`JobTimeout` and `RetryInProcess` nest by `Order`.** Both default to `Order = 0`; set `Order` to choose which one is outermost. With the timeout outside, it covers all in-process attempts; inside, each attempt gets its own timeout, and a `TimeoutException` is retried in process because it is not an `OperationCanceledException`.
- **Interceptors are not de-duplicated.** A `[JobTimeout]` on the class and another on the method both apply, nested, with a `CancellationToken` parameter as with an `IJobCancellationToken` one: the token the method gets is canceled by whichever timeout expires first, and the `TimeoutException` names that one (`Timed out after 1 seconds (JobTimeout)`). For a method without a cancellation parameter, `TimeoutExceeded` records the shorter timeout that was exceeded.
- **Time zone ids must exist on the server.** An unknown id throws `TimeZoneNotFoundException` when the attribute is read, which breaks creating and processing the job. On .NET 8 and later both IANA (`Europe/Berlin`) and Windows (`W. Europe Standard Time`) ids usually work; on .NET Framework use Windows ids. See [Time zones and misfires](../recurring-jobs/time-zones-and-misfires.md).
- **A rescheduled job doesn't use a retry attempt.** `RunWindow` moves the job to Scheduled during election, before it runs.
- **A rescheduled job goes back to its queue.** When a job's queue came only from its Enqueued state (`new EnqueuedState("emails")`), `RunWindow` remembers it in the `HeldQueue` job parameter, like a job held by [drain mode](../server/drain-mode.md), and puts the job back in that queue when the window opens, also when `DrainModeFilter` was removed from the global filters. For a job requeued while it ran (still in the Processing state), the queue is read from the last Enqueued state in its history.

## See also

- [Retries](retries.md)
- [Concurrency](concurrency.md)
- [Cancellation](../jobs/cancellation.md)
- [Delayed jobs](../jobs/delayed-jobs.md)
- [Alerts and webhooks](../monitoring/alerts-and-webhooks.md)
- [Dashboard pages](../dashboard/pages.md)
- [Time zones and misfires](../recurring-jobs/time-zones-and-misfires.md)
