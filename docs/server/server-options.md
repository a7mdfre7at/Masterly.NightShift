# Server options

`BackgroundJobServerOptions` controls one background job server: how many workers it runs, which queues it listens to, how it shuts down and how often it polls and sends heartbeats. You pass it to `AddNightShiftServer` in an ASP.NET Core or Worker Service app, or to `new BackgroundJobServer(options)` anywhere else.

## Quick example

```csharp
using Masterly.NightShift;

builder.Services.AddNightShiftServer(options =>
{
    options.ServerName = "billing-worker";
    options.WorkerCount = 10;
    options.Queues = new[] { "critical", "default" };
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});
```

Without dependency injection:

```csharp
using Masterly.NightShift;

var options = new BackgroundJobServerOptions
{
    WorkerCount = 10,
    Queues = new[] { "critical", "default" }
};

using (var server = new BackgroundJobServer(options))
{
    Console.WriteLine("Press Enter to stop the server.");
    Console.ReadLine();
}
```

## How it works

A `BackgroundJobServer` is a set of background processes that run on dedicated threads until the server stops. The options decide which processes are started and how they behave:

| Process | Threads | Runs when | What it does |
| --- | --- | --- | --- |
| `Worker` | `WorkerCount` | always | Fetches a job from one of `Queues`, moves it to Processing, runs it, moves it to Succeeded or Failed. |
| `DelayedJobScheduler` | 1 | not lightweight | Every `SchedulePollingInterval`, enqueues scheduled jobs whose time has come (delayed jobs and retries). |
| `RecurringJobScheduler` | 1 | not lightweight | Every `SchedulePollingInterval`, creates the runs of recurring jobs that are due. |
| `ServerHeartbeatProcess` | 1 | always | Every `HeartbeatInterval`, tells the storage the server is alive. It keeps running until the very end of a shutdown, so the server is not taken for dead while jobs finish. |
| `ServerWatchdog` | 1 | always | Every `ServerCheckInterval`, removes servers that have not sent a heartbeat for `ServerTimeout`. |
| `ServerJobCancellationWatcher` | 1 | always | Every `CancellationCheckInterval`, cancels the tokens of running jobs that were deleted or moved to another state. |
| Storage processes | 1 each | see below | Provided by the storage. For SQL Server: its heartbeat process (always) and `ExpirationManager` and `CountersAggregator` (not lightweight). |
| Additional processes | 1 each | always | Whatever you pass to the constructor or register as `IBackgroundProcess` in DI, for example the [keep-alive process](keep-alive.md). |

When the server starts it announces itself in the storage with an ID, its queues and its worker count; the dashboard's Servers page lists it. The server ID is built from `ServerName` (or the machine name), the process ID and a new GUID, all lowercase, for example `billing-worker:4120:1f0c6a3e-...`. A new GUID is generated every time the server starts, so two servers never share an ID, even in the same process.

On start the server writes its options to the log (worker count, queues, shutdown timeout, polling interval). A queue name that does not match the allowed format (lowercase letters, digits, `_` and `-`) produces a warning here; it does not stop the server.

