# Troubleshooting

This page lists the problems people run into most often with Masterly.NightShift, each with its usual causes and the fix. Start with the symptom you see; most sections point to a page in the dashboard where you can confirm the cause.

## Quick example

Three places answer most questions:

1. **The job's details page** in the dashboard (click a job ID). Besides the state history and exception details, it shows a diagnosis for jobs that aren't moving: no server listens to the queue, processing is paused, the scheduled time has passed with no server alive, the parent of a continuation failed, the server running it is gone, or why a failed job won't be retried.
2. **The Servers page.** It lists every running server with its queues, worker count and last heartbeat. If your server isn't there, nothing processes jobs.
3. **The log.** Turn on debug logging for the library's categories, which all start with `Masterly.NightShift`. In an ASP.NET Core or Worker Service app, in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Masterly.NightShift": "Debug"
    }
  }
}
```

## How it works

Most problems come down to one of the parts in [Core concepts](getting-started/core-concepts.md): a job is a record in the storage, servers fetch it from a queue, a worker runs it, and filters decide which state comes next. When a job "does nothing", either no server is fetching from its queue, a filter (drain mode, throttling, a run window) is holding it, or it can't be loaded. When a job "runs twice", it's the at-least-once guarantee doing its work. The sections below go through these cases.

## Options / API

The settings that come up on this page, with their defaults:

| Setting | Type | Default | Relevant to |
|---|---|---|---|
| `BackgroundJobServerOptions.Queues` | `string[]` | `{ "default" }` | Jobs that stay Enqueued |
| `BackgroundJobServerOptions.WorkerCount` | `int` | `Math.Min(Environment.ProcessorCount * 5, 20)` | Jobs that wait for a free worker |
| `BackgroundJobServerOptions.SchedulePollingInterval` | `TimeSpan` | 15 seconds | Delayed jobs and retries starting late; recurring job scheduling |
| `BackgroundJobServerOptions.ShutdownTimeout` | `TimeSpan` | 15 seconds | Jobs interrupted on shutdown |
| `BackgroundJobServerOptions.ServerTimeout` | `TimeSpan` | 5 minutes | When a silent server is removed from the Servers page |
| `SqlServerStorageOptions.SlidingInvisibilityTimeout` | `TimeSpan?` | 5 minutes | When a job held by a crashed server becomes available again |
| `SqlServerStorageOptions.CommandTimeout` | `TimeSpan?` | `null` (the driver's default) | SQL timeouts |
| `SqlServerStorageOptions.CommandBatchMaxTimeout` | `TimeSpan?` | 5 minutes | Timeouts on batched writes |
| `SqlServerStorageOptions.PrepareSchemaIfNecessary` | `bool` | `true` | Schema installation on start-up |
| `SqlServerStorageOptions.EnableHeavyMigrations` | `bool` | `false` | Upgrading old schemas |
| `DrainMode.HoldInterval` | `TimeSpan` | 30 seconds | How long held jobs wait before they are looked at again |
| `DashboardOptions.Authorization` | `IEnumerable<IDashboardAuthorizationFilter>` | `LocalRequestsOnlyAuthorizationFilter` | Dashboard `401` |
| `RecurringJobOptions.TimeZone` | `TimeZoneInfo` | UTC | Recurring jobs firing at the wrong hour |
| `KeepAliveOptions.Interval` | `TimeSpan` | 5 minutes | IIS idle shutdown |

## Jobs stay in Enqueued

The job is in its queue, but no worker takes it.

| Cause | How to tell | Fix |
|---|---|---|
| No server is running | The Servers page is empty; the job's diagnosis says no server is alive | Call `AddNightShiftServer()` (or start a `BackgroundJobServer`) in at least one process. An app that only calls `AddNightShift(...)` creates jobs but never runs them |
| No server listens to the job's queue | The diagnosis says the queue isn't served; the Servers page shows other queues | Add the queue to `BackgroundJobServerOptions.Queues` on some server, or move the job to a served queue. `[Queue("billing")]` on a method sends its jobs to `billing` |
| The server uses a different database or schema | Your server is missing from this dashboard's Servers page, or the job never shows on the other app's dashboard | Use the same connection string and `SqlServerStorageOptions.SchemaName` in every app |
| All workers are busy | **Processing** shows `WorkerCount` jobs per server, often long ones | Raise `WorkerCount`, add servers, or give long jobs their own queue and server |

```csharp
services.AddNightShiftServer(options =>
{
    options.Queues = new[] { "billing", "default" };
});
```

Queue names are lowercase: they may only contain lowercase letters, digits, underscores and dashes. `[Queue("Billing")]` throws `ArgumentException` as soon as the attribute is read, so creating any job of that method fails. A template such as `[Queue("tenant-{0}")]` is checked when the job is enqueued: an argument that produces an invalid name, such as `Contoso`, throws `InvalidOperationException` naming the template and the result.

## Jobs are held: "processing is paused"

Jobs move from Enqueued to Scheduled with the reason `Held: processing is paused`, `Held: queue '{name}' is paused` or `Held: server '{id}' is paused`, come back after 30 seconds, and are held again.

This is [drain mode](server/drain-mode.md): someone paused all processing, one queue, or one server, from the dashboard or in code. Running jobs finish; new ones are held until processing resumes. The Servers page shows the switch and a **Resume** button, and the **Queues** page shows paused queues. In code:

```csharp
using (var connection = storage.GetConnection())
{
    var status = DrainMode.Read(connection);
    Console.WriteLine($"Drain: {status.Active}, paused queues: {string.Join(", ", status.PausedQueues.Keys)}");

    DrainMode.Disable(connection);
    DrainMode.ResumeQueue(connection, "billing");
}
```

The switch lives in the storage (the `hangfire:control` hash), so restarting the app doesn't turn it off. Each process caches the answer for 2 seconds, so a change takes effect almost at once.

## Jobs stay in Scheduled, or start late

- **No server is running.** The delayed job scheduler runs inside servers. Without one, nothing moves Scheduled jobs to Enqueued. The diagnosis says the scheduled time has passed and no server is alive.
- **Up to 15 seconds late is normal.** Servers check for due jobs every `SchedulePollingInterval`. Lower it if you need delayed jobs and retries to start sooner, at the cost of more queries.
- **The job is waiting for a retry.** A failed job with retries left is Scheduled, and it is listed on the **Retries** page. The delay grows with each attempt, up to a couple of hours for the tenth (see [Retries](filters/retries.md)).
- **A filter rescheduled it.** Throttling, rate limits, circuit breakers, run windows and `RetryLaterException` all reschedule jobs instead of running them. The state history shows the reason each time.

## Jobs stuck in Processing

A job shows as Processing for a long time, and its diagnosis says the server running it is gone or has stopped sending heartbeats.

The server crashed or was killed while the job ran. The job isn't lost: with SQL Server, the queue entry becomes visible to other workers once `SlidingInvisibilityTimeout` (5 minutes by default) passes without the crashed server refreshing it, and another worker runs the job again from the start. The dead server is removed from the Servers page after `ServerTimeout`. If you can't wait, requeue the job from the dashboard.

If the server is alive and the job is just slow, the job details page compares the run time with the job's usual duration. Use [`[JobTimeout]`](filters/timing.md) to stop runs that take too long.

## A job runs twice

Jobs run at least once, not exactly once. The usual reasons:

| Cause | What you see |
|---|---|
| The job threw and was retried | The history shows Scheduled states with "Retry attempt n of m" reasons. Work done before the exception is repeated |
| The server stopped while the job ran | The job ran on one server, then started again after a restart or deploy. A job canceled by the shutdown goes back to its queue; so does one still running when the process exits, because the server stops waiting for it after `ShutdownTimeout` |
| The server crashed, or lost the database for longer than `SlidingInvisibilityTimeout` | Another worker fetched the job because the queue entry looked abandoned. Rarely, both runs overlap |
| The same work was enqueued twice | Two different job IDs with the same method and arguments. Common with recurring jobs triggered by hand, or when a request that enqueues is retried |
| Two environments share a database | A developer machine or a test environment pointed at the production database runs production jobs. The Servers page shows unexpected server names |

Make jobs idempotent first: check whether the work is already done, use upserts, and pass idempotency keys to external services. Then, where it fits:

```csharp
public class ImportService
{
    // Only one ImportCatalog job runs at a time across all servers
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public void ImportCatalog() { }

