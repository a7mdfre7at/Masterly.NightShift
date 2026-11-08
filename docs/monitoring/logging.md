# Logging

Masterly.NightShift writes its own diagnostic messages (server start-up, job retries and failures, storage maintenance, problems in background processes) to a log provider. In an ASP.NET Core or generic-host app they go to `Microsoft.Extensions.Logging` automatically; other apps pick a provider. This page covers where the messages go, how to filter or silence them, how to log from inside your jobs, and `[LogInvocation]`, which writes to the job's dashboard console rather than to the log.

## Quick example

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

// Masterly.NightShift writes to the app's ILoggerFactory automatically
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")));

builder.Services.AddNightShiftServer();
```

Filter the messages like any other category in `appsettings.json`. Every category starts with `Masterly.NightShift`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Masterly.NightShift": "Warning"
    }
  }
}
```

## How it works

Internally Masterly.NightShift logs through a vendored copy of LibLog (`Masterly.NightShift.Logging`): every class asks `LogProvider` for an `ILog`, and the provider set with a `Use...LogProvider()` method decides where the messages go. An `ILog` looks the provider up when it writes, not when it is created, so setting or changing the provider applies to every logger, including the ones created earlier, for example by the default global filters. The `netstandard2.0` build is an exception, see [Things to know](#things-to-know).

**With `AddNightShift` (ASP.NET Core, Worker Service, generic host).** When the configuration is first resolved from the container (which happens when the server's hosted service, the dashboard, `IBackgroundJobClient` or `JobStorage` is first used), `AddNightShift` sets an `AspNetCoreLogProvider` built on the container's `ILoggerFactory`, and then runs your configuration callback. So logging needs no code, and a `Use...LogProvider()` call inside the callback replaces the default.

**Without `AddNightShift`.** If no provider was set, Masterly.NightShift looks for a known logging library the first time it writes a message and uses the first one it finds, in this order: Serilog (its static `Log.Logger`), NLog, log4net, Enterprise Library Logging, Loupe, ELMAH. If none is loaded, nothing is logged. Call one of the `Use...LogProvider()` methods to choose explicitly.

### Categories

The category (logger name) is the full name of the class that writes the message. Some of the useful ones:

| Category | What it logs |
|---|---|
| `Masterly.NightShift.BackgroundJobServer` | Server start (storage and the options in use), queue names that don't match the naming rules, stop |
| `Masterly.NightShift.AutomaticRetryAttribute` | Each failed attempt that will be retried (Warning), a job that failed for good (Error), a job deleted after its last attempt (Warning) |
| `Masterly.NightShift.Server.Worker` | State changes that kept failing, a worker stopped while it was processing a job |
| `Masterly.NightShift.Server.DelayedJobScheduler`, `...Server.RecurringJobScheduler` | Scheduled and recurring jobs that could not be enqueued |
| `Masterly.NightShift.Server.ServerWatchdog` | Servers removed because their heartbeat timed out |
| `Masterly.NightShift.Processing.BackgroundExecution` | A background process that keeps failing (for example the storage is unreachable) and its recovery |
| `Masterly.NightShift.SqlServer.SqlServerStorage`, `...SqlServer.ExpirationManager`, `...SqlServer.CountersAggregator` | Schema installation, cleanup of expired records |
| `Masterly.NightShift.CircuitBreakerAttribute` | Circuits opening |
| `Masterly.NightShift.Alert`, `Masterly.NightShift.WebhookAlertSink` | Alert sinks that failed, webhook requests rejected or dropped |

### Levels

| Masterly.NightShift level | `Microsoft.Extensions.Logging` level | Used for |
|---|---|---|
| `Trace` | `Trace` | Very detailed progress of maintenance processes |
| `Debug` | `Debug` | Background process loops starting and stopping, scheduler batch counts, transient errors that are retried |
| `Info` | `Information` | Server start-up and its options, servers removed by the watchdog, processes recovering, jobs that asked to be retried later |
| `Warn` | `Warning` | A job attempt failed and will be retried, a job was deleted after its retries, an alert or webhook failed, a circuit opened, the server stops because the process exits |
| `Error` | `Error` | A job failed after all its retries, a background process is in the Failed state, state changes failed repeatedly, cleanup failed |
| `Fatal` | `Critical` | A background process had to stop |

Messages are written as plain, already-formatted text (no message template or structured properties) with event id 0, and the exception, when there is one, is passed as the log entry's exception.

## Options / API

All methods are extensions on `IGlobalConfiguration` and replace the current provider for the whole process.

| Method | Provider |
|---|---|
| `UseLogProvider<TLogProvider>(TLogProvider provider)` | Any `ILogProvider`, including your own and `AspNetCoreLogProvider`. |
| `UseNoOpLogProvider()` | Turns all Masterly.NightShift logging off. |
| `UseColouredConsoleLogProvider()` | Coloured console output, `Info` and above. |
| `UseColouredConsoleLogProvider(LogLevel minLevel)` | Coloured console output from `minLevel` (a `Masterly.NightShift.Logging.LogLevel`). |
| `UseSerilogLogProvider()` | Serilog's static `Log.Logger`. |
| `UseNLogLogProvider()` | NLog. |
| `UseLog4NetLogProvider()` | log4net. |
| `UseEntLibLogProvider()` | Enterprise Library Logging. |
| `UseLoupeLogProvider()` | Loupe (Gibraltar). |
| `UseElmahLogProvider()`, `UseElmahLogProvider(LogLevel minLevel)` | ELMAH. |

The Serilog, NLog, log4net, Enterprise Library, Loupe and ELMAH providers find the library by reflection, so Masterly.NightShift has no package dependency on them; reference the library in your app.

| Type | Namespace | Meaning |
|---|---|---|
| `AspNetCoreLogProvider` | `Masterly.NightShift.AspNetCore` (package `Masterly.NightShift.NetCore`) | Writes to a `Microsoft.Extensions.Logging.ILoggerFactory`: `new AspNetCoreLogProvider(loggerFactory)`. |
| `ILogProvider` | `Masterly.NightShift.Logging` | `ILog GetLogger(string name)`. |
| `ILog` | `Masterly.NightShift.Logging` | `bool Log(LogLevel logLevel, Func<string> messageFunc, Exception exception = null)`. Called with a `null` `messageFunc` to ask whether a level is enabled. |
| `LogLevel` | `Masterly.NightShift.Logging` | `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`. |
| `LogProvider` | `Masterly.NightShift.Logging` | `For<T>()`, `GetLogger(Type)`, `GetLogger(string)`, `SetCurrentLogProvider(ILogProvider)`. `SetCurrentLogProvider` reaches only loggers created after the call; use `UseLogProvider` instead. |

`LogLevel` has the same name as `Microsoft.Extensions.Logging.LogLevel`. In a file that imports both namespaces, use an alias: `using NightShiftLogLevel = Masterly.NightShift.Logging.LogLevel;`.

### `[LogInvocation]`

| Member | Meaning |
|---|---|
| `[LogInvocation]` | A job interceptor. Apply to a job class, interface or method, or register it for every job with `UseJobInterceptor(new LogInvocationAttribute())`. Its only property is the inherited `Order`, the position among other interceptors (lower runs first, outermost). |

It writes three kinds of lines to the job's console (`IJobConsole`), which the dashboard shows live on the job details page. It does not write to the application log. If the storage fails to write a line, the line is dropped with a warning in the application log; the job's outcome doesn't change. See [Progress, console and checkpoints](../jobs/progress-and-console.md).

| When | Console level | Line |
|---|---|---|
| Before the method runs | `Debug` | `Starting ReportService.BuildDaily (Compiled invoker)` |
| After it returns | `Debug` | `Finished in 1,204 ms` |
| When it throws | `Error` | `Failed after 37 ms: SmtpException: Mailbox unavailable` (the exception is re-thrown) |

## More examples

### A console app

```csharp
using Masterly.NightShift;
using NightShiftLogLevel = Masterly.NightShift.Logging.LogLevel;

GlobalConfiguration.Configuration
    .UseColouredConsoleLogProvider(NightShiftLogLevel.Warn)
    .UseSqlServerStorage(@"Server=.\sqlexpress;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;");
```

On .NET 8 and later the order of the calls doesn't matter. On .NET Framework, set the log provider first, before the storage and anything else (see [Things to know](#things-to-know)).

### `Microsoft.Extensions.Logging` without the generic host

`AspNetCoreLogProvider` works with any `ILoggerFactory`, for example in a .NET 8 console app that uses `Microsoft.Extensions.Logging.Console` but no host:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.AspNetCore;
using Microsoft.Extensions.Logging;

ILoggerFactory loggerFactory = LoggerFactory.Create(logging => logging
    .AddConsole()
    .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));

GlobalConfiguration.Configuration
    .UseLogProvider(new AspNetCoreLogProvider(loggerFactory))
    .UseSqlServerStorage(@"Server=.\sqlexpress;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;");
```

### Turn logging off

For tests, or when the dashboard is enough:

```csharp
builder.Services.AddNightShift(config => config
    .UseNoOpLogProvider()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")));
```

In an ASP.NET Core app, raising the category level (`"Masterly.NightShift": "None"`) does the same and is easier to change later.

### Your own provider

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Logging;
using NightShiftLogLevel = Masterly.NightShift.Logging.LogLevel;

public sealed class TraceLogProvider : ILogProvider
{
    public ILog GetLogger(string name) => new TraceLog(name);

    private sealed class TraceLog : ILog
    {
        private readonly string _name;

        public TraceLog(string name)
        {
            _name = name;
        }

        public bool Log(NightShiftLogLevel logLevel, Func<string>? messageFunc, Exception? exception = null)
        {
            if (logLevel < NightShiftLogLevel.Info) return false;  // Trace and Debug are off
            if (messageFunc == null) return true;                  // "is this level enabled?"

            System.Diagnostics.Trace.WriteLine($"[{logLevel}] {_name}: {messageFunc()} {exception}");
            return true;
        }
    }
}
```

```csharp
GlobalConfiguration.Configuration.UseLogProvider(new TraceLogProvider());
```

### Log from inside a job

Job classes are created by the DI container (with `AddNightShift`), so they take an `ILogger<T>` like any other service. To also show progress on the dashboard, add an `IJobConsole` parameter and pass `null` when you create the job; Masterly.NightShift supplies the console when the job runs.

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;
using Microsoft.Extensions.Logging;

public class EmailService
{
    private readonly ILogger<EmailService> _logger;

    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
    }

    public async Task SendWelcome(int userId, IJobConsole? console)
    {
        _logger.LogInformation("Sending the welcome e-mail to user {UserId}", userId);
        console?.WriteLine($"Sending the welcome e-mail to user {userId}");

        await Task.Delay(10);
    }
}

public class SignupController
{
    private readonly IBackgroundJobClient _jobs;

    public SignupController(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public void Register(int userId)
    {
        _jobs.Enqueue<EmailService>(x => x.SendWelcome(userId, null));
    }
}
```

With `UseTelemetry()` and a listener on the `Masterly.NightShift` source, the job runs inside its `process` span, so log providers that record the current trace (for example OpenTelemetry logging) attach the job's trace and span ids to these entries. See [OpenTelemetry](opentelemetry.md).

### Job id and name on every entry

A server added with `AddNightShiftServer` runs each job inside an `ILogger.BeginScope`. The scope opens before the server filters run and closes after the worker has moved the job to its next state (Succeeded, Failed, Scheduled for a retry, ...). Every entry logged in between carries these values: the entries of the job's own `ILogger<T>`, after an `await` too, the server filters' entries, and what state filters log about the outcome, such as `AutomaticRetry`'s retry warning and final failure, or the warnings of `[Deadline]` and `[CircuitBreaker]`:

| Name | Value |
|---|---|
| `JobId` | the background job id |
| `JobName` | `{Type}.{Method}`, the same name as the `nightshift.job.name` telemetry tag |
| `RecurringJobId` | the recurring job that created the job, when there is one |
| `RetryCount` | the retry attempt, when the job is being retried |

Providers that support scopes show them: the console logger with `IncludeScopes`, the JSON console formatter, Serilog, NLog, OpenTelemetry logging. With the simple console formatter and `IncludeScopes = true`, an entry looks like this:

```
info: EmailService[0]
      => JobId:42 JobName:EmailService.SendWelcome
      Sending the welcome e-mail to user 7
```

The values come from the job the worker already read, never from the storage, and providers that don't print scopes never compute them. To turn the scope off:

```csharp
builder.Services.ConfigureNightShiftJobLogging(options => options.BeginScope = false);
```

The scope is added to servers created by `AddNightShiftServer`. It is not added to a server you create yourself (`new BackgroundJobServer(...)`, `app.UseNightShiftServer()`, or `AddNightShiftServer` with a factory that returns your own server), nor when you register your own `IBackgroundJobPerformer`, `IBackgroundJobFactory` and `IBackgroundJobStateChanger` in the container.

### Show start, end and errors on the dashboard

```csharp
public class ReportService
{
    [LogInvocation]
    public void BuildDaily()
    {
    }
}
```

Or for every job:

```csharp
config.UseJobInterceptor(new LogInvocationAttribute());
```

### Quieter retries

`AutomaticRetryAttribute` logs a warning with the full exception for every failed attempt. For jobs that are expected to fail now and then, turn its logging off with `LogEvents = false`, on one job:

```csharp
public class NewsletterService
{
    [AutomaticRetry(Attempts = 3, LogEvents = false)]
    public void SendIssue(int issueId) { }
}
```

or for all jobs, by replacing the global retry filter:

```csharp
builder.Services.AddNightShift(config =>
{
    // Replace the default retry filter with a quiet one
    GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>();

    config
        .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
        .UseFilter(new AutomaticRetryAttribute { Attempts = 5, LogEvents = false });
});
```

`LogEvents = false` also silences the Error entry for the final failure. Failed jobs are still visible on the dashboard, and you can still get [alerts](alerts-and-webhooks.md) for them.

## Things to know

- **On .NET Framework, set the provider before anything else.** The `netstandard2.0` build, which .NET Framework and .NET versions before 8 use, cannot look the provider up late for loggers created before the first `Use...LogProvider()` call: they keep the provider that was current when they were created. Most classes get their logger when they are constructed, some once per process (for example the default global filters, created the first time `GlobalJobFilters` is used). There, make `Use...LogProvider()` the first configuration call. Loggers created after it follow later changes. On .NET 8 and later the order doesn't matter.
- **Use `UseLogProvider`, not `LogProvider.SetCurrentLogProvider`.** Calling LibLog's `SetCurrentLogProvider` directly works as in LibLog: only loggers created after the call use the new provider. The `Use...LogProvider()` methods reach every logger (on .NET Framework, with the limit in the previous point).
- **Job exceptions are logged by `AutomaticRetryAttribute`**, not by the worker. If you remove the retry filter or set `LogEvents = false`, a failing job writes nothing to the log; the exception is still stored in the job's Failed state and shown on the dashboard.
- **`[LogInvocation]` is not application logging.** Its lines go to the job's console in storage (the list `job:{id}:console`, at most `JobConsole.DefaultMaxLines` = 500 lines per job) and expire with the job.
- **Masterly.NightShift's own messages are plain text.** The job id is part of the message text. Entries logged while a job runs and while its state changes afterwards, by Masterly.NightShift or by your code, also carry `JobId` as a scope value (see [Job id and name on every entry](#job-id-and-name-on-every-entry)).
- **`UseNoOpLogProvider()` silences everything**, including errors from background processes such as a lost database connection. Prefer raising the level to `Warning` or `Error`.

## See also

- [Monitoring overview](README.md)
- [OpenTelemetry](opentelemetry.md)
- [Alerts and webhooks](alerts-and-webhooks.md)
- [Behavior filters](../filters/behavior.md): `LogInvocation` alongside `Queue`, `JobDisplayName`, `CaptureCulture`, `Redacted`
- [Retries](../filters/retries.md): `AutomaticRetry`
- [Dependency injection](../server/dependency-injection.md): `AddNightShift` and job activation
- [Global configuration](../configuration/global-configuration.md)
