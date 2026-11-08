# Getting started

This section takes you from an empty project to background jobs running against SQL Server, with the dashboard showing them. Start with [Installation](installation.md) to pick the packages, follow the quick start that matches your app, then read [Core concepts](core-concepts.md) for the mental model that the rest of the documentation builds on.

| Page | What it covers |
|---|---|
| [Installation](installation.md) | The packages, target frameworks, the SQL Server driver, and which packages to pick for your kind of app |
| [Quick start: ASP.NET Core](quick-start-aspnetcore.md) | Storage, server and dashboard in an ASP.NET Core app, then your first fire-and-forget, delayed and recurring jobs |
| [Quick start: console and Worker Service](quick-start-worker.md) | A Worker Service with dependency injection, a console or .NET Framework app without it, and a separate app for the dashboard |
| [Core concepts](core-concepts.md) | Jobs, storage, client and server, workers, states, queues, retries, at-least-once execution and the dashboard |

## The most common setup

An ASP.NET Core app that stores jobs in SQL Server, processes them in the same process and serves the dashboard at `/nightshift`:

```
dotnet add package Masterly.NightShift
dotnet add package Microsoft.Data.SqlClient
```

If the build then warns about `System.Security.Cryptography.Pkcs` (NU1902, NU1903), see [The SQL Server driver](installation.md#the-sql-server-driver).

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));

builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapNightShiftDashboard();

app.MapPost("/users/{userId:int}/welcome", (int userId, IBackgroundJobClient jobs) =>
{
    var jobId = jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));
    return Results.Accepted(value: new { jobId });
});

app.Run();
```

The full walk-through, including the connection string and the `EmailService` class, is in [Quick start: ASP.NET Core](quick-start-aspnetcore.md).
