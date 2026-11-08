# Architecture

This page is for contributors. It explains how the source tree is organized, follows one job from the line of code that creates it to the method call on a server, and shows where new code belongs. For the reasoning behind the larger features, read the [architecture blueprint](../architecture/blueprints/nightshift-next.md); for how the dashboard pages moved off RazorGenerator, read the [dashboard Razor plan](../architecture/dashboard-razor-plan.md).

## Quick example

Here is what happens, in code, when an application runs this line and a server picks the job up:

```csharp
jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));
```

| Step | Type | Folder |
|---|---|---|
| 1. The lambda becomes a `Job` (type, method, arguments) | `Common.Job` | `Common/` |
| 2. The client asks the factory to create it in the Enqueued state | `BackgroundJobClient` | `Jobs/` |
| 3. Client filters run around creation | `BackgroundJobFactory`, `IClientFilter` | `Client/` |
| 4. The job and its parameters are written; the initial state is applied | `CoreBackgroundJobFactory`, `InvocationData`, `IStorageConnection.CreateExpiredJob` | `Client/`, `Storage/` |
| 5. Election and apply filters run; state handlers write side effects in one transaction | `StateMachine`, `CoreStateMachine`, `IStateHandler`, `IWriteOnlyTransaction` | `States/` |
| 6. A worker fetches the job and moves it to Processing | `Worker`, `IFetchedJob`, `BackgroundJobStateChanger` | `Server/`, `States/` |
| 7. Server filters and interceptors run around the method call | `BackgroundJobPerformer`, `CoreBackgroundJobPerformer`, `IServerFilter`, `IJobInterceptor` | `Server/` |
| 8. The method is called through an invoker | `JobInvokerRegistry`, `JobInvoker` | `Common/` |
| 9. The worker moves the job to Succeeded or Failed and acknowledges the queue | `Worker`, `BackgroundJobStateChanger` | `Server/` |

Paths are relative to `src/Masterly.NightShift.Core/`. The sections below explain each step.

## How it works

### Source tree

| Project | Ships as | Contents |
|---|---|---|
| `src/Masterly.NightShift.Core` | `Masterly.NightShift.Core` | Everything storage- and host-independent: client, server, states, filters, dashboard, serialization |
| `src/Masterly.NightShift.SqlServer` | `Masterly.NightShift.SqlServer` | The reference storage implementation |
| `src/Masterly.NightShift.NetCore` | `Masterly.NightShift.NetCore` | Microsoft.Extensions integration: `AddNightShift`, `AddNightShiftServer`, hosted services, DI-backed activator, `ILogger` log provider |
| `src/Masterly.NightShift.AspNetCore` | `Masterly.NightShift.AspNetCore` | Dashboard middleware, `UseNightShiftDashboard`, `MapNightShiftDashboard`, keep-alive registration |
| `src/Masterly.NightShift` | `Masterly.NightShift` | Meta-package that depends on all of the above |
| `src/Masterly.NightShift.SourceGenerator` | Inside `Masterly.NightShift.Core`, as an analyzer | Generates job invokers in consumers' projects |
| `src/Masterly.NightShift.Dashboard.Generator` | Not shipped | Compiles the dashboard's `.cshtml` pages during the build |
| `samples/ConsoleSample`, `samples/NetCoreSample`, `samples/WebSample` | Not shipped | Working examples: a console app, a generic host, and an ASP.NET Core app with the dashboard |
| `samples/CSharp73Check` | Not shipped | Not an example: builds job code as C# 7.3, so the solution build fails if the job invoker generator emits newer syntax |
| `tests/*` | Not shipped | One test project per package; folders mirror `src/` |

Inside `Masterly.NightShift.Core`, the user-facing features are grouped in feature folders:

| Folder | Contents |
|---|---|
| `Jobs/` | `BackgroundJob`, `BackgroundJobClient` and its extensions, batches, continuations, fan-in, `[FromResult]`/`[FromParameter]`, tags, notes, the job catalog, `[GenerateJobInvokers]` |
| `RecurringJobs/` | `RecurringJob`, `RecurringJobManager`, `RecurringJobOptions`, the manifest, misfire handling, the time zone resolver |
| `Scheduling/` | `Cron`, `CronBuilder`, `CronDescriber`, `CronTextParser`, `TimeText` |
| `Hosting/` | `BackgroundJobServer` and its options, `DrainMode`, `JobActivator`, job cancellation tokens |
| `Filters/` | `GlobalJobFilters`, `GlobalJobInterceptors`, `GlobalStateHandlers`, and the attributes in `Retries/`, `Concurrency/`, `Timing/` and `Behavior/` |
| `Monitoring/` | Activity feed, alerts and the webhook sink, audit log, job and queue insights, statistics history |
| `Configuration/` | `GlobalConfiguration`, `CompatibilityLevel` and the `Use*` extension methods |

