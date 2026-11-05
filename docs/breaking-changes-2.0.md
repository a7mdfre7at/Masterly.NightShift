# Breaking changes in Masterly.NightShift 2.0

This list compares 2.0 with the last 1.8 release (1.8.25). It was produced by two tools, so it is complete for public APIs:

- **Package validation** against 1.8.25, which ran on every `build` and `dotnet pack` until the rename, with each intentional break recorded in a `CompatibilitySuppressions.xml` file.
- **A one-time ApiCompat comparison** of the 1.8.25 .NET Framework builds against the new `netstandard2.0` builds. Package validation can't show these differences, because the .NET Framework builds no longer exist.

## Renamed from Hangfire

Masterly.NightShift 2.0 is based on Hangfire 1.8.25, which the rest of this list compares it with, and is renamed throughout. Code written for Hangfire needs these changes:

| Hangfire | Masterly.NightShift |
|---|---|
| Packages `Hangfire`, `Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.NetCore`, `Hangfire.AspNetCore` | `Masterly.NightShift`, `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer`, `Masterly.NightShift.NetCore`, `Masterly.NightShift.AspNetCore` (assemblies have the same names) |
| Namespaces `Hangfire`, `Hangfire.*` | `Masterly.NightShift`, `Masterly.NightShift.*` |
| `AddHangfire`, `AddHangfireServer`, `UseHangfireDashboard`, `MapHangfireDashboard`, `AddHangfireKeepAlive`, and other `Hangfire`-named members | `AddNightShift`, `AddNightShiftServer`, `UseNightShiftDashboard`, `MapNightShiftDashboard`, `AddNightShiftKeepAlive`, and so on |
| Default dashboard path `/hangfire` | `/nightshift` (keep-alive default: `/nightshift/ping`) |
| Telemetry: `Hangfire` activity source and meter, `hangfire.job.*` instruments and tags, `messaging.system` = `hangfire` | `Masterly.NightShift` activity source and meter, `nightshift.job.*`, `messaging.system` = `nightshift` |
| Prometheus metrics `hangfire_*` | `nightshift_*` |
| Dashboard JavaScript global `window.Hangfire`, assets `hangfire.js`, `hangfire.css`, … | `window.NightShift`, `nightshift.js`, `nightshift.css`, … |
| MSBuild property `HangfireDisableJobInvokerGeneration` | `NightShiftDisableJobInvokerGeneration` |

Existing data keeps working. The SQL Server schema is still `HangFire`, the drain-mode key is still `hangfire:control`, and jobs stored by Hangfire, whose type names read `Hangfire.*, Hangfire.Core`, are mapped to the new types when they are loaded. Third-party storages and extensions built for Hangfire have to be recompiled against the new names. Saved dashboard preferences in the browser (theme, watch list) start from their defaults.

There is no published Masterly.NightShift release yet, so package validation checks only that each package's API agrees across its target frameworks. The comparison with 1.8.25 below was made before the rename.

## JSON: System.Text.Json instead of Newtonsoft.Json

The library no longer depends on Newtonsoft.Json.

- `UseSerializerSettings(...)` takes `System.Text.Json.JsonSerializerOptions` instead of `JsonSerializerSettings`, and `UseRecommendedSerializerSettings(Action<...>)` configures a `JsonSerializerOptions` copy of the internal options.
- `JobHelper.SetSerializerSettings(JsonSerializerSettings)`, obsolete since 1.7, is removed.
- `TypeHelperSerializationBinder` no longer implements Newtonsoft's `ISerializationBinder`.
- Job argument types follow System.Text.Json rules: properties need a public setter or `[JsonInclude]`, and a type without a public parameterless constructor needs a single public constructor or `[JsonConstructor]`. An argument declared as `object` is read back as a primitive (`bool`, `long`, `double`, `DateTime`, `string`) or a `JsonElement`, or as its stored `$type` under the recommended settings.
- `JsonConvert.DefaultSettings` has no effect on anything any more.

Stored data stays readable: states saved with a `"$type"`, arrays saved as `{"$type", "$values"}`, quoted numbers and enum names all read as before.

## Supported platforms

