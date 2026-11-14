# Entity Framework Core storage

`Masterly.NightShift.EntityFrameworkCore` keeps jobs in any relational database Entity Framework Core supports: SQL Server, PostgreSQL, SQLite, MySQL and others. It works with a context of its own, or with your application's context, and then it can **stage** jobs in that context so they are created only if your `SaveChanges` commits.

## Quick example

```
dotnet add package Masterly.NightShift.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL   # or the provider you use
```

With a context of its own, the storage creates its tables in the `nightshift` schema when they are missing:

```csharp
using Masterly.NightShift;

builder.Services.AddNightShift(config => config
    .UseEntityFrameworkCoreStorage(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db"))));
builder.Services.AddNightShiftServer();
```

### Jobs that commit with your data

Add the storage's tables to your context, so your migrations create them:

```csharp
public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyNightShiftModel(); // tables in the "nightshift" schema
    }
}
```

Use your context for the storage, then stage jobs with `context.NightShift()`:

```csharp
builder.Services.AddDbContextFactory<ShopContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddNightShift((provider, config) => config.UseEntityFrameworkCoreStorage(
    () => provider.GetRequiredService<IDbContextFactory<ShopContext>>().CreateDbContext()));

// In a request
public async Task PlaceOrderAsync(ShopContext db, Order order)
{
    db.Orders.Add(order);
    db.NightShift().Enqueue<IEmailSender>(x => x.SendConfirmation(order.Id));
    await db.SaveChangesAsync(); // the order and the job, or neither
}
```

`context.NightShift()` returns an `IBackgroundJobClient`, so `Enqueue`, `Schedule` and every other creation method work. Without `SaveChanges`, or when your transaction rolls back, the job never runs.

## How it works

