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

The solution builds the libraries, both source generators, the tests and the samples in `samples/`. To try the dashboard, run `dotnet run --project samples/WebSample` (LocalDB by default) and open `http://localhost:5000/nightshift`.

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

There are three test projects, each targeting `net8.0` and `net10.0`:

| Project | Tests |
|---|---|
| `tests/Masterly.NightShift.Core.Tests` | `Masterly.NightShift.Core` |
| `tests/Masterly.NightShift.SqlServer.Tests` | `Masterly.NightShift.SqlServer` (needs SQL Server) |
| `tests/Masterly.NightShift.AspNetCore.Tests` | `Masterly.NightShift.NetCore` and `Masterly.NightShift.AspNetCore` together: DI registration, hosted services, endpoint mapping, keep-alive |

Run each project, picking a framework with `-f`:

```
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0
dotnet test tests/Masterly.NightShift.SqlServer.Tests -f net8.0
dotnet test tests/Masterly.NightShift.AspNetCore.Tests -f net8.0
```

Replace `net8.0` with `net10.0` to run the other target.

Run one class or one test with `--filter`:

```
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0 --filter "FullyQualifiedName~Masterly.NightShift.Core.Tests.Server.WorkerFacts"
dotnet test tests/Masterly.NightShift.Core.Tests -f net8.0 --filter "FullyQualifiedName~WorkerFacts.Execute_TakesConnectionAndReleasesIt"
```

Before you open a pull request, build and run the tests on `net8.0` and `net10.0`.

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

Each target framework uses its own database (`Masterly.NightShift.SqlServer.Tests.net8_0`, `Masterly.NightShift.SqlServer.Tests.net10_0`), so they can run at once. The databases are created on demand with a case-sensitive collation, which catches queries that only work on a case-insensitive server. Set `NightShift_SqlServer_DatabaseName` to use another name; every framework then uses that one database, so run them one at a time.

A SQL Server container works too, for example on Linux or macOS:

```
docker run -d -p 1433:1433 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Password12!' mcr.microsoft.com/mssql/server:2022-latest
export NightShift_SqlServer_ConnectionStringTemplate='Server=tcp:127.0.0.1,1433;Database={0};User Id=sa;Password=Password12!;TrustServerCertificate=True;'
```

This is what the Linux CI job does.

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
- **Exceptions.** `Microsoft.CodeAnalysis.CSharp` stays at the oldest Roslyn the source generators need; compiling them against a newer one would stop them from loading in older compilers, such as the .NET 8 SDK. The Microsoft.Extensions packages of `Masterly.NightShift.NetCore` stay at the oldest serviced version for each target framework, 8.0.x on `net8.0` and 10.0.x on `net10.0`, so an app keeps its own runtime's stack; the CI consumer job fails if a .NET 8 app gets a newer one. LibLog, StackTraceParser/StackTraceFormatter and MoreLinq are source-only packages referenced with `ExcludeAssets="contentFiles"`: the code compiled is the vendored copy in `App_Packages/`, so bumping them changes nothing.
- **Cronos, Dapper and Microsoft.Data.SqlClient** are ordinary package dependencies of the shipped packages. `Masterly.NightShift.SqlServer` also references a patched `System.Security.Cryptography.Pkcs`, so apps don't get the NU1902/NU1903 warnings of the version Microsoft.Data.SqlClient asks for; drop that reference once Microsoft.Data.SqlClient depends on a fixed version.

## Rules the code follows

### Warnings are errors

`TreatWarningsAsErrors` is on for every `src/` project, and the .NET analyzers run with `AnalysisMode` `Recommended`. An analyzer warning breaks the build, so build before you push. CA1863 (cache a `CompositeFormat`) is off, because the dashboard formats localized strings that change with the UI culture. The vendored code in `App_Packages/` is marked as generated in `.editorconfig`, so the analyzers skip it.

### Target frameworks

Every library package targets `net8.0;net10.0`, so any .NET 8 API can be used without conditions. Code that needs a .NET 9 or later API goes behind `#if NET9_0_OR_GREATER` with a `net8.0` path. The two source generators target `netstandard2.0`, the only target Roslyn loads analyzers from; the analyzer rules that suggest newer APIs are off for them. .NET Framework and `netstandard2.0` consumers aren't supported.