    // A second Reindex(productId) isn't created while the first one is still pending
    [PreventDuplicates]
    public void Reindex(int productId) { }
}
```

See [Concurrency](filters/concurrency.md), and [Core concepts](getting-started/core-concepts.md#at-least-once-execution) for why this can't be fully avoided.

## A job fails because it can't be loaded

The server couldn't rebuild the method call from the stored record, so it couldn't move the job to the next state. The reason says what failed: `the job's type or method was not found.` when the class, its assembly or the method can't be found, or `the job's arguments could not be deserialized.` when an argument can't be read. It starts with `Can not change the state to '<state>':`, naming the state the server was moving the job to: `Processing` when a worker fetched the job, `Enqueued` when a scheduled or retried job was due. The exception details show the error.

The failure goes through the global filters like any other, so the default `AutomaticRetry` filter retries it 10 times: the job first shows under **Retries**, scheduled with a reason like `Retry attempt 1 of 10: Could not load type ...`, and reaches **Failed** only when the attempts are used up. Each attempt fails the same way until a server that can load the job picks it up. Attributes on the job's class or method (for example `[AutomaticRetry(Attempts = 0)]`) don't apply, because they can't be read without the method; only global filters do. Look at the inner exception:

| Inner exception | Cause | Fix |
|---|---|---|
| `TypeLoadException`, `FileNotFoundException` (assembly) | The class, its namespace or its assembly was renamed or moved, or the server doesn't reference the assembly at all | Keep the old type and method as a thin wrapper that calls the new code until old jobs are gone. Make sure every server references the assembly with the job classes |
| `InvalidOperationException` or `MissingMethodException` about the method | The method was renamed, or its parameter list changed | Same: keep the old signature as an overload that forwards to the new one |
| A JSON exception (`JsonException`, `NotSupportedException` about deserialization) | An argument can't be read: its type changed, or it isn't readable with System.Text.Json | See [Serialization errors after an upgrade](#serialization-errors-after-an-upgrade) |
| `FileLoadException` about an assembly version | Stored type names contain an assembly version that the server doesn't have | Use `UseSimpleAssemblyNameTypeSerializer()` for new jobs (it stores names without versions), and `UseIgnoredAssemblyVersionTypeResolver()` to read old ones |

Other common setups that cause it:

- **Rolling deploys.** A new client creates jobs for a method that old servers don't have yet. Deploy servers before clients, or create the new jobs with a new queue passed as an argument (see the next point) that only updated servers listen to.
- **Several apps share one database.** Each server takes any job from the queues it listens to, including jobs from other apps whose classes it doesn't have. Separate queues isolate the apps only partly. Any server moves due scheduled and retried jobs to their queue, and that loads the job's class unless the job was created with the queue passed as an argument (`BackgroundJob.Enqueue("app-a", ...)`); a queue set with `[Queue]`, or the default queue, needs the class. Any server also triggers recurring jobs, and the server that finishes a parent job starts its continuations, which always loads their class. A recurring job that a server can't load is retried and, after several failures in a row, disabled. To isolate apps, give each one its own schema with `SqlServerStorageOptions.SchemaName` (or its own database).

Once fixed, jobs still under **Retries** run again at their next attempt; requeue the failed ones from the **Failed** page.

## Serialization errors after an upgrade

Jobs created before an upgrade fail with "the job's arguments could not be deserialized" and a JSON exception, or run with missing data (for example a property that is now `null`).

Arguments are stored as JSON. When the reading side changes, old payloads may no longer fit:

- **Coming from Hangfire:** Newtonsoft.Json was replaced by System.Text.Json. Properties with private setters need `[JsonInclude]`, types without a public parameterless constructor need a single public constructor or `[JsonConstructor]`, Newtonsoft attributes such as `[JsonProperty]` are ignored, and an argument declared as an interface or abstract class needs a `"$type"` naming its actual type, which Hangfire wrote only with its recommended settings (`TypeNameHandling`). See [Migrating from Hangfire](migrating-from-hangfire.md#serialization-changes).
- **Custom serializer options:** whatever options you pass to `UseSerializerSettings`, user data is read leniently (property names in any case, quoted numbers, public fields). What can still break old payloads is a custom converter that reads only its own format; make it accept the old format too.
- **You changed an argument type:** renaming or removing properties, or changing their types, affects jobs that are already stored. Add new properties with defaults; keep old ones until old jobs are processed.

To see what is stored, open the job in the dashboard, or read the `Arguments` column of the `HangFire.Job` table. Once the type can read the old payloads, requeue the failed jobs. See [Serialization](configuration/serialization.md).

## The dashboard returns 401, 403 or 404

| Response | Cause | Fix |
|---|---|---|
| `401` for everyone but you | The default authorization filter, `LocalRequestsOnlyAuthorizationFilter`, only lets in requests from the same machine. Requests from another computer, from outside a Docker container, or through a proxy that adds `X-Forwarded-For` are refused (a proxy on the same machine that adds no forwarding header makes every request look local) | Set `DashboardOptions.Authorization` to your own filter, or use `MapNightShiftDashboardWithAuthorizationPolicy` (see below) |
| `403` on every page | You're signed in, but an authorization filter refused you. Unauthenticated users get `401`, authenticated ones `403` | Check the filter or policy, for example the role it requires |
| `403` when clicking buttons (Requeue, Delete, Trigger now) | The antiforgery token was rejected. Common with several instances behind a load balancer that don't share Data Protection keys, so a token issued by one instance is invalid on another. Without `AddAntiforgery()`, the dashboard only accepts POSTs that carry `X-Requested-With` and come from its own origin (same scheme, host and port), so a script or a form on another site gets `403` with an explanation in the body. Behind a proxy that changes the `Host` header or the scheme, browsers that don't send `Sec-Fetch-Site` get `403` on every button, because their `Origin` no longer matches | Register antiforgery with `services.AddAntiforgery()`, and share the Data Protection key ring between instances. `DashboardOptions.IgnoreAntiforgeryToken = true` turns the check off, which you should only do when nothing else protects the dashboard from cross-site requests |
| `404` | The path is wrong: the default is `/nightshift` (Hangfire used `/hangfire`). In an IIS virtual directory, the path is below it, for example `/shop/nightshift` | Use the path you passed to `MapNightShiftDashboard(...)`, or pass the one you want |
| `404` for the dashboard's CSS and scripts behind a reverse proxy | The proxy serves the app under a prefix it strips, so the dashboard builds links without it | Set `DashboardOptions.PrefixPath` to that prefix, or forward it as the path base |
| `404` with `UseNightShiftDashboard` | The middleware was added after a middleware that ends the request | Add it earlier in the pipeline, or use `MapNightShiftDashboard` on .NET 8+ |

A dashboard for administrators only, with ASP.NET Core authorization:

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("JobsAdmin", policy => policy.RequireRole("Admin")));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapNightShiftDashboardWithAuthorizationPolicy("JobsAdmin");
```

Or with a dashboard filter:

```csharp
using Masterly.NightShift.Dashboard;

public sealed class AdminDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true && user.IsInRole("Admin");
    }
}

