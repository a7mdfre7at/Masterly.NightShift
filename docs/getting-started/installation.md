# Installation

Masterly.NightShift ships as a few NuGet packages: a core library, a storage, and integration packages for `Microsoft.Extensions` hosting and ASP.NET Core. This page shows which ones to install for your kind of app, and what each one needs.

## Quick example

For an ASP.NET Core app that uses SQL Server:

```
dotnet add package Masterly.NightShift
dotnet add package Microsoft.Data.SqlClient
```

If the build warns about `System.Security.Cryptography.Pkcs` (NU1902, NU1903), see [The SQL Server driver](#the-sql-server-driver).

`Masterly.NightShift` is a meta-package: it has no code of its own and installs `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer` and `Masterly.NightShift.AspNetCore` (which brings `Masterly.NightShift.NetCore` with it). The SQL Server driver is a separate package on purpose, see [The SQL Server driver](#the-sql-server-driver).

## How it works

- **`Masterly.NightShift.Core`** has everything that doesn't depend on a host or a database: the client API (`BackgroundJob`, `IBackgroundJobClient`, `RecurringJob`, `IRecurringJobManager`), the server (`BackgroundJobServer`), states, filters, the storage abstraction (`JobStorage`) and the dashboard pages. It also ships a source generator as an analyzer, which writes direct calls for your job methods at build time (see [Source-generated invokers](../jobs/source-generated-invokers.md)).
- **A storage package** decides where jobs are kept. `Masterly.NightShift.SqlServer` is the one in this repository. Other storages implement `JobStorage` (see [Custom storage](../storage/custom-storage.md)).
- **`Masterly.NightShift.NetCore`** connects to `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Hosting`: `AddNightShift` registers the client services, `AddNightShiftServer` runs the server as an `IHostedService`, and job classes are created from the container, one scope per job.
- **`Masterly.NightShift.AspNetCore`** serves the dashboard (`MapNightShiftDashboard`, `UseNightShiftDashboard`) and adds `AddNightShiftKeepAlive` for hosts that idle, such as IIS.

The packages depend on each other in a line: AspNetCore needs NetCore, NetCore and SqlServer need Core. Installing the top one you need is enough.

## Options / API

### Packages

| Package | Contains | Depends on |
|---|---|---|
| `Masterly.NightShift` | Nothing; a meta-package | Core, SqlServer, AspNetCore |
| `Masterly.NightShift.Core` | Client API, server, states, filters, storage abstraction, dashboard, source generator | Cronos; System.Text.Json on `netstandard2.0` |
| `Masterly.NightShift.SqlServer` | `SqlServerStorage`, `SqlServerStorageOptions`, `UseSqlServerStorage(...)` | Core, Dapper |
| `Masterly.NightShift.NetCore` | `AddNightShift`, `AddNightShiftServer`, DI-backed job activator, logging through `ILoggerFactory` | Core, `Microsoft.Extensions.*` abstractions |
| `Masterly.NightShift.AspNetCore` | `MapNightShiftDashboard`, `UseNightShiftDashboard`, `AddNightShiftKeepAlive` | NetCore, ASP.NET Core |

### Which packages to install

| Your app | Install |
|---|---|
| ASP.NET Core app that creates and processes jobs and shows the dashboard | `Masterly.NightShift` + `Microsoft.Data.SqlClient` |
| Worker Service or other Generic Host app (no web server) | `Masterly.NightShift.NetCore` + `Masterly.NightShift.SqlServer` + `Microsoft.Data.SqlClient` |
| Console app without dependency injection | `Masterly.NightShift.Core` + `Masterly.NightShift.SqlServer` + `Microsoft.Data.SqlClient` |
| .NET Framework app (Windows Service, WinForms, classic ASP.NET) | `Masterly.NightShift.Core` + `Masterly.NightShift.SqlServer` (the driver is optional, see below) |
| ASP.NET Core app that only shows the dashboard for jobs processed elsewhere | `Masterly.NightShift.AspNetCore` + `Masterly.NightShift.SqlServer` + `Microsoft.Data.SqlClient` |
| App that only creates jobs, for example a web API whose jobs run in a separate worker | Same packages as above for its host type; just don't call `AddNightShiftServer` or start a `BackgroundJobServer` |

Every app that creates or processes jobs needs the assemblies that define the job classes. A job created by a web app and processed by a worker must find the same type and method in the worker.

A dashboard-only app doesn't need them for most pages and buttons, but **Trigger now** on recurring jobs and the job catalog (**Run…**, **Run again with changes**) build jobs from their types: without the job assemblies, the dashboard refuses them with `422` and names what it couldn't load. See [Setting up the dashboard](../dashboard/setup.md).

### Target frameworks

Every library package targets `netstandard2.0`, `net8.0` and `net10.0`.

| Your app runs on | It gets | Notes |
|---|---|---|
| .NET 10 | `net10.0` | Everything is available |
| .NET 8 or 9 | `net8.0` | Everything is available |
| .NET Framework 4.6.2 or later | `netstandard2.0` | No `UseTelemetry()`, no `MapNightShiftDashboard*` (use `UseNightShiftDashboard` on ASP.NET Core 2.x for .NET Framework, or host the dashboard in a separate app), `AddNightShiftKeepAlive` needs an explicit URL. Classic ASP.NET and OWIN are not supported |
| .NET Core 3.x to .NET 7 | `netstandard2.0` | These runtimes are out of support; the same limits as .NET Framework apply |

The assemblies aren't strong-named, as Hangfire's weren't. A .NET Framework app or library that is strong-named itself can't load them (`FileLoadException: A strongly-named assembly is required`). This stays so for 1.x, because signing them later would change their identity.

### Features that depend on the target framework

| Feature | Available on |
|---|---|
| `UseTelemetry()` (OpenTelemetry traces and metrics) | `net8.0`, `net10.0` |
| `MapNightShiftDashboard`, `MapNightShiftDashboardWithAuthorizationPolicy`, `MapNightShiftDashboardWithNoAuthorizationFilters` | `net8.0`, `net10.0` |
| `UseNightShiftDashboard` | All targets |
| `AddNightShiftKeepAlive()` without a URL (resolves the server's own address) | `net8.0`, `net10.0`; on `netstandard2.0`, set `KeepAliveOptions.Url` |

### The SQL Server driver

`Masterly.NightShift.SqlServer` works with either `Microsoft.Data.SqlClient` or `System.Data.SqlClient` and doesn't reference either, so your app decides. At start-up it looks for a driver in this order:

1. `Microsoft.Data.SqlClient`
2. `System.Data.SqlClient` from `System.Data` (built into .NET Framework)
3. The `System.Data.SqlClient` package

On .NET 8 and later, add `Microsoft.Data.SqlClient`. Without a driver, the storage throws this when it first opens a connection, which is at start-up while it installs the schema: `InvalidOperationException: Please add a NuGet package reference to either 'Microsoft.Data.SqlClient' or 'System.Data.SqlClient' in your application project.` On .NET Framework the built-in `System.Data` driver is found without any package. To choose a driver explicitly, set `SqlServerStorageOptions.SqlClientFactory` (see [SQL Server storage](../storage/sql-server.md)).

`Microsoft.Data.SqlClient` 7.1.1 depends on a version of `System.Security.Cryptography.Pkcs` with known vulnerabilities, so after `dotnet add package Microsoft.Data.SqlClient` the build warns with NU1902 and NU1903 (errors if warnings are treated as errors). Reference the patched version directly, as this repository's samples do:

```
dotnet add package System.Security.Cryptography.Pkcs --version 10.0.14
```

### Source generator requirements

The generator inside `Masterly.NightShift.Core` is built against Roslyn 4.8, so it loads in the C# compiler of the .NET 8 SDK (or Visual Studio 2022 17.8) and later. To turn it off for a project, set this MSBuild property:

```xml
<PropertyGroup>
  <NightShiftDisableJobInvokerGeneration>true</NightShiftDisableJobInvokerGeneration>
</PropertyGroup>
```

When a job method has no generated invoker, it is called through an invoker compiled at runtime, so jobs still run.

## More examples

### Install packages one by one

If you don't want the meta-package, for example because you don't use the dashboard in this app:

```
dotnet add package Masterly.NightShift.Core
dotnet add package Masterly.NightShift.SqlServer
dotnet add package Masterly.NightShift.NetCore
dotnet add package Microsoft.Data.SqlClient
```

## Things to know

- Keep all Masterly.NightShift packages on the same version in every app that shares a database. Mixing versions works for reading old data, but newer servers may write data that older ones don't understand (see [Compatibility levels](../configuration/compatibility-levels.md)).
- The SQL Server storage creates its tables on first start (in the `HangFire` schema by default), but not the database itself. Create an empty database first; the quick starts show how.
- JSON goes through System.Text.Json. Newtonsoft.Json is not used or referenced (see [Serialization](../configuration/serialization.md)).

## See also

- [Quick start: ASP.NET Core](quick-start-aspnetcore.md)
- [Quick start: console and Worker Service](quick-start-worker.md)
- [SQL Server storage](../storage/sql-server.md)
- [Migrating from Hangfire](../migrating-from-hangfire.md)
