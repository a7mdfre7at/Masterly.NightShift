# Compatibility levels

The data compatibility level chooses the format in which jobs, arguments, dates and state data are written to storage. Raise it to get the newest, most compact format; keep it lower while servers that only understand older formats still process jobs from the same storage.

## Quick example

```csharp
using Masterly.NightShift;

services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSqlServerStorage("Server=.;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;"));
```

Without DI, call it on `GlobalConfiguration.Configuration`. A new application, with no older servers, should use `Version_180`.

## How it works

The level changes only what is **written**. Every Masterly.NightShift version reads every format, whatever its own level: a server at `Version_110` reads data written at `Version_180`, and the other way round. The data already in storage is never converted; new writes simply use the new format.

The default is `Version_110`, the oldest format, so that adding a newer Masterly.NightShift version to a storage that servers of an older version still use doesn't change anything they read. That default is kept for safety, not because it is the better choice. Hangfire servers are another matter: they can't share a storage with Masterly.NightShift servers at any level (see [Migrating from Hangfire](../migrating-from-hangfire.md#what-can-break)).

The level is process-wide (`GlobalConfiguration`), and it applies to everything that writes: clients creating jobs, servers changing states, the dashboard's actions and recurring job managers.

The value names come from the versions of Hangfire, the library Masterly.NightShift started from, in which each format first appeared.

## Options / API

`SetDataCompatibilityLevel(CompatibilityLevel level)` is an extension method on `IGlobalConfiguration`. It throws `InvalidEnumArgumentException` for a value that isn't defined.

| Value | Number | Writes |
|---|---|---|
| `CompatibilityLevel.Version_110` | 110 | The original format. The default. |
| `CompatibilityLevel.Version_170` | 170 | The compact job payload, millisecond timestamps, JSON dates in arguments, isolated internal options. |
| `CompatibilityLevel.Version_180` | 180 | Everything in `Version_170`, plus two writes that are now skipped. |

### What changes at `Version_170`

| Data | Below 170 | From 170 |
|---|---|---|
| Job payload (type, method, parameter types; SQL Server's `InvocationData` column, recurring jobs' `Job` field) | `{"Type":"...","Method":"...","ParameterTypes":"[\"...\"]","Arguments":"[...]","Queue":"..."}`, with parameter types and arguments as nested JSON strings. Always these names, whatever your serializer options: see below. | `{"t":"...","m":"...","p":["..."],"a":["..."],"q":"..."}`, with empty parameter types and arguments left out |
| `DateTime` arguments | The round-trip text without JSON quotes: `2019-03-05T13:20:04.5932150Z` | A JSON string: `"2019-03-05T13:20:04.593215Z"` |
| Dates in state data and hashes (`EnqueuedAt`, `SucceededAt`, `FailedAt`, `StartedAt`, `EnqueueAt`, `ScheduledAt`, `DeletedAt`, recurring jobs' `CreatedAt`, `LastExecution`, `NextExecution`, and others) | ISO 8601: `2026-11-06T09:30:00.0000000Z` | Unix time in milliseconds: `1793957400000`. Dates before 1978-01-11 and `DateTime.MaxValue` stay ISO 8601, so they can't be mistaken for seconds. |
| Awaiting state (continuations) | `Options` as a name (`OnlyOnSucceededState`) plus an `Expiration` field | `Options` as a number (`1`), no `Expiration` |
| Internal library data | Written with the user serializer options, or with the internal options when yours can't write it | Written with the isolated internal options, so `UseSerializerSettings` can't change it. See [Serialization](serialization.md#three-sets-of-options). |

Below `Version_170`, the job payload itself is still written with the internal options. Your options could otherwise rename its members (a snake_case naming policy writes `parameter_types`) or leave them out (`IgnoreReadOnlyProperties` drops `Type`, `Method` and `ParameterTypes`), and then no server finds the job's method. Masterly.NightShift and Hangfire 1.7 or later read these PascalCase names whatever their own settings; older Hangfire versions read them unless their own serializer settings rename members. The parameter types and arguments inside it are written as before. A payload stored earlier with renamed members is still read: when `ParameterTypes` is missing, the reader tries your options' names.

The `Tags`, `BatchId` and `BatchContinuationOf` job parameters and the `FanInContinuations` list are written with the internal options at every level too. Only Masterly.NightShift reads them, and your options (a source-generated resolver that doesn't know `string[]`, for example) must not be able to break them. Values written with your options before are still read.

Below `Version_170`, any other library data your options can't write is written with the internal options as well, instead of failing. With a source-generated resolver that only knows your argument types, the `string[]` of the job's arguments, SQL Server's state data (`Dictionary<string, string>`), the continuation list and the job console entries would otherwise make every job creation and state change throw `NotSupportedException`. Data your options can write is written with them, as before.

### What changes at `Version_180`

| Data | Below 180 | At 180 |
|---|---|---|
| `CurrentUICulture` job parameter | Always written by `CaptureCultureAttribute` | Not written when it equals `CurrentCulture`. A server reading the job uses `CurrentCulture` for both. |
| `awaiting` set | Every job entering the Awaiting state is added to it | Not written when the storage reports the `Monitoring.AwaitingJobs` feature, as SQL Server does. The monitoring API finds awaiting jobs without it. |

## More examples

### A new application

```csharp
GlobalConfiguration.Configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings();
```

### Rolling upgrade: old and new servers side by side

Use this when servers of an older Masterly.NightShift version keep processing jobs from the same storage while you roll out a newer one. It doesn't apply to Hangfire servers: stop all of them before Masterly.NightShift servers start, as [Migrating from Hangfire](../migrating-from-hangfire.md#what-can-break) explains.

1. **Find the newest format every server can read.** That is the newest level the oldest server's version knows. Every Masterly.NightShift version so far reads `Version_110`, `Version_170` and `Version_180`; a level that a later version adds can be used only once every server runs that version. If you don't know, use the level the old servers are configured with, or `Version_110` if they set none.
2. **Deploy the new version with that level**, on clients and servers alike:

   ```csharp
   GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_110);
   ```

3. **Let both versions run side by side.** Old servers process jobs written by new clients, and new servers process jobs already in storage, whatever their format.
4. **Remove the old servers.** Wait until none of them sends heartbeats any more (the dashboard's Servers page lists them).
5. **Raise the level everywhere** and redeploy:

   ```csharp
   GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180);
   ```

   The order doesn't matter, because every new server reads every format. Nothing in storage needs converting.

### Rolling back

Data written at a higher level can't be read by a version that doesn't know that format. Before going back to an older version, lower the level on the new version and wait until the jobs written in the newer format have finished, or roll back only to a version that reads the formats in your storage.

## Things to know

- **The level controls the format, not the types.** An older server can only run jobs whose types it can load. That is also why Hangfire servers can't share the storage at any level: they don't have the `Masterly.NightShift.*` types that continuations (their next state is stored with its type name) and methods taking `IJobCancellationToken`, `PerformContext` or `IJobConsole` name in stored data, and they don't know the new state data and job parameters. Stop them before Masterly.NightShift servers start; see [Migrating from Hangfire](../migrating-from-hangfire.md#what-can-break).
- **Newer features have their own rules.** Pausing recurring jobs, fan-in continuations (`ContinueJobWithAll`/`ContinueJobWithAny`) and other additions write data that older servers ignore or don't act on, whatever the level. Use them after every server runs the new version. The list is in [breaking changes](../breaking-changes.md#behaviour-changes-that-are-not-api-breaks).
- **Clients count too.** An application that only creates jobs (a web front end with no server) writes data, so it needs the same level as the servers.
- **Some type names don't depend on the level.** Arguments declared as an interface or abstract class are written with a `"$type"` at every level, because they can't be read back without it. Every Masterly.NightShift version reads it, whatever its serializer settings. See [Serialization](serialization.md#how-type-is-handled).
- **Tests** in this repository run each serialization test once per level with `[DataCompatibilityRangeFact]`. See [Contributing](../contributing/README.md#tests).

## See also

- [Serialization](serialization.md)
- [Global configuration](global-configuration.md)
- [Migrating from Hangfire](../migrating-from-hangfire.md)
- [Schema and upgrades](../storage/schema-and-upgrades.md)
- [Source: GlobalConfiguration.cs](../../src/Masterly.NightShift.Core/Configuration/GlobalConfiguration.cs)
