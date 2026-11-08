# Masterly.NightShift

Reliable background jobs for .NET: fire-and-forget, delayed, recurring and continuation jobs, persisted to a storage you already run, with a built-in dashboard.

This readme ships with every Masterly.NightShift package:

| Package | What it is |
|---|---|
| `Masterly.NightShift` | Meta-package: Core + SqlServer + AspNetCore, for ASP.NET Core apps |
| `Masterly.NightShift.Core` | Jobs, the server, filters, the dashboard |
| `Masterly.NightShift.SqlServer` | SQL Server storage |
| `Masterly.NightShift.NetCore` | `Microsoft.Extensions.*` integration (DI, hosted service) |
| `Masterly.NightShift.AspNetCore` | Dashboard middleware and endpoint mapping |

An ASP.NET Core app with the `Masterly.NightShift` meta-package (with only Core and SqlServer, configure `GlobalConfiguration.Configuration` and start a `BackgroundJobServer` instead, see the documentation):

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

For more details, please take a look at the [documentation](https://github.com/a7mdfre7at/Masterly.NightShift/tree/main/docs) and the [README](https://github.com/a7mdfre7at/Masterly.NightShift#readme) on GitHub.
