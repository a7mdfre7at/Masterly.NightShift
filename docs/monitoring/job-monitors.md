# Job monitors

A job monitor watches the jobs of one method and raises an alert when something is wrong, and another when it is right again: the nightly report didn't succeed by 7 o'clock, an import has run for three hours, the payment sync failed five times in a row. Failed-job alerts tell you about each failure; monitors tell you about the situation, once, and when it ends.

## Quick example

```csharp
TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

GlobalConfiguration.Configuration
    .UseWebhookAlerts("https://hooks.slack.com/services/...", o => o.Format = WebhookFormat.Slack)
    .UseJobMonitor(new DailyDeadlineMonitor("daily-report", "ReportService.BuildDaily", TimeSpan.FromHours(7), berlin, grace: TimeSpan.FromMinutes(30)))
    .UseJobMonitor(new LongRunningMonitor("long-import", "ImportService.Import", TimeSpan.FromHours(2)))
    .UseJobMonitor(new RepeatedFailuresMonitor("payment-sync", "PaymentSync.Run", failures: 5, window: TimeSpan.FromHours(6)));
```

## The monitors

A monitor watches the jobs of one method, written `Type.Method` with the type's name without its namespace, as alerts name it (`ReportService.BuildDaily`). Its name is 1 to 64 letters, digits, `.`, `_` and `-`, lower-cased.

| Monitor | Needs attention when | Healthy when |
|---|---|---|
| `DailyDeadlineMonitor(name, method, by, timeZone, grace)` | No job of the method has succeeded today (in `timeZone`, UTC by default) by `by` plus `grace` (0 to 12 hours) | A job succeeded today. Before the deadline it is `Waiting` |
| `LongRunningMonitor(name, method, threshold)` | A job of the method has been processing longer than `threshold` (5 seconds to 7 days). The time it waited in its queue doesn't count | No job runs that long |
| `RepeatedFailuresMonitor(name, method, failures, window)` | The last `failures` (2 to 20) jobs of the method failed for good in a row, the first and the last of them at most `window` (1 hour to 7 days) apart. The window slides: each new failure in a row is judged with the ones just before it | A job of the method succeeded, even when new failures followed before the next check (fewer than `failures` of them). Failures that keep coming, however far apart, don't end an open incident |

`Paused = true` (an `init` property) keeps a monitor registered but silent: it isn't checked, shows `Paused` and raises nothing. Its stored state stays as it was, so an incident open when it was paused is still open, without a new alert, when the monitor is registered again without `Paused`.

A `DailyDeadlineMonitor` whose deadline falls in the hour skipped when clocks go forward (02:30 on the night clocks jump from 02:00 to 03:00) uses the first valid minute after it, 03:00, that day.

## How it works

1. `UseJobMonitor` registers the monitor and the global filter that records, for monitored methods only, each success and each final failure in the monitor's storage hash `monitor:{name}`, once the state change is committed. For other jobs the filter costs a dictionary lookup.
2. Every server runs a process that checks every monitor each `JobMonitors.CheckInterval` (30 seconds by default, 5 seconds to 5 minutes), one server at a time across the cluster: the others skip the check while one holds its lock. Monitors registered after a server started are checked too.
3. A monitor that moves to `NeedsAttention` opens an incident: it raises one `AlertKind.MonitorIncident` alert, with the reason as the message, the method and the monitor's name (`Alert.Monitor`). It raises nothing more while the incident is open. A daily deadline missed yesterday stays open the next morning, until a job succeeds.
4. A monitor that becomes `Healthy` again closes the incident with one `AlertKind.MonitorRecovered` alert.

Alerts go to every [alert sink](alerts-and-webhooks.md), through their rules. A monitor that can't be checked (the storage fails) shows `Unknown`, with the error as its reason, and keeps an open incident open. Each monitor is checked and read on its own: one that fails, or whose stored state can't be read, is logged and the others are still checked. A stored value that can't be read counts as missing.

## Reading the monitors

```csharp
foreach (JobMonitorStatus monitor in JobMonitors.GetStatus(storage))
{
    Console.WriteLine($"{monitor.Name} ({monitor.Method}): {monitor.State} {monitor.Reason}");
}
```

`JobMonitorStatus` has the `State` (`Waiting`, `Healthy`, `NeedsAttention`, `Unknown`, `Paused`), the `Reason`, `IncidentSince`, `CheckedAt`, `LastSuccessAt` and `ConsecutiveFailures`. `JobMonitors.GetAll()` lists the registered monitors and `JobMonitors.Remove(name)` removes one.

## Things to know

- **Register the monitors in every process that runs jobs,** so their outcomes are recorded, and in the servers' processes, so they are checked. A process that only creates jobs needs none.
- **Monitors are defined in code.** Their state is in storage, so a server that starts takes over open incidents.
- **Recording is best effort.** It runs after the state change is committed; a storage error is logged and doesn't affect the job. A failure, or a success after failures, is written under the monitor's distributed lock. A success while nothing failed only refreshes `LastSuccessAt`, without the lock, and at most every 5 seconds (a daily deadline still gets the first success of each local day), so a busy method doesn't take a lock for each job.
- **`RepeatedFailuresMonitor` keeps the times of the last failures in a row** (at most 20) in the `RecentFailures` field of the monitor's hash.
- **The long-running monitor reads up to 1,000 processing jobs** through the monitoring API at each check.

## See also

- [Alerts and webhooks](alerts-and-webhooks.md)
- [OpenTelemetry](opentelemetry.md)
- [Timing filters](../filters/timing.md), for a deadline on one job (`[Deadline]`)
