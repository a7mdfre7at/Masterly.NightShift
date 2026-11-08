# Fan-in and batches

Fan-out/fan-in is splitting work into many jobs that run in parallel, then running one job when they are done. `ContinueJobWithAll` waits for every parent, `ContinueJobWithAny` for the first one. `BatchJob` groups jobs under one ID so you can follow their progress on the dashboard and continue after the whole group.

## Quick example

```csharp
using Masterly.NightShift;

public class ExportService
{
    public Task ExportRegionAsync(string region) => Task.CompletedTask;
    public Task MergeAsync() => Task.CompletedTask;
    public void NotifyFirstDone() { }
}

string?[] exports =
{
    jobs.Enqueue<ExportService>(x => x.ExportRegionAsync("eu")),
    jobs.Enqueue<ExportService>(x => x.ExportRegionAsync("us")),
    jobs.Enqueue<ExportService>(x => x.ExportRegionAsync("apac")),
};

jobs.ContinueJobWithAll<ExportService>(exports, x => x.MergeAsync());
```

`jobs` is an `IBackgroundJobClient`; `BackgroundJob.ContinueJobWithAll(...)` does the same through the static facade.

## How it works

### Fan-in continuations

1. `ContinueJobWithAll` / `ContinueJobWithAny` drop `null` and empty parent IDs (creations canceled by a filter), remove duplicates, and create the child in the `Awaiting` state with an `AwaitingJobsState`. It records every parent ID, the next state (`Enqueued` by default), the `JobContinuationOptions` and the `FanInMode` (`All` or `Any`).
2. The `FanInContinuationsSupportAttribute` filter, registered globally by default, adds the child's ID to each parent's `FanInContinuations` job parameter, under a distributed lock per parent. It first checks that every parent exists: a parent that doesn't exist makes creation fail before the child is added to any parent. If enough parents have already finished, the child is decided right away. Otherwise it checks its parents again once its own `Awaiting` state is committed, in case a parent finished while the child was being created.
3. Each time a parent moves to `Succeeded` or `Deleted`, the filter decides the parent's children once that state change is committed, on the thread that made it (a worker, a dashboard request, a `ChangeState` or `Delete` call). It reads the committed states of the child's parents and decides:

| Mode | The child starts when | The child is deleted when |
|---|---|---|
| `All` | every parent has finished and satisfies the options | one finished parent does not satisfy the options (decided at once, without waiting for the others) |
| `Any` | one finished parent satisfies the options (the others are not waited for) | every parent has finished and none satisfies the options |

A parent satisfies `OnlyOnSucceededState` (the default) when it succeeded, `OnlyOnDeletedState` when it was deleted, and `OnAnyFinishedState` either way. A parent that no longer exists in storage counts as deleted, so a child can't wait forever for a parent that expired. A deleted child gets the reason "Continuation condition was not met".

Deciding after the commit matters when the last parents finish at the same moment: each parent reads the others only after its own state is saved, so at least one of them sees all of them finished. The child is changed under its own distributed lock and only while it is still `Awaiting`, so it starts once even when two parents decide it together.

A finished parent reads its own state first, then the other parents from the last one to the first, and stops at the first parent that hasn't finished. The last parent to finish reads them all.

`AwaitingJobsState` shares the `Awaiting` state name with ordinary continuations, so fan-in jobs appear on the same dashboard page. The job details page lists a child's parents, and a parent's fan-in children.

### Batches

1. `BatchJob.StartNew` creates the batch record (description, creation time) with a new 12-character ID, then calls your callback. Every job created during the callback, on the same async flow, gets the `BatchId` job parameter, even jobs created through another client or by code you call from the callback.
2. `BatchSupportAttribute`, a default global filter, adds each new job to the batch's job list and keeps one counter per state up to date on every state change. A state change made without filters is counted too: when every attempt to store a job's final state fails, the worker stores a `FailedState` with filters disabled, and the state machine then updates the counters itself, so the job doesn't stay counted as Processing. Removing `BatchSupportAttribute` from `GlobalJobFilters` turns this off as well.
3. `BatchJob.ContinueWith` reads the batch's current job IDs and creates a fan-in continuation (`FanInMode.All`) over them, tagged with the `BatchContinuationOf` parameter. The continuation is added to the batch's continuation list in the same transaction as its first state. That list is only for display: the fan-in mechanism above decides the continuation.
4. The dashboard's Batches page lists batches with a progress bar; each batch page lists its jobs and continuations and has buttons to requeue its failed jobs and delete its unfinished ones.

