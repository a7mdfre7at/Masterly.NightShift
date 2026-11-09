# Background processes

A background process is code that runs in a loop on the server for as long as the server runs, outside of any job: relaying an outbox table, exporting metrics, warming a cache, pinging a URL. The server's own parts (workers, schedulers, heartbeat, watchdog) are background processes too. Write one when the work is continuous and belongs to the server's lifetime; when the work runs on a timetable, a [recurring job](../recurring-jobs/README.md) is usually simpler, because it runs once per occurrence across the whole cluster and shows up on the dashboard.

## Quick example

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;

public sealed class OutboxRelay : IBackgroundProcess
{
    public void Execute(BackgroundProcessContext context)
    {
        // Do one round of work...
        Console.WriteLine($"Relaying outbox messages on server {context.ServerId}");

        // ...then wait. Wait returns early, by throwing, when the server stops.
        context.Wait(TimeSpan.FromSeconds(10));
    }
}
```

Register it in the container, next to the server:

```csharp
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Jobs")!));

builder.Services.AddSingleton<IBackgroundProcess, OutboxRelay>();
builder.Services.AddNightShiftServer();
```

Without a container, pass it to the server:

```csharp
using (var server = new BackgroundJobServer(
    new BackgroundJobServerOptions(),
    JobStorage.Current,
    new IBackgroundProcess[] { new OutboxRelay() }))
{
    Console.ReadLine();
}
```

## How it works

The server calls `Execute` again and again, on a thread of its own, until the server stops. One call is one round of work. The method must wait before it returns, or the loop spins and uses a whole core. Wait with `context.Wait(timeout)`: it returns after the timeout, and throws `OperationCanceledException` as soon as the server is told to stop, which ends the loop without an error.

When `Execute` throws any other exception, the server calls `Execute` again after a delay that grows with every failure in a row: 1 second, 4, 9, 16 and so on (the square of the attempt number), at most 5 minutes. A successful round resets the count. The first failures are logged at the debug level only; once the process has kept failing for more than 15 seconds, the exception is logged as an error, and again at most once a minute while the failures go on. One failing process doesn't stop the server or its other processes.

Each process gets the server's stop signals through the context:

| Signal | When | What to do |
| --- | --- | --- |
| `StoppingToken` | As soon as the server is told to stop (`SendStop`, `Dispose`, the host stopping). | Finish the current round and return. `Wait` reacts to this one. |
| `StoppedToken` | `StopTimeout` (500 ms by default) after the stop signal. | Abort long operations: pass it to I/O that must end before shutdown. |
| `ShutdownToken` | `ShutdownTimeout` (15 seconds by default) after the stop signal. The server stops waiting for its processes. | Nothing: the server has given up on you. |

The server waits for every process to return before it removes itself from the storage, up to `ShutdownTimeout`. See [Graceful shutdown](queues-and-workers.md#graceful-shutdown).

### Where processes run

- **`BackgroundJobServer`** runs every process in its `additionalProcesses` constructor argument, each on one dedicated thread. It doesn't dispose them.
- **`AddNightShiftServer`** passes every `IBackgroundProcess` registered in the container to each server it adds. The overloads that take an `IEnumerable<IBackgroundProcess>` use that list instead and ignore the container. See [Dependency injection](dependency-injection.md#addnightshiftserver).
- **`BackgroundProcessingServer`** runs only the processes you give it (plus the heartbeat, the watchdog, the cancellation watcher, the storage's `GetServerRequiredProcesses()` and, unless `ExcludeStorageProcesses` is `true`, the storage's maintenance processes). Use it for asynchronous processes, or to run processes without workers. See [Run an asynchronous process](#run-an-asynchronous-process).

A process runs on every server that has it. With three servers, three copies run at the same time. When only one copy may do the work at a time, take a distributed lock in each round (see [Run on one server at a time](#run-on-one-server-at-a-time)), or schedule a recurring job instead.

## API

All types are in the `Masterly.NightShift.Server` namespace, in `Masterly.NightShift.Core`.

### IBackgroundProcess and IBackgroundProcessAsync

| Interface | Member | Meaning |
| --- | --- | --- |
| `IBackgroundProcess` | `void Execute(BackgroundProcessContext context)` | One round of synchronous work, called in a loop on a dedicated thread. |
| `IBackgroundProcessAsync` | `Task ExecuteAsync(BackgroundProcessContext context)` | One round of asynchronous work, called in a loop. Wait with `await Task.Delay(interval, context.StoppingToken)`. |

`BackgroundJobServer` and the `AddNightShiftServer` overloads accept only `IBackgroundProcess`. An `IBackgroundProcessAsync` runs in a `BackgroundProcessingServer`, through one of the `UseBackgroundPool` or `UseThreadPool` extensions below.

### BackgroundProcessContext

| Member | Type | Meaning |
| --- | --- | --- |
| `ServerId` | `string` | The ID of the server running the process, as shown on the dashboard's Servers page. |
| `Storage` | `JobStorage` | The server's storage. Open connections with `Storage.GetConnection()` and dispose them in the same round. |
| `Properties` | `IReadOnlyDictionary<string, object>` | The server's properties. A `BackgroundJobServer` puts `Queues` and `WorkerCount` there. Keys are case-insensitive. |
| `ExecutionId` | `Guid` | Identifies this run of the process loop, for logs. |
| `StoppingToken`, `StoppedToken`, `ShutdownToken` | `CancellationToken` | The stop signals described above. |
| `IsStopping` | `bool` | `true` once any of the three tokens is canceled. |
| `IsStopped` | `bool` | `true` once `StoppedToken` or `ShutdownToken` is canceled. |
| `Wait(TimeSpan timeout)` | `void` | Waits for `timeout`. Throws `OperationCanceledException` when `StoppingToken` is canceled. |


### BackgroundProcessExtensions

These extensions turn a process into an `IBackgroundProcessDispatcherBuilder`, which is what `BackgroundProcessingServer` runs. They decide how many threads (or concurrent loops) run the process.

| Extension | Meaning |
| --- | --- |
| `IBackgroundProcess.UseBackgroundPool()` | One loop per processor (`Environment.ProcessorCount` dedicated threads). |
| `IBackgroundProcess.UseBackgroundPool(int threadCount)` | `threadCount` dedicated threads, each running its own loop. Use `1` for most processes. |
| `IBackgroundProcess.UseBackgroundPool(int threadCount, Action<Thread> threadConfig)` | The same, with `threadConfig` called for each thread before it starts (name, priority, culture). |
| `IBackgroundProcess.UseBackgroundPool(Func<string, ThreadStart, IEnumerable<Thread>> threadFactory)` | Threads you create yourself. The `string` is the process type name. |
| `IBackgroundProcessAsync.UseBackgroundPool(int maxConcurrency)` | `maxConcurrency` concurrent loops on a scheduler with dedicated threads (at most one thread per processor). Without the argument, one loop per processor. |
| `IBackgroundProcessAsync.UseBackgroundPool(int maxConcurrency, int threadCount)` | The same, with `threadCount` threads. |
| `IBackgroundProcessAsync.UseBackgroundPool(int maxConcurrency, Func<string, ThreadStart, IEnumerable<Thread>> threadFactory)` | The same, with threads you create. |
| `IBackgroundProcessAsync.UseThreadPool(int maxConcurrency)` | `maxConcurrency` concurrent loops on the .NET thread pool. Without the argument, one loop per processor. |

Values of zero or less throw `ArgumentOutOfRangeException`. `BackgroundJobServer` and `AddNightShiftServer` use `UseBackgroundPool(1)` for every additional process.

### BackgroundProcessingServer

A server that runs background processes and nothing else. `BackgroundJobServer` is built on it. It implements `IBackgroundProcessingServer` (`SendStop()`, `WaitForShutdown(TimeSpan)`, `WaitForShutdownAsync(CancellationToken)`, `Dispose()`), the same stop members as `BackgroundJobServer`; see [Stopping a server you created](queues-and-workers.md#stopping-a-server-you-created).

| Constructor | Meaning |
| --- | --- |
| `BackgroundProcessingServer(IEnumerable<IBackgroundProcess> processes)` | Runs `processes` on `JobStorage.Current`, one thread each, with default options. |
| `BackgroundProcessingServer(IEnumerable<IBackgroundProcess> processes, IDictionary<string, object> properties)` | The same, with server properties. |
| `BackgroundProcessingServer(JobStorage storage, IEnumerable<IBackgroundProcess> processes)` | On `storage`. |
| `BackgroundProcessingServer(JobStorage storage, IEnumerable<IBackgroundProcess> processes, IDictionary<string, object> properties)` | On `storage`, with properties. |
| `BackgroundProcessingServer(JobStorage storage, IEnumerable<IBackgroundProcess> processes, IDictionary<string, object> properties, BackgroundProcessingServerOptions options)` | With options. |
| `BackgroundProcessingServer(JobStorage storage, IEnumerable<IBackgroundProcessDispatcherBuilder> dispatcherBuilders, IDictionary<string, object> properties, BackgroundProcessingServerOptions options)` | With dispatcher builders from the extensions above: the only way to run an `IBackgroundProcessAsync`, or a process on several threads. |

The server starts when it is created. Like `BackgroundJobServer`, it announces itself in the storage, sends heartbeats and appears on the dashboard's Servers page; its `properties` are what the page shows (`Queues` as `string[]` and `WorkerCount` as `int`, when present).

### BackgroundProcessingServerOptions

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `ServerName` | `string` | `null` | The first part of the server ID; `null` uses the machine name, as for [`BackgroundJobServerOptions.ServerName`](server-options.md#options). |
| `ExcludeStorageProcesses` | `bool` | `false` | `true` skips the storage's maintenance processes (for SQL Server, `ExpirationManager` and `CountersAggregator`). Set it when another server in the process already runs them. |
| `HeartbeatInterval` | `TimeSpan` | 30 seconds | How often the server reports that it is alive. |
| `ServerCheckInterval` | `TimeSpan` | 5 minutes | How often the watchdog looks for dead servers. |
| `ServerTimeout` | `TimeSpan` | 5 minutes | After how long without a heartbeat a server counts as dead. |
| `CancellationCheckInterval` | `TimeSpan` | 5 seconds | How often running jobs are checked for deletion. |
| `StopTimeout` | `TimeSpan` | 500 ms | Time from the stop signal to `StoppedToken`. |
| `ShutdownTimeout` | `TimeSpan` | 15 seconds | Time from the stop signal to `ShutdownToken`, when the server stops waiting for its processes. |
| `LastChanceTimeout` | `TimeSpan` | 1 second | How long the server waits for its processes when the process or the app domain is exiting. |
| `RetryDelay` | `Func<int, TimeSpan>` | the square of the attempt number, in seconds, at most 5 minutes | The delay before a failed process runs again, from the number of failures in a row. `null` throws `ArgumentNullException`. |
| `RestartDelay` | `TimeSpan` | 15 seconds | The delay before the whole server restarts after it failed, for example when the storage was unreachable. |

These options are not validated. `BackgroundJobServerOptions` validates its copies of them; see [Server options](server-options.md).

### Advanced: dispatcher builders

`IBackgroundProcessDispatcherBuilder` has one method, `IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)`, called once when the server starts. `BackgroundServerContext` carries the server ID, the storage, the properties and the three tokens. `IBackgroundDispatcher` (namespace `Masterly.NightShift.Processing`) is what the server waits on at shutdown: `Wait(TimeSpan)`, `WaitAsync(TimeSpan, CancellationToken)` and `Dispose()`. Implement them only to run processes in a way the extensions don't offer. `BackgroundTaskScheduler`, a `TaskScheduler` on dedicated threads, is what `IBackgroundProcessAsync.UseBackgroundPool` uses.

The built-in processes `Worker`, `DelayedJobScheduler` and `RecurringJobScheduler` are public `IBackgroundProcess` classes, so a `BackgroundProcessingServer` can run them too. `BackgroundJobServer` puts them together from its options, which is what you want in almost every case.

## More examples

### Run on one server at a time

Every server runs its own copy of the process. When the work must not overlap, take a distributed lock for each round and skip the round when another server holds it:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

public sealed class OutboxRelay : IBackgroundProcess
{
    public void Execute(BackgroundProcessContext context)
    {
        using (var connection = context.Storage.GetConnection())
        {
            try
            {
                using (connection.AcquireDistributedLock("outbox-relay", TimeSpan.Zero))
                {
                    // Only one server gets here at a time
                }
            }
            catch (DistributedLockTimeoutException)
            {
                // Another server is relaying; try again next round
            }
        }

        context.Wait(TimeSpan.FromSeconds(10));
    }
}
```