The extension points and internals keep their original folders:

| Folder | Contents |
|---|---|
| `Client/` | Job creation pipeline: `IBackgroundJobFactory`, client filter interfaces and contexts |
| `Server/` | `Worker`, schedulers, heartbeat, watchdog, `BackgroundProcessingServer`, the performer, server filter interfaces and contexts, interceptors, job console |
| `States/` | `IState`, the built-in states, the state machine, `IBackgroundJobStateChanger`, election and apply filter interfaces |
| `Storage/` | `JobStorage`, `IStorageConnection`, `IWriteOnlyTransaction`, `IMonitoringApi`, their base classes, `JobStorageFeatures`, `InvocationData`, monitoring DTOs |
| `Processing/` | `BackgroundDispatcher` and `BackgroundExecution`: the thread pool and retry/back-off loop every server process runs in |
| `Dashboard/` | Routes, dispatchers, Razor pages (`Pages/`), static content and localized strings (`Content/`) |
| `Diagnostics/` | `TelemetryFilter` and the `NightShiftTelemetry` names (net8.0+), and the job outcome classification that telemetry and insights share |
| `Common/` | `Job`, filter infrastructure, `SerializationHelper` and the JSON converters, `TypeHelper`, job invokers, polyfills |
| `Profiling/` | The slow-call profiler used around filters |
| `Obsolete/` | Types kept only for compatibility |
| `App_Packages/` | Vendored source-only packages (LibLog, StackTraceParser, StackTraceFormatter, MoreLinq); change only their namespaces |

**Namespaces don't follow folders.** Files were moved into feature folders without changing their namespaces, so every type in `Jobs/`, `RecurringJobs/`, `Scheduling/`, `Hosting/`, `Filters/`, `Monitoring/` and `Configuration/` is in the `Masterly.NightShift` namespace. The older folders mostly match their namespaces (`Masterly.NightShift.Server`, `.States`, `.Storage`, `.Client`, `.Dashboard`, `.Common`, `.Processing`, `.Profiling`, `.Diagnostics`), and their subfolders add a level (`Storage/Monitoring/` is `Masterly.NightShift.Storage.Monitoring`). A few public types there are in `Masterly.NightShift` instead: `JobStorage` (`Storage/`), `DashboardOptions` (`Dashboard/`), `ExceptionInfo` and `ExceptionTypeHelper` (`Common/`) and `NightShiftTelemetry` (`Diagnostics/`). `Common/LanguagePolyfills.cs` declares its polyfills in `System.*` namespaces, and `Obsolete/` holds types from several namespaces. Put a new type in the namespace its neighbours use.

The SQL Server project is organized the same way: storage, connection, transaction, monitoring API and options at the root; `Queues/` (`IPersistentJobQueueProvider`, `SqlServerJobQueue`, the sliding-invisibility `SqlServerTimeoutJob` and the legacy `SqlServerTransactionJob`), `Locking/` (`SqlServerDistributedLock`), `Maintenance/` (`ExpirationManager`, `CountersAggregator`, `SqlServerHeartbeatProcess`), `Schema/` (`Install.sql`, the generated `DefaultInstall.sql`, the installer), `Entities/` and `Infrastructure/` (command batching and helpers). `Masterly.NightShift.NetCore` has `DependencyInjection/`, `Hosting/` (hosted services) and `AspNetCore/` (activator and log provider).

### Creating a job

`BackgroundJobClient.Create(job, state)` builds a `CreateContext` and calls `IBackgroundJobFactory.Create`. The public `BackgroundJobFactory` runs the client filters (`IClientFilter.OnCreating`/`OnCreated`, then `IClientExceptionFilter` on failure) around the inner `CoreBackgroundJobFactory`, which:

