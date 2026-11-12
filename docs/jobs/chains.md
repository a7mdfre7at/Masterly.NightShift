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

The chain's record is started first, before the first job is created: it says that the chain is being created, and lists no step. The steps are listed last, once every one of them exists, and a step runs only when the record lists it (see [When a step runs](#when-a-step-runs)). So a chain is created whole or not at all, and creating it may take as long as it needs:

- When a filter cancels a later step, or the record can't be written, the steps already created are deleted, the last first, each only from a state where it hasn't started, the record that was started is removed, and the exception is thrown (`InvalidOperationException` for a canceled step). A first step that a worker took meanwhile didn't run: it was waiting for the record, and is deleted with the others.
- While it creates the steps, the client confirms that it is still at it, about every 30 seconds, between two steps. When it doesn't for 2 minutes (one step took that long to create, or the process was paused) and a worker takes the first step after that, the worker takes the chain for abandoned and deletes that step. `Enqueue` and `Schedule` then delete every step and the record, and throw `InvalidOperationException`: nothing of the chain is left and none of it ran, so create it again.
- When the process stops in the middle, the record never lists a step, and none of the steps it created runs. On a storage that expires hashes, the record that was started expires after a day.

The steps are listed while the client holds the first step's job lock, after it read that step's state under it. A worker that takes the first step at that moment either decided before, and the client sees what it decided, or decides afterwards, with the record that lists the step.

Each step carries the `ChainId` and `ChainStep` job parameters (`JobChains.ChainIdParameter`, `ChainStepParameter`), and `PerformContext.ParentId` / `ParentIds` give the steps it waited for. The two parameter names are reserved for chains: a job of your own that carries both is taken for a step, and is deleted instead of running when no chain records it.

## When a step runs

A step runs only when its chain's record lists its job for that step, and never while the record says that the chain is being created or changed. The client that creates a chain, and `RetryChainFrom` and `ReplaceChain` before they change anything, write that into the record first: a lease that lasts 2 minutes, which the client renews while it works. The record that lists the new jobs is written last, without the lease. When a worker picks up a job that carries the chain parameters, `JobChainStepFilter`, a default global filter registered before the other default filters, reads the record and decides:

