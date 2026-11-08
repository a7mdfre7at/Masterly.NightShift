# CRON and schedules

Every recurring job has a schedule written as a CRON expression. You can write the expression yourself, take one from the `Cron` helpers, build it step by step with `Cron.Builder()`, or turn a plain-English phrase into one with `Cron.FromText`. `Cron.Describe` goes the other way and explains an expression in English.

## Quick example

```csharp
using Masterly.NightShift;

// Raw CRON: 02:30 every day
recurringJobs.AddOrUpdate<ReportService>("nightly-report", x => x.BuildDaily(), "30 2 * * *");

// A helper: the same schedule
recurringJobs.AddOrUpdate<ReportService>("nightly-report", x => x.BuildDaily(), Cron.Daily(2, 30));

// The builder: every 15 minutes, 09:00-17:59, Monday to Friday
recurringJobs.AddOrUpdate<SyncService>("pull-orders", x => x.PullOrdersAsync(),
    Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays());

// Plain English
recurringJobs.AddOrUpdate<CleanupService>("purge-sessions", x => x.PurgeExpiredSessions(),
    Cron.FromText("every 10 minutes"));
```

All four produce an ordinary CRON string. That string is what gets stored, shown on the dashboard and exported in [manifests](recurring-job-manifest.md).

## How it works