## Options

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `ServerName` | `string?` | `null` | The first part of the server ID. `null` uses the `COMPUTERNAME` environment variable, then `HOSTNAME`, then `Environment.MachineName`. Use it to tell servers apart on the dashboard, for example when one process runs several servers. |
| `WorkerCount` | `int` | `Math.Min(Environment.ProcessorCount * 5, 20)` | How many jobs the server runs at the same time. Each worker is a dedicated thread. Must be greater than 0, otherwise `ArgumentOutOfRangeException`. See [Queues and workers](queues-and-workers.md#choosing-the-worker-count). |
| `Queues` | `string[]` | `{ "default" }` | The queues the workers fetch from. Must not be `null` (`ArgumentNullException`) or empty (`ArgumentException`). See [Queues and workers](queues-and-workers.md) for how the order is used. |
| `IsLightweightServer` | `bool` | `false` | When `true`, the server runs only workers and the required processes (heartbeat, watchdog, cancellation watcher, the storage's required processes, and your additional processes). It does not run the delayed and recurring schedulers or the storage-wide maintenance processes. Use it for extra servers in a process that already has a full one. |
| `StopTimeout` | `TimeSpan` | 500 ms | Time between the stop signal and the moment the `CancellationToken` of every running job is canceled. Must be non-negative (or `Timeout.InfiniteTimeSpan`) and at most `Int32.MaxValue` milliseconds. |
| `ShutdownTimeout` | `TimeSpan` | 15 seconds | Time between the stop signal and the moment the server stops waiting for its processes and removes itself from the storage. Jobs still running then are abandoned and are picked up again later. Same limits as `StopTimeout`. |
| `SchedulePollingInterval` | `TimeSpan` | 15 seconds | How long the delayed and recurring job schedulers wait between runs. A delayed job runs up to this much later than its time. Must be non-negative and at most `Int32.MaxValue` milliseconds. With `TimeSpan.Zero` the recurring scheduler wakes at the start of every minute, but the delayed scheduler polls without pause, so avoid it. |
| `HeartbeatInterval` | `TimeSpan` | 30 seconds | How often the server reports that it is alive. Must be greater than zero and at most 24 hours, otherwise `ArgumentOutOfRangeException`. Keep it well below `ServerTimeout`. |
| `ServerCheckInterval` | `TimeSpan` | 5 minutes | How often the watchdog looks for dead servers. Must be greater than zero and at most 24 hours. |
| `ServerTimeout` | `TimeSpan` | 5 minutes | A server that has not sent a heartbeat for this long is considered dead and removed by the watchdog of any server. If this server itself fails to send heartbeats for this long (for example the database is unreachable), it restarts its processes. Must be greater than zero and at most 24 hours. |
| `CancellationCheckInterval` | `TimeSpan` | 5 seconds | How often the server checks whether any of its running jobs was deleted or moved to another state, so that the job's cancellation token can be signaled. Must be greater than zero and at most `Int32.MaxValue` milliseconds. |
| `FilterProvider` | `IJobFilterProvider?` | `null` | Where the server gets job filters. `null` uses `JobFilterProviders.Providers` (global filters plus filter attributes); with `AddNightShiftServer`, the `IJobFilterProvider` service. |
| `Activator` | `JobActivator?` | `null` | Creates the instances that job methods are called on. `null` uses `JobActivator.Current`; with `AddNightShiftServer`, the `JobActivator` service, which is backed by the DI container. See [Dependency injection](dependency-injection.md). |
| `TimeZoneResolver` | `ITimeZoneResolver?` | `null` | Turns the time zone IDs stored with recurring jobs into `TimeZoneInfo`. `null` uses the global resolver set by `GlobalConfiguration.Configuration.UseTimeZoneResolver(...)` (`DefaultTimeZoneResolver` unless one was set); with `AddNightShiftServer`, the `ITimeZoneResolver` service. See [Time zones and misfires](../recurring-jobs/time-zones-and-misfires.md). |
| `TaskScheduler` | `TaskScheduler?` | `TaskScheduler.Default` | Where asynchronous job methods (returning `Task` or `ValueTask`) run. The default runs them on the thread pool while the worker waits for the result. `null` runs them on the worker thread itself, with continuations pumped back onto it. Also used by the schedulers when `MaxDegreeOfParallelismForSchedulers` is greater than 1. |
| `WorkerThreadConfigurationAction` | `Action<Thread>?` | `null` | Called for each worker thread before it starts, for example to set its culture or priority. |
| `MaxDegreeOfParallelismForSchedulers` | `int` | `1` | Experimental. When greater than 1, the delayed and recurring schedulers process due jobs in parallel on `TaskScheduler`. Workers are not affected. |
| `ServerWatchdogOptions` | `ServerWatchdogOptions?` | `null` | Obsolete. When set, its `ServerTimeout` and `CheckInterval` replace `ServerTimeout` and `ServerCheckInterval`. Use those two properties instead. |

The values are read once, when the server is created. Changing the options object afterwards has no effect on a running server.

## More examples

### Read the options from configuration

Pass a configuration section to `AddNightShiftServer`, and keep the server settings in `appsettings.json`, environment variables or any other configuration source:

```csharp
using Masterly.NightShift;

builder.Services.AddNightShiftServer(builder.Configuration.GetSection("NightShift:Server"));
```

```json
{
  "NightShift": {
    "Server": {
      "ServerName": "billing-worker",
      "WorkerCount": 16,
      "Queues": [ "critical", "default" ],
      "SchedulePollingInterval": "00:00:05",
      "ShutdownTimeout": "00:00:30"
    }
  }
}
```

The keys are the property names, matched without regard to case: `ServerName`, `WorkerCount`, `Queues`, `IsLightweightServer`, `MaxDegreeOfParallelismForSchedulers`, `StopTimeout`, `ShutdownTimeout`, `SchedulePollingInterval`, `HeartbeatInterval`, `ServerCheckInterval`, `ServerTimeout` and `CancellationCheckInterval`. Time spans use the `hh:mm:ss` form (`"00:00:30"`, or `"1.00:00:00"` for a day). `Queues` is an array, or a comma-separated string, which is easier to set from an environment variable (`NightShift__Server__Queues=critical,default`); the list replaces the default queue rather than being added to it. When one source has an array and another a comma-separated string, as with an array in `appsettings.json` and the environment variable above, the comma-separated string wins, whichever source was added last: configuration merges the two forms without saying which source set which. A missing key keeps its default. The section is read when the server starts, and a value that can't be read or that the option refuses stops the start with an `InvalidOperationException` naming the key.

The settings that configuration can't hold (`Activator`, `FilterProvider`, `TimeZoneResolver`, `TaskScheduler`, `WorkerThreadConfigurationAction`) come from the container as usual. To change them, or anything else, after the section is read, add a callback:

```csharp
builder.Services.AddNightShiftServer(
    builder.Configuration.GetSection("NightShift:Server"),
    (provider, options) => options.WorkerThreadConfigurationAction = thread => thread.Priority = ThreadPriority.BelowNormal);
```

For other settings, or settings in another shape, read them yourself in the overload that takes `IServiceProvider`:

```csharp
builder.Services.AddNightShiftServer((provider, options) =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();

    options.WorkerCount = configuration.GetValue("Jobs:WorkerCount", 10);
});
```

### A second, lightweight server in the same process

Every server runs its own schedulers and storage maintenance unless it is lightweight. When one process runs several servers, make the extra ones lightweight:

```csharp
// The main server: workers, schedulers and storage maintenance
services.AddNightShiftServer(options =>
{
    options.Queues = new[] { "default" };
});

// A second server in the same process that only runs workers
services.AddNightShiftServer(options =>
{
    options.ServerName = "imports";
    options.Queues = new[] { "imports" };
    options.WorkerCount = 2;
    options.IsLightweightServer = true;
});
```

Make sure at least one server somewhere in the cluster is not lightweight, or delayed jobs, retries and recurring jobs never get enqueued.

### Pick up delayed jobs sooner

```csharp
services.AddNightShiftServer(options =>
{
    // Delayed and recurring jobs are picked up within about 5 seconds instead of 15
    options.SchedulePollingInterval = TimeSpan.FromSeconds(5);
});
```

Each run of the schedulers queries the storage, so with many servers a short interval adds load. One server with a short interval is enough: all servers share the same scheduled jobs.

### Give jobs more time to stop

```csharp
services.AddNightShiftServer(options =>
{
    options.StopTimeout = TimeSpan.FromSeconds(5);
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

// Give the host at least as long as the server needs
services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(45));
```

See [Graceful shutdown](queues-and-workers.md#graceful-shutdown) for the full sequence.

### Set the culture of worker threads

```csharp
using System.Globalization;
using Masterly.NightShift;

services.AddNightShiftServer(options =>
{
    options.WorkerThreadConfigurationAction = thread =>
    {
        thread.CurrentCulture = CultureInfo.InvariantCulture;
        thread.CurrentUICulture = CultureInfo.InvariantCulture;
    };
});
```

To run each job in the culture of the code that created it, use `CaptureCulture` instead (it is on by default); see [Behavior filters](../filters/behavior.md).

### Detect dead servers faster

```csharp
services.AddNightShiftServer(options =>
{
    options.HeartbeatInterval = TimeSpan.FromSeconds(15);
    options.ServerCheckInterval = TimeSpan.FromMinutes(1);
    options.ServerTimeout = TimeSpan.FromMinutes(2);
});
```

Removing a dead server only cleans up the server list. Jobs that were running on it come back through the storage's own mechanism: with SQL Server, after the invisibility timeout (see [SQL Server options](../storage/sql-server-options.md)).

## Things to know

- **Custom pipeline services win.** When `IBackgroundJobFactory`, `IBackgroundJobPerformer` and `IBackgroundJobStateChanger` are all registered in the container, `AddNightShiftServer` uses them, and `FilterProvider` and `Activator` are not used by the workers (your performer decides).
- **`AddNightShiftServer()` without a callback** uses a `BackgroundJobServerOptions` registered in the container if there is one, otherwise the defaults. The overloads with a callback or a configuration section always start from a new `BackgroundJobServerOptions`. `services.Configure<BackgroundJobServerOptions>(...)` is not read; pass the section to `AddNightShiftServer` instead.
- **The default worker count is capped at 20**, however many cores the machine has. Set `WorkerCount` explicitly on large machines or for I/O-bound work.
- **`ServerTimeout` should be several times `HeartbeatInterval`.** With a timeout shorter than the interval, healthy servers are removed by the watchdog and then have to restart.

## See also

- [Queues and workers](queues-and-workers.md)
- [Dependency injection](dependency-injection.md)
- [Drain mode](drain-mode.md)
- [Keep-alive](keep-alive.md)
- [Core concepts](../getting-started/core-concepts.md)
- [Cancellation](../jobs/cancellation.md)
