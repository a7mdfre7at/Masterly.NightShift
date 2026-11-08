# Managing jobs

Once a job exists you can delete it, run it again, move it to another time, or put it in any state, from code or from the dashboard. This page also explains every state a job can be in, and how to read jobs, their states and counts through the monitoring API.

## Quick example

```csharp
using Masterly.NightShift;

bool deleted = jobs.Delete(jobId);                                 // to Deleted
bool requeued = jobs.Requeue(jobId);                               // to Enqueued, runs again
bool moved = jobs.Reschedule(jobId, TimeSpan.FromMinutes(30));     // to Scheduled
```

`jobs` is an `IBackgroundJobClient`. The static facade has the same methods: `BackgroundJob.Delete(jobId)`, `BackgroundJob.Requeue(jobId)`, `BackgroundJob.Reschedule(jobId, TimeSpan.FromMinutes(30))`.

## How it works

Nothing is ever edited in place: every action is a state change. `Delete` moves the job to the `Deleted` state, `Requeue` to `Enqueued`, `Reschedule` to `Scheduled`. They all call `IBackgroundJobClient.ChangeState`, which goes through the state machine:

1. It takes a distributed lock on the job and reads its current state.
2. If you gave an expected state (`fromState`) and the job is in another state, nothing changes and the method returns `false`.
3. Election filters can replace the requested state. For example, `AutomaticRetry` turns a requested `Failed` into `Scheduled` while attempts are left.
4. The new state is applied in one transaction: the old state's handler undoes its side effects (for example, removes the job from the schedule), the new one applies its own (adds it to a queue), and the state is appended to the job's history.

The methods return `true` only when the requested state was applied. They return `false` when the job doesn't exist (or expired), when the expected state didn't match, or when a filter chose a different state.

Changing the state of a running job doesn't stop the method that is running. Its cancellation token is canceled within a few seconds, so a job that observes its token stops; one that doesn't runs to the end and its result is discarded. See [Cancellation](cancellation.md).

## Job states