| | 1.8.25 | 2.0 |
|---|---|---|
| Targets | `net451`, `net46`/`net461`, `netstandard1.3`, `netstandard2.0`, `netstandard2.1`, `netcoreapp3.0` | `netstandard2.0`, `net8.0`, `net10.0` |
| .NET Framework | 4.5.1+ | **4.6.2+** (through `netstandard2.0`) |
| Classic ASP.NET / OWIN | Supported | **Removed** |

## Removed packages

- **Masterly.NightShift.SqlServer.MSMQ** is no longer published. Use the default SQL Server queue.
- The **Masterly.NightShift** meta-package no longer pulls in `Microsoft.Owin.Host.SystemWeb`.

## Who is affected and what to use instead

### .NET Framework apps (Core)

All of these were OWIN-only or marked obsolete in 1.8.

| Removed | Use instead |
|---|---|
| `AppBuilderExtensions` (`app.UseNightShiftServer()`, `app.UseNightShiftDashboard()`), `ServerOwinExtensions`, `DashboardOwinExtensions`, `Dashboard.MiddlewareExtensions` | ASP.NET Core: `services.AddNightShiftServer()` and `app.MapNightShiftDashboard()`. Other apps: `new BackgroundJobServer()` |
| `OwinBootstrapper`, `IBootstrapperConfiguration`, `BootstrapperConfigurationExtensions` | `GlobalConfiguration.Configuration` |
| `Dashboard.OwinDashboardContext`, `OwinDashboardContextExtensions`, `Dashboard.Owin.IOwinDashboardAntiforgery` | `Masterly.NightShift.AspNetCore` |
| `Dashboard.IAuthorizationFilter`, `DashboardOptions.AuthorizationFilters`, `LocalRequestsOnlyAuthorizationFilter.Authorize(IDictionary<string, object>)` | `IDashboardAuthorizationFilter` and `DashboardOptions.Authorization` |
| `Dashboard.IRequestDispatcher`, `RequestDispatcherContext`, `RequestDispatcherWrapper`, `RouteCollection.Add(string, IRequestDispatcher)` | `IDashboardDispatcher` and `RouteCollection.Add(string, IDashboardDispatcher)` |
| `RouteCollectionExtensions.AddCommand` / `AddBatchCommand` overloads that take `RequestDispatcherContext` | The overloads that take `DashboardContext` |
| `UrlHelper(IDictionary<string, object>)` | `UrlHelper(DashboardContext)` |
| `Cron.GetDescription(string)` | The `CronExpressionDescriptor` package |

The dashboard no longer shows plain-English descriptions of cron expressions on .NET Framework. They were never available on other platforms.

### .NET Framework apps (SqlServer)

| Removed | Use instead |
|---|---|
| `SqlServerBootstrapperConfigurationExtensions` | `GlobalConfiguration.Configuration.UseSqlServerStorage(...)` |
| `SqlServerStorageOptions.TransactionIsolationLevel` | `SqlServerStorageOptions.UseRecommendedIsolationLevel` |
| `IPersistentJobQueue.Enqueue(IDbConnection, string, string)` | `Enqueue(DbConnection, DbTransaction, string, string)`, the signature every other platform already used |

The `IPersistentJobQueue` change affects third-party queue providers built for .NET Framework. They must implement the new signature.

### Apps on .NET Core 3.0 to .NET 7

These runtimes are out of support. They now get the `netstandard2.0` builds, which lack APIs that the old `netstandard2.1`/`netcoreapp3.0` builds had. Apps on .NET 8 or later are not affected.

| Package | Missing API |
|---|---|
| Masterly.NightShift.NetCore | `BackgroundJobServerHostedService` and `BackgroundProcessingServerHostedService` constructors that take `IHostApplicationLifetime` |
| Masterly.NightShift.AspNetCore | `NightShiftEndpointRouteBuilderExtensions` (`endpoints.MapNightShiftDashboard(...)`). Use `app.UseNightShiftDashboard()` instead, or move to .NET 8+ |

## Dashboard: moment.js is no longer bundled

The dashboard formats dates with the browser's built-in `Intl` APIs instead of moment.js, which is 81 KB smaller and supports every locale the browser knows. Chart.js uses a matching date adapter, `Dashboard/Content/js/nightshift-intl.js`.

