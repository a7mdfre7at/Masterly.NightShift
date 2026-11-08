# Pausing and updating recurring jobs

`Pause`, `Resume` and `UpdateSchedule` change a recurring job that already exists without touching the method it runs. Use them to stop a job during an incident or a maintenance window, or to move its schedule, time zone or queue from an admin screen or configuration, without redeploying. The dashboard's **Pause**, **Resume** and **Edit** buttons call the same methods.

## Quick example

```csharp
using Masterly.NightShift;

recurringJobs.Pause("weekly-digest");                     // out of the schedule until resumed
recurringJobs.Resume("weekly-digest");                    // back on the schedule, from now
recurringJobs.UpdateSchedule("weekly-digest", "0 8 * * 1"); // Mondays at 08:00, same method
```

`recurringJobs` is the `IRecurringJobManager` from the service container. Without dependency injection, use the static facade:

```csharp
RecurringJob.Pause("weekly-digest");
RecurringJob.Resume("weekly-digest");
RecurringJob.UpdateSchedule("weekly-digest", Cron.Weekly(DayOfWeek.Monday, 8));
```

## How it works

The three operations are defined on `IRecurringJobManagerV3`, which `RecurringJobManager` implements. Extension methods in `RecurringJobManagerExtensions` make them available on any `IRecurringJobManager`; they forward to `IRecurringJobManagerV3` and throw `NotSupportedException` when the manager doesn't implement it (a manager from another package, or a test double). The manager registered by `AddNightShift` and the static `RecurringJob` class both support them.

Each call takes the recurring job's distributed lock, reads the stored job, changes it and writes back only what changed.

**Pause** sets the job's `Paused` flag, clears its next execution (so the scheduler no longer picks it up), and clears any scheduling error and retry count. The definition, the last execution and the history stay. While paused:

- the scheduler creates no runs;
- `Trigger` and the dashboard's **Trigger now** still create a run, and the job stays paused;
- `AddOrUpdate` with the same ID updates the definition but doesn't resume the job, so a redeploy that re-registers all jobs keeps it paused;
- the dashboard shows a **Paused** badge.

**Resume** clears the flag, records the resume time and computes the next execution from now. Runs that would have happened while the job was paused are never created, whatever the job's misfire handling mode, because the scheduler doesn't look for missed runs before the resume time. Resuming a job that isn't paused does nothing.

**UpdateSchedule** changes the CRON expression, the time zone, the queue and the misfire handling mode. Arguments left `null` keep their current value. When the CRON expression or the time zone actually changes (or the job was disabled by errors), the next execution is recomputed from now and the error state is cleared; changing only the queue or misfire mode keeps the next execution. A schedule change also records when it happened, and the scheduler never looks for missed runs before that moment, so `Strict` misfire handling doesn't create the runs the new expression would have had before the change. A paused job stays paused and out of the schedule; the new schedule applies when it is resumed. `UpdateSchedule` keeps the job's [start and end dates](time-zones-and-misfires.md#start-and-end-dates); change them with `AddOrUpdate`. `Resume` respects them too: a job resumed before its `StartAt` waits for the first occurrence at or after it, and one resumed after its `EndAt` stays off the schedule.

On storages that support per-job queues (SQL Server does), a new queue other than `default` also becomes the job's own queue, as it is for a job registered with `AddOrUpdate(id, queue, ...)`, so `AutomaticRetry` retries the runs on that queue too. On other storages the queue applies only to the first enqueue of each run; retries go to `default` unless the method has `[Queue]`.

## Options / API

| Member | Returns | Meaning |
|--------|---------|---------|
| `Pause(string recurringJobId)` | `bool` | Takes the job out of the schedule. `false` when the job doesn't exist. |
| `Resume(string recurringJobId)` | `bool` | Puts a paused job back on the schedule from now, without catching up. `false` when the job doesn't exist (also `true`, with no change, when it exists but isn't paused). |
| `UpdateSchedule(string recurringJobId, string? cronExpression, TimeZoneInfo? timeZone = null, string? queue = null, MisfireHandlingMode? misfireHandling = null)` | `bool` | Changes when and where the job runs. `false` when the job doesn't exist. |

They exist as instance methods on `RecurringJobManager` (`IRecurringJobManagerV3`), as extension methods on `IRecurringJobManager`, and as static methods on `RecurringJob`. A `null` `recurringJobId` throws `ArgumentNullException`.

`UpdateSchedule` parameters:

| Parameter | Type | Default | Meaning |
|-----------|------|---------|---------|
| `recurringJobId` | `string` | required | The job to change. |
| `cronExpression` | `string?` | required, may be `null` | New CRON expression, or `null` to keep the current one. Must be CRON: an invalid expression (including plain English) throws `ArgumentException` before anything is written. Wrap English in `Cron.FromText(...)`. |
| `timeZone` | `TimeZoneInfo?` | `null` | New time zone, or `null` to keep it. See [Time zones and misfires](time-zones-and-misfires.md). |
| `queue` | `string?` | `null` | New queue, or `null` to keep it. Lowercase letters, digits, `_` and `-` only, otherwise `ArgumentException`. Retries use it too on storages that support per-job queues (see above). |
| `misfireHandling` | `MisfireHandlingMode?` | `null` | New misfire handling, or `null` to keep it. A value that isn't `Relaxed`, `Strict` or `Ignorable` throws `ArgumentOutOfRangeException` before anything is written. See [misfire handling](time-zones-and-misfires.md#misfire-handling). |

