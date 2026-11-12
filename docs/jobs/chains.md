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

The [`ScheduleOptions`](schedule-policies.md#delayed-and-fire-and-forget-jobs) apply to the first step: a schedule policy, a late-start limit, a queue. Every step is created right away, the later ones in the `Awaiting` state. The methods return a `JobChainResult` with the `ChainId`, the `RootId` (the first step's job) and `Steps`, the job of each step by step key (`level.index`: `0.0` for the first step, `2.1` for the second branch of the third level). They return `null` when a client filter or a schedule policy didn't let the first step be created. The client must implement `IBackgroundJobClientV2`, as `BackgroundJobClient` does.

The chain's record is written last, once every step exists, and a step runs only when the record lists it (see [When a step runs](#when-a-step-runs)). So a chain is created whole or not at all:

- When a filter cancels a later step, or the record can't be written, the steps already created are deleted, the last first, each only from a state where it hasn't started, and the exception is thrown (`InvalidOperationException` for a canceled step). A first step that a worker took meanwhile didn't run: it was waiting for the record, and is deleted with the others.
- When creating the chain takes longer than 5 minutes and a worker took the first step meanwhile, that step was deleted before the record was written. `Enqueue` and `Schedule` then delete every step and the record, and throw `InvalidOperationException`: nothing of the chain is left, so create it again.
- When the process stops in the middle, no record is ever written, and none of the steps it created runs.

Each step carries the `ChainId` and `ChainStep` job parameters (`JobChains.ChainIdParameter`, `ChainStepParameter`), and `PerformContext.ParentId` / `ParentIds` give the steps it waited for. The two parameter names are reserved for chains: a job of your own that carries both is taken for a step, and is deleted instead of running when no chain records it.

## When a step runs

A step runs only when its chain's record lists its job for that step. Creating a chain writes the record once every step exists, and `RetryChainFrom` and `ReplaceChain` write a new one once their new jobs exist. When a worker picks up a job that carries the chain parameters, `JobChainStepFilter`, a default global filter registered before the other default filters, reads the record and decides:

| The record | What happens to the job |
|---|---|
| Lists the job for its step | It runs |
| Doesn't list it, or doesn't exist yet, and the job was created less than 5 minutes ago | The chain is being created, retried or replaced. The job goes to the `Scheduled` state with the reason `Waiting for chain {id} to be recorded` and is checked again: after 2 seconds at first, later each time, at most a minute. That isn't a retry |
| Lists another job for the step, or none, and the job is older than 5 minutes | It is deleted with the reason `Not a current step of chain {id}`: a retry or a replacement replaced it, or it never was a step of the chain |
| Doesn't exist, and the job is older than 5 minutes | It is deleted with the reason `Chain {id} isn't recorded: it was deleted or never completely created` |
| Can't be read, because of a storage error | It neither runs nor fails: it is scheduled again 15 seconds later with the reason `Waiting: the record of chain {id} couldn't be read`, and checked again |

The reasons are the constants `JobChainStepFilter.WaitingReasonFormat`, `NotCurrentReasonFormat`, `NotRecordedReasonFormat` and `UnreadableReasonFormat`. A job held this way goes back to the queue it was taken from.

On a storage that expires hashes, a record can be gone for another reason than a deletion, and the step then runs: a job older than 30 days whose chain has no record is a step of a chain whose record expired, and a job whose chain is still in the `chains` list lost its record early (a storage that evicts keys or caps expiration times), which is logged as a warning.

What this means:

- **A client that stops while it creates a chain leaves nothing that runs.** A worker that takes the first step holds it for up to 5 minutes after its creation, then deletes it, and none of the steps after it runs.
- **A first step taken while the chain is created waits for the record.** The client enqueues it as soon as the record is written, so the chain starts without waiting for the hold to end. The step's history then shows `Enqueued`, `Scheduled` (waiting for the record) and `Enqueued` again before `Processing`. While a chain is created, only its first step can be held this way: the others wait for it.
- **Jobs that a retry or a replacement replaced never run,** also when `RetryChainFrom` or `ReplaceChain` was interrupted before it deleted them (see [Run again from a step](#run-again-from-a-step)).
- **A step of a deleted chain doesn't run** when it is requeued by hand. A step that its chain's record still lists runs again when it is requeued.

A job outside a chain costs a dictionary lookup in the parameters the storage loads with the job, as SQL Server and the other storages of this project do, and one parameter read on a storage that doesn't load them. A step costs one read of its chain's record.

Without the filter (`GlobalJobFilters.Filters.Remove<JobChainStepFilter>()`, or a server whose `FilterProvider` leaves out the global filters), steps run whatever the record says. The record then only serves reading, retrying, replacing and deleting: the steps of a chain whose creation stopped halfway run, a step a worker took before a change failed is left to run, and a step's state changes no longer keep the record longer (see [Things to know](#things-to-know)).

## Managing a chain

These are extension methods on `JobStorage`:

| Method | What it does |
|---|---|
| `GetChain(chainId)` | A `JobChainInfo`: each step's key, current job, state and level, the `Generation`, `IsSettled` and `FailedCount`. `null` when the chain isn't recorded |
| `RetryChainFrom(stepJobId)` | Runs a settled chain again from a step (see below) |
| `ReplaceChain(chainId, chain)` | Replaces a chain that hasn't started |
| `DeleteChain(chainId)` | Deletes the steps that haven't finished, and the record (see below) |

A chain is **settled** when it has nothing left to do by itself: every step succeeded, was deleted (skipped steps included), failed for good or expired, or waits after a step that failed for good or whose job expired, directly or behind other steps that wait, which only a requeue or a retry moves on. A waiting step whose condition the steps it waits for already meet (a failure join after a step that failed for good, for example) is about to start or to be skipped, so the chain isn't settled until that happened. This lasts at most 5 minutes after the last of those steps finished: past that, the step was never told, because the process that was to start it stopped, and it waits for a requeue or a retry like the others.

### Run again from a step

```csharp
JobChainInfo? info = storage.GetChain(chainId);
JobChainStepInfo failed = info!.Steps.First(x => x.State == "Failed");

JobChainResult retried = storage.RetryChainFrom(failed.JobId);
```

The step's job is requeued (or created again when it expired), and every step after it gets a new job, waiting for its parents as when the chain was created. The steps before it, and the branches beside it, keep their outcome and don't run again: a join after a group waits for the retried branch and counts the others as they finished. The old jobs of the steps after it that were still waiting are deleted, the deepest first. Each retry raises the chain's `Generation`, as each replacement does.

While it works, `RetryChainFrom` holds the step in the `Scheduled` state, with the reason `Chain retried from this step` in its history, so nothing after it starts or is skipped before the retry is complete; the step is then enqueued. When a new step can't be created or the record can't be written, nothing changes: the jobs created are deleted and the step goes back to the state it was in, without being counted in the statistics a second time.

Once the new record is written, the retry has succeeded and nothing fails it any more. The jobs it replaced can't run from then on, because the record no longer lists them. When one of them can't be deleted right away, or the step can't be enqueued (a storage error, a process that stops), a warning is logged and nothing is thrown: what is left to do is kept in the record, and done by the next `RetryChainFrom`, `ReplaceChain` or `DeleteChain` of the chain, or by a server's chain maintenance, which runs every 5 minutes (see [Things to know](#things-to-know)). A step still held is enqueued at the latest when its hold ends, after an hour.

`RetryChainFrom` throws `InvalidOperationException` while the chain isn't settled, for a job that isn't a current step of a recorded chain, when the step's state changed meanwhile, and when a job the new steps wait for expired: finished jobs expire after a day by default, so retry soon or keep them longer with [`[ExpireAfter]`](../filters/timing.md#expireafterattribute).

### Replace before it starts

`ReplaceChain(chainId, newChain)` works while the first step is still enqueued or scheduled and the others wait. It holds the old first step in the `Scheduled` state (the reason `The chain is being replaced` shows in its history), so it neither runs nor starts its continuations, then creates the new chain under the same identifier, enqueued, or scheduled for the moment the old first step was, with the old first step's schedule policy, late-start limit and queue (unless the new first step has a queue of its own). When the new chain can't be created, the old first step goes back to its state and the old chain is kept as it was. Once a worker took the first step, it throws `InvalidOperationException`.

Once the new chain is recorded, the replacement has succeeded. The old steps, which the record no longer lists and so can't run, are then deleted, the deepest first, so no continuation starts; the ones that can't be deleted right away are deleted later, as after a retry. One case throws after that point: when creating the new chain took longer than 5 minutes and a worker deleted its first step meanwhile, `ReplaceChain` throws `InvalidOperationException` and the new chain stays recorded without running. Run it with `RetryChainFrom` on its first step (the message names the job), or delete it with `DeleteChain`.

### Delete

`DeleteChain(chainId)` deletes every step that hasn't finished (waiting, scheduled, enqueued, failed and processing ones), the last steps first so no continuation starts, and removes the record. It returns the number of jobs deleted, or -1 when the chain isn't recorded. It first deletes the jobs an earlier retry or replacement left to delete; when some of them still can't be deleted, it keeps the chain and throws `InvalidOperationException`, and can be called again. A chain built with `.OnDelete(ChainDeleteBehavior.Restrict)` refuses with `InvalidOperationException` while a step is processing, and never deletes a processing step, even one that starts while the chain is deleted. When a step started meanwhile and still runs, `DeleteChain` deletes what it can, keeps the chain recorded and throws `InvalidOperationException`; delete it again once the step finished.

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

- **Storage keys.** A chain is recorded in the hash `chain:{id}` (its definition, the job of each step, the generation, and in the `Cleanup` field what a retry or a replacement left to do) and listed in the set `chains`. A chain with something left to do is also listed in the set `chains:cleanup`. A recurring chain keeps its last chain's identifier in the hash `recurring-chain:{id}`. `RetryChainFrom`, `ReplaceChain` and `DeleteChain` hold the lock `lock:chain:{id}`.
- **The record lives as long as the chain has steps to run.** On storages that expire hashes, it is kept for 30 days (`JobChain.RecordRetention`) after the last state change of one of its steps, or after the time a scheduled step is due, not just 30 days after it was written. Each state change of a step extends it once the change is committed; a process writes the new expiry about once an hour per chain, so the record can expire up to an hour sooner than that. A retry or a replacement never shortens it. On a storage without expiring hashes the record stays until the chain is deleted.
- **Chain maintenance.** Every `BackgroundJobServer` runs a process that, every 5 minutes and on one server at a time (the lock `lock:chains:maintenance`), removes from the `chains` list the chains whose record expired and finishes what interrupted retries and replacements left to do: replaced jobs to delete, a held step to enqueue. Each run is bounded: it looks at up to 1,000 chains created more than 30 days ago and goes on from there the next time, and finishes up to 50 chains. The dashboard's Chains page leaves out the chains whose record expired meanwhile. The process needs a storage with the extended API (expiring hashes and ranges of sets), as every storage of this project has; on another one, retry, replace or delete a chain to finish what was left.
- **Every step is created at once,** so a chain of 1,000 steps creates 1,000 jobs. Steps that end up skipped are deleted when their turn comes.
- **A worker can take the first step before the last one is created.** It doesn't run then: it waits for the chain's record, and starts as soon as the record is written (see [When a step runs](#when-a-step-runs)).
- **Failed isn't final.** A step that failed for good keeps the steps after it waiting (except failure joins), so requeuing it from the dashboard continues the chain, like `RetryChainFrom` on that step.

## See also

- [Continuations](continuations.md)
- [Fan-in and batches](fan-in-and-batches.md)
- [Schedule policies](schedule-policies.md)
- [Recurring jobs](../recurring-jobs/README.md)