Tables (in `Schema`, `nightshift` by default): `Job` (with its current state), `State` (history), `JobParameter`, `JobQueue`, `Set`, `List`, `Hash`, `Counter`, `AggregatedCounter`, `Server` and `Lock`. Every query goes through LINQ, `ExecuteUpdate` and `ExecuteDelete`, with no provider-specific SQL, except the one statement that reads [the database's clock](#the-databases-clock).

- **Transactions** run in a database transaction. When two transactions add the same set item or hash field at once, the one that loses starts again and updates the row the other added.
- **Fetching** reads the oldest visible entry of a queue, then claims it with an update that only succeeds if nobody claimed it first. On SQL Server it skips the entries another transaction holds (`readpast`), so an open transaction with staged jobs doesn't stall the workers, with `READ_COMMITTED_SNAPSHOT` or without. PostgreSQL, MySQL and Oracle read without waiting for writers, and SQLite has no row locks, so no database setting is needed. Whether a fetched job's worker stopped keeping it is judged by [the database's clock](#the-databases-clock) where the storage reads it, from a reading at most a second old.
- **Locks** are rows with an owner and an expiry, renewed every third of `DistributedLockLifetime`. The expiry is stamped and judged with [the database's clock](#the-databases-clock) where the storage reads it. A resource longer than its column (255 characters) keeps a readable prefix followed by `~` and 16 hexadecimal digits of a hash of the whole name, as on SQL Server, so two long resources that begin alike stay two locks.
- **Counters** are inserted as increments and merged by a storage-wide process, so state changes never wait for each other on a counter. Another process deletes expired rows every `JobExpirationCheckInterval`. It deletes a job only while it is still expired, so a job requeued meanwhile stays with its history; the states and parameters of a deleted job go with it, through the cascading foreign keys of the model, and are deleted next where the database doesn't enforce them (SQLite with `Foreign Keys=False`). Where the foreign keys don't cascade (tables made by a migration of your own), deleting the jobs first fails; the storage logs a warning once and from then on deletes each batch in one transaction, children first: it locks the jobs that are still expired, deletes their states and parameters, then the jobs and their queue entries. A job requeued meanwhile waits for that transaction, so no job is left without its history. Make those foreign keys `ON DELETE CASCADE`, as the storage's model does, to delete with less work.
- **Staging.** `context.NightShift()` reserves the job's id at once, in a row committed on a connection of its own, outside your transaction, so client filters see the real id. The reservation lasts at least a day, so the context may be saved long after the job is staged. The job's state, queue entry and every other write of its creation become tracked changes of your context. Saved, they commit with your data; discarded or rolled back, the reserved row has no state and is deleted when it expires.
- **Staging inside your transaction.** While your context has an open transaction (`BeginTransaction`, `UseTransaction`) or is inside a `TransactionScope`, what the client and its filters read or write about the jobs, sets, hashes, lists and counters saved in that transaction runs in it, on your context's connection: a continuation, a fan-in join or a duplicate check about such a job sees it, and commits or rolls back with your transaction. That covers rows staged before `BeginTransaction` and saved in the transaction, and rows saved by another context of yours that shares the transaction. Everything else the filters read or write, the locks they take, and the reservation of the id run on connections of the storage, outside your transaction.
- **Continuations of jobs you didn't stage.** A continuation or a join whose parents you didn't stage is registered on those parents only once it is committed: when `SaveChanges` saves it without a transaction, or when your transaction or scope commits. It starts then if its parents have already finished, and its history shows `Awaiting` twice. Rolled back, or never saved, it is never registered, and its parents never wait for it. Should the process stop between the commit and the registration, a server registers it within about 2 minutes (the storage's `StagedContinuationsProcess`, which keeps such continuations in the set `staging:continuations`). A continuation of a parent you staged is registered at once, in your transaction.
- **`[PreventDuplicates]` reserves at once.** A staged job with [`[PreventDuplicates]`](../filters/concurrency.md#preventduplicatesattribute) reserves its fingerprint at once, outside your transaction, as a job created through the storage does: a second copy staged before the same save, or created by any process meanwhile, is refused. Without a save, or when your transaction rolls back, the reservation lapses after 2 minutes, as for a creation that stopped.
- **Saved rows aren't tracked.** After every successful `SaveChanges`, your context stops tracking the storage's rows, so a later staged change (a requeue, for example) applies to the row as it is in the database.
- **Your context's tracking settings don't matter.** The storage changes rows it reads, so each context your factory gives it is switched to `QueryTrackingBehavior.TrackAll` with `AutoDetectChangesEnabled` on, whatever `UseQueryTrackingBehavior` or the context's constructor set. Staging in your own context switches tracking on only while it reads the storage's rows, then puts your setting back, and when your context doesn't detect changes, it detects those of the storage's rows only: your queries and your entities behave as you configured them.
- **Retrying execution strategies.** Staging works with a storage whose contexts use `EnableRetryOnFailure`. The operations it runs inside your transaction run without retries, as a retry inside a transaction can't be made; a fully custom `IExecutionStrategy` isn't covered.
- **Lost replies aren't applied twice.** With a retrying execution strategy, each of the storage's own transactions also writes a marker, a field of the hash `ef-storage:commits` that expires after 5 minutes, in the same commit. A commit that succeeded but whose reply was lost is recognized by its marker, as EF Core's `ExecuteInTransaction` does, instead of being run again and duplicating queue entries, counters, list items and history. A fetch whose claim succeeded but whose reply was lost claims the same entry again when the strategy retries it, so the job isn't left hidden under a claim nobody holds. Without a retrying strategy, nothing is retried and no marker is written. A marker is one `Hash` row per transaction: every expiration manager deletes the expired ones every minute (or every `JobExpirationCheckInterval`, if that is shorter), so the table holds at most about 6 minutes of them, roughly 290,000 rows at 800 transactions a second. The retry that looks for a marker comes within the strategy's retry delays, a minute or two with the providers' defaults; a strategy whose retries go on for longer than 5 minutes after a lost commit may apply that transaction twice.
- **Lengths are checked before writing.** Keys and hash fields of at most 100 characters, set values of 256, job parameter names of 40, state names of 20 and queue names of 50 fit the columns, the limits of the SQL Server storage. A longer one is refused with `ArgumentException` before anything is written, on every provider (SQLite would otherwise store it whole, and the other databases refuse it deep in `SaveChanges`). State reasons longer than 100 characters are cut.
- **No ambient transaction.** The storage's contexts never join a `TransactionScope` of the caller, on any provider and with a context of its own or yours: a job created inside a scope exists when the call returns and stays when the scope rolls back, and a storage write that fails doesn't doom the scope. To create a job together with your data, stage it with `context.NightShift()` (see [Jobs that commit with your data](#jobs-that-commit-with-your-data)): it then follows your `SaveChanges`, your transaction or your scope. A context whose connection you opened and enlisted yourself is used as it is.
- **Comparisons follow the database's collation.** Keys, hash fields, set values and queue names are compared by the database: SQLite and PostgreSQL compare them as written, while the default collations of SQL Server and MySQL ignore case, so `Key` and `key` are one key there.

### The database's clock

On SQL Server, PostgreSQL (Npgsql), MySQL and MariaDB (Pomelo and Oracle's MySQL provider) and Oracle, the storage reads the database's clock and reports `JobStorageFeatures.Connection.GetUtcDateTime`. The delayed and recurring job schedulers, the dashboard, the [health endpoints and the health check](../monitoring/health-checks.md) and the lease of a [chain](../jobs/chains.md#when-a-step-runs) that is being created or changed use that clock. Server heartbeats, the leases of distributed locks and the invisibility of fetched jobs are stamped and timed out with it. For these, the clocks of the clients and servers that share the storage needn't agree.

| Provider | Statement |
|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | `select sysutcdatetime()` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | `select clock_timestamp() at time zone 'utc'` |
| `Pomelo.EntityFrameworkCore.MySql`, `MySql.EntityFrameworkCore` | `select date_format(utc_timestamp(6), '%Y-%m-%d %H:%i:%s.%f')` |
| `Oracle.EntityFrameworkCore` | `select sys_extract_utc(systimestamp) from dual` |

- **SQLite keeps the process's clock.** SQLite runs inside the process and reads the machine's clock, so its clock is the process's own. The storage runs no statement there and doesn't report the feature.
- **Any other provider needs `DatabaseClockSql`.** Without it the storage logs one line at the information level when it is created, doesn't report the feature, and each process uses its own clock. Set the option to the SQL that returns the database's current UTC time as a single value: a date and time, or text such as `2026-01-31 12:00:00.000`. The option also replaces the statement of a provider in the table, and with it set on SQLite, SQLite is asked too.
- **A failed read is never final.** Whether the feature is reported comes from the provider and the option alone; the database isn't asked for it. SQL Server's built-in statement is never given up on: a read that fails throws like any other storage read, and the next read asks again. Any other statement (the PostgreSQL, MySQL, MariaDB and Oracle ones, and any statement from `DatabaseClockSql`) is checked by the reads until it has answered once, because this repository's tests run only the SQL Server statement and the SQLite path against a real database. A read the database refuses throws too, as one refusal may be a busy database, a lost deadlock or a failover. The statement is taken for wrong only after the database, in reach, refused it 3 times over at least 5 seconds, or at once when it answers something that isn't a date and time, or a time more than 30 minutes from the process's clock (a statement that returns local time instead of UTC). Then one warning is logged, the process uses its own clock, and the statement is asked again every 15 seconds; once it answers the UTC time, the database's clock is taken up and an information line says so. A process changes clocks at most once while the storage lives, from its own to the database's, never back: once a statement has answered, a read that fails throws.
- **`UseTimeProvider` doesn't drive the schedulers on these databases,** as on the SQL Server storage: they ask the database for the time. On SQLite it does, as with the in-memory storage.
- **Cost.** Each read is one more statement, on a context of the storage: one for each scheduler pass, heartbeat, watchdog check, dashboard page, poll of the live jobs, `/health` or `/metrics` request and health check run, and each time a chain's lease is taken, renewed or checked. Taking a free lock costs the same as without the clock, two statements; each poll for a lock that is held costs one more, and a renewal costs two instead of one. Fetching reads the clock once a second at most, for the whole process.

What still uses process clocks is listed under [Things to know](#things-to-know).

## Options / API

| `EntityFrameworkCoreStorageOptions` | Default | What it does |
|---|---|---|
| `Schema` | `nightshift` (none on MySQL and MariaDB) | The schema of a dedicated context's tables; `null` for the default schema (SQLite has none). Left unset on MySQL or MariaDB (Pomelo or Oracle's MySQL provider), it is `null`: the tables go in the connection's database. Your own context sets it in `ApplyNightShiftModel(schema)`. |
| `PrepareSchemaIfNecessary` | `true` | Whether a dedicated context creates the database and its tables when they are missing, and checks the schema version of tables that exist; they are never changed. The storage connects when it is created. Several processes may start at once: one creates the tables and the others wait for them. On a database whose DDL isn't transactional (MySQL, MariaDB, Oracle), where the tables appear one by one, a process that finds them incomplete waits up to 30 seconds, then creates those still missing itself (with their indexes), so a creator that stopped half-way doesn't block later starts. |
| `QueuePollInterval` | 1 second | How long a waiting worker sleeps. Jobs enqueued, or staged and saved, through the same storage object wake workers at once. |
| `InvisibilityTimeout` | 30 minutes | How long a fetched job stays invisible after its worker stopped keeping it alive. |
| `JobExpirationCheckInterval` | 30 minutes | How often expired rows are deleted. Expired commit markers (see above) are deleted every minute in between. |
| `CountersAggregateInterval` | 5 minutes | How often counter increments are merged. |
| `DistributedLockLifetime` | 30 seconds | How long a lock outlives a process that died holding it. |
| `DatabaseClockSql` | `null` | The SQL that returns the database's current UTC time as a single value, for a provider the storage knows no statement for, or to replace a built-in one. `null` uses the provider's statement; an empty or whitespace value throws `ArgumentException`. See [The database's clock](#the-databases-clock). |

| API | What it does |
|---|---|
| `UseEntityFrameworkCoreStorage(o => o.UseSqlServer(...), options?)` | A storage with a context of its own. |
| `UseEntityFrameworkCoreStorage(() => context, options?)` | A storage on contexts of your application, whose model calls `ApplyNightShiftModel`. |
| `modelBuilder.ApplyNightShiftModel(schema)` | Adds the storage's tables to your model. |
| `context.NightShift(storage)`, `context.NightShift()` | A client that stages jobs in the context (the second uses `JobStorage.Current`). |

## Things to know

- **EF Core versions.** The package needs EF Core 8 on .NET 8 and EF Core 10 on .NET 10, the versions each runtime supports.
- **MySQL and MariaDB have no schemas.** Their schemas are databases, and Pomelo refuses a table with a schema unless its `SchemaBehavior` option says what to do with it (`MySqlSchemaBehavior.Throw` is its default). A dedicated context puts the tables in the connection's database there, unless you set `Schema`. With your own context, call `modelBuilder.ApplyNightShiftModel(schema: null)`, since `ApplyNightShiftModel()` puts the tables in the `nightshift` schema on every provider; or keep a schema and configure Pomelo's `SchemaBehavior(MySqlSchemaBehavior.Ignore)` or `Translate`, which then decide the tables' names.
- **Schema versions.** A dedicated context records the version of the tables it creates in a one-row `SchemaVersion` table, and refuses tables of another version with `InvalidOperationException` (tables of a newer version: upgrade every process; of an older one, or without the table: use a new schema or database). With your own context, the tables are yours: when a new version of the package changes the model (the changelog says so), add a migration with `dotnet ef migrations add` and apply it before the new version runs.
- **Staging covers creation, not chains.** Enqueue, Schedule and the rest stage the job's state and queue entry. A job chain can't be staged: `chain.Enqueue(context.NightShift())`, and `Schedule` with that client, throw `NotSupportedException` (`A job chain can't be staged in a context: the client checks the steps it has created, which the context only writes when it is saved. [...]`). Create the chain with a client of the storage (`new BackgroundJobClient(storage)`) once your transaction has committed.
- **After-commit work runs when the job is staged.** Alerts, activity feed entries and other work filters do once a job's creation is committed run when the job is staged, not when the context is saved, and run even if the context is never saved.
- **Known limits of staging.** With a transaction held open for more than 2 minutes, the fingerprint reservation of a `[PreventDuplicates]` job lapses, and another process can create a second copy. A transaction you commit directly on the `DbTransaction` tells nobody: continuations of jobs you didn't stage are registered by the server process, in about 1 to 2 minutes.
- **Saving wakes workers.** A `SaveChanges` that saves staged queue entries wakes this process's waiting workers: at once without a transaction, and when the transaction commits inside one your context began or a `TransactionScope`. A transaction you commit directly on the `DbConnection` tells nobody: the workers find the jobs at their next poll. Other processes see the jobs at their next poll. Saves without queue entries wake nobody.
- **SQLite has one writer at a time.** While your context's transaction is open, anything the storage writes outside it (the job's id, its locks, a fingerprint reservation) would wait for that transaction, so staging throws `InvalidOperationException` at once wherever it would have to: `A job can't be staged on SQLite while the context's transaction is open: [...]`. Stage and save before `BeginTransaction`, or enqueue through the storage once your transaction committed.
- **Some times still come from process clocks.** On every provider, when jobs and other rows expire, the times written into states and the times jobs are scheduled for use the clock of the process that writes them; they are counted in hours and days. Without the database's clock (on SQLite, on a provider without a statement, and while a statement is taken for wrong), lock leases and the invisibility of fetched jobs use process clocks too: processes whose clocks differ by more than `DistributedLockLifetime` (30 seconds) can then take each other's locks. Keep the clocks of the processes that share a storage synchronized (NTP).
- **Without the database's clock, the processes' clocks must agree to well within 2 minutes.** That is the case on a provider without a statement and while a statement is taken for wrong (see [The database's clock](#the-databases-clock)). The schedulers, heartbeats, locks and fetched jobs then use process clocks, and so does the 2-minute lease that protects a [chain](../jobs/chains.md#when-a-step-runs) that is being created or changed: the client writes and renews it with its own clock and the servers compare it with theirs. With a client clock that far behind a server's, the server takes the lease for lapsed: a worker that takes the first step of a chain that is still being created deletes it, so `Enqueue` or `Schedule` throws `InvalidOperationException`, and steps can start while a chain is retried or replaced. With a client clock ahead, the steps wait that much longer after a client that stopped; more than an hour ahead, the lease is taken for damaged and doesn't count.

## See also

- [Storage](README.md)
- [SQL Server storage](sql-server.md)
- [Custom storage](custom-storage.md)
- [Creating jobs](../jobs/README.md)