- **Extensions that use the global `moment` must bundle it themselves**, for example with `DashboardRoutes.AddJavaScript`.
- The `data-moment`, `data-moment-title` and `data-moment-local` attributes still work, so extensions that render them need no changes. They hold Unix timestamps in seconds.
- Relative dates use `Intl.RelativeTimeFormat`, so ±1 day now reads "yesterday" or "tomorrow" instead of "a day ago" or "in a day".

## Nullable reference type annotations (source-level only)

These parts of the API now carry C# nullable annotations:

- **Client:** `BackgroundJob`, `IBackgroundJobClient`, `BackgroundJobClient`, `BackgroundJobClientExtensions`, `RecurringJob`, `IRecurringJobManager`, `RecurringJobManager`, `RecurringJobManagerExtensions` and `RecurringJobOptions`.
- **Server:** `BackgroundJobServer` and `BackgroundJobServerOptions`.
- **Filters:** the client, server and state filter interfaces and their contexts (`CreateContext`, `CreatingContext`, `CreatedContext`, `PerformContext`, `PerformingContext`, `PerformedContext`, `ElectStateContext`, `ApplyStateContext` and the exception contexts).
- **States:** `IState` and the built-in states.
- **Storage:** `JobStorage`, `IStorageConnection`, `JobStorageConnection`, `IWriteOnlyTransaction`, `JobStorageTransaction`, `IFetchedJob`, `JobData` and `StateData`.
- **Monitoring:** `IMonitoringApi`, `JobStorageMonitor` and the DTOs in `Masterly.NightShift.Storage.Monitoring`.
 Compiled code is unaffected. Projects with `<Nullable>enable</Nullable>` may see new warnings:

- **Job IDs are `string?`.** All `Enqueue`, `Schedule`, `ContinueJobWith`, `ContinueWith` and `Create` methods return `null` when creation doesn't happen: a client filter cancels it, an exception filter handles a failure, or the storage returns no ID. Check the result before passing it on, for example to `ContinueJobWith`.
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

## Behaviour changes that are not API breaks

- Job methods are performed through `JobInvoker`s (generated by the `Masterly.NightShift.SourceGenerator` analyzer that ships
  inside `Masterly.NightShift.Core`, or compiled at runtime) instead of `MethodInfo.Invoke`. Exceptions thrown by a job are
  no longer wrapped in `TargetInvocationException` before Masterly.NightShift handles them; `JobPerformanceException.InnerException`
  is the job's own exception, as before. Call `UseCompiledJobInvokers(false)` to go back to reflection.
- `GlobalJobFilters.Filters` contains four more default filters: `JobConsoleStateFilter` (expires the job
  console list `job:{id}:console` together with the job), `FanInContinuationsSupportAttribute` (drives
  `ContinueJobWithAll`/`ContinueJobWithAny`), `DrainModeFilter` (holds jobs while processing is paused from the
  dashboard) and `ActivityFeedFilter` (two list writes per state transition into the `activity` list, 200 entries;
  `ActivityFeed.Enabled = false` turns it off). Code that asserts the exact number of global filters needs updating.
- `RecurringJobDto` has two new properties, `Paused` and `MisfireHandling`. The recurring jobs hash has a new
  optional field, `Paused`. Older servers ignore it, so a job paused from a new dashboard keeps running on them
  until they are upgraded: pause only after every server runs this version.
- The dashboard registers new routes: `/recurring/pause`, `/recurring/resume`, `/recurring/update/{id}`,
  `/recurring/preview`, `/recurring/remove-all`, `/jobs/live`, `/jobs/{state}/delete-all`, `/retries/delete-all`,
  `/insights`, `/insights/circuits/reset/{resource}`, `/search`, `/maintenance/clear-jobs`, `/maintenance/reset`,
  `/maintenance/pause`, `/maintenance/resume`, `/activity`, `/catalog`, `/catalog/run`, `/recurring/forecast`,
  `/health`, `/tags`, `/tags/{tag}`, `/tags/forget/{tag}`, `/recurring/export` and `/recurring/import`.
  Extensions that registered the same paths now conflict.
- Jobs created with `ContinueJobWithAll`/`ContinueJobWithAny` are in the `Awaiting` state with extra state data
  (`ParentIds`, `Mode`) and their parents carry the `FanInContinuations` job parameter. Older servers never start
  them, so create fan-in continuations only after every server runs this version.