app.MapNightShiftDashboard(new DashboardOptions
{
    Authorization = new[] { new AdminDashboardFilter() },
});
```

> **Note:** Behind a reverse proxy on the same machine (nginx, or IIS forwarding to Kestrel) every request can look local, so the default filter may let everyone in. Configure the forwarded headers middleware so ASP.NET Core sees the real client address, and use real authorization for anything reachable from outside. See [Dashboard authorization](dashboard/authorization.md).

## "Unable to find the required services"

```
InvalidOperationException: Unable to find the required services. Please add all the required services by calling 'IServiceCollection.AddNightShift' inside the call to 'ConfigureServices(...)' in the application startup code.
```

`AddNightShiftServer`, `MapNightShiftDashboard` and `UseNightShiftDashboard` need the services that `AddNightShift(...)` registers. Call `builder.Services.AddNightShift(config => ...)` in every app that uses them, including a dashboard-only app.

## "Current JobStorage instance has not been initialized yet"

```
InvalidOperationException: Current JobStorage instance has not been initialized yet. You must set it before using Masterly.NightShift Client or Server API. ...
```

The static `BackgroundJob`, `RecurringJob` or `new BackgroundJobServer()` were used before any storage was configured.

- **Without dependency injection:** call `GlobalConfiguration.Configuration.UseSqlServerStorage(...)` before creating jobs or servers.
- **With `AddNightShift`:** the configuration callback runs lazily, the first time a NightShift service is resolved: when a server starts, when the dashboard is mapped, or when you resolve `IBackgroundJobClient` or `IRecurringJobManager`. Code that runs between `builder.Build()` and `app.Run()` and uses the static classes can run too early. Resolve the services instead, which also runs the configuration:

```csharp
var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6));
```

## A recurring job doesn't fire

Open **Recurring Jobs** and look at the job's row: its next execution, its last execution and any error.

| Cause | How to tell | Fix |
|---|---|---|
| No server is running | Next execution is in the past and nothing happens | Recurring jobs are scheduled by servers. Start one |
| It is paused | The row has a **Paused** label and no next execution | **Resume** it on the page, or call `RecurringJob.Resume("daily-report")`. `AddOrUpdate` at start-up doesn't resume a paused job, on purpose |
| Wrong time zone | It fires, but at the wrong hour | Recurring jobs use UTC unless you set `RecurringJobOptions.TimeZone` |
| It can't be scheduled | The row shows an error, such as a type that can't be loaded or a time zone that doesn't exist on the server | The scheduler retries 5 times, then stops scheduling the job. Fix the cause, then call `AddOrUpdate` again (a job with an error is rescheduled) or trigger it |
| The cron expression means something else | The next execution is not what you expected | Six fields mean the first is seconds: `*/15 * * * * *` is every 15 seconds. `0 0 31 2 *` never fires. The **Recurring Jobs** page and [`Cron.Describe`](recurring-jobs/cron-and-schedules.md#describing-expressions-crondescribe) show what an expression means |
| It fired while no server was running | Nothing ran during the downtime | `MisfireHandlingMode.Relaxed` (the default) runs once when a server is back, `Strict` runs every missed occurrence, `Ignorable` skips them |
| The occurrence was created, but the job didn't run | **Jobs** has a job "Triggered by recurring job scheduler" that is Enqueued or Failed | Treat it like any other job: see the sections above |

```csharp
recurringJobs.AddOrUpdate<ReportService>(
    "daily-report",
    x => x.BuildDaily(),
    Cron.Daily(6),
    new RecurringJobOptions
    {
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
        MisfireHandling = MisfireHandlingMode.Relaxed,
    });
