# Breaking changes in Hangfire 2.0

This list compares 2.0 with the last 1.8 release (1.8.25). It was produced by two tools, so it is complete for public APIs:

- **Package validation**, which now runs on every `build` (the `Validate` step) and every `dotnet pack`. It compares each package with 1.8.25. Every intentional break is recorded in the project's `CompatibilitySuppressions.xml`. Any other break fails the build.
- **A one-time ApiCompat comparison** of the 1.8.25 .NET Framework builds against the new `netstandard2.0` builds. Package validation can't show these differences, because the .NET Framework builds no longer exist.

## Supported platforms

| | 1.8.25 | 2.0 |
|---|---|---|
| Targets | `net451`, `net46`/`net461`, `netstandard1.3`, `netstandard2.0`, `netstandard2.1`, `netcoreapp3.0` | `netstandard2.0`, `net8.0`, `net10.0` |
| .NET Framework | 4.5.1+ | **4.6.2+** (through `netstandard2.0`) |
| Classic ASP.NET / OWIN | Supported | **Removed** |

## Removed packages

- **Hangfire.SqlServer.MSMQ** is no longer published. Use the default SQL Server queue.
- The **Hangfire** meta-package no longer pulls in `Microsoft.Owin.Host.SystemWeb`.

## Who is affected and what to use instead

### .NET Framework apps (Core)

All of these were OWIN-only or marked obsolete in 1.8.

| Removed | Use instead |
|---|---|
| `AppBuilderExtensions` (`app.UseHangfireServer()`, `app.UseHangfireDashboard()`), `ServerOwinExtensions`, `DashboardOwinExtensions`, `Dashboard.MiddlewareExtensions` | ASP.NET Core: `services.AddHangfireServer()` and `app.MapHangfireDashboard()`. Other apps: `new BackgroundJobServer()` |
| `OwinBootstrapper`, `IBootstrapperConfiguration`, `BootstrapperConfigurationExtensions` | `GlobalConfiguration.Configuration` |
| `Dashboard.OwinDashboardContext`, `OwinDashboardContextExtensions`, `Dashboard.Owin.IOwinDashboardAntiforgery` | `Hangfire.AspNetCore` |
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
| Hangfire.NetCore | `BackgroundJobServerHostedService` and `BackgroundProcessingServerHostedService` constructors that take `IHostApplicationLifetime` |
| Hangfire.AspNetCore | `HangfireEndpointRouteBuilderExtensions` (`endpoints.MapHangfireDashboard(...)`). Use `app.UseHangfireDashboard()` instead, or move to .NET 8+ |

## Nullable reference type annotations (source-level only)

The client API now carries C# nullable annotations: `BackgroundJob`, `IBackgroundJobClient`, `BackgroundJobClient`, `BackgroundJobClientExtensions`, `RecurringJob`, `IRecurringJobManager`, `RecurringJobManager`, `RecurringJobManagerExtensions` and `RecurringJobOptions`. Compiled code is unaffected. Projects with `<Nullable>enable</Nullable>` may see new warnings:

- **Job IDs are `string?`.** All `Enqueue`, `Schedule`, `ContinueJobWith`, `ContinueWith` and `Create` methods return `null` when creation doesn't happen: a client filter cancels it, an exception filter handles a failure, or the storage returns no ID. Check the result before passing it on, for example to `ContinueJobWith`.
- **`RecurringJob.TriggerJob` and `RecurringJobManager.TriggerJob` return `string?`.** They return `null` when the recurring job doesn't exist or wasn't triggered.
- **Optional parameters that accept `null` are marked as nullable**, for example `fromState` and `timeZone`.

## Dependency changes

- `Hangfire.AspNetCore` on `netstandard2.0` now requires `System.Security.Cryptography.Xml` 4.7.1+ and `System.Text.Encodings.Web` 4.5.1+. These are the first versions that fix known vulnerabilities in the packages that `Microsoft.AspNetCore.Antiforgery` 2.0.0 depends on.
- `Hangfire.NetCore` on `net10.0` depends on the 10.0.0 `Microsoft.Extensions.*.Abstractions` packages.

## After 2.0.0 ships

Set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to `2.0.0`, then delete the `CompatibilitySuppressions.xml` files. From then on, any break against 2.0 fails the build.
