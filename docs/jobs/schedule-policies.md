# Schedule policies

A schedule policy is a business calendar: the days, the times of day and the dates when jobs that follow it may start. Policies are stored by name, so every server, the code that declares them and the jobs that refer to them share one definition. A delayed or fire-and-forget job follows a policy through `ScheduleOptions`, and a recurring job through `RecurringJobOptions.Policy`. When a policy blocks a job's start time, the job is moved to the next allowed time, skipped or not created, as you choose. A recurring job doesn't run the occurrences its policy blocks.

Use a policy when the rule belongs to the business rather than to a method: "reminders go out on working days between 9 and 5", "nothing runs on public holidays", "no exports during the Sunday maintenance window". For a rule tied to one job method, the attributes [`[RunWindow]` and `[Blackout]`](../filters/timing.md) are simpler.

## Quick example

```csharp
using Masterly.NightShift;

JobStorage storage = JobStorage.Current;

storage.SaveSchedulePolicy(new SchedulePolicy("business-hours")
    .InTimeZone("Europe/Berlin")
    .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
    .AllowBetween("09:00", "17:00")
    .ExcludeAnnualDate(12, 25)
    .ExcludeAnnualDate(1, 1));

// Due in 4 hours; if that falls outside business hours, it runs at the next opening
BackgroundJob.Schedule<ReminderService>(
    x => x.SendTrialEnding(accountId),
    TimeSpan.FromHours(4),
    new ScheduleOptions { Policy = "business-hours" });

// Every hour, but only the occurrences inside business hours run
RecurringJob.AddOrUpdate<ReportService>(
    "hourly-report",
    x => x.Build(),
    "0 * * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), Policy = "business-hours" });
```