```

The time zone ID is stored with the recurring job and resolved on every server that schedules it, so the ID must exist on all of them. On .NET 8 and later, Windows IDs (`W. Europe Standard Time`) and IANA IDs (`Europe/Berlin`) both resolve on Windows and Linux when ICU is available; on .NET Framework only Windows IDs do. To map IDs yourself, give the server an `ITimeZoneResolver` (see [Time zones and misfires](recurring-jobs/time-zones-and-misfires.md)).

## SQL Server: errors at start-up

| Error | Cause | Fix |
|---|---|---|
| `InvalidOperationException: Please add a NuGet package reference to either 'Microsoft.Data.SqlClient' or 'System.Data.SqlClient'...` | No SQL Server driver in the app | Add `Microsoft.Data.SqlClient` (see [Installation](getting-started/installation.md#the-sql-server-driver)) |
| `Cannot open database "NightShift" requested by the login` | The database doesn't exist, or the login has no access | The storage creates tables, not the database. Create it first and grant the login access |
| Warning `Was unable to perform the Masterly.NightShift schema migration due to an exception`, then `Invalid object name 'HangFire.Job'` | The schema couldn't be installed, usually because the login can't create tables | Run the install once with a privileged login, or run the script by hand (see [Schema and upgrades](storage/schema-and-upgrades.md)), then set `PrepareSchemaIfNecessary = false` for the app's login |
| `ArgumentException: The server id '...' is N characters long, but the SQL Server storage keeps server ids of at most 200 characters`, and the server never processes jobs | `ServerName` is longer than 150 characters | Set a shorter `BackgroundJobServerOptions.ServerName` |
| The schema stays at version 5 (or 7): `select Version from [HangFire].[Schema]` doesn't reach 9 | An old database needs a heavy migration, which is skipped by default. The install script reports it only as SQL `PRINT` output ("Migration process STOPPED at schema version 5"), which shows when you run the script by hand, not in the application log | In a quiet window, start once with `EnableHeavyMigrations = true`, or run the script by hand |

## SQL Server: timeouts and deadlocks

- **Old schema.** On start-up, the storage reads the schema version and turns on the options it supports: no global locks from version 6, and `IGNORE_DUP_KEY` instead of `MERGE` from version 8, which avoids most deadlocks on the `Hash` and `Set` tables. A database stuck at an old version because a heavy migration was skipped deadlocks more. Upgrade it (see the table above). The dashboard home page shows the schema version.
- **Command timeouts.** `CommandTimeout` (the driver's default, 30 seconds, when `null`) applies to single commands, and `CommandBatchMaxTimeout` (5 minutes) to the batched writes of a transaction. Raise them if a busy or remote database times out:

```csharp
using Masterly.NightShift.SqlServer;

