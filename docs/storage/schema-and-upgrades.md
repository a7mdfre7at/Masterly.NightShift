# Schema and upgrades

The SQL Server storage keeps its data in a fixed set of tables, created and upgraded by one versioned script, `Install.sql`. This page lists the tables, explains the schema versions, shows how to install them automatically or by hand, and covers purging data and backups.

## Quick example

By default there is nothing to do: the storage runs the install script every time it starts. To review the script, or to hand it to a DBA, write it to a file:

```csharp
using Masterly.NightShift.SqlServer;

var script = SqlServerObjectsInstaller.GetInstallScript("HangFire", enableHeavyMigrations: true);
File.WriteAllText("nightshift-install.sql", script);

Console.WriteLine($"Written the script for schema version {SqlServerObjectsInstaller.LatestSchemaVersion}.");
```

## How it works

The script is embedded in `Masterly.NightShift.SqlServer.dll` ([source](../../src/Masterly.NightShift.SqlServer/Schema/Install.sql)). When it runs, it:

1. Starts a transaction with `XACT_ABORT ON`, so any error rolls back everything.
2. Takes an exclusive application lock named `{schema}:SchemaLock`, so app instances that start at the same time install one after another.
3. Creates the schema (`[HangFire]` by default) and the `[Schema]` table if they're missing, and reads the installed version.
4. If the installed version is newer than the script's (9), stops without changes. An older package version can still run against a newer schema.
5. Applies each missing version in order, from the installed one up to 9, then records the new version.

Running it again on an up-to-date database changes nothing but the version row. The table and index names keep the `HangFire` prefix for compatibility with existing databases, whatever schema name you choose.

