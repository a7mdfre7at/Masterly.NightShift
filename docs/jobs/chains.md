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

- When a filter cancels a later step, or the record can't be written, the steps already created are deleted, the last first, each only from a state where it hasn't started, the record that was started is removed, and the exception is thrown (`InvalidOperationException` for a canceled step). A first step that a worker took meanwhile didn't run: it was given back to its queue or held until the record lists it, and is deleted with the others. When one of the jobs can't be deleted, the record that was started is kept instead, without any step, so that job is deleted when a worker takes it.
- While it creates the steps, the client confirms that it is still at it, about every 30 seconds, between two steps. When it doesn't for 2 minutes (one step took that long to create, or the process was paused), a worker that takes a job of the chain after that, usually the first step, takes the chain for abandoned and deletes that job. The client looks for such a job when it confirms after the 2 minutes passed, again before it lists the steps, and once more, at the first step, after it listed them. When it finds one, `Enqueue` and `Schedule` delete every step and the record, also a record they just wrote, and throw `InvalidOperationException` (`Chain '...' wasn't created: its background job '...' was deleted before the chain was recorded`): none of the chain ran and nothing of it is left to run, so create it again.
- When the process stops in the middle, the record never lists a step, and none of the steps it created runs: a worker that takes one deletes it. On a storage that expires hashes, the record that was started is kept for the [chain retention](#things-to-know) (30 days by default), counted from the time the first step is due when it is scheduled. A first step scheduled months ahead is deleted when it comes due, like any other.

The steps are listed while the client holds the first step's job lock, after it read that step's state under it. A worker that takes the first step at that moment either decided before, and the client sees what it decided, or decides afterwards, with the record that lists the step.

Each step carries the `ChainId` and `ChainStep` job parameters (`JobChains.ChainIdParameter`, `ChainStepParameter`), and `PerformContext.ParentId` / `ParentIds` give the steps it waited for. The two parameter names are reserved for chains: a job of your own that carries both is taken for a step, and is deleted instead of running when no chain records it.

A job that a record lists also carries the `ChainRecorded` parameter (`JobChains.ChainRecordedParameter`), from the moment the first record that lists it is written: the record of a new chain, of a retry or of a replacement. Its value is the chain retention of the process that wrote that record, in seconds (`2592000` for the default 30 days); `true`, written by earlier builds, stands for the retention of the process that reads it. It tells a step of a chain whose record expired from a job no record ever listed (see [When a step runs](#when-a-step-runs)). On a storage that can set a job parameter in a transaction, as every storage of this project can, it is written in the record's transaction: a step is never listed without the parameter, and a record that fails to commit leaves none. A step whose job is gone by then (it expired, or was purged) gets none, and the record is written all the same. On a storage that can't, it is written right after the record is committed, one write per step; when that fails, or the process stops there, a warning is logged at most and the chain is recorded all the same. A step left without the parameter runs like any other while the record lists it; it is deleted, not run, only should it still be waiting when the record has expired.

## When a step runs

A step runs only when its chain's record lists its job for that step, and never while the record says that the chain is being created or changed. The client that creates a chain, and `RetryChainFrom` and `ReplaceChain` before they change anything, write that into the record first: a lease that lasts 2 minutes, which the client renews while it works. The record that lists the new jobs is written last, without the lease. When a worker picks up a job that carries the chain parameters, `JobChainStepFilter`, a default global filter registered before the other default filters, reads the record and decides:

| The record | What happens to the job |
|---|---|
| Says the chain is being created, lists no step yet, and the creation began less than 5 seconds ago | The worker gives the job back to its queue and takes it again a moment later. Nothing is stored: no state, no history entry, and nothing counts as a failure, a retry or a stale restart. Only for a job in the `Enqueued` state that a worker fetched (see [The first step of a chain that is being created](#the-first-step-of-a-chain-that-is-being-created)) |
| Says the chain is being created for longer than that, or is being retried or replaced: it holds a lease that hasn't lapsed | The job waits, whether the record lists it or not. It goes to the `Scheduled` state with the reason `Waiting for chain {id} to be recorded` and is checked again: after 2 seconds at first, later each time, at most a minute. That isn't a retry |
| Holds no lease any more and still has the generation it had when a retry held the job (the job carries the `ChainRetry` parameter): the retry was never recorded | The job doesn't run. It is put back into the state it was in before the retry, with the reason `The retry of chain {id} was abandoned` (see [Run again from a step](#run-again-from-a-step)) |
| Lists the job for its step | It runs |
| Lists another job for the step, or none | It is deleted with the reason `Not a current step of chain {id}`: a retry or a replacement replaced it, or it belongs to a change that was abandoned |
| Lists no step at all | It is deleted with the reason `Chain {id} isn't recorded: it was deleted or never completely created`: the creation of the chain stopped halfway, or failed and couldn't delete this job |
| Doesn't exist | It is deleted with the same reason: the chain was deleted, or never completely created. On a storage that expires hashes there are two exceptions, see below |
| Can't be read, because of a storage error | It neither runs nor fails: it is scheduled again with the reason `Waiting: the record of chain {id} couldn't be read`, and checked again. It waits 15 seconds the first time and twice as long each time in a row the same server process can't read the record for it, at most 15 minutes |

The reasons are the constants `JobChainStepFilter.WaitingReasonFormat`, `NotCurrentReasonFormat`, `NotRecordedReasonFormat` and `UnreadableReasonFormat`. A job held in the `Scheduled` state this way goes back to the queue it was taken from. `DrainModeFilter`, another default global filter, puts it back there when the hold ends, so keep that filter registered.

The filter never throws, so it never fails a state change. When the record can't be read, the server process logs a warning the first time for a job, and the next times in a row at the debug level. When the queue of a job it holds can't be kept, the job is held all the same and a warning is logged: unless the job has a queue of its own, it goes to the default queue when the hold ends.

Whether a step waits doesn't depend on the age of its job. The lease is written and compared with the storage's clock where the storage has one, as SQL Server, Redis, MongoDB and the Entity Framework Core storage on a server database do, so the clocks of the client and of the servers needn't agree. Where the storage has none, each process uses its own clock, and the clocks of the client and of the servers must agree to well within the 2 minutes of the lease: a storage of your own without `Connection.GetUtcDateTime`, and the Entity Framework Core storage on a provider it knows no clock statement for, or whose statement failed its check (see [Entity Framework Core](../storage/entity-framework-core.md#the-databases-clock)). On SQLite each process uses its own clock too, but the processes that share the database file share a machine, and so its clock. A lease that ends more than an hour later than a lease written now would (it is damaged, or was written with a clock that is far off) counts as lapsed, with a warning in the log, so it never holds the steps of a chain for good.

On a storage that expires hashes, a record can be gone for another reason than a deletion, and the step then runs, in two cases:

- **The record expired.** The job carries the `ChainRecorded` parameter and was created longer ago than the retention the parameter says: a record listed it once, and a record outlives the steps it lists by that retention at least. The retention is the one of the process that wrote the record, so a server set to a shorter one doesn't take the record for expired sooner. A job without the parameter doesn't run for being old: no record ever listed it, because its chain was never completely created, or the retry or the replacement that created it was never recorded.
- **The record was lost early.** The chain is still in the `chains` list, where it stays until it is deleted or a server takes it out: the storage evicted the record or caps expiration times, or no server maintained the chains for longer than a quarter of the retention (see [Things to know](#things-to-know)). This is logged as a warning.

What this means:

- **A client that stops while it creates a chain leaves nothing that runs.** A worker that takes the first step gives it back for the first 5 seconds, holds it while the lease lasts, up to 2 minutes after the client last renewed it, then deletes it, and none of the steps after it runs. A first step scheduled far ahead is deleted when it comes due, also after the record that was started has expired, because it doesn't carry the `ChainRecorded` parameter.
- **A first step taken while the chain is created leaves no trace, as a rule.** Creating a chain takes milliseconds, and an idle worker fetches the first step in the middle of it. The worker gives the step back to its queue and takes it again once the record lists it, so the step's history reads `Enqueued`, `Processing` and so on (see [The first step of a chain that is being created](#the-first-step-of-a-chain-that-is-being-created)). While a chain is created, only its first step can be taken this way: the others wait for it.
- **No step starts while a chain is retried or replaced.** A step that comes due meanwhile waits in the `Scheduled` state, also one the record lists, and its history shows it. When the client stops in the middle, it stops renewing the lease, and 2 minutes later at most the record counts as it stands: the steps it lists run, except the step a retry held, which is put back into the state it was in, and the jobs the change created never run.
- **Jobs that a retry or a replacement replaced never run,** also when `RetryChainFrom` or `ReplaceChain` was interrupted before it deleted them (see [Run again from a step](#run-again-from-a-step)).
- **A step of a deleted chain doesn't run** when it is requeued by hand. A step that its chain's record still lists runs again when it is requeued.
- **Every process must know the lease.** The client that creates or changes a chain writes it, and the `JobChainStepFilter` of each server reads it. A build of this library from before the lease does neither: as a client, it starts no record before the first job, so a first step that a worker takes before the steps are listed is deleted instead of held; as a server, it runs a listed step while the chain is retried or replaced. Such a client doesn't write the `ChainRecorded` parameter either, so a step of its chains that is still waiting when the record has expired is deleted, not run. The same goes for the step of an abandoned retry: a server from before the `ChainRetry` parameter runs it when its hold ends instead of putting it back. Update the clients and the servers that share a storage together.

A job outside a chain costs a dictionary lookup in the parameters the storage loads with the job, as SQL Server and the other storages of this project do, and one parameter read on a storage that doesn't load them. A step costs one read of its chain's record (only the jobs it lists, where the storage reads one field of a hash), and a read of the storage's clock while the chain is being created or changed.

Without the filter (`GlobalJobFilters.Filters.Remove<JobChainStepFilter>()`, or a server whose `FilterProvider` leaves out the global filters), steps run whatever the record says. The record then only serves reading, retrying, replacing and deleting: the steps of a chain whose creation stopped halfway run, steps start while a chain is retried or replaced, a step a worker took before a change failed is left to run, and a step's state changes no longer keep the record longer (see [Things to know](#things-to-know)).

### The first step of a chain that is being created

The first step is enqueued before the later steps exist and before the record lists any of them, and an idle worker fetches it within milliseconds. For the first 5 seconds of a creation, the worker doesn't hold such a step in the `Scheduled` state. It gives the job back: the move to `Processing` is dropped before anything is stored, the worker keeps the job to itself for as long as the creation has lasted so far, between 10 and 250 milliseconds, and then puts it back in its queue, where it is fetched again. By then the record usually lists the step, and it runs.

- **Nothing is stored about it.** No state, no history entry, no job parameter, and no filter after `JobChainStepFilter` sees the job. It counts as no failure, retry or stale restart. The step's history reads `Enqueued`, `Processing` and so on.
- **The waiting entries appear only for a slow creation.** When the creation has lasted more than 5 seconds, or its client stopped, the step is held instead: `Scheduled` with the reason `Waiting for chain {id} to be recorded`, then `Enqueued` with the reason `The chain is recorded`, because the client enqueues the step as soon as the record lists the steps, without waiting for the hold to end. A step whose client hasn't renewed the lease for 2 minutes is deleted.
- **Creation only.** A step that comes due while its chain is retried or replaced is held in the `Scheduled` state, with the waiting entry in its history.
- **The step keeps its place in its queue.** A server with a single worker on that queue takes it again each time, so the other jobs of the queue can wait up to about 5 seconds while a slow creation lasts.
- **Only the first election filter can have a job given back,** and by default that is `JobChainStepFilter`. An election filter of your own that runs before it (an `Order` below -1) ends the offer: the step is then held in the `Scheduled` state from the start, with the two history entries. A job that isn't in the `Enqueued` state when the worker takes it is held too.
- **Clocks.** On a storage without a clock of its own, a server whose clock is behind the client's sees a creation that began in the future, and holds the step in the `Scheduled` state.

## Managing a chain

These are extension methods on `JobStorage`:

| Method | What it does |
|---|---|
| `GetChain(chainId)` | A `JobChainInfo`: each step's key, current job, state, level and method call (`Job`), the `Generation`, `IsSettled` and `FailedCount`. `null` when the chain isn't recorded. A step whose method can't be loaded any more has a `null` `Job` (see [Things to know](#things-to-know)) |
| `RetryChainFrom(stepJobId)` | Runs a settled chain again from a step (see below) |
| `ReplaceChain(chainId, chain)` | Replaces a chain that hasn't started |
| `DeleteChain(chainId)` | Deletes the steps that haven't finished, and the record (see below) |

A chain can be read and managed for as long as it is recorded: while a step is left to run or failed for good, and for the chain retention after that, 30 days unless `UseChainRetention` sets another time (see [Things to know](#things-to-know)).

A chain is **settled** when it has nothing left to do by itself: every step succeeded, was deleted (skipped steps included), failed for good or expired, or waits after a step that failed for good or whose job expired, directly or behind other steps that wait, which only a requeue or a retry moves on. A waiting step whose start the steps it waits for already decide (a step after one that succeeded, a failure join after a step that failed for good) is about to start or to be skipped, within seconds, so the chain isn't settled until that happened. This lasts at most 5 minutes after the last of those steps finished, for every such step: past that, the step was never told, because the process that was to start it stopped or couldn't, and it no longer keeps the chain from being settled. Such a step is started or skipped later in the cases listed under [Things to know](#things-to-know), and the chain then runs again.

### Run again from a step

```csharp
JobChainInfo? info = storage.GetChain(chainId);
JobChainStepInfo failed = info!.Steps.First(x => x.State == "Failed");

