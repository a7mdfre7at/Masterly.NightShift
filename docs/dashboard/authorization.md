# Dashboard authorization

The dashboard can delete jobs, trigger recurring jobs, pause processing and wipe the storage, so it must only be reachable by the right people. This page covers who can open it, who can change things, and the antiforgery protection behind every button.

## Quick example

Protect the dashboard with an ASP.NET Core authorization policy (.NET 8 and later):

```csharp
using Masterly.NightShift;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddAuthorization(options =>
    options.AddPolicy("NightShiftAdmins", policy => policy.RequireRole("Admin")));
builder.Services.AddAntiforgery();

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")));
builder.Services.AddNightShiftServer();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapNightShiftDashboardWithAuthorizationPolicy("NightShiftAdmins");

app.Run();
```

Signed-out users go through your authentication scheme's challenge (for cookies, a redirect to the login page); signed-in users without the `Admin` role are forbidden (for cookies, a redirect to the access-denied page).

## How it works

Each request to the dashboard goes through these steps, in this order:

1. **Route match.** A path with no dashboard route answers 404.
2. **Endpoint authorization** (only with `MapNightShiftDashboard...` and `RequireAuthorization`). ASP.NET Core's authorization middleware runs your policy before the dashboard sees the request.
3. **`DashboardOptions.Authorization`.** Every `IDashboardAuthorizationFilter` must return `true`.
4. **`DashboardOptions.AsyncAuthorization`.** Every `IDashboardAsyncAuthorizationFilter` must return `true`.
5. **Antiforgery.** Unless `IgnoreAntiforgeryToken` is `true`, a `POST` must come from the dashboard's own pages. With `IAntiforgery` registered, it must carry a valid token. Without it, it must carry the `X-Requested-With` header, and the browser's `Sec-Fetch-Site` or `Origin` must not name another site. Either way a failing request answers **403**. `GET` always passes. See [Antiforgery](#antiforgery).
6. **The route.** Pages and JSON endpoints render. Commands first check `IsReadOnlyFunc`, and answer 401 for read-only users, then answer 405 to anything but `POST`.

When a dashboard filter (steps 3 and 4) says no, the response is **401** if the user isn't signed in and **403** if they are. The dashboard doesn't redirect to a login page; with an endpoint policy (step 2), your authentication scheme does.

### The default: local requests only

A new `DashboardOptions` has one filter, `LocalRequestsOnlyAuthorizationFilter`. It allows a request when:

- the remote IP is `127.0.0.1` or `::1`, or
- the remote IP equals the local IP of the connection, or
- the remote IP is any loopback address, including IPv4-mapped ones such as `::ffff:127.0.0.1`,

and the request has none of the headers a proxy adds when it forwards a request: `Forwarded`, `X-Forwarded-For`, `X-Forwarded-Host` and `X-Real-IP`. It denies the request when the remote IP is unknown.

In development on your machine this lets you in. In a container, or behind a load balancer on another machine, the remote IP is not loopback, so everyone gets 401 until you configure authorization.

**Behind a proxy on the same machine the filter is not a safe gate.** A reverse proxy or a sidecar on the same host (nginx, IIS, Envoy, an Istio sidecar connecting from `127.0.0.6`) connects from a loopback address, so every request it forwards comes from "the local machine". The filter turns these requests away when the proxy adds one of the headers above, which most do. A proxy that adds none of them (nginx's default `proxy_pass` configuration, for example, or a plain TCP proxy) makes every request look local. If `ForwardedHeadersMiddleware` runs first, it replaces the remote IP with the client's and removes `X-Forwarded-For`, and the filter then checks the client's IP. Don't rely on this filter outside development.

## Options / API

| Member | Type | Default | Meaning |
|---|---|---|---|
| `DashboardOptions.Authorization` | `IEnumerable<IDashboardAuthorizationFilter>` | `[ new LocalRequestsOnlyAuthorizationFilter() ]` | Synchronous filters; all must allow. Assign a new collection to replace the default; assign `[]` to remove it. |
| `DashboardOptions.AsyncAuthorization` | `IEnumerable<IDashboardAsyncAuthorizationFilter>` | `[]` | Asynchronous filters, run after the synchronous ones; all must allow. Setting it while `Authorization` still holds the default also sets `Authorization` to `[]`. |
| `DashboardOptions.IsReadOnlyFunc` | `Func<DashboardContext, bool>` | `_ => false` | `true` makes the request read-only: no buttons that change data, and command routes answer 401. Called at most once per request. |
| `DashboardOptions.IgnoreAntiforgeryToken` | `bool` | `false` | `true` skips antiforgery validation (and the same-origin check used without `IAntiforgery`) and doesn't emit the token. |
| `IDashboardAuthorizationFilter.Authorize(DashboardContext context)` | returns `bool` | | Your synchronous check. |
| `IDashboardAsyncAuthorizationFilter.AuthorizeAsync(DashboardContext context)` | returns `Task<bool>` | | Your asynchronous check. |
| `LocalRequestsOnlyAuthorizationFilter` | class | | The default filter described above. |
| `context.GetHttpContext()` | extension on `DashboardContext`, returns `HttpContext` | | In `Masterly.NightShift.Dashboard` (package `Masterly.NightShift.AspNetCore`). Gives your filter the user, headers and services of the request. |
| `MapNightShiftDashboardWithNoAuthorizationFilters(...)` | `IEndpointConventionBuilder` | | Maps the dashboard without the dashboard's own filters, ready for `.RequireAuthorization(...)`. It uses a copy of the options you pass (or the ones registered in DI) with both filter lists empty; the original keeps its filters, and every other option, such as `IsReadOnlyFunc`, applies. |
| `MapNightShiftDashboardWithAuthorizationPolicy(policyName, ...)` | `IEndpointConventionBuilder` | | The same, plus `.RequireAuthorization(policyName)`. |

Useful members of `DashboardContext` in a filter: `Request.Path` (relative to the dashboard, for example `/jobs/failed`), `Request.Method`, `Request.RemoteIpAddress`, `Request.LocalIpAddress`, `Request.GetQuery(key)`, `Request.GetHeader(name)` (`null` when the header is missing), `Storage`, `Options`, and `GetUserName()` (the signed-in user's name, or `null`).

## More examples

### Using ASP.NET Core authorization policies

`MapNightShiftDashboard` returns an endpoint builder, so you can call `RequireAuthorization` on it. **Remove the default local-only filter when you do**, or remote users are still turned away after the policy lets them in:

```csharp
app.MapNightShiftDashboard("/nightshift", new DashboardOptions
    {
        Authorization = [],   // drop the default local-requests-only filter
    })
    .RequireAuthorization("NightShiftAdmins");
```

`MapNightShiftDashboardWithNoAuthorizationFilters` does the clearing for you, which is handy with an inline policy:

```csharp
app.MapNightShiftDashboardWithNoAuthorizationFilters()
    .RequireAuthorization(policy => policy.RequireRole("Admin", "Support"));
```

These methods exist on .NET 8 and later. With `UseNightShiftDashboard`, use a dashboard filter instead.

### Writing an authorization filter

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Dashboard;

public sealed class AdminOnlyFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true && user.IsInRole("Admin");
    }
}
```

```csharp
app.MapNightShiftDashboard(new DashboardOptions
{
    Authorization = [new AdminOnlyFilter()],
});
```

The filter runs for every request, including the browser's polling of `/stats` every couple of seconds and every CSS and JavaScript file. Keep it cheap.

### Writing an async filter

Use `IDashboardAsyncAuthorizationFilter` when the check has to await something, such as `IAuthorizationService`:

```csharp
using Masterly.NightShift.Dashboard;
using Microsoft.AspNetCore.Authorization;

public sealed class PolicyFilter : IDashboardAsyncAuthorizationFilter
{
    private readonly string _policyName;

    public PolicyFilter(string policyName)
    {
        _policyName = policyName;
    }

    public async Task<bool> AuthorizeAsync(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var authorization = httpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(httpContext.User, _policyName);
        return result.Succeeded;
    }
}
```

```csharp
app.MapNightShiftDashboard(new DashboardOptions
{
    // Setting AsyncAuthorization also removes the default local-requests-only filter
    AsyncAuthorization = [new PolicyFilter("NightShiftAdmins")],
});
```

### Read-only dashboards

`IsReadOnlyFunc` decides, per request, whether the user may change anything. Here everyone in the `Admin` or `Support` role can open the dashboard, but only admins see the buttons:

```csharp
app.MapNightShiftDashboard(new DashboardOptions
    {
        Authorization = [],
        IsReadOnlyFunc = context => !context.GetHttpContext().User.IsInRole("Admin"),
    })
    .RequireAuthorization(policy => policy.RequireRole("Admin", "Support"));
```

In a read-only request:

- Buttons and forms that change data are not rendered: requeue, delete, delete all, enqueue now, trigger, pause and resume (recurring jobs, queues, servers, processing), clearing pauses left by stopped servers, edit schedule, import, run from the catalog, run again, notes, circuit reset, tag forget, removing expired jobs from a tag, batch actions and the maintenance section.
- Every command route answers **401**, even if someone posts to it by hand.
- Reading still works: every page, job details, CSV and JSON exports, the recurring job manifest export, search, live job updates and the activity feed.
- The Audit page shows who did what, but not the IP addresses it recorded, which are personal data. Set `DisplayAuditIpAddressesToReadOnlyUsers = true` to show them to read-only users too.

The read-only check protects data, not secrets. Read-only users still see job arguments, exception messages and stack traces. Mark sensitive parameters with `[Redacted]` (see [Behavior filters](../filters/behavior.md)).

Masking needs the job's method: the dashboard reads `[Redacted]` from the method's parameters. When the dashboard's app can't load the method (the job's assembly isn't deployed there, or the class was renamed), it can't tell which arguments are secret. The job page then shows the raw arguments and payload (`Args` and `Payload` under "Can not find the target method") only to users who aren't read-only, and the JSON export leaves them out for everyone. Deploy the jobs' assemblies with the dashboard if read-only users need to see their arguments.

### A full dashboard for operators and a read-only one for everyone else

```csharp
// Operators: full control
app.MapNightShiftDashboardWithAuthorizationPolicy("NightShiftAdmins", "/nightshift");

// Everyone in the company: look, don't touch
app.MapNightShiftDashboardWithNoAuthorizationFilters("/nightshift-view", new DashboardOptions
    {
        IsReadOnlyFunc = static _ => true,
        DashboardTitle = "Background jobs (read-only)",
    })
    .RequireAuthorization();
```

### Letting health probes in

`/ping`, `/health` and `/metrics` are dashboard routes, so they go through the same filters. A load balancer or Kubernetes probe usually can't sign in. Let those paths through in your filter and keep everything else protected:

```csharp
using Masterly.NightShift.Dashboard;

public sealed class ProbesOrAdminFilter : IDashboardAuthorizationFilter
{
    private static readonly string[] AnonymousPaths = ["/ping", "/health"];

    public bool Authorize(DashboardContext context)
    {
        // Request.Path is relative to the dashboard: "/health", not "/nightshift/health"
        if (AnonymousPaths.Contains(context.Request.Path, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return context.GetHttpContext().User.IsInRole("Admin");
    }
}
```

```csharp
app.MapNightShiftDashboard(new DashboardOptions
{
    Authorization = [new ProbesOrAdminFilter()],
});
```

An endpoint policy added with `RequireAuthorization` applies to the whole dashboard path and can't make exceptions for single routes, so use a filter like this one when probes need anonymous access. `/health` reveals queue names and recurring job ids in its details; leave it out of the list if that matters to you. Alternatively, map health and metrics outside the dashboard's filters with `app.MapNightShiftHealth(...)` / `app.MapNightShiftMetrics(...)`, or use ASP.NET Core health checks with `AddNightShift()`; see [Health checks and probes](../monitoring/health-checks.md).

### Antiforgery

Every dashboard button that changes data sends a `POST` from the browser. If the dashboard trusts a cookie (your login cookie, or simply "the browser is on localhost"), another website the same user visits could make the browser send that `POST`, for example to `/nightshift/maintenance/reset`. This is cross-site request forgery. The antiforgery token stops it: the forged request can't know the token.

How the dashboard uses it when `IAntiforgery` is registered in DI:

- When it renders a page, it calls `IAntiforgery.GetAndStoreTokens`, which sets the antiforgery cookie, and writes the header name and token into `<meta name="csrf-header">` and `<meta name="csrf-token">` on the page. Only pages do this: generating tokens marks the response as not cacheable, so the CSS and JavaScript bundles and the JSON answers stay cacheable.
- The dashboard's scripts send the token in that header with every AJAX request.
- Before running a route, it calls `IAntiforgery.IsRequestValidAsync`. A `POST` without a valid token answers **403**. When the token isn't in the header, antiforgery looks for it in the form; a form over the server's limits (`FormOptions`) answers **413**.

MVC with views and Razor Pages register `IAntiforgery`; if your app doesn't use them, add it yourself:

```csharp
builder.Services.AddAntiforgery();
```

Without `IAntiforgery` there is no token, so the dashboard falls back to a weaker check and refuses the request (**403**, with a short explanation in the body) unless:

- it is a `GET`, `HEAD`, `OPTIONS` or `TRACE`, or
- it carries the `X-Requested-With` header, which the dashboard's scripts send with every request, and nothing says it comes from another site: `Sec-Fetch-Site` is `same-origin` (or `none`); or, when the request has no `Sec-Fetch-Site`, `Origin` is the request's own origin (the same scheme, host and port as the request; `X-Forwarded-Host` is not used), or there is no `Origin` at all.

This check only stops browsers. A form on another site can't add a custom header, a script on another site can only add one after a CORS preflight that your app would have to allow, and browsers send `Sec-Fetch-Site` or `Origin` with such requests. A program that isn't a browser can send `X-Requested-With` without `Origin` or `Sec-Fetch-Site` and is accepted; keeping such callers out is the job of the authorization filters. Register antiforgery anyway: the token doesn't depend on your CORS policy or on what browsers send. It is also the setup to use behind a proxy that rewrites the `Host` header, where the request's own origin no longer matches the address in the browser, so older browsers that don't send `Sec-Fetch-Site` are refused.

`IgnoreAntiforgeryToken = true` turns both checks off. Do this only for a dashboard that never trusts cookies, for example one protected by a bearer token that browsers don't send on their own:

```csharp
app.MapNightShiftDashboard(new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});
```

If you want to automate jobs, use `IBackgroundJobClient` and `IRecurringJobManager` from your code rather than posting to dashboard URLs: with antiforgery registered a script's `POST` gets 403 because it has no token, and without it the fallback check above is not a promise that scripts keep working.

### Framing

Every dashboard response carries `X-Frame-Options: SAMEORIGIN` and `Content-Security-Policy: frame-ancestors 'self'`, so a page on another site can't show the dashboard in a frame and trick a signed-in user into pressing its buttons. Each header is only added when the response doesn't have it yet, so to allow a portal on another origin to embed the dashboard, set `Content-Security-Policy: frame-ancestors 'self' https://portal.example.com` in a middleware that runs before the dashboard; browsers that support `frame-ancestors` then ignore `X-Frame-Options`.

## Production checklist

1. **Replace the default.** Local-only access is a development setting. Use `MapNightShiftDashboardWithAuthorizationPolicy` or your own filters, and make sure the default `LocalRequestsOnlyAuthorizationFilter` is gone when you use `RequireAuthorization`.
2. **Use HTTPS** for the dashboard, like the rest of your app.
3. **Register antiforgery** (`AddAntiforgery()`) and leave `IgnoreAntiforgeryToken` at `false` whenever users sign in with cookies. Behind several instances, share the Data Protection keys so a token issued by one instance is valid on the others.
4. **Give most people read-only access** with `IsReadOnlyFunc`. Remember who can press "Clear all jobs" and "Reset storage" on the Servers page: anyone who isn't read-only.
5. **Hide sensitive data.** Mark secret job parameters with `[Redacted]`. Set `DisplayStorageConnectionString = false` if the database server name shouldn't be shown.
6. **Sign users in** so the [audit log](../monitoring/audit-log-and-activity.md) records names: it uses `HttpContext.User.Identity.Name`, and shows "anonymous" otherwise.
7. **Decide about probes and keep-alive.** Allow only `/ping` (and `/health` if needed) anonymously. The [keep-alive](../server/keep-alive.md) process requests `/nightshift/ping`; any answer keeps the host awake, but a 401 or 403 is logged as a warning.
8. **Limit where it is reachable** if you can, for example with `RequireHost("jobs.internal.example.com")` on the endpoint, or by not exposing the path on your public proxy.

## Things to know

- All filters in both lists must pass; there is no "any of". Put "or" logic inside one filter.
- `IsReadOnlyFunc` is called only after the filters pass, so it can assume the user is allowed in.
- Filters don't run for paths that aren't dashboard routes; those get 404 first.
- A filter that throws fails the request with an error. Catch what you expect inside the filter.

## See also

- [Setting up the dashboard](setup.md)
- [Pages](pages.md)
- [Audit log and activity](../monitoring/audit-log-and-activity.md)
- [Keep-alive](../server/keep-alive.md)
- [Behavior filters](../filters/behavior.md) (`Redacted`, `JobDisplayName`)
