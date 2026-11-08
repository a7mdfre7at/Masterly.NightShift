# Breaking changes in Masterly.NightShift 1.0

This list compares Masterly.NightShift 1.0 with the last Hangfire 1.8 release (1.8.25). It was produced by two tools, so it is complete for public APIs:

- **Package validation** against 1.8.25, which ran on every `build` and `dotnet pack` until the rename, with each intentional break recorded in a `CompatibilitySuppressions.xml` file.
- **A one-time ApiCompat comparison** of the 1.8.25 .NET Framework builds against the new `netstandard2.0` builds. Package validation can't show these differences, because the .NET Framework builds no longer exist.

## Renamed from Hangfire

Masterly.NightShift 1.0 is based on Hangfire 1.8.25, which the rest of this list compares it with, and is renamed throughout. Code written for Hangfire needs these changes:

| Hangfire | Masterly.NightShift |
|---|---|
| Packages `Hangfire`, `Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.NetCore`, `Hangfire.AspNetCore` | `Masterly.NightShift`, `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer`, `Masterly.NightShift.NetCore`, `Masterly.NightShift.AspNetCore` (assemblies have the same names) |
| Namespaces `Hangfire`, `Hangfire.*` | `Masterly.NightShift`, `Masterly.NightShift.*` |
| `AddHangfire`, `AddHangfireServer`, `UseHangfireServer`, `UseHangfireDashboard`, `MapHangfireDashboard`, and other `Hangfire`-named members | `AddNightShift`, `AddNightShiftServer`, `UseNightShiftServer`, `UseNightShiftDashboard`, `MapNightShiftDashboard`, and so on |
| Default dashboard path `/hangfire` | `/nightshift` |
| `Hangfire.Common.CancellationTokenExtentions` (misspelled) | `Masterly.NightShift.Common.CancellationTokenExtensions` |
| Dashboard JavaScript global `window.Hangfire`, assets `hangfire.js`, `hangfire.css`, … | `window.NightShift`, `nightshift.js`, `nightshift.css`, … |

Some features have no Hangfire name because Hangfire 1.8.25 didn't have them: keep-alive (`AddNightShiftKeepAlive`), telemetry (`UseTelemetry()`, the `Masterly.NightShift` activity source and meter), the Prometheus metrics (`nightshift_*`) and the job invoker generator (`NightShiftDisableJobInvokerGeneration`).

Existing data keeps working. The SQL Server schema is still `HangFire`, the drain-mode key is still `hangfire:control`, and jobs stored by Hangfire, whose type names read `Hangfire.*, Hangfire.Core`, are mapped to the new types when they are loaded. Third-party storages and extensions built for Hangfire have to be recompiled against the new names. Saved dashboard preferences in the browser (theme, watch list) start from their defaults.

There is no published Masterly.NightShift release yet, so package validation checks only that each package's API agrees across its target frameworks. The comparison with 1.8.25 below was made before the rename.

## JSON: System.Text.Json instead of Newtonsoft.Json

The library no longer depends on Newtonsoft.Json.

- `UseSerializerSettings(...)` takes `System.Text.Json.JsonSerializerOptions` instead of `JsonSerializerSettings`, and `UseRecommendedSerializerSettings(Action<...>)` configures a `JsonSerializerOptions` copy of the internal options.
- `JobHelper.SetSerializerSettings(JsonSerializerSettings)`, obsolete since 1.7, is removed.
- `TypeHelperSerializationBinder` is removed. It existed for Newtonsoft.Json and nothing uses it any more; resolve type names with `TypeHelper.CurrentTypeResolver` and `TypeHelper.CurrentTypeSerializer` instead.
- Job argument types follow System.Text.Json rules: properties need a public setter or `[JsonInclude]`, and a class without a public parameterless constructor needs a single public constructor or `[JsonConstructor]`. Two Newtonsoft.Json behaviours are kept when reading, with any options: a collection property without a setter gets the stored items added to the collection it holds, and a struct whose only public constructor sets its get-only properties is created through that constructor. A struct with several public constructors needs `[JsonConstructor]`.
- An argument declared as `object` is read back as a primitive or a `JsonElement`: whole numbers, enums and whole `double`s as `long`, other numbers (`decimal` too) as `double`, `Guid`, `TimeSpan` and `char` as `string`, ISO 8601 date text as `DateTime`, collections as `JsonElement`. Under the recommended settings, a value written as a JSON object (a class, a struct, a dictionary) keeps its `"$type"` and is read back as that type.
- `JsonConvert.DefaultSettings` has no effect on anything any more.
- An argument declared as an interface or abstract class is written with a leading `"$type"` naming its actual type, whatever serializer options are configured, and read back as that type. Hangfire needed `TypeNameHandling` for this.
- Options passed to `UseSerializerSettings(...)` decide how data is written; it is always read leniently (property names in any case, numbers from strings, public fields). A converter added in the `UseRecommendedSerializerSettings(Action<...>)` callback wins over the built-in one for the same type.

