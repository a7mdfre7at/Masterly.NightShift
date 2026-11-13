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
- **Fetching** reads the oldest visible entry of a queue, then claims it with an update that only succeeds if nobody claimed it first.
- **Locks** are rows with an owner and an expiry, renewed every third of `DistributedLockLifetime`. A resource longer than its column (255 characters) keeps a readable prefix followed by `~` and 16 hexadecimal digits of a hash of the whole name, as on SQL Server, so two long resources that begin alike stay two locks.
- **Counters** are inserted as increments and merged by a storage-wide process, so state changes never wait for each other on a counter. Another process deletes expired rows every `JobExpirationCheckInterval`.
- **Staging.** `context.NightShift()` reserves the job's id at once, in a row committed on a connection of its own, outside your transaction, so client filters see the real id. The reservation lasts at least a day, so the context may be saved long after the job is staged. The job's state, queue entry and every other write of its creation become tracked changes of your context. Saved, they commit with your data; discarded or rolled back, the reserved row has no state and is deleted when it expires.
- **Lengths are checked before writing.** Keys and hash fields of at most 100 characters, set values of 256, job parameter names of 40, state names of 20 and queue names of 50 fit the columns, the limits of the SQL Server storage. A longer one is refused with `ArgumentException` before anything is written, on every provider (SQLite would otherwise store it whole, and the other databases refuse it deep in `SaveChanges`). State reasons longer than 100 characters are cut.
- **Comparisons follow the database's collation.** Keys, hash fields, set values and queue names are compared by the database: SQLite and PostgreSQL compare them as written, while the default collations of SQL Server and MySQL ignore case, so `Key` and `key` are one key there.

### The database's clock

