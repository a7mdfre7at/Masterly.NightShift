# Custom storage

A job storage is the only place Masterly.NightShift keeps state, so supporting another database means implementing `JobStorage` and a few classes around it. This page is for storage authors: what to implement, which optional features to advertise, the behavior the rest of the library relies on, and a skeleton to start from. Storages written for Hangfire need to be recompiled against the `Masterly.NightShift` names; their logic carries over.

## Quick example

A storage is registered like the SQL Server one, through `UseStorage`. Give it an extension method so users can write one line:

```csharp
using Masterly.NightShift;

public static class MyDbStorageExtensions
{
    public static IGlobalConfiguration<MyDbStorage> UseMyDbStorage(
        this IGlobalConfiguration configuration,
        string connectionString,
        MyDbStorageOptions? options = null)
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        return configuration.UseStorage(new MyDbStorage(connectionString, options ?? new MyDbStorageOptions()));
    }
}
```

```csharp
builder.Services.AddNightShift(config => config
    .UseMyDbStorage(builder.Configuration.GetConnectionString("MyDb")!));
builder.Services.AddNightShiftServer();
```

`MyDbStorage` and its classes are in the [skeleton](#skeleton) below.

## How it works

Every part of the library talks to the storage through the same small API:

- **Clients** create a job with `IStorageConnection.CreateExpiredJob` (a job with no state that expires by itself if nothing else happens), then commit a transaction that sets its first state (`SetJobState`), persists it and, for an enqueued job, adds it to a queue (`AddToQueue`).
- **Workers** call `FetchNextJob`, which blocks until a job is available, move the job to Processing, run it, record the result in a transaction, and finally call `IFetchedJob.RemoveFromQueue`. Every state change happens under the distributed lock `job:{id}:state-lock`.
- **The delayed and recurring job schedulers** read the `schedule` and `recurring-jobs` sets by lowest score (a Unix timestamp), under distributed locks, and enqueue what is due.
- **The server heartbeat and watchdog** call `AnnounceServer`, `Heartbeat`, `RemoveServer` and `RemoveTimedOutServers`.
- **The dashboard** reads through `IMonitoringApi` and `GetReadOnlyConnection()`.
- **Storage processes** you provide run inside each server, for example to delete expired data.

Everything else (recurring jobs, retries, continuations, batches, tags, statistics, the audit log) is built from five primitives: hashes, sets with scores, lists, counters and job parameters. A storage doesn't need to know about any of those features; it needs to implement the primitives correctly.

## Options / API

### What to implement

| Type | Derive from | Role |
|---|---|---|
| Storage | `JobStorage` (abstract, in `Masterly.NightShift`) | Entry point: hands out connections and the monitoring API, lists background processes, reports features. Long-lived and shared by all threads. |
| Connection | `JobStorageConnection` (in `Masterly.NightShift.Storage`) | Reads, job creation, fetching, locks, server registration. Created per operation and disposed by the caller; used by one thread at a time. |
| Transaction | `JobStorageTransaction` | Buffered writes applied atomically by `Commit`. |
| Fetched job | `IFetchedJob` | A job claimed from a queue by one worker. |
| Monitoring API | `JobStorageMonitor` | Queries for the dashboard and `IMonitoringApi` users. |
| Background processes | `IBackgroundProcess` (in `Masterly.NightShift.Server`) | Optional: expiration, aggregation, keep-alive. |

### `JobStorage` members

| Member | Required | Default | Meaning |
|---|---|---|---|
| `GetConnection()` | Yes | | A new connection. |
| `GetMonitoringApi()` | Yes | | The monitoring API. |
| `GetReadOnlyConnection()` | No | `GetConnection()` | A connection for reads only, used by the dashboard. May point at a replica. |
| `HasFeature(string featureId)` | No | `false` for everything | Which optional features are supported. See [Feature flags](#feature-flags). |
| `GetStorageWideProcesses()` | No | none | Processes every server runs for this storage, unless it's a lightweight server, for example the cleanup of expired data. |
| `GetServerRequiredProcesses()` | No | none | Processes every server runs, lightweight ones included, for example a keep-alive for fetched jobs. |
| `GetStateHandlers()` | No | none | Extra `IStateHandler`s applied in every state transition, for storage-specific bookkeeping. |
| `WriteOptionsToLog(ILog logger)` | No | nothing | Called when a server starts, to log your options. |
| `LinearizableReads` | No | `false` | Return `true` only if a read always sees every write committed before it (no replicas, no eventual consistency). Lets the state changer skip waiting for a job's first state to appear. |
| `Purge(StoragePurgeOptions)` | No | throws `NotSupportedException` | Delete job data, keeping what the options ask for: recurring job definitions (`KeepRecurringJobs`), servers (`KeepServers`), the `audit` list (`KeepAuditLog`) and the `hangfire:control` hash (`KeepDrainMode`). Report `JobStorageFeatures.Purge` when you implement it; the dashboard then shows its maintenance buttons. |
| `JobExpirationTimeout` | (property) | 1 day | How long finished jobs are kept; set by users. |
| `ToString()` | Recommended | type name | Shown in logs and on the dashboard. Don't include passwords. |
| `JobStorage.Current` | (static property) | | The storage used by the static APIs and by clients and servers created without one. `UseStorage` and `AddNightShift` set it; your storage doesn't. |

### Feature flags

Optional capabilities are advertised by `HasFeature`, with the ids in `JobStorageFeatures`. A storage answers `true` for what it supports; the library checks before using a capability, or falls back.

| Constant | Id | Supported means | Without it |
|---|---|---|---|
| `JobStorageFeatures.ExtendedApi` | `Storage.ExtendedApi` | The extended members of `JobStorageConnection` and `JobStorageTransaction` (counts, ranges, TTLs, `ExpireSet`, `RemoveSet` and so on) are implemented. | Extensions that check it avoid those members. Note that much of the library calls them whenever the connection derives from `JobStorageConnection`, without checking (see [Why base classes](#why-base-classes-and-not-new-interface-members)). |
| `JobStorageFeatures.JobQueueProperty` | `Job.Queue` | The job's own queue (`Job.Queue`, set by `Enqueue("queue", ...)` overloads) is stored with the job. Serializing with `InvocationData.SerializeJob(job)` and `SerializePayload()` includes it. | Creating a job with an explicit queue throws `NotSupportedException`; only `[Queue]` works. |
| `JobStorageFeatures.Purge` | `Storage.Purge` | `Purge` is implemented. | The dashboard hides Clear all jobs / Reset storage; calls throw `NotSupportedException`. |
| `JobStorageFeatures.Connection.GetUtcDateTime` | `Connection.GetUtcDateTime` | `GetUtcDateTime()` returns the database clock. The schedulers, the dashboard, the [health endpoints and the health check](../monitoring/health-checks.md) and the lease of a [chain](../jobs/chains.md#when-a-step-runs) that is being created or changed use it. Server heartbeats are compared with it, so write them and time servers out with the same clock. The SQL Server, Redis and MongoDB storages read their server's clock, the Entity Framework Core storage [the database's where it knows how](entity-framework-core.md#the-databases-clock), and the in-memory storage answers with the process's. | Schedulers, the dashboard and the health checks use the application server's clock, so clock skew between servers shows, and the lease of a chain that is being created or changed is written and compared with the clocks of the processes. |
| `JobStorageFeatures.Connection.GetQueueEntryId` | `Connection.GetQueueEntryId` | `JobStorageConnection.GetQueueEntryId(fetchedJob)` returns an identifier of the queue entry a fetched job came from. It must stay the same for that entry when it is fetched again (put back by its worker, or handed out again after the invisibility timeout), and differ for every other entry of the job, in any queue; `null` for a fetched job whose entry the connection can't tell. The worker records it in the `Processing` state (`QueueEntry`), and a worker that takes the job from another entry while it is `Processing` removes that entry instead of running the job (see [A job with several queue entries](../server/reliability.md#a-job-with-several-queue-entries)). | Workers can't tell the entries apart: a job with two entries in its queues can run twice at once. |
| `JobStorageFeatures.Connection.GetSetContains` | `Connection.GetSetContains` | `GetSetContains(key, value)` is implemented. | Whether a [chain](../jobs/chains.md#when-a-step-runs) without a record is still listed is read with `GetAllItemsFromSet`, the whole `chains` set. |
| `JobStorageFeatures.Connection.LimitedGetSetCount` | `Connection.GetSetCount.Limited` | `GetSetCount(keys, limit)` is implemented. | Not used by the library today; for extensions. |
| `JobStorageFeatures.Connection.BatchedGetFirstByLowest` | `Connection.BatchedGetFirstByLowestScoreFromSet` | `GetFirstByLowestScoreFromSet(key, from, to, count)` is implemented. | The schedulers fetch due jobs one at a time. |
| `JobStorageFeatures.Transaction.AcquireDistributedLock` | `Transaction.AcquireDistributedLock` | `JobStorageTransaction.AcquireDistributedLock` holds a lock until the transaction ends. A state change made inside a caller's transaction locks the job this way. | Callers lock through the connection instead. |
| `JobStorageFeatures.Transaction.RemoveFromQueue(typeof(MyDbFetchedJob))` | `TransactionalAcknowledge:MyDbFetchedJob` | `JobStorageTransaction.RemoveFromQueue(fetchedJob)` removes the queue entry as part of the commit, for fetched jobs of that type. The worker then acknowledges a job in the same transaction as its final state. | The worker calls `IFetchedJob.RemoveFromQueue()` after the state change. |
| `JobStorageFeatures.Transaction.CreateJob` | `Transaction.CreateJob` | `JobStorageTransaction.CreateJob` creates a job inside a transaction. | Not used by the library today; reserved for extensions. |
| `JobStorageFeatures.Transaction.SetJobParameter` | `Transaction.SetJobParameter` | `JobStorageTransaction.SetJobParameter` sets a parameter inside a transaction. The job parameters that go with a state change, such as the `Lease` of a job picked up again, a continuation's due time or the count of failed policy checks, are written in its transaction, and the `ChainRecorded` parameter of a [chain](../jobs/chains.md#creating-it)'s steps with the chain's record. Every storage of this project supports it. | They are written through the connection, beside the transaction: `ChainRecorded` right after the chain's record is committed, one write per step. |
| `JobStorageFeatures.Monitoring.AwaitingJobs` | `Monitoring.AwaitingJobs` | `JobStorageMonitor.AwaitingJobs` and `AwaitingCount` are implemented. | Every awaiting job is also added to an `awaiting` set, which the dashboard's Awaiting page reads instead. (With compatibility levels before 180 the set is written either way.) |
| `JobStorageFeatures.Monitoring.DeletedStateGraphs` | `Monitoring.DeletedStateGraphs` | `DeletedByDatesCount` and `HourlyDeletedJobs` are implemented. | The home page graphs show no deleted jobs. |
| `JobStorageFeatures.Monitoring.QueueCounts` | `Monitoring.QueueCounts` | `JobStorageMonitor.QueueCounts` is overridden with a query cheaper than `Queues`: each queue's `Name`, `Length` and `Fetched`, with an empty `FirstJobs`. The dashboard's `/health` and `/metrics` endpoints and the job page's diagnostics use it. | They call `Queues`, which also reads the first jobs of every queue. The default `QueueCounts` calls `Queues` too. |

`JobStorageFeatures.GetNotSupportedException(featureId)` builds the exception the base classes throw for an unsupported feature; use it in your own code for the same message.

### Why base classes and not new interface members

`IStorageConnection`, `IWriteOnlyTransaction`, `IFetchedJob` and `IMonitoringApi` are frozen. Many storages live in other repositories, and adding a member to an interface would break every one of them. New capabilities are added instead as virtual members of `JobStorageConnection`, `JobStorageTransaction` and `JobStorageMonitor`, which throw `NotSupportedException` until a storage overrides them, together with a feature id. Old storages keep compiling and working; new ones opt in.

So:

- **Derive from the base classes**, not from the interfaces. The library checks `connection is JobStorageConnection` (and the same for transactions and the monitoring API) to reach the extended members; a storage that only implements the interfaces loses tags paging, batch progress, the activity feed, the audit log, job consoles, insights and batched scheduling.
- **Override the whole extended API.** Several features call extended members when the connection derives from `JobStorageConnection`, without checking `HasFeature`. An unimplemented member then throws `NotSupportedException` at run time. The skeleton lists them all.
- **Report a feature only when it's fully implemented.**
- **Don't add members to the interfaces** in a fork. Ask for a new feature on the base classes instead.

### Required semantics

#### Transactions

- Methods of `JobStorageTransaction` only record work. Nothing is visible to anyone until `Commit()`, which applies all of it atomically and durably, or none of it. `Dispose()` without `Commit()` discards it.
- A transaction is used by one thread and committed at most once.
- `AcquireDistributedLock(resource, timeout)` (if reported) takes the lock immediately and releases it when the transaction commits or is disposed.
- `SetJobParameter(jobId, name, value)` (if reported) writes the parameter in the same commit: it is visible only from the commit, and a commit that fails leaves the old value. A parameter of a job that doesn't exist (it expired or was purged, or its id is malformed) is ignored, and the rest of the transaction commits: a state change or a chain's record isn't lost because a job is gone. The conformance suite checks this.
- `RemoveFromQueue(fetchedJob)` (if reported for that fetched-job type) deletes the queue entry in the same commit. After that commit, the fetched job's own `RemoveFromQueue`, `Requeue` and `Dispose` must do nothing.

#### Jobs and states

| Member | Behavior |
|---|---|
| `CreateExpiredJobs(jobs, parameters, createdAt, expireIn)` (on `JobStorageConnection`, optional) | Store several jobs at once and return their identifiers in the same order. The default calls `CreateExpiredJob` for each; override it to use fewer round trips. `EnqueueMany` calls it |
| `CreateExpiredJob(job, parameters, createdAt, expireIn)` | Store the job (`InvocationData.SerializeJob(job)`; `SerializePayload()` gives one string, or store `SerializePayload(excludeArguments: true)` and `Arguments` separately) with its parameters, no current state, and an expiration time of `createdAt + expireIn`. Return its id as a string. `null` or empty means "not created". Parameter values may be `null`. |
| `GetJobData(jobId)` | The job, or `null` when the id is unknown or malformed. Set `InvocationData`, `State` (current state name), `CreatedAt`, and `Job` from `InvocationData.DeserializeJob()`; when that throws `JobLoadException`, put the exception in `LoadException` instead of throwing. `ParametersSnapshot` (all parameters) is optional and saves round trips. |
| `GetStateData(jobId)` | The current state's `Name`, `Reason` and `Data`, or `null`. |
| `SetJobParameter` / `GetJobParameter` | Insert or update one parameter; return `null` for a missing one. |
| `SetJobState(jobId, state)` | Add a history entry (`state.Name`, `state.Reason`, `state.SerializeData()`, current UTC time) and make it the current state. |
| `AddJobState(jobId, state)` | Add a history entry without changing the current state. |
| `ExpireJob(jobId, expireIn)` / `PersistJob(jobId)` | Set or clear the job's expiration time. An expired job is deleted with its states and parameters. |

#### Queues and fetching

| Member | Behavior |
|---|---|
| `AddToQueue(queue, jobId)` | Add the job to the queue, as part of the commit. |
| `FetchNextJob(queues, cancellationToken)` | Block until a job is available in any of the queues, claim it so no other worker gets it, and return it. Never return `null`. When the token is canceled, throw `OperationCanceledException`. |
| `IFetchedJob.RemoveFromQueue()` | The job is done with: delete the queue entry. |
| `IFetchedJob.Requeue()` | Give the job back: make the entry available again at once, in the same queue, and leave the job as it was (its state, history and parameters). A worker calls it in two cases only: when the server stops while the worker waits with a job whose state it didn't change (it tries up to 3 times), and when an exception stops the worker before the job's final state is stored, so the job runs again. A worker that waits for the first step of a [chain](../jobs/chains.md#the-first-step-of-a-chain-that-is-being-created) keeps the job fetched while it waits, so the storage doesn't see one call per look. |
| `IFetchedJob.Dispose()` | If neither of the above was called, requeue. Stop any keep-alive. |

A claimed job must come back if its server dies without calling either method. Two common ways: a claim timestamp that a keep-alive refreshes and that other workers ignore only while it's recent (the SQL Server storage's sliding invisibility), or a database transaction held open while the job runs. A job may run more than once after a crash; it must never be lost.

#### Distributed locks

`AcquireDistributedLock(resource, timeout)` returns an `IDisposable` that holds an exclusive lock on `resource` for every process using the storage, until it's disposed.

- Throw `DistributedLockTimeoutException(resource)` if the lock isn't acquired within `timeout`.
- Acquiring a resource the same connection already holds must succeed, and the lock is released when the last holder disposes.
- A lock held by a process that dies must be released, through a session-bound lock or an expiry that a keep-alive extends.

#### Sets, hashes, lists and counters

Keys are strings such as `schedule`, `recurring-job:nightly-report` or `stats:succeeded:2026-11-06`; plan for at least 100 characters. Values are never `null`.

| Primitive | Behavior |
|---|---|
| Set | Unique values per key, each with a `double` score. `AddToSet(key, value)` uses score 0; adding an existing value updates its score. `GetFirstByLowestScoreFromSet(key, from, to)` returns the value with the lowest score between `from` and `to` inclusive, or `null`; the batched overload returns up to `count`, lowest first. `GetRangeFromSet(key, start, end)` is ordered by score, with inclusive zero-based indexes: a negative position comes before the first item, so a range that ends before the first item, or before its start, reads nothing, as on SQL Server. `GetAllItemsFromSet` returns an empty set, not `null`, for a missing key. |
| Hash | Fields with string values per key. `SetRangeInHash` inserts or updates fields. `GetAllEntriesFromHash` returns `null` when the key has no fields. `GetValueFromHash` returns `null` for a missing field. `RemoveHash` deletes the key. |
| List | Values per key, newest first: `InsertToList` puts the value at index 0. `GetRangeFromList(key, start, end)` uses inclusive indexes counted from the newest, numbered as for sets: a range that ends before the first item, or before its start, reads nothing. `TrimList(key, keepStartingFrom, keepEndingAt)` keeps only that index range. `RemoveFromList` removes every occurrence of the value. |
| Counter | `IncrementCounter` and `DecrementCounter` add +1 and -1; the overloads with `expireIn` set an expiration time. `GetCounter` returns the sum. |
| Expiration | `ExpireSet`, `ExpireHash`, `ExpireList` set an expiration time on all of a key's entries; `PersistSet`, `PersistHash`, `PersistList` clear it. `GetSetTtl`, `GetHashTtl` and `GetListTtl` return the time left, or `TimeSpan.FromSeconds(-1)` when there is none. Expired entries must disappear, usually through a storage-wide background process that deletes them periodically. |

#### Servers

| Member | Behavior |
|---|---|
| `AnnounceServer(serverId, context)` | Insert or update the server with `context.WorkerCount`, `context.Queues`, a start time and a heartbeat of now. "Now" is the clock `GetUtcDateTime` answers with when the storage reports `Connection.GetUtcDateTime`: the dashboard and the health checks compare heartbeats with that clock. |
| `Heartbeat(serverId)` | Update the heartbeat, with the same clock. Throw `BackgroundServerGoneException` if the server isn't registered (for example, the watchdog removed it); the server then announces itself again. |
| `RemoveServer(serverId)` | Delete it. |
| `RemoveTimedOutServers(timeOut)` | Delete servers whose last heartbeat is older than `timeOut` by that clock, and return how many. |

#### Monitoring

Derive from `JobStorageMonitor` and implement every abstract member; add `AwaitingJobs`/`AwaitingCount` and `DeletedByDatesCount`/`HourlyDeletedJobs` with their features, and `QueueCounts` with `Monitoring.QueueCounts` when your storage can count the queues without loading their first jobs. Job lists are `JobList<TDto?>`, a list of id/DTO pairs; an entry's DTO is `null` when the job expired between queries. Some DTO fields must never be `null` because the dashboard uses them without checks: see [For storage implementers](../changes-from-hangfire.md#for-storage-implementers).

The DTOs are in `Masterly.NightShift.Storage.Monitoring`. Every job DTO has `Job` (from `InvocationData.DeserializeJob()`), `LoadException` (set instead of `Job` when that throws) and `InvocationData`; the table lists the other fields. `In{State}State` is `true` when the job is still in the list's state (a job can change state between the list query and the read of its details), and `StateData` is the current state's data.

| Member | Returns | Fields to fill |
|---|---|---|
| `Queues()` | `IList<QueueWithTopEnqueuedJobsDto>` | `Name`, `Length` (waiting jobs), `Fetched` (fetched jobs, or `null` if the storage can't tell), `FirstJobs` (the first few waiting jobs, as `JobList<EnqueuedJobDto?>`). `QueueCounts()` returns the same type with an empty `FirstJobs`. |
| `Servers()` | `IList<ServerDto>` | `Name`, `WorkersCount`, `StartedAt`, `Queues`, `Heartbeat`. |
| `JobDetails(jobId)` | `JobDetailsDto?` | `CreatedAt`, `ExpireAt`, `Properties` (the job parameters), `History` (`StateHistoryDto`s, newest first: `StateName`, `Reason`, `CreatedAt`, `Data`). `null` for an unknown id. |
| `GetStatistics()` | `StatisticsDto` | Counts: `Servers`, `Recurring`, `Enqueued`, `Queues`, `Scheduled`, `Processing`, `Succeeded`, `Failed`, `Deleted`, and `Retries` and `Awaiting` (`null` when unknown). |
| `EnqueuedJobs(queue, from, perPage)` | `EnqueuedJobDto` | `State`, `EnqueuedAt`, `InEnqueuedState`, `StateData`. |
| `FetchedJobs(queue, from, perPage)` | `FetchedJobDto` | `State`, `FetchedAt`. |
| `ProcessingJobs(from, count)` | `ProcessingJobDto` | `ServerId`, `StartedAt`, `InProcessingState`, `StateData`. |
| `ScheduledJobs(from, count)` | `ScheduledJobDto` | `EnqueueAt` (when it is due), `ScheduledAt`, `InScheduledState`, `StateData`. |
| `SucceededJobs(from, count)` | `SucceededJobDto` | `Result`, `TotalDuration` (milliseconds), `SucceededAt`, `InSucceededState`, `StateData`. |
| `FailedJobs(from, count)` | `FailedJobDto` | `Reason`, `FailedAt`, `ExceptionType`, `ExceptionMessage`, `ExceptionDetails`, `InFailedState`, `StateData`. |
| `DeletedJobs(from, count)` | `DeletedJobDto` | `DeletedAt`, `InDeletedState`, `StateData`. |
| `AwaitingJobs(from, count)` | `AwaitingJobDto` | `AwaitingAt`, `InAwaitingState`, `StateData`, `ParentStateName` (the current state of the job it waits for). |

The `*Count` methods count the same lists. The SQL Server storage returns succeeded, failed and deleted jobs newest first and the other lists oldest first; do the same so the dashboard pages read alike. `SucceededByDatesCount`, `FailedByDatesCount` and `DeletedByDatesCount` return one count per day for the last 7 days, and `HourlySucceededJobs`, `HourlyFailedJobs` and `HourlyDeletedJobs` one count per hour for the last 24 hours, keyed by UTC time; the home page's history graph draws them.

## Skeleton

A starting point that compiles. Replace each `NotImplementedException` with real code.

### The storage

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

public sealed class MyDbStorageOptions
{
    public TimeSpan InvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ExpirationCheckInterval { get; set; } = TimeSpan.FromMinutes(30);
}

public class MyDbStorage : JobStorage
{
    private static readonly HashSet<string> Features = new(StringComparer.OrdinalIgnoreCase)
    {
        JobStorageFeatures.ExtendedApi,
        JobStorageFeatures.JobQueueProperty,
        JobStorageFeatures.Connection.GetUtcDateTime,
        JobStorageFeatures.Connection.BatchedGetFirstByLowest,
    };

    public MyDbStorage(string connectionString, MyDbStorageOptions options)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal string ConnectionString { get; }
    internal MyDbStorageOptions Options { get; }

    public override IStorageConnection GetConnection() => new MyDbConnection(this);

    public override IMonitoringApi GetMonitoringApi() => new MyDbMonitoringApi(this);

    public override bool HasFeature(string featureId)
    {
        if (featureId == null) throw new ArgumentNullException(nameof(featureId));
        return Features.Contains(featureId) || base.HasFeature(featureId);
    }

    // Runs once per server, unless the server is lightweight
    public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
    {
        yield return new MyDbExpirationManager(Options.ExpirationCheckInterval);
    }

    public override void WriteOptionsToLog(ILog logger)
    {
        logger.Info($"Using MyDb storage, invisibility timeout: {Options.InvisibilityTimeout}.");
    }

    public override string ToString() => "MyDb";
}
```

### The connection

```csharp
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

internal sealed class MyDbConnection : JobStorageConnection
{
    private readonly MyDbStorage _storage;

    public MyDbConnection(MyDbStorage storage) => _storage = storage;

    public override IWriteOnlyTransaction CreateWriteTransaction() => new MyDbTransaction(_storage);

    // Throw DistributedLockTimeoutException when the lock isn't acquired in time.
    // Re-acquiring a resource this connection already holds must succeed.
    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => throw new NotImplementedException();

    // Insert the job and its parameters atomically, with no state, expiring at createdAt + expireIn.
    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
        => throw new NotImplementedException();

    // Block until a job is available in one of the queues, or throw OperationCanceledException
    // when the token is canceled. The returned job stays invisible to other workers.
    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken) => throw new NotImplementedException();

    public override void SetJobParameter(string id, string name, string? value) => throw new NotImplementedException();
    public override string? GetJobParameter(string id, string name) => throw new NotImplementedException();

    // Return null for an unknown id. Put a JobLoadException in LoadException instead of throwing.
    public override JobData? GetJobData(string jobId) => throw new NotImplementedException();
    public override StateData? GetStateData(string jobId) => throw new NotImplementedException();

    public override void AnnounceServer(string serverId, ServerContext context) => throw new NotImplementedException();
    public override void RemoveServer(string serverId) => throw new NotImplementedException();
    public override void Heartbeat(string serverId) => throw new NotImplementedException();
    public override int RemoveTimedOutServers(TimeSpan timeOut) => throw new NotImplementedException();

    public override HashSet<string> GetAllItemsFromSet(string key) => throw new NotImplementedException();
    public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => throw new NotImplementedException();
    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => throw new NotImplementedException();
    public override Dictionary<string, string>? GetAllEntriesFromHash(string key) => throw new NotImplementedException();

    // Extended API (JobStorageFeatures.ExtendedApi and the Connection.* features)
    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count) => throw new NotImplementedException();
    public override long GetSetCount(string key) => throw new NotImplementedException();
    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt) => throw new NotImplementedException();
    public override TimeSpan GetSetTtl(string key) => throw new NotImplementedException();
    public override string? GetValueFromHash(string key, string name) => throw new NotImplementedException();
    public override long GetHashCount(string key) => throw new NotImplementedException();
    public override TimeSpan GetHashTtl(string key) => throw new NotImplementedException();
    public override long GetListCount(string key) => throw new NotImplementedException();
    public override List<string> GetAllItemsFromList(string key) => throw new NotImplementedException();
    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt) => throw new NotImplementedException();
    public override TimeSpan GetListTtl(string key) => throw new NotImplementedException();
    public override long GetCounter(string key) => throw new NotImplementedException();
    public override DateTime GetUtcDateTime() => throw new NotImplementedException();
}
```

`GetSetCount(IEnumerable<string> keys, int limit)` and `GetSetContains(key, value)` are also virtual; override them when you report `LimitedGetSetCount` and `GetSetContains`.

### The transaction

```csharp
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

internal sealed class MyDbTransaction : JobStorageTransaction
{
    private readonly MyDbStorage _storage;
    private readonly List<Action> _commands = new();

    public MyDbTransaction(MyDbStorage storage) => _storage = storage;

    // Every method below only records a command. Nothing is visible to anyone until Commit,
    // which applies all of them in one database transaction. Dispose without Commit discards them.
    public override void ExpireJob(string jobId, TimeSpan expireIn) => _commands.Add(() => { /* set the job's ExpireAt */ });
    public override void PersistJob(string jobId) => _commands.Add(() => { /* clear the job's ExpireAt */ });
    public override void SetJobState(string jobId, IState state) => _commands.Add(() => { /* add history row, make it current */ });
    public override void AddJobState(string jobId, IState state) => _commands.Add(() => { /* add history row only */ });
    public override void AddToQueue(string queue, string jobId) => _commands.Add(() => { /* insert queue entry */ });
    public override void IncrementCounter(string key) => throw new NotImplementedException();
    public override void IncrementCounter(string key, TimeSpan expireIn) => throw new NotImplementedException();
    public override void DecrementCounter(string key) => throw new NotImplementedException();
    public override void DecrementCounter(string key, TimeSpan expireIn) => throw new NotImplementedException();
    public override void AddToSet(string key, string value) => throw new NotImplementedException();
    public override void AddToSet(string key, string value, double score) => throw new NotImplementedException();
    public override void RemoveFromSet(string key, string value) => throw new NotImplementedException();
    public override void InsertToList(string key, string value) => throw new NotImplementedException();
    public override void RemoveFromList(string key, string value) => throw new NotImplementedException();
    public override void TrimList(string key, int keepStartingFrom, int keepEndingAt) => throw new NotImplementedException();
    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => throw new NotImplementedException();
    public override void RemoveHash(string key) => throw new NotImplementedException();

    // Extended API
    public override void ExpireSet(string key, TimeSpan expireIn) => throw new NotImplementedException();
    public override void ExpireList(string key, TimeSpan expireIn) => throw new NotImplementedException();
    public override void ExpireHash(string key, TimeSpan expireIn) => throw new NotImplementedException();
    public override void PersistSet(string key) => throw new NotImplementedException();
    public override void PersistList(string key) => throw new NotImplementedException();
    public override void PersistHash(string key) => throw new NotImplementedException();
    public override void AddRangeToSet(string key, IList<string> items) => throw new NotImplementedException();
    public override void RemoveSet(string key) => throw new NotImplementedException();

    public override void Commit()
    {
        // Open a database transaction, run every recorded command, commit.
        foreach (var command in _commands)
        {
            command();
        }
    }
}
```

`AcquireDistributedLock`, `RemoveFromQueue`, `CreateJob` and `SetJobParameter` are also virtual; override each one together with its `Transaction.*` feature.

### The fetched job

```csharp
using Masterly.NightShift.Storage;

internal sealed class MyDbFetchedJob : IFetchedJob
{
    private bool _removedFromQueue;
    private bool _requeued;

    public MyDbFetchedJob(string jobId, string queue)
    {
        JobId = jobId;
        Queue = queue;
        // Start keeping the queue entry invisible (for example, refresh its FetchedAt
        // every InvisibilityTimeout / 5) until RemoveFromQueue, Requeue or Dispose.
    }

    public string JobId { get; }
    public string Queue { get; }

    // The job reached a final state: delete the queue entry.
    public void RemoveFromQueue()
    {
        _removedFromQueue = true;
    }

    // Make the queue entry visible to other workers again.
    public void Requeue()
    {
        _requeued = true;
    }

    public void Dispose()
    {
        // Stop the keep-alive. If the worker didn't decide, give the job back.
        if (!_removedFromQueue && !_requeued)
        {
            Requeue();
        }
    }
}
```

### The expiration process

```csharp
using Masterly.NightShift.Server;

internal sealed class MyDbExpirationManager : IBackgroundProcess
{
    private readonly TimeSpan _checkInterval;

    public MyDbExpirationManager(TimeSpan checkInterval) => _checkInterval = checkInterval;

    public void Execute(BackgroundProcessContext context)
    {
        // Delete jobs (with their states and parameters), sets, lists, hashes and counters
        // whose ExpireAt is in the past. Take a distributed lock so only one server does it.
        context.Wait(_checkInterval);
    }

    public override string ToString() => GetType().Name;
}
```

The server calls `Execute` in a loop until it stops. `context.Wait` throws `OperationCanceledException` as soon as the server is stopping, which ends the loop. Any other exception is logged and `Execute` is called again after a delay.

### The monitoring API

```csharp
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;

internal sealed class MyDbMonitoringApi : JobStorageMonitor
{
    private readonly MyDbStorage _storage;

    public MyDbMonitoringApi(MyDbStorage storage) => _storage = storage;

    public override IList<QueueWithTopEnqueuedJobsDto> Queues() => throw new NotImplementedException();
    public override IList<ServerDto> Servers() => throw new NotImplementedException();
    public override JobDetailsDto? JobDetails(string jobId) => throw new NotImplementedException();
    public override StatisticsDto GetStatistics() => throw new NotImplementedException();
    public override JobList<EnqueuedJobDto?> EnqueuedJobs(string queue, int from, int perPage) => throw new NotImplementedException();
    public override JobList<FetchedJobDto?> FetchedJobs(string queue, int from, int perPage) => throw new NotImplementedException();
    public override JobList<ProcessingJobDto?> ProcessingJobs(int from, int count) => throw new NotImplementedException();
    public override JobList<ScheduledJobDto?> ScheduledJobs(int from, int count) => throw new NotImplementedException();
    public override JobList<SucceededJobDto?> SucceededJobs(int from, int count) => throw new NotImplementedException();
    public override JobList<FailedJobDto?> FailedJobs(int from, int count) => throw new NotImplementedException();
    public override JobList<DeletedJobDto?> DeletedJobs(int from, int count) => throw new NotImplementedException();
    public override long ScheduledCount() => throw new NotImplementedException();
    public override long EnqueuedCount(string queue) => throw new NotImplementedException();
    public override long FetchedCount(string queue) => throw new NotImplementedException();
    public override long FailedCount() => throw new NotImplementedException();
    public override long ProcessingCount() => throw new NotImplementedException();
    public override long SucceededListCount() => throw new NotImplementedException();
    public override long DeletedListCount() => throw new NotImplementedException();
    public override IDictionary<DateTime, long> SucceededByDatesCount() => throw new NotImplementedException();
    public override IDictionary<DateTime, long> FailedByDatesCount() => throw new NotImplementedException();
    public override IDictionary<DateTime, long> HourlySucceededJobs() => throw new NotImplementedException();
    public override IDictionary<DateTime, long> HourlyFailedJobs() => throw new NotImplementedException();
}
```

## More examples

### Replacing only the queue of the SQL Server storage

If you want SQL Server for everything but the queues, implement `IPersistentJobQueueProvider` instead of a whole storage. These types are in `Masterly.NightShift.SqlServer`:

| Type / member | What to implement |
|---|---|
| `IPersistentJobQueueProvider.GetJobQueue()` | Returns your `IPersistentJobQueue`. |
| `IPersistentJobQueueProvider.GetJobQueueMonitoringApi()` | Returns your `IPersistentJobQueueMonitoringApi`. |
| `IPersistentJobQueue.Dequeue(string[] queues, CancellationToken cancellationToken)` | Same contract as `FetchNextJob` above: block until a job is available, claim it and return an `IFetchedJob`. |
| `IPersistentJobQueue.Enqueue(DbConnection connection, DbTransaction transaction, string queue, string jobId)` | Add the job to the queue. Called inside the storage's SQL transaction, with its connection and transaction, so the enqueue commits or rolls back with the job's state change. |
| `IPersistentJobQueueMonitoringApi.GetQueues()` | The names of the queues that hold jobs. |
| `GetEnqueuedJobIds(queue, from, perPage)` / `GetFetchedJobIds(queue, from, perPage)` | A page of the ids (`long`) of waiting jobs, and of jobs a worker has fetched. |
| `GetEnqueuedAndFetchedCount(queue)` | An `EnqueuedAndFetchedCountDto` with `EnqueuedCount` and `FetchedCount` (both `int?`). |

Use the provider for all queues with `SqlServerStorageOptions.DefaultQueueProvider`, or for some with `storage.QueueProviders.Add(provider, new[] { "critical" })`; `storage.QueueProviders.GetProvider(queue)` tells which one a queue uses. A server's queues must all use the same provider.

### Testing a storage

Start with the conformance suite, [`StorageConformanceFacts`](../../tests/Shared/StorageConformance/StorageConformanceFacts.cs): what every storage must do, from sets, lists and hashes to queues, locks, the monitoring API, purging and jobs run end to end by a real server. Copy it into your test project, derive a class from it and return an empty storage from `CreateStorage`; the SQL Server, Entity Framework Core, Redis, MongoDB and in-memory storages all pass it. The SQL Server storage's own tests describe more edge cases method by method: [`SqlServerConnectionFacts`](../../tests/Masterly.NightShift.SqlServer.Tests/SqlServerConnectionFacts.cs), [`SqlServerWriteOnlyTransactionFacts`](../../tests/Masterly.NightShift.SqlServer.Tests/SqlServerWriteOnlyTransactionFacts.cs), [`SqlServerMonitoringApiFacts`](../../tests/Masterly.NightShift.SqlServer.Tests/SqlServerMonitoringApiFacts.cs) and the queue and lock tests next to them. Then run a real app against your storage with several servers and kill one while jobs run: every job must finish exactly once or, after a crash, at least once.

## Things to know

- **Threading.** One storage instance serves every thread. Connections and transactions are created, used and disposed by one thread; the library never shares them.
- **Dispose is always called** on connections, transactions and fetched jobs, usually through `using`. Release database connections and locks there.
- **Ids are strings.** Use whatever your database generates, but return them as strings and accept malformed ones without throwing: reads return `null` (`GetJobData`, `GetStateData`, `GetJobParameter`), and writes for such an id (`SetJobParameter`, `ExpireJob`, `PersistJob`, `SetJobState`, `AddJobState`, `AddToQueue`) do nothing, as no job can have it.
- **Store strings as given.** Job payloads, state data and parameter values are serialized by the library; don't parse or re-encode them. That keeps data readable across [compatibility levels](../configuration/compatibility-levels.md).
- **Times are UTC.**
- **The SQL Server storage is the reference.** When in doubt about a behavior, read [`src/Masterly.NightShift.SqlServer`](../../src/Masterly.NightShift.SqlServer/). For a storage without SQL, the [in-memory storage](../../src/Masterly.NightShift.InMemory/) is the shortest complete example.

## See also

- [SQL Server setup](sql-server.md)
- [Core concepts](../getting-started/core-concepts.md)
- [Custom filters](../filters/custom-filters.md)
- [Architecture for contributors](../contributing/architecture.md)
- [Changes from Hangfire](../changes-from-hangfire.md)
