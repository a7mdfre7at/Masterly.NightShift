# Masterly.NightShift.Core

The core of Masterly.NightShift: fire-and-forget, delayed, recurring and continuation background jobs, the job server, filters (retries, concurrency limits, timeouts and more) and the dashboard. Jobs are stored in a storage package you add next to it, such as [Masterly.NightShift.SqlServer](https://www.nuget.org/packages/Masterly.NightShift.SqlServer).

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.Core?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.Core)

## Install

ASP.NET Core apps usually install the [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift) meta-package instead. Install the core directly for a console app or a Windows Service without a host:

```bash
dotnet add package Masterly.NightShift.Core
dotnet add package Masterly.NightShift.SqlServer
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** [Cronos](https://www.nuget.org/packages/Cronos)

## Example

Without a host or a DI container, configure `GlobalConfiguration` once at start-up and start a `BackgroundJobServer` yourself:

```csharp
using Masterly.NightShift;

GlobalConfiguration.Configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseColouredConsoleLogProvider()
    .UseSqlServerStorage(
        @"Server=(localdb)\MSSQLLocalDB;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;");

BackgroundJob.Enqueue<ReportService>(x => x.BuildDaily());
RecurringJob.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6));

using (var server = new BackgroundJobServer())
{
    Console.WriteLine("Server started. Press Enter to stop.");
    Console.ReadLine();
}

public class ReportService
{
    public void BuildDaily() => Console.WriteLine($"Daily report built at {DateTime.UtcNow:O}");
}
```

A job that throws is retried automatically, with growing delays.

## Documentation

Job types, filters, the server, the dashboard and monitoring are covered in the full documentation: <https://a7mdfre7at.github.io/Masterly.NightShift/>. For this setup, start with [Quick start: console and Worker Service](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-worker).

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
