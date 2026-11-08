# Creating recurring jobs

`AddOrUpdate` registers a method call under a recurring job ID with a CRON schedule. If a recurring job with that ID already exists, it is updated instead, so you can call `AddOrUpdate` every time your application starts.

## Quick example

```csharp
using Masterly.NightShift;

public class ReportService
{
    public void BuildDaily()
    {
        // build and send the report
    }
}

// In Program.cs, after builder.Build():
var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();

recurringJobs.AddOrUpdate<ReportService>(
    "daily-report",                  // the recurring job ID
    service => service.BuildDaily(), // what to run
    Cron.Daily(6));                  // when: every day at 06:00 UTC
```

The same with the static facade, for code that doesn't use dependency injection:

```csharp
RecurringJob.AddOrUpdate<ReportService>("daily-report", service => service.BuildDaily(), Cron.Daily(6));
```

`IRecurringJobManager` is registered by `AddNightShift` (see [Dependency injection](../server/dependency-injection.md)). The static `RecurringJob` class uses a `RecurringJobManager` over `JobStorage.Current`, which `AddNightShift` or `GlobalConfiguration.Configuration.UseSqlServerStorage(...)` sets, with the time zone resolver set by `UseTimeZoneResolver` (see [Time zones and misfires](time-zones-and-misfires.md#a-custom-time-zone-resolver)).

## How it works

`AddOrUpdate` doesn't run anything. It validates the CRON expression, then, under a distributed lock for that ID, writes the recurring job to storage: a hash `recurring-job:{id}` with the serialized method call, the CRON expression, the time zone, the queue, the misfire handling mode, the start and end dates when you set them, and the next execution time, plus an entry in the `recurring-jobs` set scored by that next execution time.

The `RecurringJobScheduler` process, which every `BackgroundJobServer` runs unless it is a lightweight server, polls that set (every `SchedulePollingInterval`, 15 seconds by default). For each recurring job that is due, it creates an ordinary background job from the stored method call, moves it to the Enqueued state in the recurring job's queue, records the run as the last execution and computes the next one. From there the job goes through the normal lifecycle (Enqueued, Processing, Succeeded or Failed) and shows up on the dashboard like any other job. Several servers can run the scheduler at once; distributed locks make sure each run is created once.

Each created job gets two job parameters: `RecurringJobId` (the ID) and `Time` (the scheduled time, as Unix seconds). See [Knowing which run you are](#knowing-which-run-you-are) below.

If the scheduler can't create a job (the type or method no longer exists, the arguments can't be deserialized, the time zone is unknown), it logs a warning and retries the recurring job up to 5 times, one polling interval apart (one minute when the interval is zero). After that the recurring job is disabled: it keeps its definition, its next execution is cleared, and the dashboard shows the error. Calling `AddOrUpdate` again with a working definition puts it back on the schedule.

## Options / API

### AddOrUpdate overloads

The core method is `IRecurringJobManager.AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)`. Extension methods on `IRecurringJobManager` (`RecurringJobManagerExtensions`) and static methods on `RecurringJob` take a lambda instead of a `Job`. Both offer the same shapes:

| Overload | Notes |
|----------|-------|
| `AddOrUpdate(id, methodCall, cronExpression)` | Runs in the time zone `UTC`, default queue, `Relaxed` misfire handling. |
| `AddOrUpdate(id, methodCall, cronExpression, options)` | With `RecurringJobOptions`. |
| `AddOrUpdate(id, queue, methodCall, cronExpression)` | Enqueues every run to `queue`. |
| `AddOrUpdate(id, queue, methodCall, cronExpression, options)` | Queue and options. |
| `IRecurringJobManager.AddOrUpdate(id, job, cronExpression)` | Extension that takes a `Job` you built yourself, with default options. |

`methodCall` can be any of these, so static, instance, synchronous and `async` methods all work:

| Lambda type | Example |
|-------------|---------|
| `Expression<Action>` | `() => Console.WriteLine("Still alive")` |
| `Expression<Action<T>>` | `service => service.BuildDaily()` with `AddOrUpdate<ReportService>` |
| `Expression<Func<Task>>` | `() => CacheWarmer.WarmAsync()` |
| `Expression<Func<T, Task>>` | `service => service.SendDigestAsync()` with `AddOrUpdate<EmailService>` |

`cronExpression` is a `string`, or a `Func<string>` (so `Cron.Daily` can be passed as a method group). A `CronBuilder` converts to `string` implicitly, so `Cron.Builder()...` can be passed directly.

The overloads without a recurring job ID, and the ones that take `TimeZoneInfo timeZone` and `string queue` as trailing parameters, are obsolete. The ID-less overloads derive the ID from the method, as `{Type}.{Method}` (for example `ReportService.BuildDaily`), so two registrations of the same method with different arguments overwrite each other. Always pass an ID.

### Parameters

| Parameter | Type | Default | Meaning |
|-----------|------|---------|---------|
| `recurringJobId` | `string` | required | The key of the recurring job. Registering the same ID again updates that job. |
| `queue` | `string` | `"default"` | The queue every run is enqueued to. Lowercase letters, digits, `_` and `-` only. Needs a storage that supports per-job queues (SQL Server does); others throw `NotSupportedException`, use `[Queue]` there instead. |
| `methodCall` | expression | required | The method to call and its arguments. Arguments are evaluated and serialized once, when you call `AddOrUpdate`. |
| `cronExpression` | `string` or `Func<string>` | required | A 5-field or 6-field (with seconds) CRON expression, or a `@` macro. See [CRON and schedules](cron-and-schedules.md). An invalid expression throws `ArgumentException`; the inner exception says what is wrong. |
| `options` | `RecurringJobOptions` | `new RecurringJobOptions()` | Time zone, misfire handling, and start and end dates. |

### RecurringJobOptions

| Property | Type | Default | Meaning |
|----------|------|---------|---------|
| `TimeZone` | `TimeZoneInfo` | `TimeZoneInfo.Utc` | The time zone the CRON expression is evaluated in. Setting `null` throws `ArgumentNullException`. Its `Id` is what is stored; servers look it up again through `ITimeZoneResolver`. See [Time zones and misfires](time-zones-and-misfires.md). |
| `MisfireHandling` | `MisfireHandlingMode` | `Relaxed` | What to do with runs that were missed while no scheduler was running: `Relaxed` (one catch-up run), `Strict` (one run per missed occurrence) or `Ignorable` (skip them). Any other value makes `AddOrUpdate` throw `ArgumentOutOfRangeException`. See [Time zones and misfires](time-zones-and-misfires.md#misfire-handling). |
| `StartAt` | `DateTimeOffset?` | `null` (no start) | The instant before which the job doesn't run. The first run is the first occurrence at or after it (an occurrence exactly at `StartAt` runs), and no misfire handling mode catches up on occurrences before it. See [Start and end dates](#run-only-between-two-dates). |
| `EndAt` | `DateTimeOffset?` | `null` (no end) | The instant after which the job doesn't run. An occurrence exactly at `EndAt` runs. Once the next occurrence is later, the job leaves the schedule: its next execution is cleared, but it stays registered, so it is still listed and can be triggered, updated or removed. Must be later than `StartAt`. |
| `QueueName` | `string` | `"default"` | Obsolete. Use the overloads with the `queue` parameter. |

`StartAt` and `EndAt` are absolute instants. They are stored in UTC, to the millisecond, and `TimeZone` doesn't change them: it only decides when the CRON occurrences fall. `AddOrUpdate` throws `ArgumentException` when `EndAt` isn't later than `StartAt`, and `ArgumentOutOfRangeException` when either is before 1970-01-01 UTC. A `StartAt` or `EndAt` in the past is accepted: a job whose end has passed is registered without a next execution, so an application that registers its jobs on every start keeps starting after the end date.

### Triggering and removing

| Member | Returns | Meaning |
|--------|---------|---------|
| `IRecurringJobManager.Trigger(id)` | `void` | Creates and enqueues a run now, outside the schedule. Does nothing when the ID doesn't exist. |
| `IRecurringJobManagerV2.TriggerJob(id)` | `string?` | The same, returning the created background job's ID, or `null` when the recurring job doesn't exist or a client filter canceled the creation. |
| `RecurringJob.TriggerJob(id)` | `string?` | Static equivalent of `TriggerJob`. (`RecurringJob.Trigger` is obsolete.) |
| `IRecurringJobManager.RemoveIfExists(id)` / `RecurringJob.RemoveIfExists(id)` | `void` | Deletes the recurring job definition. Jobs it already created are not touched. Does nothing when the ID doesn't exist. |
| `IRecurringJobManagerV2.Storage` | `JobStorage` | The storage the manager writes to. |

Triggering a run sets the job's `Time` parameter and last execution to now, and computes the next execution from now. A paused job can be triggered; it stays paused. A job can also be triggered outside its start and end dates: the run is created, and the job keeps its schedule (no next execution after `EndAt`, the first occurrence at or after `StartAt` before it).

`RecurringJobManager` is the implementation of `IRecurringJobManager`, `IRecurringJobManagerV2` and `IRecurringJobManagerV3`. The container registers `IRecurringJobManager`, `IRecurringJobManagerV2` and `IRecurringJobManagerV3`. Outside a container, create it with `new RecurringJobManager(storage)`, which uses the time zone resolver set by `UseTimeZoneResolver` (`DefaultTimeZoneResolver` unless you set one), or `new RecurringJobManager(storage, filterProvider, timeZoneResolver)` to pass a custom filter provider and time zone resolver.

## Recurring job IDs

- The ID is the identity of the recurring job. Registering the same ID again updates it; registering a different ID creates a second, independent recurring job, even for the same method.
- Pick stable, readable IDs such as `daily-report` or `invoice-reminders-eu`. They appear on the dashboard, in logs, in the `RecurringJobId` job parameter and in [manifests](recurring-job-manifest.md).
- Renaming an ID in code doesn't rename the stored job: the old ID keeps running until you remove it with `RemoveIfExists`.
- On SQL Server the ID can be at most 86 characters: it is stored as `recurring-job:{id}` in a 100-character key column.

## What happens when you register the same ID again

`AddOrUpdate` with an existing ID overwrites the definition and keeps the history:

| Field | On update |
|-------|-----------|
| Method call and arguments | Replaced. |
| Queue, misfire handling | Replaced. |
| CRON expression, time zone | Replaced. If either one changed, the next execution is recomputed from now, so runs the old schedule would have made are not caught up, and `Strict` misfire handling doesn't create the runs the new schedule would have had before the change. If neither changed, the next execution stays as it was. |
| Start and end dates | Replaced. Options without them clear them. If either changed, the next execution is recomputed from now like a CRON change, so `Strict` doesn't create runs from before the change. Clearing an end date that has passed puts the job back on the schedule. |
| Paused flag | Kept. Re-registering a paused job doesn't resume it. |
| Error / disabled state | Cleared, and the job is scheduled again (unless paused). |
| Created time, last execution, last job ID | Kept. |

When nothing changed, `AddOrUpdate` writes nothing at all, so calling it on every start-up is cheap.

## More examples

### Run a job in a dedicated queue

```csharp
recurringJobs.AddOrUpdate<ReportService>(
    "daily-report",
    "reports",
    service => service.BuildDaily(),
    Cron.Daily(6));
```

The server must listen to the `reports` queue (`options.Queues = new[] { "reports", "default" }`). See [Queues and workers](../server/queues-and-workers.md).

### Run in a time zone and catch up every missed run

```csharp
recurringJobs.AddOrUpdate<ReportService>(
    "daily-report",
    service => service.BuildDaily(),
    "0 6 * * *",
    new RecurringJobOptions
    {
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
        MisfireHandling = MisfireHandlingMode.Strict,
    });
```

### Run only between two dates

Every day at 09:00 Berlin time, from next Monday until the end of the year:

```csharp
var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
var nextMonday = new DateTime(2026, 11, 16);
var endOfYear = new DateTime(2026, 12, 31, 23, 59, 59);

recurringJobs.AddOrUpdate<ReportService>(
    "daily-report-q4",
    service => service.BuildDaily(),
    "0 9 * * *",
    new RecurringJobOptions
    {
        TimeZone = berlin,
        StartAt = new DateTimeOffset(nextMonday, berlin.GetUtcOffset(nextMonday)),
        EndAt = new DateTimeOffset(endOfYear, berlin.GetUtcOffset(endOfYear)),
    });
```

The dates are instants, so give each one the offset of the zone you mean (here `berlin.GetUtcOffset(...)`, which is +01:00 in winter and +02:00 in summer). A `DateTimeOffset` built from a `DateTime` without an offset uses the offset of the machine running the code.

After the last run on 31 December the job stays on the dashboard, marked **Ended**, without a next execution. Register it again with a later `EndAt`, or without one, to put it back on the schedule, or remove it. `StartAt` and `EndAt` are respected only by servers of this version or later; see [Time zones and misfires](time-zones-and-misfires.md#start-and-end-dates).

### Async methods

```csharp
public class EmailService
{
    public Task SendDigestAsync() => Task.CompletedTask;
}

recurringJobs.AddOrUpdate<EmailService>(
    "weekly-digest",
    service => service.SendDigestAsync(),
    Cron.Weekly(DayOfWeek.Monday, 7, 30));
```

### Same method, different arguments

Arguments are part of the stored definition. Give each variant its own ID:

```csharp
public class InvoiceService
{
    public void SendReminders(string region, int daysOverdue) { }
}

recurringJobs.AddOrUpdate<InvoiceService>(
    "invoice-reminders-eu",
    service => service.SendReminders("eu", 14),
    Cron.Daily(9));

recurringJobs.AddOrUpdate<InvoiceService>(
    "invoice-reminders-us",
    service => service.SendReminders("us", 30),
    Cron.Daily(9));
```

The arguments are evaluated once, at registration. Don't pass values that should be fresh on every run, such as `DateTime.Now`; compute them inside the job instead. See [Job arguments](../jobs/job-arguments.md).

### Schedules from helpers, the builder or plain English

```csharp
recurringJobs.AddOrUpdate<CleanupService>(
    "purge-sessions",
    service => service.PurgeExpiredSessions(),
    Cron.FromText("every 15 minutes"));

recurringJobs.AddOrUpdate<ReportService>(
    "daily-report",
    service => service.BuildDaily(),
    Cron.Builder().At(6, 30).OnWeekdays());
```

### A static method without DI

```csharp
RecurringJob.AddOrUpdate("heartbeat", () => Console.WriteLine("Still alive"), Cron.Minutely());
```

### In a console app or Worker without a container

```csharp
using Masterly.NightShift;

GlobalConfiguration.Configuration
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;TrustServerCertificate=True;");

RecurringJob.AddOrUpdate<ReportService>("daily-report", service => service.BuildDaily(), Cron.Daily(6));

using (var server = new BackgroundJobServer())
{
    Console.ReadLine();
}
```

### Run it now

```csharp
recurringJobs.Trigger("daily-report");

// With IRecurringJobManagerV2 (also in the container), get the created job's ID:
string? jobId = recurringJobsV2.TriggerJob("daily-report");
if (jobId == null)
{
    Console.WriteLine("No such recurring job, or a client filter canceled the run.");
}

// Static facade:
string? staticJobId = RecurringJob.TriggerJob("daily-report");
```

The dashboard's **Trigger now** button does the same.

### Remove jobs that are no longer in the code

`RemoveIfExists` deletes one job. To clean up IDs you stopped registering, compare the stored jobs with the ones you know:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Storage;

public static void RemoveUnknown(JobStorage storage, IRecurringJobManager recurringJobs)
{
    var known = new HashSet<string> { "daily-report", "weekly-digest", "purge-sessions" };

    using (var connection = storage.GetConnection())
    {
        foreach (var recurringJob in connection.GetRecurringJobs())
        {
            if (!known.Contains(recurringJob.Id))
            {
                recurringJobs.RemoveIfExists(recurringJob.Id);
            }
        }
    }
}
```

`JobStorage` is registered in the container by `AddNightShift`. A [manifest](recurring-job-manifest.md) applied with `prune: true` does this for you.

### Knowing which run you are

Add a `PerformContext` parameter and pass `null` when registering; the server supplies the real context. The `Time` parameter is the scheduled time of the run (see [misfire handling](time-zones-and-misfires.md#misfire-handling) for what it holds after downtime):

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;

public class ScheduledReportService
{
    public void BuildDaily(PerformContext? context)
    {
        string? recurringJobId = context?.GetJobParameter<string>("RecurringJobId");
        long scheduledAt = context?.GetJobParameter<long>("Time") ?? 0;
        DateTimeOffset runFor = DateTimeOffset.FromUnixTimeSeconds(scheduledAt);

        Console.WriteLine($"{recurringJobId} runs for {runFor:u}");
    }
}

recurringJobs.AddOrUpdate<ScheduledReportService>(
    "daily-report",
    service => service.BuildDaily(null),
    Cron.Daily(6));
```

## Things to know

- **The scheduler runs only on full servers.** A server with `IsLightweightServer = true` doesn't run the recurring job scheduler. If no server runs it, nothing is created; the dashboard marks a job **Late** when its next run is more than a minute overdue. See [Server options](../server/server-options.md).
- **Runs start up to one polling interval late.** With the default `SchedulePollingInterval` of 15 seconds, a job due at 06:00:00 is enqueued by about 06:00:15, then waits for a free worker.
- **A recurring job doesn't wait for its previous run.** If a run takes longer than the interval, the next one is enqueued anyway and can run at the same time. To skip a run while the previous one is still enqueued, processing or waiting for a retry, put [`[PreventDuplicates]`](../filters/concurrency.md#skip-a-recurring-run-while-the-previous-one-is-still-waiting) on the job's method. To let the next run wait for the previous one instead, use [`[DisableConcurrentExecution]`](../filters/concurrency.md#disableconcurrentexecutionattribute), which holds a worker while it waits. Or use a longer interval.
- **Retries belong to the created job, not to the schedule.** A failed run is retried by [`AutomaticRetry`](../filters/retries.md) like any other job; the schedule moves on regardless.
- **Changing the code of the job is fine; renaming the class or method isn't.** The definition stores the type and method names. After a rename, register the job again so the stored definition points at the new names, otherwise the scheduler fails to load it and eventually disables it.
- **One broken recurring job doesn't stop the others.** When the scheduler can't read a stored recurring job (a damaged field, or a value only a newer version understands), it logs a warning, retries that job a few times (once per polling interval) and then disables it, with the error shown on the dashboard. The other recurring jobs keep running on time. To fix it, remove the job (`RemoveIfExists`, or **Remove** on the dashboard) and register it again.
- **Seconds-based CRON expressions are accepted**, but the scheduler only looks for due jobs once per polling interval. Occurrences that fall between two polls count as missed runs, so with the default `Relaxed` handling a `*/5 * * * * *` schedule runs about once every 15 seconds, and with `Strict` it creates the missed runs in batches. Recurring jobs are not meant for sub-minute precision.
- **Filters apply to the created jobs.** Client filters run when each run is created (a filter that cancels creation skips that run), and filter attributes on the job's class or method apply as usual. See [Filters](../filters/README.md).

## See also

- [CRON and schedules](cron-and-schedules.md)
- [Pausing and updating](pausing-and-updating.md)
- [Time zones and misfires](time-zones-and-misfires.md)
- [Recurring job manifest](recurring-job-manifest.md)
- [Delayed jobs](../jobs/delayed-jobs.md), for a one-off run at a later time
- [Managing jobs](../jobs/managing-jobs.md)
- [Dashboard pages](../dashboard/pages.md)
