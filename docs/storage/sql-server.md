# SQL Server storage

`Masterly.NightShift.SqlServer` stores jobs, queues, recurring jobs and statistics in tables of a SQL Server database (SQL Server or Azure SQL). Use it when your app already has a SQL Server database or you want job data in one you can back up and query like any other.

## Quick example

Install the packages. The storage works with both SQL Server client libraries but references neither, so add the one you want:

```
dotnet add package Masterly.NightShift
dotnet add package Microsoft.Data.SqlClient
```

If the build warns about `System.Security.Cryptography.Pkcs` (NU1902, NU1903), see [The SQL Server driver](../getting-started/installation.md#the-sql-server-driver).

Add a connection string to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "NightShift": "Server=.\\sqlexpress;Database=MyApp;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

Register the storage, a server and the dashboard in `Program.cs`:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")));
builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapNightShiftDashboard();

app.Run();
```

On the first start the storage creates its tables in the `[HangFire]` schema of the `MyApp` database. The database itself must already exist.

## How it works

- **Startup.** The `SqlServerStorage` object is created when the configuration callback runs (with `AddNightShift`, when the host starts its NightShift server or the first NightShift service is resolved). Its constructor runs the install script (unless `PrepareSchemaIfNecessary` is `false`), then reads the schema version and turns on the options that version supports, except those you set yourself (unless `TryAutoDetectSchemaDependentOptions` is `false`). When the database can't be reached yet, the install is tried three times, 1 and 3 seconds apart, and then again when the storage is used (30 seconds later at first, less often after each failure); the version is read again the first time the storage needs it. Between the startup attempts the storage clears the SQL client's connection pool, so that each one really contacts the server. See [Schema and upgrades](schema-and-upgrades.md).
- **Jobs and queues.** Creating a job inserts a row into `[Job]` and its parameters into `[JobParameter]`. Enqueuing it adds a row to `[JobQueue]`. Each state change adds a row to `[State]` and updates the job's current state.
- **Fetching.** Each worker runs an `UPDATE TOP (1) ... WITH (READPAST)` that marks one queue row as fetched by setting `FetchedAt`. While the job runs, the server refreshes `FetchedAt` every fifth of `SlidingInvisibilityTimeout` (once a minute with the default 5 minutes). If the server dies, the row is picked up by another worker once `FetchedAt` is older than the timeout. When the job finishes, the queue row is deleted. Workers in the process that enqueued a job are woken up at once; others find it on their next poll (see `QueuePollInterval` in [SQL Server options](sql-server-options.md)).
- **Everything else** (recurring jobs, the schedule, retries, statistics, tags, batches, audit log) is stored in the generic `[Hash]`, `[Set]`, `[List]`, `[Counter]` and `[AggregatedCounter]` tables.
- **Distributed locks** are SQL Server application locks (`sp_getapplock`), held by a dedicated connection for as long as the lock is held.
- **Maintenance.** Every server (except a lightweight one, see [Server options](../server/server-options.md)) runs two storage processes: the expiration manager, which deletes expired jobs, lists, sets, hashes and aggregated counters every 30 minutes under a distributed lock so only one server does the work at a time, and the counters aggregator, which merges `[Counter]` rows into `[AggregatedCounter]` every 5 minutes.

There is no broker or extra service: every client and server talks to the database directly.

## Options / API

### `UseSqlServerStorage` overloads

All four are extension methods on `IGlobalConfiguration` in the `Masterly.NightShift` namespace. They create a `SqlServerStorage`, make it the current storage, add the SQL Server metrics to the dashboard, and return `IGlobalConfiguration<SqlServerStorage>`, so you can chain `.WithJobExpirationTimeout(...)`.

| Overload | Use it when |
|---|---|
| `UseSqlServerStorage(string nameOrConnectionString)` | You have a connection string and the default options suit you. |
| `UseSqlServerStorage(string nameOrConnectionString, SqlServerStorageOptions options)` | You have a connection string and want to change options. |
| `UseSqlServerStorage(Func<DbConnection> connectionFactory)` | You need to build each connection yourself, for example to set an access token. |
| `UseSqlServerStorage(Func<DbConnection> connectionFactory, SqlServerStorageOptions options)` | The same, with options. |

`nameOrConnectionString` is a connection string or the name of one. Every connection string has `key=value` pairs, so a text without `=` is taken for a name and looked up in the `connectionStrings` section of `app.config`/`web.config`. That works on .NET Framework, and on .NET when the app has the `System.Configuration.ConfigurationManager` package (and a configuration file). When no connection string with that name is found, the constructor throws `ArgumentException` at once. On .NET, read the connection string with `IConfiguration.GetConnectionString` and pass it. Passing `null` throws `ArgumentNullException`, so a missing `GetConnectionString("NightShift")` fails at startup.

```xml
<connectionStrings>
  <add name="NightShift" connectionString="Server=.\sqlexpress;Database=NightShift;Integrated Security=True;" />
</connectionStrings>
```

```csharp
GlobalConfiguration.Configuration.UseSqlServerStorage("NightShift");
```

### `SqlServerStorage` constructors

Use these when you need the storage object itself, for example for a second storage, without making it the current one.

| Constructor | Notes |
|---|---|
| `SqlServerStorage(string nameOrConnectionString)` | Default options. |
| `SqlServerStorage(string nameOrConnectionString, SqlServerStorageOptions options)` | |
| `SqlServerStorage(Func<DbConnection> connectionFactory)` | Default options. |
| `SqlServerStorage(Func<DbConnection> connectionFactory, SqlServerStorageOptions options)` | |
| `SqlServerStorage(DbConnection existingConnection)` | Every operation uses this one connection, which the storage never disposes. For tests and single-threaded tools only: a server runs many threads at once. |
| `SqlServerStorage(DbConnection existingConnection, SqlServerStorageOptions options)` | The same, with options. |

With the default options every constructor connects to the database (to install the schema and detect its version), so construct the storage once and reuse it.

### `SqlServerStorage` members

| Member | Type | Meaning |
|---|---|---|
| `QueueProviders` | `PersistentJobQueueProviderCollection` | The queue implementation per queue name. `Add(provider, queues)` assigns a custom `IPersistentJobQueueProvider` to some queues; the rest use `SqlServerStorageOptions.DefaultQueueProvider` or the built-in SQL Server queue. A server's queues must all use the same provider. |
| `JobExpirationTimeout` | `TimeSpan` | Inherited from `JobStorage`. How long finished jobs are kept, 1 day by default. Set it with `.WithJobExpirationTimeout(...)`. |
| `Purge(StoragePurgeOptions)` | method | Deletes job data in batches, keeping what the options say. See [Schema and upgrades](schema-and-upgrades.md#purging-data). |
| `HasFeature(string)` | method | Reports which optional storage features are supported. See [Custom storage](custom-storage.md#feature-flags). |
| `ToString()` | method | `SQL Server: server@database`, as shown in logs and on the dashboard. The password is never included. |
| `SchemaVersion`, `ActiveConnections`, `TotalConnections`, `ActiveTransactions`, `DataFilesSize`, `LogFilesSize` | `DashboardMetric` (static) | The metrics `UseSqlServerStorage` adds to the dashboard home page. See [Dashboard metrics](#dashboard-metrics). |
| `PerformanceCounterDatabaseMetric` | `Func<string, string, string, DashboardMetric>` (static) | Builds a metric from `sys.dm_os_performance_counters` (object name, counter name, instance name or `null` for the current database). |

## Choosing the SQL client

The storage creates connections through `SqlServerStorageOptions.SqlClientFactory`. When you don't set it, it takes the first of these it can load:

1. `Microsoft.Data.SqlClient` (the `Microsoft.Data.SqlClient` package)
2. `System.Data.SqlClient` from the .NET Framework's `System.Data` assembly (always present on .NET Framework)
3. `System.Data.SqlClient` (the `System.Data.SqlClient` package)

So on .NET 8 or 10 you must reference one of the two packages; on .NET Framework it works without either, and uses `Microsoft.Data.SqlClient` if you add it. With neither available, the storage throws `InvalidOperationException` ("Please add a NuGet package reference to either 'Microsoft.Data.SqlClient' or 'System.Data.SqlClient'...") when it first connects, which is at startup.

Prefer `Microsoft.Data.SqlClient`: `System.Data.SqlClient` is no longer developed. To choose explicitly, for example when both packages are referenced and you want the old one:

```csharp
using Masterly.NightShift.SqlServer;

var options = new SqlServerStorageOptions
{
    SqlClientFactory = System.Data.SqlClient.SqlClientFactory.Instance,
};
```

`Microsoft.Data.SqlClient` encrypts connections by default and checks the server certificate, so a local SQL Server with a self-signed certificate needs `TrustServerCertificate=True` (or `Encrypt=False`) in the connection string.

The connection factory overloads bypass `SqlClientFactory`: the connections your factory returns are used as they are.

## Connection string or connection factory

With a connection string, the storage asks the client factory for a new connection each time, sets the connection string, opens it, and disposes it after use. Connection pooling is done by the SQL client as usual, so `Max Pool Size` and similar keywords apply. Most operations use a connection only for the duration of a query. Connections stay open longer for each held distributed lock and, with the transactional fetch, for each worker running a job, so raise `Max Pool Size` (100 by default) if you run more workers than that in one process.

Use a factory when a connection needs more than a connection string. The factory is called for every connection. Return a new connection each time (opened or not; the storage opens it if it's closed). The storage disposes it.

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;
using Microsoft.Data.SqlClient;

builder.Services.AddNightShift((provider, config) =>
{
    var tokens = provider.GetRequiredService<ISqlTokenProvider>();

    config.UseSqlServerStorage(
        () => new SqlConnection(connectionString) { AccessToken = tokens.GetAccessToken() },
        new SqlServerStorageOptions());
});
```

`ISqlTokenProvider` stands for your own service that returns a cached Microsoft Entra access token. With `Microsoft.Data.SqlClient` you can often avoid the factory and put `Authentication=Active Directory Default` (or another `Authentication` value) in the connection string instead.

When the factory throws while the storage installs the schema at startup (the token service isn't ready yet, for example), the install is tried again, as after a database error: startup doesn't stop. Later, an exception from the factory fails the operation that needed the connection.

## Database permissions

Installing or upgrading the schema and running jobs need different rights. You can run the app with an account that can do both, or install with a privileged account and run with a restricted one.

| Task | Needs |
|---|---|
| Install or upgrade the schema (`PrepareSchemaIfNecessary = true`, `SqlServerObjectsInstaller.Install`, or running the script) | Create a schema, create and alter tables, indexes and constraints in it. `db_owner` is simplest; `db_ddladmin` together with `db_datareader` and `db_datawriter` also works. |
| Run clients, servers and the dashboard | `SELECT`, `INSERT`, `UPDATE` and `DELETE` on the NightShift schema. Application locks (`sp_getapplock`) need nothing beyond the `public` role. |
| Dashboard connection metrics | Optional: `VIEW SERVER STATE`. Without it, `sys.sysprocesses` shows only the current session, so the connection and transaction counts are not meaningful. |

A setup with a restricted runtime account:

```sql
-- Run once as a privileged user, after the schema was installed (see Schema and upgrades)
CREATE USER [myapp_jobs] FOR LOGIN [myapp_jobs];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[HangFire] TO [myapp_jobs];
```

```csharp
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        PrepareSchemaIfNecessary = false,
    }));
```

With `PrepareSchemaIfNecessary = false` the app never runs the install script, so deploy schema upgrades yourself before you deploy a version that needs them. See [Schema and upgrades](schema-and-upgrades.md#manual-install).

## Using an existing database

Point the connection string at your application's database. All NightShift objects live in their own schema (`[HangFire]` by default, see `SchemaName`), so they don't collide with your tables as long as you don't already use a schema with that name. Entity Framework Core migrations don't touch them unless you map them.

Putting job data in its own database is worth considering when job volume is high: job processing writes constantly, and a separate database keeps that write load, its transaction log and its backups apart from your business data.

## Recommended options for new installs

Start with the defaults. A new `SqlServerStorageOptions` already uses sliding invisibility fetching (5 minutes), long polling of empty queues (200 ms), command batching (up to 5 minutes per batch) and `READ COMMITTED` transactions, and on a schema of version 6 or later the storage turns off global locks and, on version 8 or later, switches `[Set]` and `[Hash]` writes from `MERGE` to cheaper `INSERT`/`UPDATE` pairs. A new install creates version 9, so all of this is on, as long as you leave `DisableGlobalLocks` and `UseIgnoreDupKeyOption` unset.

What you might still change:

- `PrepareSchemaIfNecessary = false` once schema changes go through your deployment pipeline.
- `.WithJobExpirationTimeout(...)` if succeeded and deleted jobs should stay visible for longer than a day.
- `InactiveStateExpirationTimeout`, if jobs are long-lived (for example, retried or awaiting for weeks) and the `[State]` table grows.
- `SchemaName`, if `HangFire` isn't a name you want in your database.

Each option is explained in [SQL Server options](sql-server-options.md).

## More examples

### Without dependency injection

Console apps, Windows services and .NET Framework apps configure the static `GlobalConfiguration` and start a `BackgroundJobServer`:

```csharp
using Masterly.NightShift;

GlobalConfiguration.Configuration
    .UseSqlServerStorage(@"Server=.\sqlexpress;Database=MyApp;Integrated Security=True;TrustServerCertificate=True;");

using (var server = new BackgroundJobServer())
{
    BackgroundJob.Enqueue<EmailService>(x => x.SendWelcome(42));

    Console.WriteLine("Press Enter to stop.");
    Console.ReadLine();
}
```

### A custom schema name

```csharp
var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        SchemaName = "NightShift",
    }));
```

The schema is created if it doesn't exist. Changing `SchemaName` on an existing installation doesn't move data: the storage installs a new, empty set of tables under the new name.

The install script puts the name inside both `[...]` and `N'...'`, so a name can't contain `]`, `'` or control characters, and is at most 128 characters long. Setting such a name throws `ArgumentException`. Spaces, dashes, dots and other characters, including letters of any alphabet (for example `Задания`, whatever the database's collation), are fine.

### Several storages in one database

Each `SchemaName` is a separate storage with its own jobs, queues, recurring jobs and statistics. That lets two independent job systems share a database, for example a main one and one for long-running reports:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("NightShift")!;

// The default storage, used by IBackgroundJobClient, the first server and the first dashboard
builder.Services.AddNightShift(config => config.UseSqlServerStorage(connectionString));
builder.Services.AddNightShiftServer();

// A second, separate set of tables in the [Reports] schema of the same database
var reportsStorage = new SqlServerStorage(connectionString, new SqlServerStorageOptions
{
    SchemaName = "Reports",
});

builder.Services.AddNightShiftServer(reportsStorage);

var app = builder.Build();

app.MapNightShiftDashboard();                                         // /nightshift
app.MapNightShiftDashboard("/nightshift-reports", storage: reportsStorage);

var reportsClient = new BackgroundJobClient(reportsStorage);
reportsClient.Enqueue<ReportService>(x => x.BuildDaily());

app.Run();
```

The injected `IBackgroundJobClient` and `IRecurringJobManager`, and the static `BackgroundJob` and `RecurringJob` classes, always use the default storage. For the second one, create a `BackgroundJobClient` or `RecurringJobManager` with the storage, or get one from `IBackgroundJobClientFactory.GetClient(storage)`.

If you only need to separate kinds of work inside one app, queues are simpler than a second storage: see [Queues and workers](../server/queues-and-workers.md).

## Dashboard metrics

`UseSqlServerStorage` adds these metrics to the dashboard home page (once per process, the first time it's called):

| Metric | Shows |
|---|---|
| Schema Version | The schema version in the database. Shown as a success when it equals the version this package installs (9), as a warning when it's older (for example, after an install stopped before a heavy migration), and as a danger when the `[Schema]` table is empty. |
| Active Connections, Total Connections | Sessions connected to the database (`sys.sysprocesses`). Needs `VIEW SERVER STATE` to count other sessions. |
| Active Transactions | Sessions with an open transaction. |
| Data File(s) Used (MB), Log File(s) Used (MB) | Space used in the database's data and log files. |

## Things to know

- **Startup doesn't stop on install errors.** If the install script fails, with a database error or an exception from your connection factory, the storage waits 1 second and tries again, waits 3 seconds and tries a last time, then logs a warning and carries on. After a failed open it clears the SQL client's connection pool, so that the next attempt contacts the server instead of failing at once with the same error. Only a missing SQL client stops it: with a connection string and neither `Microsoft.Data.SqlClient` nor `System.Data.SqlClient` available, the constructor throws `InvalidOperationException`. If it can't read the schema version, it logs an error and uses the option defaults until it can: it reads the version again the first time the storage needs it, and then at most every 30 seconds, until that works. If the tables are still missing, the first real query fails. When jobs don't appear, check the log from startup.
- **Column sizes limit some names.** Keys of counters, sets, lists and hashes are at most 100 characters, hash field names 100, set values 256, job parameter names 40, state names 20, queue names 50 and server ids 200. Writing a longer one throws `ArgumentException` that names the limit; nothing is cut short, so a value is never stored under a different name than the one you read it back with. Removing, expiring or reading a key that is too long does nothing or finds nothing, because such a key can't exist. State reasons are cut to 99 characters. Distributed lock names (the schema name, a colon and the resource) go to `sp_getapplock`, which takes at most 255 characters: a longer name keeps its first 238 characters followed by `~` and 16 hexadecimal digits of a hash of the whole name, so two long names that start alike are still two locks. Names that fit are used as they are. A server id is the server's `ServerName` followed by the process id and a GUID (up to 48 characters), so keep `ServerName` to 150 characters or fewer: a server with a longer id can't announce itself, and logs the `ArgumentException` and tries again without processing jobs.
- **Apps sharing a storage share its queues and its schedulers.** Any server listening to a queue may pick up any job in it, so it must be able to load the job's type. Separate queues aren't enough on their own: any server moves due scheduled and retried jobs to their queue, which loads the job's type unless the job was created with the queue passed as an argument, and any server triggers recurring jobs and starts continuations, which always loads it. If two different apps use the same database, give each its own schema with `SchemaName` (see [Troubleshooting](../troubleshooting.md#a-job-fails-because-it-cant-be-loaded)).
- **Clocks.** Fetching, keeping fetched jobs invisible, server heartbeats and the removal of timed-out servers use the database server's clock (`SYSUTCDATETIME`, `GETUTCDATE`), and the delayed and recurring job schedulers ask the database for the current time. Other times come from the clock of the application server that writes them: when data expires (`ExpireAt`, set as "now plus the expiration timeout") and the "now" the expiration manager deletes against, when a state was entered (the state's `CreatedAt`) and when a job was created, and when a delayed job is due (`EnqueueAt`, set as "now plus the delay" when it is scheduled). So keep the application servers' clocks in sync, for example with NTP: a server whose clock is ahead schedules delayed jobs late, and if it runs the expiration manager, it deletes finished jobs and other expiring data early.

## See also

- [SQL Server options](sql-server-options.md)
- [Schema and upgrades](schema-and-upgrades.md)
- [Queues and workers](../server/queues-and-workers.md)
- [Dependency injection](../server/dependency-injection.md)
- [Dashboard setup](../dashboard/setup.md)
- [Troubleshooting](../troubleshooting.md)