- `[DefaultValue]` no longer leaves members out of job arguments and return values: the default and recommended options write every member, and set a member that is missing from the JSON to its `[DefaultValue]` when reading, so data that Hangfire's recommended settings (`DefaultValueHandling.IgnoreAndPopulate`) stored reads back as written. A member bound to a constructor parameter takes the parameter's default value instead. Options passed to `UseSerializerSettings(...)` don't apply `[DefaultValue]`, as System.Text.Json doesn't.
- `double.NaN`, `float.NaN` and the infinities are written as `"NaN"`, `"Infinity"` and `"-Infinity"` by the default and recommended options, as Hangfire did, and read from those strings whatever options are configured.
- Below `Version_170`, the job payload is written with the internal options, so `Type`, `Method`, `ParameterTypes`, `Arguments` and `Queue` keep these names whatever your options say (a snake_case or kebab-case naming policy, `IgnoreReadOnlyProperties`). Payloads stored with renamed members are still read.
- The `Tags`, `BatchId` and `BatchContinuationOf` job parameters and the `FanInContinuations` lists are written with the internal options at every compatibility level, so a restrictive `TypeInfoResolver` (a source-generated context) can't break tagging or batches. Values written with your options before are still read.
- Library data that the options passed to `UseSerializerSettings(...)` can't write (System.Text.Json throws `NotSupportedException`, as a source-generated context does for types it doesn't list) is written with the internal options instead of failing: below `Version_170` the job's argument list, parameter types, SQL Server state data, continuation lists and job console entries, and at every level the library's own types stored as user data, such as a deleted job's `ExceptionInfo`. Your own argument and return value types are not rerouted.

Stored data stays readable: states saved with a `"$type"`, arguments declared as an interface or abstract class saved with a `"$type"`, arrays and other collections saved as `{"$type", "$values"}` and dictionaries saved with a leading `"$type"` naming their type (also as members and items, read as the declared type), quoted numbers, enum names and property names in any case all read as before, whatever serializer options are configured. The one exception: options passed to `UseSerializerSettings(...)` with a `ReferenceHandler` leave collections and arrays to System.Text.Json, so cycles and shared references work as in `JsonSerializer`, and collections saved as `{"$type", "$values"}` aren't read with those options. A dictionary entry `"$type"` that doesn't name a fitting dictionary type is kept as a key.

## Supported platforms

