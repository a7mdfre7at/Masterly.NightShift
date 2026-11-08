# Quick start: console and Worker Service

This page sets up a process whose only job is to run background jobs: a Worker Service (Generic Host with dependency injection), or a plain console app or .NET Framework app without it. Because these apps have no web server, the last step adds a small ASP.NET Core app that shows the dashboard for the same database.

## Quick example

A Worker Service `Program.cs` that processes jobs from SQL Server and registers a recurring job:

```csharp
using Masterly.NightShift;
using Shop.Jobs;
using Shop.Worker;

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
builder.Services.AddHostedService<JobRegistration>();

var host = builder.Build();
host.Run();
```

## How it works

The server is the part that processes jobs. It can run in any long-lived .NET process: `AddNightShiftServer()` runs it as a hosted service of the Generic Host, and `new BackgroundJobServer()` runs it anywhere else. The server polls the storage, so it doesn't matter which process created a job: a web app, another worker, or this one. Any number of processes can create and process jobs against the same database.

The dashboard reads the same database. It can be served by any ASP.NET Core app, including one that doesn't process jobs at all.

## Worker Service

### Step 1: create the projects and add the packages

The job classes go in a class library, `Shop.Jobs`, so that every app that creates, runs or shows jobs can reference them: this worker, and later the dashboard app.

```
dotnet new classlib -n Shop.Jobs
dotnet add Shop.Jobs package Microsoft.Extensions.Logging.Abstractions
dotnet new worker -n Shop.Worker
cd Shop.Worker
dotnet add package Masterly.NightShift.NetCore
dotnet add package Masterly.NightShift.SqlServer
dotnet add package Microsoft.Data.SqlClient
dotnet add reference ../Shop.Jobs/Shop.Jobs.csproj
```

