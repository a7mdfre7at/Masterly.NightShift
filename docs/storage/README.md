# Storage

Masterly.NightShift keeps every job, its state history, queues, recurring job definitions, counters and server heartbeats in a job storage, so jobs survive restarts and any number of servers can share the work. SQL Server storage ships in the box (`Masterly.NightShift.SqlServer`, included in the `Masterly.NightShift` meta-package); other databases can be supported by implementing `JobStorage`. This section covers setting up SQL Server, tuning it, managing its schema, and writing a storage of your own.

| Page | What it covers |
|---|---|
| [SQL Server setup](sql-server.md) | `UseSqlServerStorage` overloads, connection strings and factories, choosing `Microsoft.Data.SqlClient` or `System.Data.SqlClient`, database permissions, schemas, several storages in one database |
| [SQL Server options](sql-server-options.md) | Every `SqlServerStorageOptions` property: fetching, polling, batching, locks, cleanup, schema handling |
| [Schema and upgrades](schema-and-upgrades.md) | The tables created, schema versions 1 to 9, automatic and manual install, heavy migrations, purging data, backups |
| [Custom storage](custom-storage.md) | For storage authors: `JobStorage` and its base classes, feature flags, the semantics a storage must provide, a skeleton |

## Most common setup

Add a SQL Server client package next to Masterly.NightShift (the storage works with either client but doesn't pick one for you):

```
dotnet add package Masterly.NightShift
dotnet add package Microsoft.Data.SqlClient
```

If the build warns about `System.Security.Cryptography.Pkcs` (NU1902, NU1903), see [The SQL Server driver](../getting-started/installation.md#the-sql-server-driver).

Then point the storage at a database. The tables are created on first start in the `[HangFire]` schema:

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

## See also

- [Installation](../getting-started/installation.md)
- [Core concepts](../getting-started/core-concepts.md)
- [Server options](../server/server-options.md)
- [Global configuration](../configuration/global-configuration.md)
