# Quick start: ASP.NET Core

This page builds an ASP.NET Core app that stores jobs in SQL Server, processes them in the same process, and shows them in the dashboard. Use it when your jobs are created by a web app or API and you're fine running the workers inside it. To process jobs in a separate process, see [Quick start: console and Worker Service](quick-start-worker.md).

## Quick example

The finished `Program.cs` (`EmailService` and `ReportService` are written in steps 4 and 7):

```csharp
using Masterly.NightShift;
using Shop.Web;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? throw new InvalidOperationException("Connection string 'NightShift' is missing.");

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));

builder.Services.AddNightShiftServer();

builder.Services.AddTransient<EmailService>();
builder.Services.AddTransient<ReportService>();

var app = builder.Build();

app.MapNightShiftDashboard();

app.MapPost("/users/{userId:int}/welcome", (int userId, IBackgroundJobClient jobs) =>
{
    var jobId = jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));
    return Results.Accepted(value: new { jobId });
});

app.MapPost("/users/{userId:int}/reminder", (int userId, IBackgroundJobClient jobs) =>
{
    var jobId = jobs.Schedule<EmailService>(x => x.SendWelcome(userId), TimeSpan.FromMinutes(1));
    return Results.Accepted(value: new { jobId });
});

var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily());

app.Run();
```

The steps below build it up and explain each part.

## Step 1: create the project and add the packages

```
dotnet new web -n Shop.Web
cd Shop.Web
dotnet add package Masterly.NightShift
dotnet add package Microsoft.Data.SqlClient
```

`Masterly.NightShift` brings the core library, the SQL Server storage and the ASP.NET Core integration. `Microsoft.Data.SqlClient` is the SQL Server driver; the storage doesn't pick one for you (see [Installation](installation.md#the-sql-server-driver)). If the build then warns about `System.Security.Cryptography.Pkcs` (NU1902, NU1903), see [The SQL Server driver](installation.md#the-sql-server-driver).

## Step 2: create a database and add the connection string

The storage creates its tables by itself, but the database must exist. With SQL Server Express LocalDB, which comes with Visual Studio:

```
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "CREATE DATABASE NightShift"
```

Any SQL Server works; use your own server name instead of `(localdb)\MSSQLLocalDB`. Then add the connection string to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "NightShift": "Server=(localdb)\\MSSQLLocalDB;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

The account the app runs as needs rights to create tables in that database the first time, because the schema is installed on start-up. If it can't have them, install the schema by hand (see [Schema and upgrades](../storage/schema-and-upgrades.md)).

## Step 3: configure storage, server and dashboard

Replace `Program.cs` with:

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

app.Run();
```

What each call does:

- `AddNightShift(...)` registers `IBackgroundJobClient`, `IRecurringJobManager`, `JobStorage` and the other client services. The callback configures the global configuration once, the first time one of those services is needed. It also sends the library's logs to ASP.NET Core logging and creates job classes from the service container.
- `SetDataCompatibilityLevel(CompatibilityLevel.Version_180)`, `UseSimpleAssemblyNameTypeSerializer()` and `UseRecommendedSerializerSettings()` choose the newest, most compact data format. They are right for a new app. If servers of an older version share the database, read [Compatibility levels](../configuration/compatibility-levels.md) first.
- `UseSqlServerStorage(connectionString)` stores jobs in SQL Server and installs the schema on start-up.
- `AddNightShiftServer()` runs a background job server as a hosted service. It starts once the app has started. When the app stops, the server stops fetching jobs and, half a second later (`StopTimeout`), cancels the `CancellationToken` that running jobs receive. A job that honours the token stops and is enqueued again, to run later on any server; a job that ignores it keeps running while the host waits for the server, up to the host's shutdown timeout. To give jobs more time to finish, raise `StopTimeout` in `AddNightShiftServer(options => ...)`.
- `MapNightShiftDashboard()` serves the dashboard at `/nightshift`.

Run the app:

```
dotnet run
```

The console shows `Starting Masterly.NightShift Server using job storage: ...` followed by the server options. Then the app prints the address it listens on, `Now listening on: http://localhost:<port>`. The `dotnet new web` template picks a random port for each new project (it is in `Properties/launchSettings.json`); to choose it yourself, run `dotnet run -- --urls http://localhost:5000`. Open `/nightshift` on that address, for example `http://localhost:5000/nightshift`. The **Servers** page lists one server with its workers.