| | 1.8.25 | 1.0 |
|---|---|---|
| Targets | `net451`, `net46`/`net461`, `netstandard1.3`, `netstandard2.0`, `netstandard2.1`, `netcoreapp3.0` | `netstandard2.0`, `net8.0`, `net10.0` |
| .NET Framework | 4.5.1+ | **4.6.2+** (through `netstandard2.0`) |
| Classic ASP.NET / OWIN | Supported | **Removed** |
| Strong-named assemblies | No | No (unchanged; strong-named .NET Framework apps can't load them) |

## Removed packages

- **Hangfire.SqlServer.MSMQ** has no Masterly.NightShift equivalent. Use the default SQL Server queue.
- The **Masterly.NightShift** meta-package doesn't pull in `Microsoft.Owin.Host.SystemWeb`, which the **Hangfire** meta-package did.

## Who is affected and what to use instead

### .NET Framework apps (Core)

All of these were OWIN-only or marked obsolete in 1.8.

| Removed | Use instead |
|---|---|
| `AppBuilderExtensions` (Hangfire's `UseHangfireServer()` and `UseHangfireDashboard()` for the OWIN `IAppBuilder`), `ServerOwinExtensions`, `DashboardOwinExtensions`, `Dashboard.MiddlewareExtensions` | ASP.NET Core: `services.AddNightShiftServer()` and `app.MapNightShiftDashboard()` (the `IApplicationBuilder` extensions `app.UseNightShiftServer()` and `app.UseNightShiftDashboard()` still exist). Other apps: `new BackgroundJobServer()` |
| `OwinBootstrapper`, `IBootstrapperConfiguration`, `BootstrapperConfigurationExtensions` | `GlobalConfiguration.Configuration` |
| `Dashboard.OwinDashboardContext`, `OwinDashboardContextExtensions`, `Dashboard.Owin.IOwinDashboardAntiforgery` | `Masterly.NightShift.AspNetCore` |
| `Dashboard.IAuthorizationFilter`, `DashboardOptions.AuthorizationFilters`, `LocalRequestsOnlyAuthorizationFilter.Authorize(IDictionary<string, object>)` | `IDashboardAuthorizationFilter` and `DashboardOptions.Authorization` |
| `Dashboard.IRequestDispatcher`, `RequestDispatcherContext`, `RequestDispatcherWrapper`, `RouteCollection.Add(string, IRequestDispatcher)` | `IDashboardDispatcher` and `RouteCollection.Add(string, IDashboardDispatcher)` |
| `RouteCollectionExtensions.AddCommand` / `AddBatchCommand` overloads that take `RequestDispatcherContext` | The overloads that take `DashboardContext` |
| `UrlHelper(IDictionary<string, object>)` | `UrlHelper(DashboardContext)` |
| `Cron.GetDescription(string)` (the .NET Framework builds only, through the `CronExpressionDescriptor` package) | `Cron.Describe(string)`, which throws `FormatException` for an invalid expression, or `Cron.TryDescribe(string)`, which returns `null`. Both exist on every target |

The dashboard shows plain-English descriptions of cron expressions on every platform now, from `Cron.TryDescribe`. Hangfire showed them on .NET Framework only. The wording differs from `CronExpressionDescriptor`'s, and it is always English.

### .NET Framework apps (SqlServer)

| Removed | Use instead |
|---|---|
| `SqlServerBootstrapperConfigurationExtensions` | `GlobalConfiguration.Configuration.UseSqlServerStorage(...)` |
| `SqlServerStorageOptions.TransactionIsolationLevel` of type `System.Transactions.IsolationLevel?` (the .NET Framework builds only) | `SqlServerStorageOptions.UseRecommendedIsolationLevel`. The obsolete `System.Data.IsolationLevel?` property of the same name, which every other platform had, still exists |
| `IPersistentJobQueue.Enqueue(IDbConnection, string, string)` | `Enqueue(DbConnection, DbTransaction, string, string)`, the signature every other platform already used |

The `IPersistentJobQueue` change affects third-party queue providers built for .NET Framework. They must implement the new signature.

### Apps on .NET Core 3.0 to .NET 7

These runtimes are out of support. They now get the `netstandard2.0` builds, which lack APIs that the old `netstandard2.1`/`netcoreapp3.0` builds had. Apps on .NET 8 or later are not affected.

| Package | Missing API |
|---|---|
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
- **Hosting:** `JobActivator` (`ActivateJob` returns `object?`), `JobActivatorScope` (`Resolve` returns `object?`, `Current` is `JobActivatorScope?`) and `JobActivatorContext` (`GetJobParameter<T>` returns `T?`, `SetJobParameter` takes `object?`).
- **Scheduling:** `Cron` (`TryDescribe(string?)` returns `string?`; `TryFromText(string?, out string?)`).
- **Retries:** `AutomaticRetryAttribute.DelaysInSeconds`, `OnlyOn` and `ExceptOn` are nullable.
- **SQL Server:** `UseSqlServerStorage` and `SqlServerStorageOptions` (`ImpersonationFunc` and `DefaultQueueProvider` are nullable). Passing `configuration.GetConnectionString(...)` warns, because the method throws for `null`.
- **ASP.NET Core and DI:** `AddNightShift`, `AddNightShiftServer`, `UseNightShiftDashboard`, `UseNightShiftServer`, `MapNightShiftDashboard*` and `AddNightShiftKeepAlive`; optional `options`, `storage`, `additionalProcesses` and `configure` parameters are nullable, and `GetInternalServices` has nullable `out` parameters.
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
- `GlobalJobFilters.Filters` contains four more default filters: `JobConsoleStateFilter` (expires the job's console
  lists `job:{id}:console` and `job:{id}:checkpoints` together with the job, only for jobs whose console wrote to
  storage, which carry the `Console` job parameter, and only on storages with `ExtendedApi`; it clears the checkpoints
  when a succeeded or deleted job is requeued), `FanInContinuationsSupportAttribute` (drives
  `ContinueJobWithAll`/`ContinueJobWithAny`), `DrainModeFilter` (holds jobs while processing is paused from the
  dashboard) and `ActivityFeedFilter`, which does nothing until the feed is turned on with `UseActivityFeed()`; it then
  queues one entry per transition and writes them to the `activity` list in the background after each state change
  commits. Code that asserts the exact number of global filters needs updating.
- `RecurringJobDto` has two new properties, `Paused` and `MisfireHandling`. The recurring jobs hash has a new
  optional field, `Paused`. Older servers ignore it, so a job paused from a new dashboard keeps running on them
  until they are upgraded: pause only after every server runs this version.
- The dashboard registers new routes: `/recurring/pause`, `/recurring/resume`, `/recurring/update/{id}`,
  `/recurring/preview`, `/recurring/remove-all`, `/jobs/live`, `/jobs/{state}/delete-all`, `/retries/delete-all`,
  `/insights`, `/insights/circuits/reset/{resource}`, `/search`, `/maintenance/clear-jobs`, `/maintenance/reset`,
  `/maintenance/pause`, `/maintenance/resume`, `/activity`, `/catalog`, `/catalog/run`, `/recurring/forecast`,
  `/health`, `/tags`, `/tags/{tag}`, `/tags/forget/{tag}`, `/tags/forget-expired/{tag}`, `/recurring/export` and
  `/recurring/import`. Extensions that registered the same paths now conflict.
- Jobs created with `ContinueJobWithAll`/`ContinueJobWithAny` are in the `Awaiting` state with extra state data
  (`ParentIds`, `Mode`) and their parents carry the `FanInContinuations` job parameter. Older servers never start
  them, so create fan-in continuations only after every server runs this version.
- `[PreventDuplicates]` keeps `fingerprint:{hash}` hashes and sets the `Fingerprint` job parameter; `DrainMode`
  uses the `hangfire:control` hash.
- `JobStorage` has a new virtual method, `Purge(StoragePurgeOptions)`, which throws `NotSupportedException`
  unless a storage overrides it and reports `JobStorageFeatures.Purge`. Storages in other packages are not
  affected until they opt in; those that do should honour all four options (`KeepRecurringJobs`, `KeepServers`,
  `KeepAuditLog`, `KeepDrainMode`), which default to `true`.
- `AutomaticRetryAttribute` treats `RetryLaterException` specially: the job is scheduled for the moment it asked
  for, the attempt is not counted in `RetryCount`, and the `RetryLaterCount` job parameter counts the requests
  (up to `MaxRetryLaterRequests`, 100 by default; set it to 0 for the old behaviour). The Scheduled state reason
  starts with "Retry later" and such jobs appear on the Retries page.
- More dashboard routes: `/health`, `/tags`, `/tags/{tag}`, `/tags/forget/{tag}`, `/tags/forget-expired/{tag}`,
  `/recurring/export` and `/recurring/import`. `/search` also returns a `tags` array. The `/catalog/run` form accepts `source` (an
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
  `Server:{id}` fields of the `hangfire:control` hash, next to the paused queues; an id too long for a 100-character
  field is stored as `Server:#{SHA-256 of the lowercased id}` with the id at the start of the value. A held job whose
  queue came only from its Enqueued state gets a `HeldQueue` job parameter, so it returns to that queue once; the
  parameter is then cleared. RunWindow, Throttle, RateLimit and CircuitBreaker holds do the same.
- Notes written from the dashboard live in the `Notes` job parameter.
- `ActivityFeed.MaxEntries` defaults to 1000. The list is trimmed on the writer's first write and then every 100
  entries, so it can briefly hold a few more.
- The activity feed is **off by default**; turn it on with `UseActivityFeed()` in every process that changes job states.
  Entries are written after commit, best effort: one can be lost when a write fails or the process stops.
  `ActivityEntry` and `AuditEntry` are written with the library's own JSON options, not `UseSerializerSettings`.
- Each audit-log entry is also written to the library logger at Info (category `Masterly.NightShift.AuditLog`), with the
  user name and IP. "Clear all jobs" keeps the audit log and drain mode; "Reset storage" keeps the audit log.
- Job insights keys longer than 80 characters are shortened with a hash; insights and queue-insights failures are
  logged and never fail a job or a state change; queue insights are written after commit.
- `/health` and `/metrics` are reused for 5 seconds, report storage errors without their exception text (the details
  go to the log), and `/metrics` groups each Prometheus family. "Disabled by errors" counts only recurring jobs with no
  next run.
- Webhook alerts: Slack text is escaped (`&`, `<`, `>`); `WebhookFormat.Discord` is new; `WebhookAlertSink.Dispose`
  waits up to the request timeout (at most `MaxDisposeWait`, 30 s) for requests in flight and ignores `Publish` after.
- Dashboard: without `IAntiforgery` registered (and `IgnoreAntiforgeryToken` false), a POST must carry
  `X-Requested-With` and come from the dashboard's own origin (same scheme, host and port; `X-Forwarded-Host` is
  ignored), or it gets `403`; scripts that post to dashboard URLs need that header. Behind a proxy that rewrites
  `Host`, register `AddAntiforgery()`. Every dashboard response has `X-Frame-Options: SAMEORIGIN` and
  `Content-Security-Policy: frame-ancestors 'self'` unless already set. The `/jobs/*/delete-all` and
  `/retries/delete-all` routes answer `200` with `{processed, remaining, complete}` instead of `204`, and change at most
  100,000 jobs or about 20 seconds of work per call. New routes: `/jobs/failed/requeue-all` and
  `/tags/forget-expired/{tag}`. Pages show at most 1000 rows; notes are limited to 2000 characters; a form over the
  server's limits answers `413`. The job catalog form refuses `System.Type` arguments. Route values decode only `%2F`.
  `MapNightShiftDashboardWithNoAuthorizationFilters` uses the `DashboardOptions` registered in DI and no
  longer changes the instance it is given. Manifest import accepts only methods the application already uses as jobs
  unless `DashboardOptions.AllowAnyMethodInManifestImport` (or `allowAnyMethod` on `Apply`) is set, and the manifest
  export hides `[Redacted]` values from read-only users.
- Tags: `JobTags.MaxLength` is 95 (the `tags:{tag}` key must fit 100 characters); a `[Tag]` template that formats to an
  empty value is skipped and an over-long one is shortened with a hash, instead of failing job creation. The Tags page
  is paged, in name order, and filtered on the server (`?filter=`).
- Fan-in and batch continuations are decided right after a parent's state change commits, so parents finishing at
  the same moment start them once. Batch bookkeeping expires `BatchJob.ExpireIn` after creation on storages with the
  extended API; the Batches page no longer writes.
- Concurrency filters: a resource or templated fingerprint is shortened only when its longest storage key would pass
  100 characters (over 91 characters for Throttle, 79 for RateLimit, 83 for CircuitBreaker, 88 for PreventDuplicates
  fingerprints). `[Throttle]` and `[RateLimit]` also implement `IElectStateFilter`, to remember the queue a held job
  was fetched from.
  `[Throttle]` backs off between reschedules (15 s doubling up to `MaxRetryDelaySeconds`, 300 by default; job parameter
  `ThrottleReschedules`). `[RateLimit]` treats `WaitTimeoutSeconds` as a hard limit and gives a start back when the job
  method was never called (a later filter canceled the run or threw first). Its set is
  `ratelimit:{resource}:{perSeconds}` (lock `ratelimit:{resource}:{perSeconds}:lock`), so limits with different
  windows on one resource are counted apart; on storages with `ExtendedApi` and `BatchedGetFirstByLowest` it counts by
  score instead of reading the whole set. Concurrency state expires instead of staying in storage. `[CircuitBreaker]` lets one probe
  job through when half-open, records failures under its lock, and stores `OpenUntil` and `WindowSeconds`.
  The `CircuitOpened` alert is sent once the job's state change ends, even when that change fails, and a retried
  state change doesn't count the same failure twice.
  `[PreventDuplicates]` releases the fingerprint when a job fails (`ReleaseOnFailure`) and `ReplaceExisting` never
  replaces a running owner; when two clients create the same fingerprint at once, both jobs can exist (`Create` no
  longer fails after the job was stored). `[IdempotentCompletion]` records the final state after election, without the result, and
  honours explicit Delete and Requeue. `[RetryInProcess]` with no exception list doesn't retry `RetryLaterException`
  or `ConcurrencyLimitException`. Invalid `[Throttle]`/`[RateLimit]` values and an `[ExpireAfter]` over 100 years
  throw when the attribute is read.
- Without a template, `[Throttle]`, `[RateLimit]` and `[CircuitBreaker]` use the job's full type name (namespace and
  generic arguments), method name and parameter types as the resource, lower-cased
  (`orders.cleanupjob.execute(system.int32)`), and `[PreventDuplicates]` hashes the same name with the arguments.
  Same-named types in other namespaces and overloads no longer share a slot, window, circuit or fingerprint.
  `[DisableConcurrentExecution]` keeps its old lock name, shortened with a hash only above 200 characters.
- `[CircuitBreaker]` no longer counts failures with `ConcurrencyLimitException`, `DistributedLockTimeoutException` or
  `RetryLaterException` (the dependency was never called), for any job or the probe. New properties `ExceptOn` (those
  three by default) and `OnlyOn` (empty) change that.
- Nested `[JobTimeout]` attributes (class and method) both apply with `CancellationToken` and `IJobCancellationToken`
  parameters alike: the first to expire cancels the token, the `TimeoutException` names it, and `TimeoutExceeded`
  keeps the shorter timeout. A synchronous job retried by `[RetryInProcess]` runs every attempt on the worker thread.
- `RunWindow`, `Throttle`, `RateLimit` and `CircuitBreaker` restore a held job's queue themselves (they no longer
  depend on `DrainModeFilter`). A job requeued while still in the Processing state (shutdown timeout, crash) has its
  queue read from the last Enqueued state in its history, so paused queues and holds apply to it.
