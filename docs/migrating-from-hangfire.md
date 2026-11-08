# Migrating from Hangfire

Masterly.NightShift is based on Hangfire 1.8.25 and renamed throughout. This page explains what you change in your code, what stays the same in your database, what can break, and gives a step-by-step checklist. Use it when you move an app, and its production data, from Hangfire to Masterly.NightShift.

## Quick example

A typical Hangfire setup in ASP.NET Core:

```csharp
// Before: Hangfire
using Hangfire;

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));
builder.Services.AddHangfireServer();

app.MapHangfireDashboard();
```

The same app on Masterly.NightShift:

```csharp
// After: Masterly.NightShift
using Masterly.NightShift;

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));
builder.Services.AddNightShiftServer();

app.MapNightShiftDashboard("/hangfire"); // keep the old URL; the new default is /nightshift
```

Point it at the same connection string and your existing jobs, recurring jobs and history show up and keep running.

## How it works

### What changes: names

Package IDs, namespaces and every member with `Hangfire` in its name were renamed. Type and member names without `Hangfire` in them are unchanged: `BackgroundJob`, `IBackgroundJobClient`, `RecurringJob`, `Cron`, `AutomaticRetryAttribute`, `QueueAttribute`, `BackgroundJobServerOptions`, `SqlServerStorageOptions`, `DashboardOptions`, `IDashboardAuthorizationFilter`, the states and the filter interfaces all keep their names. For most apps, the migration is a find-and-replace of package references, `using` lines and the few `Hangfire`-named extension methods.

### What stays the same: your data

The storage format is the same, so a database written by Hangfire is read as it is:

- **The SQL Server schema is still named `HangFire`**, and the tables and indexes are the same. If you set `SqlServerStorageOptions.SchemaName` before, set it to the same value now.
- **The schema version is 9**, as installed by `Install.sql`. If your database is at version 8, the only change applied on start-up is a new index on the `State` table. Older databases are upgraded step by step on start-up, as Hangfire did; see [Schema and upgrades](storage/schema-and-upgrades.md) for the heavy steps.
- **Stored type names are mapped.** Hangfire stored the names of its own types in job data, for example the parameter type `Hangfire.IJobCancellationToken, Hangfire.Core` of a job that takes a cancellation token, or `Hangfire.States.EnqueuedState, Hangfire.Core` as the next state of a continuation. When such a name is loaded, the type resolver maps the assemblies `Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.NetCore` and `Hangfire.AspNetCore` to `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer`, `Masterly.NightShift.NetCore` and `Masterly.NightShift.AspNetCore`, and the `Hangfire.` namespace prefix to `Masterly.NightShift.`. The assembly version and public key token in the stored name are dropped. Your own job types are stored under your own names and are not touched.
- **Storage keys keep their names.** Lists, sets and hashes are named as before, and a few keys that new features use carry the old name too, such as the drain-mode hash `hangfire:control`. Seeing `hangfire` in the data is expected.

Written data is not migrated in place: new jobs are written with the new type names, old jobs are mapped every time they are read.

### What can break

