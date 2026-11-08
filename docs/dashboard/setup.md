# Setting up the dashboard

The dashboard is a web UI, served by your ASP.NET Core app, that shows every job, queue, server and recurring job in a storage and lets you act on them. You add it with one line, `MapNightShiftDashboard()` or `UseNightShiftDashboard()`, and tune it with `DashboardOptions`.

## Quick example

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")));
builder.Services.AddNightShiftServer();
builder.Services.AddAntiforgery();   // tokens for the dashboard's buttons

var app = builder.Build();

app.MapNightShiftDashboard();   // https://localhost:5001/nightshift

app.Run();
```

Open `/nightshift` in a browser on the same machine. By default the dashboard only answers requests that come from the local machine, so before you deploy, read [Authorization](authorization.md).

`AddAntiforgery()` lets the dashboard protect its buttons with antiforgery tokens. MVC and Razor Pages apps already have it. Without it, the dashboard still refuses requests that don't come from its own pages, with a weaker check; see [Antiforgery](authorization.md#antiforgery).

## How it works

The dashboard lives in `Masterly.NightShift.Core` (the pages, routes and embedded CSS/JS) and is hosted by `Masterly.NightShift.AspNetCore`, which turns each HTTP request into a `DashboardContext` and hands it to the matching route.

When you call `MapNightShiftDashboard` or `UseNightShiftDashboard`:

1. It checks that `AddNightShift(...)` was called, and throws an `InvalidOperationException` that names `AddNightShift` if it wasn't.
2. It picks the storage: the `storage` argument if you pass one, otherwise the `JobStorage` registered in DI (the one you configured in `AddNightShift`).
3. It picks the options: the `options` argument, otherwise a `DashboardOptions` registered in DI, otherwise `new DashboardOptions()`. If `options.TimeZoneResolver` is null, it is set to the `ITimeZoneResolver` from DI.
4. Every request under the path is matched against `DashboardRoutes.Routes`. A request with no matching route gets a 404. A matched request goes through the authorization filters, then the antiforgery check (for `POST`), then the route's dispatcher (a page, a JSON endpoint, a command or a static file). A `GET` skips command routes when a page also matches the path, so a queue named `delete` still has its page.
5. Every response says that only pages from the dashboard's own origin may show it in a frame (`X-Frame-Options: SAMEORIGIN` and `Content-Security-Policy: frame-ancestors 'self'`), unless your app set those headers already.

The dashboard reads the storage directly through the monitoring API. It doesn't need a running server, but jobs only move when a server is running somewhere against the same storage. Buttons that change jobs (requeue, delete, trigger) use the `IBackgroundJobClient` and `IRecurringJobManager` for the dashboard's storage, resolved from DI when available.

A dashboard can run in an app that doesn't reference the job assemblies. Most pages and buttons work there, but **Trigger now** on recurring jobs and the job catalog (**Run…**, **Run again with changes**) build jobs from their types, so they need the job assemblies in the dashboard's process. Without them, the dashboard answers `422` and names the job and what it couldn't load, such as `Could not resolve assembly 'Shop.Web'`; the catalog only lists the types it has.

### Endpoint routing or middleware

| | `MapNightShiftDashboard` | `UseNightShiftDashboard` |
|---|---|---|
| Style | Endpoint routing (`app.Map...`) | Middleware branch (`app.Map(path, ...)`) |
| Target frameworks | `net8.0` and later only | Every target, including `netstandard2.0` (ASP.NET Core on .NET Framework) |
| Returns | `IEndpointConventionBuilder`, so you can chain `RequireAuthorization(...)`, `RequireHost(...)` and other endpoint conventions | `IApplicationBuilder` |
| Unknown paths under the prefix | Answer 404 | Answer 404 (the branch has nothing after the dashboard) |

Prefer `MapNightShiftDashboard` on .NET 8 and later: it works with ASP.NET Core authorization policies (see [Authorization](authorization.md#using-aspnet-core-authorization-policies)).

## Options / API

### Extension methods

All of them are in the `Masterly.NightShift` namespace. `options` and `storage` are optional everywhere; when omitted they come from DI as described above.

| Method | Parameters | What it does |
|---|---|---|
| `endpoints.MapNightShiftDashboard(options, storage)` | `DashboardOptions? options = null`, `JobStorage? storage = null` | Maps the dashboard at `/nightshift`. |
| `endpoints.MapNightShiftDashboard(pattern, options, storage)` | `string pattern`, `DashboardOptions? options = null`, `JobStorage? storage = null` | Maps the dashboard at `pattern`. The endpoint's route is `{pattern}/{**path}`. |
| `endpoints.MapNightShiftDashboardWithNoAuthorizationFilters(pattern, options, storage)` | `string pattern = "/nightshift"`, `DashboardOptions? options = null`, `JobStorage? storage = null` | Same as `MapNightShiftDashboard`, but without the dashboard's own filters: it uses a copy of the options (the argument, or the `DashboardOptions` from DI, or new ones) with `Authorization` and `AsyncAuthorization` empty. The instance you pass or registered keeps its filters. Use it when you protect the endpoint with `RequireAuthorization(...)`. |
| `endpoints.MapNightShiftDashboardWithAuthorizationPolicy(authorizationPolicyName, pattern, options, storage)` | `string authorizationPolicyName`, `string pattern = "/nightshift"`, `DashboardOptions? options = null`, `JobStorage? storage = null` | `MapNightShiftDashboardWithNoAuthorizationFilters(...)` followed by `.RequireAuthorization(authorizationPolicyName)`. |
| `app.UseNightShiftDashboard(pathMatch, options, storage)` | `string pathMatch = "/nightshift"`, `DashboardOptions? options = null`, `JobStorage? storage = null` | Adds the dashboard as a middleware branch at `pathMatch`. |

The four `Map...` methods exist only when your app targets `net8.0` or later.

### DashboardOptions

`DashboardOptions` is in the `Masterly.NightShift` namespace.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `AppPath` | `string` | `"/"` | Target of the "Back to site" link at the bottom of the sidebar. Set to `null` to hide the link (the sidebar then shows the version instead). |
| `PrefixPath` | `string` | `""` | Text put in front of every link the dashboard generates (`PrefixPath` + path base + route). Use it when a reverse proxy removes part of the public path before the request reaches your app. |
| `Authorization` | `IEnumerable<IDashboardAuthorizationFilter>` | One `LocalRequestsOnlyAuthorizationFilter` | Synchronous filters. Every filter must return `true`, or the request gets 401 (anonymous) or 403 (signed in). See [Authorization](authorization.md). |
| `AsyncAuthorization` | `IEnumerable<IDashboardAsyncAuthorizationFilter>` | Empty | Asynchronous filters, run after `Authorization`. Setting this property while `Authorization` still holds the default filter also clears `Authorization`. |
| `IsReadOnlyFunc` | `Func<DashboardContext, bool>` | Always `false` | Called once per request. When it returns `true`, buttons that change data are hidden and every command route answers 401. See [Read-only dashboards](authorization.md#read-only-dashboards). |
| `StatsPollingInterval` | `int` | `2000` | How often, in milliseconds, the browser polls `/stats` to refresh the counters in the sidebar and the realtime graph. |
| `DisplayStorageConnectionString` | `bool` | `true` | Shows the storage description in the footer. For SQL Server this is `SQL Server: {server}@{database}`, taken from the connection string, without credentials. |
| `DashboardTitle` | `string` | `"Masterly.NightShift Dashboard"` | Shown at the top of the sidebar and in the browser tab. In the sidebar it is written as raw HTML, so you can use markup; when it contains `<`, the browser tab shows the default title instead. |
| `DisplayNameFunc` | `Func<DashboardContext, Job, string>` | `null` | Gives jobs a display name in lists and on the job page. It is used only when the method has neither a `[JobDisplayName]` nor a `[System.ComponentModel.DisplayName]` attribute. Exceptions it throws are ignored and the default name is shown. |
| `IgnoreAntiforgeryToken` | `bool` | `false` | Turns off the antiforgery check for `POST` requests (and, without `IAntiforgery`, the same-origin check that replaces it) and stops the page from emitting the token. See [Antiforgery](authorization.md#antiforgery). |
| `TimeZoneResolver` | `ITimeZoneResolver` | `null` (filled from DI) | Resolves time zone ids for the recurring job schedule editor, the schedule preview and the Forecast page. When still `null`, the global resolver is used (`GlobalConfiguration.Configuration.UseTimeZoneResolver(...)`, `DefaultTimeZoneResolver` unless one was set). |
| `DefaultRecordsPerPage` | `int` | `20` | Rows per page on list pages when the URL has no `count`. Users can switch to 10, 20, 50, 100, 500 or 1000 with the per-page buttons. A page never shows more than 1000 rows, whatever this option or the URL's `count` says. |
| `DarkModeEnabled` | `bool` | `true` | Includes the dark stylesheet and the theme switcher (system, light, dark). When `false`, the dashboard is always light. See [Theming](customization.md#theming-and-dark-mode). |
| `FaviconPath` | `string` | `""` | URL of a favicon for the dashboard pages. Written as is, so give a full path such as `/favicon.ico`. Empty means no icon. |
| `ServerPossiblyAbortedThreshold` | `TimeSpan` | 1 minute | A server whose last heartbeat is older than this is shown as possibly aborted, and its processing jobs get a warning. Keep it larger than `BackgroundJobServerOptions.HeartbeatInterval` and smaller than `ServerTimeout`. |
| `DisplayAuditIpAddressesToReadOnlyUsers` | `bool` | `false` | Shows read-only users the IP address recorded with each entry of the Audit page. Users who can change things always see it. See [Read-only dashboards](authorization.md#read-only-dashboards). |
| `AllowAnyMethodInManifestImport` | `bool` | `false` | Lets **Import** on the Recurring Jobs page accept a manifest entry for any public method the application can load. When `false`, only methods the application already uses as jobs are accepted (an existing recurring job's method, a Job Catalog entry, or a method with a generated invoker), and other entries are reported as errors. See [Which methods a manifest may use](../recurring-jobs/recurring-job-manifest.md#which-methods-a-manifest-may-use). |

## More examples

### A different path and title

```csharp
app.MapNightShiftDashboard("/admin/jobs", new DashboardOptions
{
    DashboardTitle = "Orders - background jobs",
    AppPath = "/admin",
    StatsPollingInterval = 5000,
    DefaultRecordsPerPage = 50,
});
```

The pattern is a plain path: don't end it with `/` and don't put route parameters in it.

### Middleware style (any target framework)

```csharp
app.UseNightShiftDashboard("/jobs", new DashboardOptions
{
    DashboardTitle = "Background jobs",
});
```

Put `UseAuthentication()` (and anything else that sets `HttpContext.User`) before this call, so your authorization filters can see the user.

### Options from dependency injection

If you register a `DashboardOptions` instance, every method uses it when you don't pass options yourself. `MapNightShiftDashboardWithNoAuthorizationFilters` and `MapNightShiftDashboardWithAuthorizationPolicy` use a copy without the authorization filters, so other dashboards that use the registered instance keep them.

```csharp
builder.Services.AddSingleton(new DashboardOptions
{
    DashboardTitle = "Background jobs",
    DisplayStorageConnectionString = false,
});

