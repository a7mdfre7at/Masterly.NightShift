# What's new: Hangfire Next

The features below were added together, on top of the 2.0 branch. Each one works with every storage, because
they only use the hash, set, list, counter and job-parameter primitives every storage already implements. The
design and the storage layout of each feature are in `architecture/blueprints/hangfire-next.md`.

## Reflection-free job invocation

Hangfire no longer calls job methods through `MethodInfo.Invoke`. Every method is performed by a `JobInvoker`,
resolved once and cached:

| Tier | When | How |
|---|---|---|
| Generated | The job's assembly references `Hangfire.Core` (the source generator ships inside the package) | A direct, typed call emitted at compile time |
| Compiled | No generated invoker, dynamic code supported | A delegate compiled from an expression tree, once per method |
| Reflection | `UseCompiledJobInvokers(false)`, or dynamic code unsupported | `MethodInfo.Invoke` |

Nothing to configure. The generator finds the methods used in `Enqueue`, `Schedule`, `ContinueJobWith`,
`AddOrUpdate` and `Job.FromExpression` lambdas. For job classes that other assemblies enqueue, mark the class:

```csharp
[GenerateJobInvokers]
public class ReportJobs { public Task Build(int reportId) { ... } }
```

The Insights page shows how many methods each server performs with each tier. To skip generation for one project,
set the MSBuild property `HangfireDisableJobInvokerGeneration` to `true`.

## Interceptors (AOP)

Interceptors wrap the method call itself. They see the activated instance, can replace arguments, call the method
several times or not at all, and run inside the performer, on the job's synchronization context.

```csharp
public sealed class MeasureAttribute : AsyncJobInterceptorAttribute
{
    protected override async Task<object?> InterceptAsync(JobInvocation invocation)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await invocation.ProceedAsync();
        invocation.Context.Console.WriteLine($"took {stopwatch.ElapsedMilliseconds} ms");
        return result;
    }
}
```

Apply them as attributes on the class or method, or globally with `GlobalConfiguration.Configuration.UseJobInterceptor(...)`.
Built-in: `[JobTimeout(seconds)]`, `[RetryInProcess(attempts)]`, `[LogInvocation]`.

## Concurrency control

```csharp
[Throttle("tenant-{0}", MaxConcurrency = 2)]                 // distributed semaphore, {0} = first argument
[RateLimit("sms", limit: 100, perSeconds: 60)]                // sliding window
[CircuitBreaker("payments", failureThreshold: 5, windowSeconds: 60, openSeconds: 120)]
public Task Send(string tenant, Message message) { ... }
```

When a job can't run, `Action` decides: `Reschedule` (default: back to the schedule, the worker moves on),
`Wait` (hold the worker, fail after `WaitTimeoutSeconds`) or `Skip` (delete the job). Open circuits are listed on
the Insights page and can be reset there.

## Recurring jobs from the dashboard

Pause, resume and edit recurring jobs without redeploying. The editor accepts CRON or plain English:

```csharp
RecurringJob.AddOrUpdate("digest", () => SendDigest(), Cron.FromText("weekdays at 7:30"));
RecurringJob.Pause("digest");
RecurringJob.UpdateSchedule("digest", "every 2 hours between 8 and 18 on weekdays");
```

A paused job keeps its definition, stays paused across redeploys, can still be triggered by hand, and resumes
from now without catching up. Managers from other packages that don't implement `IRecurringJobManagerV3` throw
`NotSupportedException` from these calls.

## Live monitoring

```csharp
public async Task Import(string file, IJobConsole console)   // injected, pass null when enqueueing
{
    console.Checkpoint("download");
    await Download(file);
    for (var i = 0; i < rows.Count; i++)
    {
        if (i % 100 == 0) console.SetProgress(100.0 * i / rows.Count, $"row {i}");
    }
    console.WriteLine(JobConsoleLevel.Warning, "3 rows skipped");
}
```

The job details page shows the progress bar, console and checkpoint timeline live, with the elapsed time, and
the Processing page shows every job's progress. `PerformContext.Console` gives filters and interceptors the same
console. Console data expires with the job.

## Insights

`GlobalConfiguration.Configuration.UseJobInsights()` records runs, failures and the last 200 durations per method.
The Insights page shows failure rates, median/p95/max, a trend sparkline, the invoker tier, and flags a last run
that took more than three times the p95.

## Smaller additions

- `Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays()` builds CRON expressions fluently and validates them.
- `[Deadline("00:10:00")]` records the moment a job should be done by. The Processing page and the job details
  page flag overdue jobs, and a job that finishes late keeps a "finished N after its deadline" notice. Nothing is
  canceled; use `[JobTimeout]` for that.
- The Servers page shows how many methods this process performs with generated, compiled and reflection invokers.

## Dashboard

A redesigned shell and design system, in light and dark:

- Left sidebar navigation with icons and live counts for every job state, so the duplicated per-page job-state
  sidebar is gone and every page uses the full width. Collapses behind a menu button on narrow screens.
- Top bar with the page title, a connection pulse that turns red when polling fails, and the search button.
- Theme switcher (system, light, dark) in the sidebar footer, remembered per browser; `?theme=dark` in the URL
  also selects a theme. Both themes come from one set of design tokens (`hangfire-ui.css`); the legacy
  `hangfire-dark.css` is now loaded through a `media` attribute that the switcher controls.
- Overview page with KPI tiles and chart cards; tables, buttons, pills, alerts, forms and modals restyled.
- Command palette (`Ctrl+K`, `/`, or the search button): pages, a job by id, recurring jobs by name.
  `g` then `j`, `p`, `f`, `r`, `s`, `i` or `h` jumps to a page.
- Every job list has **Refresh** (reloads the table in place) and **Live** (keeps reloading) buttons, and a
  **Delete all** button that empties the whole list, not just the selected page. The recurring jobs page has
  **Remove all**. A refresh button in the top bar reloads any page.
- **Maintenance** on the Servers page: **Clear all jobs** removes every job with its history, queues, counters,
  consoles and statistics but keeps recurring job definitions and servers; **Reset storage** removes everything.
  Both call the new `JobStorage.Purge(StoragePurgeOptions)`, implemented by `Hangfire.SqlServer` and advertised
  through `JobStorageFeatures.Purge`; the buttons appear only when the storage supports it.
- Cron expressions are explained in plain English (`Cron.Describe`) under the schedule editor and as tooltips on
  the cron badges.
- Every new string is translated in all 15 dashboard locales.

`MenuItem.Icon` lets dashboard extensions give their navigation items a Glyphicon; items without one get a chevron.