With a host, declare the policies with the recurring jobs, so they are saved at start-up before the jobs that use them (see [Declaring policies in code](#declaring-policies-in-code)).

## The rules

An instant is allowed when every rule allows it. A policy without rules allows every instant.

| Method | Rule |
|---|---|
| `InTimeZone(id)` / `InTimeZone(TimeZoneInfo)` | The time zone the rules are read in, Windows or IANA id. UTC by default |
| `AllowWeekdays(params DayOfWeek[])` | Only these days of the week. Every day when never called. Can be called several times |
| `AllowBetween("09:00", "17:00")` | Only inside this daily window, from the start included to the end excluded. An end before the start crosses midnight (`"22:00"`, `"06:00"`). Several windows are allowed; an instant inside any of them is |
| `ExcludeDate(DateOnly)` | Nothing on this date |
| `ExcludeAnnualDate(month, day)` | Nothing on this day of every year. `ExcludeAnnualDate(2, 29)` matches leap years only |
| `ExcludeDatesFrom("public-holidays")` | Nothing on the dates of the [date provider](#date-providers) registered under this name |
| `Require("trading")` | Also needs the stored policy `trading` to allow the instant |
| `ExceptWhen("maintenance")` | Blocks every instant the stored policy `maintenance` allows, and every instant it can't evaluate (see [Policies that can't be evaluated](#policies-that-cant-be-evaluated)) |
| `WithDescription(text)` | A description for the dashboard, up to 500 characters |

The day and the date are those of the instant checked, in the policy's time zone: in a window from 22:00 to 06:00, 02:00 belongs to the next day, so `AllowWeekdays(DayOfWeek.Friday)` with that window allows Friday from 00:00 to 06:00 and from 22:00 to midnight.

Names are 1 to 64 letters, digits, `.`, `_` and `-`, starting with a letter or a digit, and are lower-cased: `Business-Hours` and `business-hours` are the same policy. Date providers are named the same way.

A policy can't require itself or make an exception for itself, can't both require a policy and make an exception for it, and a chain of `Require` and `ExceptWhen` references can't lead back to it. Saving such a policy throws `ArgumentException`.

## Storing and reading policies

Policies are extension methods on `JobStorage`, in the `Masterly.NightShift` namespace.

| Method | What it does |
|---|---|
| `SaveSchedulePolicy(policy)` | Creates the policy or replaces the stored one, whatever its revision. Writes nothing when the rules are the same. Returns the revision |
| `UpdateSchedulePolicy(policy, expectedRevision)` | Replaces the stored policy only if it still has `expectedRevision` (0 to create one that must not exist yet). Returns the new revision, or `null` when someone changed, created or deleted it meanwhile |
| `DeleteSchedulePolicy(name, expectedRevision)` | Deletes it when the revision matches; `false` otherwise. Throws `InvalidOperationException` while a recurring job or another policy uses it |
| `GetSchedulePolicy(name)` | The stored policy with its `Revision` and `UpdatedAt`, or `null` |
| `GetSchedulePolicies()` | Every stored policy, by name |
| `EvaluateSchedulePolicy(name, instant)` | A `SchedulePolicyDecision`: `Allowed`, and the `Reason` when it isn't |
| `FindNextAllowed(name, from)` | The first allowed instant at or after `from`, or `null` when there is none in the next ten years |
| `PreviewSchedulePolicy(name, cron, timeZone, count)` | The next `count` occurrences of a CRON expression (20 by default, up to 1,000), each with its decision: what a recurring job with this schedule and policy would do |

Every change raises the revision by one, so two people editing the same policy don't overwrite each other's work: read it, change it, and pass the revision you read to `UpdateSchedulePolicy`.

```csharp
SchedulePolicy? current = storage.GetSchedulePolicy("business-hours");

SchedulePolicy changed = new SchedulePolicy("business-hours")
    .InTimeZone("Europe/Berlin")
    .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
    .AllowBetween("08:00", "18:00");

if (storage.UpdateSchedulePolicy(changed, current?.Revision ?? 0) == null)
{
    // someone else changed it: read it again
}
```

The reasons say which rule blocked the instant, for example `policy 'business-hours': Saturday isn't an allowed day`, `policy 'business-hours': 17:30 is outside 09:00-17:00 (Europe/Berlin)` or `policy 'reports' requires 'trading', and policy 'trading': 12-31 is excluded every year`.

```csharp
foreach (SchedulePolicyDecision run in storage.PreviewSchedulePolicy("business-hours", "0 */4 * * *", count: 6))
{
    Console.WriteLine($"{run.Instant:u} {(run.Allowed ? "runs" : "skipped: " + run.Reason)}");
}
```

## Delayed and fire-and-forget jobs

`Enqueue` and `Schedule` take a `ScheduleOptions`, as static methods on `BackgroundJob` and as extension methods on `IBackgroundJobClient`, for the four lambda types:

| Overload | Meaning |
|---|---|
| `Enqueue(methodCall, ScheduleOptions)` | Run now, if the policy allows it |
| `Schedule(methodCall, TimeSpan delay, ScheduleOptions)` | Run after `delay` |
| `Schedule(methodCall, DateTimeOffset enqueueAt, ScheduleOptions)` | Run at `enqueueAt` |

| `ScheduleOptions` member | Meaning |
|---|---|
| `Policy` | The name of the stored policy the job follows |
| `WhenBlocked` | What happens when the policy blocks the job's time: `MoveForward` (default) schedules it at the next allowed instant, `Skip` deletes it with the reason `Skipped: ...`, `Reject` doesn't create it, so `Enqueue` and `Schedule` return `null`. It applies to a time the policy blocks, not to a policy that [can't be evaluated](#policies-that-cant-be-evaluated): such a job is created whatever the value (`Enqueue` and `Schedule` return its id), and a server decides once the policy can be evaluated. Setting it without `Policy` throws `ArgumentException` when the job is created |
| `SkipAfter` | How late the job may start, at least one second: a job that starts later than this after its time is skipped, as [`[SkipIfLate]`](../filters/timing.md#skipiflateattribute) does |
| `Queue` | The job's queue, kept with the job (it needs a storage with `JobStorageFeatures.JobQueueProperty`, as SQL Server has) |

The policy is checked twice:

1. **When the job is created**, for the moment it is to run: now for `Enqueue`, the due time for `Schedule`. A job moved forward is created in the `Scheduled` state at the next allowed instant, with the reason `Moved by policy: ...`, and its `PerformContext.ScheduledFor` is that instant.
2. **When a delayed job is due**, because the policy may have changed meanwhile: a holiday added, the hours shortened. The delayed job scheduler moves it forward again, or skips it. A job created with `Reject` whose due time is now blocked is skipped, as it exists already.

A job enqueued by hand, with the dashboard's Enqueue now or `Requeue`, isn't checked, and neither is a job already waiting in its queue. Retries scheduled by `[AutomaticRetry]` are checked when they are due, like any delayed job.

Creating a job with a policy that isn't stored throws `InvalidOperationException`, as does `MoveForward` when the policy allows no time in the ten years after the job's time. Only a complete search ends this way: when the search meets an instant the policy [can't evaluate](#policies-that-cant-be-evaluated) before an allowed one, the job is moved to that instant and checked again when it is due. A due job that `MoveForward` finds no time for is scheduled a day later, with the reason `Moved by policy: '...' allows no time in the next ten years`, and checked again then, as the policy may change. A policy deleted after the job was created no longer applies to it. `ScheduleOptions` need a client that implements `IBackgroundJobClientV2`, as `BackgroundJobClient` does.

The options are job parameters (`Policy`, `WhenBlocked`, `SkipAfter`), so `Create(job, state, parameters)` with the same parameters has the same effect. The default global filter `SchedulePolicyFilter` applies the policy, and `LateStartFilter` the late-start limit.

## Recurring jobs

```csharp
RecurringJob.AddOrUpdate<ExportService>(
    "nightly-export",
    x => x.Export(),
    "0 2 * * *",
    new RecurringJobOptions { Policy = "trading-days" });
```

The scheduler leaves out the occurrences the policy blocks: they create no job, so a job that runs every minute doesn't fill the history with skipped runs over a weekend. The next run is the next occurrence the policy allows, so the dashboard shows when the job really runs next, and `storage.GetNextOccurrences(id, count)` lists only allowed occurrences.

- The policy must be stored before the job is registered; `AddOrUpdate` throws `ArgumentException` otherwise.
- The policy is checked for the occurrence, when it is due, not when its run starts. Use `SkipAfter` to limit how late a run may start.
- Changing a policy reschedules the recurring jobs that follow it, directly or through policies that require it or make an exception for it, as a new CRON expression does: they don't catch up on runs, whatever their misfire handling.
- With `Relaxed`, `Strict` or `RunLatest` misfire handling after an outage, the missed occurrences the policy blocks are left out too. `Relaxed` checks the policy again for the moment its catch-up run would be created: when the policy blocks that moment, the run waits for the next time the policy allows instead of running at once.
- A policy that allows none of the job's occurrences in the next ten years stops the job's schedule with an error on the dashboard. Saving the policy, or registering the job again, schedules it again. Only a search that could evaluate every instant it met ends this way: a policy that can't be evaluated keeps the job on the schedule (see [Policies that can't be evaluated](#policies-that-cant-be-evaluated)).
- `Trigger` (the dashboard's Trigger now) runs the job whatever the policy.
- `RecurringJobDto.Policy` and the manifest's `policy` field carry the policy's name.

Servers of earlier versions ignore the policy and run every occurrence.

## Declaring policies in code

`RecurringJobSet.AddPolicy` declares policies next to the recurring jobs. With `AddNightShiftRecurringJobs`, they are saved when the host starts, before the jobs:

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .AddPolicy(new SchedulePolicy("trading-days")
        .InTimeZone("America/New_York")
        .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
        .ExcludeDatesFrom("nyse-holidays"))
    .AddPolicy(new SchedulePolicy("market-hours").Require("trading-days").InTimeZone("America/New_York").AllowBetween("09:30", "16:00"))
    .Add<PriceService>("prices", x => x.Refresh(), "*/5 * * * *", new RecurringJobOptions { Policy = "market-hours" }));
```

- Every policy a declared policy or job refers to must be declared or already stored; a cycle of references is refused. This is checked before anything is written, so a broken declaration stops the start-up without changing the storage.
- A policy is saved after the policies it refers to, whatever the order of the declarations.
- An unchanged policy keeps its revision and writes nothing. A changed one replaces the stored one, also when someone edited it elsewhere, and reschedules the jobs that follow it.
- A policy no longer declared stays stored: delete it with `DeleteSchedulePolicy`.
- `RecurringJobSetResult.PoliciesSaved` lists the policies the call created or changed.

## Date providers

Dates that come from code or another system, such as public holidays or a trading calendar, come from an `IScheduleDateProvider`:

```csharp
public sealed class NyseHolidays : IScheduleDateProvider
{
    private readonly HashSet<DateOnly> _holidays = LoadHolidays();

    public bool Contains(DateOnly day) => _holidays.Contains(day);
}

GlobalConfiguration.Configuration.UseScheduleDateProvider("nyse-holidays", new NyseHolidays());

// or, with a container: created once, when the Masterly.NightShift configuration is
builder.Services.AddNightShiftScheduleDateProvider<NyseHolidays>("nyse-holidays");
```

- `Contains` receives the date in the time zone of the policy that asks. It is called often, from several threads, when the next allowed instant is searched: keep it fast, with the dates computed once.
- Register the provider in every process that creates jobs following such a policy and in every server. Where it is missing, the policy can't be evaluated, so it never lets a job start on a date it can't check (see [Policies that can't be evaluated](#policies-that-cant-be-evaluated)).
- A provider that throws makes the policy unable to evaluate the date checked, with the error in the reason. A search doesn't ask a provider again once it has failed, and the error is logged as a warning at most once a minute per provider.

## Policies that can't be evaluated

Sometimes a policy can't tell whether an instant is allowed: the stored policy can't be read, its time zone isn't known on this server, a [date provider](#date-providers) it uses isn't registered on this server or throws, or its references lead back to it. Such an instant is never allowed, but it isn't blocked for good either, because the answer may differ later or on another server.

- A rule that blocks the instant decides first: a failing date provider doesn't matter on a day the policy doesn't allow anyway.
- A policy whose `ExceptWhen` policy can't be evaluated can't be evaluated either: it doesn't let the instant through.
- `EvaluateSchedulePolicy` answers not allowed, with the reason, and `FindNextAllowed` returns `null` when it meets such an instant before an allowed one.
- A **recurring job** stays on the schedule: the occurrences don't run, and the scheduler looks at the job again at the first occurrence the policy couldn't evaluate or, when that time has come already, after its polling interval. Its `Error` on the dashboard says why, for example `Schedule policy 'trading-days' can't be evaluated, so the job waits and tries again: policy 'trading-days': date provider 'nyse-holidays' isn't registered on this server.`, and the scheduler logs the error as a warning when it first appears. Missed occurrences are counted from the job's last execution, so its misfire handling deals with them once the policy can be evaluated again. Registering the job again while it waits keeps it where it is, as long as its schedule is the same: `AddOrUpdate` with the same CRON expression, time zone, dates and policy, or `UpdateSchedule` that changes neither the CRON expression nor the time zone, doesn't compute the next execution from now, which would put the missed occurrences off until the next one still to come. A changed schedule computes it again, as always. A run created for an occurrence that was due while the job waited carries the `WaitedForPolicy` job parameter (`SkipIfLateAttribute.WaitedForPolicyParameter`), the policy's name: when `SkipAfter` or `[SkipIfLate]` skips it as late, its reason and a warning name the policy, as for [a job that waited for its policy](#jobs-that-wait-for-their-policy).
- A **fire-and-forget or delayed job** waits. It is never skipped or rejected, and creating it never throws, because its policy can't be evaluated, whatever its `WhenBlocked`: the policy blocks nothing, it can't tell yet.

### Jobs that wait for their policy

**When the job is created.** A delayed job keeps its time and is checked when it is due. A job that would run now (`Enqueue`) is created in the `Scheduled` state, due a minute later. With `WhenBlocked.Reject` too the job is created, so `Enqueue` and `Schedule` return its id, not `null`: a server decides once the policy can be evaluated, and skips the job when the policy blocks its time then.

**When the job is due.** It stays in the `Scheduled` state and is checked again after 1 minute, then 5, then 15, then every 60 minutes, without limit, until the policy can be evaluated. The reason of the state starts with `Waiting for policy, next check in 5 min: ` (with the minutes to the next check), followed by the error; the whole reason is cut to 100 characters. The `PolicyCheckAttempts` job parameter (`SchedulePolicyFilter.CheckAttemptsParameter`) holds the number of checks that failed in a row. Once the policy can be evaluated, the parameter is set to `0` when the policy lets the job run, and removed when the policy moves or skips the job. A warning is logged at most once a minute for each policy.

**Once the policy can be evaluated,** the job runs when the policy allows that moment. When the policy blocks it, `WhenBlocked` applies as for any due job: `MoveForward` moves it to the next allowed instant, `Skip` and `Reject` skip it.

- **Waiting isn't a retry.** The job didn't run, so the attempts of [`[AutomaticRetry]`](../filters/retries.md) aren't used up, and the retry count stays.
- **The time the job was meant to run stays.** [`PerformContext.ScheduledFor`](job-arguments.md#the-runs-scheduled-time-and-lateness) is still the job's due time, or the moment it was created for a job that would have run at once, so its `Lateness` includes the wait. `ScheduleOptions.SkipAfter` and [`[SkipIfLate]`](../filters/timing.md#skipiflateattribute) count from that time too: a job with a late-start limit that waited through a long outage is skipped as late when it finally starts. Set a limit only where a late run is worth nothing. The limit is the job's own rule and applies whatever made the job late, but the `Deleted` state's reason says what did: `Skipped: started 3 h late: waited for policy 'business-hours', which couldn't be evaluated`. The reason has at most 99 characters, so a long policy name is shortened in it; one warning with the whole name is logged. A job skipped this way runs when it is requeued by hand. When the policy, once it can be evaluated, moves the job forward, the new time becomes its `ScheduledFor`, as for any job moved forward.
- **A search that meets such an instant stops there.** With `MoveForward`, a blocked job whose search for the next allowed instant meets an instant the policy can't evaluate first is moved to that instant, with the usual reason `Moved by policy: ...`, at creation and when it is due. Every instant before it is blocked, and the policy is asked again when the job is due there. The error about no time in ten years is only for a search that could evaluate every instant.

## Things to know

- **Storage keys.** A policy is kept in the hash `schedule-policy:{name}` and listed in the set `schedule-policies`. A recurring job keeps its policy's name in the `Policy` field of its hash; a job in its `Policy` and `WhenBlocked` parameters, and in `PolicyCheckAttempts` once it has waited for a policy that couldn't be evaluated (the count while it waits, `0` after the policy let it run). A run of a recurring job that was due while the recurring job waited for its policy carries the policy's name in `WaitedForPolicy`. Every storage supports them. Saving and deleting take one distributed lock for all policies, `lock:schedule-policies`, because a save checks the policies it refers to and a delete the policies that refer to it.
- **Policies are read when they are needed**, one storage read per policy and per policy it refers to: when a job with a policy is created or due, and when a recurring job with a policy is scheduled. `SchedulePolicyFilter` keeps a policy it read for about 5 seconds per storage and name, so a changed policy can take a few seconds to apply to new jobs and due delayed jobs (a policy that didn't exist is read again each time). The recurring job scheduler reads each policy once per pass, and again for a job whose schedule changed in the last minute. For a job without a policy, the filter looks for the `Policy` parameter when the job is created and when a delayed job is due: a dictionary lookup on SQL Server, which loads the parameters with the job, and one storage read per due delayed job on a storage that doesn't.
- **Daylight saving time.** A window that opens inside the hour skipped when clocks go forward opens at the end of the gap; a window that opens inside the hour repeated when clocks go back opens the first time, and again when the hour repeats: the change of the UTC offset is a boundary of the search, so the second pass of the window is found too.
- **Searches stop after ten years.** A policy with no allowed instant in that time counts as allowing none.
- **`[RunWindow]` and `[Blackout]`** are checked when a worker picks the job up, from the method's attributes; a policy is checked when the job is created and when it is due, from storage.

## See also

- [Delayed jobs](delayed-jobs.md)
- [Creating recurring jobs](../recurring-jobs/creating-recurring-jobs.md)
- [Recurring jobs in code](../recurring-jobs/recurring-jobs-in-code.md)
- [Time zones and misfires](../recurring-jobs/time-zones-and-misfires.md)
- [Timing filters](../filters/timing.md)