var app = builder.Build();
app.MapNightShiftDashboard(); // picks up the DashboardOptions registered above
```

### One dashboard per storage

Pass a storage to show something other than the default one. Each dashboard needs its own path. Buttons on each dashboard act on that dashboard's storage.

```csharp
using Masterly.NightShift.SqlServer;

var euStorage = new SqlServerStorage(configuration.GetConnectionString("JobsEu"));
var usStorage = new SqlServerStorage(configuration.GetConnectionString("JobsUs"));

app.MapNightShiftDashboard("/jobs/eu", new DashboardOptions { DashboardTitle = "Jobs - EU" }, euStorage);
app.MapNightShiftDashboard("/jobs/us", new DashboardOptions { DashboardTitle = "Jobs - US" }, usStorage);
```

Use one `DashboardOptions` instance per dashboard: the methods write `TimeZoneResolver` into the instance you pass. Custom pages, menu items and metrics are global (see [Customization](customization.md)), so they appear on every dashboard.

### Behind a reverse proxy that strips a prefix

If users open `https://example.com/billing/nightshift` and the proxy forwards the request as `/nightshift`, the dashboard would generate links without `/billing`. Add the missing part with `PrefixPath`:

```csharp
// The proxy serves https://example.com/billing/nightshift and strips "/billing" before
// forwarding to this app, which only sees /nightshift.
app.MapNightShiftDashboard(new DashboardOptions
{
    PrefixPath = "/billing",
    AppPath = "/billing/",
});
```

