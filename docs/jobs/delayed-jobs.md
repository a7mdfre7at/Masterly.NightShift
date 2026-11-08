# Delayed jobs

A delayed job runs once, at or shortly after a moment you choose: in 11 days, at a given date and time, or at a moment written in plain English such as `"tomorrow at 9am"`. Use it for reminders, follow-ups, grace periods and anything that should happen later but only once. For work that repeats, use [Recurring jobs](../recurring-jobs/README.md).

## Quick example

```csharp
using Masterly.NightShift;

public class ReminderService
{
    public void SendTrialEnding(int accountId)
    {
        // send the "your trial ends in 3 days" e-mail
    }
}

app.MapPost("/accounts/{id:int}/trial", (int id, IBackgroundJobClient jobs) =>
{
    jobs.Schedule<ReminderService>(x => x.SendTrialEnding(id), TimeSpan.FromDays(11));
    return Results.NoContent();
});
```

With the static facade: `BackgroundJob.Schedule<ReminderService>(x => x.SendTrialEnding(accountId), TimeSpan.FromDays(11));`.

## How it works

1. `Schedule` captures the method call like `Enqueue` does (see [Job arguments](job-arguments.md)) and creates the job in the `Scheduled` state. `ScheduledState.EnqueueAt` holds the moment, in UTC. A `TimeSpan` is added to the current time when you call `Schedule`; a `DateTimeOffset` is converted to UTC; a phrase is parsed by `TimeText` right away.
2. The job's ID is added to the storage's schedule, sorted by that moment. Nothing runs yet.
3. The `DelayedJobScheduler` process, which every server runs, checks the schedule every `BackgroundJobServerOptions.SchedulePollingInterval` (15 seconds by default). When a job is due, it moves it to `Enqueued`, on the job's queue.
4. From there it is an ordinary [fire-and-forget job](fire-and-forget.md): a worker fetches it, runs it, and it ends up `Succeeded` or `Failed`.

So a delayed job starts no earlier than its moment, and usually within one polling interval after it, provided a server is running and a worker is free.

## API

### Schedule