The batch's bookkeeping (its record, job list, continuation list and counters) expires `BatchJob.ExpireIn` (7 days) after creation. The job list gets its expiration when the `StartNew` callback returns, the continuation list each time `ContinueWith` adds to it. The jobs themselves expire on their own schedule.

The `batches` index has no expiration of its own. Each `StartNew` removes up to 100 batch IDs older than `ExpireIn` from it. The Batches page never writes: it skips batches that expired but are still in the index, so a page can show fewer rows than its size until the next `StartNew`.

Expiration and the per-state counters need a storage with the extended API (`JobStorageFeatures.ExtendedApi`), like SQL Server. On other storages the bookkeeping is kept until you remove it, `JobBatch.Total` counts the job list and `JobBatch.Counts` is empty.

## API

### ContinueJobWithAll and ContinueJobWithAny

Extension methods on `IBackgroundJobClient` (`BackgroundJobClientFanInExtensions`) and static methods on `BackgroundJob`. Each returns `string?`: the child's ID, or `null` when a client filter canceled the creation.

| Overload | Lambda types | Where |
|---|---|---|
| `ContinueJobWithAll(IEnumerable<string?> parentIds, methodCall, JobContinuationOptions options = OnlyOnSucceededState)` | `Action`, `Func<Task>`, `Action<T>`, `Func<T, Task>` | Both |
| `ContinueJobWithAll(IEnumerable<string?> parentIds, methodCall, IState nextState, JobContinuationOptions options = OnlyOnSucceededState)` | `Action`, `Func<Task>`, `Action<T>`, `Func<T, Task>` | `IBackgroundJobClient` only |
| `ContinueJobWithAll(IEnumerable<string?> parentIds, string queue, methodCall, IState? nextState = null, JobContinuationOptions options = OnlyOnSucceededState)` | `Action`, `Func<Task>`, `Action<T>`, `Func<T, Task>` | `IBackgroundJobClient`; `BackgroundJob` has it without `nextState` |
| `ContinueJobWithAny(IEnumerable<string?> parentIds, methodCall, JobContinuationOptions options = OnlyOnSucceededState)` | `Action`, `Func<Task>`, `Action<T>`, `Func<T, Task>` | Both |
| `ContinueJobWithAny(IEnumerable<string?> parentIds, methodCall, IState nextState, JobContinuationOptions options = OnlyOnSucceededState)` | `Action`, `Func<Task>`, `Action<T>`, `Func<T, Task>` | `IBackgroundJobClient` only |
| `ContinueJobWithAny(IEnumerable<string?> parentIds, string queue, methodCall, IState? nextState = null, JobContinuationOptions options = OnlyOnSucceededState)` | `Action`, `Func<Task>`, `Action<T>`, `Func<T, Task>` | `IBackgroundJobClient`; `BackgroundJob` has it without `nextState` |

With a `queue`, the child job is stored with that queue; a `null` `nextState` means `Enqueued`.

When every given parent ID is `null` or empty, the methods throw `ArgumentException`.

### AwaitingJobsState

| Member | Type | Meaning |
|---|---|---|
| `AwaitingJobsState(IEnumerable<string> parentIds)` | constructor | All parents must succeed; next state `Enqueued` |
| `AwaitingJobsState(IEnumerable<string> parentIds, IState nextState, JobContinuationOptions options, FanInMode mode)` | constructor | Empty IDs and duplicates are removed; at least one ID is required |
| `ParentIds` | `IReadOnlyList<string>` | The parents |
| `NextState` | `IState` | The state applied when the condition is met |
| `Options` | `JobContinuationOptions` | Which parent outcomes count |
| `Mode` | `FanInMode` | `All` or `Any` |
| `FromStateData(IDictionary<string, string> data)` | static method | Rebuilds the state from stored state data, or `null` for an ordinary `AwaitingState` |

