# Concurrency

These filters control how many copies of a job run at once, how often jobs may start, and whether a second copy may be created at all. They work across every server that shares the storage, because their state lives in the storage, not in memory.

## Quick example

```csharp
using Masterly.NightShift;

public class TenantExportService
{
    // At most two exports per tenant at the same time, on all servers together.
    [Throttle("tenant-{0}", MaxConcurrency = 2)]
    public Task Export(string tenantId, int exportId) => Task.CompletedTask;
}

jobs.Enqueue<TenantExportService>(x => x.Export("contoso", 1001));
```

When a third export for `contoso` is picked up while two are running, it goes back to the Scheduled state for about 15 seconds and the worker moves on to other work. If both slots are still taken then, it waits 30 seconds, then a minute, and so on, up to 5 minutes.

## Which one should I use?

| Attribute | Limits | Checked | When the limit is hit | Holds a worker while waiting? |
|-----------|--------|---------|-----------------------|-------------------------------|
| `DisableConcurrentExecution` | One running job per resource | When the job starts | Waits for the lock up to the timeout, then fails the job (and it is retried) | Yes |
| `Throttle` | `MaxConcurrency` running jobs per resource | When the job starts | `Reschedule` (default), `Wait` or `Skip` | Only with `Wait` |
| `RateLimit` | `Limit` job starts per `PerSeconds` window | When the job starts | `Reschedule` (default), `Wait` or `Skip` | Only with `Wait` |
| `PreventDuplicates` | One pending job per fingerprint | When the job is created | The new job is not created, or it replaces the pending one | No |
| `IdempotentCompletion` | A finished job is not run again by a repeated pickup | On every state change | The recorded final state is restored | No |

In short: use `Throttle` for "no more than N at a time", `RateLimit` for "no more than N per minute", `PreventDuplicates` for "don't queue this twice", and `IdempotentCompletion` for "don't redo work that already finished". `DisableConcurrentExecution` is the classic mutex from Hangfire; `Throttle` with its defaults does the same job without blocking a worker.

## Resource templates

`DisableConcurrentExecution`, `Throttle`, `RateLimit`, `CircuitBreaker` and `PreventDuplicates` all take a template that names the shared resource. `{0}`, `{1}` ... are replaced with the job's arguments (formatted with the invariant culture), and the result is lower-cased. `"tenant-{0}"` for `Export("Contoso", 7)` is `tenant-contoso`. Jobs that produce the same resource share the limit, even if they are different methods.

Without a template, `Throttle`, `RateLimit` and `CircuitBreaker` use the job's method as the resource: the full name of the job's type (namespace and generic arguments included), the method's name and its parameter types, lower-cased. For `TenantExportService.Export(string, int)` in the `Contoso.Exports` namespace that is `contoso.exports.tenantexportservice.export(system.string,system.int32)`. Types with the same name in different namespaces, and overloads of one method, get different resources. A long name is shortened like any other resource (see below). `PreventDuplicates` without a template fingerprints the same method name together with all the job's arguments. `DisableConcurrentExecution` keeps the lock name of the earlier library: the type name without its namespace, a dot, and the method name.

