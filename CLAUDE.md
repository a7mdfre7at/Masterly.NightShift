# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Hangfire is a .NET library for fire-and-forget, delayed, recurring, and continuation background jobs, persisted to pluggable storage. This repo ships the NuGet packages `Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.NetCore`, `Hangfire.AspNetCore`, and the `Hangfire` meta-package (`nuspecs/`). It is a library consumed by many old and new runtimes, so **binary/API compatibility across a wide range of target frameworks is a primary constraint**.

## Build and test

Full build (Windows only): `build.bat` runs psake (`psake-project.ps1`) after restoring tools from `.nuget/packages.config` into `packages/`. Task chain: `Compile → Merge → Test → Collect → Pack → Sign`.

```
build            # default task = Pack (compile, ILRepack merge, test, collect, nupkg + zip into build/)
build test       # stop after tests
build pack
```

Run `build` before proposing a pull request (per README).

Day-to-day with the dotnet CLI:

```
dotnet build Hangfire.sln -c Release
dotnet test tests/Hangfire.Core.Tests -f net8.0
dotnet test tests/Hangfire.Core.Tests -f net8.0 --filter "FullyQualifiedName~Hangfire.Core.Tests.Server.WorkerFacts"
dotnet test tests/Hangfire.Core.Tests -f net8.0 --filter "FullyQualifiedName~WorkerFacts.Execute_TakesConnectionAndReleasesIt"
```

- Test projects target `net8.0;net10.0`. Use `-f` to pick one. Building requires the .NET 10 SDK (pinned in `global.json`).
- `TreatWarningsAsErrors` is on for all `src/` projects, and .NET analyzers run in `Recommended` mode (`src/Directory.Build.props`). Analyzer warnings break the build.
- Package versions are managed centrally in `Directory.Packages.props`; `PackageReference` items have no `Version`. Library packages deliberately reference the lowest versions they support (Hangfire.Core keeps Newtonsoft.Json 11.0.1 through `VersionOverride`), so raising one changes what consumers get.
- NuGet lock files (`packages.lock.json`) are used. After changing a package, run `dotnet restore Hangfire.sln --force-evaluate`. CI (GitHub Actions and AppVeyor) restores in locked mode.
- **Public API changes are checked automatically.** `dotnet pack` (and the `Validate` step of `build`) compares each package with the 1.8.25 release (`PackageValidationBaselineVersion` in `src/Directory.Build.props`) and fails on any break. To check one project: `dotnet pack src/Hangfire.Core -c Release`. If a break is intentional, add `-p:ApiCompatGenerateSuppressionFile=true` to regenerate that project's `CompatibilitySuppressions.xml`, and record the break in `docs/breaking-changes-2.0.md`.

### Test prerequisites

- **SQL Server tests** need a reachable SQL Server. Set `Hangfire_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` database placeholder, for example `Server=.\sqlexpress;Database={0};Integrated Security=True;TrustServerCertificate=True;` or, with LocalDB, `Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;`. The default is `Server=.\;Database={0};Trusted_Connection=True;...`. Each TFM uses its own database (`Hangfire.SqlServer.Tests.net8_0`, and so on), created on demand with a case-sensitive collation. To override the name, set `Hangfire_SqlServer_DatabaseName`.
- In `psake`, tests run against the **ILRepack-merged** assemblies (Cronos, Dapper, etc. internalized), not the plain build output. Test csproj files use `extern alias` (`ReferencedCronos`, `ReferencedDapper`) so tests can reference those packages without clashing with the internalized copies.

## Architecture

### Job lifecycle (Hangfire.Core)