On SQL Server, PostgreSQL (Npgsql), MySQL and MariaDB (Pomelo and Oracle's MySQL provider) and Oracle, the storage reads the database's clock and reports `JobStorageFeatures.Connection.GetUtcDateTime`. The delayed and recurring job schedulers, the dashboard, the [health endpoints and the health check](../monitoring/health-checks.md) and the lease of a [chain](../jobs/chains.md#when-a-step-runs) that is being created or changed use that clock, and server heartbeats are written and timed out with it. For these, the clocks of the clients and servers that share the storage needn't agree.

| Provider | Statement |
|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | `select sysutcdatetime()` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | `select clock_timestamp() at time zone 'utc'` |
| `Pomelo.EntityFrameworkCore.MySql`, `MySql.EntityFrameworkCore` | `select date_format(utc_timestamp(6), '%Y-%m-%d %H:%i:%s.%f')` |
| `Oracle.EntityFrameworkCore` | `select sys_extract_utc(systimestamp) from dual` |

- **SQLite keeps the process's clock.** SQLite runs inside the process and reads the machine's clock, so its clock is the process's own. The storage runs no statement there and doesn't report the feature.
- **Any other provider needs `DatabaseClockSql`.** Without it the storage logs one line at the information level when it is created, doesn't report the feature, and each process uses its own clock. Set the option to the SQL that returns the database's current UTC time as a single value: a date and time, or text such as `2026-01-31 12:00:00.000`. The option also replaces the statement of a provider in the table, and with it set on SQLite, SQLite is asked too.
- **The statement is checked the first time the clock is read.** Whether the feature is reported comes from the provider and the option alone; the database isn't asked for it. A statement that fails on a database that can be reached, or that answers something other than a date and time, is logged once as a warning, and the process uses its own clock for as long as the storage object lives. Every statement but SQL Server's built-in one must also answer within 30 minutes of the process's clock, which tells a statement that returns local time from one that returns UTC. That covers the PostgreSQL, MySQL, MariaDB and Oracle statements and any statement from `DatabaseClockSql`: this repository's tests run only the SQL Server statement and the SQLite path against a real database, so the others are checked at first use instead. While the database can't be reached, the read fails like any other read and the next one checks again. Once a statement has passed, a read that fails throws; the storage doesn't go back to the process's clock.
- **`UseTimeProvider` doesn't drive the schedulers on these databases,** as on the SQL Server storage: they ask the database for the time. On SQLite it does, as with the in-memory storage.
- **Cost.** Each read is one more statement, on a context of the storage: one for each scheduler pass, heartbeat, watchdog check, dashboard page, poll of the live jobs, `/health` or `/metrics` request and health check run, and each time a chain's lease is taken, renewed or checked.

What still uses process clocks is listed under [Things to know](#things-to-know).

## Options / API

| `EntityFrameworkCoreStorageOptions` | Default | What it does |
|---|---|---|
| `Schema` | `nightshift` | The schema of a dedicated context's tables; `null` for the default schema (SQLite has none). Your own context sets it in `ApplyNightShiftModel(schema)`. |
| `PrepareSchemaIfNecessary` | `true` | Whether a dedicated context creates the database and its tables when they are missing, and checks the schema version of tables that exist; they are never changed. The storage connects when it is created. Several processes may start at once: one creates the tables and the others use them. |
| `QueuePollInterval` | 1 second | How long a waiting worker sleeps. Jobs enqueued, or staged and saved, through the same storage object wake workers at once. |
| `InvisibilityTimeout` | 30 minutes | How long a fetched job stays invisible after its worker stopped keeping it alive. |
| `JobExpirationCheckInterval` | 30 minutes | How often expired rows are deleted. |
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
- **Schema versions.** A dedicated context records the version of the tables it creates in a one-row `SchemaVersion` table, and refuses tables of another version with `InvalidOperationException` (tables of a newer version: upgrade every process; of an older one, or without the table: use a new schema or database). With your own context, the tables are yours: when a new version of the package changes the model (the changelog says so), add a migration with `dotnet ef migrations add` and apply it before the new version runs.
- **Staging covers creation.** Enqueue, Schedule and the rest stage the job's state and queue entry. `ContinueJobWith` registers the continuation on its parent at once; if your transaction rolls back, that continuation never runs.
- **After-commit work runs when the job is staged.** Alerts, activity feed entries and other work filters do once a job's creation is committed run when the job is staged, not when the context is saved, and run even if the context is never saved.
- **Saving wakes workers.** A `SaveChanges` that saves staged queue entries wakes this process's waiting workers at once; other processes see the jobs at their next poll. Saves without queue entries wake nobody.
- **SQLite has one writer at a time.** The job's id is reserved through a separate connection, which would wait while your context's transaction holds the write lock, so staging a job while that transaction is open throws `InvalidOperationException` at once. Stage jobs before `BeginTransaction`, let `SaveChanges` open its own transaction, or enqueue through the storage once your transaction committed.
- **Some times still come from process clocks.** On every provider, lock leases, the invisibility of fetched jobs, when jobs and other rows expire, the times written into states and the times jobs are scheduled for use the clock of the process that writes or compares them. Keep the clocks of the processes that share a storage synchronized (NTP): processes whose clocks differ by more than `DistributedLockLifetime` (30 seconds) can take each other's locks.
- **Without the database's clock, the processes' clocks must agree to well within 2 minutes.** That is the case on a provider without a statement and after a statement failed its check (see [The database's clock](#the-databases-clock)). The schedulers and heartbeats then use process clocks too, and so does the 2-minute lease that protects a [chain](../jobs/chains.md#when-a-step-runs) that is being created or changed: the client writes and renews it with its own clock and the servers compare it with theirs. With a client clock that far behind a server's, the server takes the lease for lapsed: a worker that takes the first step of a chain that is still being created deletes it, so `Enqueue` or `Schedule` throws `InvalidOperationException`, and steps can start while a chain is retried or replaced. With a client clock ahead, the steps wait that much longer after a client that stopped; more than an hour ahead, the lease is taken for damaged and doesn't count.

## See also

- [Storage](README.md)
- [SQL Server storage](sql-server.md)
- [Custom storage](custom-storage.md)
- [Creating jobs](../jobs/README.md)
