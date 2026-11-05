# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Masterly.NightShift started from the source of an earlier open-source library (see `NOTICE.md`) and was renamed throughout. Existing data keeps working: the SQL Server schema is still named `HangFire`, the drain-mode storage key is still `hangfire:control`, and `TypeHelper` maps type names stored by the earlier library (`Hangfire.*, Hangfire.Core`) to the new types. Files that carry a license header naming the earlier project must keep it (`NOTICE.md`). Everything written for this project is MIT (`LICENSE.txt`).

Masterly.NightShift is a .NET library for fire-and-forget, delayed, recurring, and continuation background jobs, persisted to pluggable storage. This repo ships the NuGet packages `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer`, `Masterly.NightShift.NetCore`, `Masterly.NightShift.AspNetCore`, and the `Masterly.NightShift` meta-package (`src/Masterly.NightShift`). It is a library consumed by many old and new runtimes, so **binary/API compatibility across a wide range of target frameworks is a primary constraint**.

## Build and test

Everything builds and packs with the dotnet CLI; package metadata (authors, license, icon, readme, version) lives in `src/Directory.Build.props`, and each package's description in its `.csproj`. `Masterly.NightShift.Core` packs the source generator as an analyzer and `build/Masterly.NightShift.Core.props`. Cronos and Dapper are normal package dependencies (they are no longer merged into the assemblies).

```
dotnet build Masterly.NightShift.slnx -c Release
dotnet pack src/Masterly.NightShift.Core -c Release --no-build -o artifacts   # likewise SqlServer, NetCore, AspNetCore, and src/Masterly.NightShift (meta-package)
```

Run the build and the tests on both `net8.0` and `net10.0` before proposing a pull request.

Day-to-day with the dotnet CLI:

```
dotnet build Masterly.NightShift.slnx -c Release
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0 --filter "FullyQualifiedName~Masterly.NightShift.Core.Tests.Server.WorkerFacts"
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0 --filter "FullyQualifiedName~WorkerFacts.Execute_TakesConnectionAndReleasesIt"
```

- Test projects target `net8.0;net10.0`. Use `-f` to pick one. `tests/Masterly.NightShift.Core.Tests` and `tests/Masterly.NightShift.SqlServer.Tests` test their packages; `tests/Masterly.NightShift.AspNetCore.Tests` tests `Masterly.NightShift.NetCore` and `Masterly.NightShift.AspNetCore` together (DI registration, hosted services, endpoint mapping, keep-alive). Building requires the .NET 10 SDK (pinned in `global.json`).
- `TreatWarningsAsErrors` is on for all `src/` projects, and .NET analyzers run in `Recommended` mode (`src/Directory.Build.props`). Analyzer warnings break the build.
- Package versions are managed centrally in `Directory.Packages.props`; `PackageReference` items have no `Version`. Library dependencies are kept current (`dotnet list Masterly.NightShift.slnx package --outdated`); raising one raises the minimum consumers get. Exceptions: `Microsoft.CodeAnalysis.CSharp` stays at the oldest Roslyn the generators need (see the comment there), and the source-only packages (LibLog, StackTraceParser/Formatter, MoreLinq) are references with `ExcludeAssets="contentFiles"`: the code compiled is the vendored copy, so bumping them changes nothing. System.Text.Json is referenced on `netstandard2.0` only.
- NuGet lock files (`packages.lock.json`) are used. After changing a package, run `dotnet restore Masterly.NightShift.slnx --force-evaluate`. CI restores in locked mode.
- **The public API has no baseline yet.** Nothing has been published under the `Masterly.NightShift.*` IDs. `dotnet pack` runs package validation, which checks that the API agrees across target frameworks. After the first release is on nuget.org, set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to it, so later breaks are caught, and record intentional ones in `docs/breaking-changes-2.0.md`.

### Test prerequisites

