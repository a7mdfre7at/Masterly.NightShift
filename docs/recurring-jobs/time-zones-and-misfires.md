# Time zones and misfires

A recurring job's CRON expression is evaluated in a time zone, UTC unless you choose another. This page explains how to run a schedule in local time, what happens when the clocks change for daylight saving time, and what the scheduler does with runs that were missed because no server was running (misfires).

## Quick example

```csharp
using Masterly.NightShift;

// 06:00 Berlin time, all year round, catching up every missed run after downtime
recurringJobs.AddOrUpdate<ReportService>(
    "daily-report-berlin",
    x => x.BuildDaily(),
    Cron.Daily(6),
    new RecurringJobOptions
    {
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
        MisfireHandling = MisfireHandlingMode.Strict,
    });
```

## How it works

When a job is registered, the manager stores the time zone's `Id` (`Europe/Berlin`) next to the CRON expression. Whenever a next run is computed (at registration, after each run, on `Resume` and `UpdateSchedule`), the time zone is looked up again by that ID through an `ITimeZoneResolver`, the CRON expression is evaluated in local time of that zone, and the result is stored as a UTC instant. The scheduler compares UTC instants only, using the storage's clock when the storage has one (SQL Server does), so the time zone and clock settings of the machines running your servers don't matter.

The recurring job scheduler wakes up every `SchedulePollingInterval` (15 seconds by default; at the start of every minute when it is zero) and picks the jobs whose next run is due. For each one it walks the occurrences of the CRON expression from the last run up to now. If the only one is the run that just came due, it is created as usual. If there are older ones, those runs were missed (the servers were stopped, or no server ran the scheduler), and the job's `MisfireHandling` decides what happens to them.

## Options / API

### Time zone

| Where | Member | Default | Meaning |
|-------|--------|---------|---------|
| `RecurringJobOptions` | `TimeZone` (`TimeZoneInfo`) | `TimeZoneInfo.Utc` | The time zone the CRON expression is evaluated in. |
| `IRecurringJobManagerV3.UpdateSchedule` | `timeZone` (`TimeZoneInfo?`) | `null` (keep) | Changes the time zone of an existing job. See [Pausing and updating](pausing-and-updating.md). |
| `GlobalConfiguration.Configuration` | `UseTimeZoneResolver(ITimeZoneResolver? resolver)` | `DefaultTimeZoneResolver`; with `AddNightShift`, a custom `ITimeZoneResolver` registered in the container | The resolver used where none is given: by the static `RecurringJob` class, by a `RecurringJobManager` created without one, by `RecurringJobManifest.Apply(json)`, by a `BackgroundJobServer` whose `TimeZoneResolver` option is `null`, and by a dashboard whose `TimeZoneResolver` option is `null`. `null` restores `DefaultTimeZoneResolver`. |
| `BackgroundJobServerOptions` | `TimeZoneResolver` (`ITimeZoneResolver?`) | `null` (the global resolver) | Resolver the server's recurring job scheduler uses. `AddNightShiftServer` fills it from the container when you leave it `null`. |
| `DashboardOptions` | `TimeZoneResolver` (`ITimeZoneResolver`) | `null` (the global resolver) | Resolver the dashboard uses for previews, edits and the forecast. `UseNightShiftDashboard` and `MapNightShiftDashboard` fill it from the container when you leave it `null`. |
| `RecurringJobManager` constructor | `ITimeZoneResolver timeZoneResolver` | the global resolver | Resolver the manager uses to compute next runs when registering or changing jobs, and that `RecurringJobManifest.Apply` uses for the manifest's time zones. |

`ITimeZoneResolver` has one method, `TimeZoneInfo GetTimeZoneById(string timeZoneId)`. `DefaultTimeZoneResolver` calls `TimeZoneInfo.FindSystemTimeZoneById`. `AddNightShift` registers it as the container's `ITimeZoneResolver`; register your own to replace it. "The global resolver" is the one set with `UseTimeZoneResolver`, or `DefaultTimeZoneResolver` when you didn't set one. When you register your own `ITimeZoneResolver` in the container, `AddNightShift` makes it the global resolver before your configuration callback runs; the default registration leaves the global resolver as it is.

### MisfireHandlingMode

| Value | Number | Missed runs | `Time` parameter of the created job |
|-------|--------|-------------|-------------------------------------|
| `Relaxed` (default) | 0 | All missed runs together become **one** run, created as soon as the scheduler notices them. | The moment the scheduler created it. |
| `Strict` | 1 | **One run per missed occurrence**, created as soon as the scheduler notices them, at most 100 per scheduler pass (the following passes, which start right away, create the rest). | The occurrence it stands for. |
| `Ignorable` | 2 | **None.** Missed runs are dropped; the job waits for its next occurrence. | The occurrence. |