JobChainResult retried = storage.RetryChainFrom(failed.JobId);
```

The step's job is requeued (or created again when it expired), and every step after it gets a new job, waiting for its parents as when the chain was created. The steps before it, and the branches beside it, keep their outcome and don't run again: a join after a group waits for the retried branch and counts the others as they finished. The old jobs of the steps after it that were still waiting are deleted, the deepest first. Each retry raises the chain's `Generation`, as each replacement does.

While it works, `RetryChainFrom` says in the chain's record that the chain is being changed, so no step of the chain starts (see [When a step runs](#when-a-step-runs)), and holds the step in the `Scheduled` state for 2 minutes 15 seconds, with the reason `Chain retried from this step` in its history, so nothing after it starts or is skipped before the retry is complete; the step is then enqueued. The hold doesn't limit the retry: a step whose hold ends while the client still renews its lease waits, with the reason `Waiting for chain {id} to be recorded`. When a new step can't be created or the record can't be written, nothing changes: the jobs created are deleted and the step goes back to the state it was in, with the time it was to expire, without being counted in the statistics a second time.

When the process stops, or stalls past its 2-minute lease, before the retry is recorded, the retry never happened, and the step doesn't run. When its hold ends, the worker that takes it puts it back into the state it was in:

- The state has the same data, and the job expires when it was to. Its reason is `The retry of chain {id} was abandoned`.
- The step's history shows `Scheduled`, `Enqueued` and the old state again, with no `Processing` entry.
- Going back isn't a new outcome: no automatic retry, alert, [job monitor](../monitoring/job-monitors.md) outcome, statistics count or activity feed entry, and a continuation that started the first time doesn't start again.
- The jobs the retry had created are deleted with the same reason.

The chain is as it was, and can be retried again.

A retry confirms that it is still at it as a creation does (see [Creating it](#creating-it)). When it didn't for 2 minutes, a server may have taken it for abandoned meanwhile: it put the step back, or deleted one of the new steps, which the record doesn't list yet. `RetryChainFrom` finds that when it confirms, or before it writes the record. It then takes the retry back as above and throws `InvalidOperationException`: `Chain '...' wasn't retried: background job '...' is no longer held for the retry. [...] The chain is as it was; retry it again.` when the step was put back or changed, and `Chain '...' wasn't retried: its background job '...' was deleted before the chain was recorded` when a new step was deleted. Nothing of the retry is left to run.

One case is found only after the record is written: the process was frozen while it recorded the retry (that took more than 5 seconds), for so long that a server took the retry for abandoned. `RetryChainFrom` then puts the record back as it was, deletes the new steps, puts the step back into the state it was in unless a server did that already, and throws `InvalidOperationException` (`Chain '...' wasn't retried: this process was frozen while it recorded the retry, for so long that a server took the retry for abandoned [...]. The retry was taken back, so the chain is as it was; retry it again.`). A step that was created again because its job had expired is deleted instead, as the old record doesn't list its new job.