`FanInContinuationsSupportAttribute.ReadChildren(IStorageConnection connection, string jobId)` lists a parent's fan-in children in registration order.

### BatchJob

| Member | Meaning |
|---|---|
| `StartNew(Action<IBatchBuilder> createJobs, string? description = null)` | Creates a batch in `JobStorage.Current` and returns its ID |
| `StartNew(IBackgroundJobClient client, JobStorage storage, Action<IBatchBuilder> createJobs, string? description = null)` | The same with an explicit client and storage (use this with DI) |
| `ContinueWith(string batchId, methodCall, JobContinuationOptions options = OnAnyFinishedState)` | Fan-in continuation over the batch's current jobs, through `JobStorage.Current`. Lambda types `Action`, `Action<T>`, `Func<Task>` |
| `ContinueWith(string batchId, Job job, JobContinuationOptions options = OnAnyFinishedState)` | The same with a `Job` object |
| `ContinueWith(IBackgroundJobClient client, JobStorage storage, string batchId, Job job, JobContinuationOptions options = OnAnyFinishedState)` | The same with an explicit client and storage |
| `Cancel(string batchId, TimeSpan? timeBudget = null)` | Deletes every job of the batch that hasn't succeeded or been deleted, through `JobStorage.Current`, and returns a `BulkJobResult` |
| `Cancel(IBackgroundJobClient client, JobStorage storage, string batchId, TimeSpan? timeBudget = null)` | The same with an explicit client and storage |
| `RequeueFailed(string batchId, TimeSpan? timeBudget = null)` | Requeues every failed job of the batch, through `JobStorage.Current`, and returns a `BulkJobResult` |
| `RequeueFailed(IBackgroundJobClient client, JobStorage storage, string batchId, TimeSpan? timeBudget = null)` | The same with an explicit client and storage |
| `Get(IStorageConnection connection, string batchId)` | Reads a batch as a `JobBatch`, or `null` when it doesn't exist or expired |
| `GetJobIds(IStorageConnection connection, string batchId)` | The batch's job IDs, in creation order |
| `List(IStorageConnection connection, int from, int count)` | Batches, newest first. Expired batches still in the index are skipped; nothing is written |
| `Count(IStorageConnection connection)` | How many batches are listed, including expired ones not yet removed from the index |
| `Current` (`string?`) | The batch that jobs created on the current async flow belong to, or `null` |
| `ExpireIn` (`TimeSpan`, default 7 days) | How long a batch's bookkeeping is kept |
| `MaxDescriptionLength` (`200`) | Longer descriptions throw `ArgumentException` |
| `ParameterName` (`"BatchId"`), `ContinuationParameterName` (`"BatchContinuationOf"`), `IndexKey` (`"batches"`) | Storage names |
| `GetHashKey`, `GetJobsKey`, `GetTotalKey`, `GetStateKey`, `GetContinuationsKey` | Storage keys of a batch (`batch:{id}`, `batch:{id}:jobs`, ...) |

`ContinueWith` throws `InvalidOperationException` when the batch doesn't exist, has expired, or has no jobs. `Cancel` and `RequeueFailed` throw it when the batch doesn't exist or has expired.

`Cancel` and `RequeueFailed` do what the batch page's "Delete" and "Requeue failed" buttons do. They read the states of the batch's jobs 500 at a time and change each selected job only if it is still in the state that was read, so a job that succeeds meanwhile isn't deleted, and a job someone else requeued isn't requeued twice. `Cancel` deletes enqueued, scheduled, awaiting, processing and failed jobs; a processing job keeps running until it checks its cancellation token. Without a `timeBudget` they go on until every job is reached. With one, they stop when the time is up: the result's `Complete` is `false`, `Remaining` is the number of jobs not reached, and another call goes on with them.

`BulkJobResult` has `Processed` (the jobs whose state was changed), `Remaining` (`0` when complete) and `Complete`.

### IBatchBuilder

The callback of `StartNew` receives an `IBatchBuilder`. It creates jobs through the client given to `StartNew`.

| Member | Meaning |
|---|---|
| `BatchId` | The new batch's ID |
| `Enqueue(methodCall)` | Lambda types `Action`, `Action<T>`, `Func<Task>`, `Func<T, Task>` |
| `Schedule(methodCall, TimeSpan delay)` | Lambda types `Action`, `Action<T>` |
| `Create(Job job, IState state)` | Any job in any state |