Set it with `RecurringJobOptions.MisfireHandling`, change it with `UpdateSchedule(id, null, misfireHandling: ...)`, or pick it in the dashboard's **Edit** dialog. Only the three values above are accepted: `AddOrUpdate` and `UpdateSchedule` throw `ArgumentOutOfRangeException` for any other number (such as `(MisfireHandlingMode)7`), and a manifest entry with one is reported as an error. The `Time` job parameter (Unix seconds) and the `RecurringJobId` parameter are on every created job; see [Creating recurring jobs](creating-recurring-jobs.md#knowing-which-run-you-are).

## Misfire handling

### A timeline

A job `charge-subscriptions` runs hourly (`0 * * * *`, UTC). It last ran at 09:00. All servers are stopped from 09:10 to 12:20 for a deployment. The first scheduler pass after the restart happens at 12:20:05.

```
09:00      10:00      11:00      12:00  12:20:05      13:00
  |----------x----------x----------x------|-------------|
  ran        missed     missed     missed  scheduler    next
                                           wakes up     occurrence
```

| Mode | Jobs created at 12:20:05 | `Time` of each | Last execution | Next run |
|------|--------------------------|----------------|----------------|----------|
| `Relaxed` | 1 | 12:20:05 | 12:20:05 | 13:00 |
| `Strict` | 3 | 10:00, 11:00, 12:00 | 12:00 | 13:00 |
| `Ignorable` | 0 | | 09:00 (unchanged) | 13:00 |

Choose:

- `Relaxed` when the job's work doesn't depend on how many runs there were: rebuilding a cache, syncing "everything since last time", sending a digest. One late run brings things up to date.
- `Strict` when each run stands for its own period and the job reads the `Time` parameter to know which one: hourly billing, per-day exports, "close the books for this date".
- `Ignorable` when a late run is worthless or harmful: "send the 9 o'clock reminder" at noon, refreshing a cache that the next run refreshes anyway.

### Normal operation

The scheduler polls every 15 seconds, so even with healthy servers a run is usually noticed a few seconds after its occurrence. That is not treated as a misfire:

- `Relaxed` creates the run; its `Time` is the moment it was noticed, a few seconds after the occurrence.
- `Strict` creates the run with `Time` set to the exact occurrence.
- `Ignorable` creates the run as long as it is noticed within twice the `SchedulePollingInterval` (30 seconds by default) of the occurrence; later than that, it is dropped as missed. With `SchedulePollingInterval = TimeSpan.Zero` the scheduler wakes up once a minute, so the tolerance is two minutes.

### Downtime

The same rules apply however long the servers were down. Missed runs are counted from the latest of: the job's last execution (or, if it never ran, when it was created), the last `Resume`, the last change of its CRON expression, time zone or start and end dates (through `AddOrUpdate`, `UpdateSchedule` or the dashboard's **Edit**), and its `StartAt`. So runs missed while a job was [paused](pausing-and-updating.md) are never created, a new schedule never creates the runs it would have had before it was set, and no run is created for an occurrence before `StartAt`, whatever the mode.

`Strict` creates at most 100 runs of a job in one scheduler pass. When more were missed (a job that runs every minute, after a few hours of downtime), the job stays due, and the following passes, which start right away, create the next 100 and so on until it has caught up. No run is lost; they are only created in batches, each in its own storage transaction.

### When creating a run fails

If the scheduler can't create or enqueue a run (a client or state filter throws, the storage fails), the runs enqueued before it in the same pass are kept, and the job's last execution stays at the last of them. The scheduler tries the job again on its next poll and counts missed runs from there, so the failed run and the ones after it are created again, following the job's misfire handling: `Strict` creates each of them, `Relaxed` creates one run, and `Ignorable` creates the ones still within its tolerance. After 5 retries that fail too, the job is disabled, with the error shown on the dashboard. A run that was created but not enqueued has no state and appears nowhere; its data expires after 30 days.

## Start and end dates