| The record | What happens to the job |
|---|---|
| Says the chain is being created, retried or replaced: it holds a lease that hasn't lapsed | The job waits, whether the record lists it or not. It goes to the `Scheduled` state with the reason `Waiting for chain {id} to be recorded` and is checked again: after 2 seconds at first, later each time, at most a minute. That isn't a retry |
| Lists the job for its step | It runs |
| Lists another job for the step, or none | It is deleted with the reason `Not a current step of chain {id}`: a retry or a replacement replaced it, or it belongs to a change that was abandoned |
| Lists no step at all | It is deleted with the reason `Chain {id} isn't recorded: it was deleted or never completely created`: the creation of the chain stopped halfway |
| Doesn't exist | It is deleted with the same reason: the chain was deleted, or never completely created. On a storage that expires hashes there are two exceptions, see below |
| Can't be read, because of a storage error | It neither runs nor fails: it is scheduled again with the reason `Waiting: the record of chain {id} couldn't be read`, and checked again. It waits 15 seconds the first time and twice as long each time in a row the same server process can't read the record for it, at most 15 minutes |

The reasons are the constants `JobChainStepFilter.WaitingReasonFormat`, `NotCurrentReasonFormat`, `NotRecordedReasonFormat` and `UnreadableReasonFormat`. A job held this way goes back to the queue it was taken from. `DrainModeFilter`, another default global filter, puts it back there when the hold ends, so keep that filter registered.

Nothing depends on the age of the job. The lease is written and compared with the storage's clock where the storage has one, as SQL Server, Redis and MongoDB do, so the clocks of the client and of the servers needn't agree. The Entity Framework Core storage has no clock of its own: there each process uses its own clock, and the clocks of the client and of the servers must agree to well within the 2 minutes of the lease.

On a storage that expires hashes, a record can be gone for another reason than a deletion, and the step then runs: a job older than 30 days whose chain has no record is a step of a chain whose record expired, and a job whose chain is still in the `chains` list lost its record early (a storage that evicts keys or caps expiration times), which is logged as a warning.

What this means:

- **A client that stops while it creates a chain leaves nothing that runs.** A worker that takes the first step holds it while the lease lasts, up to 2 minutes after the client last renewed it, then deletes it, and none of the steps after it runs.
- **A first step taken while the chain is created waits for the record.** The client enqueues it as soon as the record lists the steps, so the chain starts without waiting for the hold to end. The step's history then shows `Enqueued`, `Scheduled` (waiting for the record) and `Enqueued` again before `Processing`. While a chain is created, only its first step can be held this way: the others wait for it.
- **No step starts while a chain is retried or replaced.** A step that comes due meanwhile waits the same way, also one the record lists. When the client stops in the middle, it stops renewing the lease, and 2 minutes later at most the record counts as it stands: the steps it lists run, and the jobs the change created never do.
- **Jobs that a retry or a replacement replaced never run,** also when `RetryChainFrom` or `ReplaceChain` was interrupted before it deleted them (see [Run again from a step](#run-again-from-a-step)).
- **A step of a deleted chain doesn't run** when it is requeued by hand. A step that its chain's record still lists runs again when it is requeued.
- **Every process must know the lease.** The client that creates or changes a chain writes it, and the `JobChainStepFilter` of each server reads it. A build of this library from before the lease does neither: as a client, it starts no record before the first job, so a first step that a worker takes before the steps are listed is deleted instead of held; as a server, it runs a listed step while the chain is retried or replaced. Update the clients and the servers that share a storage together.

A job outside a chain costs a dictionary lookup in the parameters the storage loads with the job, as SQL Server and the other storages of this project do, and one parameter read on a storage that doesn't load them. A step costs one read of its chain's record (only the jobs it lists, where the storage reads one field of a hash), and a read of the storage's clock while the chain is being created or changed.

Without the filter (`GlobalJobFilters.Filters.Remove<JobChainStepFilter>()`, or a server whose `FilterProvider` leaves out the global filters), steps run whatever the record says. The record then only serves reading, retrying, replacing and deleting: the steps of a chain whose creation stopped halfway run, steps start while a chain is retried or replaced, a step a worker took before a change failed is left to run, and a step's state changes no longer keep the record longer (see [Things to know](#things-to-know)).

## Managing a chain

These are extension methods on `JobStorage`:

| Method | What it does |
|---|---|
| `GetChain(chainId)` | A `JobChainInfo`: each step's key, current job, state and level, the `Generation`, `IsSettled` and `FailedCount`. `null` when the chain isn't recorded |
| `RetryChainFrom(stepJobId)` | Runs a settled chain again from a step (see below) |
| `ReplaceChain(chainId, chain)` | Replaces a chain that hasn't started |
| `DeleteChain(chainId)` | Deletes the steps that haven't finished, and the record (see below) |

A chain is **settled** when it has nothing left to do by itself: every step succeeded, was deleted (skipped steps included), failed for good or expired, or waits after a step that failed for good or whose job expired, directly or behind other steps that wait, which only a requeue or a retry moves on. A waiting step whose start the steps it waits for already decide (a step after one that succeeded, a failure join after a step that failed for good) is about to start or to be skipped, within seconds, so the chain isn't settled until that happened. This lasts at most 5 minutes after the last of those steps finished, for every such step: past that, the step was never told, because the process that was to start it stopped or couldn't, and it no longer keeps the chain from being settled. Such a step is started or skipped later in the cases listed under [Things to know](#things-to-know), and the chain then runs again.

### Run again from a step

```csharp
JobChainInfo? info = storage.GetChain(chainId);
JobChainStepInfo failed = info!.Steps.First(x => x.State == "Failed");

