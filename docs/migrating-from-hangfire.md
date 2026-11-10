# Migrating from Hangfire

Masterly.NightShift is based on Hangfire 1.8.25 and renamed throughout. This page is for moving an app, and its production data, from Hangfire to Masterly.NightShift. It explains what you change in your code, what stays the same in your database, what can break, and ends with a step-by-step checklist.

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

Point it at the same connection string, and your existing jobs, recurring jobs and history show up and keep running.

## How it works

### What changes: names

Package IDs, namespaces and every member with `Hangfire` in its name were renamed. Names without `Hangfire` in them are unchanged: `BackgroundJob`, `IBackgroundJobClient`, `RecurringJob`, `Cron`, `AutomaticRetryAttribute`, `QueueAttribute`, `BackgroundJobServerOptions`, `SqlServerStorageOptions`, `DashboardOptions`, `IDashboardAuthorizationFilter`, the states and the filter interfaces all keep their names. For most apps, the code change is a find-and-replace of package references, `using` lines and a few extension methods.

Logger names follow the namespaces, so log categories start with `Masterly.NightShift` instead of `Hangfire`. Update log filters in `appsettings.json` and in your logging backend (see [Keep your log filters working](#keep-your-log-filters-working)).

### What stays the same: your data

The storage format is the same, so a database written by Hangfire is read as it is:

- **The SQL Server schema is still named `HangFire`**, and the tables and indexes are the same. If you set `SqlServerStorageOptions.SchemaName` before, set it to the same value now.
- **The schema version is still 9.** A database used by Hangfire 1.8 is already at version 9, so nothing is changed on start-up. A database from an older Hangfire version is upgraded step by step on start-up, as Hangfire did; see [Schema and upgrades](storage/schema-and-upgrades.md) for the heavy steps.
- **Stored type names are mapped.** Hangfire stored the names of its own types in job data, for example the parameter type `Hangfire.IJobCancellationToken, Hangfire.Core` of a job that takes a cancellation token, or `Hangfire.States.EnqueuedState, Hangfire.Core` as the next state of a continuation. When such a name is loaded, the type resolver maps the assemblies `Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.NetCore` and `Hangfire.AspNetCore` to `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer`, `Masterly.NightShift.NetCore` and `Masterly.NightShift.AspNetCore`, and the `Hangfire.` namespace prefix to `Masterly.NightShift.`. The assembly version and public key token in the stored name are dropped. Your own job types are stored under your own names and are not touched.
- **Storage keys keep their names.** Lists, sets and hashes are named as before, and a few keys that new features use carry the old name too, such as the drain-mode hash `hangfire:control`. Seeing `hangfire` in the data is expected.

Written data is not migrated in place: new jobs are written with the new type names, and old jobs are mapped every time they are read.

### What can break

- **JSON is System.Text.Json, not Newtonsoft.Json.** Stored payloads stay readable, but your job argument types now have to suit System.Text.Json, and custom serializer settings must be rewritten (see [Serialization changes](#serialization-changes)).
- **Extensions built for Hangfire don't load.** Storages for Redis, PostgreSQL or MongoDB, dashboard extensions, health check and OpenTelemetry packages and filter libraries all reference `Hangfire.Core` and must be recompiled against Masterly.NightShift. Data they stored under their own type names (for example `Hangfire.Console` or `Hangfire.Pro` types) is not mapped. Several of them have a built-in counterpart now (see [Hangfire extensions](#hangfire-extensions)).
- **Old and new servers don't mix.** A Hangfire server can't load jobs whose stored data names Masterly.NightShift types, such as a job that takes a cancellation token or a continuation's next state, and it doesn't know the new state data and job parameters. No compatibility level changes that. Stop every Hangfire server before Masterly.NightShift servers start against the same database. The rolling upgrade in [Compatibility levels](configuration/compatibility-levels.md#rolling-upgrade-old-and-new-servers-side-by-side) is for moving between Masterly.NightShift versions.
- **.NET 8 or later only.** The packages target `net8.0` and `net10.0`. Apps on .NET Framework or .NET 7 or earlier must move to .NET 8 first, or stay on Hangfire.
- **Removed APIs.** OWIN and classic ASP.NET integration, the MSMQ queue package, `System.Data.SqlClient` support and every API Hangfire marked obsolete are gone (see [Removed features](#removed-features)).
- **Some behaviour changed.** The ones you are most likely to notice are listed in [Behaviour you may notice](#behaviour-you-may-notice). The full list is in [Changes from Hangfire](changes-from-hangfire.md).

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

Assembly names are the same as the package IDs. `Masterly.NightShift.SqlServer` brings its SQL Server driver, `Microsoft.Data.SqlClient`, so remove your own `System.Data.SqlClient` or `Microsoft.Data.SqlClient` reference unless other code needs it (see [Installation](getting-started/installation.md#the-sql-server-driver)).

Dependencies changed too. Newtonsoft.Json is no longer one. Cronos and Dapper, which Hangfire merged into its own assemblies, are ordinary package dependencies now, so NuGet unifies them with any other version your app references. The full list is in [Changes from Hangfire](changes-from-hangfire.md#dependency-changes).

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

As in Hangfire, the extension methods `UseSqlServerStorage`, `AddNightShift`, `AddNightShiftServer`, `UseNightShiftDashboard` and `MapNightShiftDashboard` are in the root `Masterly.NightShift` namespace.

### Renamed members

| Hangfire | Masterly.NightShift |
|---|---|
| `services.AddHangfire(...)` | `services.AddNightShift(...)` |
| `services.AddHangfireServer(...)` | `services.AddNightShiftServer(...)` |
| `app.UseHangfireDashboard(...)` | `app.UseNightShiftDashboard(...)` |
| `endpoints.MapHangfireDashboard(...)` | `endpoints.MapNightShiftDashboard(...)` |
| `endpoints.MapHangfireDashboardWithAuthorizationPolicy(...)` | `endpoints.MapNightShiftDashboardWithAuthorizationPolicy(...)` |
| `endpoints.MapHangfireDashboardWithNoAuthorizationFilters(...)` | `endpoints.MapNightShiftDashboardWithNoAuthorizationFilters(...)` |
| `app.UseHangfireServer(...)` (obsolete) | `services.AddNightShiftServer(...)`. The overloads that created the server are removed; `app.UseNightShiftServer(Func<IBackgroundProcessingServer>)`, which runs a server you create, remains |
| `serviceProvider.RegisterHangfireServer(server)` | `serviceProvider.RegisterNightShiftServer(server)` |
| Classes `HangfireServiceCollectionExtensions`, `HangfireApplicationBuilderExtensions`, `HangfireEndpointRouteBuilderExtensions` | `NightShiftServiceCollectionExtensions`, `NightShiftApplicationBuilderExtensions`, `NightShiftEndpointRouteBuilderExtensions` |
| `Hangfire.Common.CancellationTokenExtentions` (misspelled) | `Masterly.NightShift.Common.CancellationTokenExtensions` |

The `IGlobalConfiguration` methods (`UseSqlServerStorage`, `UseSerializerSettings`, `UseFilter`, `UseActivator`, `UseLogProvider` and the others) keep their names, because they never had `Hangfire` in them.

### Other visible changes

| Hangfire | Masterly.NightShift |
|---|---|
| Default dashboard path `/hangfire` | `/nightshift` |
| Default dashboard title "Hangfire Dashboard" | "Masterly.NightShift Dashboard" (`DashboardOptions.DashboardTitle`) |
| Log categories `Hangfire.*` (for example `Hangfire.Server.Worker`) | `Masterly.NightShift.*` (`Masterly.NightShift.Server.Worker`) |
| Dashboard JavaScript global `window.Hangfire`, assets `hangfire.js`, `hangfire.css` | `window.NightShift`, `nightshift.js`, `nightshift.css` |
| Bundled moment.js in the dashboard | Not bundled; dates use the browser's `Intl` APIs. Extensions that use the global `moment` must bring it themselves |
| `UseSerializerSettings(JsonSerializerSettings)` | `UseSerializerSettings(JsonSerializerOptions)` |
| `UseRecommendedSerializerSettings(Action<JsonSerializerSettings>)` | `UseRecommendedSerializerSettings(Action<JsonSerializerOptions>)` |
| Job methods invoked with `MethodInfo.Invoke` | Invoked through generated or compiled invokers; a job's exception is no longer wrapped in `TargetInvocationException` |
| Failure reason "Can not change the state to '...': target method was not found." | Says what failed: "the job's type or method was not found.", "the job's arguments could not be deserialized." or "the job could not be loaded." |
| Browser-saved dashboard preferences | Start from defaults |

### Removed features

| Removed | Use instead |
|---|---|
| OWIN and classic ASP.NET: `app.UseHangfireServer()`/`app.UseHangfireDashboard()` on `IAppBuilder`, `OwinBootstrapper`, `DashboardOwinExtensions` | ASP.NET Core with `AddNightShiftServer()` and `MapNightShiftDashboard()`, or `new BackgroundJobServer()` in any app plus a separate ASP.NET Core app for the dashboard |
| .NET Framework, .NET Core 3.x, .NET 5 to 7 | .NET 8 or later |
| `Dashboard.IAuthorizationFilter`, `DashboardOptions.AuthorizationFilters` | `IDashboardAuthorizationFilter` and `DashboardOptions.Authorization` |
| `IRequestDispatcher`, `RequestDispatcherContext` | `IDashboardDispatcher` and `DashboardContext` |
| `SqlServerStorageOptions.TransactionIsolationLevel`, `UseRecommendedIsolationLevel`, `TransactionTimeout`, `DisableTransactionScope`, `UsePageLocksOnDequeue` | Nothing: transactions always run as `READ COMMITTED`, and the others had no effect |
| `System.Data.SqlClient` | `Microsoft.Data.SqlClient`, which the storage references |
| Members Hangfire marked obsolete (`ContinueWith`, recurring job overloads without an ID, `BackgroundJobServer.Stop()`, `JobStorage.GetComponents()`, `PerformContext.JobId` and others) | See [Removed obsolete APIs](changes-from-hangfire.md#removed-obsolete-apis) for each replacement |
| `JobHelper.SetSerializerSettings(...)` | `UseSerializerSettings(...)` |
| `TypeHelperSerializationBinder` | `TypeHelper.CurrentTypeResolver` and `TypeHelper.CurrentTypeSerializer` |
| `Cron.GetDescription(string)` (.NET Framework only) | `Cron.Describe(string)` or `Cron.TryDescribe(string)`; the wording differs and is always English |
| `Hangfire.SqlServer.MSMQ` | The default SQL Server queue |

### Hangfire extensions

Packages built for Hangfire don't load. Some have a built-in counterpart; the APIs are similar in purpose, not source-compatible, and data stored by the extension is not converted.

| Extension | Built-in counterpart |
|---|---|
| Hangfire.Console (`WriteLine`, progress bars) | `IJobConsole` as a job parameter or `PerformContext.Console` (see [Progress, console and checkpoints](jobs/progress-and-console.md)) |
| Hangfire.Tags | `[Tag]` and `JobTags.Add` (see [Tags and notes](jobs/tags-and-notes.md)) |
| Hangfire.Pro batches | `BatchJob.StartNew` and `BatchJob.ContinueWith` (see [Fan-in and batches](jobs/fan-in-and-batches.md)) |
| Hangfire.Throttling (mutexes, semaphores, rate limiters) | `[DisableConcurrentExecution]`, `[Throttle]`, `[RateLimit]` (see [Concurrency filters](filters/concurrency.md)) |
| OpenTelemetry.Instrumentation.Hangfire | `UseTelemetry()` (see [OpenTelemetry](monitoring/opentelemetry.md)) |
| AspNetCore.HealthChecks.Hangfire | `AddHealthChecks().AddNightShift()` (see [Health checks and probes](monitoring/health-checks.md)) |
| Dashboard extensions that start jobs or edit recurring jobs | The job catalog, and Pause, Resume and Edit on the Recurring Jobs page (see [Job catalog](jobs/job-catalog.md) and [Pausing and updating](recurring-jobs/pausing-and-updating.md)) |
| Storages other than SQL Server (Redis, PostgreSQL, MongoDB, in-memory) | None. The storage must be ported to Masterly.NightShift (see [Custom storage](storage/custom-storage.md)) |

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

See [Serialization](configuration/serialization.md) for the details.

### Behaviour you may notice

These changes affect code or operations that worked with Hangfire. Each is explained in [Changes from Hangfire](changes-from-hangfire.md).

- **A dashboard behind a reverse proxy refuses requests** when it relies on the default `LocalRequestsOnlyAuthorizationFilter`: requests with `X-Forwarded-For` and similar headers are no longer treated as local. Configure real authorization (see [Dashboard authorization](dashboard/authorization.md)).
- **Scripts that POST to dashboard URLs** need an `X-Requested-With` header and the dashboard's own origin when antiforgery isn't registered.
- **The dashboard can't be framed by another site.** Responses carry `X-Frame-Options: SAMEORIGIN` unless you set the header yourself.
- **Monitoring that matches the failure reason "target method was not found"** must match the new reasons (see [Other visible changes](#other-visible-changes)).
- **Over-long keys and names throw.** The SQL Server storage throws `ArgumentException` for keys over 100 characters, queue names over 50 and similar, where Hangfire failed inside SQL Server or cut the value.
- **The exact overload runs.** A job created for `Handle(Derived)` no longer runs `Handle(Base)` when that overload is declared first.
- **More default global filters.** `GlobalJobFilters.Filters` holds five more filters than in Hangfire; tests that assert the exact count need updating.

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

### Keep your log filters working

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Masterly.NightShift": "Warning"
    }
  }
}
```

The entry used to be `"Hangfire": "Warning"`. Do the same for category filters in Serilog, NLog or your logging backend, and for alerts that match logger names. See [Logging](monitoring/logging.md).

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

Do the code changes first, test them against a copy of your data, and switch the servers over last.

1. **Inventory your extensions.** List every `Hangfire.*` package other than the five in [Packages](#packages), and other packages that reference `Hangfire.Core`. Each one needs a Masterly.NightShift build, a [built-in counterpart](#hangfire-extensions), or removal. A storage other than SQL Server must exist for Masterly.NightShift before you can migrate.
2. **Move to .NET 8 or later, then replace the packages.** Remove the `Hangfire*` references and add the matching `Masterly.NightShift*` ones; the SQL Server storage brings its driver. Remove `Newtonsoft.Json` if nothing else uses it.
3. **Rename in code.** Change `using Hangfire...` to `using Masterly.NightShift...`, and the calls in [Renamed members](#renamed-members). Build, and fix what the compiler reports, starting with the [removed APIs](#removed-features).
4. **Keep the configuration.** Keep the same connection string, `SchemaName`, data compatibility level, type serializer and queue names. Change one thing at a time: the migration should not also change how data is written. A connection string *name* passed to `UseSqlServerStorage` (for example `UseSqlServerStorage("HangfireConnection")`) now throws `ArgumentException` at start-up: read the connection string with `IConfiguration.GetConnectionString` and pass it.
5. **Port serializer settings.** Rewrite `UseSerializerSettings` and custom converters for System.Text.Json. Check every job argument type against [Serialization changes](#serialization-changes), and test deserializing real `Arguments` values from your database.
6. **Update log filters.** Change `Hangfire` categories to `Masterly.NightShift` (see [Keep your log filters working](#keep-your-log-filters-working)).
7. **Decide on the dashboard path.** Keep `/hangfire` by passing it to `MapNightShiftDashboard`, or move to `/nightshift` and update links, health checks, keep-alive URLs and reverse proxy rules.
8. **Update dashboard customizations.** Scripts that use `window.Hangfire` or the global `moment`, and custom pages or routes, need changes. New built-in routes may clash with paths you registered (see [Changes from Hangfire](changes-from-hangfire.md#new-dashboard-routes)). Check the dashboard's authorization behind your proxy (see [Behaviour you may notice](#behaviour-you-may-notice)).
9. **Test against a copy of production data.** Start the app on a restored copy. Check the log for the schema installation, open the dashboard, look at old succeeded, failed, scheduled and recurring jobs, trigger a recurring job, and requeue a failed job.
10. **Back up the database.** Nothing in the migration rewrites existing rows, but new servers write to it from their first minute.
11. **Stop all Hangfire servers.** Before the first Masterly.NightShift server starts, stop every process that runs a Hangfire server against the database: web apps, workers and Windows Services. Ideally wait until nothing is in **Processing**; a job interrupted by the shutdown is not lost, the new servers pick it up again. Apps that only create jobs can be switched in the same deploy.
12. **Deploy and watch.** Start the new servers. On the **Servers** page, only Masterly.NightShift servers should be listed. Servers that stopped cleanly are removed at once; others are removed by the watchdog after `ServerTimeout` (5 minutes), checked every 5 minutes. Watch **Retries** and **Failed** for reasons that start with "Can not change the state to 'Processing':" or "Can not change the state to 'Enqueued':". The global `AutomaticRetry` filter retries such jobs, so they show under **Retries** first, with reasons like "Retry attempt 1 of 10: ...", and reach **Failed** only when the attempts are used up. "the job's type or method was not found" points to a type or method that can't be loaded, and "the job's arguments could not be deserialized" to an argument that can't be read; the exception details show the error.
13. **Raise the compatibility level later, if you want.** Once every server runs Masterly.NightShift, you can move to `CompatibilityLevel.Version_180` and the simple type serializer if you weren't using them (see [Compatibility levels](configuration/compatibility-levels.md)).

## Things to know

- **Rolling back.** New jobs are written with Masterly.NightShift type names in places where Hangfire wrote its own. If you go back to Hangfire, jobs created in the meantime that reference library types (cancellation tokens, `PerformContext`, continuations) won't load in Hangfire. Plan a rollback as "stop the Masterly.NightShift servers, let Hangfire process what it can, requeue or delete the rest".
- **Features that write new data** (fan-in continuations, paused recurring jobs, tags, batches, duplicate prevention) are only understood by Masterly.NightShift servers. That's another reason not to mix old and new servers.
- **New features are opt-in.** Telemetry, insights, the activity feed, alerts, Prometheus metrics, drain mode and keep-alive have no Hangfire equivalent. Turn them on after the migration has settled; [What's new](whats-new-next.md) lists them.
- **Dashboard preferences** saved in the browser (theme, watch list) start from their defaults.

## See also

- [Changes from Hangfire](changes-from-hangfire.md)
- [What's new](whats-new-next.md)
- [Installation](getting-started/installation.md)
- [Serialization](configuration/serialization.md)
- [Compatibility levels](configuration/compatibility-levels.md)
- [Schema and upgrades](storage/schema-and-upgrades.md)
- [Dashboard setup](dashboard/setup.md)
- [Troubleshooting](troubleshooting.md)