### Binary and API compatibility

The library is consumed by many old and new runtimes, and other repositories implement its storage interfaces (Redis, PostgreSQL, MongoDB and more). So:

- **Never add a member to `IStorageConnection`, `IWriteOnlyTransaction`, `IMonitoringApi` or the other public interfaces.** It breaks every third-party implementation. Add a virtual member to `JobStorageConnection`/`JobStorageTransaction`/`JobStorageMonitor` and advertise it through `JobStorage.HasFeature(...)` and `JobStorageFeatures`, or add a new interface. See [Architecture](architecture.md#storage-abstraction).
- **Respect the compatibility level** in any change to stored data (`InvocationData`, state data, `SerializationHelper`): older servers may read what you write.
- **Record consumer-visible changes** in [`docs/breaking-changes.md`](../breaking-changes.md) and user-facing changes in `CHANGELOG.md`.

### Nullable annotations

Nullable reference types are on in every project. When you add or change code:

- keep the JetBrains-style `[NotNull]`/`[CanBeNull]` attributes, and write `?` where a value can be null;
- fix what the compiler reports instead of suppressing it;
- leave a member that is always set before use non-nullable, with `= null!`;
- give every `!` a comment saying why the value can't be null;
- for storage interfaces, base each annotation on evidence: what Masterly.NightShift passes in, and what `Masterly.NightShift.SqlServer` returns.

`Common/ExpressionUtil` and `CachedExpressionCompiler` are ported from ASP.NET MVC and stay `#nullable disable`. The test projects don't report nullable warnings, because tests pass `null` on purpose.

Job IDs returned by `Create`, `Enqueue`, `Schedule` and `ContinueJobWith` are `string?` on purpose: a client filter can cancel creation.

### Code style

- The build enforces the code style in [`.editorconfig`](../../.editorconfig). Use C# 8 to 14 features: file-scoped namespaces, primary constructors, records for read-only data, collection expressions, pattern matching, `extension` blocks and raw string literals. Use explicit types with target-typed `new()` (`Foo foo = new();`) instead of `var`, which stays only for anonymous types. Parameters carry `[NotNull]`/`[CanBeNull]` (from `Masterly.NightShift.Annotations`).
- In library code, read the time from `JobClock.UtcNow`, not `DateTime.UtcNow`.
- Four spaces, no tabs ([`.editorconfig`](../../.editorconfig)).
- Most public types are in the `Masterly.NightShift` namespace no matter which folder they live in. Keep a moved or new type in the namespace its neighbours use. See [Architecture](architecture.md#source-tree).
- Don't hand-edit generated files: `Strings.Designer.cs` (from `Strings.resx`) and `DefaultInstall.sql` (from `Install.sql` through the `DefaultInstall.tt` T4 template).
- Don't change `App_Packages/` (LibLog, StackTraceParser, StackTraceFormatter, MoreLinq) beyond their namespaces.
- Static dashboard files in `Dashboard/Content` must be listed as `EmbeddedResource` in `Masterly.NightShift.Core.csproj`.

### License headers

Masterly.NightShift started from the source of an earlier LGPL-licensed project (see [NOTICE.md](../../NOTICE.md)). Files inherited from it start with that project's license header, and **the header must stay**, including in SQL scripts, where it is written as `--` comments. Changes to those files stay under that license. New files need no header and are MIT, like everything written for this project ([LICENSE.txt](../../LICENSE.txt)).

The rename also left a few names that must not change, because existing data depends on them: the SQL Server schema is still `HangFire`, the drain-mode storage key is still `hangfire:control`, and `TypeHelper` maps type names stored by the earlier library (`Hangfire.*, Hangfire.Core`) to the new types.

## Documentation site

The pages in `docs/` are published as the site at <https://a7mdfre7at.github.io/Masterly.NightShift/>, built with [Docusaurus](https://docusaurus.io/) from the [`website/`](../../website/) folder. The same files read well on GitHub, so write them as plain Markdown with relative links between pages. Links to files outside the published pages (source files, `CONTRIBUTING.md`, the design notes in `docs/architecture/`) are rewritten to GitHub links when the site is built.

- A new page goes in the folder of its section and in `website/sidebars.js`, and in the table of its section's `README.md`.
- The landing page (`website/src/pages/index.js`) copies its code samples from the pages it links to; keep them in sync.
- The site reads the package version from `src/Directory.Build.props`.

To preview and check the site, you need Node.js 20 or later (CI uses 24):

```
cd website
npm ci
npm start        # live preview; search only works in a build
npm run build    # fails on any broken link or anchor
```

## Releasing

Pushing a raised version to `master` is the release. Every package ships with the same version.

1. Raise `<Version>` in [`src/Directory.Build.props`](../../src/Directory.Build.props). The assembly versions and the version the documentation site shows are derived from it.
2. Add a `## [x.y.z] - yyyy-mm-dd` section to [`CHANGELOG.md`](../../CHANGELOG.md) that opens with a one-line summary. If the section was kept as `## [x.y.z] - Unreleased` while work went on, replace `Unreleased` with the date. The section, without its heading, becomes the GitHub Release's description, and the Publish workflow stops before pushing anything when no `## [x.y.z]` section exists for the version.
3. Record consumer-visible changes in [Breaking changes](../breaking-changes.md) if there are any.
4. Merge to `master`. After the Build workflow succeeds for that push, the Publish workflow builds and packs the tested commit, pushes the packages to nuget.org (Core, SqlServer, NetCore, AspNetCore, then the meta-package, so no package arrives before one it depends on) and creates the GitHub Release `vx.y.z` from that commit, with the packages attached.
5. After the first release, set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to it (see [Build and pack](#build-and-pack)).

Versions already on nuget.org are skipped and an existing release only gets the assets it is missing, so a merge that doesn't raise the version publishes nothing, and re-running a failed Publish run finishes the job.

The Publish workflow signs in to nuget.org with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): no API key is stored in the repository. It needs a trusted publishing policy on nuget.org for the owner `a7mdfre7at`, the repository `Masterly.NightShift` and the workflow file `publish.yml`. The job runs in the `nuget` GitHub environment, where required reviewers or other protection rules can be set in the repository's **Settings > Environments**.

## CI

The workflows are in `.github/workflows/`:

| Workflow | Runs on | Does |
|---|---|---|
| `build.yml` (Build) | Every push and pull request to `master` that changes anything outside `docs/`, `website/` and `docs.yml`, and on demand | On Windows: restores (in locked mode, as every CI job does), builds, runs all three test projects on `net8.0` and `net10.0` against LocalDB, packs, and uploads the packages as an artifact. On Linux: builds and runs the tests on `net8.0` and `net10.0` against a SQL Server 2022 container. Then a consumer job installs the packages just built into new apps outside the repository, as a user would: ASP.NET Core apps on `net8.0` and `net10.0` with the meta-package, a `net8.0` console app with only `Masterly.NightShift.Core` and `Masterly.NightShift.SqlServer` that runs a job against LocalDB, and a Worker Service on `net8.0` with only `Masterly.NightShift.NetCore` and `Masterly.NightShift.SqlServer`, built with the .NET 8 SDK (so the source generator still loads in the oldest supported compiler) and checked to use the 8.0 `Microsoft.Extensions` packages. |
| `codeql-analysis.yml` (CodeQL Analysis) | Every push and pull request to `master`, and monthly | Builds and runs CodeQL's `security-and-quality` queries for C#. |
| `publish.yml` (Publish to NuGet) | After a Build of a push to `master` completes | When the Build succeeded: builds and packs the tested commit, pushes the packages to nuget.org in dependency order with `--skip-duplicate` (Trusted Publishing, see [Releasing](#releasing)), and creates or completes the GitHub Release from the version's `CHANGELOG.md` section. When it failed: reports that nothing was published. |
| `docs.yml` (Docs) | Every push and pull request to `master` that changes `docs/`, `website/` or `src/Directory.Build.props`, and on demand | Builds the documentation site with Docusaurus, which fails on any broken link or anchor, and on `master` deploys it to GitHub Pages. |

## See also

- [Architecture](architecture.md)
- [Compatibility levels](../configuration/compatibility-levels.md)
- [Custom storage](../storage/custom-storage.md)
- [Breaking changes](../breaking-changes.md)