1. turns the `Job` into `InvocationData`: type and method names through `TypeHelper`, each argument through `SerializationHelper` with the user options;
2. calls `IStorageConnection.CreateExpiredJob`, which stores the job with its parameters and an expiration, so a job whose creation fails halfway disappears on its own;
3. opens an `IWriteOnlyTransaction`, applies the initial state (Enqueued, Scheduled, Awaiting) through the state machine, and commits, which removes the expiration.

A filter can cancel creation, which is why `Create` and every `Enqueue`/`Schedule` overload return `string?`.

### Changing state

Every transition goes through `IBackgroundJobStateChanger`, implemented by `BackgroundJobStateChanger`. It takes a distributed lock on the job, loads the job data, checks the expected current state, and then:

- **election:** `StateMachine` runs the `IElectStateFilter`s, which can replace the candidate state. This is how `AutomaticRetryAttribute` turns Failed into Scheduled, and how many of the concurrency and timing filters work;
- **apply:** `StateMachine` runs `IApplyStateFilter.OnStateUnapplied` for the old state and `OnStateApplied` for the new one, and `CoreStateMachine` runs the `IStateHandler`s registered for the old and new state names (`GlobalStateHandlers` and `JobStorage.GetStateHandlers()`). Handlers do the storage side effects: adding to a queue, to the schedule set, to the succeeded list, setting expirations;
- **commit:** everything is written in one `IWriteOnlyTransaction`.

### Processing a job

`BackgroundJobServer` builds the list of server processes and hands them to `BackgroundProcessingServer`, which runs each one through `BackgroundDispatcher` and `BackgroundExecution` (a loop that retries with back-off when a process throws):

| Process | Does |
|---|---|
| `Worker` (one per `WorkerCount`) | Fetches jobs from the configured queues and performs them |
| `DelayedJobScheduler` | Moves Scheduled jobs whose time has come to Enqueued |
| `RecurringJobScheduler` | Creates jobs for recurring jobs that are due |
| `ServerHeartbeatProcess` | Reports the server as alive |
| `ServerWatchdog` | Removes servers that stopped sending heartbeats |
| `ServerJobCancellationWatcher` | Cancels the tokens of running jobs that were aborted, for example deleted from the dashboard |
| Storage processes | Whatever `JobStorage.GetServerRequiredProcesses()`/`GetStorageWideProcesses()` return; for SQL Server, expiration, counter aggregation and its heartbeat |