| State | Final | How a job gets there | What happens next |
|---|---|---|---|
| `Enqueued` | No | `Enqueue`, `Requeue`, a scheduled job falling due, a continuation whose parent finished | A worker on a server that listens to its queue fetches it and moves it to `Processing` |
| `Scheduled` | No | `Schedule`, `Reschedule`, `AutomaticRetry` after a failure | At `EnqueueAt`, the `DelayedJobScheduler` moves it to `Enqueued` |
| `Awaiting` | No | `ContinueJobWith`, `ContinueJobWithAll`/`Any`, `BatchJob.ContinueWith` | When its parent(s) finish, it moves to its next state or to `Deleted` |
| `Processing` | No | A worker started it | The method returns (`Succeeded`), throws (`Failed`), or the job is deleted or moved while running |
| `Succeeded` | Yes | The method returned | Kept until it expires |
| `Failed` | No | The method threw and no retry is left (or retries are off) | Stays until you requeue or delete it, or until its retention period ends ([Failed jobs](../filters/timing.md#failed-jobs)) |
| `Deleted` | Yes | `Delete`, the dashboard, a continuation whose condition wasn't met, a filter that canceled the job, `AutomaticRetry` with `AttemptsExceededAction.Delete` | Kept until it expires |

When a job enters a final state (`Succeeded` or `Deleted`), its data gets an expiration time: `JobStorage.JobExpirationTimeout`, one day by default. Set it with `WithJobExpirationTimeout(...)` after the storage, or per method with `[ExpireAfter]` (see [Timing filters](../filters/timing.md)). When a job leaves a final state, for example when you requeue a deleted job, the expiration is removed again.

Each state is a class in `Masterly.NightShift.States`:

| Class | Notable members | Created by you? |
|---|---|---|
| `EnqueuedState` | `EnqueuedState()`, `EnqueuedState(string? queue)`, `Queue`, `EnqueuedAt`, `DefaultQueue` (`"default"`) | Yes |
| `ScheduledState` | `ScheduledState(TimeSpan enqueueIn)`, `ScheduledState(DateTime enqueueAt)` (UTC), `EnqueueAt`, `ScheduledAt` | Yes |
| `AwaitingState` | `AwaitingState(string parentId, IState nextState, JobContinuationOptions options)`, `ParentId`, `NextState`, `Options` | Yes, see [Continuations](continuations.md) |
| `AwaitingJobsState` | `ParentIds`, `NextState`, `Options`, `Mode` | Yes, see [Fan-in and batches](fan-in-and-batches.md) |
| `ProcessingState` | `ServerId`, `WorkerId`, `StartedAt` | No, only workers create it |
| `SucceededState` | `Result`, `Latency`, `PerformanceDuration` (milliseconds), `SucceededAt` | No |
| `FailedState` | `FailedState(Exception exception)`, `FailedState(Exception exception, string? serverId)`, `Exception`, `FailedAt`, `ServerId` | Rarely |
| `DeletedState` | `DeletedState()`, `DeletedState(ExceptionInfo? exceptionInfo)`, `ExceptionInfo`, `DeletedAt` | Yes |

Every state has `Name`, a settable `Reason` (shown in the job's history), `IsFinal`, and a static `StateName` (`"Enqueued"`, `"Scheduled"`, ...) to compare with or pass as an expected state. Create a new state object for each call; state objects are not thread-safe.

## API

### Delete, Requeue, Reschedule, ChangeState

Extension methods on `IBackgroundJobClient`; all but `ChangeState` also exist as static methods on `BackgroundJob`. All return `bool`. `fromState` is the state name the job must be in for the change to happen; `null` means any state.

| Method | New state |
|---|---|
| `Delete(string jobId)` | `Deleted` |
| `Delete(string jobId, string? fromState)` | `Deleted`, only from `fromState` |
| `Requeue(string jobId)` | `Enqueued` (on the `default` queue, or the job's own queue) |
| `Requeue(string jobId, string? fromState)` | `Enqueued`, only from `fromState` |
| `Reschedule(string jobId, TimeSpan delay)` | `Scheduled`, now plus `delay` |
| `Reschedule(string jobId, TimeSpan delay, string? fromState)` | The same, only from `fromState` |
| `Reschedule(string jobId, DateTimeOffset enqueueAt)` | `Scheduled`, at `enqueueAt` |
| `Reschedule(string jobId, DateTimeOffset enqueueAt, string? fromState)` | The same, only from `fromState` |
| `ChangeState(string jobId, IState state)` | Any state (extension method) |
| `ChangeState(string jobId, IState state, string? expectedState)` | Any state, only from `expectedState` (interface method) |

When the storage fails, the methods throw `BackgroundJobClientException`.

### Querying jobs

`JobStorage.GetMonitoringApi()` returns an `IMonitoringApi` (`Masterly.NightShift.Storage`), the read side the dashboard uses. Get the `JobStorage` from DI, or use `JobStorage.Current`. Lists are paged with `from` (zero-based offset) and `count`, and come back as `JobList<TDto>`, a list of `KeyValuePair<string, TDto>` keyed by job ID. DTOs are in `Masterly.NightShift.Storage.Monitoring`.

| Method | Returns |
|---|---|
| `JobDetails(string jobId)` | `JobDetailsDto?`: `Job`, `CreatedAt`, `ExpireAt`, `Properties` (job parameters), `History` (newest first: `StateName`, `Reason`, `CreatedAt`, `Data`), `LoadException` |
| `GetStatistics()` | `StatisticsDto`: counts of `Enqueued`, `Scheduled`, `Processing`, `Succeeded`, `Failed`, `Deleted`, `Awaiting`, `Retries`, `Recurring`, `Servers`, `Queues` |
| `Queues()` | Each queue's `Name`, `Length`, `Fetched` and `FirstJobs` |
| `Servers()` | The running servers |
| `EnqueuedJobs(string queue, int from, int perPage)`, `FetchedJobs(string queue, int from, int perPage)` | Jobs on a queue |
| `ProcessingJobs`, `ScheduledJobs`, `SucceededJobs`, `FailedJobs`, `DeletedJobs` (`int from, int count`) | Jobs in that state, with state-specific fields (for example `FailedJobDto.ExceptionMessage`, `ScheduledJobDto.EnqueueAt`, `SucceededJobDto.Result` as JSON) |
| `ScheduledCount()`, `EnqueuedCount(string queue)`, `FetchedCount(string queue)`, `FailedCount()`, `ProcessingCount()`, `SucceededListCount()`, `DeletedListCount()` | Counts |
| `SucceededByDatesCount()`, `FailedByDatesCount()`, `HourlySucceededJobs()`, `HourlyFailedJobs()` | Counts per day or hour, for graphs |

Storages that derive from `JobStorageMonitor` (SQL Server does) also have `AwaitingJobs(int from, int count)` and `AwaitingCount()` when `storage.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs)` is `true`, and `DeletedByDatesCount()` / `HourlyDeletedJobs()` for `JobStorageFeatures.Monitoring.DeletedStateGraphs`. `QueueCounts()` returns the same queues as `Queues()` without reading their first jobs; it is cheaper when `storage.HasFeature(JobStorageFeatures.Monitoring.QueueCounts)` is `true`, as on SQL Server.

### A job's state and result

`JobStorageExtensions` (namespace `Masterly.NightShift`) reads one job without its history:

| Method | Returns |
|---|---|
| `storage.GetJobState(string jobId)` | `JobStateInfo?`: the current state's `Name`, `Reason` and `Data` (by name, ignoring case), and the job's `JobCreatedAt`. `null` when the job doesn't exist, expired, or has no state yet. |
| `storage.TryGetResult<T>(string jobId, out T? result)` | `true` with the value the job returned, read from its `Succeeded` state with your serializer options. `false` when the job doesn't exist, hasn't succeeded, returned nothing (`void`, `Task` or `null`), or returned a value that couldn't be serialized. It throws `JsonException` when the stored value can't be read as `T`. |
| `storage.RequeueFailedJobs(Func<FailedJobDto, bool>? filter = null, TimeSpan? timeBudget = null)` | Requeues every failed job, or those the filter selects, and returns a `BulkJobResult` (`Processed`, `Remaining`, `Complete`). An overload takes the `IBackgroundJobClient` to use. |

`GetJobState` and `TryGetResult` read through `storage.GetReadOnlyConnection()`. For more, `storage.GetConnection()` returns a connection; dispose it when done. `GetStateData(jobId)` returns the current state, `GetJobData(jobId)` the job itself, and `GetJobParameter(jobId, name)` one parameter.

## More examples

### Cancel a reminder that hasn't been sent yet

```csharp
using Masterly.NightShift.States;

bool canceled = jobs.Delete(reminderJobId, ScheduledState.StateName);
if (!canceled)
{
    // Too late: the reminder was already enqueued, is running, or has finished.
}
```

### Change to any state

```csharp
jobs.ChangeState(jobId, new EnqueuedState("critical") { Reason = "Escalated by support" });
jobs.ChangeState(jobId, new DeletedState { Reason = "Customer canceled the order" }, ProcessingState.StateName);
```

### Retry every failed job

```csharp
BulkJobResult result = storage.RequeueFailedJobs();
Console.WriteLine($"requeued {result.Processed} jobs");
```

It reads the failed list a page at a time, like "Requeue all" on the dashboard's Failed page, and requeues each job only if it is still failed when its turn comes, so a job someone else handled in the meantime is left alone. A job that fails again while it runs isn't requeued twice. It goes on until the end of the list; pass `timeBudget` to stop earlier, for example inside a web request. The result's `Complete` is then `false`, `Remaining` says how many jobs are left at most, and another call goes on where this one stopped.

To requeue only some jobs, pass a filter. It gets each job's `FailedJobDto`:

```csharp
using Masterly.NightShift.Storage.Monitoring;

BulkJobResult result = storage.RequeueFailedJobs(
    failed => failed.ExceptionType == typeof(TimeoutException).FullName,
    timeBudget: TimeSpan.FromSeconds(20));
```

The jobs are requeued through a new `BackgroundJobClient` for the storage. With dependency injection, pass the registered client so its filters apply: `storage.RequeueFailedJobs(client, filter)`.

### List failures with their messages

```csharp
using Masterly.NightShift.Storage.Monitoring;

IMonitoringApi monitoring = storage.GetMonitoringApi();
long total = monitoring.FailedCount();
for (int from = 0; from < total; from += 100)
{
    foreach (KeyValuePair<string, FailedJobDto?> pair in monitoring.FailedJobs(from, 100))
    {
        FailedJobDto? failed = pair.Value;
        Console.WriteLine($"{pair.Key} {failed?.Job?.Method.Name}: {failed?.ExceptionMessage}");
    }
}
```

### A job status endpoint

```csharp
app.MapGet("/jobs/{id}", (string id, JobStorage storage) =>
{
    JobDetailsDto? details = storage.GetMonitoringApi().JobDetails(id);
    if (details == null)
    {
        return Results.NotFound();
    }

    StateHistoryDto? current = details.History.FirstOrDefault();
    return Results.Ok(new
    {
        id,
        state = current?.StateName,
        reason = current?.Reason,
        createdAt = details.CreatedAt,
        expireAt = details.ExpireAt,
    });
});
```

Only expose job IDs and states this way to callers that are allowed to see them; job IDs are sequential numbers in SQL Server.

### Just the current state

```csharp
JobStateInfo? state = storage.GetJobState(id);
string? stateName = state?.Name;
```

Without the extension, read it through a connection:

```csharp
using IStorageConnection connection = storage.GetReadOnlyConnection();
StateData? state = connection.GetStateData(id);
```

### A job's result

```csharp
if (storage.TryGetResult<int>(id, out int count))
{
    Console.WriteLine($"the job imported {count} rows");
}
```

### Counts for a health page

```csharp
StatisticsDto stats = storage.GetMonitoringApi().GetStatistics();
Console.WriteLine($"enqueued {stats.Enqueued}, processing {stats.Processing}, failed {stats.Failed}");
```

### Keep finished jobs longer

```csharp
services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString)
    .WithJobExpirationTimeout(TimeSpan.FromDays(7)));
```

## Things to know

- **`Delete` doesn't remove data.** It moves the job to `Deleted`; the data goes away when it expires. To remove everything at once, see [Schema and upgrades](../storage/schema-and-upgrades.md) (purge).
- **`Delete` doesn't guarantee the job won't run.** A job enqueued a moment ago may already be running, and a running job runs to the end unless it observes its cancellation token.
- **`Requeue` runs a job again from the start,** including a succeeded one. Make jobs safe to run twice.
- **`false` is not an error.** Check it when the outcome matters (the job had already moved on, expired, or a filter chose another state).
- **The monitoring API reads directly from storage.** It is meant for dashboards and tools. Don't call it in a tight loop or on every web request; large `count` values are slow.
- **Failed jobs don't expire by default.** Requeue or delete them, or give them a retention period with `[ExpireAfter(..., IncludeFailed = true)]` or `UseFailedJobRetention` ([Failed jobs](../filters/timing.md#failed-jobs)), or they pile up.

## See also

- [Fire-and-forget jobs](fire-and-forget.md)
- [Cancellation](cancellation.md)
- [Continuations](continuations.md)
- [Retries](../filters/retries.md)
- [Dashboard pages](../dashboard/pages.md)
- [Custom storage](../storage/custom-storage.md)
