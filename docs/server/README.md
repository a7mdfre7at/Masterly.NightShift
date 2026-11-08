# Server

The background job server is the part of Masterly.NightShift that runs your jobs. It fetches jobs from the queues it listens to and runs them on its worker threads, enqueues delayed jobs and recurring jobs when they are due, sends heartbeats so other servers know it is alive, and stops gracefully when your application shuts down. You can run one server or many, in your web application or in a separate process, on one machine or several; they all share the same storage. In ASP.NET Core and Worker Service apps you add a server with `AddNightShiftServer`; anywhere else you create a `BackgroundJobServer`.

| Page | What it covers |
| --- | --- |
| [Server options](server-options.md) | Every `BackgroundJobServerOptions` property: workers, queues, timeouts, polling, heartbeats, activator, filters. |
| [Queues and workers](queues-and-workers.md) | Queue order and priority, the `Queue` attribute, choosing a queue per job, several servers, worker counts for CPU- and I/O-bound jobs, graceful shutdown. |
| [Drain mode](drain-mode.md) | Pause processing for the whole cluster, one queue or one server, without stopping anything. |
| [Keep-alive](keep-alive.md) | Keep IIS from shutting down an idle application, the `/ping` route, and what the host must still do. |
| [Dependency injection](dependency-injection.md) | `AddNightShift` and `AddNightShiftServer`, the container-backed job activator, a scope per job, factories, and running without a container. |

## The most common setup

An ASP.NET Core application that runs one server with two queues and shows the dashboard:

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Jobs")));

builder.Services.AddNightShiftServer(options =>
{
    options.Queues = new[] { "critical", "default" };
});

var app = builder.Build();
app.MapNightShiftDashboard();
app.Run();
```

The server starts when the application has started and stops with it. Without a host, create it yourself and dispose it when the application exits:

```csharp
using Masterly.NightShift;

GlobalConfiguration.Configuration
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;");

using (var server = new BackgroundJobServer())
{
    Console.WriteLine("Press Enter to stop the server.");
    Console.ReadLine();
}
```

## See also

- [Core concepts](../getting-started/core-concepts.md)
- [Quick start: ASP.NET Core](../getting-started/quick-start-aspnetcore.md)
- [Quick start: worker](../getting-started/quick-start-worker.md)
- [SQL Server storage](../storage/sql-server.md)