If the build then warns about `System.Security.Cryptography.Pkcs` (NU1902, NU1903), see [The SQL Server driver](installation.md#the-sql-server-driver).

The templates create a sample `Class1` and a sample `Worker` class; delete `Class1.cs` in `Shop.Jobs`, and `Worker.cs` and its `AddHostedService<Worker>()` line in `Shop.Worker`, you won't need them.

### Step 2: create a database and add the connection string

Create an empty database (the storage creates the tables, not the database). With LocalDB:

```
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "CREATE DATABASE NightShift"
```

Add the connection string to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "NightShift": "Server=(localdb)\\MSSQLLocalDB;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

### Step 3: write the job class

Add `ReportService.cs` to the `Shop.Jobs` project:

```csharp
using Microsoft.Extensions.Logging;

namespace Shop.Jobs;

public class ReportService
{
    private readonly ILogger<ReportService> _logger;

    public ReportService(ILogger<ReportService> logger)
    {
        _logger = logger;
    }

    public void BuildDaily()
    {
        _logger.LogInformation("Daily report built");
    }

    public async Task Rebuild(DateTime day, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Rebuilding the report for {Day:d}", day);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
```

Job methods can be synchronous or return `Task`. A `CancellationToken` parameter receives a token that is canceled when the server shuts down; when you create the job, pass `CancellationToken.None` as a placeholder (see [Cancellation](../jobs/cancellation.md)).

### Step 4: create jobs at start-up

A hosted service is a convenient place to register recurring jobs. Add `JobRegistration.cs` to the `Shop.Worker` project:

```csharp
using Masterly.NightShift;
using Shop.Jobs;

namespace Shop.Worker;

public class JobRegistration : BackgroundService
{
    private readonly IBackgroundJobClient _jobs;
    private readonly IRecurringJobManager _recurringJobs;

    public JobRegistration(IBackgroundJobClient jobs, IRecurringJobManager recurringJobs)
    {
        _jobs = jobs;
        _recurringJobs = recurringJobs;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6));

        _jobs.Enqueue<ReportService>(x => x.Rebuild(DateTime.UtcNow.Date.AddDays(-1), CancellationToken.None));

        return Task.CompletedTask;
    }
}
```

`Cron.Daily(6)` fires every day at 06:00 UTC. `AddOrUpdate` is safe to call on every start: it updates the existing recurring job instead of adding a second one. The `Enqueue` call is only there so you have something to see right away.

### Step 5: configure and run

Use the `Program.cs` from the [Quick example](#quick-example) above, then run:

```
dotnet run
```

The log shows `Starting Masterly.NightShift Server using job storage: ...`, then `Rebuilding the report for ...` as soon as a worker picks up the job. To see it in the dashboard, add the [dashboard app](#see-the-jobs-in-the-dashboard) below.

To run the worker as a Windows Service or a systemd unit, use the usual Generic Host integration for that platform. Nothing in the job configuration changes.

## Console app or .NET Framework app without dependency injection

Without the Generic Host, configure `GlobalConfiguration.Configuration` once at start-up and start a `BackgroundJobServer` yourself. This works the same way in a .NET 8+ console app and in a .NET Framework 4.6.2+ app (Windows Service, WinForms, classic ASP.NET).

```
dotnet new console -n Shop.Runner
cd Shop.Runner
dotnet add package Masterly.NightShift.Core
dotnet add package Masterly.NightShift.SqlServer
dotnet add package Microsoft.Data.SqlClient
```

For .NET Framework, set the target framework in `Shop.Runner.csproj` and remove the `ImplicitUsings` and `Nullable` lines, which C# 7.3 (the default language version on .NET Framework) doesn't support:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net48</TargetFramework>
</PropertyGroup>
```

On .NET Framework, `Microsoft.Data.SqlClient` is optional: the driver built into `System.Data` is used when no other is installed.

Replace `Program.cs` with this code. It uses a `Main` method and explicit `using` directives, so it compiles on .NET Framework as well as on .NET 8 and later:

```csharp
using System;
using Masterly.NightShift;

namespace Shop.Runner
{
    public static class Program
    {
        public static void Main()
        {
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
        }
    }

    public class ReportService
    {
        public void BuildDaily()
        {
            Console.WriteLine($"Daily report built at {DateTime.UtcNow:O}");
        }
    }
}
```

- `UseSqlServerStorage(...)` sets `JobStorage.Current`, which the static `BackgroundJob` and `RecurringJob` classes and `new BackgroundJobServer()` use.
- `UseColouredConsoleLogProvider()` writes the library's log to the console. Without an explicit log provider, the library uses a logging library it finds loaded (Serilog, NLog, log4net and others), or logs nothing (see [Logging](../monitoring/logging.md)).
- Without a container, job classes are created with `Activator.CreateInstance`, so they need a public parameterless constructor. To create them from your own container, derive from `JobActivator` and pass it to `UseActivator(...)` (see [Dependency injection](../server/dependency-injection.md)).
- Disposing the server stops it: workers finish or cancel their jobs, and the server is removed from the **Servers** page. In a Windows Service, create the server in `OnStart` and dispose it in `OnStop`.

## See the jobs in the dashboard

A Worker Service or console app has no web server, so serve the dashboard from an ASP.NET Core app pointed at the same database. It can be an app you already have, or a small one just for the dashboard. From the folder that holds `Shop.Jobs` and `Shop.Worker`:

```
dotnet new web -n Shop.Dashboard
cd Shop.Dashboard
dotnet add package Masterly.NightShift.AspNetCore
dotnet add package Masterly.NightShift.SqlServer
dotnet add package Microsoft.Data.SqlClient
dotnet add reference ../Shop.Jobs/Shop.Jobs.csproj
```

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

var app = builder.Build();

app.MapNightShiftDashboard();

app.Run();
```

Give it the same `ConnectionStrings:NightShift` value in its `appsettings.json`, run it, and open `/nightshift`. The **Servers** page lists the worker process, **Jobs > Succeeded** lists the `Rebuild` job, and **Recurring Jobs** lists `daily-report`, where **Trigger now** runs it right away. There is no `AddNightShiftServer()` call, so this app shows jobs but never runs them.

The reference to `Shop.Jobs` matters: the dashboard loads job types to show their names and arguments. Without the assembly that defines `ReportService`, jobs are listed as "Can not find the target method." That is why the job classes live in a class library that the worker, the dashboard app and any app that creates jobs all reference. Don't reference the Worker Service project from the web app instead: both have an `appsettings.json`, and `dotnet publish` of the web app fails with error NETSDK1152.

> **Note:** The dashboard only answers local requests by default. Before you expose it, set up [Dashboard authorization](../dashboard/authorization.md).

## Options / API

| Member | Parameters | Default | What it does |
|---|---|---|---|
| `services.AddNightShift(configuration)` | `Action<IGlobalConfiguration>` or `Action<IServiceProvider, IGlobalConfiguration>` | | Registers the client services, the container-backed job activator and `ILoggerFactory` logging |
| `services.AddNightShiftServer(...)` | none, `Action<BackgroundJobServerOptions>`, `Action<IServiceProvider, BackgroundJobServerOptions>`, optional `JobStorage` and `IEnumerable<IBackgroundProcess>` | Options from DI or `new BackgroundJobServerOptions()` | Runs a server as an `IHostedService` |
| `GlobalConfiguration.Configuration` | | | The process-wide configuration the `Use*` methods change (see [Global configuration](../configuration/global-configuration.md)) |
| `new BackgroundJobServer()` | optional `BackgroundJobServerOptions`, optional `JobStorage` | `new BackgroundJobServerOptions()`, `JobStorage.Current` | Starts a server immediately; `Dispose()` stops it |
| `UseColouredConsoleLogProvider()` | optional `LogLevel minLevel` | | Logs to the console in colour |

The full list of server options is in [Server options](../server/server-options.md).

## More examples

### Listen to several queues with fewer workers

```csharp
var options = new BackgroundJobServerOptions
{
    WorkerCount = 4,
    Queues = new[] { "critical", "default" },
};

using (var server = new BackgroundJobServer(options))
{
    Console.ReadLine();
}
```

### Change SQL Server storage options

```csharp
using Masterly.NightShift.SqlServer;

GlobalConfiguration.Configuration
    .UseSqlServerStorage(
        @"Server=(localdb)\MSSQLLocalDB;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;",
        new SqlServerStorageOptions
        {
            PrepareSchemaIfNecessary = true,
        });
```

Every option is described in [SQL Server options](../storage/sql-server-options.md).

## Things to know

- Configure once, at start-up, before you create jobs or start servers. Calling `Use*` methods later changes global state that running servers already read.
- Every process that runs jobs needs the job classes. If a web app creates `ReportService.BuildDaily` jobs, the worker must reference the assembly that defines `ReportService`; otherwise the worker can't load the job, which is retried and then fails with a reason like "Can not change the state to 'Enqueued': the job's type or method was not found." (see [Troubleshooting](../troubleshooting.md#a-job-fails-because-it-cant-be-loaded)).
- You can run as many worker processes as you like against one database. A job is fetched by one worker at a time, and it runs at least once rather than exactly once (see [Core concepts](core-concepts.md#at-least-once-execution)).

## See also

- [Quick start: ASP.NET Core](quick-start-aspnetcore.md)
- [Core concepts](core-concepts.md)
- [Queues and workers](../server/queues-and-workers.md)
- [Dependency injection](../server/dependency-injection.md)
- [Dashboard setup](../dashboard/setup.md)