- **SQL Server tests** need a reachable SQL Server. Set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` database placeholder, for example `Server=.\sqlexpress;Database={0};Integrated Security=True;TrustServerCertificate=True;` or, with LocalDB, `Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;`. The default is `Server=.\;Database={0};Trusted_Connection=True;...`. Each TFM uses its own database (`Masterly.NightShift.SqlServer.Tests.net8_0`, and so on), created on demand with a case-sensitive collation. To override the name, set `NightShift_SqlServer_DatabaseName`.
- Test projects use `extern alias ReferencedCronos` (see the `ChangeAliasesOfStrongNameAssemblies` target) so tests can reference Cronos without clashing with the copy the library uses.

## Architecture

### Job lifecycle (Masterly.NightShift.Core)

1. **Client side** (`Client/`, `BackgroundJobClient`, `BackgroundJob` static facade, `RecurringJobManager`): a lambda expression is turned into a `Common.Job` (type + method + args). `Storage.InvocationData` serializes it. `IBackgroundJobFactory` → `CoreBackgroundJobFactory` creates it in storage, running client filters (`IClientFilter`) around creation.
2. **State machine** (`States/`): every transition goes through `IBackgroundJobStateChanger` → `StateMachine`/`CoreStateMachine`. Election filters (`IElectStateFilter`) can redirect the candidate state, which is how `AutomaticRetryAttribute` turns `Failed` into `Scheduled`. Apply filters (`IApplyStateFilter`) and `IStateHandler`s (`GlobalStateHandlers`) then perform the storage side effects inside one `IWriteOnlyTransaction`. Built-in states: Enqueued, Scheduled, Processing, Succeeded, Failed, Deleted, Awaiting (continuations).
3. **Server side** (`Server/`, `Processing/`): `BackgroundJobServer` builds the list of processes: `Worker` (pool of `WorkerCount` threads), `DelayedJobScheduler`, `RecurringJobScheduler`, `ServerHeartbeatProcess`, `ServerWatchdog`, plus any storage-provided processes. It hands them to `BackgroundProcessingServer`, which runs them through `Processing/BackgroundDispatcher` + `BackgroundExecution` (retry/backoff loop). `Worker` fetches a job, moves it to Processing, calls `IBackgroundJobPerformer` (server filters `IServerFilter` wrap `CoreBackgroundJobPerformer`), then moves it to Succeeded/Failed.
4. **Filters** are the main extension mechanism. `JobFilterAttribute` subclasses implement any of the client/server/state filter interfaces. They are discovered via `GlobalJobFilters` and `JobFilterAttributeFilterProvider`, which reads attributes on the job's type and method.
5. **Job activation**: `JobActivator` / `JobActivatorScope`. `Masterly.NightShift.NetCore` supplies the DI-container-backed activator and `IHostedService` integration. `Masterly.NightShift.AspNetCore` adds dashboard middleware and `IApplicationBuilder` extensions.

### Storage abstraction

`JobStorage` is the entry point every storage implements. It exposes `IStorageConnection` (reads, fetch, distributed locks), `IWriteOnlyTransaction` (atomic writes), `IMonitoringApi` (dashboard queries), and optional extra server processes. Newer optional capabilities are advertised through `JobStorage.HasFeature(...)` / `JobStorageFeatures` and the `JobStorageConnection` / `JobStorageTransaction` base classes, rather than new interface members. **Adding a member to these interfaces breaks third-party storages** (Redis, Postgres, Mongo, and others live in other repos), so extend via features/base classes.

`Masterly.NightShift.SqlServer` is the reference implementation:

- `SqlServerStorage` / `SqlServerStorageOptions`: configuration and connection management (supports both `System.Data.SqlClient` and `Microsoft.Data.SqlClient`).
- `SqlServerConnection`, `SqlServerWriteOnlyTransaction` (batched via `SqlCommandBatch`/`SqlCommandSet`), `SqlServerMonitoringApi`, `SqlServerDistributedLock`.
- Queues are pluggable via `IPersistentJobQueueProvider`. `SqlServerJobQueue` is the default. Fetching happens either through sliding invisibility timeouts (`SqlServerTimeoutJob`) or the legacy transactional mode (`SqlServerTransactionJob`).
- Background maintenance: `ExpirationManager`, `CountersAggregator`, `SqlServerHeartbeatProcess`.
- **Schema migrations** live in the single script `Install.sql`, embedded as a resource. It steps through `@CURRENT_SCHEMA_VERSION` up to `@TARGET_SCHEMA_VERSION`, which is currently 9. A schema change means adding a new versioned block and bumping the target. `DefaultInstall.sql` is generated from it by the `DefaultInstall.tt` T4 template, so don't hand-edit it.

### Telemetry

`UseTelemetry()` (net8.0+ only, so no new package dependencies) registers `Diagnostics/TelemetryFilter`, a global client and server filter. It emits `create`/`process` spans from the `Masterly.NightShift` `ActivitySource` and the `nightshift.job.*` instruments from the `Masterly.NightShift` `Meter` (names in `NightShiftTelemetry`). The creating trace context travels in the `TraceParent`/`TraceState` job parameters. It must stay cheap when nobody listens: check `HasListeners()`/`Enabled` before doing work, and never read job parameters otherwise. `tests/Masterly.NightShift.SqlServer.Tests/TelemetryFacts.cs` checks the whole path end to end.

### JSON serialization

All JSON goes through System.Text.Json; Newtonsoft.Json is not referenced. `Common/SerializationHelper` holds isolated internal options and the user options set by `UseSerializerSettings(JsonSerializerOptions)` / `UseRecommendedSerializerSettings(...)`. The options read leniently (case-insensitive names, quoted numbers, enum names or numbers, public fields), so data written by the previous Newtonsoft-based versions stays readable. `Common/JsonConverters.cs` adds what System.Text.Json lacks: `IState` values are written with a leading `"$type"` (`StateJsonConverter`, used by `SerializationOption.TypedInternal`), `object` values become primitives (and keep `"$type"` under the internal/recommended options), arrays may be wrapped as `{"$type","$values"}`, plus `System.Type`, `CultureInfo` and `XElement`. These converters are appended to user options too, after the user's own. Dashboard JSON uses `Dashboard/DashboardJson.Create(...)` (camelCase, UTC dates).

### Data compatibility

`GlobalConfiguration.SetDataCompatibilityLevel(CompatibilityLevel.Version_110/170/180)` controls the serialized payload format, so servers on older versions can still process jobs. Any change to serialization (`InvocationData`, state data, `SerializationHelper`) must respect the configured level. Core tests use `[DataCompatibilityRangeFact]` / `[DataCompatibilityRangeTheory]` (`tests/Masterly.NightShift.Core.Tests/Utils/`) to run a test across a range of levels: each test case is repeated per level (display name suffix like `[Version_170]`) and sets `GlobalConfiguration.CompatibilityLevel` in `PreInvoke`/`PostInvoke`.

### Dashboard

`Dashboard/` is a self-contained, host-agnostic UI: `RouteCollection`/`DashboardRoutes` map paths to `IDashboardDispatcher`s (Razor pages, JSON stats, commands, embedded static resources). The ASP.NET Core adapter (`Masterly.NightShift.AspNetCore`) sits on top. OWIN support was removed.

- Pages are `.cshtml` files compiled to C# during every build by the `Masterly.NightShift.Dashboard.Generator` source generator (`src/Masterly.NightShift.Dashboard.Generator`, built on `Microsoft.AspNetCore.Razor.Language`, never shipped). Hand-written partial logic goes in the sibling `.cs` files. Nothing generated is checked in; to see the output, build with `-p:EmitCompilerGeneratedFiles=true` and look in `obj/<config>/<tfm>/generated`. The generator reproduces RazorGenerator's output byte for byte: Windows line endings on every OS, the `@* Generator: ... *@` options (visibility, `TrimLeadingUnderscores`), and lines holding only a Razor comment keep their whitespace.
- UI strings live in `Dashboard/Content/resx/Strings*.resx`, one file per locale. `Strings.Designer.cs` is generated.
- Static assets in `Dashboard/Content` must be listed as `EmbeddedResource` in `Masterly.NightShift.Core.csproj`.
- Dates are formatted by `Content/js/nightshift-intl.js` (browser `Intl` APIs). It's also Chart.js 2.x's date adapter, so moment.js isn't used. Its date arithmetic matches moment's exactly, including across daylight saving changes. Keep it that way: dashboard extensions and the realtime and history graphs rely on it. How the pages were moved off RazorGenerator is recorded in `docs/dashboard-razor-plan.md`.

### Multi-targeting

Every library package targets `netstandard2.0;net8.0;net10.0`. `netstandard2.0` is what .NET Framework (4.6.2+) consumers get. Code that needs newer BCL APIs is gated with `#if NET8_0_OR_GREATER`, and anything shared must still compile for `netstandard2.0`, so check before using a newer API. Small polyfills live in `Common/LanguagePolyfills.cs`. `App_Packages/` holds vendored source-only packages (LibLog, StackTraceParser/Formatter), so don't modify them beyond their namespaces, which follow the rename.