1. **Client side** (`Client/`, `BackgroundJobClient`, `BackgroundJob` static facade, `RecurringJobManager`): a lambda expression is turned into a `Common.Job` (type + method + args). `Storage.InvocationData` serializes it. `IBackgroundJobFactory` → `CoreBackgroundJobFactory` creates it in storage, running client filters (`IClientFilter`) around creation.
2. **State machine** (`States/`): every transition goes through `IBackgroundJobStateChanger` → `StateMachine`/`CoreStateMachine`. Election filters (`IElectStateFilter`) can redirect the candidate state, which is how `AutomaticRetryAttribute` turns `Failed` into `Scheduled`. Apply filters (`IApplyStateFilter`) and `IStateHandler`s (`GlobalStateHandlers`) then perform the storage side effects inside one `IWriteOnlyTransaction`. Built-in states: Enqueued, Scheduled, Processing, Succeeded, Failed, Deleted, Awaiting (continuations).
3. **Server side** (`Server/`, `Processing/`): `BackgroundJobServer` builds the list of processes: `Worker` (pool of `WorkerCount` threads), `DelayedJobScheduler`, `RecurringJobScheduler`, `ServerHeartbeatProcess`, `ServerWatchdog`, plus any storage-provided processes. It hands them to `BackgroundProcessingServer`, which runs them through `Processing/BackgroundDispatcher` + `BackgroundExecution` (retry/backoff loop). `Worker` fetches a job, moves it to Processing, calls `IBackgroundJobPerformer` (server filters `IServerFilter` wrap `CoreBackgroundJobPerformer`), then moves it to Succeeded/Failed.
4. **Filters** are the main extension mechanism. `JobFilterAttribute` subclasses implement any of the client/server/state filter interfaces. They are discovered via `GlobalJobFilters` and `JobFilterAttributeFilterProvider`, which reads attributes on the job's type and method.
5. **Job activation**: `JobActivator` / `JobActivatorScope`. `Hangfire.NetCore` supplies the DI-container-backed activator and `IHostedService` integration. `Hangfire.AspNetCore` adds dashboard middleware and `IApplicationBuilder` extensions.

### Storage abstraction

`JobStorage` is the entry point every storage implements. It exposes `IStorageConnection` (reads, fetch, distributed locks), `IWriteOnlyTransaction` (atomic writes), `IMonitoringApi` (dashboard queries), and optional extra server processes. Newer optional capabilities are advertised through `JobStorage.HasFeature(...)` / `JobStorageFeatures` and the `JobStorageConnection` / `JobStorageTransaction` base classes, rather than new interface members. **Adding a member to these interfaces breaks third-party storages** (Redis, Postgres, Mongo, and others live in other repos), so extend via features/base classes.

`Hangfire.SqlServer` is the reference implementation:

- `SqlServerStorage` / `SqlServerStorageOptions`: configuration and connection management (supports both `System.Data.SqlClient` and `Microsoft.Data.SqlClient`).
- `SqlServerConnection`, `SqlServerWriteOnlyTransaction` (batched via `SqlCommandBatch`/`SqlCommandSet`), `SqlServerMonitoringApi`, `SqlServerDistributedLock`.
- Queues are pluggable via `IPersistentJobQueueProvider`. `SqlServerJobQueue` is the default. Fetching happens either through sliding invisibility timeouts (`SqlServerTimeoutJob`) or the legacy transactional mode (`SqlServerTransactionJob`).
- Background maintenance: `ExpirationManager`, `CountersAggregator`, `SqlServerHeartbeatProcess`.
- **Schema migrations** live in the single script `Install.sql`, embedded as a resource. It steps through `@CURRENT_SCHEMA_VERSION` up to `@TARGET_SCHEMA_VERSION`, which is currently 9. A schema change means adding a new versioned block and bumping the target. `DefaultInstall.sql` is generated from it by the `DefaultInstall.tt` T4 template, so don't hand-edit it.

### Telemetry

`UseTelemetry()` (net8.0+ only, so no new package dependencies) registers `Diagnostics/TelemetryFilter`, a global client and server filter. It emits `create`/`process` spans from the `Hangfire` `ActivitySource` and the `hangfire.job.*` instruments from the `Hangfire` `Meter` (names in `HangfireTelemetry`). The creating trace context travels in the `TraceParent`/`TraceState` job parameters. It must stay cheap when nobody listens: check `HasListeners()`/`Enabled` before doing work, and never read job parameters otherwise. `tests/Hangfire.SqlServer.Tests/TelemetryFacts.cs` checks the whole path end to end.

### Data compatibility

`GlobalConfiguration.SetDataCompatibilityLevel(CompatibilityLevel.Version_110/170/180)` controls the serialized payload format, so servers on older versions can still process jobs. Any change to serialization (`InvocationData`, state data, `SerializationHelper`) must respect the configured level. Core tests use `[DataCompatibilityRangeFact]` / `[DataCompatibilityRangeTheory]` (`tests/Hangfire.Core.Tests/Utils/`) to run a test across a range of levels: each test case is repeated per level (display name suffix like `[Version_170]`) and sets `GlobalConfiguration.CompatibilityLevel` in `PreInvoke`/`PostInvoke`.

### Dashboard

`Dashboard/` is a self-contained, host-agnostic UI: `RouteCollection`/`DashboardRoutes` map paths to `IDashboardDispatcher`s (Razor pages, JSON stats, commands, embedded static resources). The ASP.NET Core adapter (`Hangfire.AspNetCore`) sits on top. OWIN support was removed.