### JobBatch

| Property | Type | Meaning |
|---|---|---|
| `Id`, `Description`, `CreatedAt` | `string`, `string?`, `DateTime?` | The batch record |
| `Total` | `long` | Jobs created in the batch |
| `Counts` | `IReadOnlyDictionary<string, long>` | Jobs per state name right now |
| `Succeeded`, `Failed`, `Deleted`, `Processing` | `long` | Shortcuts into `Counts` |
| `Pending` | `long` | Neither finished nor failed: enqueued, scheduled, awaiting or processing |
| `IsFinished` | `bool` | Every job succeeded or was deleted |
| `IsStuck` | `bool` | Some jobs failed and nothing is pending any more |
| `Progress` | `double` | Succeeded jobs as a percentage, 0 to 100 |
| `Continuations` | `IReadOnlyList<string>` | IDs of the jobs created with `ContinueWith` |
| `Count(string state)` | `long` | Jobs in one state |

## More examples

### Start on the first parent

```csharp
jobs.ContinueJobWithAny<ExportService>(exports, x => x.NotifyFirstDone());
```

### Wait for every parent, whatever happened to them

```csharp
jobs.ContinueJobWithAll<ExportService>(exports, x => x.MergeAsync(), JobContinuationOptions.OnAnyFinishedState);
```

### A custom next state

```csharp
using Masterly.NightShift.States;

public static class ExportNotifications
{
    public static void SendSummary() { }
}

jobs.ContinueJobWithAll(exports, () => ExportNotifications.SendSummary(), new EnqueuedState("reports"));
```

### A batch with DI

Inject `IBackgroundJobClient` and `JobStorage` (both registered by `AddNightShift`):

```csharp
using Masterly.NightShift.Common;

public class InvoiceService
{
    public Task RenderAsync(int invoiceId) => Task.CompletedTask;
}

public class InvoiceRunReport
{
    public void Send(string batchId)
    {
        // e-mail the batch summary
    }
}

string batchId = BatchJob.StartNew(jobs, storage, batch =>
{
    foreach (int invoiceId in invoiceIds)
    {
        batch.Enqueue<InvoiceService>(x => x.RenderAsync(invoiceId));
    }
}, "November invoices");

BatchJob.ContinueWith(jobs, storage, batchId, Job.FromExpression<InvoiceRunReport>(x => x.Send(batchId)));
```

With the global storage the same is shorter:

```csharp
string batchId = BatchJob.StartNew(batch =>
{
    foreach (int invoiceId in invoiceIds)
    {
        batch.Enqueue<InvoiceService>(x => x.RenderAsync(invoiceId));
    }
}, "November invoices");

BatchJob.ContinueWith<InvoiceRunReport>(batchId, x => x.Send(batchId));
```

### Continue only if every job succeeded

```csharp
BatchJob.ContinueWith<InvoiceRunReport>(batchId, x => x.Send(batchId), JobContinuationOptions.OnlyOnSucceededState);
```

With this option a deleted job in the batch deletes the continuation. A failed job doesn't: it isn't finished, so the continuation waits until the job is requeued or deleted.

### Showing progress

```csharp
using Masterly.NightShift.Storage;

app.MapGet("/invoice-runs/{batchId}", (string batchId, JobStorage storage) =>
{
    using IStorageConnection connection = storage.GetConnection();
    JobBatch? batch = BatchJob.Get(connection, batchId);
    if (batch == null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        batch.Description,
        batch.Total,
        batch.Succeeded,
        batch.Failed,
        batch.Pending,
        batch.Progress,
        batch.IsFinished,
        batch.IsStuck,
    });
});
```

### Cancel a batch, or retry its failures

```csharp
BulkJobResult retried = BatchJob.RequeueFailed(jobs, storage, batchId);
Console.WriteLine($"requeued {retried.Processed} failed jobs");

BulkJobResult canceled = BatchJob.Cancel(jobs, storage, batchId);
Console.WriteLine($"deleted {canceled.Processed} jobs");
```

