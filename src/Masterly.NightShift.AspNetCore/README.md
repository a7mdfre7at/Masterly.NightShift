# Masterly.NightShift.AspNetCore

ASP.NET Core integration for Masterly.NightShift: the dashboard as middleware or a mapped endpoint, ASP.NET Core health checks, and `/health` and Prometheus `/metrics` endpoints for probes and scrapers.

This is a package of [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift), reliable background jobs for .NET. Most apps install that meta-package, which brings this one with the core and the SQL Server storage.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.AspNetCore?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.AspNetCore)

## Install

```bash
dotnet add package Masterly.NightShift
```

or, to pick the storage yourself:

```bash
dotnet add package Masterly.NightShift.AspNetCore
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** `Masterly.NightShift.NetCore`, and the ASP.NET Core shared framework

## Example

The dashboard behind an authorization policy, with health checks for the server and the cluster:

```csharp
using Masterly.NightShift;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")!));
builder.Services.AddNightShiftServer();

// Your app's authentication goes here (cookies, OpenID Connect...); the policy needs a signed-in user
builder.Services.AddAuthorization(options =>
    options.AddPolicy("NightShiftAdmins", policy => policy.RequireRole("Admin")));

builder.Services.AddHealthChecks()
    .AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process, name: "nightshift-server", tags: ["live"])
    .AddNightShift(name: "nightshift-cluster", tags: ["ready"]);

var app = builder.Build();

app.MapNightShiftDashboardWithNoAuthorizationFilters()   // served at /nightshift
    .RequireAuthorization("NightShiftAdmins");

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
```

Without an authorization setup, the dashboard only answers requests from the local machine.

## Documentation

Dashboard options, authorization, every page, customization and the health and metrics endpoints are covered in the full documentation: <https://a7mdfre7at.github.io/Masterly.NightShift/docs/dashboard>

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
