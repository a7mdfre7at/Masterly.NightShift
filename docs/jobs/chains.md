# Chains

A chain is a workflow of jobs created together: a first step, then steps that run one after the other, in parallel branches, or after a group of branches. Each step is an ordinary job: it has its own retries, filters, queue and console. The chain is recorded, so you can read its progress, run it again from a step that failed, replace it before it starts, or delete it. A recurring chain creates a new chain at each occurrence of a schedule.

Use a chain when several jobs make up one piece of work: an order that is processed, invoiced, mailed and posted to the ERP; an ETL run; an import followed by a report. For a single follow-up job, [`ContinueJobWith`](continuations.md) is enough; for "wait for these jobs" without a fixed shape, see [Fan-in and batches](fan-in-and-batches.md).

## Quick example

```csharp
using Masterly.NightShift;

JobChainResult? chain = JobChain
    .StartWith<OrderService>(x => x.Process(orderId))
    .Then<InvoiceService>(x => x.Render(orderId))
    .ThenMany(branches => branches
        .Branch<Mailer>(x => x.SendInvoice(orderId))
        .Branch<ErpClient>(x => x.Post(orderId)))
    .ThenAfterAll<Notifier>(x => x.OrderDone(orderId))
    .ThenAfterAnyFailed<Ops>(x => x.Alert(orderId))
    .Enqueue();
```

`Process` runs first, then `Render`; the mail and the ERP post run in parallel; `OrderDone` runs when both succeeded, and `Alert` when one of them failed for good. The step that the outcome doesn't satisfy is deleted with the reason "Continuation condition was not met".

## Building a chain

A chain is a list of levels. Every step of a level waits for every step of the level before it.