Once every job is succeeded or deleted, the batch is finished: a continuation created with `OnAnyFinishedState` (the default) runs, and one created with `OnlyOnSucceededState` is deleted if any job was deleted.

### Mixing job kinds in a batch

```csharp
BatchJob.StartNew(jobs, storage, batch =>
{
    batch.Enqueue<InvoiceService>(x => x.RenderAsync(invoiceId));
    batch.Schedule<InvoiceRunReport>(x => x.Send(batch.BatchId), TimeSpan.FromHours(1));
    batch.Create(Job.FromExpression<InvoiceService>(x => x.RenderAsync(invoiceId)), new EnqueuedState("invoices"));
});
```

## Things to know

- **Failed jobs block.** `Failed` is not a finished state. A fan-in child or batch continuation waits while any parent is `Failed`, even with `OnAnyFinishedState`; requeue or delete the failed parents to let it go on. `JobBatch.IsStuck` tells you when a batch is in that situation. The batch page, or `BatchJob.RequeueFailed` from code, requeues all its failed jobs at once.
- **Batch continuations wait for the jobs that exist when you call `ContinueWith`.** Jobs added to the batch later are not waited for. Call `ContinueWith` after `StartNew` returns.
- **Batch continuations default to `OnAnyFinishedState`,** unlike `ContinueJobWith` and the fan-in methods, which default to `OnlyOnSucceededState`.
- **The batch callback is synchronous.** `StartNew` takes an `Action<IBatchBuilder>`. Create the jobs inside it. Async work started there and finished after `StartNew` returns still adds its jobs to the batch, but a `ContinueWith` called before they exist doesn't wait for them, and their entries in the batch's job list don't expire.
- **`BatchJob.ContinueWith` has no overload for `Func<T, Task>` lambdas.** Build the job with `Job.FromExpression<T>(x => x.MethodAsync(...))` and use the `Job` overload.
- **Large fan-ins cost more than they look.** The child's state stores every parent ID (about 10 characters each), and each parent that finishes reads that whole state once. Over a fan-in of N parents that is N reads of N IDs: 1,000 parents read about 10 MB in total, 10,000 parents about 1 GB, on the workers that run the parents and inside each parent's job lock. Creating the child also takes N locks and writes N parameters, and the last parent to finish reads the state of every parent. Keep a single fan-in to a few thousand parents; for more, split the work into stages: fan in per chunk of up to 1,000 jobs, then fan in the chunk continuations. Batches are fan-ins over all their jobs, so the same applies to `BatchJob.ContinueWith` on a large batch: start several smaller batches and continue each, then continue those continuations.
- **A decision is made once, right after a parent's state change is committed.** It is also made when the commit throws, because the commit may have gone through (a timeout or a lost reply) and the caller's retry then finds the parent finished and does nothing; if the commit really failed, the parent isn't finished in storage and nothing is decided. If the process stops between the commit and the decision, or the decision keeps failing (it is tried up to 3 times within about 5 seconds, and then logged as an error), the child stays `Awaiting` until another of its parents finishes again. Requeue or delete it from the dashboard. A state change applied with a transaction that you commit yourself (a `StateChangeContext` created with a transaction) decides its children before the commit instead. A parent spends at most about 5 seconds on one decision round, on the thread that changed its state; a child whose own state is still being stored by then is skipped with a warning and decides itself once that state is committed (also when that commit throws after going through).
- **A child whose creation failed stays listed on its parents.** When creating a fan-in child fails after it was added to its parents (a state filter that runs after it throws, or every commit attempt fails), its ID stays in their `FanInContinuations` parameter. A parent that finishes within 15 minutes of that attempt waits up to about 5 seconds for the child's state, then skips it with a warning; later parents skip it at once. A child that doesn't exist at all is skipped at once.
- **Parents that expire count as deleted.** With `OnlyOnSucceededState`, a long-waiting child whose succeeded parent expired is deleted. Keep the parents' expiration longer than the slowest parent; see [Managing jobs](managing-jobs.md#job-states).

## See also

- [Continuations](continuations.md)
- [Fire-and-forget jobs](fire-and-forget.md)
- [Managing jobs](managing-jobs.md)
- [Dashboard pages](../dashboard/pages.md)