JobChainResult retried = storage.RetryChainFrom(failed.JobId);
```

The step's job is requeued (or created again when it expired), and every step after it gets a new job, waiting for its parents as when the chain was created. The steps before it, and the branches beside it, keep their outcome and don't run again: a join after a group waits for the retried branch and counts the others as they finished. The old jobs of the steps after it that were still waiting are deleted, the deepest first. Each retry raises the chain's `Generation`, as each replacement does.

While it works, `RetryChainFrom` says in the chain's record that the chain is being changed, so no step of the chain starts (see [When a step runs](#when-a-step-runs)), and holds the step in the `Scheduled` state for 5 minutes, with the reason `Chain retried from this step` in its history, so nothing after it starts or is skipped before the retry is complete; the step is then enqueued. When a new step can't be created or the record can't be written, nothing changes: the jobs created are deleted and the step goes back to the state it was in, without being counted in the statistics a second time. When the process stops before the retry is recorded, nothing is lost either: the record still lists the steps it listed before, so the step runs again when its hold ends, the steps that still waited after it follow it as before, and the jobs the retry had created never run.

Once the new record is written, the retry has succeeded and nothing fails it any more. The jobs it replaced can't run from then on, because the record no longer lists them. When one of them can't be deleted right away, or the step can't be enqueued (a storage error, a process that stops), a warning is logged and nothing is thrown: what is left to do is kept in the record, and done by the next `RetryChainFrom`, `ReplaceChain` or `DeleteChain` of the chain, or by a server's chain maintenance, which runs every 5 minutes (see [Things to know](#things-to-know)). So when the process stops after the retry is recorded, a server enqueues the step and deletes the jobs the retry replaced. A step still held is enqueued at the latest when its hold ends, after 5 minutes.

`RetryChainFrom` throws `InvalidOperationException` while the chain isn't settled, for a job that isn't a current step of a recorded chain, when the step's state changed meanwhile, when preparing the retry took longer than the 5 minutes the step is held and the step left its hold by itself, and when a job the new steps wait for expired: finished jobs expire after a day by default, so retry soon or keep them longer with [`[ExpireAfter]`](../filters/timing.md#expireafterattribute). A waiting step that was never told to start (see [Things to know](#things-to-know)) is started first; the chain then runs again by itself, so the retry is refused as for any chain that isn't settled.

### Replace before it starts

`ReplaceChain(chainId, newChain)` works while the first step is still enqueued or scheduled and the others wait. It says in the chain's record that the chain is being changed, so no step of the chain, old or new, starts meanwhile (see [When a step runs](#when-a-step-runs)), and leaves the old first step alone: it keeps its state and its time. Then it creates the new chain under the same identifier, enqueued, or scheduled for the moment the old first step was, with the old first step's schedule policy, late-start limit and queue (unless the new first step has a queue of its own). The new record is written while the job locks of the old and of the new first step are held, and is refused when the old first step started meanwhile. Once the first step started, `ReplaceChain` throws `InvalidOperationException`.

Until the new chain is recorded, the old one is kept as it was. When the new chain can't be created, its jobs are deleted and the exception is thrown. When the process stops in the middle, the old chain runs when it was to run (a first step that came due meanwhile, once the lease lapsed, 2 minutes at most after the client last renewed it), and nothing of the new chain runs.

Once the new chain is recorded, the replacement has succeeded. The old steps, which the record no longer lists and so can't run, are then deleted, the deepest first, so no continuation starts; the ones that can't be deleted right away are deleted later, as after a retry. Nothing throws after that point. When the client didn't confirm for 2 minutes that it was still at it and a worker deleted the new first step meanwhile (see [Creating it](#creating-it)), `ReplaceChain` throws `InvalidOperationException` before it records anything: the new jobs are deleted, and the old chain is kept.

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

The recurring job runs `JobChainRunner.Run`, which creates a new chain at each occurrence and returns its identifier as the job's result. With `skipWhileRunning`, an occurrence whose previous chain isn't settled is skipped (deleted with the reason `Skipped: the previous chain ... is still running`). Before it decides, it starts the waiting steps of the previous chain that were never told to start (see [Things to know](#things-to-know)): that chain then runs again, and the occurrence is skipped rather than run beside it. `IRecurringJobManager.AddOrUpdateChain` does the same through a manager. The chain's definition is the recurring job's argument, so registering it again with another chain updates it, and the recurring job has every other option ([schedule policies](schedule-policies.md#recurring-jobs), `SkipAfter`, misfire handling).

## Things to know

- **Storage keys.** A chain is recorded in the hash `chain:{id}` (its definition, the job of each step in the `Jobs` field, the generation, and in the `Cleanup` field what a retry or a replacement left to do) and listed in the set `chains`. While a chain is created, retried or replaced, its `Jobs` field also holds the entry `changing`, the lease: when the change began, and until when it counts unless the client renews it. The record of a chain that is being created holds nothing else and isn't listed; on a storage that expires hashes it expires after a day, should the creation stop. A chain with something left to do is also listed in the set `chains:cleanup`, and a chain with a step that waits for several steps in the set `chains:joins`, until no such step waits any more. A recurring chain keeps its last chain's identifier in the hash `recurring-chain:{id}`. `RetryChainFrom`, `ReplaceChain` and `DeleteChain` hold the lock `lock:chain:{id}`.
- **The record lives as long as the chain has steps to run.** On storages that expire hashes, it is kept for 30 days (`JobChain.RecordRetention`) after the last state change of one of its steps, or after the time a scheduled step is due, not just 30 days after it was written. Each state change of a step extends it once the change is committed; a process writes the new expiry about once an hour per chain, so the record can expire up to an hour sooner than that. A retry or a replacement never shortens it. On a storage without expiring hashes the record stays until the chain is deleted.
- **Chain maintenance.** Every `BackgroundJobServer` runs a process that, every 5 minutes and on one server at a time (the lock `lock:chains:maintenance`), removes from the `chains` list the chains whose record expired, finishes what interrupted retries and replacements left to do (replaced jobs to delete, a held step to enqueue), and starts or skips the waiting steps that were never told to start (see the next point). Each run is bounded: it looks at up to 1,000 chains created more than 30 days ago and goes on from there the next time, finishes up to 50 chains, and looks at up to 200 chains with a step that waits for several steps. A run that stopped at one of these limits is followed by the next one 10 seconds later. The dashboard's Chains page leaves out the chains whose record expired meanwhile. Only removing expired chains needs a storage with the extended API (expiring hashes and ranges of sets), as every storage of this project has: nothing expires without it. The rest runs on every storage.
- **A step that was never told to start is started later.** A waiting step is started, or skipped, by the process that finishes the last step it waits for: a step that waits for a single step while that step changes state, a step that waits for several steps right after the state of the last of them is committed. When the process stops at that moment, or the storage fails then, the step keeps waiting. Once the steps it waits for finished more than 5 minutes ago, it is decided again from their stored states and changed only from the `Awaiting` state, with a warning in the log. Three things do that: the chain maintenance, for the chains with a step that waits for several steps (it looks at such a chain 5 minutes after it is recorded, then again after a time that grows with the chain's age, from 10 minutes to 4 hours, for as long as such a step waits); a recurring chain with `skipWhileRunning`, for its previous chain; and `RetryChainFrom`, for the chain it is asked to retry. One case is left alone: with [`UseResultsInContinuations()`](continuations.md#passing-the-parents-result-or-exception), a step that waits for a single step isn't started this way, because it would run without its parent's result. A warning says so, and the step waits until you requeue it.
- **Every step is created at once,** so a chain of 1,000 steps creates 1,000 jobs. Steps that end up skipped are deleted when their turn comes.
- **A worker can take the first step before the last one is created.** It doesn't run then: it waits for the chain's record, and starts as soon as the record lists the steps (see [When a step runs](#when-a-step-runs)).
- **Failed isn't final.** A step that failed for good keeps the steps after it waiting (except failure joins), so requeuing it from the dashboard continues the chain, like `RetryChainFrom` on that step.

## See also

- [Continuations](continuations.md)
- [Fan-in and batches](fan-in-and-batches.md)
- [Schedule policies](schedule-policies.md)
- [Recurring jobs](../recurring-jobs/README.md)