If the proxy sends the prefix in a header instead (for example `X-Forwarded-Prefix`) and you set `Request.PathBase` from it, you don't need `PrefixPath`: the path base is already part of every link.

### Job display names

```csharp
app.MapNightShiftDashboard(new DashboardOptions
{
    DisplayNameFunc = (context, job) => $"{job.Type.Name}.{job.Method.Name}",
});
```

A `[JobDisplayName("...")]` or `[System.ComponentModel.DisplayName("...")]` attribute on the method wins over `DisplayNameFunc`. See [Behavior filters](../filters/behavior.md).

### Branding

```csharp
app.MapNightShiftDashboard(new DashboardOptions
{
    DashboardTitle = "<strong>Acme</strong> jobs",
    FaviconPath = "/favicon.ico",
    DarkModeEnabled = false,
    ServerPossiblyAbortedThreshold = TimeSpan.FromMinutes(2),
});
```

## Things to know

- **Local requests only by default.** A new `DashboardOptions` has one `LocalRequestsOnlyAuthorizationFilter`, a development setting. In a container or behind a load balancer on another machine, requests look remote and get 401. Behind a reverse proxy or a sidecar on the same machine, requests come from a loopback address: the filter turns them away when the proxy adds `X-Forwarded-For` (or `Forwarded`, `X-Forwarded-Host`, `X-Real-IP`), but lets them in when it doesn't. Configure [authorization](authorization.md) before you deploy.
- **`DashboardTitle` is raw HTML** in the sidebar. Don't build it from user input.
- **`/ping`, `/health` and `/metrics` are dashboard routes.** They live under the dashboard path (for example `/nightshift/health`) and go through the same authorization filters as the pages. To serve health and metrics outside the dashboard's filters, map them with `app.MapNightShiftHealth(...)` / `app.MapNightShiftMetrics(...)`, or use ASP.NET Core health checks with `AddNightShift()`; see [Health checks and probes](../monitoring/health-checks.md). See also [Pages](pages.md#endpoints-for-tools) and [Keep-alive](../server/keep-alive.md).
- The dashboard page polls `/stats` every `StatsPollingInterval` milliseconds for as long as it is open. Many open tabs on a busy storage add up; raise the interval if the storage is under load.

## See also

- [Authorization](authorization.md)
- [Pages](pages.md)
- [Customization](customization.md)
- [Quick start: ASP.NET Core](../getting-started/quick-start-aspnetcore.md)
- [Dependency injection](../server/dependency-injection.md)
- [SQL Server storage](../storage/sql-server.md)