Otherwise, once the new record is written, the retry has succeeded and nothing fails it any more. The jobs it replaced can't run from then on, because the record no longer lists them. When one of them can't be deleted right away, or the step can't be enqueued (a storage error, a process that stops), a warning is logged and nothing is thrown: what is left to do is kept in the record, and done by the next `RetryChainFrom`, `ReplaceChain` or `DeleteChain` of the chain, or by a server's chain maintenance, which runs every 5 minutes (see [Things to know](#things-to-know)). So when the process stops after the retry is recorded, a server enqueues the step and deletes the jobs the retry replaced. A step still held runs at the latest when its hold ends, 2 minutes 15 seconds after it began: the record says the retry happened.

What to know about an abandoned retry:

- **Putting the step back needs a worker on the step's queue.** The worker that takes the step when its hold ends puts it back. With no server listening to that queue, the step stays `Enqueued`: it doesn't run, and it isn't put back until a worker takes it.
- **Deleting the jobs the retry created is best effort.** It is done once, after the step is put back, and only while nobody changes the chain. A job left behind stays in the `Awaiting` state and never runs, because the record doesn't list it.
- **Update clients and servers together,** as for the lease: a server from before this behaviour runs the step when its hold ends.

`RetryChainFrom` throws `InvalidOperationException` while the chain isn't settled, for a job that isn't a current step of a recorded chain, when the step's state changed meanwhile, when the step is no longer held for the retry (see above), and when a job the new steps wait for expired: finished jobs expire after a day by default, so retry soon or keep them longer with [`[ExpireAfter]`](../filters/timing.md#expireafterattribute). A waiting step that was never told to start (see [Things to know](#things-to-know)) is started first; the chain then runs again by itself, so the retry is refused as for any chain that isn't settled.

It also throws `InvalidOperationException` when a step it must create again names a method that can't be loaded any more, because it was renamed or removed since the chain was created: `Step '1.0' of chain '...' can't be created again: its method can't be loaded any more`. The steps it must create again are the steps after the one retried, and that step itself when its job expired. It says so before it holds the step or creates a job, so the chain stays as it is; the methods of the other steps aren't loaded, so a retry that doesn't need the missing method works. To run that work again, create a new chain.

### Replace before it starts

`ReplaceChain(chainId, newChain)` works while the first step is still enqueued or scheduled and the others wait. It says in the chain's record that the chain is being changed, so no step of the chain, old or new, starts meanwhile (see [When a step runs](#when-a-step-runs)), and leaves the old first step alone: it keeps its state and its time. Then it creates the new chain under the same identifier, enqueued, or scheduled for the moment the old first step was, with the old first step's schedule policy, late-start limit and queue (unless the new first step has a queue of its own). The new record is written while the job locks of the old and of the new first step are held, and is refused when the old first step started meanwhile. Once the first step started, `ReplaceChain` throws `InvalidOperationException`.

Until the new chain is recorded, the old one is kept as it was. When the new chain can't be created, its jobs are deleted and the exception is thrown. When the process stops in the middle, the old chain runs when it was to run (a first step that came due meanwhile, once the lease lapsed, 2 minutes at most after the client last renewed it), and nothing of the new chain runs.

When the client didn't confirm for 2 minutes that it was still at it and a worker deleted one of the new jobs meanwhile, `ReplaceChain` finds that as a creation does (see [Creating it](#creating-it)) and throws `InvalidOperationException` (`Chain '...' wasn't replaced: its background job '...' was deleted before the chain was recorded`): the new jobs are deleted, and the old chain is kept. That also holds when it finds the new first step deleted only after it wrote the new record: it puts the old record back first.

Otherwise, once the new chain is recorded, the replacement has succeeded. The old steps, which the record no longer lists and so can't run, are then deleted, the deepest first, so no continuation starts; the ones that can't be deleted right away are deleted later, as after a retry. Nothing throws after that point.

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

The recurring job runs `JobChainRunner.Run`, which creates a new chain at each occurrence and returns its identifier as the job's result. With `skipWhileRunning`, an occurrence whose previous chain isn't settled is skipped (deleted with the reason `Skipped: the previous chain ... is still running`). Before it decides, it starts the waiting steps of the previous chain that were never told to start (see [Things to know](#things-to-know)): that chain then runs again, and the occurrence is skipped rather than run beside it. The previous chain is read without loading the methods of its steps, so occurrences go on being decided after a deploy that renamed or removed one of them. `IRecurringJobManager.AddOrUpdateChain` does the same through a manager. The chain's definition is the recurring job's argument, so registering it again with another chain updates it, and the recurring job has every other option ([schedule policies](schedule-policies.md#recurring-jobs), `SkipAfter`, misfire handling).

An occurrence creates its chain once. The chain's identifier is derived from the job that creates it: the first 32 hexadecimal digits of the SHA-256 hash of `chain-of-job:{jobId}`, so it looks like any other chain identifier. When that job runs again after its chain was recorded (its server stopped before the job finished, or the job was retried or requeued), it finds the chain recorded under that identifier and returns it instead of creating a second one, and with `skipWhileRunning` it isn't skipped because of its own chain. When the chain was deleted meanwhile, or its record expired, the job creates it again.

## Things to know

- **Storage keys.** A chain is recorded in the hash `chain:{id}` (its definition, the job of each step in the `Jobs` field, the generation, and in the `Cleanup` field what a retry or a replacement left to do) and listed in the set `chains`. While a chain is created, retried or replaced, its `Jobs` field also holds the entry `changing`, the lease: when the change began, and until when it counts unless the client renews it. The record of a chain that is being created holds nothing else and isn't listed; on a storage that expires hashes it is kept, should the creation stop, for the chain retention, counted from the time a scheduled first step is due. A creation that is given up removes the record, or empties it when a job couldn't be deleted, and takes the chain out of `chains`, `chains:due` and `chains:expiring` in the same write. Four more keys serve the chain maintenance: the set `chains:cleanup` lists the chains with something left to do after a retry or a replacement; the set `chains:due` holds, for each chain a server is to look at again, the time of that look; the set `chains:expiring` holds, for each finished chain, the time it leaves `chains`; and the set `chains:soon` holds the looks that were asked for, one entry `{chainId}|{id}` each. The hash `chains:index` says how far the chains listed by an earlier build have been given a place in `chains:due`; the set `chains:joins` of earlier builds is removed once that is done. A recurring chain keeps its last chain's identifier in the hash `recurring-chain:{id}`. `RetryChainFrom`, `ReplaceChain` and `DeleteChain` hold the lock `lock:chain:{id}`, as a recurring chain does while it creates its chain and a server does while it looks at the chain. Each step's job carries the `ChainId` and `ChainStep` job parameters, `ChainRecorded` once a record lists it, and `ChainRetry` while a retry holds it: what the retry started from, so an abandoned retry can put the step back.
- **The record lives as long as a step is left to run, or to run again.** While the job of a step exists in a state other than `Succeeded` or `Deleted` (it waits, is scheduled, enqueued or processing, or failed for good and is kept), the record is kept, the chain stays in the list, and its flowchart keeps working. Failed jobs are kept until somebody requeues or deletes them unless you set a [retention](../server/reliability.md#retention-per-state), so a failed chain stays listed for as long as its failed job does. On a storage without expiring hashes the record stays until the chain is deleted.
- **A finished chain is kept for the chain retention.** Once every step succeeded or was deleted, or its job is gone, the record is kept for `JobChain.RecordRetention` after the last state change of a step, 30 days by default, and the chain leaves the list a minute after its record expires, at the next maintenance run. `GlobalConfiguration.Configuration.UseChainRetention(TimeSpan.FromDays(90))` sets another time, from 1 day to 36,500 days; a value outside that range throws `ArgumentOutOfRangeException`. Processes may be set differently: a record is written with the retention of the client that creates the chain and kept longer with that of the servers, never shorter. Each state change of a step extends the record once the change is committed; a process writes the new expiry about once an hour per chain, so the record can expire up to an hour sooner than the retention says. A retry or a replacement never shortens it.
- **Chain maintenance.** Every `BackgroundJobServer` runs a process that, every 5 minutes and on one server at a time (the lock `lock:chains:maintenance`), finishes what interrupted retries and replacements left to do (replaced jobs to delete, a held step to enqueue) and looks at the chains that are due for a look. A run reads only those chains, not every chain that is kept. A look, under the chain's lock, starts or skips the waiting steps that were never told to start (see the next point), extends the record while a step is left to run or failed for good, takes a chain whose record expired out of the list, and says when the next look is. Each run is bounded: it finishes up to 50 chains, does up to 500 looks that were asked for, 500 looks that are due and 500 at chains whose record expired, and gives up to 500 chains listed by an earlier build their first look, the newest first. A run that stopped at one of these limits is followed by the next one 10 seconds later. The dashboard's Chains page leaves out the chains whose record expired meanwhile. Only removing expired chains needs a storage with the extended API (expiring hashes and ranges of sets), as every storage of this project has: nothing expires without it. The rest runs on every storage.
- **When a chain is looked at.** A chain with more than one step gets its first look 5 minutes after it is recorded. While a step is enqueued or processing, a scheduled step is due, or a waiting step is about to be told to start, the next look comes 10 to 15 minutes later. A chain that stands still, behind a step that failed for good or until a scheduled step is due, is looked at again after as long as it has stood still, so each wait is about twice the one before, but no later than 10 minutes after its next scheduled step is due and no later than a quarter of the retention before its record expires. A chain with a single step is only looked at then, before its record expires. A step that leaves the `Failed`, `Succeeded` or `Deleted` state (it is requeued, deleted, or held by a retry) asks for a look about 5 minutes later, whatever the chain's next look is; a run does these looks first.
- **A step that was never told to start is started later.** A waiting step is started, or skipped, by the process that finishes the last step it waits for: a step that waits for a single step while that step changes state, a step that waits for several steps right after the state of the last of them is committed. When the process stops at that moment, or the storage fails then, the step keeps waiting. Once the steps it waits for finished more than 5 minutes ago, it is decided again from their stored states and changed only from the `Awaiting` state, with a warning in the log. Three things do that: the chain maintenance, when it looks at the chain; a recurring chain with `skipWhileRunning`, for its previous chain; and `RetryChainFrom`, for the chain it is asked to retry. So such a step is started within about 25 minutes of the moment the steps it waits for finished, also in a chain that stood still and moves on because a failed step was requeued. With [`UseResultsInContinuations()`](continuations.md#passing-the-parents-result-or-exception), a step that waits for a single step is handed what it would have been handed then: its parent's result, or the exception of a parent that failed or was deleted. When the parent's state can't be read any more, the step is left waiting, with a warning, until you requeue it. A step that waits for several steps is handed nothing, as always.
- **Every step is created at once,** so a chain of 1,000 steps creates 1,000 jobs. Steps that end up skipped are deleted when their turn comes.
- **A worker can take the first step before the last one is created.** It doesn't run then: the worker gives it back to its queue, and takes it again once the record lists the steps (see [The first step of a chain that is being created](#the-first-step-of-a-chain-that-is-being-created)).
- **What the maintenance can miss.** A step that changes state inside a transaction of your own (a `StateChangeContext` created with a transaction) asks for no look: its chain is found at its next look, which can be weeks away for a chain that stood still for long. When no server runs for longer than a quarter of the retention, the record of a chain that waits behind a failed step can expire. The chain then stays in the `chains` set for one more retention, so its steps still run when they are requeued, but it has no record: the dashboard no longer shows it, and it can't be read, retried or replaced. A warning says so. The same goes, after an upgrade, for the chains an earlier build listed whose record is gone: they stay in the set for one more retention. Until a server starts a step that was never told to start, the dashboard shows it as waiting.
- **A chain outlives the methods of its steps.** The record keeps each step's method call as the storage keeps a job's, and loads a method only to create a job from it. After a deploy that renamed or removed the method of a step, the chain can still be read (`GetChain` gives that step a `null` `Job`, and the dashboard shows its step key in place of the method), deleted and checked for being settled, its waiting steps that were never told to start are still started, and a recurring chain with `skipWhileRunning` goes on. Only `RetryChainFrom` refuses, when it must create such a step again (see [Run again from a step](#run-again-from-a-step)).
- **Failed isn't final.** A step that failed for good keeps the steps after it waiting (except failure joins), so requeuing it from the dashboard continues the chain, like `RetryChainFrom` on that step.

## See also

- [Continuations](continuations.md)
- [Fan-in and batches](fan-in-and-batches.md)
- [Schedule policies](schedule-policies.md)
- [Recurring jobs](../recurring-jobs/README.md)