- **JSON is System.Text.Json, not Newtonsoft.Json.** Stored payloads stay readable, but your job argument types now have to suit System.Text.Json (see [Serialization changes](#serialization-changes) below).
- **Extensions built for Hangfire don't load.** Packages such as storages for Redis, PostgreSQL or MongoDB, dashboard extensions and filter libraries reference `Hangfire.Core` and must be recompiled against Masterly.NightShift. Data they stored under their own type names (for example `Hangfire.Console` or `Hangfire.Pro` types) is not mapped. Some of what they offered is built in now: a job console with progress (`IJobConsole`), throttling, rate limits, batches and more (see [What's new](whats-new-next.md)).
- **Old and new servers don't mix.** A Hangfire server can't load jobs whose stored data names Masterly.NightShift types, such as a job that takes a cancellation token or a continuation's next state, and it doesn't know the new state data and job parameters. No compatibility level changes that. Stop every Hangfire server before Masterly.NightShift servers start against the same database. The rolling upgrade in [Compatibility levels](configuration/compatibility-levels.md#rolling-upgrade-old-and-new-servers-side-by-side) is for moving between Masterly.NightShift versions.
- **Removed platforms and APIs.** OWIN and classic ASP.NET integration, .NET Framework before 4.6.2, the MSMQ queue package and a list of obsolete APIs are gone (see [Removed features](#removed-features)).

## Options / API

### Packages

| Hangfire | Masterly.NightShift |
|---|---|
| `Hangfire` (meta-package) | `Masterly.NightShift` (Core + SqlServer + AspNetCore; no longer pulls in `Microsoft.Owin.Host.SystemWeb`) |
| `Hangfire.Core` | `Masterly.NightShift.Core` |
| `Hangfire.SqlServer` | `Masterly.NightShift.SqlServer` |
| `Hangfire.NetCore` | `Masterly.NightShift.NetCore` |
| `Hangfire.AspNetCore` | `Masterly.NightShift.AspNetCore` |
| `Hangfire.SqlServer.MSMQ` | Removed. Use the default SQL Server queue |

`Masterly.NightShift.SqlServer` still needs a SQL Server driver in your app: `Microsoft.Data.SqlClient` or `System.Data.SqlClient` (see [Installation](getting-started/installation.md#the-sql-server-driver)). Newtonsoft.Json is no longer a dependency.

### Namespaces

| Hangfire | Masterly.NightShift |
|---|---|
| `Hangfire` | `Masterly.NightShift` |
| `Hangfire.Annotations` | `Masterly.NightShift.Annotations` |
| `Hangfire.Client` | `Masterly.NightShift.Client` |
| `Hangfire.Common` | `Masterly.NightShift.Common` |
| `Hangfire.Dashboard` | `Masterly.NightShift.Dashboard` |
| `Hangfire.Logging` | `Masterly.NightShift.Logging` |
| `Hangfire.Processing` | `Masterly.NightShift.Processing` |
| `Hangfire.Profiling` | `Masterly.NightShift.Profiling` |
| `Hangfire.Server` | `Masterly.NightShift.Server` |
| `Hangfire.SqlServer` | `Masterly.NightShift.SqlServer` |
| `Hangfire.States` | `Masterly.NightShift.States` |
| `Hangfire.Storage`, `Hangfire.Storage.Monitoring` | `Masterly.NightShift.Storage`, `Masterly.NightShift.Storage.Monitoring` |

As in Hangfire, the extension methods `UseSqlServerStorage`, `AddNightShift`, `UseNightShiftDashboard` and `MapNightShiftDashboard` are in the root `Masterly.NightShift` namespace.

### Renamed members

| Hangfire | Masterly.NightShift |
|---|---|
| `services.AddHangfire(...)` | `services.AddNightShift(...)` |
| `services.AddHangfireServer(...)` | `services.AddNightShiftServer(...)` |
| `app.UseHangfireDashboard(...)` | `app.UseNightShiftDashboard(...)` |
| `endpoints.MapHangfireDashboard(...)` | `endpoints.MapNightShiftDashboard(...)` |
| `endpoints.MapHangfireDashboardWithAuthorizationPolicy(...)` | `endpoints.MapNightShiftDashboardWithAuthorizationPolicy(...)` |
| `endpoints.MapHangfireDashboardWithNoAuthorizationFilters(...)` | `endpoints.MapNightShiftDashboardWithNoAuthorizationFilters(...)` |
| `app.UseHangfireServer(...)` (obsolete) | `app.UseNightShiftServer(...)` (still obsolete; use `AddNightShiftServer`) |
| Classes `HangfireServiceCollectionExtensions`, `HangfireApplicationBuilderExtensions`, `HangfireEndpointRouteBuilderExtensions` | `NightShiftServiceCollectionExtensions`, `NightShiftApplicationBuilderExtensions`, `NightShiftEndpointRouteBuilderExtensions` |

### Other visible changes

| Hangfire | Masterly.NightShift |
|---|---|
| Default dashboard path `/hangfire` | `/nightshift` |
| Default dashboard title "Hangfire Dashboard" | "Masterly.NightShift Dashboard" (`DashboardOptions.DashboardTitle`) |
| Dashboard JavaScript global `window.Hangfire`, assets `hangfire.js`, `hangfire.css` | `window.NightShift`, `nightshift.js`, `nightshift.css` |
| Bundled moment.js in the dashboard | Not bundled; dates use the browser's `Intl` APIs. Extensions that use the global `moment` must bring it themselves |
| `UseSerializerSettings(JsonSerializerSettings)` | `UseSerializerSettings(JsonSerializerOptions)` |
| `UseRecommendedSerializerSettings(Action<JsonSerializerSettings>)` | `UseRecommendedSerializerSettings(Action<JsonSerializerOptions>)` |
| Job methods invoked with `MethodInfo.Invoke` | Invoked through generated or compiled invokers; a job's exception is no longer wrapped in `TargetInvocationException` |
| Browser-saved dashboard preferences | Start from defaults |

The complete, tool-checked list is in [Breaking changes](breaking-changes.md).

### Removed features

| Removed | Use instead |
|---|---|
| OWIN and classic ASP.NET: `app.UseHangfireServer()`/`app.UseHangfireDashboard()` on `IAppBuilder`, `OwinBootstrapper`, `DashboardOwinExtensions` | ASP.NET Core with `AddNightShiftServer()` and `MapNightShiftDashboard()`, or `new BackgroundJobServer()` in any app plus a separate ASP.NET Core app for the dashboard |
| .NET Framework 4.5.1 to 4.6.1 | .NET Framework 4.6.2 or later, which uses the `netstandard2.0` build |
| `Dashboard.IAuthorizationFilter`, `DashboardOptions.AuthorizationFilters` | `IDashboardAuthorizationFilter` and `DashboardOptions.Authorization` |
| `IRequestDispatcher`, `RequestDispatcherContext` | `IDashboardDispatcher` and `DashboardContext` |
| `SqlServerStorageOptions.TransactionIsolationLevel` of type `System.Transactions.IsolationLevel?` (the .NET Framework builds only) | `SqlServerStorageOptions.UseRecommendedIsolationLevel`. The obsolete `System.Data.IsolationLevel?` property of the same name still exists |
| `JobHelper.SetSerializerSettings(...)` | `UseSerializerSettings(...)` |
| `Cron.GetDescription(string)` (.NET Framework only) | `Cron.Describe(string)` or `Cron.TryDescribe(string)`, on every target; the wording differs and is always English |
| `Hangfire.SqlServer.MSMQ` | The default SQL Server queue |

Apps on .NET Core 3.0 to .NET 7 now get the `netstandard2.0` build, which has no `MapNightShiftDashboard`; use `UseNightShiftDashboard` or move to .NET 8.

### Serialization changes

Hangfire serialized job arguments, states and parameters with Newtonsoft.Json. Masterly.NightShift uses System.Text.Json with lenient read options, so data written by Hangfire reads back: property names case-insensitively, numbers also from strings, enums as names or numbers, `"$type"` on states, arrays and other collections wrapped as `{"$type", "$values"}` (an `IReadOnlyList<T>` argument or member holding a `List<T>`, for example), and dictionaries with a leading `"$type"`. Structs that Newtonsoft.Json created through their constructor, and collection properties without a setter that it filled, read back too. What can break is your argument types:

| Newtonsoft.Json behaviour you may rely on | With System.Text.Json |
|---|---|
| Fills properties with a private or no setter | Needs a public setter, or `[JsonInclude]` on the property. A collection property without a setter still gets the stored items, added to the collection it holds, as before |
| Picks a constructor on its own | A class needs a public parameterless constructor, a single public constructor, or `[JsonConstructor]`. A struct is created through its constructor, as before, when that is its only public one and its parameters match get-only properties; a struct with several public constructors needs `[JsonConstructor]` |
| `[JsonProperty("name")]`, `[JsonIgnore]` and `[JsonConverter]` from `Newtonsoft.Json` | Ignored. Use `[JsonPropertyName]`, `[JsonIgnore]` and `[JsonConverter]` from `System.Text.Json.Serialization` |
| `JsonConvert.DefaultSettings` | Has no effect |
| Custom `JsonConverter`s registered in `UseSerializerSettings` | Rewrite them as System.Text.Json converters |
| A `"$type"` in a stored argument picks the subtype | For arguments declared as an interface or abstract class (new ones are always written with `"$type"` too) and for arguments declared as `object` (with the recommended settings). For a concrete declared type, `"$type"` is ignored and the value is read as that type. Properties and collection items declared as an interface or abstract class can't be read |
| `DefaultValueHandling.IgnoreAndPopulate` (Hangfire's recommended settings) left out members equal to their `[DefaultValue]`, `0`, `false` or `null` and filled them in when reading | Every member is written. A member missing from stored JSON gets its `[DefaultValue]` (default and recommended options); one without `[DefaultValue]` keeps its initializer. See [Serialization](configuration/serialization.md#defaultvalue-members) |
| An argument declared as `object` comes back as the stored object | A primitive or a `JsonElement`: whole numbers, enums and numbers without a fraction come back as `long`, other numbers as `double`, `Guid` and `TimeSpan` as `string`, collections as `JsonElement`. With the recommended settings, a class, struct or dictionary comes back as its stored `"$type"`. The full table is in [Serialization](configuration/serialization.md#rules-for-argument-types) |

See [Serialization](configuration/serialization.md).

## More examples

### Keep the old dashboard URL and authorization

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Dashboard;

app.MapNightShiftDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new LocalRequestsOnlyAuthorizationFilter() },
});
```

Bookmarks, health checks and keep-alive pings that use `/hangfire` keep working. Your existing authorization filters only need their `using` lines changed: `IDashboardAuthorizationFilter` and `DashboardContext` keep their names.

### Keep a custom schema name

```csharp
using Masterly.NightShift.SqlServer;

config.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
{
    SchemaName = "jobs",
});
```

Without `SchemaName`, the storage uses `HangFire`, the same default as before.

### Make an argument type readable by System.Text.Json

```csharp
using System.Text.Json.Serialization;

// Newtonsoft.Json could fill a private setter and pick a constructor on its own; System.Text.Json can't
public class InvoiceRequest
{
    [JsonConstructor]
    public InvoiceRequest(int customerId, decimal amount)
    {
        CustomerId = customerId;
        Amount = amount;
    }

    public int CustomerId { get; }

    public decimal Amount { get; }

    [JsonInclude]
    public string? Note { get; private set; }

    [JsonPropertyName("currency_code")] // was [JsonProperty("currency_code")] from Newtonsoft.Json
    public string Currency { get; set; } = "EUR";
}
```

Constructor parameters are matched to properties by name, ignoring case. Ship these changes in the same deploy as the migration, because jobs already in the database are read with the new serializer as soon as the new servers start. Test them against a few payloads copied from the `Arguments` column of the `HangFire.Job` table.

### Replace Newtonsoft serializer settings

```csharp
using System.Text.Json;

// Starting from the recommended options keeps the lenient reading of old data
config.UseRecommendedSerializerSettings(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Or your own options: data is written as they say, and still read leniently
config.UseSerializerSettings(new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
});
```

With your own options, the library adds its compatibility converters after yours (`$type` on states, `{"$type", "$values"}` arrays, `Type`, `CultureInfo`, `XElement`, enums as names or numbers). Your options decide how data is written; it is always read leniently (property names in any case, numbers from strings, public fields), so PascalCase arguments written by Hangfire still bind under a camelCase policy.

### Keep a custom type resolver working with old names

If you replaced the type resolver with `UseTypeResolver(...)`, the mapping of `Hangfire.*` names lives in the default resolver, so fall back to it:

```csharp
using Masterly.NightShift.Common;

config.UseTypeResolver(typeName =>
{
    if (typeName.StartsWith("MyCompany.Billing.OldInvoiceJob", StringComparison.Ordinal))
    {
        return typeof(InvoiceRequest);
    }

    // Falls back to the default resolver, which maps Hangfire.* type names to Masterly.NightShift.*
    return TypeHelper.DefaultTypeResolver(typeName);
});
```

`UseIgnoredAssemblyVersionTypeResolver()` uses the default resolver too, so the mapping works with it.

## Step-by-step checklist

1. **Back up the database.** Nothing in the migration rewrites existing rows, but the schema upgrade on start-up and the first runs of new servers write to it.
2. **Inventory your extensions.** List every `Hangfire.*` package other than the five in the table above. Each one needs a Masterly.NightShift build, a built-in replacement, or removal. A storage other than SQL Server must exist for Masterly.NightShift before you can migrate.
3. **Replace the packages.** Remove `Hangfire*` references, add the matching `Masterly.NightShift*` ones and a SQL Server driver. Remove `Newtonsoft.Json` if nothing else uses it.
4. **Rename in code.** Change `using Hangfire...` to `using Masterly.NightShift...`, and the `AddHangfire`/`AddHangfireServer`/`UseHangfireDashboard`/`MapHangfireDashboard` calls to their new names. Build and fix what the compiler reports, starting with the removed APIs.
5. **Keep the configuration.** Keep the same connection string, `SchemaName`, data compatibility level, type serializer and queue names. Change one thing at a time; the migration should not also change how data is written. A connection string *name* passed to `UseSqlServerStorage` (for example `UseSqlServerStorage("HangfireConnection")` with an `app.config` entry) is still looked up on .NET Framework; if the entry is missing, the storage now throws `ArgumentException` at startup.
6. **Port serializer settings.** Rewrite `UseSerializerSettings` and custom converters for System.Text.Json. Check every job argument type against [Serialization changes](#serialization-changes), and test deserializing real `Arguments` values from your database.
7. **Decide on the dashboard path.** Keep `/hangfire` by passing it to `MapNightShiftDashboard`, or move to `/nightshift` and update links, health checks, keep-alive URLs and reverse proxy rules.
8. **Update dashboard customizations.** Scripts that use `window.Hangfire` or the global `moment`, and custom pages or routes, need changes. New built-in routes may clash with paths you registered (see [Breaking changes](breaking-changes.md)).
9. **Test against a copy of production data.** Start the app on a restored copy. Check the log for the schema installation, open the dashboard, look at old succeeded, failed, scheduled and recurring jobs, trigger a recurring job, and requeue a failed job.
10. **Stop all Hangfire servers.** Before the first Masterly.NightShift server starts, stop every process running a Hangfire server against the database: web apps, workers and Windows Services. Ideally wait until nothing is in **Processing**; a job interrupted by the shutdown is not lost, it is picked up again by the new servers. Apps that only create jobs can be switched in the same deploy.
11. **Deploy and watch.** Start the new servers. On the **Servers** page, only Masterly.NightShift servers should be listed. Servers that stopped cleanly are removed at once; others are removed by the watchdog after `ServerTimeout` (5 minutes), checked every 5 minutes. Watch **Retries** and **Failed** for reasons that start with "Can not change the state to 'Processing':" or "Can not change the state to 'Enqueued':". The global `AutomaticRetry` filter retries such jobs, so they show under **Retries** first, with reasons like "Retry attempt 1 of 10: ...", and reach **Failed** only when the attempts are used up. "the job's type or method was not found" points to a type or method that can't be loaded, and "the job's arguments could not be deserialized" to an argument that can't be read; the exception details show the error.
12. **Raise the compatibility level later, if you want.** Once every server runs Masterly.NightShift, you can move to `CompatibilityLevel.Version_180` and the simple type serializer if you weren't using them (see [Compatibility levels](configuration/compatibility-levels.md)).

## Things to know

- **Rolling back.** New jobs are written with Masterly.NightShift type names in places where Hangfire wrote its own. If you go back to Hangfire, jobs created in the meantime that reference library types (cancellation tokens, `PerformContext`, continuations) won't load in Hangfire. Plan a rollback as "stop NightShift servers, let Hangfire process what it can, requeue or delete the rest".
- **Features that write new data** (fan-in continuations, paused recurring jobs, tags, batches, duplicate prevention) are only understood by Masterly.NightShift servers. That's another reason not to mix old and new servers.
- **Telemetry, Prometheus metrics, drain mode and keep-alive** are new; nothing in Hangfire maps to them. Turn them on after the migration has settled.
- **Global filter count.** `GlobalJobFilters.Filters` has more default filters than in Hangfire. Code or tests that assert the exact count need updating.
- **Dashboard preferences** saved in the browser (theme, watch list) start from their defaults.

## See also

- [Breaking changes](breaking-changes.md)
- [Installation](getting-started/installation.md)
- [Serialization](configuration/serialization.md)
- [Compatibility levels](configuration/compatibility-levels.md)
- [Schema and upgrades](storage/schema-and-upgrades.md)
- [Dashboard setup](dashboard/setup.md)
- [Troubleshooting](troubleshooting.md)