> **Note:** By default the dashboard only answers requests from the local machine. Anyone else, including a browser outside a Docker container, gets `401`. Set up authorization before you deploy (see [Dashboard authorization](../dashboard/authorization.md)).

## Step 4: write a job class

A job is an ordinary public method. Add `EmailService.cs`:

```csharp
namespace Shop.Web;

public class EmailService
{
    private readonly ILogger<EmailService> _logger;

    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
    }

    public async Task SendWelcome(int userId)
    {
        _logger.LogInformation("Sending the welcome e-mail to user {UserId}", userId);
        await Task.Delay(TimeSpan.FromSeconds(2)); // pretend to call an e-mail API
    }
}
```

When the job runs, the server creates `EmailService` from the service container in a new scope, so constructor injection works. Registering the class is optional: an unregistered class is still created with its constructor dependencies resolved from the container. Register it when you want to control its lifetime or put an interface in front of it.

## Step 5: create jobs

Inject `IBackgroundJobClient` wherever you need it. Add two endpoints before `app.Run()`, and a `using Shop.Web;` line at the top of `Program.cs`:

```csharp
app.MapPost("/users/{userId:int}/welcome", (int userId, IBackgroundJobClient jobs) =>
{
    var jobId = jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));
    return Results.Accepted(value: new { jobId });
});

app.MapPost("/users/{userId:int}/reminder", (int userId, IBackgroundJobClient jobs) =>
{
    var jobId = jobs.Schedule<EmailService>(x => x.SendWelcome(userId), TimeSpan.FromMinutes(1));
    return Results.Accepted(value: new { jobId });
});
```

`Enqueue` creates a fire-and-forget job: it is stored right away and runs as soon as a worker is free. `Schedule` creates a delayed job that runs after the given delay. Both return the new job's ID (or `null` if a filter canceled the creation). The lambda is not run here: the method, its type and the argument `userId` are written to the database, and a worker calls the method later.

Run the app again and call the endpoint, with the address the app prints:

```
curl -X POST http://localhost:<port>/users/42/welcome
```

## Step 6: see the job in the dashboard