### Run an asynchronous process

Create a `BackgroundProcessingServer` in the factory overload of `AddNightShiftServer`. The host starts it, stops it and disposes it:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;

public sealed class MetricsExporter : IBackgroundProcessAsync
{
    public async Task ExecuteAsync(BackgroundProcessContext context)
    {
        var monitoring = context.Storage.GetMonitoringApi();
        long enqueued = monitoring.GetStatistics().Enqueued;
        Console.WriteLine($"{enqueued} jobs waiting");

        await Task.Delay(TimeSpan.FromSeconds(30), context.StoppingToken);
    }
}
```

```csharp
builder.Services.AddNightShiftServer();

builder.Services.AddNightShiftServer(provider => new BackgroundProcessingServer(
    provider.GetRequiredService<JobStorage>(),
    new[] { new MetricsExporter().UseBackgroundPool(maxConcurrency: 1) },
    new Dictionary<string, object>(),
    new BackgroundProcessingServerOptions
    {
        ServerName = "metrics",
        ExcludeStorageProcesses = true, // the job server above already runs them
    }));
```

The `OperationCanceledException` that `Task.Delay` throws when the server stops ends the loop quietly, like `context.Wait`.

### A process with dependencies

A process registered as a singleton is created once and shared by every server in the process, so it can't take scoped services in its constructor. Create a scope in each round:

```csharp
public sealed class CacheWarmup : IBackgroundProcess
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CacheWarmup(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Execute(BackgroundProcessContext context)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ProductCatalog>();
            catalog.Warm();
        }

        context.Wait(TimeSpan.FromMinutes(10));
    }
}
```

```csharp
builder.Services.AddScoped<ProductCatalog>();
builder.Services.AddSingleton<IBackgroundProcess, CacheWarmup>();
builder.Services.AddNightShiftServer();
```

With two `AddNightShiftServer` calls, both servers call `Execute` on the same instance, from two threads, so keep its fields thread-safe.

## Things to know

- **Always wait in each round.** An `Execute` that returns at once is called again at once, forever.
- **Don't swallow `OperationCanceledException` from `context.Wait`.** It is how the loop ends when the server stops. A `catch (Exception)` around the whole round turns a stop into another round; catch your own exceptions only.
- **Exceptions don't stop the process.** The round runs again after a growing delay, so a permanent error (a missing table, a wrong URL) shows up as a repeated error in the log, not as a stopped server. A failure that clears up within 15 seconds is logged at the debug level only; log inside `Execute` if you want to see every one.
- **Lightweight servers run them too.** `IsLightweightServer` turns off the schedulers and the storage maintenance, not your additional processes.
- **The keep-alive process is an example.** [`KeepAliveProcess`](keep-alive.md) is an `IBackgroundProcess` that `AddNightShiftKeepAlive` registers in the container.

## See also

- [Server options](server-options.md)
- [Dependency injection](dependency-injection.md)
- [Keep-alive](keep-alive.md)
- [Queues and workers](queues-and-workers.md#graceful-shutdown)
- [Recurring jobs](../recurring-jobs/README.md)