- `[PreventDuplicates]` keeps `fingerprint:{hash}` hashes and sets the `Fingerprint` job parameter; `DrainMode`
  uses the `hangfire:control` hash.
- `JobStorage` has a new virtual method, `Purge(StoragePurgeOptions)`, which throws `NotSupportedException`
  unless a storage overrides it and reports `JobStorageFeatures.Purge`. Storages in other packages are not
  affected until they opt in.
- `AutomaticRetryAttribute` treats `RetryLaterException` specially: the job is scheduled for the moment it asked
  for, the attempt is not counted in `RetryCount`, and the `RetryLaterCount` job parameter counts the requests
  (up to `MaxRetryLaterRequests`, 100 by default; set it to 0 for the old behaviour). The Scheduled state reason
  starts with "Retry later" and such jobs appear on the Retries page.
- More dashboard routes: `/health`, `/tags`, `/tags/{tag}`, `/tags/forget/{tag}`, `/recurring/export` and
  `/recurring/import`. `/search` also returns a `tags` array. The `/catalog/run` form accepts `source` (an
  existing job id) instead of `id`.
- `[Tag]` and `JobTags.Add` write the `tags` set, `tags:{tag}` sets and the `Tags` job parameter. Nothing is
  written unless the attribute or the API is used.
- The dashboard renders `[Redacted]` arguments as `"••••••"`; the stored value is unchanged.
- `GlobalJobFilters.Filters` gains `BatchSupportAttribute`, which reads the `BatchId` job parameter on every
  state transition (from the parameter snapshot when the storage provides one) and writes batch counters only
  for jobs that have it.
- `IJobConsole` has two new members, `Checkpoints` and `HasCheckpoint`. Custom implementations must add them.
- `DashboardContext` has a new virtual method, `GetUserName()`, returning `null` by default; the ASP.NET Core
  context overrides it. `AuditLog` records dashboard actions to the `audit` list unless `AuditLog.Enabled` is
  `false`.
- `UseJobInsights()` also registers `QueueInsightsFilter`, which reads the previous state once per job start
  and writes per-queue latency and throughput keys (`queue:{name}:*`, `queue-insights`).
- `DrainMode` stores paused queues as `Queue:{name}` fields of the `hangfire:control` hash; a resumed queue
  leaves a marker field behind because hash fields can't be removed individually.
- More dashboard routes: `/metrics`, `/batches`, `/batches/{id}`, `/batches/requeue-failed/{id}`,
  `/batches/delete/{id}`, `/audit`, `/queues/pause/{queue}` and `/queues/resume/{queue}`.
- And more: `/wallboard`, `/timeline`, `/servers/pause/{id}`, `/servers/resume/{id}`, `/jobs/notes/add/{id}`,
  `/jobs/export/{id}`, `/jobs/{failed|succeeded|scheduled|processing|deleted}/export` and
  `/jobs/enqueued/export/{queue}`. The export routes are registered before the list pages whose patterns
  would also match them.
- `StatisticsHistoryAttribute` keeps the hourly `stats:*:yyyy-MM-dd-HH` counters for eight days instead of one
  (168 counters per state instead of 24), so the Insights heatmap can show a week.
- `ActivityEntry` has a new `WorkerId` property (`w` in the stored JSON). `DrainMode` stores paused servers as
  `Server:{id}` fields of the `hangfire:control` hash, next to the paused queues.
- Notes written from the dashboard live in the `Notes` job parameter.
- `ActivityFeed.MaxEntries` defaults to 1000 (was 200), so the Timeline covers more than a few seconds on a busy
  cluster; the list is trimmed on every write as before.

## Dependency changes

- `Masterly.NightShift.AspNetCore` on `netstandard2.0` now requires `System.Security.Cryptography.Xml` 4.7.1+ and `System.Text.Encodings.Web` 4.5.1+. These are the first versions that fix known vulnerabilities in the packages that `Microsoft.AspNetCore.Antiforgery` 2.0.0 depends on.
- `Masterly.NightShift.NetCore` on `net10.0` depends on the 10.0.0 `Microsoft.Extensions.*.Abstractions` packages.

## After 2.0.0 ships

Set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to `2.0.0`, then delete the `CompatibilitySuppressions.xml` files. From then on, any break against 2.0 fails the build.
