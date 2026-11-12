# Reliability and cluster

What happens when a server dies in the middle of a job, how a new deployment takes over from the old one, how to start and stop servers from your code, how long jobs keep their own workers, and how to keep storage small. Everything on this page is opt-in except lease fencing, which costs nothing until a job is picked up again.

## Quick example

```csharp
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString)
    .UseStaleRestartLimit(5)
    .UseDeploymentFencing()
    .UseJobRetention(o => { o.Succeeded = TimeSpan.FromHours(6); o.Deleted = TimeSpan.FromDays(7); })
    .UseArgumentCompression(minLength: 4096));

builder.Services.AddNightShiftServer(options =>
{
    options.WorkerCount = 20;
    options.LongRunningWorkerCount = 2;
});
```

## When a server dies during a job

A job runs at least once. When its server dies (a crash, a killed container, a lost network) or stops before the job finished, the job stays in the `Processing` state and another server picks it up again once the storage notices: on SQL Server, when the job's invisibility timeout passes. The job runs again from the start.

### Restart limit

A job that kills its server (it runs out of memory, it crashes the process) would loop through the cluster, killing one server after the other. `UseStaleRestartLimit(n)` stops that:

```csharp
GlobalConfiguration.Configuration.UseStaleRestartLimit(5);
```

Each restart is counted in the job's `StaleRestarts` parameter. The restart past the limit fails the job for good, without a retry, with the reason `Failed: stale: its server stopped 6 times before it finished`, and the [failure continuations](../jobs/continuations.md#jobcontinuationoptions) start. `0` fails a job the first time its server stops; `null`, the default, sets no limit.

A server that stops gracefully also puts its running jobs back, after its stop timeout, so the count includes deployments: set the limit above the number of deployments a long job may see. The default global filter `StaleJobFilter` (`StaleJobFilter.MaxRestarts`) applies it.

### Lease fencing

When a job is picked up again, the worker that takes it over records itself in the job's `Lease` parameter. If the former owner wasn't dead after all (a long garbage collection, a network partition) and finishes late, its state change is refused: the job keeps the outcome of the run that replaced it, and the late result is dropped. Nothing is written for a job that was never picked up again, so normal processing costs nothing. The check reads the parameters the storage loads with the job; SQL Server loads them, on another storage the check is skipped.

## Deployment fencing

During a rolling deployment, old and new servers run side by side, and an old server can pick up a job created for the new code. `UseDeploymentFencing()` keeps servers of an older deployment from taking jobs once a newer one runs:

```csharp
GlobalConfiguration.Configuration.UseDeploymentFencing();          // the entry assembly's version
GlobalConfiguration.Configuration.UseDeploymentFencing("2026.11.3"); // or your own id, up to 64 characters
```

1. Every server created after the call checks the deployment every 15 seconds (`DeploymentFencing.CheckInterval`).
2. The first server of a deployment the cluster hasn't seen claims it: it becomes the current deployment.
3. A server of any other deployment pauses itself, as [drain mode](drain-mode.md) pauses a server, with the reason `Fenced: deployment '...' was replaced by '...'`. Jobs it picks up are held and taken by the current servers; its running jobs finish.
4. A deployment seen before (an old version restarted by its orchestrator, a rollback) takes over only once no server of the current deployment has checked in for 30 seconds (`DeploymentFencing.TakeOverAfter`), so a crash-looping old server doesn't fence the new ones. To roll back on purpose, deploy with a new id.

A server paused by fencing resumes by itself when its deployment becomes the current one again; a pause set by hand stays. `DeploymentFencing.GetCurrentDeployment(storage)` reads the current deployment. The state is kept in the storage hash `nightshift:deployment`. The default id is the entry assembly's informational version, so make sure it changes with every release.

## Starting and stopping servers from code

```csharp
builder.Services.AddNightShiftServer(options => options.StartMode = ServerStartMode.Manual);

app.MapPost("/admin/jobs/start", (IBackgroundJobServerControl servers) => servers.StartAsync());
app.MapPost("/admin/jobs/stop", (IBackgroundJobServerControl servers, CancellationToken token) => servers.StopAsync(token));
```

