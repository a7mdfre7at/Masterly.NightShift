# SQL Server options

`SqlServerStorageOptions` controls how the SQL Server storage fetches jobs, polls queues, batches commands, takes locks, cleans up old data and manages its schema. The defaults suit most applications; change an option when you have a specific reason from the sections below.

## Quick example

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        SchemaName = "NightShift",
        QueuePollInterval = TimeSpan.FromMilliseconds(100),
        InactiveStateExpirationTimeout = TimeSpan.FromDays(7),
    }));
```

Pass the options to `UseSqlServerStorage` or to a `SqlServerStorage` constructor. They are read when the storage is created and while it runs, so don't change them afterwards.

## How it works

Options fall into a few groups:

- **Fetching and polling** decide how a worker takes a job from `[JobQueue]` and how often idle workers ask for more.
- **Commands and transactions** decide timeouts, batching and isolation of the SQL sent to the server.
- **Locks** decide which application locks guard writes to the `[Set]`, `[Hash]` and `[List]` tables.
- **Cleanup** decides how the expiration manager and counters aggregator, the two background processes every (non-lightweight) server runs for the storage, do their work.
- **Schema** decides whether the install script runs at startup and how the storage adapts to the schema version it finds.

When the storage is created, and `TryAutoDetectSchemaDependentOptions` is `true` (the default), it reads the schema version and decides the schema-dependent options **you didn't set**: `DisableGlobalLocks` is on for version 6 or later, `UseIgnoreDupKeyOption` is on for version 8 or later, and a `DeleteExpiredBatchSize` left at -1 means 10000 rows on version 6 or later. A value you set yourself is always kept, whatever the version. The storage keeps the detected values to itself: the options object you passed isn't changed, so its properties still show your values or the defaults, and one object can be shared by storages on different schema versions. If the database can't be reached at that moment, an error is logged and the defaults are used until the version can be read: the storage tries again the first time it needs one of these options, and then at most every 30 seconds, until that works.

## Options / API

All properties are on `Masterly.NightShift.SqlServer.SqlServerStorageOptions`.

| Option | Type | Default | Meaning |
|---|---|---|---|
| `SlidingInvisibilityTimeout` | `TimeSpan?` | 5 minutes | How a worker claims a job. With a value, the queue row is marked as fetched and kept alive while the job runs; if the server disappears, the job is fetched again once the mark is older than this. `null` switches to the legacy transactional fetch. SQL Server compares it in whole seconds, so it must be at least 1 second; a shorter value throws `ArgumentOutOfRangeException` (it would round down to 0 and let a second worker fetch a running job at once). See [Fetching](#fetching-sliding-invisibility-or-transactional). |
| `QueuePollInterval` | `TimeSpan` | `Zero` | How long the polling worker waits before asking for a job again. Values below 1 second are clamped to 100 ms to 1 s, and `Zero` means 200 ms. With sliding invisibility, only one idle worker per process and set of queues polls at a time; the others wait for it. Negative values throw `ArgumentException`. See [Polling](#polling). |
| `DisableFetchSemaphores` | `bool` | `false` | `true` lets every idle worker poll on its own instead of one per process and set of queues. More queries, faster pickup of bursts. |
| `CommandTimeout` | `TimeSpan?` | `null` | Timeout of each SQL command, including each batch of `Purge`. `null` uses the client's default (30 seconds). Doesn't apply to the install script, expiration and aggregation, which run without a timeout. Commands take whole seconds, so a value must be at least 1 second; a shorter one throws `ArgumentOutOfRangeException` (it would round down to 0, which means no timeout at all). |
| `CommandBatchMaxTimeout` | `TimeSpan?` | 5 minutes | When set, the commands of one storage transaction are sent to the server as a single batch, with a timeout of `CommandTimeout` (or 30 s) times the number of commands, capped at this value. `null` sends them one by one. At least 1 second, like `CommandTimeout`. |
| `UseRecommendedIsolationLevel` | `bool` | `true` | Run storage transactions as `READ COMMITTED`. When `false`, `TransactionIsolationLevel` is used instead. |
| `DisableGlobalLocks` | `bool` | `false` (`true` on schema 6+ when auto-detected and not set) | When `false`, every transaction that writes to `[Set]`, `[Hash]` or `[List]` takes an exclusive application lock on that whole table type, which serializes those writes across all servers. Needed only on schema versions before 6. Setting it to `true` on such a version logs a warning at startup when auto-detection is on. |
| `UseFineGrainedLocks` | `bool` | `false` | With `DisableGlobalLocks = true`, take an application lock per key (for example one per set) instead of none. Writers of different keys don't wait for each other. |
| `UseIgnoreDupKeyOption` | `bool` | `false` (`true` on schema 8+ when auto-detected and not set) | Write `[Set]` and `[Hash]` rows with `INSERT` and a fallback `UPDATE` instead of `MERGE`, which avoids range-lock deadlocks. Requires the `IGNORE_DUP_KEY` option that schema 8 sets on those tables. When auto-detection finds an older schema, the option stays off even if you set it, and a warning is logged at startup; with auto-detection off, don't turn it on for an older schema. |
| `UseTransactionalAcknowledge` | `bool` | `false` | Experimental. Remove the finished job's queue row in the same transaction as its final state change, instead of in a separate command afterwards. Fewer round trips and better behavior when asynchronous replication loses the latest writes, at the cost of more blocking on `[JobQueue]`. Only with sliding invisibility. |
| `JobExpirationCheckInterval` | `TimeSpan` | 30 minutes | How often the expiration manager deletes expired jobs, lists, sets, hashes and aggregated counters. Must be greater than zero (checked when a server starts) and at most `int.MaxValue` milliseconds. |
| `DeleteExpiredBatchSize` | `int` | `-1` | Rows the expiration manager and `Purge` delete per statement. -1 means 1000, or 10000 when auto-detection finds schema 6 or later. Other values outside 1 to 100000 mean 1000. |
| `InactiveStateExpirationTimeout` | `TimeSpan` | `Zero` (off) | When greater than zero, the expiration manager also deletes rows of a job's state history that are older than this and aren't the job's current state. Needs schema 9, which adds the `[State].[CreatedAt]` index the cleanup uses. When auto-detection finds an older schema, the state history isn't cleaned up and a warning is logged once; with auto-detection off, the cleanup is tried and logs an error at every check on such a schema. Negative values throw when a server starts. |
| `CountersAggregateInterval` | `TimeSpan` | 5 minutes | How often the counters aggregator merges `[Counter]` rows into `[AggregatedCounter]`. Zero or negative values throw `ArgumentException`, and values above `int.MaxValue` milliseconds throw `ArgumentOutOfRangeException`. |
| `DashboardJobListLimit` | `int?` | 10000 | Upper limit of the job counts used to page the Scheduled, Processing, Failed, Awaiting, Succeeded and Deleted lists on the dashboard. Counting stops at this number, which keeps the query cheap on large tables. `null` counts every job. |
| `SchemaName` | `string` | `"HangFire"` | The database schema that holds the tables. Each schema is a separate storage. The install script uses the name inside both `[...]` and `N'...'`, so `null`, empty or white-space values, names longer than 128 characters, and names containing `]`, `'` or a control character throw `ArgumentException`. Spaces, dashes, dots and other characters, including letters of any alphabet, are fine, whatever the database's collation. |
| `PrepareSchemaIfNecessary` | `bool` | `true` | Run the install script when the storage is created, which creates or upgrades the tables. After a failure (a database error, or an exception from your connection factory) it tries twice more, 1 and 3 seconds apart, clearing the SQL client's connection pool after a failed open so that each attempt contacts the server, then logs a warning and goes on; it tries again when the storage is used, 30 seconds later at first and less often after each failure (up to every 30 minutes). Turn it off when the account the app uses can't change the schema, or when you deploy schema changes yourself. |
| `EnableHeavyMigrations` | `bool` | `false` | Let the startup install apply the upgrades from schema 5 to 6 and from 7 to 8, which rebuild large tables. Without it, an upgrade stops at version 5 or 7. New installs always go to the latest version. See [Heavy migrations](schema-and-upgrades.md#heavy-migrations). |
| `TryAutoDetectSchemaDependentOptions` | `bool` | `true` | Read the schema version at startup and decide the dependent options you didn't set, as described in [How it works](#how-it-works). |
| `SqlClientFactory` | `DbProviderFactory` | `Microsoft.Data.SqlClient` if referenced, else `System.Data.SqlClient` | Creates connections when the storage was given a connection string. Reading it throws `InvalidOperationException` when neither client is available, and so does the storage's connection string constructor; setting it to `null` throws `ArgumentNullException`. See [Choosing the SQL client](sql-server.md#choosing-the-sql-client). |
| `ImpersonationFunc` | `Func<IDisposable>` | `null` | Called before each connection is opened; the result is disposed right after. For Windows impersonation on .NET Framework, where opening a connection under another identity needs an impersonation context. |
| `DefaultQueueProvider` | `IPersistentJobQueueProvider` | `null` (the built-in SQL Server queue) | A replacement queue implementation for every queue that has no provider of its own in `SqlServerStorage.QueueProviders`. |

### Obsolete options

These still compile, with an obsolete warning. Most do nothing.

| Option | Type | Default | Status |
|---|---|---|---|
| `TransactionIsolationLevel` | `IsolationLevel?` | `null` | Used only when `UseRecommendedIsolationLevel` is `false`. `null` means `READ COMMITTED`. Prefer `UseRecommendedIsolationLevel`. |
| `InvisibilityTimeout` | `TimeSpan` | 30 minutes | Used only by the legacy transactional fetch (`SlidingInvisibilityTimeout = null`), to also take queue rows that were marked as fetched longer ago than this (for example by a server that used sliding invisibility before). No effect with sliding invisibility. |
| `TransactionTimeout` | `TimeSpan` | 1 minute | No effect: explicit transactions are always used. |
| `DisableTransactionScope` | `bool` | `false` | No effect, for the same reason. |
| `UsePageLocksOnDequeue` | `bool` | `false` | No effect. |

## More examples

### Fetching: sliding invisibility or transactional

**Sliding invisibility** (the default) sets `FetchedAt` on the queue row and commits right away. While the job runs, the server's heartbeat process refreshes `FetchedAt` every `SlidingInvisibilityTimeout / 5`. When the job finishes, the row is deleted. When the server is stopped gracefully, an unfinished job's row is released at once; when the server crashes, the row becomes visible again after `SlidingInvisibilityTimeout`, and another worker runs the job again. Long jobs are fine: as long as the server is alive, the row stays claimed.

Lower the timeout to have jobs from crashed servers retried sooner, at the price of more frequent keep-alive updates. Keep it well above the longest pause your servers might see (garbage collection, a VM being suspended), or a live job can be fetched by a second worker.

**Transactional fetch** (`SlidingInvisibilityTimeout = null`) deletes the queue row inside a transaction that stays open, on its own connection, until the job finishes. If the server crashes, SQL Server rolls back the transaction and the job is back in the queue immediately. The cost is one open connection and one long transaction per busy worker, which holds locks and keeps the transaction log from being truncated. Use it only if you need that immediate return.

```csharp
var options = new SqlServerStorageOptions
{
    SlidingInvisibilityTimeout = null,
    QueuePollInterval = TimeSpan.FromSeconds(1),
};
```

In transactional mode every idle worker polls on its own, and `QueuePollInterval = Zero` means 1 second.

### Polling

When a worker finds no job, it waits and asks again. A job enqueued in the same process (including a scheduled job that this process moves to its queue) wakes the waiting workers immediately, so polling matters for jobs enqueued by other processes. A worker can wait for at most 63 queues this way (the limit of `WaitHandle.WaitAny`, less one for shutdown): when a server listens to more, jobs in the first 63 of its queues, in the order they are configured, wake it immediately, and jobs in the others are found on the next poll.

With sliding invisibility, the idle workers of a process that listen to the same queues take turns: one of them polls, the others wait until it has found a job, and then the next one starts polling. So a process sends one query per poll interval for each set of queues, however many workers it has.

- **`Zero` (default) or below 1 second:** the delay between polls is the value you set, clamped to 100 ms to 1 s; `Zero` means 200 ms. Jobs from other processes start within a fraction of a second.
- **1 second or more:** the delay is the value you set. Use it to lower the query load on a database shared with other work when a few seconds of delay don't matter.

```csharp
// Pick up jobs from other processes within about 100 ms
var lowLatency = new SqlServerStorageOptions
{
    QueuePollInterval = TimeSpan.FromMilliseconds(100),
};

// A quiet background system on a busy shared database
var quiet = new SqlServerStorageOptions
{
    QueuePollInterval = TimeSpan.FromSeconds(15),
};
```

For bursty load where many workers should start at once, add `DisableFetchSemaphores = true`. Every idle worker then polls on its own, which costs more queries while queues are empty.

### Cleaning up old data

Finished jobs (succeeded or deleted) get an expiration time, by default one day after they finish. The expiration manager deletes expired rows every `JobExpirationCheckInterval`, in batches of `DeleteExpiredBatchSize`. The job's state history and parameters go with it.

```csharp
var options = new SqlServerStorageOptions
{
    JobExpirationCheckInterval = TimeSpan.FromMinutes(5),
    DeleteExpiredBatchSize = 20000,
    InactiveStateExpirationTimeout = TimeSpan.FromDays(7),
};
```

Check more often and delete bigger batches when the tables grow faster than they are cleaned, which happens with very high job throughput. `InactiveStateExpirationTimeout` helps when jobs live a long time and collect many states, for example recurring retries or jobs awaiting a parent for weeks: older history rows are deleted while the job and its current state stay.

How long finished jobs are kept is a `JobStorage` setting, not an option of this class. It can be at most 36,500 days (`ExpireAfterAttribute.MaxDays`). The storage also keeps any expiration it is asked to write within what its `datetime` columns hold: one that would fall after December 31, 9999 is stored as that day, so the data never expires instead of failing the transaction.

```csharp
configuration
    .UseSqlServerStorage(connectionString)
    .WithJobExpirationTimeout(TimeSpan.FromDays(7));
```

### Command timeouts and batching

```csharp
var options = new SqlServerStorageOptions
{
    CommandTimeout = TimeSpan.FromSeconds(60),
    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
};
```

Raise `CommandTimeout` when the dashboard or monitoring queries time out on very large tables. Batching needs the SQL client's internal `SqlCommandSet` type; when it isn't available (for example in `System.Data.SqlClient` older than 4.7 on .NET Core), commands are sent one by one automatically.

### Schema managed by your deployment

```csharp
var options = new SqlServerStorageOptions
{
    PrepareSchemaIfNecessary = false,
    TryAutoDetectSchemaDependentOptions = true,
};
```

The app no longer runs the install script, but still reads the schema version to pick the right options. Deploy the script before you deploy a version of this package that needs a newer schema. See [Manual install](schema-and-upgrades.md#manual-install).

### Options from configuration

`SqlServerStorageOptions` is a plain class with settable properties, so the configuration binder fills it from `appsettings.json`. `TimeSpan` values are written as `"hh:mm:ss"` (or `"d.hh:mm:ss"`), and `null` clears a nullable option:

```json
{
  "NightShift": {
    "SqlServer": {
      "SchemaName": "NightShift",
      "QueuePollInterval": "00:00:00.100",
      "CommandTimeout": "00:01:00",
      "InactiveStateExpirationTimeout": "7.00:00:00",
      "DeleteExpiredBatchSize": 20000
    }
  }
}
```

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

var options = new SqlServerStorageOptions();
builder.Configuration.GetSection("NightShift:SqlServer").Bind(options);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString, options));
```

`Bind` comes from the `Microsoft.Extensions.Configuration.Binder` package, which ASP.NET Core and the generic host already reference. Options missing from the section keep their defaults, and the properties that hold objects (`SqlClientFactory`, `DefaultQueueProvider`, `ImpersonationFunc`) are left alone; set those in code. A value the options reject, such as a `SlidingInvisibilityTimeout` under 1 second, makes `Bind` throw, with the option's exception as the inner exception.

To read only a few values, use `GetValue`:

```csharp
var section = builder.Configuration.GetSection("NightShift:SqlServer");

var options = new SqlServerStorageOptions
{
    SchemaName = section.GetValue("SchemaName", "HangFire")!,
    QueuePollInterval = section.GetValue("QueuePollInterval", TimeSpan.Zero),
    CommandTimeout = section.GetValue<TimeSpan?>("CommandTimeout"),
};
```

## Things to know

- **Your values win over auto-detection.** Setting `DisableGlobalLocks` or `UseIgnoreDupKeyOption` turns auto-detection off for that option, even when you set the default (`false`). Leave an option unset to let the schema version decide it.
- **The options object shows what you set, not what is in effect.** After the storage is created, `DisableGlobalLocks`, `UseIgnoreDupKeyOption` and `DeleteExpiredBatchSize` still return your values or the defaults, not the values auto-detection chose.
- **Lightweight servers** (`BackgroundJobServerOptions.IsLightweightServer`) don't run the expiration manager and the counters aggregator, so the cleanup options have no effect on them. Run at least one regular server per storage.

## See also

- [SQL Server setup](sql-server.md)
- [Schema and upgrades](schema-and-upgrades.md)
- [Server options](../server/server-options.md)
- [Queues and workers](../server/queues-and-workers.md)
- [Global configuration](../configuration/global-configuration.md)
