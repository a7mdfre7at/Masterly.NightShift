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

Tables (in `Schema`, `nightshift` by default): `Job` (with its current state), `State` (history), `JobParameter`, `JobQueue`, `Set`, `List`, `Hash`, `Counter`, `AggregatedCounter`, `Server` and `Lock`. Every query goes through LINQ, `ExecuteUpdate` and `ExecuteDelete`, with no provider-specific SQL.

- **Transactions** run in a database transaction. When two transactions add the same set item or hash field at once, the one that loses starts again and updates the row the other added.
- **Fetching** reads the oldest visible entry of a queue, then claims it with an update that only succeeds if nobody claimed it first.
- **Locks** are rows with an owner and an expiry, renewed every third of `DistributedLockLifetime`.
- **Counters** are inserted as increments and merged by a storage-wide process, so state changes never wait for each other on a counter. Another process deletes expired rows every `JobExpirationCheckInterval`.
- **Staging.** `context.NightShift()` reserves the job's id at once, in a row that expires by itself, so client filters see the real id. The job's state, queue entry and every other write of its creation become tracked changes of your context. Saved, they commit with your data; discarded or rolled back, the reserved row has no state and is deleted when it expires.

## Options / API

| `EntityFrameworkCoreStorageOptions` | Default | What it does |
|---|---|---|
| `Schema` | `nightshift` | The schema of a dedicated context's tables; `null` for the default schema (SQLite has none). Your own context sets it in `ApplyNightShiftModel(schema)`. |
| `PrepareSchemaIfNecessary` | `true` | Whether a dedicated context creates the database and its tables when they are missing. Tables that exist are never changed. |
| `QueuePollInterval` | 1 second | How long a waiting worker sleeps. Jobs enqueued, or staged and saved, through the same storage object wake workers at once. |
| `InvisibilityTimeout` | 30 minutes | How long a fetched job stays invisible after its worker stopped keeping it alive. |
| `JobExpirationCheckInterval` | 30 minutes | How often expired rows are deleted. |
| `CountersAggregateInterval` | 5 minutes | How often counter increments are merged. |
| `DistributedLockLifetime` | 30 seconds | How long a lock outlives a process that died holding it. |

| API | What it does |
|---|---|
| `UseEntityFrameworkCoreStorage(o => o.UseSqlServer(...), options?)` | A storage with a context of its own. |
| `UseEntityFrameworkCoreStorage(() => context, options?)` | A storage on contexts of your application, whose model calls `ApplyNightShiftModel`. |
| `modelBuilder.ApplyNightShiftModel(schema)` | Adds the storage's tables to your model. |
| `context.NightShift(storage)`, `context.NightShift()` | A client that stages jobs in the context (the second uses `JobStorage.Current`). |

## Things to know

- **EF Core versions.** The package needs EF Core 8 on .NET 8 and EF Core 10 on .NET 10, the versions each runtime supports.
- **Staging covers creation.** Enqueue, Schedule and the rest stage the job's state and queue entry. `ContinueJobWith` registers the continuation on its parent at once; if your transaction rolls back, that continuation never runs.
- **SQLite has one writer at a time.** The job's id is reserved through a separate connection, which waits while your explicit transaction holds the write lock. Stage jobs before `BeginTransaction`, or let `SaveChanges` open its own transaction.
- **No server clock.** EF Core has no portable way to read the database's time, so servers use their own clocks.

## See also

- [Storage](README.md)
- [SQL Server storage](sql-server.md)
- [Custom storage](custom-storage.md)
- [Creating jobs](../jobs/README.md)