| Method | Adds |
|---|---|
| `JobChain.StartWith(methodCall, queue)` | The first step |
| `Then(methodCall, options, queue)` | A level with one step. After a single step it is a continuation with these [`JobContinuationOptions`](continuations.md#jobcontinuationoptions) (`OnlyOnSucceededState` by default); after a group, it is a join chosen from them (see below) |
| `ThenMany(branches => branches.Branch(methodCall, options, queue)...)` | A level of parallel branches, each continuing the level before. At least one branch |
| `ThenAfterAll(methodCall, queue)` | A join: starts once every step of the level before succeeded |
| `ThenAfterAny(methodCall, queue)` | A join: starts at the first success; skipped when none succeeds |
| `ThenAfterAnyFailed(methodCall, queue)` | A join: starts at the first step that failed for good (no retry left); skipped when none fails |
| `ThenAfterAnyFailedOrCanceled(methodCall, queue)` | A join: starts at the first failure or deletion; skipped when every step succeeds |
| `ThenAfterAllCompleted(methodCall, queue)` | A join: starts once every step finished, whatever the outcome |
| `OnDelete(ChainDeleteBehavior)` | What `DeleteChain` does while a step is processing (see below) |

Each method has overloads for `Expression<Action>`, `Expression<Func<Task>>`, `Expression<Action<T>>` and `Expression<Func<T, Task>>`, as `Enqueue` does.

**Options after a group.** A `Then`, or a branch of `ThenMany`, that follows a level of several steps (a group or consecutive joins) waits for all of them, as a [fan-in](fan-in-and-batches.md) does. Its options choose the join closest to what they mean for a single step:

| Options | Starts |
|---|---|
| `OnlyOnSucceededState` (the default) | Once every step succeeded, like `ThenAfterAll` |
| `OnlyOnFailedState` | Once one step failed for good, like `ThenAfterAnyFailed` |
| `OnlyOnFailedState \| OnlyOnDeletedState` | Once one step failed for good or was deleted, like `ThenAfterAnyFailedOrCanceled` |
| `OnlyOnFailedState \| OnlyOnSucceededState`, or `OnAnyFinishedState` | Once every step finished, whatever the outcome, like `ThenAfterAllCompleted`: it also runs when a branch failed |
| Other options, such as `OnlyOnDeletedState` | Once every step finished in a state the options accept |

`OnlyOnProcessingState` needs a single step before it: after a group, `Then` and `ThenMany` throw `ArgumentException`.

**Consecutive joins wait for the same level.** `ThenAfterAll(...)` followed by `ThenAfterAnyFailed(...)` gives the group a success path and a failure path. A `Then` after joins waits for all of them; as one of them is usually skipped (deleted), give it `JobContinuationOptions.OnAnyFinishedState`, or end the chain with the joins.

A chain has at most 1,000 steps (`JobChain.MaxSteps`) and 50 levels (`JobChain.MaxLevels`). Retries and timeouts of a step come from its method's attributes ([`[AutomaticRetry]`](../filters/retries.md), [`[JobTimeout]`](../filters/timing.md)), as for any job.

## Creating it

| Method | First step |
|---|---|
| `Enqueue(ScheduleOptions? options = null)` | Enqueued now, through `JobStorage.Current` |
| `Enqueue(IBackgroundJobClient client, ScheduleOptions? options = null)` | Enqueued now, through the client |
| `Schedule(TimeSpan delay, ...)`, `Schedule(DateTimeOffset enqueueAt, ...)` | Scheduled, with or without a client |

The [`ScheduleOptions`](schedule-policies.md#delayed-and-fire-and-forget-jobs) apply to the first step: a schedule policy, a late-start limit, a queue. Every step is created right away, the later ones in the `Awaiting` state. The methods return a `JobChainResult` with the `ChainId`, the `RootId` (the first step's job) and `Steps`, the job of each step by step key (`level.index`: `0.0` for the first step, `2.1` for the second branch of the third level). They return `null` when a client filter or a schedule policy didn't let the first step be created. When a filter cancels a later step, or the chain's record can't be written, the steps already created are deleted, the last first, each only from a state where it hasn't started (a step a worker took meanwhile is left to run), and the exception is thrown (`InvalidOperationException` for a canceled step). The client must implement `IBackgroundJobClientV2`, as `BackgroundJobClient` does.

Each step carries the `ChainId` and `ChainStep` job parameters (`JobChains.ChainIdParameter`, `ChainStepParameter`), and `PerformContext.ParentId` / `ParentIds` give the steps it waited for.

## Managing a chain

These are extension methods on `JobStorage`:

| Method | What it does |
|---|---|
| `GetChain(chainId)` | A `JobChainInfo`: each step's key, current job, state and level, the `Generation`, `IsSettled` and `FailedCount`. `null` when the chain isn't recorded |
| `RetryChainFrom(stepJobId)` | Runs a settled chain again from a step (see below) |
| `ReplaceChain(chainId, chain)` | Replaces a chain that hasn't started |
| `DeleteChain(chainId)` | Deletes the steps that haven't finished, and the record (see below) |

A chain is **settled** when it has nothing left to do by itself: every step succeeded, was deleted (skipped steps included), failed for good or expired, or waits after a step that failed for good or whose job expired, directly or behind other steps that wait, which only a requeue or a retry moves on.

### Run again from a step

```csharp
JobChainInfo? info = storage.GetChain(chainId);
JobChainStepInfo failed = info!.Steps.First(x => x.State == "Failed");

JobChainResult retried = storage.RetryChainFrom(failed.JobId);
```

The step's job is requeued (or created again when it expired), and every step after it gets a new job, waiting for its parents as when the chain was created. The steps before it, and the branches beside it, keep their outcome and don't run again: a join after a group waits for the retried branch and counts the others as they finished. The old jobs of the steps after it that were still waiting are deleted, the deepest first. Each retry raises the chain's `Generation`.

While it works, `RetryChainFrom` holds the step in the `Scheduled` state, with the reason `Chain retried from this step` in its history, so nothing after it starts or is skipped before the retry is complete; the step is then enqueued. When a new step can't be created or the record can't be written, nothing changes: the jobs created are deleted and the step goes back to the state it was in.

`RetryChainFrom` throws `InvalidOperationException` while the chain isn't settled, for a job that isn't a current step of a recorded chain, when the step's state changed meanwhile, and when a job the new steps wait for expired: finished jobs expire after a day by default, so retry soon or keep them longer with [`[ExpireAfter]`](../filters/timing.md#expireafterattribute).

### Replace before it starts

`ReplaceChain(chainId, newChain)` works while the first step is still enqueued or scheduled and the others wait. It holds the old first step in the `Scheduled` state (the reason `The chain is being replaced` shows in its history), so it neither runs nor starts its continuations, then creates the new chain under the same identifier, enqueued, or scheduled for the moment the old first step was, with the old first step's schedule policy, late-start limit and queue (unless the new first step has a queue of its own). Last, it deletes the old steps, the deepest first, so no continuation starts. When the new chain can't be created, the old first step goes back to its state and the old chain is kept as it was. Once a worker took the first step, it throws `InvalidOperationException`.

### Delete

`DeleteChain(chainId)` deletes every step that hasn't finished (waiting, scheduled, enqueued, failed and processing ones), the last steps first so no continuation starts, and removes the record. It returns the number of jobs deleted, or -1 when the chain isn't recorded. A chain built with `.OnDelete(ChainDeleteBehavior.Restrict)` refuses with `InvalidOperationException` while a step is processing, and never deletes a processing step, even one that starts while the chain is deleted. When a step started meanwhile and still runs, `DeleteChain` deletes what it can, keeps the chain recorded and throws `InvalidOperationException`; delete it again once the step finished.

## Recurring chains

```csharp
RecurringJob.AddOrUpdateChain(
    "nightly-etl",
    JobChain.StartWith<Etl>(x => x.Extract()).Then<Etl>(x => x.Transform()).Then<Etl>(x => x.Load()),
    Cron.Daily(2),
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin") },
    skipWhileRunning: true);
```

The recurring job runs `JobChainRunner.Run`, which creates a new chain at each occurrence and returns its identifier as the job's result. With `skipWhileRunning`, an occurrence whose previous chain isn't settled is skipped (deleted with the reason `Skipped: the previous chain ... is still running`). `IRecurringJobManager.AddOrUpdateChain` does the same through a manager. The chain's definition is the recurring job's argument, so registering it again with another chain updates it, and the recurring job has every other option ([schedule policies](schedule-policies.md#recurring-jobs), `SkipAfter`, misfire handling).

## Things to know

- **Storage keys.** A chain is recorded in the hash `chain:{id}` (its definition, the job of each step, the generation), kept for 30 days (`JobChain.RecordRetention`) on storages that expire hashes, and listed in the set `chains`. Chains whose record expired leave that list gradually, at most 20 each time a chain is recorded; the dashboard's Chains page leaves them out meanwhile. A recurring chain keeps its last chain's identifier in the hash `recurring-chain:{id}`. The jobs run without the record: it only serves reading, retrying, replacing and deleting.
- **Every step is created at once,** so a chain of 1,000 steps creates 1,000 jobs. Steps that end up skipped are deleted when their turn comes.
- **The first step can run before the last one is created.** That is fine: a step created after the steps before it finished starts at once, or is skipped.
- **Failed isn't final.** A step that failed for good keeps the steps after it waiting (except failure joins), so requeuing it from the dashboard continues the chain, like `RetryChainFrom` on that step.

## See also

- [Continuations](continuations.md)
- [Fan-in and batches](fan-in-and-batches.md)
- [Schedule policies](schedule-policies.md)
- [Recurring jobs](../recurring-jobs/README.md)
