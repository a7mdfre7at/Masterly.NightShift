# Changes from Hangfire

This page lists every difference from Hangfire 1.8.25, the release Masterly.NightShift 1.0 is based on, that code or operations built for Hangfire could notice. Each entry says what changed, why, and what to do. For a step-by-step migration, start with [Migrating from Hangfire](migrating-from-hangfire.md); for what was added, see [What's new](whats-new-next.md).

The public API part of this list was produced by two tools, so it is complete:

- **Package validation** against 1.8.25, which ran on every build and `dotnet pack` until the rename, with each intentional difference recorded in a `CompatibilitySuppressions.xml` file.
- **A one-time ApiCompat comparison** of the 1.8.25 .NET Framework builds against the new builds. Package validation can't show these differences, because the .NET Framework builds no longer exist.

There is no published Masterly.NightShift release yet, so package validation now checks only that each package's API agrees across its target frameworks.

| Section | Read it if |
|---|---|
| [Renamed from Hangfire](#renamed-from-hangfire) | You are moving code from Hangfire (everyone) |
| [JSON](#json-systemtextjson-instead-of-newtonsoftjson) | You configure serializer settings or pass complex job arguments |
| [Supported platforms](#supported-platforms) and [Removed packages](#removed-packages) | Your app runs on .NET Framework or .NET 7 or earlier, or uses OWIN or MSMQ |
| [Removed obsolete APIs](#removed-obsolete-apis) | Your code calls a member Hangfire marked obsolete |
| [Who is affected and what to use instead](#who-is-affected-and-what-to-use-instead) | Your code uses an API that was removed |
| [Nullable reference type annotations](#nullable-reference-type-annotations-source-level-only) | Your projects have `<Nullable>enable</Nullable>`, or you write a storage |
| [Behaviour changes](#behaviour-changes) | Your code compiles but you want to know what behaves differently |
| [Behaviour that Hangfire got wrong](#behaviour-that-hangfire-got-wrong) | Your code relied on a Hangfire bug or on a value Hangfire accepted |
| [Dependency changes](#dependency-changes) | You care about the packages your app pulls in |

## Renamed from Hangfire

Masterly.NightShift is renamed throughout. Code written for Hangfire needs these changes:

| Hangfire | Masterly.NightShift |
|---|---|
| Packages `Hangfire`, `Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.NetCore`, `Hangfire.AspNetCore` | `Masterly.NightShift`, `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer`, `Masterly.NightShift.NetCore`, `Masterly.NightShift.AspNetCore` (assemblies have the same names) |
| Namespaces `Hangfire`, `Hangfire.*` | `Masterly.NightShift`, `Masterly.NightShift.*` |
| `AddHangfire`, `AddHangfireServer`, `UseHangfireServer`, `UseHangfireDashboard`, `MapHangfireDashboard*`, `RegisterHangfireServer`, and the classes that hold them | `AddNightShift`, `AddNightShiftServer`, `UseNightShiftServer`, `UseNightShiftDashboard`, `MapNightShiftDashboard*`, `RegisterNightShiftServer`, and so on |
| `Hangfire.Common.CancellationTokenExtentions` (misspelled) | `Masterly.NightShift.Common.CancellationTokenExtensions` |
| Default dashboard path `/hangfire` | `/nightshift` |
| Default dashboard title "Hangfire Dashboard" | "Masterly.NightShift Dashboard" |
| Log categories `Hangfire.*` | `Masterly.NightShift.*` |
| Dashboard JavaScript global `window.Hangfire`, assets `hangfire.js`, `hangfire.css`, … | `window.NightShift`, `nightshift.js`, `nightshift.css`, … |

[Migrating from Hangfire](migrating-from-hangfire.md#renamed-members) has the full table.

Existing data keeps working. The SQL Server schema is still `HangFire`, the drain-mode key is still `hangfire:control`, and jobs stored by Hangfire, whose type names read `Hangfire.*, Hangfire.Core`, are mapped to the new types when they are loaded. Third-party storages and extensions built for Hangfire have to be recompiled against the new names. Saved dashboard preferences in the browser (theme, watch list) start from their defaults.

## JSON: System.Text.Json instead of Newtonsoft.Json

The library no longer depends on Newtonsoft.Json, so it has one JSON stack and no Newtonsoft.Json version conflicts. Settings and argument types written for Newtonsoft.Json need changes; data that Hangfire stored stays readable.

**Configuration:**

- `UseSerializerSettings(...)` takes `System.Text.Json.JsonSerializerOptions` instead of `JsonSerializerSettings`, and `UseRecommendedSerializerSettings(Action<...>)` configures a `JsonSerializerOptions` copy of the internal options.
- `JobHelper.SetSerializerSettings(JsonSerializerSettings)`, obsolete since 1.7, is removed.
- `TypeHelperSerializationBinder` is removed. It existed for Newtonsoft.Json and nothing uses it any more; resolve type names with `TypeHelper.CurrentTypeResolver` and `TypeHelper.CurrentTypeSerializer` instead.
- `JsonConvert.DefaultSettings` has no effect on anything any more.
- Options passed to `UseSerializerSettings(...)` decide how data is written; it is always read leniently (property names in any case, numbers from strings, public fields). A converter added in the `UseRecommendedSerializerSettings(Action<...>)` callback wins over the built-in one for the same type.

**Job argument types:**

- Properties need a public setter or `[JsonInclude]`, and a class without a public parameterless constructor needs a single public constructor or `[JsonConstructor]`. Two Newtonsoft.Json behaviours are kept when reading, with any options: a collection property without a setter gets the stored items added to the collection it holds, and a struct whose only public constructor sets its get-only properties is created through that constructor. A struct with several public constructors needs `[JsonConstructor]`.
- An argument declared as `object` is read back as a primitive or a `JsonElement`: whole numbers, enums and whole `double`s as `long`, other numbers (`decimal` too) as `double`, `Guid`, `TimeSpan` and `char` as `string`, ISO 8601 date text as `DateTime`, collections as `JsonElement`. Under the recommended settings, a value written as a JSON object (a class, a struct, a dictionary) keeps its `"$type"` and is read back as that type.
- An argument declared as an interface or abstract class is written with a leading `"$type"` naming its actual type, whatever serializer options are configured, and read back as that type. Hangfire needed `TypeNameHandling` for this.
- A `"$type"` names a type only as the first member of a value (after one `"$id"`, as Newtonsoft.Json writes it with `PreserveReferencesHandling`). A value of yours whose members include `"$type"` or `"$values"` (a dictionary key, a `JsonElement` property) is written wrapped, as `{"$type": <its type>, "$values": {<its members>}}`, and read back as itself, so the data in an argument can't choose the type it is read back as.
- `BigInteger`, `Rune`, `Index`, `Range`, `TimeZoneInfo` and types whose `TypeConverter` converts to and from text (such as `System.Drawing.Color`) are written so they read back: System.Text.Json wrote them as their read-only members, read back as their default value. `BigInteger` is a JSON number, as Hangfire wrote it. Creating a job with a struct argument that wouldn't read back (no member can be set or bound by a constructor, and the value isn't the type's default) throws `NotSupportedException`; a job read from storage is never refused.
- `[DefaultValue]` no longer leaves members out of job arguments and return values. The default and recommended options write every member, and set a member that is missing from the JSON to its `[DefaultValue]` when reading, so data that Hangfire's recommended settings (`DefaultValueHandling.IgnoreAndPopulate`) stored reads back as written. A member bound to a constructor parameter takes the parameter's default value instead. Options passed to `UseSerializerSettings(...)` don't apply `[DefaultValue]`, as System.Text.Json doesn't.
- `double.NaN`, `float.NaN` and the infinities are written as `"NaN"`, `"Infinity"` and `"-Infinity"` by the default and recommended options, as Hangfire did, and read from those strings whatever options are configured.

**Library data written with your options:**

- Below `Version_170`, the job payload is written with the internal options, so `Type`, `Method`, `ParameterTypes`, `Arguments` and `Queue` keep these names whatever your options say (a snake_case or kebab-case naming policy, `IgnoreReadOnlyProperties`). Payloads stored with renamed members are still read.
- Library data that the options passed to `UseSerializerSettings(...)` can't write (System.Text.Json throws `NotSupportedException`, as a source-generated context does for types it doesn't list) is written with the internal options instead of failing: below `Version_170` the job's argument list, parameter types, SQL Server state data, continuation lists and job console entries, and at every level the library's own types stored as user data, such as a deleted job's `ExceptionInfo`. Your own argument and return value types are not rerouted.

**Stored data stays readable:** states saved with a `"$type"`, arguments declared as an interface or abstract class saved with a `"$type"`, arrays and other collections saved as `{"$type", "$values"}` and dictionaries saved with a leading `"$type"` naming their type (also as members and items, read as the declared type), quoted numbers, enum names and property names in any case all read as before, whatever serializer options are configured. The one exception: options passed to `UseSerializerSettings(...)` with a `ReferenceHandler` leave collections and arrays to System.Text.Json, so cycles and shared references work as in `JsonSerializer`, and collections saved as `{"$type", "$values"}` aren't read with those options. A dictionary entry `"$type"` that doesn't name a fitting dictionary type is kept as a key.

**What to do:** port your settings and check your argument types as shown in [Migrating from Hangfire](migrating-from-hangfire.md#serialization-changes). Details are in [Serialization](configuration/serialization.md).

## Supported platforms

| | 1.8.25 | 1.0 |
|---|---|---|
| Targets | `net451`, `net46`/`net461`, `netstandard1.3`, `netstandard2.0`, `netstandard2.1`, `netcoreapp3.0` | `net8.0`, `net10.0` |
| .NET Framework | 4.5.1+ | **Not supported** |
| .NET Core 3.0 to .NET 7 | Supported | **Not supported** |
| .NET 8 and later | Supported (through `netstandard2.0`/`netcoreapp3.0`) | Supported |
| Classic ASP.NET / OWIN | Supported | **Removed** |
| Strong-named assemblies | No | No (unchanged) |

**What to do:** move the app to .NET 8 or later before you switch, or keep it on Hangfire 1.8. Data written by Hangfire on .NET Framework stays readable: type names written as `mscorlib` are mapped to the .NET types. Stop every Hangfire server before Masterly.NightShift servers start against the same database (see [Migrating from Hangfire](migrating-from-hangfire.md#what-can-break)).

## Removed packages

- **Hangfire.SqlServer.MSMQ** has no Masterly.NightShift equivalent. Use the default SQL Server queue.
- The **Masterly.NightShift** meta-package doesn't pull in `Microsoft.Owin.Host.SystemWeb`, which the **Hangfire** meta-package did.

## Who is affected and what to use instead

### OWIN and classic ASP.NET (Core)

All of these were OWIN-only or marked obsolete in 1.8.

| Removed | Use instead |
|---|---|
| `AppBuilderExtensions` (Hangfire's `UseHangfireServer()` and `UseHangfireDashboard()` for the OWIN `IAppBuilder`), `ServerOwinExtensions`, `DashboardOwinExtensions`, `Dashboard.MiddlewareExtensions` | ASP.NET Core: `services.AddNightShiftServer()` and `app.MapNightShiftDashboard()` (the `IApplicationBuilder` extensions `app.UseNightShiftDashboard()` and `app.UseNightShiftServer(() => server)` still exist). Other apps: `new BackgroundJobServer()` |
| `OwinBootstrapper`, `IBootstrapperConfiguration`, `BootstrapperConfigurationExtensions` | `GlobalConfiguration.Configuration` |
| `Dashboard.OwinDashboardContext`, `OwinDashboardContextExtensions`, `Dashboard.Owin.IOwinDashboardAntiforgery` | `Masterly.NightShift.AspNetCore` |
| `Dashboard.IAuthorizationFilter`, `DashboardOptions.AuthorizationFilters`, `LocalRequestsOnlyAuthorizationFilter.Authorize(IDictionary<string, object>)` | `IDashboardAuthorizationFilter` and `DashboardOptions.Authorization` |
| `Dashboard.IRequestDispatcher`, `RequestDispatcherContext`, `RequestDispatcherWrapper`, `RouteCollection.Add(string, IRequestDispatcher)` | `IDashboardDispatcher` and `RouteCollection.Add(string, IDashboardDispatcher)` |
| `RouteCollectionExtensions.AddCommand` / `AddBatchCommand` overloads that take `RequestDispatcherContext` | The overloads that take `DashboardContext` |
| `UrlHelper(IDictionary<string, object>)` | `UrlHelper(DashboardContext)` |
| `Cron.GetDescription(string)` (the .NET Framework builds only, through the `CronExpressionDescriptor` package) | `Cron.Describe(string)`, which throws `FormatException` for an invalid expression, or `Cron.TryDescribe(string)`, which returns `null`. |

The dashboard shows plain-English descriptions of cron expressions on every platform now, from `Cron.TryDescribe`. Hangfire showed them on .NET Framework only. The wording differs from `CronExpressionDescriptor`'s, and it is always English.

### SQL Server storage

| Removed | Use instead |
|---|---|
| `SqlServerBootstrapperConfigurationExtensions` | `GlobalConfiguration.Configuration.UseSqlServerStorage(...)` |
| `SqlServerStorageOptions.TransactionIsolationLevel` (both the `System.Transactions` and the `System.Data` versions) and `UseRecommendedIsolationLevel` | Nothing: storage transactions always run as `READ COMMITTED`, as with the default options |
| `SqlServerStorageOptions.TransactionTimeout`, `DisableTransactionScope`, `UsePageLocksOnDequeue` | Nothing: they had no effect |
| Support for `System.Data.SqlClient` | `Microsoft.Data.SqlClient`, which `Masterly.NightShift.SqlServer` now references, so remove your own driver reference. A custom `SqlClientFactory` must create `Microsoft.Data.SqlClient` connections or wrap them |
| `IPersistentJobQueue.Enqueue(IDbConnection, string, string)` | `Enqueue(DbConnection, DbTransaction, string, string)`, the signature every other platform already used |
| `SqlServerDistributedLock` (public class) | `IStorageConnection.AcquireDistributedLock`. The class is internal now |
| `SqlServerObjectsInstaller.RequiredSchemaVersion` | `SqlServerObjectsInstaller.LatestSchemaVersion` |

The `IPersistentJobQueue` change affects third-party queue providers built for .NET Framework. They must implement the new signature.

### Removed obsolete APIs

Every member Hangfire 1.8 marked obsolete is removed. Most were thin wrappers; use the replacement shown.

| Removed | Use instead |
|---|---|
| `BackgroundJob.ContinueWith`, `IBackgroundJobClient.ContinueWith` (extension methods) | `ContinueJobWith` with the same arguments |
| `RecurringJob.AddOrUpdate` and `IRecurringJobManager.AddOrUpdate` overloads without a recurring job ID, or with trailing `TimeZoneInfo timeZone` / `string queue` parameters | The overloads that take the ID first, with `RecurringJobOptions { TimeZone = ... }` and the `queue` parameter |
| `RecurringJob.Trigger`, `RecurringJobManager.TriggerExecution` | `RecurringJob.TriggerJob`, `IRecurringJobManager.TriggerJob` |
| `BackgroundJobServer.Start()`, `Stop()`, `Stop(bool)` | A server starts when it is created; `SendStop()` asks it to stop, `Dispose()` stops it and waits |
| `BackgroundJobServerOptions.ServerWatchdogOptions`, `ServerWatchdogOptions` | `BackgroundJobServerOptions.ServerTimeout` and `ServerCheckInterval` |
| `JobStorage.GetComponents()`, `IServerComponent`, `IServerProcess`, `JobStorageFeatures.ProcessesInsteadOfComponents` | `JobStorage.GetStorageWideProcesses()` and `GetServerRequiredProcesses()` with `IBackgroundProcess`; servers always call them now |
| `JobActivator.BeginScope()` (without arguments), `JobActivatorScope.InnerScope` | `BeginScope(JobActivatorContext)` |
| `Job(Type, MethodInfo, string[])`, `Job.Arguments`, `Job.Perform(...)` | `Job(Type, MethodInfo, object[])`, `Job.Args`, `CoreBackgroundJobPerformer` |
| `InvocationData.Serialize(Job)`, `Deserialize()`, `SetTypeSerializer`, `SetTypeResolver` | `InvocationData.SerializeJob(Job)`, `DeserializeJob()`, `UseTypeSerializer`, `UseTypeResolver` |
| `JobHelper.ToJson`, `FromJson` | `SerializationHelper.Serialize` and `Deserialize` with a `SerializationOption` |
| `PerformContext.JobId`, `Job`, `CreatedAt`, and the `PerformContext` constructor without a `JobStorage` | `PerformContext.BackgroundJob.Id`, `.Job`, `.CreatedAt`, and the constructor that takes the storage first |
| `CreatedContext.JobId`, `CreatedContext.SetJobParameter` | `CreatedContext.BackgroundJob.Id`; set parameters in `OnCreating` |
| `StateContext` (the base class of `ElectStateContext` and `ApplyStateContext`) and its `JobId`, `Job`, `CreatedAt` | `context.BackgroundJob` |
| `BackgroundProcessContext.CancellationToken`, `IsShutdownRequested`, and its constructor without the stopping, stopped and shutdown tokens | `StoppingToken`, `IsStopping` |
| `CreateJobFailedException` | `BackgroundJobClientException`, which now derives from `Exception` |
| `StateHandlerCollection`, `Worker.TransactionalAcknowledgePrefix` | Nothing: they weren't used |
| `JobHistoryRenderer.AddForegroundStateColor`, `AddBackgroundStateColor`, `HtmlHelper.FormatProperties` | `JobHistoryRenderer.AddStateCssSuffix` |
| `RouteCollectionExtensions.AddRecurringBatchCommand(string, Action<RecurringJobManager, string>)` | The overload that takes `Action<IRecurringJobManager, string>` |
| `app.UseNightShiftServer(options, ...)` (the overloads that create the server) | `services.AddNightShiftServer(...)` |
| The serialization constructors of the exception types (`SerializationInfo`, `StreamingContext`) | Nothing: binary serialization of exceptions isn't supported on .NET 8 |

## Dashboard: moment.js is no longer bundled

The dashboard formats dates with the browser's built-in `Intl` APIs instead of moment.js, which is 81 KB smaller and supports every locale the browser knows. Chart.js uses a matching date adapter, `Dashboard/Content/js/nightshift-intl.js`.

- **Extensions that use the global `moment` must bundle it themselves**, for example with `DashboardRoutes.AddJavaScript`.
- The `data-moment`, `data-moment-title` and `data-moment-local` attributes still work, so extensions that render them need no changes. They hold Unix timestamps in seconds.
- Relative dates use `Intl.RelativeTimeFormat`, so ±1 day now reads "yesterday" or "tomorrow" instead of "a day ago" or "in a day".

## Dashboard: Bootstrap 5, jQuery 4 and Chart.js 4

The dashboard bundles Bootstrap 5.3.9 (`bootstrap.bundle.min.js`, with Popper), jQuery 4.0.0, Chart.js 4.6.1 and an SVG sprite of Bootstrap Icons 1.13.2. Hangfire bundled Bootstrap 3.4, jQuery 3.7, Chart.js 2.9 with chartjs-plugin-streaming, and the Glyphicons font. Pages and partials that only use the dashboard's own helpers and classes need no changes. **What to do:** move dashboard extensions that use the libraries directly.

- **Bootstrap 3 markup is gone.** The pages use Bootstrap 5 classes and `data-bs-*` attributes (`badge` for `label`, `btn-outline-secondary` for `btn-default`, `card` for `panel`, `float-end` for `pull-right`, `d-none d-md-block` for `hidden-xs`, and so on). `label-state-*` stays and now has the colors of its suffix (Hangfire's and earlier builds' badges were gray), and the old `page-header` and `dl-horizontal` classes still style extension pages. `Html.ServerId` writes a `badge`.
- **Glyphicons are gone**, and so are the `/fonts/glyphicons-halflings-regular/{eot,svg,ttf,woff,woff2}` routes. Icons come from one SVG sprite served at `/icons{n}`: write them with `Html.Icon(name)` or `Html.Icon(name, cssClass)` in a page and `NightShift.icon(name, cssClass)` in a script, which resolve names the same way (a Glyphicon name of the dashboard gives the icon that replaced it; a name the sprite doesn't hold draws nothing). The sprite holds only the icons the dashboard uses (listed in `Dashboard/Content/icons/icons.svg`); an extension that needs another one draws its own SVG.
- **`MenuItem.Icon` takes a Bootstrap Icons name** (`house-door-fill`), not a Glyphicon name. The Glyphicon names the dashboard used, with or without the `glyphicon-` prefix, still map to their replacements, and any other name gets the chevron.
- **jQuery 4 and Bootstrap 5 scripts.** Bootstrap 3's `$.fn.button('loading'/'reset')` no longer exists: use `NightShift.setLoading(button, true/false)`. A `data-ajax` button without `data-loading-text` keeps its content while it works. Use `NightShift.tooltips(container)` for tooltips on content added later, `NightShift.setTitle(element, title)` to change one, and `NightShift.disposeTooltips(container)` before content with tooltips is replaced or removed: Bootstrap 5 keeps its tooltips outside jQuery's data and shows them in `<body>`, so without it the old elements stay in memory and a tooltip shown at that moment stays on the screen. Bootstrap 5 still provides `$.fn.modal` and `$.fn.tooltip` while jQuery is loaded. jQuery 4 removed `$.trim`, `$.isArray`, `$.isFunction`, `$.type`, `$.now`, `$.parseJSON` and other deprecated helpers.
- **Chart.js 4.** The global `Chart` is Chart.js 4.6.1. Charts written for Chart.js 2 move to its options (`scales.x`/`scales.y`, `grid`, `plugins.legend`, `plugins.tooltip`, `Chart.defaults.font` instead of `Chart.defaults.global`). chartjs-plugin-streaming isn't bundled any more: the `realtime` scale and `plugins.streaming` options are gone. `NightShift.RealtimeGraph`, `NightShift.HistoryGraph` and the `nightshift:theme` event keep their shape; their internal `_chart` is a Chart.js 4 instance.
- **`rem` sizes are larger.** The root font size is 14px; Bootstrap 3 set it to 10px, so `1rem` in an extension's stylesheet grows from 10px to 14px. **What to do:** divide such sizes by 1.4, or use `px` or `em`.
- **`NightShiftIntl.startOf(t, 'isoWeek', weekday)`** returns the start of that day, as the moment adapter of Chart.js does; it used to keep the time of day.
- **Embedded resources renamed or removed:** `jquery-3.7.1.min.js` became `jquery-4.0.0.min.js`, `bootstrap.min.js` became `bootstrap.bundle.min.js` and `Chart.min.js` became `chart.umd.min.js`; `chartjs-plugin-streaming.min.js`, `Chart.min.css` and the Glyphicons fonts are gone. They were served through the `/js{n}` and `/css{n}` bundles, whose URLs don't change.

## Nullable reference type annotations (source-level only)

The whole public API carries C# nullable annotations. The parts where they matter most:

- **Client:** `BackgroundJob`, `IBackgroundJobClient`, `BackgroundJobClient`, `BackgroundJobClientExtensions`, `RecurringJob`, `IRecurringJobManager`, `RecurringJobManager`, `RecurringJobManagerExtensions` and `RecurringJobOptions`.
- **Server:** `BackgroundJobServer` and `BackgroundJobServerOptions`.
- **Filters:** the client, server and state filter interfaces and their contexts (`CreateContext`, `CreatingContext`, `CreatedContext`, `PerformContext`, `PerformingContext`, `PerformedContext`, `ElectStateContext`, `ApplyStateContext` and the exception contexts).
- **States:** `IState` and the built-in states.
- **Storage:** `JobStorage`, `IStorageConnection`, `JobStorageConnection`, `IWriteOnlyTransaction`, `JobStorageTransaction`, `IFetchedJob`, `JobData` and `StateData`.
- **Monitoring:** `IMonitoringApi`, `JobStorageMonitor` and the DTOs in `Masterly.NightShift.Storage.Monitoring`.
- **Hosting:** `JobActivator` (`ActivateJob` returns `object?`), `JobActivatorScope` (`Resolve` returns `object?`, `Current` is `JobActivatorScope?`) and `JobActivatorContext` (`GetJobParameter<T>` returns `T?`, `SetJobParameter` takes `object?`).
- **Scheduling:** `Cron` (`TryDescribe(string?)` returns `string?`; `TryFromText(string?, out string?)`).
- **Retries:** `AutomaticRetryAttribute.DelaysInSeconds`, `OnlyOn` and `ExceptOn` are nullable.
- **SQL Server:** `UseSqlServerStorage` and `SqlServerStorageOptions` (`ImpersonationFunc` and `DefaultQueueProvider` are nullable). Passing `configuration.GetConnectionString(...)` warns, because the method throws for `null`.
- **ASP.NET Core and DI:** `AddNightShift`, `AddNightShiftServer`, `UseNightShiftDashboard`, `UseNightShiftServer`, `MapNightShiftDashboard*` and `AddNightShiftKeepAlive`; optional `options`, `storage`, `additionalProcesses` and `configure` parameters are nullable, and `GetInternalServices` has nullable `out` parameters.

- **Interfaces that can return nothing:** `IBackgroundJobFactory.Create`, `IBackgroundJobPerformer.Perform` and `IBackgroundJobStateChanger.ChangeState` return nullable types, because filters can cancel the work. `IJobFilterProvider.GetFilters` takes `Job?`, because a job may fail to load.
- **Dashboard:** `DashboardRequest.GetQuery`, `LocalIpAddress` and `RemoteIpAddress`, and `DashboardResponse.ContentType`, are nullable.
- **`InvocationData`:** `parameterTypes`, `arguments` and `queue` are nullable.

Compiled code is unaffected. Projects with `<Nullable>enable</Nullable>` may see new warnings:

- **Job IDs are `string?`.** All `Enqueue`, `Schedule`, `ContinueJobWith` and `Create` methods return `null` when creation doesn't happen: a client filter cancels it, an exception filter handles a failure, or the storage returns no ID. Check the result before passing it on, for example to `ContinueJobWith`.
- **`RecurringJob.TriggerJob` and `RecurringJobManager.TriggerJob` return `string?`.** They return `null` when the recurring job doesn't exist or wasn't triggered.
- **Optional parameters that accept `null` are marked as nullable**, for example `fromState` and `timeZone`.
- **In filters:** `CreatedContext.BackgroundJob` is `null` when creation was canceled. `PerformedContext.Exception` and `Result` can be `null`. `GetJobParameter<T>` returns `T?` (`default` when the parameter is missing).
- **In states:** `IState.Reason` and every built-in state's `Reason` are `string?`, as are `FailedState.ServerId` and `DeletedState.ExceptionInfo`. `BackgroundJobServerOptions.ServerName` is `string?`, where `null` means the machine name is used.

### For storage implementers

These annotations are based on what Masterly.NightShift passes in and what the SQL Server storage returns. With nullable checking on, you may see these warnings:

| Member | Annotation | Why |
|---|---|---|
| `CreateExpiredJob` / `JobStorageTransaction.CreateJob` | `parameters` is `IDictionary<string, string?>`, returns `string?` | A filter can set a parameter to `null`, which serializes to `null`. Masterly.NightShift treats a `null` or empty ID as "not created". |
| `SetJobParameter` | `value` is `string?` | Same reason. |
| `GetJobParameter`, `GetJobData`, `GetStateData`, `GetFirstByLowestScoreFromSet`, `GetAllEntriesFromHash`, `GetValueFromHash` | Return nullable | Return `null` when nothing is found. |
| Hash, set and list values | Non-null | Masterly.NightShift never writes `null` values, and writes `String.Empty` for optional recurring job fields. |
| `JobData.Job`, `State`, `InvocationData`, `ParametersSnapshot`, `LoadException` | Nullable | `Job` is `null` when it can't be loaded. The others are optional or newer members. |
| `StateData.Name`, `StateData.Data` | Non-null | Always set when a storage creates a `StateData`. |

**Monitoring API:**

- `JobDetails` returns `JobDetailsDto?` for an unknown job.
- Job lists are `JobList<XxxJobDto?>`, because an entry is `null` when its job expires between queries. The dashboard already handles this.
- Job DTO fields that depend on loading the job or on its current state are nullable: `Job`, `LoadException`, `InvocationData`, `StateData`, `Reason`, `Result`, `ExceptionType`, `ExceptionMessage`, `ExceptionDetails`, `ServerId`, `State` and `ParentStateName`.
- Fields the dashboard uses without checks stay non-null, and storages must set them: `JobDetailsDto.Properties` and `History`, `ServerDto.Name` and `Queues`, `StateHistoryDto.StateName` and `Data`, `QueueWithTopEnqueuedJobsDto.Name` and `FirstJobs`.

Override signatures that use non-nullable types where the contract is nullable get `CS8609`/`CS8610`/`CS8765` warnings. These point to places where Masterly.NightShift can pass `null`.

## Types that became records

`BulkJobResult`, `JobStateInfo`, `BackgroundServerContext` and `JobDetailsRendererDto` are records, so they compare by value and support `with` and deconstruction. Their constructor parameters are named after their properties: write `new BulkJobResult(1, 0, Complete: true)`, not `complete: true`.

## Behaviour changes

Code that compiles against Masterly.NightShift can still behave differently. The sections below are grouped by area.

### Job execution

- **Job methods are called through invokers.** A `JobInvoker` (generated at compile time by the analyzer that ships inside `Masterly.NightShift.Core`, or compiled at runtime) replaces `MethodInfo.Invoke`. Exceptions thrown by a job are no longer wrapped in `TargetInvocationException` before Masterly.NightShift handles them; `JobPerformanceException.InnerException` is the job's own exception, as before. A filter that unwrapped `TargetInvocationException` itself needs a look. To go back to reflection, call `UseCompiledJobInvokers(false)` and set the MSBuild property `NightShiftDisableJobInvokerGeneration` to `true`. See [Source-generated invokers](jobs/source-generated-invokers.md).
- **A job with several queue entries runs once.** A job deleted or failed while it was enqueued and then requeued, or requeued twice, has more than one entry in its queues. Hangfire runs it once per entry, at the same time. Here a worker that takes the job from another entry while it is Processing removes that entry (see [A job with several queue entries](server/reliability.md#a-job-with-several-queue-entries)). The `Processing` state's data has a new `QueueEntry` entry. The dashboard's **Requeue** on a job's page is hidden for an Enqueued job and requeues only from the state the job is in.
- **`JobActivator.BeginScope(PerformContext)` throws `ArgumentNullException` for `null`** (it threw `NullReferenceException`).
- **The services `AddNightShift` registers resolve their dependencies with `GetRequiredService`.** A dependency removed, or replaced with a factory that returns `null`, gives `InvalidOperationException` at resolution instead of a `null` that failed later.

### Default global filters

`GlobalJobFilters.Filters` holds ten more filters than in Hangfire, besides Hangfire's four (`CaptureCultureAttribute`, `AutomaticRetryAttribute`, `StatisticsHistoryAttribute`, `ContinuationsSupportAttribute`). `JobChainStepFilter` is registered first, before `CaptureCultureAttribute`. For a job that doesn't use their feature, they only read (job parameters, and the drain-mode switch, which is cached) and write nothing:

| Filter | Does |
|---|---|
| `JobChainStepFilter` | Lets a step of a [chain](jobs/chains.md#when-a-step-runs) run only when its chain's record lists it and doesn't say that the chain is being created, retried or replaced: otherwise the step waits (`WaitingReasonFormat`, `UnreadableReasonFormat`) or is deleted (`NotCurrentReasonFormat`, `NotRecordedReasonFormat`). The first step of a chain that is still being created is kept by its worker, which looks again with nothing stored, and a step held by a retry that was never recorded is put back into the state it was in. A step it holds in the Scheduled state goes back to its queue through `DrainModeFilter`. It also keeps the record for as long as the chain has steps to run, and a step of a recorded chain that finishes writes what it finished as into the hash `chain-outcomes:{id}` in its own state transaction. Only for jobs that carry the `ChainId` job parameter, which it looks for in the parameters loaded with the job, with one read when a worker picks a job up, and one when a job finishes, on a storage that doesn't load them |
| `TerminateJobFilter` | Deletes a job that threw `TerminateJobException` as skipped or canceled, instead of failing it |
| `LateStartFilter` | Skips a run later than its `SkipAfter` job parameter, only for jobs that carry it; it reads the parameters loaded with the job, never the storage |
| `SchedulePolicyFilter` | Applies the schedule policy of a job created with `ScheduleOptions.Policy`, at creation and when the delayed job scheduler enqueues it. For other jobs it looks for the `Policy` job parameter when a delayed job is due: in the parameters loaded with the job on SQL Server, with one read on a storage that doesn't load them. A job whose policy can't be evaluated waits in the Scheduled state and counts the failed checks in the `PolicyCheckAttempts` job parameter (`SchedulePolicyFilter.CheckAttemptsParameter`), which is set to `0` once the policy lets the job run and removed when the policy moves or skips it |
| `FanInContinuationsSupportAttribute` | Drives `ContinueJobWithAll`/`ContinueJobWithAny` |
| `JobConsoleStateFilter` | Expires a job's console lists (`job:{id}:console`, `job:{id}:checkpoints`) together with the job, only for jobs whose console wrote to storage and only on storages with `ExtendedApi` |
| `DrainModeFilter` | Holds jobs while processing is paused from the dashboard or with `DrainMode` |
| `ActivityFeedFilter` | Does nothing until `UseActivityFeed()` turns the activity feed on |
| `BatchSupportAttribute` | Keeps batch counters, only for jobs that carry the `BatchId` job parameter |
| `StaleJobFilter` | Counts the restarts of a job picked up again after its server stopped (`StaleRestarts` parameter), records the worker that took it over (`Lease`), and fails it past `UseStaleRestartLimit` (no limit by default). Writes nothing for a job that wasn't picked up again |

`ContinuationsSupportAttribute` also acts when a job fails for good or starts processing, and `FanInContinuationsSupportAttribute` when it fails for good, to start the continuations that ask for it (`OnlyOnFailedState`, `OnlyOnProcessingState`, the failure joins). For a job without continuations they read the parameters loaded with the job: nothing more on SQL Server, one read on a storage that doesn't load them.

**What to do:** update code or tests that assert the exact number of global filters. Code that removes or replaces filters by type is not affected.

### New storage keys and job parameters

New features store their data in the existing primitives, under new keys. Most are written only when you use the feature. These are written without opting in:

| Key or field | Written when |
|---|---|
| `audit` list | Someone acts through the dashboard (requeue, delete, trigger...), unless `AuditLog.Enabled` is `false` |
| `stats:{state}:yyyy-MM-dd-HH` counters | Every job finishes, as before, but `StatisticsHistoryAttribute` now keeps them for eight days instead of one (168 per state instead of 24), so the Insights heatmap can show a week |
| `hangfire:control` hash | Processing, a queue or a server is paused. Every write takes the lock `lock:hangfire:control`, and a resume removes the field it ends |
| `recurring-job:{id}:runs` list | A recurring job creates a run, by its schedule or `Trigger`: the run's job ID, the last 50 kept, written in the same transaction as the run. Removed with the recurring job |
| `fan-in:{childId}` hash, `fan-in:unconfirmed` set, `FanInOutcomes` job parameter | A fan-in continuation (`ContinueJobWithAll`, `ContinueJobWithAny`, `ContinueJobWithJoin`, or a join in a chain) waits: each parent that finishes writes the state it finished in, one field per parent, in the transaction of its state change, so the child goes by it once the parent's job has expired. The child's `FanInOutcomes` parameter names the hash (`FanInContinuationsSupportAttribute.OutcomesParameterName`). The hash is removed when the child is decided. `fan-in:unconfirmed` lists each child, for about half an hour from before it registers on its parents, so the chain maintenance removes the hash of a child whose creation failed |
| `ParentId` / `ParentIds` job parameters | A continuation (`ContinueJobWith`) or fan-in continuation is created: its parents, read by `PerformContext.ParentId` and `ParentIds`. Written with the job, in the same command |
| `chain:{id}` and `chain-outcomes:{id}` hashes, `chains`, `chains:cleanup`, `chains:due`, `chains:expiring`, `chains:lost` and `chains:soon` sets, `chains:index`, `chains:retentions` and `recurring-chain:{id}` hashes, `ChainId`, `ChainStep`, `ChainRecorded` and `ChainRetry` job parameters | A `JobChain` is created, or a recurring chain runs. The record is started before the first job is created (not listed in `chains` yet, and kept for the chain retention should the creation stop, counted from the time a scheduled first step is due). It is kept while the job of a step exists in a state other than Succeeded or Deleted, and for the chain retention after that (`JobChain.RecordRetention`, 30 days unless `UseChainRetention` sets another time). Each step's job carries `ChainId` and `ChainStep` from its creation, and `ChainRecorded`, whose value is the writer's retention in seconds, once a record lists it: written in the record's transaction where the storage sets job parameters in a transaction, as every storage of this project does, and right after it, one write per step, on a storage that doesn't. A recurring chain's identifier is derived from the job that creates it, so that job creates its chain once. While a chain is created, retried or replaced, the `changing` entry of its `Jobs` field holds the client's lease. Its `Cleanup` field and the `chains:cleanup` set hold the jobs an interrupted `RetryChainFrom` or `ReplaceChain` left to delete. For the server that maintains the chains, `chains:due` holds when each chain is looked at next, `chains:expiring` when a finished chain leaves `chains`, `chains:lost` when a chain whose record the storage lost leaves it, `chains:retentions` the chain retention each process uses, `chains:soon` the looks asked for by a step that left the state it had finished in, and `chains:index` how far the chains listed by an earlier build have been taken in; the `chains:joins` set of earlier builds is removed. A step that `RetryChainFrom` holds carries `ChainRetry`, what the retry started from, so a server can put the step back when the retry is abandoned. `chain-outcomes:{id}` holds what each step finished as, one field per job, written in the step's own state transaction and kept as long as the record, so a chain can be retried from a branch after the job of another branch has expired; it is removed with the chain. Locks: `lock:chain:{id}` while a chain changes, `lock:chains:maintenance` while a server maintains the chains |
| `ScheduledFor` job parameter | A job is created in the Scheduled state (`Schedule`): the moment it is meant to run, read by `PerformContext.ScheduledFor`. It is written with the job, in the same command, so it costs no round trip |
| Recurring job hash fields `Paused`, `ResumedAt`, `ScheduleChangedAt`, `StartAt`, `EndAt` | A recurring job is paused, resumed or rescheduled, or has start and end dates |
| `ServerHold` job parameter | A server paused on its own or fenced holds a job a worker had already fetched (`DrainModeFilter.ServerHoldParameter`): the last 16 holds, as Unix times in milliseconds (`start-end;start~latest`, each counting until its held state was due plus 15 seconds at most), whose time after the moment the job was meant to run `PerformContext.Lateness` leaves out |
| `StaleRestarts` and `Lease` job parameters | A job is picked up again after its server stopped while it ran. A worker's move out of Processing is refused while the lease names another worker. Both are written in the transaction of the state change where the storage sets job parameters in a transaction |
| `monitor:{name}` hash | A [job monitor](monitoring/job-monitors.md) is registered: the outcomes of its method's jobs and its state |
| `nightshift:deployment` hash | `UseDeploymentFencing` is on and a server checks its deployment: the fields `Current`, `Seen:{id}` (a value that changes at every check) and, for the deployment order of `UseDeploymentFencing(deploymentId, deploymentOrder)`, `Order:{id}` (the highest order the id was started with, read by `DeploymentFencing.GetCurrentDeploymentOrder`; `DeploymentFencing.DeploymentOrder` is the one this process was given). The fields of a deployment gone for more than seven days are removed |
| `schedule-policy:{name}` hash, `schedule-policies` set | A schedule policy is saved (`SaveSchedulePolicy`, `RecurringJobSet.AddPolicy`); the `Policy` hash field of a recurring job and the `Policy` and `WhenBlocked` job parameters name it, and the `PolicyCheckAttempts` job parameter counts the checks of a job that waits for a policy that can't be evaluated (`0` once the policy let it run). A run of a recurring job that was due while the recurring job waited for its policy carries the policy's name in the `WaitedForPolicy` job parameter (`SkipIfLateAttribute.WaitedForPolicyParameter`) |

`RecurringJobDto` has four new properties to match: `Paused`, `MisfireHandling`, `StartAt` and `EndAt`. The keys each feature writes are listed on its page and in the [architecture blueprint](architecture/blueprints/nightshift-next.md). Keys built from user input are bounded to fit SQL Server's limits (100 characters for keys, 256 for set values).

### New dashboard routes

The dashboard registers these routes in addition to Hangfire's. **What to do:** if a dashboard extension registered the same path, move it.

| Area | Routes |
|---|---|
| Jobs | `/jobs/live`, `/jobs/export/{id}`, `/jobs/notes/add/{id}`, `/jobs/{scheduled,processing,succeeded,failed,awaiting}/delete-all`, `/jobs/enqueued/delete-all/{queue}`, `/jobs/failed/requeue-all`, `/jobs/{failed,succeeded,scheduled,processing,deleted}/export`, `/jobs/enqueued/export/{queue}`, `/retries/delete-all` |
| Recurring jobs | `/recurring/pause`, `/recurring/resume`, `/recurring/update/{id}`, `/recurring/preview`, `/recurring/remove-all`, `/recurring/forecast`, `/recurring/export`, `/recurring/import`, `/recurring/details/{id}`, `/recurring/trigger-one/{id}`, `/recurring/pause-one/{id}`, `/recurring/resume-one/{id}` |
| Batches | `/batches`, `/batches/{id}`, `/batches/requeue-failed/{id}`, `/batches/delete/{id}` |
| Chains and policies | `/chains`, `/chains/{id}`, `/chains/retry/{jobId}`, `/chains/delete/{id}`, `/policies`, `/policies/save`, `/policies/delete/{name}/{revision}` |
| Queues and servers | `/queues/pause/{queue}`, `/queues/resume/{queue}`, `/servers/pause/{id}`, `/servers/resume/{id}`, `/maintenance/pause`, `/maintenance/resume`, `/maintenance/clear-jobs`, `/maintenance/reset` |
| Pages | `/insights`, `/insights/circuits/reset/{resource}`, `/search`, `/activity`, `/audit`, `/catalog`, `/catalog/run`, `/tags`, `/tags/{tag}`, `/tags/forget/{tag}`, `/tags/forget-expired/{tag}`, `/wallboard`, `/timeline`, `/about` |
| Monitoring | `/health`, `/metrics`, `/ping` |
| Static files | `/js-intl{n}` (the date formatter that replaces moment.js), `/icons{n}` (the icon sprite) |

The export routes are registered before the list pages whose patterns would also match them.

### Dashboard

- **Requests without antiforgery.** Without `IAntiforgery` registered (and `IgnoreAntiforgeryToken` false), a POST must carry `X-Requested-With` and come from the dashboard's own origin (same scheme, host and port; `X-Forwarded-Host` is ignored), or it gets `403`. Hangfire skipped the check silently. **What to do:** scripts that post to dashboard URLs send that header; behind a proxy that rewrites `Host`, register `AddAntiforgery()`. See [Dashboard authorization](dashboard/authorization.md#antiforgery).
- **Framing.** Every dashboard response has `X-Frame-Options: SAMEORIGIN` and `Content-Security-Policy: frame-ancestors 'self'` unless already set, so another site can't frame the dashboard. **What to do:** to embed it elsewhere, set those headers yourself before the dashboard runs.
- **Pages show at most 1000 rows**, and a form over the server's limits answers `413`. Route values decode only `%2F`.
- **`GET /stats` answers `405`.** The dashboard posts to it.
- **Commands posted with `jobs[]`** (such as **Trigger now**) answer `422 {error}` instead of `500` when a job's type can't be loaded in the dashboard's process, and still handle the other ids.
- **Storages without `JobStorageFeatures.ExtendedApi`** show a "not supported" message on the Retries and Awaiting pages (and on the new Insights page), and delete-all on retries and awaiting jobs answers `422`.
- **`MapNightShiftDashboardWithNoAuthorizationFilters`** uses the `DashboardOptions` registered in DI and no longer changes the instance it is given.
- **The Servers page marks a server Possibly aborted** as soon as its last heartbeat is older than `DashboardOptions.ServerPossiblyAbortedThreshold` (1 minute by default), like the job pages. Hangfire waited for twice the threshold and showed no mark in between.
- **Dashboard actions are audited.** `DashboardContext` has a new virtual method, `GetUserName()`, returning `null` by default; the ASP.NET Core context overrides it. Each action is written to the `audit` list and to the log at Info (category `Masterly.NightShift.AuditLog`) with the user name and IP. Set `AuditLog.Enabled = false` to turn this off. See [Audit log and activity feed](monitoring/audit-log-and-activity.md).

### Recurring jobs

- **`Strict` misfire handling** creates at most 100 runs per job per scheduler pass, and doesn't replay runs from before a schedule change (new hash field `ScheduleChangedAt`, written only when the schedule's meaning changes).
- **When creating or enqueuing a run fails,** the job's `LastExecution`/`LastJobId` stay at the last run that was enqueued, so the retry pass creates the failed run and the ones after it. `Strict` used to lose them, and `Relaxed` lost its run.
- **A recurring job whose data can't be read** is retried and then disabled without stopping the scheduler for the other jobs.

### Filters

- **`[DisableConcurrentExecution]` lock names longer than 200 characters** are shortened to their first 167 characters, `~` and 32 hexadecimal digits of their SHA-256. SQL Server cut such names, so two resources sharing their first ~245 characters got the same lock. Shorter names don't change.
- **A storage error while releasing a `[DisableConcurrentExecution]` lock** is logged as a warning instead of failing a job that has already run.
- **`AutomaticRetryAttribute` treats `RetryLaterException` specially.** This only matters if you throw the new exception: see [Retries](filters/retries.md).
- **`AutomaticRetryAttribute` never retries a job that threw `TerminateJobException`.** This only matters if you throw the new exception: see [TerminateJobException](filters/retries.md#terminatejobexception).

### Server and hosting

- **An empty string argument throws `ArgumentException`,** as in .NET; `null` still throws `ArgumentNullException`. In Hangfire both threw `ArgumentNullException`. This affects `ProcessingState`, the `resource` of `AcquireDistributedLock`, and the names given to `CreatingContext`, `JobActivatorContext` and the job console.
- **Interval options must be positive.** `BackgroundJobServerOptions.HeartbeatInterval`, `ServerCheckInterval` and `ServerTimeout` no longer accept `TimeSpan.Zero`, and `CancellationCheckInterval` is now validated: all four must be greater than zero (`CancellationCheckInterval` at most `Int32.MaxValue` milliseconds), otherwise `ArgumentOutOfRangeException`.
- **`JobStorage.JobExpirationTimeout`** (and `WithJobExpirationTimeout`) accepts at most 36,500 days (`ExpireAfterAttribute.MaxDays`) and throws `ArgumentOutOfRangeException` above it. A longer value (such as `TimeSpan.MaxValue`) used to be accepted and then made every final-state commit on SQL Server throw, leaving jobs in Processing.
- **Servers added with `AddNightShiftServer` run each job inside `ILogger.BeginScope`** with `JobId`, `JobName`, `RecurringJobId` and `RetryCount`, so providers that print scopes show them. The scope stays open until the worker has changed the job's state, so retry and final-failure entries carry `JobId` too. Turn it off with `services.ConfigureNightShiftJobLogging(o => o.BeginScope = false)`. See [Logging](monitoring/logging.md#job-logging-scope).

### SQL Server storage

- **Connection string names are refused.** `UseSqlServerStorage(string)` and `new SqlServerStorage(string, ...)` take a connection string only. A text without any `key=value` pair, such as a connection string name, throws `ArgumentException` at once; Hangfire on .NET Framework looked it up in `app.config`/`web.config`, and on other platforms took it for a connection string and failed at its first use. **What to do:** read the connection string with `IConfiguration.GetConnectionString` and pass the value.
- **Writes for a job id that isn't a number** (`SetJobParameter`, `ExpireJob`, `PersistJob`, `SetJobState`, `AddJobState`, `AddToQueue`) do nothing instead of throwing `FormatException`, matching the reads, which return `null`.
- **Distributed lock names longer than 255 characters** (schema name, colon and resource) are shortened to a 238-character prefix, `~` and a 16-digit hash instead of being cut at 255 by SqlClient, so two long names that start alike are no longer the same lock.
- **`GetRangeFromSet`** orders members with the same score by value (it used to leave their order unspecified), and reads ranges near the end of long sets from the end.
- **Storage connections never join an ambient `TransactionScope`.** In Hangfire, a job created inside a `TransactionScope` joined it and disappeared when the scope rolled back. Here it exists when the call returns and stays, whatever the scope does, and there is no switch like `DisableTransactionScope` to change that. To create a job with your data in one transaction, use the [Entity Framework Core storage](storage/entity-framework-core.md#jobs-that-commit-with-your-data) and stage the job on your context. Storage transactions run at `READ COMMITTED` even on a pooled session left at another isolation level.
- **Expirations are capped.** Any expiration the storage writes past December 31, 9999 is stored as that day, within the `datetime` range.

### Storage contract

For authors of storages in other packages:

- **`JobStorage.Purge(StoragePurgeOptions)`** is a new virtual method that throws `NotSupportedException` unless a storage overrides it and reports `JobStorageFeatures.Purge`. Storages that opt in should honour all four options (`KeepRecurringJobs`, `KeepServers`, `KeepAuditLog`, `KeepDrainMode`), which default to `true`.
- **A disposed connection lets go of its distributed locks,** also of those whose handles weren't disposed, as SQL Server does when a session closes; Core's filters rely on it, and the conformance suite checks it (`DistributedLock_WhoseHandleWasNotDisposed_IsReleased_WhenItsConnectionIs`).
- **New features use the existing primitives** (hashes, sets, lists, counters, job parameters), and check `JobStorage.HasFeature(...)` before optional members. Without `JobStorageFeatures.ExtendedApi`, features that need it degrade (see [Custom storage](storage/custom-storage.md)).
- **Drain mode removes the field of a resumed queue or server** from the `hangfire:control` hash: a resume rewrites the hash in one transaction (`RemoveHash`, then `SetRangeInHash`), and every write to it takes the lock `lock:hangfire:control` (waiting at most 15 seconds), so no write is lost to a rewrite. The `[resumed]` markers of earlier builds are still read as resumed, and removed by the next resume.

## Behaviour that Hangfire got wrong

These changes fix behaviour inherited from Hangfire 1.8.25. Code that relied on the old behaviour needs a look.

- **Failure reason when a job can't be loaded.** The Failed state's reason says what failed: `Can not change the state to '<state>': the job's type or method was not found.`, `... the job's arguments could not be deserialized.`, or `... the job could not be loaded.` when a storage reports the failure itself; the error is in the exception details. Hangfire always wrote `... target method was not found.`, so monitoring that matches that text needs updating. The `JobLoadException` messages say the same.
- **Overloaded job methods.** A job runs the overload whose parameter types are exactly the stored ones. Hangfire took the first overload whose parameters accepted them, so a job created for `Handle(Derived)` could run `Handle(Base)` when that overload was declared first. When no overload matches exactly (the method changed after the job was stored), the first accepting overload is still used.
- **`SqlServerStorageOptions.SchemaName`** throws `ArgumentException` for `null`, empty or white space, for names longer than 128 characters, and for names containing `]`, `'` or a control character. Hangfire checked the previous value, so a bad value failed later, and it pasted the name into the install script unescaped, so `]` or `'` broke the install. `SqlServerObjectsInstaller.GetInstallScript`/`Install` check the name the same way.
- **`SlidingInvisibilityTimeout`, `CommandTimeout`, `CommandBatchMaxTimeout`** throw `ArgumentOutOfRangeException` under 1 second. Hangfire rounded them down to 0: duplicate fetches, or no timeout at all.
- **`SqlServerStorageOptions.CountersAggregateInterval`** throws `ArgumentException` for zero or a negative value (the aggregator would run in a tight loop) and `ArgumentOutOfRangeException` above `int.MaxValue` milliseconds.
- **Over-long values in the SQL Server storage** throw `ArgumentException` naming the limit when written: keys and hash fields over 100 characters, set values over 256 (including `RemoveFromSet`), job parameter names over 40, state names over 20, queue names over 50 and server ids over 200 (`AnnounceServer`; a server id is `ServerName` plus up to 48 characters, so keep `ServerName` to 150 characters or fewer). Hangfire failed inside SQL Server or silently cut them, so later lookups missed.
- **List items inherit the list's expiration** (`InsertToList`), taken from the newest item of the list. Hangfire's items added to an expiring list never expired.
- **`JobDetails` on SQL Server** returns `null` for a non-numeric id instead of throwing `SqlException`.
- **Startup install** waits 1 s and 3 s between attempts, and retries any exception, not only `DbException`: a connection factory that throws while its token service isn't ready no longer stops the constructor. After a failed open it clears the SQL client's connection pool for that connection string, so the retries reach the server instead of failing at once during the client's pool blocking period (5 s or more after a failed open). When the install still fails, it is retried when a connection is opened or the schema version is read: first after 30 s, then, while the database answers but the install fails, doubling up to 30 minutes, with a warning each time; while no connection can be opened, every 30 s. A schema version that couldn't be read at startup is read again when first needed (at most every 30 s).
- **Schema names outside the database's code page** (for example `Задания` on a Latin1 database) install and work. Hangfire's install script compared the name with non-Unicode literals, so the install failed and rolled back on every attempt.
- **Servers listening to 64 queues or more** fetch normally. Hangfire waited on one handle per queue plus one, and `WaitHandle.WaitAny` threw `NotSupportedException` on every idle poll, so the worker backed off for up to 5 minutes. A job enqueued in the same process wakes a worker at once only for its first 63 queues; jobs in the others are found on the next poll.
- **`InactiveStateExpirationTimeout` on a schema older than 9** skips the state history cleanup with one warning when auto-detection found the version. Hangfire ran the cleanup anyway, logged error 8622 at every check and deleted nothing.
- **The expiration manager's lock** is `{SchemaName}:locks:expirationmanager` instead of `locks:expirationmanager`, like the other locks, so storages with different schemas in one database clean up independently.
- **Schema auto-detection respects your settings.** `TryAutoDetectSchemaDependentOptions` no longer overrides `DisableGlobalLocks`, `UseIgnoreDupKeyOption` or `DeleteExpiredBatchSize` when you set them, and no longer writes the detected values into the options object you passed. If you set `DisableGlobalLocks = false` explicitly, you now get what you asked for.
- **`RecurringJobManager.AddOrUpdate`** throws `ArgumentOutOfRangeException` for a `MisfireHandlingMode` value that isn't defined. Hangfire stored it, after which the recurring job scheduler failed on every pass, for every recurring job.
- **`AutomaticRetryAttribute.DelaysInSeconds`** throws `ArgumentException` for an empty array (Hangfire threw `ArgumentNullException`). `null` still means "use `DelayInSecondsByAttemptFunc`".
- **`[Queue]`** validates a plain queue name when the attribute is read, so a method with `[Queue("Billing")]` fails as soon as a job is created for it. A name with placeholders is checked after formatting, and an invalid result throws `InvalidOperationException` naming the template and the result.
- **`LocalRequestsOnlyAuthorizationFilter`** refuses requests that carry `Forwarded`, `X-Forwarded-For`, `X-Forwarded-Host` or `X-Real-IP`, because behind a proxy on the same machine every request looks local. A new virtual `DashboardRequest.GetHeader` lets adapters expose headers. **What to do:** a dashboard behind a proxy needs real authorization (see [Dashboard authorization](dashboard/authorization.md)).
- **Dashboard CSRF protection without antiforgery.** Hangfire's ASP.NET Core middleware skipped CSRF validation silently when `IAntiforgery` wasn't registered. The dashboard now requires a same-origin request (scheme, host and port) with `X-Requested-With` in that case. A form antiforgery can't read because it is too large answers `413`; other antiforgery errors answer `403`.
- **Dashboard batch commands** (the buttons that act on selected jobs) answer `405` to anything but `POST`, like the single-job commands.
- **Dashboard state colours.** `JobHistoryRenderer.GetStateCssSuffix` returns `null` instead of `"inherit"` for a state with no suffix. Registering a suffix again for the same state replaces it instead of throwing. Hangfire's `AddForegroundStateColor`/`AddBackgroundStateColor` are removed: give a custom state a CSS suffix with `AddStateCssSuffix`, which also gives it that suffix's colors in the lineage diagram and the time-in-state bar. The parameter of `AddStateCssSuffix` is renamed from `color` to `cssSuffix`, which breaks callers that pass it by name.
- **A job's DI scope is disposed with `DisposeAsync`.** Hangfire's `netstandard2.0` build called `Dispose`, which Microsoft.Extensions.DependencyInjection rejects when a scoped service only implements `IAsyncDisposable` (Azure's `ServiceBusClient`, for example): the job failed after it had run, and was retried.
- **Logging.** Every Masterly.NightShift logger follows the provider set with `UseLogProvider` or a `Use...LogProvider()` method, including loggers created before the call. `Masterly.NightShift.Core` has a module initializer for this. Calling LibLog's `LogProvider.SetCurrentLogProvider` directly still affects only loggers created after the call.

- **A job run on the worker thread** (`TaskScheduler = null`) whose token is canceled, by a shutdown or by the dashboard deleting it, no longer hangs its worker. Hangfire stopped running the job's continuations when the token fired, and then waited for a task that needed one of them: the job stayed invisible and Processing until the process exited.
- **A server that fails to start one of its dispatchers** stops the ones it had started before it tries again. Hangfire left them running, with a server id it had removed, at every retry.
- **`TriggerJob`** reads the storage's clock, as the recurring job scheduler does, and never moves a recurring job's `LastExecution` back. Triggered from a process whose clock was behind the database's, a `Strict` or `Ignorable` job ran a slot it had already run.
- **SQL Server schema names containing `{` or `}`** work. SQL containing the schema name went through `string.Format`, so the expiration manager and the dashboard's job lists failed on every call.
- **Lock timeouts in the SQL Server expiration manager** are recognised by SQL Server's error number (1222), not by the English message, so they are no longer logged as errors under another login language.
- **`InactiveStateExpirationTimeout`** is applied in seconds, rounded up. In whole minutes, a value under a minute deleted every state but the current one at once, and a value past `dateadd`'s range failed every run.

## Dependency changes

| Package | Hangfire 1.8.25 | Masterly.NightShift 1.0 |
|---|---|---|
| Core | Newtonsoft.Json; Cronos merged into the assembly; CronExpressionDescriptor and Owin on .NET Framework | Cronos as a package dependency; no Newtonsoft.Json (System.Text.Json is part of .NET) |
| SqlServer | Dapper merged into the assembly; no SQL client (the app chose one) | Dapper and Microsoft.Data.SqlClient as package dependencies, and a patched System.Security.Cryptography.Pkcs |
| NetCore | Microsoft.Extensions `*.Abstractions` 2.0 (`netstandard2.0`) or 3.0 (`netstandard2.1`) | 8.0.x on `net8.0`, 10.0.x on `net10.0` |
| AspNetCore | `Microsoft.AspNetCore.Http.Abstractions` and `Antiforgery` 2.0 on `netstandard2.0` | The ASP.NET Core shared framework |

Cronos and Dapper are no longer hidden inside the assemblies, so NuGet unifies them with any other version your app references. The exact versions are in [`Directory.Packages.props`](../Directory.Packages.props).

## After 1.0.0 ships

Set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to `1.0.0`. From then on, `dotnet pack` fails on any API change that breaks code built against 1.0. Record each intentional one in a `CompatibilitySuppressions.xml` file next to the project (`dotnet pack -p:GenerateCompatibilitySuppressionFile=true` writes it) and list it in `CHANGELOG.md`.
