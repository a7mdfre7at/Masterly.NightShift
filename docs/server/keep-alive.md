# Keep-alive

IIS shuts down a worker process that has received no requests for 20 minutes (the application pool's idle timeout), and the background job server running inside it stops with it: delayed and recurring jobs stop firing until the next request arrives. The keep-alive process requests one of the application's own URLs at a fixed interval, so IIS counts the application as active and the idle timeout never fires.

## Quick example

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Jobs")));
builder.Services.AddNightShiftServer();
builder.Services.AddNightShiftKeepAlive(options =>
{
    options.Url = "https://jobs.example.com/nightshift/ping";
});

var app = builder.Build();
app.MapNightShiftDashboard();
app.Run();
```

Every 5 minutes the server requests `https://jobs.example.com/nightshift/ping`, a dashboard route that answers `pong` without touching the storage.

## How it works

`KeepAliveProcess` is an extra background process of the job server. `AddNightShiftKeepAlive` registers it in the container as an `IBackgroundProcess`, and `AddNightShiftServer` picks it up and runs it on a thread of its own next to the workers. The process waits for `Interval`, sends one `GET` request to the URL, and repeats until the server is asked to stop. The first request is sent one interval after the server starts. When the server stops, the process ends its wait (or cancels a request in flight) at once, so it never delays shutdown.

Any HTTP response counts: a `401`, `404` or `500` still proves that the host handled a request, which is all the idle timeout looks at. A non-success status code is recorded as a note and, when `LogFailures` is on, logged as a warning, because it usually means the URL is wrong or blocked by the dashboard authorization. Only a missing response (DNS failure, refused connection, timeout) counts as a failure.

The result of the last request is kept in `KeepAliveProcess.Status`. The dashboard's Servers page shows it in the card for the current process, together with the process name and ID, whether it runs under IIS, and its uptime. When the process runs under IIS and no keep-alive process is configured, the card shows a warning.

### The /ping route

The dashboard has a `GET /ping` route (so `/nightshift/ping` with the default dashboard path). It answers `200` with the text `pong` and does not touch the storage, so it is the cheapest URL to request. It is also handy for load-balancer liveness checks. Like every dashboard route, it goes through the dashboard's authorization filters; see [Things to know](#things-to-know).

### What keep-alive does not cover

Keep-alive only prevents the idle timeout. These still stop the process, and nothing inside the process can start it again:

- a scheduled application pool recycle,
- a deployment, an `app_offline.htm`, or a configuration change that restarts the application,
- a server reboot,
- the very first start: IIS does not start the application until the first request arrives.

For those, configure the host:

- **IIS:** set the application pool's `startMode` to `AlwaysRunning`, enable `preloadEnabled` on the site, and set the idle timeout to 0.
- **Azure App Service:** turn on **Always On**.
- **Anywhere:** run the job server in a process that is not a web application, such as a Worker Service or a Windows Service (see [Quick start: worker](../getting-started/quick-start-worker.md)). The web application can still enqueue jobs and show the dashboard without running a server.

## API

### AddNightShiftKeepAlive

In `Masterly.NightShift.AspNetCore`, namespace `Masterly.NightShift`:

```csharp
public static IServiceCollection AddNightShiftKeepAlive(
    this IServiceCollection services,
    Action<KeepAliveOptions> configure = null,
    string path = "/nightshift/ping")
```

| Parameter | Type | Default | Meaning |
| --- | --- | --- | --- |
| `services` | `IServiceCollection` | | The service collection. Call it next to `AddNightShiftServer()`. |
| `configure` | `Action<KeepAliveOptions>` | `null` | Changes to the defaults: URL, interval, timeout, logging. |
| `path` | `string` | `"/nightshift/ping"` | Used only when neither `Url` nor `UrlFactory` is set: the path appended to the server's own address. Change it if the dashboard is mapped somewhere other than `/nightshift`. |

When neither `Url` nor `UrlFactory` is set, on .NET 8 and later the URL is built from the addresses the web server listens on (`IServerAddressesFeature`): the first `http://` address is preferred over `https://`, an "any address" host (`*`, `+`, `0.0.0.0`, `[::]`) is replaced by `localhost`, and `path` is appended. The address is looked up on the first request; if the server has no address yet, it is looked up again on the next one. On the `netstandard2.0` build of the package, the address cannot be looked up and `Url` is required: without it, creating the process throws `InvalidOperationException`.

### KeepAliveOptions

In `Masterly.NightShift.Core`, namespace `Masterly.NightShift.Server`:

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Url` | `string?` | `null` | The URL to request, normally the application's public address. The request must go through the host that applies the idle timeout (IIS), not a Kestrel port behind it. |
| `UrlFactory` | `Func<string?>?` | `null` | Returns the URL, for hosts that only know their address after start-up. Used when `Url` is `null`. Called on the first request; when it returns `null` or throws, it is called again on the next request. |
| `Interval` | `TimeSpan` | 5 minutes | How often the request is sent. Must be greater than zero and well below the idle timeout (20 minutes by default in IIS). |
| `Timeout` | `TimeSpan` | 15 seconds | The request timeout. Must be greater than zero. |
| `LogFailures` | `bool` | `true` | Whether failed requests and non-success status codes are logged as warnings. |

Either `Url` or `UrlFactory` must be set when the process is created (`AddNightShiftKeepAlive` sets `UrlFactory` for you on .NET 8 and later); otherwise the constructor throws `ArgumentException`. An `Interval` or `Timeout` of zero or less throws `ArgumentOutOfRangeException`.

### KeepAliveProcess

In `Masterly.NightShift.Core`, namespace `Masterly.NightShift.Server`. Implements `IBackgroundProcess` and `IDisposable`.

| Member | Meaning |
| --- | --- |
| `KeepAliveProcess(string url)` | Requests `url` every 5 minutes. |
| `KeepAliveProcess(KeepAliveOptions options)` | Uses the given options. |
| `bool Ping(CancellationToken cancellationToken)` | Sends one request now. Returns whether the host answered, with any status code. |
| `static KeepAliveStatus? Status` | The result of the last request in this process, or `null` when no keep-alive process was created. |
| `static bool IsHostedByIis` | Whether the current process is IIS (`w3wp`) or IIS Express, where the idle timeout applies. |
| `Dispose()` | Disposes the internal `HttpClient`. |

### KeepAliveStatus

| Property | Type | Meaning |
| --- | --- | --- |
| `Url` | `string?` | The URL being requested, once resolved. |
| `LastAttemptAt` | `DateTime?` | When the last request was sent (UTC), or `null` before the first one. |
| `LastSucceeded` | `bool?` | `true` when the last request got any response, `false` when it got none, `null` before the first one. |
| `LastError` | `string?` | The last failure, or a note such as `HTTP 401 Unauthorized` for a non-success status code. |
| `Interval` | `TimeSpan` | The configured interval. |

## More examples

### Let the server find its own address

On .NET 8 and later, without a URL, the process requests `/nightshift/ping` on the first address the application listens on:

```csharp
services.AddNightShiftKeepAlive();
```

This suits hosts where the application's own port is the one that counts. Behind IIS, prefer the public URL: with out-of-process hosting the listening address is a Kestrel port behind IIS, and a request to it does not reset the IIS idle timer.

### Dashboard on another path

```csharp
// The dashboard is mapped at /admin/jobs instead of /nightshift
services.AddNightShiftKeepAlive(path: "/admin/jobs/ping");
```

### Request a different endpoint

Any URL of the application works. A health-check endpoint that allows anonymous access avoids the dashboard authorization altogether:

```csharp
services.AddNightShiftKeepAlive(options =>
{
    options.Url = "https://jobs.example.com/healthz";
    options.Interval = TimeSpan.FromMinutes(2);
    options.Timeout = TimeSpan.FromSeconds(30);
    options.LogFailures = false;
});
```

### Read the URL from configuration on first use

```csharp
builder.Services.AddNightShiftKeepAlive(options =>
{
    // Read on the first request; null means "not known yet, ask again next time"
    options.UrlFactory = () => builder.Configuration["Jobs:PublicUrl"] is string publicUrl
        ? $"{publicUrl.TrimEnd('/')}/nightshift/ping"
        : null;
});
```

### Without dependency injection

Pass the process to the server as an additional process, for example in an ASP.NET application on .NET Framework:

```csharp
using System;
using Masterly.NightShift;
using Masterly.NightShift.Server;

GlobalConfiguration.Configuration
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;");

using (var keepAlive = new KeepAliveProcess("https://jobs.example.com/nightshift/ping"))
using (var server = new BackgroundJobServer(
    new BackgroundJobServerOptions(),
    JobStorage.Current,
    new IBackgroundProcess[] { keepAlive }))
{
    Console.WriteLine("Press Enter to stop.");
    Console.ReadLine();
}
```

The server does not dispose additional processes; dispose the keep-alive process after the server.

### Check the status from code

```csharp
using Masterly.NightShift.Server;

KeepAliveStatus? status = KeepAliveProcess.Status;

if (status == null)
{
    Console.WriteLine(KeepAliveProcess.IsHostedByIis
        ? "Hosted by IIS without keep-alive: the server stops when the app pool idles."
        : "Keep-alive is not configured.");
    return;
}

Console.WriteLine($"{status.Url} every {status.Interval}: last attempt {status.LastAttemptAt:u}, " +
    $"reached = {status.LastSucceeded}, note = {status.LastError}");
```

## Things to know

- **Dashboard authorization applies to `/ping`.** The default filter allows local requests only. A request the machine sends to its own address usually passes it, unless it goes through a proxy or load balancer. With your own authorization filters the request gets `401` or `403`. That still keeps the host alive, but a warning is logged every interval; request an anonymous endpoint instead, or set `LogFailures` to `false`.
- **The dashboard must be mapped** for `/nightshift/ping` to exist. Without the dashboard, point `Url` at another endpoint of the application.
- **Explicit process lists replace registered ones.** The `AddNightShiftServer` overloads that take an `IEnumerable<IBackgroundProcess>` use that list instead of the processes registered in the container, so a process added with `AddNightShiftKeepAlive` is not run. Put a `KeepAliveProcess` in the list yourself.
- **Every server in the process runs it.** With several `AddNightShiftServer` calls, each server runs the registered keep-alive process, so the URL is requested once per server per interval. That is harmless.
- **`KeepAliveProcess.Status` is process-wide.** It holds the result of the most recently created keep-alive process.
- **The first request comes one interval after start.** If the application pool idles out sooner than that after a start, lower `Interval`.

## See also

- [Server options](server-options.md)
- [Dependency injection](dependency-injection.md)
- [Dashboard setup](../dashboard/setup.md)
- [Dashboard authorization](../dashboard/authorization.md)
- [Quick start: worker](../getting-started/quick-start-worker.md)
- [Troubleshooting](../troubleshooting.md)
