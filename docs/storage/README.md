# Storage

Masterly.NightShift keeps every job, its state history, queues, recurring job definitions, counters and server heartbeats in a job storage, so jobs survive restarts and any number of servers can share the work. SQL Server storage ships in the box (`Masterly.NightShift.SqlServer`, included in the `Masterly.NightShift` meta-package). Separate packages store jobs in memory, Redis, MongoDB or any database Entity Framework Core supports, and other databases can be supported by implementing `JobStorage`.

| Package | Storage | Use it when |
|---|---|---|
| `Masterly.NightShift.SqlServer` | [SQL Server](sql-server.md) | You run SQL Server: the reference storage, with the most tuning options |
| `Masterly.NightShift.EntityFrameworkCore` | [Entity Framework Core](entity-framework-core.md) | You use PostgreSQL, MySQL, SQLite or another EF Core provider, or want jobs that commit with your `SaveChanges` |
| `Masterly.NightShift.Redis` | [Redis](redis.md) | You want low latency and already run Redis with persistence |
| `Masterly.NightShift.MongoDB` | [MongoDB](mongodb.md) | You run a MongoDB replica set |
| `Masterly.NightShift.InMemory` | [In memory](in-memory.md) | Development, tests and demos: nothing survives the process |

Every storage passes the same conformance suite (`tests/Shared/StorageConformance`), which the SQL Server storage passes too, so jobs, queues, locks and the dashboard behave the same on all of them. The Entity Framework Core, Redis, MongoDB and in-memory storages take a distributed lock as a lease that its holder renews; releasing the lock doesn't wait for a renewal in progress, so a slow or unreachable store doesn't hold up the worker that releases it.

| Page | What it covers |
|---|---|
| [SQL Server setup](sql-server.md) | `UseSqlServerStorage` overloads, connection strings and factories, the SQL client, database permissions, schemas, several storages in one database |
| [SQL Server options](sql-server-options.md) | Every `SqlServerStorageOptions` property: fetching, polling, batching, locks, cleanup, schema handling |
| [Schema and upgrades](schema-and-upgrades.md) | The tables created, schema versions 1 to 9, automatic and manual install, heavy migrations, purging data, backups |
| [Entity Framework Core](entity-framework-core.md) | A context of its own or yours, `ApplyNightShiftModel`, staging jobs with `context.NightShift()` so they commit with `SaveChanges` |
| [Redis](redis.md) | `UseRedisStorage`, the key layout, Lua transactions, pub/sub wake-ups, clusters |
| [MongoDB](mongodb.md) | `UseMongoStorage`, replica sets, collections and TTL indexes |
| [In memory](in-memory.md) | `UseInMemoryStorage` for development and tests |
| [Custom storage](custom-storage.md) | For storage authors: `JobStorage` and its base classes, feature flags, the semantics a storage must provide, a skeleton |

## Most common setup

Install Masterly.NightShift, which brings the SQL Server storage and its driver:

```
dotnet add package Masterly.NightShift
```

Then point the storage at a database. The tables are created on first start in the `[HangFire]` schema:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")!));
builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapNightShiftDashboard();

app.Run();
```

## See also

- [Installation](../getting-started/installation.md)
- [Core concepts](../getting-started/core-concepts.md)
- [Server options](../server/server-options.md)
- [Global configuration](../configuration/global-configuration.md)