A lightweight server (`BackgroundJobServerOptions.IsLightweightServer`) runs only the workers and the processes every server needs (heartbeat, watchdog, cancellation watcher, the storage's required processes and any additional processes): no schedulers and no storage-wide processes.

A `Worker` loop: `IStorageConnection.FetchNextJob(queues)` returns an `IFetchedJob`; the worker changes the job to Processing (expecting Enqueued, Scheduled or Processing), calls `IBackgroundJobPerformer.Perform`, then changes the job to Succeeded or Failed (expecting Processing) and calls `RemoveFromQueue`. If something throws before the job's final state is saved (a storage error while changing state, for example), the worker calls `Requeue` so another worker can take it. A job whose worker died is made visible again by the storage, for SQL Server after its invisibility timeout.

### Performing a job

`BackgroundJobPerformer` runs the server filters (`IServerFilter.OnPerforming`/`OnPerformed`, `IServerExceptionFilter`) around `CoreBackgroundJobPerformer`, which:

1. opens a `JobActivatorScope` (`JobActivator.BeginScope`) and resolves the job's type, unless the method is static;
2. replaces `CancellationToken`, `IJobCancellationToken`, `PerformContext` and `IJobConsole` parameters with live values;
3. gets the method's `JobInvoker` from `JobInvokerRegistry` and runs it inside the interceptor pipeline (`GlobalJobInterceptors`, then interceptor attributes on the type and method);
4. waits for task-returning methods, and returns the result, which becomes the Succeeded state's `Result`.

### Filters

Filters are the main extension mechanism. A `JobFilterAttribute` subclass implements any mix of `IClientFilter`, `IServerFilter`, `IElectStateFilter`, `IApplyStateFilter` and the exception filter interfaces. `JobFilterProviders.Providers` combines `GlobalJobFilters.Filters` with `JobFilterAttributeFilterProvider`, which reads attributes on the job's type and method. Filters are sorted by order and scope, and for an attribute type that doesn't allow multiple instances, only the last one is kept. The default global filters are listed in `Filters/GlobalJobFilters.cs`.

### Storage abstraction

`JobStorage` is the entry point every storage implements. It hands out:

- `IStorageConnection`: reads, job creation, fetching, distributed locks, job parameters;
- `IWriteOnlyTransaction`: atomic writes to jobs, states, queues, sets, hashes, lists and counters;
- `IMonitoringApi`: the queries behind the dashboard;
- server processes and state handlers specific to the storage.

**Third-party storages (Redis, PostgreSQL, MongoDB and others) live in other repositories and implement these interfaces.** Adding a member to an interface breaks every one of them. New capabilities are added instead as:

- a virtual member on `JobStorageConnection`, `JobStorageTransaction` or `JobStorageMonitor`, whose default throws `NotSupportedException` (or does the slow but correct thing);
- a feature ID in `JobStorageFeatures`, which a storage reports through `JobStorage.HasFeature(featureId)`;
- callers that check `HasFeature` and fall back when it's missing. For example, the worker acknowledges a job inside the state transaction only when the storage reports `JobStorageFeatures.Transaction.RemoveFromQueue(...)`.

New features also store their data in the existing primitives (hashes, sets, lists, counters, job parameters), so every storage supports them without changes. The [blueprint](../architecture/blueprints/nightshift-next.md) records, per feature, which keys it writes and how it degrades.

`Masterly.NightShift.SqlServer` is the reference implementation. Its schema lives in one script, `Schema/Install.sql`, embedded as a resource. It steps from `@CURRENT_SCHEMA_VERSION` up to `@TARGET_SCHEMA_VERSION` (currently 9). `DefaultInstall.sql` is generated from it by the `DefaultInstall.tt` T4 template; don't edit it by hand.

### Serialization and compatibility

All JSON goes through `Common/SerializationHelper` and the converters in `Common/JsonConverters.cs`; type names through `Common/TypeHelper`. What is written depends on the configured compatibility level, which any change to stored data must respect. See [Serialization](../configuration/serialization.md) and [Compatibility levels](../configuration/compatibility-levels.md).

### The dashboard and its Razor source generator

`Dashboard/` is a host-independent UI. `DashboardRoutes.Routes`, a `RouteCollection`, maps paths to `IDashboardDispatcher`s: Razor pages, JSON endpoints, commands and embedded static files. `Masterly.NightShift.AspNetCore` adapts it to ASP.NET Core with `AspNetCoreDashboardMiddleware` and `AspNetCoreDashboardContext`.

Pages are `.cshtml` files in `Dashboard/Pages/`, passed to the compiler as `AdditionalFiles`. The `Masterly.NightShift.Dashboard.Generator` source generator (`RazorPageGenerator`, built on `Microsoft.AspNetCore.Razor.Language`) compiles each one, on every build, into a class derived from `RazorPage` in `Masterly.NightShift.Dashboard.Pages`. Hand-written logic for a page goes in the sibling `.cs` partial class, such as `HomePage.cs` next to `HomePage.cshtml`.

- The generator reproduces RazorGenerator's output byte for byte: Windows line endings on every OS, the `@* Generator: ... *@` options at the top of a page (`TypeVisibility`, `TrimLeadingUnderscores`), and lines that hold only a Razor comment keep their whitespace.
- A page that doesn't compile is reported as error `NSDASH001`.
- Nothing generated is checked in. To read the output, build with `-p:EmitCompilerGeneratedFiles=true` and look in `obj/<configuration>/<framework>/generated`.
- UI strings are in `Dashboard/Content/resx/Strings*.resx`, one file per locale; `Strings.Designer.cs` is generated.
- Static files in `Dashboard/Content` must be listed as `EmbeddedResource` in `Masterly.NightShift.Core.csproj`.
- Dates are formatted by `Content/js/nightshift-intl.js` with the browser's `Intl` APIs. It is also Chart.js's date adapter, and its date arithmetic matches moment.js exactly, including across daylight saving changes, because dashboard extensions and the graphs rely on that.

### The job invoker source generator

`Masterly.NightShift.SourceGenerator` (`JobInvokerGenerator`) ships inside the `Masterly.NightShift.Core` package as an analyzer, so it runs in every project that references the package. For each job method it finds, it emits a `JobInvoker` subclass that calls the method directly instead of through `MethodInfo.Invoke`. It finds methods in two ways:

- the expression lambdas passed to client APIs (`Enqueue`, `Schedule`, `AddOrUpdate` and similar) in the same project;
- every public method of a type marked `[GenerateJobInvokers]`, for jobs created from another assembly.

Generated invokers register with `JobInvokerRegistry` through a module initializer, or, where the target framework has none (.NET Framework consumers of `netstandard2.0`), through an assembly-level `JobInvokersProviderAttribute` that the registry scans for. The registry resolves a method to a generated invoker first, then a runtime-compiled one (unless `UseCompiledJobInvokers(false)`), then reflection, and caches the result.

The generator never fails a build: a method it can't call safely is skipped, and an unexpected error becomes warning `NSGEN001`. Consumers turn it off with `<NightShiftDisableJobInvokerGeneration>true</NightShiftDisableJobInvokerGeneration>`, which `build/Masterly.NightShift.Core.props` makes visible to the generator. `Microsoft.CodeAnalysis.CSharp` is kept at the oldest version the generators need so the analyzer still loads in older compilers. `tests/Masterly.NightShift.Core.Tests` references the generator as an analyzer, so `GeneratedJobInvokerFacts` tests real generated code. See [Source-generated invokers](../jobs/source-generated-invokers.md) for the user's view.

### Hosting integration

`Masterly.NightShift.NetCore` registers the configuration, the storage, the client and recurring job manager factories, and `BackgroundJobServerHostedService` for each `AddNightShiftServer` call. `AspNetCoreJobActivator` opens one DI scope per job; `AspNetCoreLogProvider` sends log messages to `ILoggerFactory`. See [Dependency injection](../server/dependency-injection.md).

## Where to add things

| You want to add | Put it in | Also |
|---|---|---|
| A filter attribute | `Filters/Retries`, `Concurrency`, `Timing` or `Behavior` (namespace `Masterly.NightShift`) | Add it to `GlobalJobFilters` only if every job needs it; list new default filters in `docs/breaking-changes.md` |
| A state | `States/`, with an `IStateHandler` registered in `GlobalStateHandlers` if it has storage side effects | Make `SerializeData` respect the compatibility level |
| A configuration method | `Configuration/GlobalConfigurationExtensions.cs` | Document it in [Global configuration](../configuration/global-configuration.md) |
| A storage capability | A virtual member on `JobStorageConnection`/`JobStorageTransaction`/`JobStorageMonitor` and an ID in `JobStorageFeatures` | Implement it in `Masterly.NightShift.SqlServer` and report the feature; check `HasFeature` in callers and fall back |
| A SQL Server schema change | A new versioned block at the end of `Schema/Install.sql`, and a higher `@TARGET_SCHEMA_VERSION` | Regenerate `DefaultInstall.sql` from `DefaultInstall.tt` |
| A server process | An `IBackgroundProcess` in `Server/`, added in `BackgroundJobServer` or returned by a storage | Make it stop promptly when the stopping token fires |
| A dashboard page | A `.cshtml` in `Dashboard/Pages/` (plus a `.cs` partial if needed) and a route in `DashboardRoutes` | Strings in `Strings.resx`; new static files as `EmbeddedResource`; a rendering test with `PageRenderer` |
| A client API | `Jobs/` or `RecurringJobs/`, as an extension method or a new interface (such as `IRecurringJobManagerV3`) | Never a new member on `IBackgroundJobClient` or `IRecurringJobManager` |
| DI registration | `src/Masterly.NightShift.NetCore/DependencyInjection/` | Tests in `tests/Masterly.NightShift.AspNetCore.Tests` |
| Telemetry | `Diagnostics/TelemetryFilter.cs`, behind `#if NET8_0_OR_GREATER` | Check `HasListeners()`/`Enabled` before doing any work |

Every new or changed file in `src/` gets tests in the matching folder under `tests/`.

## See also

- [Contributing](README.md)
- [Architecture blueprint](../architecture/blueprints/nightshift-next.md)
- [Dashboard Razor plan](../architecture/dashboard-razor-plan.md)
- [Core concepts](../getting-started/core-concepts.md)
- [Custom storage](../storage/custom-storage.md)
- [Custom filters](../filters/custom-filters.md)