- Recurring jobs: `Strict` misfire handling creates at most 100 runs per job per scheduler pass and doesn't replay runs
  from before a schedule change (new hash field `ScheduleChangedAt`, written only when the schedule's meaning
  changes). A recurring job whose data can't be read is retried and then disabled without stopping the scheduler.
  `Cron.FromText` and `CronBuilder.Between` accept overnight hour ranges; `TimeText` rejects phrases more than ten
  years ahead.
- Job console: writes are serialized and go through their own short-lived connection; writes after the job finished
  are dropped. Checkpoints are kept apart from the console lines. Checkpoint reads (`HasCheckpoint`, `Checkpoints`,
  `Resume`) throw on storage errors instead of treating them as no checkpoints; a storage without list support still
  means no checkpoints.
- `[JobTimeout]` puts the original cancellation tokens back after the run, so an in-process retry gets a fresh
  timeout.
- The source generator no longer creates invokers for compiler-generated members, `object` overrides or static
  abstract interface members, and a generated invoker throws `InvalidCastException` for an argument of the wrong value
  type instead of passing the default. Interceptors each get their own `JobInvocation`.
- `[Tag]`: when writing the `tags` and `tags:{tag}` sets fails after the job was created, the failure is logged as a
  warning and creation succeeds. It used to throw `BackgroundJobClientException` although the job existed, so a
  retrying caller created it twice.