With `StartMode = ServerStartMode.Manual`, a server added with `AddNightShiftServer` doesn't start with the host. `IBackgroundJobServerControl`, registered by `AddNightShiftServer`, controls every server of the container:

| Member | What it does |
|---|---|
| `StartAsync()` | Starts the servers that aren't running. A server stopped before starts again with a new server id. Throws `InvalidOperationException` once the host has stopped a server |
| `StopAsync(cancellationToken)` | Stops the running servers and waits for them to shut down, with their stop and shutdown timeouts |
| `IsRunning` | Whether at least one server runs |

It works for automatic servers too, to stop processing during a maintenance window and start it again. Calls made before the application has started are honoured: a server started by `StartAsync` isn't started a second time with the host, and an automatic server stopped by `StopAsync` stays stopped when the application starts. Once the host is stopping, nothing starts a server again. A `BackgroundJobServer` you create yourself always starts at once.

## A pool for long jobs

A few long jobs (an import, a transcode, a full reindex) can take every worker and make short jobs wait. Mark them with `[LongRunning]`, and give servers a pool of their own for them:

```csharp
public class Reindexer
{
    [LongRunning]
    public Task RebuildAsync(CancellationToken cancellationToken) => ...;
}

builder.Services.AddNightShiftServer(options => options.LongRunningWorkerCount = 2);
```

`[LongRunning]` sends the job to the `long-running` queue (`LongRunningAttribute.Queue`), like `[Queue("long-running")]`, which it replaces. `LongRunningWorkerCount` adds that many workers serving only that queue, besides `WorkerCount`. With 0, the default, the queue is served only by servers that list it in `Queues`; without such a server, the jobs wait. When a process runs servers but none of them serves the queue, the first `[LongRunning]` job it sends there logs a warning, once, since the jobs wait unless a server in another process serves the queue. A process without servers, which only creates jobs, doesn't warn.

## Storage size

### Retention per state

Succeeded and deleted jobs are kept a day by default, failed ones until they are deleted or requeued. `UseJobRetention` sets it per state:

```csharp
GlobalConfiguration.Configuration.UseJobRetention(o =>
{
    o.Succeeded = TimeSpan.FromHours(6);
    o.Deleted = TimeSpan.FromDays(7);
    o.Failed = TimeSpan.FromDays(30);
    o.Set("Archived", TimeSpan.FromDays(365)); // a custom final state
});
```

A retention is positive and at most 100 years. `Failed` is the same as `UseFailedJobRetention`. A method's [`[ExpireAfter]`](../filters/timing.md#expireafterattribute) wins over the global retention. Calling `UseJobRetention` again replaces it, the failed retention an earlier call set included: a call without `Failed` keeps failed jobs again. A failed retention set with `UseFailedJobRetention` stays unless the new call sets `Failed`.

### Argument compression

```csharp
GlobalConfiguration.Configuration.UseArgumentCompression(minLength: 4096);
```

The stored arguments of new jobs that are at least `minLength` characters long (1,024 by default) are compressed with GZip and stored in base64 behind the marker `gz:`. Arguments that don't get shorter are kept as they are. Arguments are compressed only at the newest [data compatibility level](../configuration/compatibility-levels.md), `CompatibilityLevel.Version_180` (`ArgumentCompression.RequiredLevel`): a lower level says servers of earlier versions still share the storage, and they can't read compressed arguments, so nothing is compressed until you raise it. Compressed arguments are always read back, whatever the level, so `UseArgumentCompression(null)` turns compression off without breaking the jobs already stored. A [recurring job manifest](../recurring-jobs/recurring-job-manifest.md) exports the arguments decompressed. Compression trades CPU for storage and network; it pays for large arguments only, so prefer passing an identifier and loading the data in the job.

## See also

- [Server options](server-options.md)
- [Drain mode](drain-mode.md)
- [Queues and workers](queues-and-workers.md)
- [Retries](../filters/retries.md)