At startup, with `PrepareSchemaIfNecessary = true` (the default), the storage runs the script with heavy migrations off unless `EnableHeavyMigrations` is set. When that fails, with a database error or an exception from your connection factory (a token service that isn't ready yet, for example), it waits 1 second and tries again, then 3 seconds and tries a last time, which gives a database that is still starting (for example in a container started at the same time) a few seconds. Each retry really contacts the server: when opening the connection fails, the storage clears the SQL client's connection pool for that connection string, because the client otherwise fails every new open of it at once, with the same error, for 5 seconds or more (its pool blocking period). An attempt against a server that doesn't answer lasts up to the connection string's `Connect Timeout` (15 seconds by default), so the constructor can take three times that plus 4 seconds. If all three attempts fail, it logs a warning and startup continues, and the install is tried again when the storage is used: first 30 seconds later, then twice as long after each failure, up to every 30 minutes, until it succeeds. Each failure logs a warning. Then, with `TryAutoDetectSchemaDependentOptions = true`, it reads the version and enables the options that version supports, except those you set yourself (see [SQL Server options](sql-server-options.md#how-it-works)). If the version can't be read, the defaults are used until it can: the storage tries again the first time it needs the version, and then at most every 30 seconds.

## Options / API

### Tables

| Table | Holds |
|---|---|
| `[Schema]` | One row: the installed schema version. |
| `[Job]` | One row per background job: the serialized method and arguments, the creation time, the current state's id and name, and the expiration time once the job is finished. |
| `[State]` | Each job's state history: one row per transition, with state name, reason, time and serialized state data. Deleted with its job. |
| `[JobParameter]` | Name/value pairs attached to a job, such as `RetryCount`, `CurrentCulture`, `Continuations` or `Tags`. Deleted with its job. |
| `[JobQueue]` | Jobs waiting in a queue or being processed: job id, queue name and `FetchedAt`, the time a worker claimed it. |
| `[Server]` | Running servers: id, data (workers, queues, start time) and last heartbeat. |
| `[Hash]` | Key/field/value records: recurring job definitions (`recurring-job:{id}`), the drain-mode flag (`hangfire:control`), batch and insight data. |
| `[Set]` | Scored sets of strings: the schedule of delayed jobs (`schedule`), recurring job ids ordered by next run (`recurring-jobs`), `retries`, tags, batches. |
| `[List]` | Ordered lists, newest first: the activity feed (`activity`), the audit log (`audit`), job console lines, duration and latency samples. |
| `[Counter]` | Raw counter increments, such as `stats:succeeded` and the per-day and per-hour statistics. |
| `[AggregatedCounter]` | Counter totals, merged from `[Counter]` by the counters aggregator every `CountersAggregateInterval`. |

`[Hash]`, `[Set]`, `[List]`, `[Job]` and `[AggregatedCounter]` have an `ExpireAt` column; the expiration manager deletes rows whose time has passed.

### Schema versions

| Version | Changes | Heavy |
|---|---|---|
| 1 | Creates `[Job]`, `[State]`, `[JobParameter]`, `[JobQueue]`, `[Server]`, `[Hash]`, `[List]`, `[Set]`, `[Value]` and `[Counter]`. | |
| 2 | Drops `[Value]` and the first `[Hash]`; `[Server].[LastHeartbeat]` becomes required. | |
| 3 | Creates the current `[Hash]` table (`Key`, `Field`, `Value`). | |
| 4 | Creates `[AggregatedCounter]`; adds `ExpireAt` and `Key` indexes. | |
| 5 | Queue names up to 50 characters, server ids up to 200. | |
| 6 | Identity columns become `bigint`; tables are clustered on their natural keys (for example `[Hash]` on `Key`, `Field`); filtered `ExpireAt` indexes; indexes on `[Set].[Score]` and `[Server].[LastHeartbeat]`. | Yes |
| 7 | `[Set]` score index on `Key`, `Score`. | |
| 8 | `[Counter]` gets a `bigint` `Id` and a clustered key on `Key`, `Id`; `[Set]` and `[Hash]` are rebuilt with `IGNORE_DUP_KEY = ON`; `[JobQueue]` gets a `bigint` `Id` and is clustered on `Queue`, `Id`. | Yes |
| 9 | Index on `[State].[CreatedAt]`, used by `InactiveStateExpirationTimeout`. | |

What the storage does with a version: from 6 it doesn't take global locks and deletes expired rows in bigger batches; from 8 it writes sets and hashes without `MERGE`. Versions 5 and 7, where an upgrade without heavy migrations stops, work, without those improvements.

### `SqlServerObjectsInstaller`

A static class in `Masterly.NightShift.SqlServer`.

| Member | Meaning |
|---|---|
| `LatestSchemaVersion` | `9`, the version the script installs. |
| `GetInstallScript(string schema, bool enableHeavyMigrations)` | Returns the script with the schema name filled in (`HangFire` when `schema` is `null` or blank). With `enableHeavyMigrations: false`, the script stops before versions 6 and 8 on an existing database. A schema name with `]`, `'` or a control character, or longer than 128 characters, throws `ArgumentException`: the script uses the name inside both `[...]` and `'...'`, and no single escaping fits both. The `Install` methods check the name the same way. |
| `Install(DbConnection connection, string schema, bool enableHeavyMigrations)` | Runs the script on the connection, with no command timeout. |
| `Install(DbConnection connection, string schema)` | The same with heavy migrations off. |
| `Install(DbConnection connection)` | The same with the default schema and heavy migrations off. |
| `RequiredSchemaVersion` | Obsolete and unused. |

The related `SqlServerStorageOptions`: `PrepareSchemaIfNecessary` (run the script at startup, default `true`), `EnableHeavyMigrations` (default `false`), `SchemaName` (default `"HangFire"`), `TryAutoDetectSchemaDependentOptions` (default `true`).

## More examples

### Manual install

Turn off the startup install when the app's database account can't change the schema, or when schema changes go through your deployment pipeline:

```csharp
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        PrepareSchemaIfNecessary = false,
    }));
```

Then install with a privileged account, in one of these ways:

- **Generate the script** with `SqlServerObjectsInstaller.GetInstallScript(schema, enableHeavyMigrations: true)`, as in the quick example, and run it with any tool. Generate it from the package version you deploy.
- **Use the script from the repository.** [`DefaultInstall.sql`](../../src/Masterly.NightShift.SqlServer/Schema/DefaultInstall.sql) is the script for the `HangFire` schema with heavy migrations on. [`Install.sql`](../../src/Masterly.NightShift.SqlServer/Schema/Install.sql) has a `$(HangFireSchema)` placeholder, which `sqlcmd -v HangFireSchema=MySchema` (or SSMS in SQLCMD mode) fills in. Neither file is in the NuGet package.
- **Run it from a deployment tool** written in .NET:

```csharp
using Masterly.NightShift.SqlServer;
using Microsoft.Data.SqlClient;

using (var connection = new SqlConnection(adminConnectionString))
{
    connection.Open();
    SqlServerObjectsInstaller.Install(connection, "HangFire", enableHeavyMigrations: true);
}
```

Install before you deploy the app version that needs it. To see what a database has:

```sql
SELECT [Version] FROM [HangFire].[Schema];
```

The dashboard home page also shows the schema version, highlighted when it's older than the package's.

### Heavy migrations

The upgrades to versions 6 and 8 change primary keys and column types and rebuild `[Job]`, `[State]`, `[JobParameter]`, `[JobQueue]`, `[Counter]`, `[Set]` and `[Hash]`. On a large database they can take a long time, block job processing while they run, and grow the transaction log. That's why the startup install skips them unless you opt in. A database without NightShift tables always gets every version, because there is nothing to rebuild.

To upgrade a database that stopped at version 5 or 7:

1. Pick a quiet time and back up the database.
2. Make the tables small: let the expiration manager catch up, or [purge](#purging-data) what you don't need.
3. Stop the servers, or pause processing from the dashboard (see [Drain mode](../server/drain-mode.md)).
4. Run the script with heavy migrations on: generate it with `enableHeavyMigrations: true`, call `SqlServerObjectsInstaller.Install(connection, schema, true)`, or start one instance with `EnableHeavyMigrations = true`:

```csharp
configuration.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
{
    EnableHeavyMigrations = true,
});
```

5. Start the servers. With auto-detection on, they pick up the new version's options at startup, unless you set `DisableGlobalLocks` or `UseIgnoreDupKeyOption` yourself.

### Coming from Hangfire

A database created by Hangfire.SqlServer works as is: the schema name (`HangFire`), the tables and the versioned script are the same, and an older version is upgraded like any other. Stored job type names from Hangfire are mapped to the new types when jobs are loaded. See [Migrating from Hangfire](../migrating-from-hangfire.md).

### How long data is kept

Finished jobs (succeeded and deleted) expire after `JobStorage.JobExpirationTimeout`, one day by default and at most 36,500 days (`ExpireAfterAttribute.MaxDays`); a longer value throws `ArgumentOutOfRangeException`. Change the timeout for all jobs at configuration time, or per job with `[ExpireAfter]` (see [Timing filters](../filters/timing.md)):

```csharp
configuration
    .UseSqlServerStorage(connectionString)
    .WithJobExpirationTimeout(TimeSpan.FromDays(7));
```

By default, failed jobs don't expire until they're retried or deleted. To delete them after a while, use `UseFailedJobRetention` for every job or `[ExpireAfter(..., IncludeFailed = true)]` per method; see [Failed jobs](../filters/timing.md#failed-jobs).

Expired rows are deleted by the expiration manager every `JobExpirationCheckInterval` (30 minutes). For jobs with long state histories, `InactiveStateExpirationTimeout` also deletes old state rows. See [SQL Server options](sql-server-options.md#cleaning-up-old-data).

### Purging data

`JobStorage.Purge(StoragePurgeOptions)` deletes job data in one call. The SQL Server storage supports it and reports `JobStorageFeatures.Purge`; other storages may not, and throw `NotSupportedException`.

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Storage;

public static void ClearJobs(JobStorage storage)
{
    if (!storage.HasFeature(JobStorageFeatures.Purge))
    {
        throw new NotSupportedException($"{storage} can't purge its data.");
    }

    // Removes jobs, queues, counters, lists, sets and hashes; keeps recurring jobs,
    // servers, the audit log and drain mode
    storage.Purge(new StoragePurgeOptions());
}
```

To wipe everything, the audit log included:

```csharp
storage.Purge(new StoragePurgeOptions
{
    KeepRecurringJobs = false,
    KeepServers = false,
    KeepAuditLog = false,
    KeepDrainMode = false,
});
```

| `StoragePurgeOptions` property | Type | Default | Meaning |
|---|---|---|---|
| `KeepRecurringJobs` | `bool` | `true` | Keep recurring job definitions (the `recurring-jobs` set and the `recurring-job:{id}` hashes), so the app doesn't have to register them again. |
| `KeepServers` | `bool` | `true` | Keep the `[Server]` rows. Running servers announce themselves again anyway; keeping them avoids a gap on the Servers page. |
| `KeepAuditLog` | `bool` | `true` | Keep the audit log (the `audit` list), so the record of who did what in the dashboard survives the purge. |
| `KeepDrainMode` | `bool` | `true` | Keep the processing switches: drain mode, paused queues and paused servers (the `hangfire:control` hash). Processing that was paused before the purge stays paused after it. |

Everything else is deleted: all jobs with their states, parameters and queue entries, counters and statistics, the other lists (activity feed, job consoles, insights), and the other sets and hashes (the schedule, retries, tags, batches, insights, circuit breakers).

The dashboard offers two actions on the Servers page, for users who aren't read-only. Neither deletes the audit log, so the entry that records the action joins the history instead of replacing it:

| Action | Deletes | Keeps |
|---|---|---|
| **Clear all jobs** | Jobs and everything that describes them: states, parameters, queues, the schedule, retries, counters, statistics, the activity feed, consoles, tags, batches, insights. | Recurring job definitions, servers, the audit log, drain mode and paused queues and servers. |
| **Reset storage** | Everything above, plus recurring job definitions, servers, drain mode and paused queues and servers. Processing that was paused resumes. | The audit log. |

See [Dashboard pages](../dashboard/pages.md).

How the SQL Server storage purges:

- **In batches.** Each table is emptied with `DELETE TOP (n)` statements, `n` being `DeleteExpiredBatchSize` (1000, or 10000 on schema 6 or later; see [SQL Server options](sql-server-options.md)). Child tables go first: `[JobParameter]`, `[State]`, `[JobQueue]`, then `[Job]`, the counters, `[List]`, `[Set]`, `[Hash]` and, when servers aren't kept, `[Server]`.
- **Each batch is its own short transaction.** Each statement is bound by `CommandTimeout`, so a purge of any size finishes instead of timing out as a whole, and other sessions wait for one batch at most, not for the whole purge. The transaction log only has to hold one batch at a time.
- **It isn't all or nothing.** If a purge fails or is interrupted (a timeout, a deadlock, a lost connection, the app stopping), the rows deleted so far stay deleted. Run it again to finish: it continues with what is left.
- **The dashboard waits for it.** The action returns when the purge is done. On very large tables that can take minutes; if a proxy cuts the request, the purge still runs to the end on the server.

Things to know about purging:

- Stop processing first, or pause it with drain mode, which Clear all jobs keeps on and Reset storage turns off. Jobs that are running lose their rows, so their results aren't recorded.
- It can't be undone. Back up first if you might need the data.

### Backups

NightShift's tables are ordinary tables in your database, so your normal backups include them. Keep in mind:

- **A restore brings jobs back as they were.** Jobs that ran after the backup was taken can run again, and jobs that were processing at backup time are fetched again once their `FetchedAt` is older than `SlidingInvisibilityTimeout`. Write jobs so that running twice is harmless.
- **Recurring jobs catch up.** Runs that fell due between the backup and the restore are handled by each recurring job's misfire setting (see [Time zones and misfires](../recurring-jobs/time-zones-and-misfires.md)).
- **Old server rows disappear by themselves.** Servers in the backup that no longer run are removed by the server watchdog after the server timeout.
- **Job data changes constantly.** With the full recovery model, back up the log often enough that it doesn't grow without bound. If losing the last minutes of jobs is acceptable, a separate job database with the simple recovery model is an option.
- **Restore job data and app data together.** Queued jobs refer to your app's data by id. Restoring one without the other can make jobs act on rows that are gone or have changed.

## Things to know

- **Downgrades.** Run against a schema newer than its own, the script of an older package stops without changes, and the storage uses the schema it finds.
- **Several apps, one database.** Every storage instance runs the script for its own schema. With different `SchemaName`s they don't affect each other.
- **Changing `SchemaName` doesn't move data.** The storage installs an empty set of tables under the new name.
- **Don't hand-edit the tables.** Changing or deleting rows directly can leave jobs half in one state. Use the dashboard, `IBackgroundJobClient` or `Purge`.

## See also

- [SQL Server setup](sql-server.md)
- [SQL Server options](sql-server-options.md)
- [Managing jobs](../jobs/managing-jobs.md)
- [Drain mode](../server/drain-mode.md)
- [Migrating from Hangfire](../migrating-from-hangfire.md)
- [Troubleshooting](../troubleshooting.md)
