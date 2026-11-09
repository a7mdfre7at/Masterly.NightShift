# Masterly.NightShift.NetCore

`Microsoft.Extensions` integration for Masterly.NightShift: `AddNightShift` and `AddNightShiftServer` for dependency injection, job classes created from your container with a scope per job, logging through `ILogger`, and the job server as a hosted service. Use it in Worker Services and other Generic Host apps.

This is a package of [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift), reliable background jobs for .NET.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.NetCore?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.NetCore)

## Install

ASP.NET Core apps get this package through [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift). For a Worker Service, add it with a storage:

```bash
dotnet add package Masterly.NightShift.NetCore
dotnet add package Masterly.NightShift.SqlServer
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** `Masterly.NightShift.Core`, and the `Microsoft.Extensions.DependencyInjection`, `Hosting` and `Logging` abstractions: 8.0.x on `net8.0`, 10.0.x on `net10.0`, so your app keeps its runtime's versions

## Example

A Worker Service that runs jobs, with the job classes created from the container:

```csharp
using Masterly.NightShift;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));

builder.Services.AddNightShiftServer(options =>
{
    options.WorkerCount = 4;
});

builder.Services.AddTransient<ReportService>();

var host = builder.Build();

host.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6));

host.Run();

public class ReportService
{
    private readonly ILogger<ReportService> _logger;

    public ReportService(ILogger<ReportService> logger) => _logger = logger;

    public void BuildDaily() => _logger.LogInformation("Daily report built");
}
```

Each job runs inside a logging scope with its job id and name.

## Documentation

What `AddNightShift` and `AddNightShiftServer` register, job activation and scopes, and server options from configuration are covered in the full documentation: <https://a7mdfre7at.github.io/Masterly.NightShift/docs/server/dependency-injection>

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