config.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
{
    CommandTimeout = TimeSpan.FromSeconds(60),
    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
});
```

- **Distributed lock timeouts.** `DistributedLockTimeoutException` in a failed job means a lock, usually from `[DisableConcurrentExecution]`, was held by another job for longer than its timeout. That's the filter working; the job is retried. Raise the timeout or reduce how often the job is created.
- **Expiration on very large tables.** Expired jobs are deleted in batches by a background process. On a database with millions of rows, see `DeleteExpiredBatchSize` and `JobExpirationCheckInterval` in [SQL Server options](storage/sql-server-options.md).
- **Too many queries on idle queues.** Every server polls its queues. With many servers, see `QueuePollInterval` in [SQL Server options](storage/sql-server-options.md).

## The server stops when IIS goes idle

Jobs only run while requests come in; recurring jobs fire late or not at all; the Servers page shows the server disappearing and coming back.

IIS shuts an app pool down after 20 minutes without requests (the default idle timeout), and the server inside it stops. Two settings together keep it running:

1. **Keep-alive.** `AddNightShiftKeepAlive` makes the server request one of the app's own URLs every 5 minutes, so the pool never counts as idle. Behind IIS, give the public address, because the request has to go through IIS to count:

   ```csharp
   services.AddNightShiftServer();
   services.AddNightShiftKeepAlive(options =>
   {
       options.Url = "https://jobs.example.com/nightshift/ping";
   });
   ```

   `/nightshift/ping` answers without touching the storage; it exists when the dashboard is mapped at `/nightshift`. Any URL that reaches the app works.

2. **Start after a recycle.** The keep-alive runs inside the app, so it can't restart a pool that IIS recycled. In IIS, set the app pool's start mode to `AlwaysRunning` and enable preload for the site (Application Initialization), so the app starts without waiting for a request.

See [Keep-alive](server/keep-alive.md). Alternatively, run the server in a Windows Service or Worker Service and keep only the client and dashboard in IIS.

## See also

- [Core concepts](getting-started/core-concepts.md)
- [Managing jobs](jobs/managing-jobs.md)
- [Queues and workers](server/queues-and-workers.md)
- [Drain mode](server/drain-mode.md)
- [Retries](filters/retries.md), [Concurrency](filters/concurrency.md)
- [SQL Server options](storage/sql-server-options.md)
- [Dashboard authorization](dashboard/authorization.md)
- [Logging](monitoring/logging.md)