- `BatchSupportAttribute` adds a batch continuation to `batch:{id}:continuations` in the transaction of the
  continuation's first state (`OnStateApplied`); its `OnCreated` writes nothing.
- Fan-in continuations are also decided when the commit of a parent's (or the child's own) state change throws,
  because it may have gone through. A fan-in child is no longer registered on any parent when one of its parents
  doesn't exist.
- `BackgroundJobServerOptions.HeartbeatInterval`, `ServerCheckInterval` and `ServerTimeout` no longer accept
  `TimeSpan.Zero`, and `CancellationCheckInterval` is now validated: all four must be greater than zero
  (`CancellationCheckInterval` at most `Int32.MaxValue` milliseconds), otherwise `ArgumentOutOfRangeException`.
  With `AddNightShiftServer(IConfiguration)`, such a value stops the start with `InvalidOperationException` naming
  the key.
- Server options from configuration: when `Queues` is set both as an array and as a comma-separated value (an array
  in `appsettings.json`, `NightShift__Server__Queues=low,default` in the environment), the comma-separated value
  wins, whichever source was added last.
- `UpdateSchedule(id, ..., queue: ...)`, and the dashboard's Edit dialog, also write a queue other than `default`
  into the stored job on storages that support per-job queues (SQL Server), so `AutomaticRetry` retries the runs on
  that queue instead of `default`. Passing `default` clears the job's queue only when an earlier `UpdateSchedule`
  set it; a queue the job was registered with through `AddOrUpdate(id, queue, ...)` stays.