- Pages are `.cshtml` files compiled to C# during every build by the `Hangfire.Dashboard.Generator` source generator (`src/Hangfire.Dashboard.Generator`, built on `Microsoft.AspNetCore.Razor.Language`, never shipped). Hand-written partial logic goes in the sibling `.cs` files. Nothing generated is checked in; to see the output, build with `-p:EmitCompilerGeneratedFiles=true` and look in `obj/<config>/<tfm>/generated`. The generator reproduces RazorGenerator's output byte for byte: Windows line endings on every OS, the `@* Generator: ... *@` options (visibility, `TrimLeadingUnderscores`), and lines holding only a Razor comment keep their whitespace.
- UI strings live in `Dashboard/Content/resx/Strings*.resx`, one file per locale. `Strings.Designer.cs` is generated.
- Static assets in `Dashboard/Content` must be listed as `EmbeddedResource` in `Hangfire.Core.csproj`.
- Dates are formatted by `Content/js/hangfire-intl.js` (browser `Intl` APIs). It's also Chart.js 2.x's date adapter, so moment.js isn't used. Its date arithmetic matches moment's exactly, including across daylight saving changes. Keep it that way: dashboard extensions and the realtime and history graphs rely on it. How the pages were moved off RazorGenerator is recorded in `docs/dashboard-razor-plan.md`.

### Multi-targeting

Every library package targets `netstandard2.0;net8.0;net10.0`. `netstandard2.0` is what .NET Framework (4.6.2+) consumers get. Code that needs newer BCL APIs is gated with `#if NET8_0_OR_GREATER`, and anything shared must still compile for `netstandard2.0`, so check before using a newer API. Small polyfills live in `Common/LanguagePolyfills.cs`. `App_Packages/` holds vendored source-only packages (LibLog, StackTraceParser/Formatter), so don't modify them.

## Conventions

- Match the existing code style, not modern C# defaults: block-scoped namespaces, `var` is used freely, and parameters are annotated with `[NotNull]`/`[CanBeNull]` from `Hangfire.Annotations`. Nullable reference types are being enabled file by file with `#nullable enable` (done: the client API (`BackgroundJob*`, `IBackgroundJobClient`, `RecurringJob*`, `IRecurringJobManager`), `BackgroundJobServer*`, the client/server/state filter interfaces and contexts, `IState` with the built-in states, and the storage contract (`JobStorage`, `IStorageConnection`, `JobStorageConnection`, `IWriteOnlyTransaction`, `JobStorageTransaction`, `IFetchedJob`, `JobData`, `StateData`), and the monitoring API with its DTOs). The whole public API that users, filter authors and storage authors use is annotated; internal and dashboard code isn't yet. For storage interfaces, base each annotation on evidence: what Hangfire passes, and what `Hangfire.SqlServer` returns. When converting a file, keep the JetBrains attributes, turn `[CanBeNull]` into `?`, and fix what the compiler reports instead of suppressing it. The compiler can't check interfaces, or values that come from files not converted yet, so review those by hand. Job IDs returned by `Create`/`Enqueue`/`Schedule`/`ContinueJobWith` are deliberately `string?`, because creation can be canceled by filters. List consumer-visible changes in `docs/breaking-changes-2.0.md`. `LangVersion` is `Latest`, but code must still compile for `netstandard2.0`, which .NET Framework 4.6.2+ consumers use.
- Each source file starts with the LGPL license header (copy it from an existing file). SQL scripts use the same header as `--` comments.
- Tests use xUnit v3 (`xunit.v3.mtp-off`, i.e. the classic VSTest runner, so `dotnet test` and `--filter` work as usual) + Moq. Test classes are named `*Facts` and mirror the `src/` folder layout. Reusable context mocks are in `tests/Hangfire.Core.Tests/Mocks`. Both test assemblies run with `[assembly: Parallelization(MaxThreads = 1)]`. Custom `BeforeAfterTestAttribute`s must not use `Monitor` (v3 can call `After` on another thread); use `SemaphoreSlim`. SQL Server tests use `[CleanDatabase]` to truncate tables between tests, and tests that touch static/global state use `[GlobalLock]`/`[StaticLock]`.
- Keep Moq at 4.10.0. Newer versions break tests (see issue #2266, noted in `tests/Directory.Build.props`).
- Don't edit the version in `appveyor.yml` by hand. Use `build.bat version`.
- CI: `.github/workflows/ci.yml` runs `build.bat pack` on Windows (LocalDB) and the tests on Linux (SQL Server container) for every push and PR. AppVeyor still signs and publishes releases.
- Indentation is 4 spaces (`.editorconfig`).