## More examples

### Pause and resume from an admin endpoint

```csharp
app.MapPost("/admin/recurring-jobs/{id}/pause", (string id, IRecurringJobManager recurringJobs) =>
    recurringJobs.Pause(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/admin/recurring-jobs/{id}/resume", (string id, IRecurringJobManager recurringJobs) =>
    recurringJobs.Resume(id) ? Results.NoContent() : Results.NotFound());
```

Protect such endpoints with your own authorization.

### Change only part of the schedule

```csharp
// A plain-English schedule: convert it first
recurringJobs.UpdateSchedule(
    "weekly-digest",
    Cron.FromText("every 2 hours between 8 and 18 on weekdays"));

// Only the time zone
recurringJobs.UpdateSchedule(
    "weekly-digest",
    cronExpression: null,
    timeZone: TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

// Only the queue
recurringJobs.UpdateSchedule("weekly-digest", null, queue: "low");

// Only the misfire handling
recurringJobs.UpdateSchedule("weekly-digest", null, misfireHandling: MisfireHandlingMode.Ignorable);
```

### Take the schedule from configuration

```csharp
string? cron = configuration["Jobs:WeeklyDigest:Cron"];
if (cron != null && !recurringJobs.UpdateSchedule("weekly-digest", cron))
{
    Console.WriteLine("weekly-digest is not registered yet.");
}
```

`UpdateSchedule` only changes jobs that exist. If the job may not be registered yet, call `AddOrUpdate` with the configured expression instead.

### Pause everything for a maintenance window

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Storage;

public static List<string> PauseAll(JobStorage storage, IRecurringJobManager recurringJobs)
{
    var paused = new List<string>();

    using (var connection = storage.GetConnection())
    {
        foreach (var recurringJob in connection.GetRecurringJobs())
        {
            if (!recurringJob.Paused && recurringJobs.Pause(recurringJob.Id))
            {
                paused.Add(recurringJob.Id);
            }
        }
    }

    return paused;
}

public static void ResumeAll(IRecurringJobManager recurringJobs, IEnumerable<string> paused)
{
    foreach (var id in paused)
    {
        recurringJobs.Resume(id);
    }
}
```

Keeping the list of IDs you paused means jobs that were already paused before the window stay paused afterwards. `RecurringJobDto.Paused` tells you whether a stored job is paused. To stop all processing (workers and schedulers) rather than only recurring jobs, see [Drain mode](../server/drain-mode.md).

### From the dashboard

On the **Recurring Jobs** page, select jobs and use **Pause** or **Resume**, or open **Edit** on a job to change its schedule, time zone, queue and misfire handling. The schedule field accepts CRON or plain English (anything [`Cron.FromText`](cron-and-schedules.md#plain-english-schedules-cronfromtext) understands) and previews the next five runs before you save. Read-only dashboard users can't do either. See [Dashboard pages](../dashboard/pages.md).

## Things to know

- **`AddOrUpdate` on start-up overwrites `UpdateSchedule` changes.** If your application registers the job with a CRON expression on every start, the next deploy puts that expression back. Keep the schedule in one place: either code (and change it there), or configuration/dashboard (and register with the stored value, or use a [manifest](recurring-job-manifest.md)). The paused flag is the exception: `AddOrUpdate` never resumes a job.
- **A manifest changes the paused state only where it says so.** `RecurringJobManifest.Apply` pauses a job whose entry says `"paused": true`, resumes one that says `"paused": false`, and leaves the others as they are. Exports write the field for every job, so applying an export brings back the paused flags it was taken with. See [Recurring job manifest](recurring-job-manifest.md).
- **Resume never catches up.** If you need the runs that were missed while paused, trigger the job by hand after resuming.
- **`default` doesn't move a job off the queue it was registered with.** A job registered with an explicit queue (the `AddOrUpdate(id, queue, ...)` overloads) carries that queue in its definition, while its recurring job's queue stays `default`. `UpdateSchedule` with any other queue replaces the job's queue, and a later `UpdateSchedule(id, null, queue: "default")` moves the job back to `default`. Passing `default` while the job still has the queue it was registered with changes nothing, because the job's own queue wins over `default`; that way the dashboard's **Edit** dialog, which sends the recurring job's queue with every change, never moves such a job. To move it to `default`, register it again without a queue. A `[Queue]` attribute on the method, or a filter that changes the queue, still applies on top.

## See also

- [Creating recurring jobs](creating-recurring-jobs.md)
- [CRON and schedules](cron-and-schedules.md)
- [Time zones and misfires](time-zones-and-misfires.md)
- [Recurring job manifest](recurring-job-manifest.md)
- [Drain mode](../server/drain-mode.md)
- [Dashboard pages](../dashboard/pages.md)