Every overload returns `string?`, the new job ID or `null` when a client filter canceled the creation. The same overloads exist as static methods on `BackgroundJob` and as extension methods on `IBackgroundJobClient`. Each row exists for the four lambda types `Expression<Action>`, `Expression<Func<Task>>`, `Expression<Action<T>>` and `Expression<Func<T, Task>>` (see [Fire-and-forget jobs](fire-and-forget.md#enqueue) for what each one is for).

| Overload | Meaning |
|---|---|
| `Schedule(methodCall, TimeSpan delay)` | Enqueue after `delay`, counted from now |
| `Schedule(string queue, methodCall, TimeSpan delay)` | The same, on the given queue |
| `Schedule(methodCall, DateTimeOffset enqueueAt)` | Enqueue at `enqueueAt` (converted to UTC) |
| `Schedule(string queue, methodCall, DateTimeOffset enqueueAt)` | The same, on the given queue |
| `Schedule(methodCall, string when)` | Enqueue at the moment the phrase describes, read in `TimeText.DefaultTimeZone`. Throws `FormatException` when the phrase is not understood or lies in the past |
| `Schedule(string queue, methodCall, string when)` | The same, on the given queue |

To move a job that already exists to a new moment, use `Reschedule`, described in [Managing jobs](managing-jobs.md#delete-requeue-reschedule-changestate).

### ScheduledState

| Member | Type | Meaning |
|---|---|---|
| `ScheduledState(TimeSpan enqueueIn)` | constructor | `EnqueueAt` is now plus `enqueueIn`, computed when the state object is created |
| `ScheduledState(DateTime enqueueAt)` | constructor | `enqueueAt` must be in UTC |
| `EnqueueAt` | `DateTime` | When the job will be enqueued, UTC |
| `ScheduledAt` | `DateTime` | When the state object was created, UTC |
| `StateName` | `string` | `"Scheduled"` |

### TimeText

`TimeText` (in the `Masterly.NightShift` namespace) turns an English phrase into a UTC `DateTime`. `Schedule(..., string when)` and the dashboard's "run later" field use it.

| Member | Meaning |
|---|---|
| `DefaultTimeZone` (`TimeZoneInfo`, default `TimeZoneInfo.Local`) | The zone phrases such as "tomorrow at 9" are read in |
| `Parse(string text)` | Parses relative to now, in `DefaultTimeZone`. Returns UTC. Throws `FormatException` when the phrase is not understood or lies in the past |
| `Parse(string text, DateTime utcNow, TimeZoneInfo? timeZone = null)` | Parses relative to `utcNow`, in `timeZone` (or `DefaultTimeZone`) |
| `TryParse(string text, DateTime utcNow, TimeZoneInfo? timeZone, out DateTime result)` | Returns `false` instead of throwing |

Phrases are case-insensitive, and extra spaces are ignored, as are the words `on` and `the`. Examples below assume it is Wednesday 2026-11-04 14:30.

| Phrase | Result |
|---|---|
| `now`, `immediately`, `asap` | Now |
| `in 20 minutes`, `20 minutes`, `in an hour`, `in 90 seconds`, `2h from now` | Now plus the amount. Units: `s`, `sec(s)`, `second(s)`; `m`, `min(s)`, `minute(s)`; `h`, `hr(s)`, `hour(s)`; `d`, `day(s)`; `w`, `week(s)`; `month(s)`. Decimals are allowed (`in 1.5 hours`) |
| `in a month`, `in 2 months` | Calendar months later, at the same time of day (2026-12-04 14:30). The number must be whole: `in 1.5 months` is refused; use days or weeks for a part of a month |
| `tomorrow`, `today at 18:00`, `tomorrow at 9:30`, `tomorrow 17:45` | That day; the time defaults to 09:00 |
| `at 15:00`, `9 pm`, `at 9am` | That time today, or tomorrow if it already passed |
| `tonight`, `tonight at 23:00`, `this morning`, `this afternoon`, `this evening` | Today at 20:00 (or the time given), 09:00, 14:00 or 18:00. Once that time has passed the phrase is refused, like `today at 9am`; `tonight at midnight` is 00:00 tomorrow |
| `noon` / `midday`, `midnight` | 12:00 today, or tomorrow if it already passed; 00:00 tomorrow |
| `tomorrow morning`, `tomorrow afternoon`, `tomorrow evening`, `tomorrow evening at 19:15` | 09:00, 14:00, 18:00, or the time given |
| `tonight at 9`, `tomorrow evening at 7`, `friday evening at 8`, `this afternoon at 3`, `tomorrow morning at 10` | With `tonight`, `afternoon` or `evening`, an hour from 1 to 11 without `am` or `pm` is in the afternoon: 21:00, 19:00, 20:00, 15:00. `morning` keeps the hour (10:00) |
| `tonight at 12`, `tomorrow evening at 12`, `tomorrow evening at midnight`, `tomorrow afternoon at 12` | `12` or `midnight` with `tonight` or `evening` is the midnight that ends that evening (00:00 the next day, so `tomorrow evening at midnight` is 00:00 the day after tomorrow); `12` with `afternoon` is noon |
| `tomorrow evening at 7am`, `tomorrow evening at 19:15`, `tomorrow evening at 07:30` | An explicit `am` or `pm`, or a 24-hour time (13 to 23, or an hour written with a leading zero), is used as written |
| `friday`, `friday at 6pm`, `6pm friday`, `next monday at 8`, `this friday`, `mon` | The next occurrence of that weekday (09:00 unless a time is given) |
| `wednesday at 18:00`, `18:00 wednesday`, `wednesday evening` | Today's weekday name with a time still ahead today, wherever the time is in the phrase: today at that time (2026-11-04 18:00) |
| `wednesday`, `wednesday at 9am`, `next wednesday at 18:00` | Today's weekday name without a time, with a time that already passed, or after `next`: the same day next week (2026-11-11) |
| `next week`, `next month`, `next month at 7` | Next Monday 09:00, the first of next month 09:00, or the time given |
| `end of day`, `end of today`, `end of week`, `end of month`, `end of year` | 23:59 today, on Sunday, on the last day of the month, on 31 December |
| `2026-12-24`, `2026-12-24 08:00` | That date and time in the time zone (midnight when no time is given) |
| `2026-12-24T08:00:00Z`, `2026-12-24T08:00:00+01:00` | That moment, as given |

Phrases that are not understood (`yesterday`, `soonish`, `at 25:00`, `end of decade`) and moments in the past (`2020-01-01`, or `today at 9am` after 9am) are rejected. `Parse` says why in its `FormatException`: a moment in the past gives "The time '...' is in the past.", and `in 1.5 months` explains that months must be a whole number. Relative phrases more than ten years ahead (`in 99999999 days`, `in 121 months`) are rejected too: `TryParse` returns `false` and `Parse` throws `FormatException`.

## More examples

### At a specific moment

```csharp
public class ReminderService
{
    public Task SendInvoiceAsync(int invoiceId)
    {
        // send the invoice reminder
        return Task.CompletedTask;
    }
}

// three days before the due date
jobs.Schedule<ReminderService>(x => x.SendInvoiceAsync(invoiceId), dueDate.AddDays(-3));

// 09:00 in Berlin on 24 December, whatever the server's time zone
var nineInBerlin = new DateTimeOffset(2026, 12, 24, 9, 0, 0, TimeSpan.FromHours(1));
jobs.Schedule<ReminderService>(x => x.SendInvoiceAsync(invoiceId), nineInBerlin);
```

### In plain English

```csharp
jobs.Schedule<ReminderService>(x => x.SendTrialEnding(accountId), "tomorrow at 9am");
jobs.Schedule<ReportService>(x => x.BuildDaily(), "end of month");
jobs.Schedule<ReportService>(x => x.BuildDaily(), "in 2 hours");
```

Set the time zone once at startup, so "9am" means 9 in your users' zone and not in the server's:

```csharp
TimeText.DefaultTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
```

### Validating a phrase a user typed

```csharp
public static IResult ScheduleReminder(IBackgroundJobClient jobs, int accountId, string when)
{
    if (!TimeText.TryParse(when, DateTime.UtcNow, TimeZoneInfo.Utc, out DateTime enqueueAt))
    {
        return Results.BadRequest($"Can't understand '{when}'. Try \"in 20 minutes\" or \"tomorrow at 9am\".");
    }

    string? jobId = jobs.Schedule<ReminderService>(x => x.SendTrialEnding(accountId), new DateTimeOffset(enqueueAt));
    return Results.Ok(new { jobId, enqueueAt });
}
```

`TryParse` returns a UTC `DateTime`, so `new DateTimeOffset(enqueueAt)` has a zero offset.

### On a named queue

```csharp
jobs.Schedule<ReminderService>("billing", x => x.SendInvoiceAsync(invoiceId), TimeSpan.FromHours(1));
jobs.Schedule<ReminderService>("billing", x => x.SendInvoiceAsync(invoiceId), "tomorrow at 9am");
```

The queue is stored with the job and used when the job is enqueued.

### Pushing a reminder back

```csharp
using Masterly.NightShift.States;

bool moved = jobs.Reschedule(jobId, TimeSpan.FromHours(2), ScheduledState.StateName);
```

Passing `ScheduledState.StateName` as the expected state means the job is moved only if it is still waiting; `moved` is `false` when it already ran.

## Things to know

- **Precision is the polling interval.** Jobs are enqueued up to `SchedulePollingInterval` (15 seconds by default) after their moment, then wait for a free worker. Lower the interval on one server if you need tighter timing; see [Server options](../server/server-options.md).
- **A server must be running.** If no server runs at the scheduled moment, the job is enqueued as soon as one starts. It is not skipped.
- **Phrases are parsed once.** The phrase is turned into a UTC moment when you call `Schedule`; only that moment is stored.
- **Check the time zone.** `TimeText.DefaultTimeZone` defaults to the machine's local zone, which is often UTC on servers and containers. Set it, or pass a `DateTimeOffset` with an explicit offset. `"2026-12-24T08:00:00Z"` and other phrases with a `Z` or an offset ignore the time zone.
- **`m` means minutes.** Write `month` or `months` for months.
- **`ScheduledState(DateTime)` expects UTC.** Prefer the `DateTimeOffset` overloads of `Schedule`, which convert for you.
- **Deleting a scheduled job** before its moment cancels it: `jobs.Delete(jobId)`. See [Managing jobs](managing-jobs.md).

## See also

- [Fire-and-forget jobs](fire-and-forget.md)
- [Managing jobs](managing-jobs.md)
- [Recurring jobs](../recurring-jobs/README.md)
- [Cron and schedules](../recurring-jobs/cron-and-schedules.md)
- [Server options](../server/server-options.md)