An argument marked [`[Redacted]`](behavior.md#redactedattribute) is not written into the resource as is: it is replaced with the first 16 hexadecimal digits of the SHA-256 of its text, such as `tenant-3c9a1f0e8b7d2a64`. Different values still give different resources, and the value doesn't show in storage keys, lock names or the reasons in the job history.

A template that refers to a missing argument (`{2}` on a method with two parameters) throws `FormatException` when the job runs or is created.

A resource is shortened only when the longest storage key built from it would pass the 100 characters SQL Server allows for set and hash keys: above 91 characters for `Throttle` (`throttle:{resource}`), 79 for `RateLimit` (`ratelimit:{resource}:{perSeconds}`, with room for a window of up to 10 digits) and 83 for `CircuitBreaker` (`circuit:{resource}:failures`). A shortened resource has exactly that many characters: its start, a `~`, and 32 hexadecimal digits of the SHA-256 of the whole text, such as `tenant-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa~3c9a1f0e8b7d2a64c1f09e7b5d3a2c18`. Different resources still get different keys. The shortened form is what you see in storage keys, lock names, job history reasons, `ConcurrencyLimitException.Resource` and the dashboard's circuit list. Resources that fit are used as they are. `DisableConcurrentExecution` uses its resource only as a lock name, and shortens it the same way above 200 characters: SQL Server cuts lock names at 255 characters, its `{schema}:` prefix included, so two long resources that start alike would otherwise share one lock.

## How it works

`Throttle`, `RateLimit` and `CircuitBreaker` keep members of the form `{unix seconds}:{job id}` in storage sets, read and change them under a short distributed lock (`{key}:lock`, 10-second timeout), and drop members that are too old. When a job may not run, they either (`CircuitBreaker` always reschedules; see [Retries](retries.md#circuitbreakerattribute)):

- **Reschedule:** move the job from Processing back to **Scheduled**, with a reason that says why, and cancel the current run. The worker is free at once; the job keeps its id, parameters and history and runs later on any server. A rescheduled job does not use up a retry attempt. The filter changes the state with the worker's state changer, so the server's filter provider (`BackgroundJobServerOptions.FilterProvider`, or the one registered in DI) applies, and the worker then leaves the state alone instead of adding its usual "Canceled by filter" Deleted state. A storage error while rescheduling is tried again twice (after 200 ms and 1 s): the state change expects the Processing state, so an attempt that reported an error but committed anyway makes the next one change nothing. If all three attempts fail, the run fails with that error and the job's retry policy decides (with `[AutomaticRetry(Attempts = 0)]` it ends Failed although its method never ran). If the server shuts down meanwhile, the job is requeued.
- **Wait:** keep the worker thread and poll until a slot frees up. If none does within `WaitTimeoutSeconds`, the job fails with `ConcurrencyLimitException` (its `Resource` property names the resource), and the normal retry rules apply. `WaitTimeoutSeconds` is the longest a worker is held, even when the next free slot is further away: the last sleep is cut short so that the job fails when the timeout ends, not at the next poll. Shutting the server down ends the wait.
- **Skip:** cancel the job. It moves to **Deleted** with the reason "Canceled by filter '...'" and never runs.

`ConcurrencyAction` values:

| Value | Meaning |
|-------|---------|
| `Reschedule` (0, default) | Back to the Scheduled state until a slot is expected to be free. |
| `Wait` (1) | Hold the worker, polling, until a slot frees up or `WaitTimeoutSeconds` passes. |
| `Skip` (2) | Delete the job. For work that is pointless when another copy is already running, such as a cache refresh. |

## DisableConcurrentExecutionAttribute

Takes a distributed lock before the job runs and releases it after. A second job with the same resource waits for the lock; if it doesn't get it within the timeout, it fails with `DistributedLockTimeoutException` and `AutomaticRetry` retries it later.

```csharp
using Masterly.NightShift;

public class ReportService
{
    // One daily report at a time; a second one waits up to 10 minutes for the first to finish.
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public void BuildDaily()
    {
    }

    // One report per customer at a time.
    [DisableConcurrentExecution("report-{0}", 300)]
    public void BuildForCustomer(int customerId)
    {
    }
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `timeoutInSeconds` / `timeoutSec` (constructor) | `int` | required | How long to wait for the lock. Zero or more; a negative value throws `ArgumentException`. |
| `resource` (constructor) | `string` | type name (without namespace) and method | Resource template (second constructor: `DisableConcurrentExecution(resource, timeoutSec)`). |
| `Resource` | `string?` | `null` | The template as given. |
| `TimeoutSec` | `int` | | The timeout as given. |
| `Order` | `int` | `-1` | Filter order. |

How it works: a server filter. `OnPerforming` calls `IStorageConnection.AcquireDistributedLock(resource, timeout)` and keeps the lock in `context.Items["DistributedLock"]`; `OnPerformed` disposes it. With SQL Server the lock is an application lock held for the whole run. The resource string is used as the lock name, so a template resource is lower-cased while the default `Type.Method` name is not. A name longer than 200 characters keeps its first 167 characters and ends with `~` and 32 hexadecimal digits of the SHA-256 of the whole name, so it fits every storage and still names one resource; shorter names are used as they are. A `[Redacted]` argument in the template is replaced with a hash, like in the other templates. If releasing the lock fails, a warning is logged and the job keeps its outcome; SQL Server releases the lock of a closed connection on its own.

The waiting job holds a worker thread the whole time. With many waiting jobs, workers are blocked doing nothing; prefer `Throttle` in that case.

## ThrottleAttribute

A distributed semaphore: at most `MaxConcurrency` jobs with the same resource run at the same time, across all servers. With the default `MaxConcurrency = 1` it is a mutex that, unlike `DisableConcurrentExecution`, does not hold a worker while waiting.

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `resource` (constructor) | `string?` | `null` | Resource template. `null` (or the parameterless constructor) means the job's method: full type name, method name and parameter types ([Resource templates](#resource-templates)). |
| `MaxConcurrency` | `int` | `1` | How many jobs may hold the resource at once. Must be greater than zero, or the job fails with `InvalidOperationException`. |
| `Action` | `ConcurrencyAction` | `Reschedule` | What happens when every slot is taken. |
| `RetryDelaySeconds` | `int` | `15` | With `Reschedule`: how long the job waits before it is tried again the first time (plus up to one second of jitter). The delay doubles every time the same job is rescheduled again. Zero or more. |
| `MaxRetryDelaySeconds` | `int` | `300` | With `Reschedule`: the longest delay the doubling reaches. A value not above `RetryDelaySeconds` turns the doubling off. Zero or more. |
| `WaitTimeoutSeconds` | `int` | `30` | With `Wait`: how long the worker waits before the job fails with `ConcurrencyLimitException`. Zero or more. |
| `StaleTimeoutMinutes` | `int` | `60` | After how long a slot that was never released is reclaimed even though its job is still in the Processing state (a server that died). A slot whose job left the Processing state is reclaimed sooner, when the resource is full. Set it above your longest run time. From 1 minute to 10 years. |
| `Order` | `int` | `-1` | Filter order. |

A negative `RetryDelaySeconds`, `MaxRetryDelaySeconds` or `WaitTimeoutSeconds`, and a `StaleTimeoutMinutes` of zero, less, or more than 10 years, throw `ArgumentOutOfRangeException` when the attribute is read, so creating or running the job fails at once.

How it works: a server filter. The slots are members of the set `throttle:{resource}`. `OnPerforming` takes the lock `throttle:{resource}:lock`, removes stale members (older than `StaleTimeoutMinutes`, unparsable, or left by an earlier attempt of the same job), and adds `{now}:{job id}` if fewer than `MaxConcurrency` live members remain. A slot is held only while its job is in the Processing state, so when every slot is taken it reads the state of each job holding one, and reclaims the slots of jobs that are in another state (they finished, failed, were rescheduled or deleted) or no longer exist. These reads happen only when the resource is full; a job whose state can't be read keeps its slot. When the storage supports `JobStorageFeatures.ExtendedApi` (SQL Server does), the set then expires `StaleTimeoutMinutes` after the newest slot, so sets of resources that are no longer used don't stay in storage. `OnPerformed` removes the member again, whether the job succeeded or failed. Releasing is only bookkeeping: if the storage fails at that moment, it tries twice more (after 100 ms and 500 ms); if that fails too, a warning is logged and the job keeps its outcome. The slot is then reclaimed by the next job that finds the resource full, once the job has left the Processing state, and in any case after `StaleTimeoutMinutes` or as soon as the same job runs again (or free it with [`Release`](#see-and-free-slots)). With `Wait`, it polls after 250 ms, 500 ms, 1 s, 2 s, then every 4 s, and the last poll comes when `WaitTimeoutSeconds` ends: with `WaitTimeoutSeconds = 5` the job fails after about 5 seconds, not 7.75. A rescheduled job's reason reads `Throttled: 2 of 2 slot(s) of 'tenant-contoso' in use`.

With `Reschedule`, the job's `ThrottleReschedules` parameter counts how often it was rescheduled in a row: the delays are 15 s, 30 s, 1 min, 2 min, 4 min, then 5 min with the defaults. The count is cleared when the job gets a slot. If clearing it fails, a warning is logged and the job runs anyway (failing it would leave its slot taken until `StaleTimeoutMinutes`); its next reschedule then waits longer. While one job holds the resource for two hours, a waiting job comes back about 30 times instead of 480, and each return is a Scheduled and an Enqueued state in its history. The flip side: a job that has backed off may wait up to `MaxRetryDelaySeconds` after the slot frees up, and a newer job can take the slot first.

### A mutex that doesn't block workers

```csharp
public class SearchIndexer
{
    [Throttle]
    public Task RebuildIndex() => Task.CompletedTask;
}
```

### Skip a refresh that is already running

```csharp
public class CacheService
{
    [Throttle(Action = ConcurrencyAction.Skip)]
    public Task RefreshProductCache() => Task.CompletedTask;
}
```

### A pool of scarce machines

```csharp
public class GpuRenderer
{
    // Four GPUs; a render job waits up to a minute for one. Renders can take two hours.
    [Throttle("gpu", MaxConcurrency = 4, Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 60, StaleTimeoutMinutes = 180)]
    public Task RenderVideo(int videoId) => Task.CompletedTask;
}
```

### See and free slots

`ThrottleAttribute.GetSlots(connection, resource)` lists the jobs that hold slots on a resource, oldest first, and `ThrottleAttribute.Release(connection, resource, jobId)` frees the slots of one job. Use them when a job is gone or stuck and the waiting jobs shouldn't wait for `StaleTimeoutMinutes`:

```csharp
using System;
using Masterly.NightShift;

public static class ThrottleAdmin
{
    public static void Show(JobStorage storage)
    {
        using (var connection = storage.GetConnection())
        {
            foreach (var slot in ThrottleAttribute.GetSlots(connection, "tenant-contoso"))
            {
                Console.WriteLine($"Job {slot.JobId} holds a slot since {slot.AcquiredAt:u}");
            }
        }
    }

    public static bool Free(JobStorage storage, string jobId)
    {
        using (var connection = storage.GetConnection())
        {
            return ThrottleAttribute.Release(connection, "tenant-contoso", jobId);
        }
    }
}
```

The resource is the formatted one: the template with the job's arguments (`tenant-contoso` for `[Throttle("tenant-{0}")]` and the argument `"Contoso"`), or the job's method, such as `contoso.exports.tenantexportservice.export(system.string,system.int32)`. Both methods lower-case it and shorten it like the filter does, so the formatted resource and the one shown in storage keys and reasons both work. `ThrottleSlot.JobId` is the job that holds the slot and `ThrottleSlot.AcquiredAt` when it took it (UTC, whole seconds). A slot older than `StaleTimeoutMinutes` is still listed until the next job on the resource reclaims it.

`Release` works under the same lock as the filter (`throttle:{resource}:lock`) and returns `false` when the job held no slot. It doesn't stop the job: if it is still running, another job may now run beside it. Nothing needs a storage feature: both use the set operations every storage has.

## RateLimitAttribute

Limits how many jobs with the same resource may **start** within a sliding window, across all servers. It doesn't care how long they run.

```csharp
using Masterly.NightShift;

public class SmsService
{
    // The SMS provider allows 100 messages a minute, shared by both methods.
    [RateLimit("sms", 100, 60)]
    public Task Send(string phoneNumber, string text) => Task.CompletedTask;

    [RateLimit("sms", limit: 100, perSeconds: 60)]
    public Task SendReminder(string phoneNumber, int appointmentId) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `resource` (constructor) | `string?` | required | Resource template. `null` means the job's method: full type name, method name and parameter types. |
| `limit` (constructor) | `int` | required | How many jobs may start per window. Must be greater than zero. |
| `perSeconds` (constructor) | `int` | required | Window length in seconds. Must be greater than zero. |
| `Limit`, `PerSeconds`, `Resource` | | | The constructor values. |
| `Action` | `ConcurrencyAction` | `Reschedule` | What happens when the limit is reached. |
| `WaitTimeoutSeconds` | `int` | `30` | With `Wait`: the longest the worker waits before the job fails with `ConcurrencyLimitException`. Zero or more; a negative value throws `ArgumentOutOfRangeException`. |
| `Order` | `int` | `-1` | Filter order. |

How it works: a server filter. Starts are members of the set `ratelimit:{resource}:{perSeconds}`, such as `ratelimit:sms:60`, so two limits on one resource with different windows (10 per second and 1000 per hour, say) are counted apart and one never removes starts the other still needs. `OnPerforming` takes the lock `ratelimit:{resource}:{perSeconds}:lock`, removes starts older than the window and records the new start if fewer than `limit` remain. When the storage supports `JobStorageFeatures.ExtendedApi` and `JobStorageFeatures.Connection.BatchedGetFirstByLowest` (SQL Server does), it doesn't read the whole set: a start's score is its time, so it removes up to 100 starts older than the window by score, counts the rest with `GetSetCount`, and reads only the oldest start when the limit is reached. With more than 100 old starts (a busy set after a quiet period), and with other storages, it reads the whole set. The set expires one window plus a minute after the newest start when the storage supports `JobStorageFeatures.ExtendedApi`.

When the limit is reached, a rescheduled job is due one second after the oldest start leaves the window (plus jitter), and a waiting job sleeps until then, but never longer than what is left of `WaitTimeoutSeconds`: with a one-hour window and `WaitTimeoutSeconds = 15`, the job fails after 15 seconds instead of holding the worker for an hour. A start counts once the job method is about to be called, so `OnPerformed` releases nothing, with one exception: when the method was never called because a later server filter canceled the run, or threw first (a `DisableConcurrentExecution` lock that timed out, for example), the start is given back. A method that was called keeps its start, whether it succeeded or failed. If giving a start back fails, a warning is logged, the run's outcome stands, and the start counts until it leaves the window. The reason reads `Rate limited: 100 job(s) per 60s already started for 'sms'`.

### Per-account API limits

```csharp
public class CrmClient
{
    // 10 calls per second per CRM account; short waits are cheaper than rescheduling here.
    [RateLimit("crm-{0}", 10, 1, Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 15)]
    public Task UpdateContact(string accountId, int contactId) => Task.CompletedTask;
}
```

### Tiered limits

A job can carry several `[RateLimit]` attributes. Many APIs allow a burst rate and a lower sustained rate; put both on the method:

```csharp
public class WeatherClient
{
    // At most 10 calls a second, and at most 1000 an hour.
    [RateLimit("weather-api", 10, 1)]
    [RateLimit("weather-api", 1000, 3600)]
    public Task FetchForecast(string city) => Task.CompletedTask;
}
```

The job starts only when every limit allows it. The limits are checked one after the other (by `Order`, then global before class before method); when one refuses, the starts the others already recorded are given back, and the job is rescheduled, skipped or fails as the refusing limit's `Action` says. A limit with `Wait` waits only when no other limit has recorded a start for the run yet: a start already recorded would age while it waited, and the job could then start after that start left the shorter window. Such a limit reschedules the job instead, to one second after its own oldest start leaves its window, like `Reschedule`, and the other limits' starts are given back. To let a limit wait, put it first (a lower `Order`). Each window has its own set (`ratelimit:weather-api:1` and `ratelimit:weather-api:3600`), so they never remove each other's starts.

Global, class and method limits all apply, and none replaces another: a `GlobalJobFilters.Filters.Add(new RateLimitAttribute("tenant", 5, 60))` and a method's `[RateLimit("sms", 100, 60)]` both count. Two limits with the same resource and window count the same starts: the run records one start, and each limit compares it with its own `limit`, so the stricter one decides. The run's own start is never removed as too old while a later limit checks the set, even when another filter held the run in between.

## PreventDuplicatesAttribute

Refuses to create a second copy of a job while the first one is still pending. The check happens at creation time, so duplicates never reach a queue. By default `Enqueue` (and `Schedule`, `ContinueJobWith`, a recurring job trigger) returns `null` for the duplicate.

```csharp
using Masterly.NightShift;

public class CatalogService
{
    // Fingerprint = method + all arguments.
    [PreventDuplicates]
    public Task Reindex(string index) => Task.CompletedTask;

    // Fingerprint = the first argument only: one pending import per supplier, whatever the timestamp.
    [PreventDuplicates("import:{0}")]
    public Task ImportFeed(string supplierId, DateTime requestedAt) => Task.CompletedTask;
}

public static class PreventDuplicatesUsage
{
    public static void Run(IBackgroundJobClient jobs)
    {
        string? first = jobs.Enqueue<CatalogService>(x => x.Reindex("products"));
        string? second = jobs.Enqueue<CatalogService>(x => x.Reindex("products"));

        if (second == null)
        {
            Console.WriteLine($"Reindex is already pending as job {first}");
        }
    }
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `template` (constructor) | `string?` | `null` | Fingerprint template over the arguments. `null` (or the parameterless constructor) fingerprints the method (full type name, method name and parameter types) and all its serialized arguments with a SHA-256 hash, so same-named types in other namespaces and overloads never share a fingerprint. |
| `Template` | `string?` | | The template as given. |
| `OnDuplicate` | `DuplicateHandling` | `Cancel` | `Cancel`: the new job is not created. `ReplaceExisting`: when the pending job hasn't started yet (Enqueued, Scheduled or Awaiting), it is deleted (reason "Replaced by a newer duplicate, background job '...'") and the new one takes its place. A pending job that is running, or still being created, is never replaced: the new job is not created, as with `Cancel`. The pending job is deleted only after the new one is created, and only if it still hasn't started. If a worker picks it up in between, or the delete fails (the error is logged), it is left alone and both jobs exist; the new job's creation still succeeds. |
| `ReleaseOnFailure` | `bool` | `true` | Release the fingerprint when the job moves to the Failed state, that is when its retries are used up, so a failed job doesn't stop a new copy. `false`: a failed job keeps blocking duplicates until it is deleted, succeeds after a requeue, or its fingerprint record expires (`ExpirationHours` after it was last scheduled). |
| `ExpirationHours` | `int` | `24` | Safety net: the fingerprint record expires after this many hours even if its release was lost. For a scheduled job the hours count from the time it is scheduled for, and they start again each time it is scheduled (a retry, for example). `0` disables the expiration. Needs a storage that supports `JobStorageFeatures.ExtendedApi` (SQL Server does); with other storages the record doesn't expire, and the owner's state check below still keeps it from blocking forever. |
| `Order` | `int` | `10` | Filter order. |

Related members: `PreventDuplicatesAttribute.FingerprintParameter` (`"Fingerprint"`), `GetFingerprint(Job)`, `GetKey(fingerprint)` (`"fingerprint:{fingerprint}"`) and `ReadOwner(connection, fingerprint)`, which returns the id of the job that holds a fingerprint. A templated fingerprint longer than 88 characters (the most that fits the key `fingerprint:{fingerprint}`) is shortened like a resource (see [Resource templates](#resource-templates)).

How it works: a client filter and an apply filter.

1. `OnCreating` computes the fingerprint, takes the distributed lock `fingerprint:{fingerprint}` (30-second timeout) and reads the hash `fingerprint:{fingerprint}`. If it names a job that is still pending (not Succeeded or Deleted, and not Failed unless `ReleaseOnFailure` is `false`), or a creation that started less than two minutes ago, the new job is a duplicate. With `ReplaceExisting` and a pending job that hasn't started yet, the filter goes on and remembers that job. Otherwise the duplicate is not created. When the new job may be created, the filter reserves the fingerprint and sets the `Fingerprint` job parameter.
2. `OnCreated` writes the new job id into the hash. With `ReplaceExisting` it then deletes the job it replaces, only if that job is still Enqueued, Scheduled or Awaiting: a job that started running in the meantime is left alone, and both run. The new job exists at this point, so an error here (writing the hash, or deleting the old job) is logged and doesn't make `Create` fail; if the hash couldn't be written, its reservation blocks duplicates for two minutes and then lets them through. When creation failed or another filter canceled it, `OnCreated` removes the reservation, or gives the fingerprint back to the job that was to be replaced, which then stays pending. The writes set the hash to expire after `ExpirationHours`, counted from the scheduled time for a scheduled job.
3. When the owning job reaches a final state, or the Failed state with `ReleaseOnFailure`, `OnStateApplied` removes the hash. When the owner is scheduled again (a retry, or a reschedule by another filter), `OnStateApplied` moves the hash's expiration to the new time plus `ExpirationHours`.

Because the owner's current state is checked on every attempt, a fingerprint can't block forever even when the release was lost.

### Replace a pending job with a newer one

```csharp
public class CatalogService
{
    // A newer cost replaces the pending recalculation for the same product.
    [PreventDuplicates("price-recalc:{0}", OnDuplicate = DuplicateHandling.ReplaceExisting)]
    public Task RecalculatePrices(int productId, decimal newCost) => Task.CompletedTask;
}
```

### Skip a recurring run while the previous one is still waiting

Put `[PreventDuplicates]` on the method of a recurring job: when the schedule fires while the last run is still enqueued, processing or scheduled for a retry, the new run is not created. A run that failed for good doesn't block the next one; set `ReleaseOnFailure = false` if the failure should be dealt with first:

```csharp
public class BillingService
{
    // No new run until the failed one has been deleted or requeued and succeeded.
    [PreventDuplicates(ReleaseOnFailure = false)]
    public Task ChargeOverdueInvoices() => Task.CompletedTask;
}
```

## IdempotentCompletionAttribute

Remembers the first final state chosen for a job. If the same job is picked up again later, for example because the storage connection dropped while the Succeeded state was being saved and the job came back from the queue, it is not run a second time: it goes straight to the recorded state.

```csharp
using Masterly.NightShift;

public class PaymentService
{
    [IdempotentCompletion]
    public Task ChargeSubscription(int subscriptionId) => Task.CompletedTask;
}
```

It has no parameters; `Order` is `0`. Related member: `IdempotentCompletionAttribute.CompletionParameter` (`"Completion"`).

How it works: an election and apply filter. The first time a final state (Succeeded or Deleted) is applied to the job, it is serialized into the `Completion` job parameter, which is written right away, before the state-change transaction commits. The state recorded is the one left after every election filter has run, such as the Deleted state `AutomaticRetry` elects with `OnAttemptsExceeded = Delete`. A Succeeded state is recorded without the job's return value, so a result that can't be serialized doesn't matter; if recording fails anyway, a warning is logged and the state change goes on. It protects against a lost final state, not against a crash inside the job method: if the process dies before the job finishes, nothing is recorded and the job runs again as usual. From then on, whenever a worker tries to move the job to Processing, the filter replaces that with the recorded state, so the job is not executed again, even when an election filter with a lower `Order` (such as `LatencyTimeout`) already replaced the Processing state with another one. Creation is not affected.

Explicit state changes still work:

- **Delete** in the dashboard (or `BackgroundJob.Delete`) moves a succeeded job to Deleted. The recorded state is kept.
- **Requeue** of a succeeded or deleted job is a request to run it again: the filter forgets the recorded state, the job runs once more, and its next final state is recorded anew.
- A job whose final state was lost is still Processing in storage. Requeuing it keeps the recorded state, so it is not executed a second time.

## Combining them

Filters of different types stack. This end-of-day job is created once per date, runs one at a time, and is retried three times:

```csharp
public class LedgerService
{
    [PreventDuplicates("close-day:{0}")]
    [Throttle("ledger")]
    [AutomaticRetry(Attempts = 3)]
    public Task CloseDay(string date, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

## Things to know

- **A requeued failed job doesn't take its fingerprint back.** With the default `ReleaseOnFailure`, the fingerprint is released when the job fails for good. If you requeue it from the dashboard, a new copy created in the meantime can run alongside it.
- **`ExpirationHours` covers waiting in a queue too.** The hours count from the scheduled time and start again on every reschedule, but a job that sits in the Enqueued state longer than `ExpirationHours` (a paused or unserved queue) loses its fingerprint, and a duplicate can then be created. Raise `ExpirationHours` above how long your jobs can wait, or set it to `0`. The expiration needs a storage that supports `JobStorageFeatures.ExtendedApi`, as SQL Server does.
- **A rescheduled job goes back to its queue.** A job created with a queue of its own returns to it. A job whose queue came only from its Enqueued state (`new EnqueuedState("emails")`) has it remembered in the `HeldQueue` job parameter while it waits, like a job held by [drain mode](../server/drain-mode.md), and each of these filters puts it back in that queue when it is enqueued again, so this works even after `GlobalJobFilters.Filters.Remove<DrainModeFilter>()`. To know that queue, `Throttle`, `RateLimit` and `CircuitBreaker` read the job's Enqueued state when it starts running (one state read per run, only for jobs without a queue of their own). A job that was requeued while it ran (a shutdown that reached `ShutdownTimeout`, a crash) comes back still in the Processing state, which names no queue; its queue is then read from the last Enqueued state in its history, one more read on that rare path only.
- **Server clocks must agree.** `Throttle`, `RateLimit` and `CircuitBreaker` stamp slots, starts and failures with the clock of the server that runs the job, and compare them with the clock of the server that checks them. Keep the clocks of all servers in sync (NTP): with a server whose clock is a minute ahead, other servers see its entries as a minute younger, and it sees theirs as a minute older, so a `RateLimit` window or a `CircuitBreaker` failure window can be cut short, and a `Throttle` slot can be reclaimed early when `StaleTimeoutMinutes` is short.
- **Waiting jobs add to their history.** Every reschedule by `Throttle`, `RateLimit` or `CircuitBreaker` adds a Scheduled and an Enqueued state to the job. `Throttle` doubles its delay to keep this down; still, a job that waits for hours behind a long holder collects a few dozen states.
- **Per-value templates leave state behind.** A template such as `"tenant-{0}"` creates sets and hashes per value. With a storage that supports `JobStorageFeatures.ExtendedApi` the `Throttle` and `RateLimit` sets expire on their own; with other storages they stay, one small set per value.
- **Slots are released by `OnPerformed`.** If the storage fails while the slot is released (three tries), the slot is reclaimed as soon as a job finds the resource full and the holder has left the Processing state. If a server dies while a throttled job runs, the job stays in the Processing state, so its slot stays taken until `StaleTimeoutMinutes` passes, the same job runs again, or you free it with [`ThrottleAttribute.Release`](#see-and-free-slots). Keep `StaleTimeoutMinutes` above your longest job, or slots are reclaimed from jobs that are still running. A job deleted or requeued from the dashboard while its method still runs loses its slot at once, since it is no longer Processing.
- **Reschedule and retries are separate.** Rescheduling because of `Throttle`, `RateLimit` or `CircuitBreaker` doesn't touch `RetryCount`, but the time spent waiting counts against [`LatencyTimeout`](timing.md#latencytimeoutattribute) and [`Deadline`](timing.md#deadlineattribute).
- **One attribute of each type per job, except `RateLimit`.** `DisableConcurrentExecution`, `Throttle`, `PreventDuplicates`, `IdempotentCompletion` and `CircuitBreaker` can't be repeated on one method, and a method attribute replaces a class attribute (or global filter) of the same type. `RateLimit` may be repeated, and every one applies ([Tiered limits](#tiered-limits)).
- **A `DisableConcurrentExecution` lock lives as long as its database session.** On SQL Server the lock is an application lock held by the session of the connection that took it. If that session ends while the job runs (the connection is killed, the database fails over or restarts), the lock is gone, and another job with the same resource can start while the first one is still running. `Throttle` slots are stored rows and survive this. Use `Throttle` with `MaxConcurrency = 1` when an overlap after a database outage would do harm.
- **Bookkeeping never fails a job.** Releasing a `Throttle` slot or a `DisableConcurrentExecution` lock, counting a `Throttle` reschedule in `ThrottleReschedules`, giving back a `RateLimit` start, removing a `PreventDuplicates` reservation after a failed creation, and forgetting a quiet closed circuit are logged as warnings when the storage fails; the job's own outcome stands.
- **Templates end up in storage keys.** A [`[Redacted]`](behavior.md#redactedattribute) argument is replaced with a hash, but a hash of a short or guessable value (a PIN, a phone number) can be guessed back. Don't build resources or fingerprints from secrets.

## See also

- [Retries](retries.md)
- [Timing](timing.md)
- [Custom filters](custom-filters.md)
- [Queues and workers](../server/queues-and-workers.md)
- [Recurring jobs](../recurring-jobs/creating-recurring-jobs.md)
- [Managing jobs](../jobs/managing-jobs.md)
