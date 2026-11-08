# Contributing

This section is for people who work on Masterly.NightShift itself: fixing a bug, adding a feature, or preparing a release. It covers the tools you need, how to build, test and pack, the rules the code follows, and how releases and CI work. Using the library in an application is covered in the rest of the documentation.

| Page | What it covers |
|---|---|
| [Contributing](README.md) (this page) | Prerequisites, building, testing, conventions, releasing, CI |
| [Architecture](architecture.md) | How the source tree is organized and how a job flows through the code |

Bug reports and pull requests go through GitHub, as described in [CONTRIBUTING.md](../../CONTRIBUTING.md). Report security problems as described in [SECURITY.md](../../SECURITY.md), not in a public issue.

## Prerequisites

- **The .NET 10 SDK.** [`global.json`](../../global.json) pins `10.0.100` and rolls forward to later feature bands, so any .NET 10 SDK works.
- **The .NET 8 runtime**, to run the tests on `net8.0`.
- **SQL Server** for the `Masterly.NightShift.SqlServer` tests: LocalDB, SQL Server Express, a full instance, or a container. See [SQL Server tests](#sql-server-tests).

Any editor works. The solution file is `Masterly.NightShift.slnx` (the XML solution format), which recent Visual Studio and Rider versions and the dotnet CLI open.

## Build and pack

```
dotnet build Masterly.NightShift.slnx -c Release
```

Pack each package into `artifacts`:

```
dotnet pack src/Masterly.NightShift.Core -c Release --no-build -o artifacts
dotnet pack src/Masterly.NightShift.SqlServer -c Release --no-build -o artifacts
dotnet pack src/Masterly.NightShift.NetCore -c Release --no-build -o artifacts
dotnet pack src/Masterly.NightShift.AspNetCore -c Release --no-build -o artifacts
dotnet pack src/Masterly.NightShift -c Release --no-build -o artifacts
```

The last one is the `Masterly.NightShift` meta-package, which depends on the others. `Masterly.NightShift.Core` also packs the job invoker source generator as an analyzer and `build/Masterly.NightShift.Core.props`, which lets consumers turn the generator off.

Shared package metadata (authors, license, icon, readme, version) lives in [`src/Directory.Build.props`](../../src/Directory.Build.props). Each package's description is in its own `.csproj`.

`dotnet pack` runs package validation, which checks that each package's public API is the same across its target frameworks. Nothing has been published under the `Masterly.NightShift.*` IDs yet, so there is no baseline release to compare with. After the first release is on nuget.org, set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to it, so later breaking changes fail the build.

## Tests

There are three test projects, each targeting `net48`, `net8.0` and `net10.0`. `net48` runs the libraries' `netstandard2.0` build on .NET Framework, so it runs on Windows only:

| Project | Tests |
|---|---|
| `tests/Masterly.NightShift.Core.Tests` | `Masterly.NightShift.Core` |
| `tests/Masterly.NightShift.SqlServer.Tests` | `Masterly.NightShift.SqlServer` (needs SQL Server) |
| `tests/Masterly.NightShift.AspNetCore.Tests` | `Masterly.NightShift.NetCore` and `Masterly.NightShift.AspNetCore` together: DI registration, hosted services, endpoint mapping, keep-alive |

Pick a framework with `-f`:

```
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0
dotnet test tests/Masterly.NightShift.Core.Tests -f net10.0
```

Run one class or one test with `--filter`:

```
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0 --filter "FullyQualifiedName~Masterly.NightShift.Core.Tests.Server.WorkerFacts"
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0 --filter "FullyQualifiedName~WorkerFacts.Execute_TakesConnectionAndReleasesIt"
```

Before you open a pull request, build and run the tests on `net8.0` and `net10.0`, and on Windows on `net48` too.

The tests use xUnit v3 through the classic VSTest runner (`xunit.v3.mtp-off`), so `dotnet test` and `--filter` work as usual, and Moq for mocks.

### SQL Server tests

Set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` placeholder for the database name.

LocalDB:

```
Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;
```

SQL Server Express:

```
Server=.\sqlexpress;Database={0};Integrated Security=True;TrustServerCertificate=True;
```

Without the variable, the tests use `Server=.\;Database={0};Trusted_Connection=True;TrustServerCertificate=True;`, the default instance on the local machine.

Each target framework uses its own database (`Masterly.NightShift.SqlServer.Tests.net8_0`, `Masterly.NightShift.SqlServer.Tests.net10_0`), so both can run at once. The databases are created on demand with a case-sensitive collation, which catches queries that only work on a case-insensitive server. Set `NightShift_SqlServer_DatabaseName` to use another name.

### Writing tests

- Test classes are named `*Facts` and mirror the `src/` folder layout: tests for `src/Masterly.NightShift.Core/Server/Worker.cs` are in `tests/Masterly.NightShift.Core.Tests/Server/WorkerFacts.cs`.
- Reusable mocks of the filter and state contexts are in `tests/Masterly.NightShift.Core.Tests/Mocks`.
- Every test assembly runs one test at a time (`[assembly: Parallelization(MaxThreads = 1)]`), because much of the library uses process-wide state.
- Tests that touch static or global state use `[GlobalLock]` or `[StaticLock]`. Tests that change the serializer options use `[CleanSerializerSettings]`. SQL Server tests use `[CleanDatabase]` to empty the tables between tests.
- Anything that touches serialization should run at every [compatibility level](../configuration/compatibility-levels.md). Use `[DataCompatibilityRangeFact]` or `[DataCompatibilityRangeTheory]` (in `tests/Masterly.NightShift.Core.Tests/Utils/`) instead of `[Fact]`/`[Theory]`: each test case runs once per level, with a display name suffix such as `[Version_170]`. `MinLevel` and `MaxExcludingLevel` narrow the range.
- Custom `BeforeAfterTestAttribute`s must not use `Monitor`, because xUnit v3 can call `After` on a different thread than `Before`. Use `SemaphoreSlim`.
- Dashboard pages are tested by rendering them with `tests/Masterly.NightShift.Core.Tests/Dashboard/Pages/PageRenderer.cs`, which mocks the storage with empty answers.
- `tests/Masterly.NightShift.SqlServer.Tests/TelemetryFacts.cs` checks the telemetry path end to end against a real storage.

The test projects reference Cronos under `extern alias ReferencedCronos` (the `ChangeAliasesOfStrongNameAssemblies` target), so tests can use Cronos without clashing with the copy the library uses.

## Packages and lock files

- **Central package management.** Every version is in [`Directory.Packages.props`](../../Directory.Packages.props); `PackageReference` items have no `Version`.
- **Lock files.** Each project has a `packages.lock.json`. After you add, remove or change a package, refresh them:

  ```
  dotnet restore Masterly.NightShift.slnx --force-evaluate
  ```

  CI restores in locked mode and fails if a lock file is out of date.
- **Keeping dependencies current.** Check with `dotnet list Masterly.NightShift.slnx package --outdated`. Raising a library dependency raises the minimum version every consumer gets, so do it on purpose.
- **Exceptions.** `Microsoft.CodeAnalysis.CSharp` stays at the oldest Roslyn the source generators need; compiling them against a newer one would stop them from loading in older compilers, such as the .NET 8 SDK. LibLog, StackTraceParser/StackTraceFormatter and MoreLinq are source-only packages referenced with `ExcludeAssets="contentFiles"`: the code compiled is the vendored copy in `App_Packages/`, so bumping them changes nothing. System.Text.Json is referenced on `netstandard2.0` only, because `net8.0` and later have it built in.
- **Cronos and Dapper** are ordinary package dependencies of the shipped packages.

## Rules the code follows

### Warnings are errors

`TreatWarningsAsErrors` is on for every `src/` project, and the .NET analyzers run with `AnalysisMode` `Recommended`. An analyzer warning breaks the build, so build before you push. A few analyzer rules are turned off in `src/Directory.Build.props` because they suggest APIs `netstandard2.0` doesn't have.

### Multi-targeting

Every library package targets `netstandard2.0;net8.0;net10.0`. `netstandard2.0` is what .NET Framework 4.6.2+ applications get, so:

- shared code must compile for `netstandard2.0`; check that a BCL API exists there before using it;
- code that needs a newer API goes behind `#if NET8_0_OR_GREATER`, with a `netstandard2.0` path or a feature that is simply not available there (telemetry is net8.0+ only);
- small polyfills live in `src/Masterly.NightShift.Core/Common/LanguagePolyfills.cs`;
- `LangVersion` is `Latest`, so modern syntax is fine as long as it needs no runtime support `netstandard2.0` lacks.

### Binary and API compatibility

The library is consumed by many old and new runtimes, and other repositories implement its storage interfaces (Redis, PostgreSQL, MongoDB and more). So:

- **Never add a member to `IStorageConnection`, `IWriteOnlyTransaction`, `IMonitoringApi` or the other public interfaces.** It breaks every third-party implementation. Add a virtual member to `JobStorageConnection`/`JobStorageTransaction`/`JobStorageMonitor` and advertise it through `JobStorage.HasFeature(...)` and `JobStorageFeatures`, or add a new interface. See [Architecture](architecture.md#storage-abstraction).
- **Respect the compatibility level** in any change to stored data (`InvocationData`, state data, `SerializationHelper`): older servers may read what you write.
- **Record consumer-visible changes** in [`docs/breaking-changes.md`](../breaking-changes.md) and user-facing changes in `CHANGELOG.md`.

### Nullable annotations

Nullable reference types are being turned on file by file with `#nullable enable`. Done so far: the client API (`BackgroundJob*`, `IBackgroundJobClient`, `RecurringJob*`, `IRecurringJobManager`), `BackgroundJobServer*`, the client/server/state filter interfaces and their contexts, `IState` and the built-in states, the storage contract (`JobStorage`, `IStorageConnection`, `JobStorageConnection`, `IWriteOnlyTransaction`, `JobStorageTransaction`, `IFetchedJob`, `JobData`, `StateData`), and the monitoring API with its DTOs. That is the whole public API that users, filter authors and storage authors use; internal and dashboard code is not converted yet.

When you convert a file:

- keep the JetBrains-style `[NotNull]`/`[CanBeNull]` attributes, and turn `[CanBeNull]` into `?`;
- fix what the compiler reports instead of suppressing it;
- review interfaces and values from unconverted files by hand, because the compiler can't check them;
- for storage interfaces, base each annotation on evidence: what Masterly.NightShift passes in, and what `Masterly.NightShift.SqlServer` returns.

Job IDs returned by `Create`, `Enqueue`, `Schedule` and `ContinueJobWith` are `string?` on purpose: a client filter can cancel creation.

### Code style

- Match the code around you rather than modern C# defaults: block-scoped namespaces, `var` where the existing code uses it, `[NotNull]`/`[CanBeNull]` (from `Masterly.NightShift.Annotations`) on parameters.
- Four spaces, no tabs ([`.editorconfig`](../../.editorconfig)).
- Most public types are in the `Masterly.NightShift` namespace no matter which folder they live in. Keep a moved or new type in the namespace its neighbours use. See [Architecture](architecture.md#source-tree).
- Don't hand-edit generated files: `Strings.Designer.cs` (from `Strings.resx`) and `DefaultInstall.sql` (from `Install.sql` through the `DefaultInstall.tt` T4 template).
- Don't change `App_Packages/` (LibLog, StackTraceParser, StackTraceFormatter, MoreLinq) beyond their namespaces.
- Static dashboard files in `Dashboard/Content` must be listed as `EmbeddedResource` in `Masterly.NightShift.Core.csproj`.

### License headers

Masterly.NightShift started from the source of an earlier LGPL-licensed project (see [NOTICE.md](../../NOTICE.md)). Files inherited from it start with that project's license header, and **the header must stay**, including in SQL scripts, where it is written as `--` comments. Changes to those files stay under that license. New files need no header and are MIT, like everything written for this project ([LICENSE.txt](../../LICENSE.txt)).

The rename also left a few names that must not change, because existing data depends on them: the SQL Server schema is still `HangFire`, the drain-mode storage key is still `hangfire:control`, and `TypeHelper` maps type names stored by the earlier library (`Hangfire.*, Hangfire.Core`) to the new types.

## Releasing

1. Raise `<Version>` in [`src/Directory.Build.props`](../../src/Directory.Build.props). The assembly versions are derived from it.
2. Add the release notes to [`CHANGELOG.md`](../../CHANGELOG.md).
3. Merge to `main`. The Publish workflow pushes the new packages to nuget.org after the Build workflow succeeds.

Versions already on nuget.org are skipped, so a merge that doesn't raise the version publishes nothing.

## CI

The workflows are in `.github/workflows/`:

| Workflow | Runs on | Does |
|---|---|---|
| `build.yml` (Build) | Every push and pull request to `main`, and on demand | On Windows: restores in locked mode, builds, runs all three test projects on `net48`, `net8.0` and `net10.0` against LocalDB, packs, and uploads the packages as an artifact. On Linux: builds and runs the tests on `net8.0` and `net10.0` against a SQL Server 2022 container. Then a consumer job installs the packages just built into new apps outside the repository, as a user would: ASP.NET Core apps on `net8.0` and `net10.0` with the meta-package, an ASP.NET Core 2.3 app on .NET Framework 4.8 (dashboard and health checks), a .NET Framework 4.8 console app that runs a job against LocalDB, and a Worker Service on `net8.0` with only `Masterly.NightShift.NetCore` and `Masterly.NightShift.SqlServer`, built with the .NET 8 SDK (so the source generator still loads in the oldest supported compiler) and checked to use the 8.0 `Microsoft.Extensions` packages. |
| `codeql-analysis.yml` (CodeQL Analysis) | Every push and pull request to `main`, and monthly | Builds and runs CodeQL's `security-and-quality` queries for C#. |
| `publish.yml` (Publish to NuGet) | After a successful Build of a push to `main` | Builds, packs and pushes the packages to nuget.org with `--skip-duplicate`. Needs the `NUGET_API_KEY` secret. |

## See also

- [Architecture](architecture.md)
- [Compatibility levels](../configuration/compatibility-levels.md)
- [Custom storage](../storage/custom-storage.md)
- [Breaking changes](../breaking-changes.md)
