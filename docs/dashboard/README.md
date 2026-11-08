# Dashboard

The dashboard is a web UI built into Masterly.NightShift and hosted by your ASP.NET Core app (package `Masterly.NightShift.AspNetCore`). It shows every job by state, the queues, servers, recurring jobs, batches, tags and statistics of one storage, and lets operators requeue, delete, trigger and pause work, edit recurring schedules and diagnose stuck jobs. It also exposes `/ping`, `/health` and `/metrics` for monitoring tools. You can add your own pages, menu items, metrics and buttons.

| Page | What it covers |
|---|---|
| [Setting up the dashboard](setup.md) | `MapNightShiftDashboard` and `UseNightShiftDashboard` with every overload, paths, several dashboards and storages, and every `DashboardOptions` property. |
| [Authorization](authorization.md) | The local-only default, authorization filters, ASP.NET Core policies, read-only access, antiforgery, and a production checklist. |
| [Pages](pages.md) | Every page and the job details page: what each shows and what you can do there, keyboard shortcuts, and the endpoints for tools. |
| [Customization](customization.md) | Custom pages, menu items, home page metrics, JSON and command routes, embedded CSS and JavaScript, job page sections, localization, theming and date formatting. |

## Most common setup

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

app.MapNightShiftDashboardWithAuthorizationPolicy("NightShiftAdmins");   // served at /nightshift

app.Run();
```

Plain `app.MapNightShiftDashboard()` works too, but only for requests from the local machine until you configure [authorization](authorization.md). Use whatever authentication scheme the rest of your app uses (cookies, OpenID Connect, and so on).