`RecurringJobOptions.StartAt` and `EndAt` limit a job to a window, for example "every day at 09:00 until 31 December" or "every hour starting next Monday". See [Creating recurring jobs](creating-recurring-jobs.md#run-only-between-two-dates) for an example.

The window bounds the **occurrences** of the CRON expression, both ends included:

- **Before `StartAt`.** The next execution is the first occurrence at or after `StartAt`, so the scheduler doesn't even look at the job before then. No mode catches up on occurrences before `StartAt`, even after downtime: missed runs are counted from `StartAt` at the earliest.
- **After `EndAt`.** When the next occurrence would be later than `EndAt`, the next execution is cleared and the job leaves the schedule. It stays registered: it is listed on the dashboard, `GetRecurringJobs` returns it, and you can trigger, update or remove it.
- **Misfire handling** works as usual inside the window, and only occurrences inside it count as missed. If the servers were down across `EndAt`, `Strict` creates one run per missed occurrence up to `EndAt` (their `Time` is inside the window), `Relaxed` creates one run (its `Time` is when the scheduler noticed, which can be after `EndAt`) only when at least one missed occurrence is inside the window, and `Ignorable` creates none. An occurrence exactly at `EndAt` that the scheduler notices a few seconds late is created in every mode, like any other occurrence (for `Ignorable`, within twice the polling interval).
- **Manual triggers** are not limited: `Trigger`, `TriggerJob` and the dashboard's **Trigger now** create a run before `StartAt` or after `EndAt` too. The job's schedule doesn't change.

The timeline above with `EndAt` set to 11:30 and the servers down from 09:10 to 12:20:

| Mode | Jobs created at 12:20:05 | `Time` of each | Next run |
|------|--------------------------|----------------|----------|
| `Relaxed` | 1 | 12:20:05 | none |
| `Strict` | 2 | 10:00, 11:00 | none |
| `Ignorable` | 0 | | none |

### How the window relates to the time zone

`StartAt` and `EndAt` are `DateTimeOffset` values: absolute instants, stored in UTC to the millisecond. `TimeZone` decides when the occurrences fall in local time; the window is compared with those occurrences as instants. So the same window means the same moments whatever the time zone, and a daylight saving change doesn't move it. To start or end at a local wall-clock time, build the value with that zone's offset at that date, `new DateTimeOffset(local, timeZone.GetUtcOffset(local))`. An occurrence that daylight saving time moves (02:30 on the spring day runs at 03:00) is inside the window when the moved instant is.

### Changing the window

`AddOrUpdate` with other dates (or without them) replaces the window and recomputes the next execution from now, like a CRON change: clearing an end date that passed puts the job back on the schedule, and `Strict` doesn't create runs from before the change. `UpdateSchedule`, the dashboard's **Edit**, `Pause` and `Resume` keep the window, and the next execution they compute respects it.

### During a rolling upgrade

The window is stored in two new fields of the recurring job (`StartAt` and `EndAt`). Servers of earlier versions ignore them and keep scheduling the job as if it had no window, so while old and new servers share a storage, an old server can create runs before `StartAt` or after `EndAt`. The window is enforced once every server that runs the recurring job scheduler is on this version. When a next execution an old server computed outside the window comes due, a new server doesn't create the run: it moves the next execution to the first occurrence at or after `StartAt`, or clears it after `EndAt`.

## Daylight saving time

UTC has no daylight saving time, so jobs in the default time zone run at the same UTC times all year. In a time zone with daylight saving, the clock skips an hour in spring and repeats one in autumn. CRON times are wall-clock times, and the Cronos library that evaluates them handles the two transitions like this (observed in `America/New_York`, where 02:00 becomes 03:00 on the spring day and 02:00 goes back to 01:00 on the autumn day):

| Schedule | Spring (02:00-02:59 doesn't exist) | Autumn (01:00-01:59 happens twice) |
|----------|-----------------------------------|------------------------------------|
| `30 2 * * *` (daily 02:30) | Runs once at 03:00, right after the jump | Unaffected |
| `30 1 * * *` (daily 01:30) | Unaffected | Runs **once**, at the first 01:30 (daylight time) |
| `0 * * * *` (hourly) | 01:00, 03:00: the 02:00 run doesn't exist | 01:00 daylight, 01:00 standard, 02:00: runs **twice** in the repeated hour |
| `*/30 * * * *` (every 30 minutes) | 01:30, 03:00, 03:30: 02:00 and 02:30 are skipped | Both passes through 01:00 and 01:30 run |
| `30 * * * *` (hourly at :30) | 01:30, 03:00, 03:30: the 02:30 run moves to 03:00 | Both 01:30s run |

So a daily job never runs twice and never gets lost, but it can run at an unusual time on the spring day. Jobs that run every hour or more often follow the wall clock: one run fewer in spring and one more in autumn. If a job must run exactly every hour in real time, keep it in UTC. Use [`Cron.GetNextOccurrences`](cron-and-schedules.md#other-cron-members) to check a schedule around a transition date before relying on it.

## More examples

### A custom time zone resolver

The resolver lets every server, the dashboard and the manager agree on time zone IDs that the operating system doesn't know. This one understands fixed offsets written as `UTC+03:00`:

```csharp
using System.Globalization;
using Masterly.NightShift;

public sealed class FixedOffsetTimeZoneResolver : ITimeZoneResolver
{
    public TimeZoneInfo GetTimeZoneById(string timeZoneId)
    {
        if (timeZoneId.Length == 9 && timeZoneId.StartsWith("UTC", StringComparison.Ordinal)
            && TimeSpan.TryParseExact(timeZoneId.Substring(4), @"hh\:mm", CultureInfo.InvariantCulture, out var offset))
        {
            var utcOffset = timeZoneId[3] == '-' ? offset.Negate() : offset;
            return TimeZoneInfo.CreateCustomTimeZone(timeZoneId, utcOffset, timeZoneId, timeZoneId);
        }

        return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }
}
```

Register it in the container. The injected manager, the server and the dashboard pick it up, and `AddNightShift` also makes it the global resolver, so the static `RecurringJob` class and `RecurringJobManifest.Apply(json)` use it too:

```csharp
builder.Services.AddSingleton<ITimeZoneResolver, FixedOffsetTimeZoneResolver>();
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;TrustServerCertificate=True;"));
builder.Services.AddNightShiftServer();
```

Then register jobs with a `TimeZoneInfo` whose `Id` is that string:

```csharp
var resolver = app.Services.GetRequiredService<ITimeZoneResolver>();
var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();

recurringJobs.AddOrUpdate<ReportService>(
    "daily-report-riyadh",
    x => x.BuildDaily(),
    Cron.Daily(6),
    new RecurringJobOptions { TimeZone = resolver.GetTimeZoneById("UTC+03:00") });
```

Without a container, set it once in the global configuration. The static `RecurringJob` class, `new RecurringJobManager(storage)`, `new BackgroundJobServer()`, `RecurringJobManifest.Apply(json)` and the dashboard then use it:

```csharp
var resolver = new FixedOffsetTimeZoneResolver();

GlobalConfiguration.Configuration
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;TrustServerCertificate=True;")
    .UseTimeZoneResolver(resolver);

RecurringJob.AddOrUpdate<ReportService>(
    "daily-report-riyadh",
    x => x.BuildDaily(),
    Cron.Daily(6),
    new RecurringJobOptions { TimeZone = resolver.GetTimeZoneById("UTC+03:00") });

var server = new BackgroundJobServer();
```

A resolver passed explicitly (`BackgroundJobServerOptions.TimeZoneResolver`, `DashboardOptions.TimeZoneResolver`, the `RecurringJobManager` constructor) wins over the global one.

### Strict and Ignorable side by side

```csharp
// Each hour is billed separately: never lose an hour
recurringJobs.AddOrUpdate<BillingService>(
    "charge-subscriptions",
    x => x.ChargeSubscriptions(),
    Cron.Hourly(),
    new RecurringJobOptions { MisfireHandling = MisfireHandlingMode.Strict });

// The next refresh makes up for a missed one
recurringJobs.AddOrUpdate<CacheService>(
    "refresh-cache",
    x => x.Refresh(),
    Cron.MinuteInterval(5),
    new RecurringJobOptions { MisfireHandling = MisfireHandlingMode.Ignorable });
```

## Things to know

- **Use time zone IDs every server can resolve.** The stored ID is resolved on whichever server runs the scheduler. On .NET 8 and later, `FindSystemTimeZoneById` accepts both IANA IDs (`Europe/Berlin`) and Windows IDs (`W. Europe Standard Time`) on Windows and Linux, as long as ICU is available (the default; not in invariant globalization mode). .NET Framework only knows Windows IDs. In a mixed fleet, pick IDs all of them understand, or install the same custom resolver everywhere. If a server can't resolve the ID, scheduling fails for that job; it is retried a few times and then disabled with the error shown on the dashboard.
- **Avoid `TimeZoneInfo.Local`.** Its ID depends on the machine that registered the job, and other servers may not know it or may be set differently.
- **A custom resolver must reach every part that resolves IDs.** With `AddNightShift`, register it in the container: that is enough, and calling `UseTimeZoneResolver` in the `AddNightShift` callback instead would change only the static `RecurringJob` class and `RecurringJobManifest.Apply(json)`, not the server or the injected managers. Without a container, `UseTimeZoneResolver` covers the static `RecurringJob` class, managers, servers and dashboards created without a resolver, and `RecurringJobManifest.Apply(json)`. A part that falls back to `DefaultTimeZoneResolver` fails for IDs only your resolver knows: registering throws, and the scheduler retries the job and then disables it.
- **`Strict` still creates every missed run.** It creates them in batches of 100 per pass, but a job that runs every minute creates one job per missed minute: after a weekend of downtime that is several thousand jobs, and as many runs to process. Use `Strict` for schedules with few runs per day.
- **Downtime and lateness are visible.** The dashboard marks a recurring job **Late** when its next run is more than a minute overdue, and the health endpoint reports it. See [Dashboard pages](../dashboard/pages.md).

## See also

- [Creating recurring jobs](creating-recurring-jobs.md)
- [CRON and schedules](cron-and-schedules.md)
- [Pausing and updating](pausing-and-updating.md)
- [Server options](../server/server-options.md)
- [Dependency injection](../server/dependency-injection.md)
- [Dashboard setup](../dashboard/setup.md)
