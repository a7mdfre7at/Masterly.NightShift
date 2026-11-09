# Installation

Masterly.NightShift ships as a few NuGet packages: a core library, a storage, and integration packages for `Microsoft.Extensions` hosting and ASP.NET Core. This page shows which ones to install for your kind of app, and what each one needs.

## Quick example

For an ASP.NET Core app that uses SQL Server:

```text
dotnet add package Masterly.NightShift
```

`Masterly.NightShift` is a meta-package: it has no code of its own and installs `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer` (which brings the SQL Server driver, `Microsoft.Data.SqlClient`) and `Masterly.NightShift.AspNetCore` (which brings `Masterly.NightShift.NetCore` with it).

## How it works

- **`Masterly.NightShift.Core`** has everything that doesn't depend on a host or a database: the client API (`BackgroundJob`, `IBackgroundJobClient`, `RecurringJob`, `IRecurringJobManager`), the server (`BackgroundJobServer`), states, filters, the storage abstraction (`JobStorage`) and the dashboard pages. It also ships a source generator as an analyzer, which writes direct calls for your job methods at build time (see [Source-generated invokers](../jobs/source-generated-invokers.md)).
- **A storage package** decides where jobs are kept. `Masterly.NightShift.SqlServer` is the one in this repository. Other storages implement `JobStorage` (see [Custom storage](../storage/custom-storage.md)).
- **`Masterly.NightShift.NetCore`** connects to `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting` and `Microsoft.Extensions.Logging`: `AddNightShift` registers the client services, `AddNightShiftServer` runs the server as an `IHostedService`, job classes are created from the container, one scope per job, and the library's log goes to `ILogger`.
- **`Masterly.NightShift.AspNetCore`** serves the dashboard (`MapNightShiftDashboard`, `UseNightShiftDashboard`), adds ASP.NET Core health checks (`AddHealthChecks().AddNightShift()`), and adds `AddNightShiftKeepAlive` for hosts that idle, such as IIS.

The packages depend on each other in a line: AspNetCore needs NetCore, NetCore and SqlServer need Core. Installing the top one you need is enough.

## Options / API

### Packages

| Package | Contains | Depends on |
|---|---|---|
| `Masterly.NightShift` | Nothing; a meta-package | Core, SqlServer, AspNetCore |
| `Masterly.NightShift.Core` | Client API, server, states, filters, storage abstraction, dashboard, source generator | Cronos |
| `Masterly.NightShift.SqlServer` | `SqlServerStorage`, `SqlServerStorageOptions`, `UseSqlServerStorage(...)` | Core, Dapper, Microsoft.Data.SqlClient |
| `Masterly.NightShift.NetCore` | `AddNightShift`, `AddNightShiftServer`, `ConfigureNightShiftJobLogging`, DI-backed job activator, logging through `ILoggerFactory` | Core; the `Microsoft.Extensions` DependencyInjection, Hosting and Logging abstractions (8.0.x on `net8.0`, 10.0.x on `net10.0`) |
| `Masterly.NightShift.AspNetCore` | `MapNightShiftDashboard`, `UseNightShiftDashboard`, `MapNightShiftHealth`, `MapNightShiftMetrics`, health checks (`IHealthChecksBuilder.AddNightShift`), `AddNightShiftKeepAlive` | NetCore; the ASP.NET Core shared framework |

### Which packages to install

| Your app | Install |
|---|---|
| ASP.NET Core app that creates and processes jobs and shows the dashboard | `Masterly.NightShift` |
| Worker Service or other Generic Host app (no web server) | `Masterly.NightShift.NetCore` + `Masterly.NightShift.SqlServer` |
| Console app or Windows Service without dependency injection | `Masterly.NightShift.Core` + `Masterly.NightShift.SqlServer` |
| ASP.NET Core app that only shows the dashboard for jobs processed elsewhere | `Masterly.NightShift.AspNetCore` + `Masterly.NightShift.SqlServer` |
| App that only creates jobs, for example a web API whose jobs run in a separate worker | Same packages as above for its host type, without calling `AddNightShiftServer` or starting a `BackgroundJobServer` |

Every app that creates or processes jobs needs the assemblies that define the job classes. A job created by a web app and processed by a worker must find the same type and method in the worker.

A dashboard-only app doesn't need them for most pages and buttons, but **Trigger now** on recurring jobs and the job catalog (**Run…**, **Run again with changes**) build jobs from their types: without the job assemblies, the dashboard refuses them with `422` and names what it couldn't load. See [Setting up the dashboard](../dashboard/setup.md).

### Target frameworks

Every library package targets `net8.0` and `net10.0`, so it runs on .NET 8 and later. An app on .NET 10 gets the `net10.0` build, an app on .NET 8 or 9 the `net8.0` build; both have every feature. .NET Framework and .NET 7 or earlier are not supported: stay on Hangfire 1.8 there, or move the app to .NET 8 first.

The assemblies aren't strong-named, as Hangfire's weren't. This stays so for 1.x, because signing them later would change their identity.

### The SQL Server driver

`Masterly.NightShift.SqlServer` uses `Microsoft.Data.SqlClient` and brings it with it, so there is no driver to add. It also lifts the driver's `System.Security.Cryptography.Pkcs` dependency to a patched version, so the build doesn't warn about known vulnerabilities (NU1902, NU1903).

To create the connections another way, for example to wrap them for profiling, set `SqlServerStorageOptions.SqlClientFactory` to your own `DbProviderFactory` (see [SQL Server storage](../storage/sql-server.md)). The connections it creates must be `Microsoft.Data.SqlClient` connections, or wrap them.

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

```text
dotnet add package Masterly.NightShift.Core
dotnet add package Masterly.NightShift.SqlServer
dotnet add package Masterly.NightShift.NetCore
```

## Things to know

- Keep all Masterly.NightShift packages on the same version in every app that shares a database. Mixing versions works for reading old data, but newer servers may write data that older ones don't understand (see [Compatibility levels](../configuration/compatibility-levels.md)).
- The SQL Server storage creates its tables on first start (in the `HangFire` schema by default), but not the database itself. Create an empty database first; the quick starts show how.
- `UseSqlServerStorage` takes a connection string, not a connection string name: read the value with `builder.Configuration.GetConnectionString("NightShift")` and pass it, as the quick starts do. A text without any `key=value` pair throws `ArgumentException` at start-up, because it is most likely a name passed by mistake.
- JSON goes through System.Text.Json. Newtonsoft.Json is not used or referenced (see [Serialization](../configuration/serialization.md)).

## See also

- [Quick start: ASP.NET Core](quick-start-aspnetcore.md)
- [Quick start: console and Worker Service](quick-start-worker.md)
- [SQL Server storage](../storage/sql-server.md)
- [Migrating from Hangfire](../migrating-from-hangfire.md)