## Conventions

- Match the existing code style, not modern C# defaults: block-scoped namespaces, `var` is used freely, and parameters are annotated with `[NotNull]`/`[CanBeNull]` from `Masterly.NightShift.Annotations`. Nullable reference types are being enabled file by file with `#nullable enable` (done: the client API (`BackgroundJob*`, `IBackgroundJobClient`, `RecurringJob*`, `IRecurringJobManager`), `BackgroundJobServer*`, the client/server/state filter interfaces and contexts, `IState` with the built-in states, and the storage contract (`JobStorage`, `IStorageConnection`, `JobStorageConnection`, `IWriteOnlyTransaction`, `JobStorageTransaction`, `IFetchedJob`, `JobData`, `StateData`), and the monitoring API with its DTOs). The whole public API that users, filter authors and storage authors use is annotated; internal and dashboard code isn't yet. For storage interfaces, base each annotation on evidence: what Masterly.NightShift passes, and what `Masterly.NightShift.SqlServer` returns. When converting a file, keep the JetBrains attributes, turn `[CanBeNull]` into `?`, and fix what the compiler reports instead of suppressing it. The compiler can't check interfaces, or values that come from files not converted yet, so review those by hand. Job IDs returned by `Create`/`Enqueue`/`Schedule`/`ContinueJobWith` are deliberately `string?`, because creation can be canceled by filters. List consumer-visible changes in `docs/breaking-changes-2.0.md`. `LangVersion` is `Latest`, but code must still compile for `netstandard2.0`, which .NET Framework 4.6.2+ consumers use.
- Files inherited from the earlier project start with its LGPL license header, which must stay; new files need no header. SQL scripts use the same header as `--` comments.
- Tests use xUnit v3 (`xunit.v3.mtp-off`, i.e. the classic VSTest runner, so `dotnet test` and `--filter` work as usual) + Moq. Test classes are named `*Facts` and mirror the `src/` folder layout. Reusable context mocks are in `tests/Masterly.NightShift.Core.Tests/Mocks`. All test assemblies run with `[assembly: Parallelization(MaxThreads = 1)]`. Dashboard pages are tested by rendering them with `tests/Masterly.NightShift.Core.Tests/Dashboard/Pages/PageRenderer.cs`, which mocks the storage with empty answers. Custom `BeforeAfterTestAttribute`s must not use `Monitor` (v3 can call `After` on another thread); use `SemaphoreSlim`. SQL Server tests use `[CleanDatabase]` to truncate tables between tests, and tests that touch static/global state use `[GlobalLock]`/`[StaticLock]`.
- To release, raise `<Version>` in `src/Directory.Build.props` and `AssemblyVersion` in `src/SharedAssemblyInfo.cs`, and add the notes to `CHANGELOG.md`.
- CI: `.github/workflows/build.yml` builds, tests and packs on Windows (LocalDB) and runs the tests on Linux (SQL Server container) for every push and PR. `publish.yml` pushes the packages to nuget.org after a successful Build on `main` (needs the `NUGET_API_KEY` secret; versions already published are skipped). `pages.yml` publishes `site/`. `codeql-analysis.yml` runs CodeQL.
- Indentation is 4 spaces (`.editorconfig`).