Expressions are parsed by the [Cronos](https://github.com/HangfireIO/Cronos) library. `AddOrUpdate`, `UpdateSchedule`, `Cron.Describe`, `CronBuilder` and `Cron.FromText` all validate through the same parser, so an expression one of them accepts is accepted by the scheduler. An invalid expression is rejected when you register the job (`ArgumentException` from `AddOrUpdate`, with the parser's message in the inner exception), not when it is due.

The expression is evaluated in the recurring job's time zone (UTC unless you set `RecurringJobOptions.TimeZone`). See [Time zones and misfires](time-zones-and-misfires.md).

## The CRON format

An expression has 5 fields, or 6 when the first one is seconds. Fields are separated by spaces or tabs. Any other number of fields is rejected with "you can only use 5 or 6 (with seconds) part-based expressions".

```
┌───────────── second (0-59)         only in the 6-field form
│ ┌─────────── minute (0-59)
│ │ ┌───────── hour (0-23)
│ │ │ ┌─────── day of month (1-31)
│ │ │ │ ┌───── month (1-12 or JAN-DEC)
│ │ │ │ │ ┌─── day of week (0-7 or SUN-SAT; 0 and 7 are Sunday)
│ │ │ │ │ │
* * * * * *
```

| Syntax | Meaning | Example |
|--------|---------|---------|
| `*` | Every value | `* * * * *`: every minute |
| `,` | List | `0 9,17 * * *`: at 09:00 and 17:00 |
| `-` | Range, inclusive. A range that ends before it starts wraps around | `0 9-17 * * *`: every hour from 09:00 to 17:00; `0 22-2 * * *`: 22:00 to 02:00 |
| `/` | Step | `*/15 * * * *`: minutes 0, 15, 30, 45 |
| `?` | Same as `*` (day fields) | `0 0 ? * MON` |
| `L` | Last day of the month (day of month) | `0 23 L * *` |
| `L-n` | n days before the last day | `0 0 L-3 * *` |
| `L-nW` | Weekday nearest to n days before the last day | `0 0 L-3W * *` |
| `nW` | Weekday nearest to day n | `0 9 15W * *` |
| `LW` | Last weekday of the month | `0 0 LW * *` |
| `nL` | Last given weekday of the month (day of week) | `0 9 * * 5L`: last Friday |
| `n#k` | k-th given weekday of the month (day of week) | `0 9 * * 1#2`: second Monday |
| Names | Three-letter month and day names, any case | `0 8 * JAN,JUL MON-FRI` |

These macros can be used instead of the fields:

| Macro | Same as |
|-------|---------|
| `@every_second` | `* * * * * *` |
| `@every_minute` | `* * * * *` |
| `@hourly` | `0 * * * *` |
| `@daily`, `@midnight` | `0 0 * * *` |
| `@weekly` | `0 0 * * 0` (Sunday) |
| `@monthly` | `0 0 1 * *` |
| `@yearly`, `@annually` | `0 0 1 1 *` |

When both the day-of-month and the day-of-week fields are restricted, a run happens only on days that match **both**: `0 12 1 * 1` runs at noon on the 1st of the month only when the 1st is a Monday. Classic Unix cron uses "either" here, so expressions copied from a crontab can behave differently.

## Options / API

### Cron helpers

All helpers return a 5-field expression string. Hours are 0-23, `DayOfWeek` is the .NET enum.

| Helper | Expression | Runs |
|--------|------------|------|
| `Cron.Minutely()` | `* * * * *` | Every minute |
| `Cron.Hourly()` | `0 * * * *` | Every hour at minute 0 |
| `Cron.Hourly(minute)` | `{minute} * * * *` | Every hour at the given minute |
| `Cron.Daily()` | `0 0 * * *` | Every day at 00:00 |
| `Cron.Daily(hour)` | `0 {hour} * * *` | Every day at the given hour |
| `Cron.Daily(hour, minute)` | `{minute} {hour} * * *` | Every day at the given time |
| `Cron.Weekly()` | `0 0 * * 1` | Every Monday at 00:00 |
| `Cron.Weekly(dayOfWeek)` | `0 0 * * {day}` | Every week on that day at 00:00 |
| `Cron.Weekly(dayOfWeek, hour)` | `0 {hour} * * {day}` | Every week on that day at the given hour |
| `Cron.Weekly(dayOfWeek, hour, minute)` | `{minute} {hour} * * {day}` | Every week on that day at the given time |
| `Cron.Monthly()` | `0 0 1 * *` | The 1st of every month at 00:00 |
| `Cron.Monthly(day)` | `0 0 {day} * *` | That day of every month at 00:00 |
| `Cron.Monthly(day, hour)` | `0 {hour} {day} * *` | That day at the given hour |
| `Cron.Monthly(day, hour, minute)` | `{minute} {hour} {day} * *` | That day at the given time |
| `Cron.Yearly()` | `0 0 1 1 *` | January 1st at 00:00 |
| `Cron.Yearly(month)` | `0 0 1 {month} *` | The 1st of that month at 00:00 |
| `Cron.Yearly(month, day)` | `0 0 {day} {month} *` | That date at 00:00 |
| `Cron.Yearly(month, day, hour)` | `0 {hour} {day} {month} *` | That date at the given hour |
| `Cron.Yearly(month, day, hour, minute)` | `{minute} {hour} {day} {month} *` | That date at the given time |
| `Cron.Never()` | `0 0 31 2 *` | Never (February 31st) |
| `Cron.MinuteInterval(interval)` | `*/{interval} * * * *` | Every `interval` minutes |
| `Cron.HourInterval(interval)` | `0 */{interval} * * *` | Every `interval` hours, at minute 0 |
| `Cron.DayInterval(interval)` | `0 0 */{interval} * *` | Every `interval` days, at 00:00 |
| `Cron.MonthInterval(interval)` | `0 0 1 */{interval} *` | Every `interval` months, on the 1st at 00:00 |

For example `Cron.Weekly(DayOfWeek.Friday, 17, 30)` is `30 17 * * 5` and `Cron.Yearly(6, 15, 9, 30)` is `30 9 15 6 *`. The helpers don't check their arguments; an out-of-range value is rejected when the expression is registered.

`Cron.Never()` registers a job that never runs on its own but can still be triggered from code or the dashboard. [Pausing](pausing-and-updating.md) the job does the same and keeps its real schedule.

### Other Cron members

| Member | Returns | Meaning |
|--------|---------|---------|
| `Cron.FromText(string text)` | `string` | Turns an English phrase into a CRON expression (see below). A string that already is a CRON expression or a `@` macro is validated and returned unchanged. Throws `FormatException` for text it doesn't understand, `ArgumentNullException` for `null`. |
| `Cron.TryFromText(string text, out string cronExpression)` | `bool` | Like `FromText`, but returns `false` and sets `cronExpression` to `null` instead of throwing. |
| `Cron.Builder()` | `CronBuilder` | Starts a fluent builder (see below). |
| `Cron.Describe(string cronExpression)` | `string` | Validates the expression and describes it in English. Throws `FormatException` (or Cronos' `CronFormatException`, which derives from it) for an invalid expression, `ArgumentNullException` for `null`. |
| `Cron.TryDescribe(string cronExpression)` | `string` | Like `Describe`, but returns `null` for `null` or an invalid expression. |
| `Cron.GetNextOccurrences(string cronExpression, TimeZoneInfo timeZone, DateTime fromUtc, int count)` | `IReadOnlyList<DateTime>` | The next `count` occurrences strictly after `fromUtc`, evaluated in `timeZone`, as UTC instants. Returns fewer when the expression stops firing (an empty list for `Cron.Never()`). `count` must be greater than 0. |

### CronBuilder

`Cron.Builder()` returns a `CronBuilder`. Each method fills its own part of the schedule and returns the builder. The calls can come in any order: `ToString()` combines them, validates the expression and returns it. The builder converts to `string` implicitly, so it can be passed straight to `AddOrUpdate` or `UpdateSchedule`. A builder with no calls gives `0 0 * * *` (every day at midnight).

| Method | Sets | Valid range | Example | Result |
|--------|------|-------------|---------|--------|
| `EverySeconds(int seconds = 1)` | second step. Makes a 6-field expression; minute and hour are `*` unless other calls set them | 1-59 | `EverySeconds(30)` | `*/30 * * * * *` |
| `EveryMinutes(int minutes = 1)` | minute step; hour is `*` unless `Between` or `EveryHours` sets it | 1-59 | `EveryMinutes(15)` | `*/15 * * * *` |
| `EveryHours(int hours = 1)` | hour step, within the `Between` range if there is one; minute is the `AtMinute` value, or 0 | 1-23 | `EveryHours(2).Between(8, 18)` | `0 8-18/2 * * *` |
| `EveryDays(int days = 1)` | day-of-month step | 1-31 | `EveryDays(3).At(4)` | `0 4 */3 * *` |
| `EveryMonths(int months = 1)` | month step. Without a day of month or days of week, runs on the 1st | 1-12 | `At(3).OnDayOfMonth(15).EveryMonths(3)` | `0 3 15 */3 *` |
| `At(int hour, int minute = 0)` | adds a time of day. Call it again for more times; they must share the minute | hour 0-23, minute 0-59 | `At(9, 30).At(17, 30)` | `30 9,17 * * *` |
| `AtMinute(int minute)` | the minute within the hour, for hourly schedules | 0-59 | `AtMinute(15).EveryHours(6)` | `15 */6 * * *` |
| `Between(int fromHour, int toHour)` | an hour range, both ends inclusive, for `EverySeconds`, `EveryMinutes` and `EveryHours`. On its own, every hour of the range. A `toHour` before `fromHour` wraps past midnight: `Between(22, 6)` is `22-6`, from 22:00 to 06:59 | 0-23 | `Between(9, 17).EveryMinutes(10)` | `*/10 9-17 * * *` |
| `OnDays(params DayOfWeek[] days)` | days of week (sorted, duplicates removed). Empty throws `ArgumentException` | | `At(8).OnDays(DayOfWeek.Friday, DayOfWeek.Monday)` | `0 8 * * 1,5` |
| `OnWeekdays()` | Monday to Friday | | `At(9).OnWeekdays()` | `0 9 * * 1-5` |
| `OnWeekends()` | Saturday and Sunday | | `At(10).OnWeekends()` | `0 10 * * 0,6` |
| `OnDayOfMonth(int day)` | the day of month | 1-31 | `At(6).OnDayOfMonth(15)` | `0 6 15 * *` |
| `OnLastDayOfMonth()` | the last day of the month | | `At(23).OnLastDayOfMonth()` | `0 23 L * *` |
| `InMonths(params int[] months)` | months (sorted, duplicates removed). Without a day of month or days of week, runs on the 1st. Empty throws `ArgumentException` | 1-12 | `InMonths(7, 1)` | `0 0 1 1,7 *` |

A value outside its range throws `ArgumentOutOfRangeException` when the method is called.

Two calls that would give the same field different values throw `InvalidOperationException` when the second one is called, whatever their order, and the message names both calls. Nothing is overwritten silently. The conflicts are:

- `At` and `EveryMinutes`, or `At` and an `AtMinute` with another minute: both set the minute. Use `Between` to keep a minute interval within some hours.
- `At` and `EveryHours` or `Between`: `At` sets fixed times of day. For "every 2 hours from 9:00" write `EveryHours(2).Between(9, 23)`.
- `OnDays`, `OnWeekdays` and `OnWeekends` with different days; `EveryDays`, `OnDayOfMonth` and `OnLastDayOfMonth`; `EveryMonths` and `InMonths`.
- The same method called again with another value, such as `EveryMinutes(5).EveryMinutes(10)`. Repeating a call with the same value is fine, and `At` adds a time each time it is called.

Calls that set different fields combine: `EveryHours(2).EveryMinutes(15)` is `*/15 */2 * * *`, every 15 minutes during even hours, and `At(9).InMonths(1).OnWeekdays()` is `0 9 * 1 1-5`, every weekday in January.

### Plain-English schedules: Cron.FromText

`Cron.FromText` reads a short English phrase and returns the CRON expression. It ignores case, extra spaces, commas and semicolons. The dashboard's schedule editor accepts the same phrases.

A phrase is an optional **start**, followed by any number of **modifiers** in any order.

**Starts**

| Start | Means | Example | Result |
|-------|-------|---------|--------|
| `every [N] second(s)` / `sec(s)` | every N seconds (6-field) | `every 30 seconds` | `*/30 * * * * *` |
| `every [N] minute(s)` / `min(s)` | every N minutes | `every 15 minutes` | `*/15 * * * *` |
| `every [N] hour(s)` / `hr(s)` | every N hours, at minute 0 | `every 6 hours` | `0 */6 * * *` |
| `every [N] day(s)` | every N days, at 00:00 | `every 3 days at 4` | `0 4 */3 * *` |
| `every weekday`, `weekdays` | Monday to Friday | `weekdays at 9` | `0 9 * * 1-5` |
| `every weekend`, `weekends` | Saturday and Sunday | `weekends at 10` | `0 10 * * 0,6` |
| `every week`, `weekly` | every Monday at 00:00, unless days are given | `weekly on sunday at 2` | `0 2 * * 0` |
| `every [N] month(s)`, `monthly` | the 1st of every (N-th) month, unless a day is given | `every 3 months on the 1st` | `0 0 1 */3 *` |
| `every year`, `yearly`, `annually` | January 1st, unless a date is given | `every year in march on the 15th at 9` | `0 9 15 3 *` |
| `hourly`, `daily`, `everyday` | every hour / every day | `daily at 3:30 pm` | `30 15 * * *` |
| `every DAY [and DAY...]`, or just `DAY [and DAY...]` | those days of week | `every monday and friday at 8am` | `0 8 * * 1,5` |
| `every DAY to DAY` | a range of days (wraps around the week) | `every monday to friday at 6` | `0 6 * * 1,2,3,4,5` |
| `first day of [every / the] month` | the 1st | `first day of every month at 6` | `0 6 1 * *` |
| `last day of [every / the] month` | the last day of the month | `last day of month at 23:00` | `0 23 L * *` |
| a time, `midnight` or `noon` | every day at that time | `7pm` | `0 19 * * *` |
| `at ...`, `on ...`, `between ...`, `from ...` | every day, narrowed by the modifier | `at 7:15` | `15 7 * * *` |

`N` is a number or one of `one`, `two`, `three`, `four`, `five`, `six`, `seven`, `eight`, `nine`, `ten`, `fifteen`, `twenty`, `thirty`, or `other` (2): `every other day` is `0 0 */2 * *`. Without `N` the step is 1. `each` means `every`, and `a`/`an` mean 1.

`every N minutes`, `every N hours` and `every N days` give CRON steps (`*/N`), which start again at the top of every hour, day or month. With an `N` that doesn't divide the hour (or the day) evenly, the last gap of each hour (or day) is shorter: `every 45 minutes` is `*/45 * * * *`, which runs at :00 and :45. `Cron.Describe` and the dashboard's preview show those runs ("At minutes 0 and 45 past every hour"), so check them, or pick a step that divides evenly. See [Things to know](#things-to-know).

**Modifiers**

| Modifier | Means | Example | Result |
|----------|-------|---------|--------|
| `at TIME [and TIME...]` | times of day; for hourly schedules, the minute (see below) | `daily at 9 and 17` | `0 9,17 * * *` |
| `TIME` (without `at`) | the same | `noon on weekdays` | `0 12 * * 1-5` |
| `on weekdays`, `on weekends` | restrict the days of week | `every hour on weekdays` | `0 * * * 1-5` |
| `on DAY [and DAY...]` | restrict to those days | `weekly on mon and thu` | `0 0 * * 1,4` |
| `on the Nth`, `on day N`, `on the Nth day` | day of month (1-31) | `every month on the 15th at 3` | `0 3 15 * *` |
| `on the last day` | last day of the month | `monthly on the last day of the month at 22` | `0 22 L * *` |
| `on MONTH [Nth]`, `in MONTH [Nth]` | restrict to a month, optionally a date | `every year on january 1st at 0:00` | `0 0 1 1 *` |
| `between H and H`, `from H to H` | an hour range, both ends inclusive; `to`, `until`, `till` and `and` all work. A range that ends before it starts runs overnight | `every 10 minutes from 9am to 5pm on weekdays` | `*/10 9-17 * * 1-5` |
| `of every month`, `of the month` | filler, ignored | `on the 1st of the month at 6` | `0 6 1 * *` |

A `TIME` is `H`, `HH`, `H:MM` or `HH:MM` on the 24-hour clock, optionally followed by `am`/`pm` or `a.m.`/`p.m.`, with or without a space (`3:30pm`, `3:30 pm`, `9a.m.`, `9 a.m.`), or the words `midnight` and `noon`. `12 am` is 00:00 and `12 pm` is 12:00. Day names can be full (`monday`), three letters (`mon`), plural (`mondays`), and `tue`/`tues`, `thu`/`thur`/`thurs`. Month names can be full or three letters (`sep` and `sept` both work). An `Nth` is a number from 1 to 31, optionally with `st`, `nd`, `rd` or `th`.

Some combinations worth knowing:

| Phrase | Result |
|--------|--------|
| `every 2 hours between 8 and 18` | `0 8-18/2 * * *` (the step stays within the range) |
| `every 10 minutes between 22 and 6` | `*/10 22-6 * * *` (overnight: from 22:00 to 06:59) |
| `every 2 hours between 22 and 6` | `0 22-6/2 * * *` (22:00, 00:00, 02:00, 04:00 and 06:00) |
| `hourly at 15`, `every hour at 15` | `15 * * * *` (for hourly schedules a plain number is the minute past the hour) |
| `every 2 hours at 9:30` | `30 */2 * * *` (for hourly schedules only the minute of a full time is used) |
| `midnight and noon on weekends` | `0 0,12 * * 0,6` |
| `every day in december at 9` | `0 9 * 12 *` |
| `every 5 minutes on the 1st` | `*/5 * 1 * *` |
| `0 */2 * * 1-5` | `0 */2 * * 1-5` (already CRON, returned as is) |
| `@daily` | `@daily` |

What `FromText` refuses, with a `FormatException` (or a subclass of it). The message names the part it didn't understand, except for values CRON can't hold (the last item), which are reported by the CRON parser with its own, more general message:

- `every 2 weeks`, `every 2 years`: CRON can't express them. Schedule weekly (or yearly) and skip runs inside the job.
- Several times with different minutes, such as `daily at 9:15 and 17:45`: CRON can only combine times that share the minute. Register two recurring jobs instead.
- Hour ranges with minutes, such as `between 9:30 and 17`: ranges use whole hours.
- A time with a minute or second interval, such as `every 15 minutes at 9`: it could mean "during 09:00-09:59" or "from 09:00 on". Say which hours with `between`: `every 15 minutes between 9 and 9` is `*/15 9 * * *`, `every 15 minutes between 9 and 17` is `*/15 9-17 * * *`.
- A time together with an hour range on a daily (or weekly, monthly, yearly) schedule, such as `daily at 9 between 8 and 17`.
- Unknown words: `whenever`, `every 5 fortnights`, `every thursday through saturday`.
- Values CRON can't hold: `every 90 seconds`, `every 25 hours` (steps must fit the field), `daily at 25:00`, `hourly at 75`.

### Describing expressions: Cron.Describe

`Cron.Describe` returns an English sentence for an expression. It covers the 5- and 6-field forms, steps, ranges (including ranges that wrap around, such as `22-2` or `FRI-MON`), lists, names, `L`, `L-n`, `W`, `L-nW`, `#` and the `@` macros. The dashboard uses it to label schedules.

| Expression | Description |
|------------|-------------|
| `* * * * *` | Every minute |
| `*/15 9-17 * * 1-5` | Every 15 minutes, between 09:00 and 17:59, Monday to Friday |
| `0 * * * *` | At the start of every hour |
| `30 * * * *` | At 30 minutes past every hour |
| `0 9-17 * * *` | Every hour from 09:00 to 17:00 |
| `0 9-17/2 * * *` | At 09:00, 11:00, 13:00, 15:00 and 17:00 |
| `0 1/2 * * *` | At the start of every 2 hours between 01:00 and 23:59 |
| `0 6/4 * * *` | At 06:00, 10:00, 14:00, 18:00 and 22:00 |
| `0 9/1 * * *` | Every hour from 09:00 to 23:00 (a start with a step of 1 runs to the end of the field, like `9-23`) |
| `*/45 * * * *` | At minutes 0 and 45 past every hour |
| `5-55/10 * * * *` | At minutes 5, 15, 25, 35, 45 and 55 past every hour |
| `*/7 * * * *` | Every 7 minutes from minute 0 through 56 of the hour |
| `0 0 * * 1-5/2` | At 00:00, on Monday, Wednesday and Friday |
| `0 4 */3 * *` | At 04:00, every 3 days from day 1 through 31 of the month |
| `0,30 9-17 * * *` | At minutes 0 and 30 past the hour, between 09:00 and 17:59 |
| `*/10 22-6 * * *` | Every 10 minutes, between 22:00 and 06:59 |
| `0 22-2 * * *` | At 22:00, 23:00, 00:00, 01:00 and 02:00 |
| `0 22-6/2,3 * * *` | At 00:00, 02:00, 03:00, 04:00, 06:00 and 22:00 (the values of a list are given in order) |
| `30 9,17 * * *` | At 09:30 and 17:30 |
| `0 8 * * MON,WED,FRI` | At 08:00, on Monday, Wednesday and Friday |
| `0 10 * * 0,6` | At 10:00, on weekends |
| `0 23 L * *` | At 23:00, on the last day of the month |
| `0 0 L-3 * *` | At 00:00, 3 days before the end of the month |
| `0 0 L-3W * *` | At 00:00, on the weekday nearest to 3 days before the end of the month |
| `0 9 15W * *` | At 09:00, on the weekday nearest to day 15 of the month |
| `0 9 * * 5L` | At 09:00, on the last Friday of the month |
| `0 9 * * 1#2` | At 09:00, on the second Monday of the month |
| `0 0 1 */3 *` | At 00:00, on day 1 of the month, every 3 months |
| `0 6 1 3-5 *` | At 06:00, on day 1 of the month, March to May |
| `0 12 1 * 1` | At 12:00, on day 1 of the month if it is a Monday |
| `0 0 13 * 5` | At 00:00, on day 13 of the month if it is a Friday |
| `*/30 * * * * *` | Every 30 seconds |
| `30 0 9 * * *` | At 09:00:30 |
| `@weekly` | At 00:00, on Sunday |

A step is called "every N" only when the runs are always N apart, across the end of the hour, day or month too: `*/15 * * * *` is "Every 15 minutes" and `0 */6 * * *` is "At the start of every 6 hours". A step with a start or an end (`6/4`, `0-29/5`), or one that doesn't divide its range (`*/45`), is described by its runs when they are six or fewer, and otherwise as "every N" with its first and last run.

The description is always English; it doesn't follow the dashboard's language.

## More examples

### A quarterly report

```csharp
recurringJobs.AddOrUpdate<ReportService>(
    "quarterly-report",
    x => x.BuildQuarterly(),
    Cron.Builder().At(6).OnDayOfMonth(1).InMonths(1, 4, 7, 10)); // "0 6 1 1,4,7,10 *"
```

### Every 30 seconds

```csharp
recurringJobs.AddOrUpdate<SyncService>("pull-orders", x => x.PullOrdersAsync(), "*/30 * * * * *");
```

The scheduler polls every `SchedulePollingInterval` (15 seconds by default), so sub-minute schedules are approximate. See [Creating recurring jobs](creating-recurring-jobs.md#things-to-know).

### Let users type a schedule

```csharp
public static string? FromUserInput(string input)
{
    if (Cron.TryFromText(input, out var cron))
    {
        return cron;
    }

    return null;
}
```

Store the returned CRON expression, not the phrase, and show `Cron.Describe(cron)` back to the user to confirm what they asked for.

### Preview the next runs

```csharp
var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
IReadOnlyList<DateTime> next = Cron.GetNextOccurrences("0 9 * * 1-5", berlin, DateTime.UtcNow, 5);

foreach (var occurrence in next)
{
    Console.WriteLine($"{occurrence:u} ({TimeZoneInfo.ConvertTimeFromUtc(occurrence, berlin):t} in Berlin)");
}
```

### Show why an expression is rejected

```csharp
try
{
    Cron.FromText("every 2 weeks");
}
catch (FormatException ex)
{
    Console.WriteLine(ex.Message);
    // CRON can't express 'every N weeks'; schedule a weekly job and skip runs in the job instead.
}
```

## Things to know

- **Steps restart at the top of their range.** `*/45 * * * *` runs at :00 and :45 of every hour, not every 45 minutes. `Cron.HourInterval(5)` runs at 00:00, 05:00, 10:00, 15:00, 20:00 and again at 00:00. `Cron.DayInterval(2)` runs on the 1st, 3rd, ... 31st and then on the 1st again. Pick steps that divide the range evenly (minutes: 1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30), or run the job more often and skip runs inside it. `Cron.Describe` names the runs of such steps instead of calling them "every N".
- **Days that don't exist are skipped.** `Cron.Monthly(31)` runs only in months with 31 days; use `OnLastDayOfMonth()` or `L` for "the last day". `Cron.Never()` relies on this.
- **`Cron.Weekly()` starts on Monday, `@weekly` on Sunday.**
- **Day of month and day of week combine with "and".** See [The CRON format](#the-cron-format). `Cron.Describe` words it that way too: `0 12 1 * 1` is "At 12:00, on day 1 of the month if it is a Monday". Expressions copied from a Unix crontab that rely on "either" need two recurring jobs.
- **`Cron.FromText` is not used by `AddOrUpdate` or `UpdateSchedule`.** They take CRON only; pass `Cron.FromText(...)` to them when you want English. The dashboard's schedule editor is the exception: it converts English itself.

## See also

- [Creating recurring jobs](creating-recurring-jobs.md)
- [Pausing and updating](pausing-and-updating.md)
- [Time zones and misfires](time-zones-and-misfires.md)
- [Delayed jobs](../jobs/delayed-jobs.md), for one-off runs and `TimeText` phrases such as "in 10 minutes"
- [Dashboard pages](../dashboard/pages.md)