Open `/nightshift` and go to **Jobs**. The job shows under **Succeeded** after about two seconds (it may pass through **Enqueued** and **Processing** if you're quick). Click its ID to open the details page: the method and arguments, every state the job went through with timestamps, and the server and worker that ran it. Call the `/reminder` endpoint and the job waits under **Scheduled** for a minute before it runs.

To see a failure, make `SendWelcome` throw. The job goes to **Retries** and is tried again with growing delays, up to 10 retries, before it ends under **Failed** (see [Retries](../filters/retries.md)).

## Step 7: add a recurring job

Add `ReportService.cs`:

```csharp
namespace Shop.Web;

public class ReportService
{
    public void BuildDaily()
    {
        Console.WriteLine($"Daily report built at {DateTime.UtcNow:O}");
    }
}
```

And register the schedule after `app.MapNightShiftDashboard()`:

```csharp
var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily());
```

`AddOrUpdate` creates the recurring job, or updates it if one with the ID `daily-report` exists, so running it on every start-up is safe. `Cron.Daily()` fires at midnight UTC. The **Recurring Jobs** page lists it with its next run; select it and click **Trigger now** to run it immediately.

## Options / API

The calls used on this page. All are extension methods in the `Masterly.NightShift` namespace.

| Method | Parameters | Default | What it does |
|---|---|---|---|
| `services.AddNightShift(configuration)` | `Action<IGlobalConfiguration>` or `Action<IServiceProvider, IGlobalConfiguration>` | | Registers the client services and configures storage, serialization and filters. Call it once |
| `services.AddNightShiftServer()` | none, `Action<BackgroundJobServerOptions>`, `Action<IServiceProvider, BackgroundJobServerOptions>`, or a `JobStorage` | Options from DI if registered, otherwise `new BackgroundJobServerOptions()` | Runs a `BackgroundJobServer` as a hosted service. Each call adds another server |
| `config.UseSqlServerStorage(connectionString, options)` | `string` connection string (or `Func<DbConnection>`), optional `SqlServerStorageOptions` | `new SqlServerStorageOptions()` | Uses SQL Server for storage. The string must be a connection string, not a connection string name |
| `endpoints.MapNightShiftDashboard(pattern, options, storage)` | `string pattern`, `DashboardOptions? options`, `JobStorage? storage` | `"/nightshift"`, options from DI or defaults, storage from DI | Maps the dashboard. .NET 8 and later |
| `app.UseNightShiftDashboard(pathMatch, options, storage)` | Same as above | `"/nightshift"` | Middleware alternative to `MapNightShiftDashboard`, available on every target |

The most common server options, set through `AddNightShiftServer(options => ...)`:

| Option | Type | Default | Meaning |
|---|---|---|---|
| `WorkerCount` | `int` | `Math.Min(Environment.ProcessorCount * 5, 20)` | How many jobs this server runs at the same time |
| `Queues` | `string[]` | `{ "default" }` | The queues this server takes jobs from |
| `ServerName` | `string?` | `null` (the machine name is used) | The name shown on the **Servers** page |
| `SchedulePollingInterval` | `TimeSpan` | 15 seconds | How often delayed jobs are checked for being due |

All server options are in [Server options](../server/server-options.md).

## More examples

### Use the static facade instead of injection

`BackgroundJob` and `RecurringJob` are static versions of the same API. They work once the configuration has run, which in ASP.NET Core happens when the server starts or the dashboard is mapped:

```csharp
BackgroundJob.Enqueue<EmailService>(x => x.SendWelcome(userId));
RecurringJob.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily());
```

Prefer the injected interfaces: they're easier to test and never run before the configuration.

### Change the worker count, queues and dashboard path

```csharp
builder.Services.AddNightShiftServer(options =>
{
    options.WorkerCount = 5;
    options.Queues = new[] { "critical", "default" };
});

// ...

app.MapNightShiftDashboard("/jobs");
```

Jobs go to the `default` queue unless you say otherwise, for example with `[Queue("critical")]` on the method (see [Queues and workers](../server/queues-and-workers.md)).

### Create jobs without processing them

A web app can create jobs and leave the processing to another app, such as a Worker Service, that uses the same database. Leave out `AddNightShiftServer()` in the web app and keep `AddNightShift(...)`. Both apps need the assembly that contains the job classes.

## Things to know

- Jobs run in the background on server worker threads, outside of any HTTP request. Don't pass `HttpContext`, a `DbContext` or other request-bound objects as arguments: pass IDs and load what you need inside the job (see [Job arguments](../jobs/job-arguments.md)).
- The server stops when the app stops. Running jobs see their `CancellationToken` canceled. A job interrupted by the shutdown, or still running when the process exits, is not lost: it goes back to its queue and runs again later (see [Cancellation](../jobs/cancellation.md)).
- When hosted in IIS, the app pool shuts down after 20 idle minutes by default, and the server with it. See [Keep-alive](../server/keep-alive.md).

## See also

- [Core concepts](core-concepts.md)
- [Fire-and-forget jobs](../jobs/fire-and-forget.md), [Delayed jobs](../jobs/delayed-jobs.md), [Creating recurring jobs](../recurring-jobs/creating-recurring-jobs.md)
- [Dashboard setup](../dashboard/setup.md) and [Dashboard authorization](../dashboard/authorization.md)
- [Dependency injection](../server/dependency-injection.md)
- [SQL Server storage](../storage/sql-server.md)
