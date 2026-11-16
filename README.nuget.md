# Masterly.NightShift

Reliable background jobs for .NET: fire-and-forget, delayed, recurring and continuation jobs, persisted to a storage you already run, with a built-in dashboard.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift)

This meta-package installs the core library, SQL Server storage and the ASP.NET Core integration, for ASP.NET Core apps. Worker Services and console apps take only the packages they use:

| Package | What it is |
|---|---|
| `Masterly.NightShift` | This meta-package: Core + SqlServer + AspNetCore |
| [`Masterly.NightShift.Core`](https://www.nuget.org/packages/Masterly.NightShift.Core) | Jobs, the server, filters, the dashboard |
| [`Masterly.NightShift.SqlServer`](https://www.nuget.org/packages/Masterly.NightShift.SqlServer) | SQL Server storage |
| [`Masterly.NightShift.NetCore`](https://www.nuget.org/packages/Masterly.NightShift.NetCore) | `Microsoft.Extensions.*` integration (DI, hosted service) |
| [`Masterly.NightShift.AspNetCore`](https://www.nuget.org/packages/Masterly.NightShift.AspNetCore) | Dashboard middleware, endpoint mapping, health checks |

Every package targets `net8.0` and `net10.0`, so it runs on .NET 8 and later. The SQL Server storage uses `Microsoft.Data.SqlClient`.

```bash
dotnet add package Masterly.NightShift
```

## Example

An ASP.NET Core app:

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config.UseSqlServerStorage(connectionString));
builder.Services.AddNightShiftServer();

var app = builder.Build();

// The storage is set up once the services are built, so create jobs after Build()
app.Services.GetRequiredService<IBackgroundJobClient>()
    .Enqueue(() => Console.WriteLine("Hello from a background job"));
app.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate("nightly-cleanup", () => Maintenance.Cleanup(), Cron.Daily());

app.MapNightShiftDashboard();   // served at /nightshift
app.Run();

public static class Maintenance
{
    public static void Cleanup() => Console.WriteLine("Cleaning up");
}
```

## Documentation

Job types, retries and other filters, the server, the dashboard and monitoring are covered in the full documentation: <https://a7mdfre7at.github.io/Masterly.NightShift/>. Start with [Quick start: ASP.NET Core](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-aspnetcore). Coming from Hangfire? See [Migrating from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire).

## License

This package is MIT and contains no code. The packages it installs (Core, SqlServer and AspNetCore) are `MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL, and each of them contains both license texts.
