# Cancellation

A running job can be asked to stop: when its server shuts down, when someone deletes or requeues it, or when a timeout filter gives up on it. The job hears about it through a `CancellationToken` (or the older `IJobCancellationToken`) parameter. Add one to every job that runs for more than a few seconds, and pass it on to the APIs you call.

## Quick example

```csharp
using Masterly.NightShift;

public class ThumbnailJobs
{
    private readonly HttpClient _http;

    public ThumbnailJobs(HttpClient http)
    {
        _http = http;
    }

    public async Task RebuildAllAsync(int albumId, CancellationToken cancellationToken)
    {
        for (int page = 0; page < 100; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpResponseMessage response = await _http.GetAsync($"https://images.example.com/albums/{albumId}/{page}", cancellationToken);
        }
    }
}

jobs.Enqueue<ThumbnailJobs>(x => x.RebuildAllAsync(albumId, CancellationToken.None));
```

`CancellationToken.None` is only a placeholder: the token isn't stored, and the server passes its own when the job runs.

## How it works

When a worker runs a job, it gives the job a token tied to two things: the server's shutdown, and the job's own state.

**Server shutdown.** When a server is asked to stop (the host shuts down, `BackgroundJobServer.SendStop()` or `Dispose()` is called), its workers stop fetching new jobs at once. After `StopTimeout` (500 ms by default) the tokens of the jobs still running are canceled. The server then waits up to `ShutdownTimeout` (15 seconds by default) for them to return. A job that ends with an `OperationCanceledException` during shutdown is put back on its queue, not marked as failed, so it runs again on the next server that picks it up.

**Deleted or moved jobs.** Every server runs a `ServerJobCancellationWatcher` that checks, every `CancellationCheckInterval` (5 seconds by default), whether each running job is still in the `Processing` state on this server and worker. If someone deleted it, requeued it, rescheduled it, or another server took it over, the watcher cancels its token. The job is then aborted: the `OperationCanceledException` it throws doesn't change its state (it keeps the state it was moved to) and it is removed from the queue.

**Timeouts.** `[JobTimeout]` cancels the same token when a job runs too long. See [Timing filters](../filters/timing.md).

Any other `OperationCanceledException` thrown by the job (one you caused yourself, for example from an `HttpClient` timeout) is an ordinary failure: the job moves to `Failed` and is retried.

A job that never looks at its token can't be interrupted. It keeps running until it returns or the process exits.

## API

### Parameter types

| Parameter | Placeholder when creating the job | Canceled when |
|---|---|---|
| `CancellationToken` | `CancellationToken.None` | Shutdown (after `StopTimeout`), the job leaves `Processing` (detected within `CancellationCheckInterval`), a timeout filter |
| `IJobCancellationToken` | `JobCancellationToken.Null` | The same; see the table below for how it checks |

### IJobCancellationToken

| Member | Meaning |
|---|---|
| `ShutdownToken` (`CancellationToken`) | The same token a `CancellationToken` parameter receives. Despite the name, it is canceled for every reason above, not only shutdown |
| `ThrowIfCancellationRequested()` | Throws `OperationCanceledException` on shutdown. It also reads the job's state from storage on every call and throws `JobAbortedException` (an `OperationCanceledException`) at once if the job was deleted or moved, without waiting for the watcher |

`JobCancellationToken` is a simple implementation for tests: `new JobCancellationToken(canceled: true)` gives a token that is already canceled. `JobCancellationToken.Null` is `null`.

### Server options

These are properties of `BackgroundJobServerOptions`; see [Server options](../server/server-options.md).

| Option | Type | Default | Meaning |
|---|---|---|---|
| `CancellationCheckInterval` | `TimeSpan` | 5 seconds | How often running jobs are checked for deletion or a state change |
| `StopTimeout` | `TimeSpan` | 500 ms | Delay between a stop request and canceling the tokens of running jobs |
| `ShutdownTimeout` | `TimeSpan` | 15 seconds | How long the server waits for running jobs during shutdown |

## More examples

### Stopping a running job from code

```csharp
jobs.Delete(jobId);
```

The job moves to `Deleted` right away. Within `CancellationCheckInterval`, its token is canceled; the job's next `ThrowIfCancellationRequested()` or awaited call that takes the token ends it. The dashboard's Delete button does the same.

### The older IJobCancellationToken

```csharp
public class LegacySyncJobs
{
    public void SyncCustomers(IReadOnlyList<int> customerIds, IJobCancellationToken cancellationToken)
    {
        foreach (int customerId in customerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyncOne(customerId);
        }
    }

    public async Task SyncAsync(IJobCancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken.ShutdownToken);
    }

    private static void SyncOne(int customerId) { }
}

jobs.Enqueue<LegacySyncJobs>(x => x.SyncCustomers(ids, JobCancellationToken.Null));
```

`ThrowIfCancellationRequested()` queries the storage on every call. In a tight loop, call it every N items, or use `ShutdownToken` instead.

### Clean up on cancellation

```csharp
public class CleanupJobs
{
    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
        }
        finally
        {
            // runs on shutdown and on delete too
        }
    }
}
```

Let the `OperationCanceledException` propagate after cleaning up. Catching and swallowing it makes the job return normally, so it is marked `Succeeded`.

### Giving long jobs more time to stop

```csharp
services.AddNightShiftServer(options =>
{
    options.CancellationCheckInterval = TimeSpan.FromSeconds(2);
    options.StopTimeout = TimeSpan.FromSeconds(5);
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});
```

Keep `ShutdownTimeout` below the host's own shutdown timeout, or the host stops the process first.

## Things to know

- **Deleting a job doesn't stop it by itself.** It changes the state; the job stops only if it observes its token. Without one, it runs to the end, and its result is ignored because it is no longer `Processing`.
- **Requeueing a running job runs it twice for a moment.** The requeued copy can start on another worker before the first one notices its canceled token.
- **A job canceled by shutdown runs again from the start.** Make long jobs resumable, for example with checkpoints (`IJobConsole.Checkpoint`, and the `Resume` / `ResumeAsync` extension methods, see [Progress, console and checkpoints](progress-and-console.md)), or split them into smaller jobs. Checkpoints survive retries and the requeue of a failed job; requeuing a succeeded or deleted job starts over. When the checkpoints can't be read because of a storage error, `Checkpoint`, `HasCheckpoint`, `Checkpoints` and `Resume` throw, so the attempt fails and is retried instead of redoing finished steps. A storage that can't read lists has no checkpoints.
- **If the process is killed**, nothing is canceled: the job stays `Processing` until the storage hands its queue entry to another worker. For SQL Server that happens after `SlidingInvisibilityTimeout` (5 minutes by default), see [SQL Server options](../storage/sql-server-options.md).
- **Swallowing `OperationCanceledException`** turns a cancellation into a success. Let it escape.

## See also

- [Job arguments](job-arguments.md)
- [Progress, console and checkpoints](progress-and-console.md)
- [Managing jobs](managing-jobs.md)
- [Timing filters](../filters/timing.md)
- [Server options](../server/server-options.md)
- [Drain mode](../server/drain-mode.md)