- `IRecurringJobManager.TriggerJob` (and the dashboard's Trigger now) runs post-commit work, such as the activity
  feed entry, after its transaction commits, and drops it when the commit fails.
- `Cron.Describe` calls a step "every N" only when the runs are always N apart: `*/45 * * * *` is "At minutes 0 and
  45 past every hour", `0 6/4 * * *` is "At 06:00, 10:00, 14:00, 18:00 and 22:00", and `0 4 */3 * *` is "At 04:00,
  every 3 days from day 1 through 31 of the month".
- `TimeText`: `tonight` and `this morning`/`this afternoon`/`this evening` mean today and are refused once that time
  has passed (`this evening` used to move to tomorrow); `tonight at midnight` is 00:00 tomorrow (it was refused).
  `in 1.5 months` is refused (the fraction used to be dropped). `Parse` says why: "The time '...' is in the past.",
  or that months must be a whole number.
- `DrainMode` writes `[resumed]` into the field of a resumed queue or server instead of a value starting with a NUL
  character, which PostgreSQL text columns refuse; the old marker is still read as resumed. A pause reason equal to
  `[resumed]` is stored with a space added.
- Run form dates and queue: `JobCatalog.FormatArguments` writes dates so they round-trip (a `DateTimeOffset` with its
  offset, a UTC `DateTime` with `Z`, a local one with its offset, fractions of a second when present), and
  `JobCatalog.CreateJob` reads a `DateTimeOffset` given without an offset as UTC (it used the server's time zone).
  **Run again with changes** keeps the source job's own queue when the queue field is empty.
- `/recurring/remove-all`, `/batches/requeue-failed/{id}` and `/batches/delete/{id}` answer
  `200 {processed, remaining, complete}` instead of `204` and, like **Delete all**, stop after about 20 seconds or
  100,000 jobs. Commands posted with `jobs[]` (such as **Trigger now**) answer `422 {error}` instead of `500` when a
  job's type can't be loaded in the dashboard's process, and still handle the other ids; a catalog "run again" of such a
  job answers `422` instead of `404`.
- Dashboard: `GET /stats` answers `405`. A failed purge returns a generic message and logs the storage's error. Failed
  dashboard actions (`422`, `500`) are written to the audit log. On storages without `JobStorageFeatures.ExtendedApi`,
  the Insights, Retries and Awaiting pages show a "not supported" message, and delete-all on retries and awaiting jobs
  answers `422`. Job notes are written with the library's own JSON options. Queue insights record a job under its own
  queue (`Job.Queue`) instead of `default`.
- `Masterly.NightShift.Dashboard.ScheduleForecast` (with `Row` and `HotSpot`) and `JobCodeRenderer` are internal; only the
  dashboard pages used them.
- `JobActivator.BeginScope(PerformContext)` throws `ArgumentNullException` for `null` (it threw
  `NullReferenceException`). The services `AddNightShift` registers resolve their dependencies with
  `GetRequiredService`, so a dependency removed or replaced with a factory returning `null` gives
  `InvalidOperationException` instead of a `null`.
- Servers added with `AddNightShiftServer` run each job inside `ILogger.BeginScope` with `JobId`, `JobName`,
  `RecurringJobId` and `RetryCount`, so providers that print scopes show them. Turn it off with
  `services.ConfigureNightShiftJobLogging(o => o.BeginScope = false)`.

- `[RateLimit]` may be repeated (`AllowMultiple = true`): global, class and method limits all apply instead of the
  most specific one replacing the others, so a global `RateLimit` now adds to a method's own. The job starts only when
  every limit allows it; when one refuses, the starts the others recorded are given back. Two limits with the same
  resource and window count one start. A limit with `Action = Wait` waits only while no other limit has recorded a
  start for the run; otherwise it reschedules the job, so a start already recorded can't age out of its window.
- `[DisableConcurrentExecution]` shortens a lock name longer than 200 characters to its first 167 characters, `~` and
  32 hexadecimal digits of its SHA-256, because SQL Server cut such names and two resources sharing their first ~245
  characters got the same lock. Shorter names don't change.
- `[JobTimeout]` accepts at most `JobTimeoutAttribute.MaxTimeoutInSeconds` (2,147,483 seconds, about 24.8 days) and
  throws `ArgumentOutOfRangeException` when the attribute is read; a longer timeout used to fail every run.
  `[RetryInProcess]` `DelayMilliseconds` must be from 0 to `RetryInProcessAttribute.MaxDelayMilliseconds` (one hour),
  and the n-th delay is capped at about 24.8 days.
- Storage errors in filter bookkeeping no longer fail a job or replace its exception: releasing a `[Throttle]` slot or
  a `[DisableConcurrentExecution]` lock, counting a `[Throttle]` reschedule, giving back a `[RateLimit]` start, writing
  `TimeoutExceeded` or `DeadlineMissed`, removing a `[PreventDuplicates]` reservation after a failed creation, and
  forgetting a quiet closed circuit are logged as warnings. Releasing a `[Throttle]` slot is tried three times, and
  when a resource is full, a slot whose job is no longer in the Processing state (or is gone) is reclaimed, so a failed
  release doesn't block the resource for `StaleTimeoutMinutes`. Rescheduling a job from `[Throttle]`, `[RateLimit]` or
  `[CircuitBreaker]`, and recording a circuit's probe, are tried three times before a storage error fails the run.
- New: `ThrottleAttribute.GetSlots(connection, resource)` and `ThrottleAttribute.Release(connection, resource, jobId)`.
- Job console: `SetProgress` and `WriteLine` no longer throw when the storage fails to write. The write is dropped
  and a warning is logged (at most once a minute per job run, with the number of writes dropped), so a job that did
  its work isn't failed and retried because of a console line. `Checkpoint`, `Checkpoints`, `HasCheckpoint` and
  `Resume` still throw. `[LogInvocation]` writes its "Finished" line after the method returned, outside its error
  handling.
- `TimeText`: with `tonight`, `afternoon` or `evening`, an hour from 1 to 11 without am/pm is in the afternoon
  (`tonight at 9` is 21:00, `tomorrow afternoon at 3` is 15:00; they used to mean 09:00 and 03:00). `12` is noon with
  `afternoon` and the midnight that ends the evening with `tonight` or `evening`, and so is `midnight` there
  (`tomorrow evening at midnight` is 00:00 the day after tomorrow, like `tomorrow evening at 12`). An explicit am/pm or a 24-hour time
  (`19:15`, `07:30`) is used as written; `morning` keeps the hour.
- `Cron.Describe`: `N/1` means N through the end of the field (`0 9/1 * * *` is "Every hour from 09:00 to 23:00"; it
  used to be described as the single value N), and a list containing a stepped range is described in order.
- Recurring jobs: when creating or enqueuing a run fails, the job's `LastExecution`/`LastJobId` stay at the last run
  that was enqueued, so the retry pass creates the failed run and the ones after it. `Strict` used to lose them and
  `Relaxed` lost its run.
- The Prometheus counter `nightshift_jobs_total` (finished jobs by `state` = `succeeded`, `deleted`) is now `nightshift_jobs_finished_total`, so OpenMetrics parsers no longer reject the scrape for clashing with the `nightshift_jobs` gauge.
- Insights no longer counts an attempt whose `[Throttle]`, `[RateLimit]` or `[DisableConcurrentExecution]` gave up waiting (`ConcurrencyLimitException`, `DistributedLockTimeoutException` before the method ran) as a failure, and its durations leave out those waits: they are measured from the moment the job method is called. Telemetry still reports such an attempt as `failed`.
- The job logging scope of servers added with `AddNightShiftServer` now stays open until the worker has changed the job's state, so `AutomaticRetry`'s retry and final-failure entries and the `[Deadline]`/`[CircuitBreaker]` warnings carry `JobId`.
- Discord webhook alerts escape Markdown (`\ * _ ~ ` | > [ ] ( ) #`) in the title, message and values.
- The audit log's Information line writes control characters and line breaks in the user, targets and detail as spaces; the stored entry is unchanged.
- `AuditLog.Read` returns an empty list for a `count` of zero or less instead of throwing `ArgumentOutOfRangeException`.
- `WebhookAlertSink.Dispose()` cancels requests still running after its wait, also with a client you passed in and with `Timeout = Timeout.InfiniteTimeSpan`.
- A recurring job whose retry after a scheduling error is more than a minute overdue counts as late, on `/health`, `/metrics` and the Recurring Jobs page.
- `JobStorage.JobExpirationTimeout` (and `WithJobExpirationTimeout`) accepts at most 36,500 days (`ExpireAfterAttribute.MaxDays`) and throws `ArgumentOutOfRangeException` above it. A longer value (such as `TimeSpan.MaxValue`) used to be accepted and then made every final-state commit on SQL Server throw, leaving jobs in Processing. The SQL Server storage also keeps any expiration it writes within its `datetime` range: one past December 31, 9999 is stored as that day.
- SQL Server: `UseSqlServerStorage(string)` and `new SqlServerStorage(string, ...)` look a text without `=` up as a connection string name in the `connectionStrings` section of `app.config`/`web.config`, as Hangfire did on .NET Framework (on .NET this works when the app has `System.Configuration.ConfigurationManager`). When no such entry exists, the constructor throws `ArgumentException`; such a text used to be taken for a connection string, and the storage failed at its first use.
- SQL Server: writes for a job id that isn't a number (`SetJobParameter`, `ExpireJob`, `PersistJob`, `SetJobState`, `AddJobState`, `AddToQueue`) do nothing instead of throwing `FormatException`, matching the reads, which return `null`.
- SQL Server: distributed lock names longer than 255 characters (schema name, colon and resource) are shortened to a 238-character prefix, `~` and a 16-digit hash instead of being cut at 255 by SqlClient, so two long names that start alike are no longer the same lock. While servers on an older build share the storage, they use a different name for such locks and don't exclude each other.
- SQL Server: `GetRangeFromSet` orders members with the same score by value (it used to leave their order unspecified), and reads ranges near the end of long sets from the end, so the newest page of a large tag is fast.
- SQL Server: `InsertToList` takes the new item's expiration from the newest item of the list instead of the latest expiration of all items. `ExpireList`/`PersistList` set every item alike, so the result is the same unless an older build added items without an expiration after the list was set to expire.
- The Servers page marks a server **Possibly aborted** as soon as its last heartbeat is older than
  `DashboardOptions.ServerPossiblyAbortedThreshold` (1 minute by default), like the job pages, the diagnostics and
  `/health`. Hangfire waited for twice the threshold and showed no mark in between.

- The obsolete members kept from Hangfire now say what to use instead and that they are kept for code ported from
  Hangfire. They no longer say "Will be removed in 2.0.0", which referred to Hangfire 2.0. Only the text of the
  `CS0618` warning changes.
- The generators' diagnostics are `NSGEN001` (job invoker generator) and `NSDASH001` (dashboard pages) instead of
  `HFGEN001` and `HFDASH001`. Update any `NoWarn` or `.editorconfig` entry that names the old IDs.

## Behaviour that Hangfire got wrong

These changes fix behaviour inherited from Hangfire 1.8.25. Code that relied on the old behaviour needs a look.

- **Failure reason when a job can't be loaded.** The Failed state's reason says what failed: `Can not change the state to '<state>': the job's type or method was not found.`, `... the job's arguments could not be deserialized.`, or `... the job could not be loaded.` when a storage reports the failure itself; the error is in the exception details. Hangfire always wrote `... target method was not found.`, so monitoring that matches that text needs updating. The `JobLoadException` messages say the same.
- **`SqlServerStorageOptions.SchemaName`** throws `ArgumentException` for `null`, empty or white space, for names longer than 128 characters, and for names containing `]`, `'` or a control character. Hangfire checked the previous value, so a bad value failed later, and it pasted the name into the install script unescaped, so `]` or `'` broke the install. `SqlServerObjectsInstaller.GetInstallScript`/`Install` check the name the same way.
- **`SlidingInvisibilityTimeout`, `CommandTimeout`, `CommandBatchMaxTimeout`** throw `ArgumentOutOfRangeException` under 1 second. Hangfire rounded them down to 0: duplicate fetches, or no timeout at all.
- **Over-long values in the SQL Server storage** throw `ArgumentException` naming the limit when written: keys and hash fields over 100 characters, set values over 256 (including `RemoveFromSet`), job parameter names over 40, state names over 20, queue names over 50 and server ids over 200 (`AnnounceServer`; a server id is `ServerName` plus up to 48 characters, so keep `ServerName` to 150 characters or fewer). Hangfire failed inside SQL Server or silently cut them, so later lookups missed.
- **List items inherit the list's expiration** (`InsertToList`). Hangfire's items added to an expiring list never expired.
- **`JobDetails` on SQL Server** returns `null` for a non-numeric id instead of throwing `SqlException`.
- **Startup install** waits 1 s and 3 s between attempts, and retries any exception, not only `DbException`: a connection factory that throws while its token service isn't ready no longer stops the constructor. After a failed open it clears the SQL client's connection pool for that connection string, so the retries reach the server instead of failing at once during the client's pool blocking period (5 s or more after a failed open). A missing SQL client still fails at once: the connection string constructor throws `InvalidOperationException`, now also with `PrepareSchemaIfNecessary = false`. When the install still fails, it is retried when a connection is opened or the schema version is read: first after 30 s, then doubling up to 30 minutes, with a warning each time. A schema version that couldn't be read at startup is read again when first needed (at most every 30 s).
- **Schema names outside the database's code page** (for example `Задания` on a Latin1 database) install and work. Hangfire's install script compared the name with non-Unicode literals, so the install failed and rolled back on every attempt.
- **Servers listening to 64 queues or more** fetch normally. Hangfire waited on one handle per queue plus one, and `WaitHandle.WaitAny` threw `NotSupportedException` on every idle poll, so the worker backed off for up to 5 minutes. A job enqueued in the same process wakes a worker at once only for its first 63 queues; jobs in the others are found on the next poll.
- **`InactiveStateExpirationTimeout` on a schema older than 9** skips the state history cleanup with one warning when auto-detection found the version. Hangfire ran the cleanup anyway, logged error 8622 at every check and deleted nothing.
- **The expiration manager's lock** is `{SchemaName}:locks:expirationmanager` instead of `locks:expirationmanager`, like the other locks, so storages with different schemas in one database clean up independently. During a rolling upgrade an old and a new server can clean up at the same time; the deletes are idempotent.
- **`Purge`** deletes in batches, each committed on its own, instead of one transaction that timed out on large tables and rolled everything back.
- **Overloaded job methods.** A job runs the overload whose parameter types are exactly the stored ones. Hangfire took the first overload whose parameters accepted them, so a job created for `Handle(Derived)` could run `Handle(Base)` when that overload was declared first. When no overload matches exactly (the method changed after the job was stored), the first accepting overload is still used.
- **`RecurringJobManager.AddOrUpdate` and `UpdateSchedule`** throw `ArgumentOutOfRangeException` for a `MisfireHandlingMode` value that isn't defined. Hangfire stored it, after which the recurring job scheduler failed on every pass, for every recurring job.
- **`LocalRequestsOnlyAuthorizationFilter`** refuses requests that carry `Forwarded`, `X-Forwarded-For`, `X-Forwarded-Host` or `X-Real-IP`, because behind a proxy on the same machine every request looks local. A new virtual `DashboardRequest.GetHeader` lets adapters expose headers.
- **The `netstandard2.0` build of the hosted services uses `IHostApplicationLifetime`.** When the host provides it (the generic host on .NET Framework or .NET 6/7), the server starts after `ApplicationStarted` and gets the stop signal at `ApplicationStopping`, as on .NET 8 and later. Hangfire's `netstandard2.0` build started the server at once. Hosts that don't register it, such as the ASP.NET Core 2.x web host, behave as before. New constructors `BackgroundJobServerHostedService(..., IHostApplicationLifetime)` and `BackgroundProcessingServerHostedService(server, IHostApplicationLifetime)` exist on every target now.
- **A job's DI scope is disposed with `DisposeAsync` on every target.** The `netstandard2.0` build called `Dispose`, which Microsoft.Extensions.DependencyInjection rejects when a scoped service only implements `IAsyncDisposable` (Azure's `ServiceBusClient`, for example): the job failed after it had run, and was retried.
- **Dashboard CSRF protection without antiforgery.** Hangfire's ASP.NET Core middleware skipped CSRF validation silently when `IAntiforgery` wasn't registered. The dashboard now requires a same-origin request (scheme, host and port) with `X-Requested-With` in that case. A form antiforgery can't read because it is too large answers `413`; other antiforgery errors answer `403`.
- **`SqlServerStorageOptions.CountersAggregateInterval`** throws `ArgumentException` for zero or a negative value (the aggregator would run in a tight loop) and `ArgumentOutOfRangeException` above `int.MaxValue` milliseconds.
- **Schema auto-detection respects your settings.** `TryAutoDetectSchemaDependentOptions` no longer overrides `UseRecommendedIsolationLevel`, `DisableGlobalLocks`, `UseIgnoreDupKeyOption` or `DeleteExpiredBatchSize` when you set them, and no longer writes the detected values into the options object you passed. If you set `DisableGlobalLocks = false` or `UseRecommendedIsolationLevel = false` explicitly, you now get what you asked for.
- **`AutomaticRetryAttribute.DelaysInSeconds`** throws `ArgumentException` for an empty array (Hangfire threw `ArgumentNullException`). `null` still means "use `DelayInSecondsByAttemptFunc`".
- **`[Queue]`** validates a plain queue name when the attribute is read, so a method with `[Queue("Billing")]` fails as soon as a job is created for it. A name with placeholders is checked after formatting, and an invalid result throws `InvalidOperationException` naming the template and the result.
- **Logging.** On .NET 8 and later, every Masterly.NightShift logger follows the provider set with `UseLogProvider` or a `Use...LogProvider()` method, including loggers created before the call. `Masterly.NightShift.Core` has a module initializer for this. Calling LibLog's `LogProvider.SetCurrentLogProvider` directly still affects only loggers created after the call.
- **Dashboard state colours.** `JobHistoryRenderer.GetStateCssSuffix` returns `null` instead of `"inherit"` for a state with no suffix, so colours registered with `AddForegroundStateColor`/`AddBackgroundStateColor` reach the state card. Registering a suffix or colour again for the same state replaces it instead of throwing. The parameter of `AddStateCssSuffix` is renamed from `color` to `cssSuffix`, which breaks callers that pass it by name.
- **Dashboard batch commands** (the buttons that act on selected jobs) answer `405` to anything but `POST`, like the single-job commands.

## Dependency changes

- `Masterly.NightShift.AspNetCore` on `netstandard2.0` now requires `System.Security.Cryptography.Xml` 4.7.1+ and `System.Text.Encodings.Web` 4.5.1+. These are the first versions that fix known vulnerabilities in the packages that `Microsoft.AspNetCore.Antiforgery` 2.0.0 depends on.
- `Masterly.NightShift.NetCore` on `net10.0` depends on the 10.0.0 `Microsoft.Extensions.*.Abstractions` packages.

## After 1.0.0 ships

Set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to `1.0.0`. From then on, `dotnet pack` fails on any break against 1.0. Record each intentional break in a `CompatibilitySuppressions.xml` file next to the project (`dotnet pack -p:GenerateCompatibilitySuppressionFile=true` writes it) and list it on this page.
