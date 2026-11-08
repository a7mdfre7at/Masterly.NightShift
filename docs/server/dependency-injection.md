# Dependency injection

`Masterly.NightShift.NetCore` connects the library to `Microsoft.Extensions.DependencyInjection`: `AddNightShift` registers the job client, the recurring job manager and the configuration, `AddNightShiftServer` runs a server as a hosted service, and jobs are created from the container with a new scope for every run. Use it in ASP.NET Core, Worker Service and any other generic-host app; without a container, create the server yourself and, if needed, plug in your own `JobActivator`.

## Quick example

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IOrderRepository, OrderRepository>();

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Jobs")));
builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapPost("/orders/{orderId:int}/receipt", (int orderId, IBackgroundJobClient jobs) =>
{
    var jobId = jobs.Enqueue<ReceiptJob>(x => x.SendAsync(orderId, CancellationToken.None));
    return Results.Accepted(value: new { jobId });
});

app.Run();
```

```csharp
// Not registered in DI: the activator creates it and resolves its constructor from the job's scope
public class ReceiptJob
{
    private readonly IOrderRepository _orders;
    private readonly ILogger<ReceiptJob> _logger;

    public ReceiptJob(IOrderRepository orders, ILogger<ReceiptJob> logger)
    {
        _orders = orders;
        _logger = logger;
    }

    public async Task SendAsync(int orderId, CancellationToken cancellationToken)
    {
        var unpaid = await _orders.GetUnpaidOrderIdsAsync(cancellationToken);
        _logger.LogInformation("Sending receipt for order {OrderId}", orderId);
    }
}
```

## How it works

Only the method call is stored with the job: the type, the method and the arguments. The `ReceiptJob` instance and its dependencies are not stored. When a worker runs the job, it asks the **job activator** for an instance:

1. `AddNightShift` installs `AspNetCoreJobActivator` (namespace `Masterly.NightShift.AspNetCore`, built on `IServiceScopeFactory`) as the global activator, and routes the library's logging to `ILoggerFactory`.
2. For every job run, the activator creates a new `IServiceScope`.
3. The job type is resolved from that scope with `ActivatorUtilities.GetServiceOrCreateInstance`: if the type is registered, the registration is used; if not, the type is created and its constructor parameters are resolved from the scope. Job classes do not need to be registered.
4. The method is called. Scoped services (a `DbContext`, a unit of work) are created once for this job run and shared by everything resolved in it.
5. When the method returns or throws, the scope is disposed, and with it every scoped and transient disposable service it created. Services that implement only `IAsyncDisposable` are disposed too, on every target framework, whenever the container's scopes support asynchronous disposal, as they do with Microsoft.Extensions.DependencyInjection 3.0 and later (every generic host). An older container, such as the one an ASP.NET Core 2.x `WebHost` on .NET Framework brings, doesn't know `IAsyncDisposable` and leaves such services undisposed.

Static job methods need no instance, so nothing is resolved for them.

The configuration callback you pass to `AddNightShift` configures the process-wide `GlobalConfiguration`. It runs once, the first time one of the registered services is resolved, which normally happens when the host starts the server. After that, the static facade (`BackgroundJob`, `RecurringJob`) and `JobStorage.Current` use the same storage as the injected services.

## API

### AddNightShift

| Overload | Meaning |
| --- | --- |
| `AddNightShift(Action<IGlobalConfiguration> configuration)` | Registers the services below and configures the library with `configuration`. |
| `AddNightShift(Action<IServiceProvider, IGlobalConfiguration> configuration)` | The same, with access to the container, for example to read options or create filters from services. |

It registers these singletons. All except `IGlobalConfiguration` use `TryAdd`, so a registration you make before calling `AddNightShift` takes precedence.

| Service | Implementation |
| --- | --- |
| `IGlobalConfiguration` | `GlobalConfiguration.Configuration`, after the configuration callback has run. It also marks that `AddNightShift` was called. |
| `JobStorage` | `JobStorage.Current`, the storage set by the callback (for example `UseSqlServerStorage`). |
| `JobActivator` | `JobActivator.Current`: the `AspNetCoreJobActivator`, or the activator set with `UseActivator` in the callback. |
| `IJobFilterProvider` | `JobFilterProviders.Providers`: global filters and filter attributes. |
| `ITimeZoneResolver` | `DefaultTimeZoneResolver`. |
| `IBackgroundJobClient`, `IBackgroundJobClientV2` | A `BackgroundJobClient` for the registered `JobStorage`. |
| `IRecurringJobManager`, `IRecurringJobManagerV2`, `IRecurringJobManagerV3` | A `RecurringJobManager` for the registered `JobStorage`. Inject `IRecurringJobManagerV3` to call `Pause`, `Resume` and `UpdateSchedule` directly. |
| `IBackgroundJobClientFactory`, `IBackgroundJobClientFactoryV2` | Create a client for any `JobStorage` (`GetClient`, `GetClientV2`). |
| `IRecurringJobManagerFactory`, `IRecurringJobManagerFactoryV2`, `IRecurringJobManagerFactoryV3` | Create a recurring job manager for any `JobStorage` (`GetManager`, `GetManagerV2`, `GetManagerV3`). All three are the same factory object. |
| `RouteCollection` | `DashboardRoutes.Routes`, used by the dashboard. |

### AddNightShiftServer

Each call adds one more server to the application, as an `IHostedService`. All overloads except the last require `AddNightShift`; without it the host fails at start with `InvalidOperationException` ("Unable to find the required services...").

| Overload | Options | Storage | Additional processes |
| --- | --- | --- | --- |
| `AddNightShiftServer()` | The registered `BackgroundJobServerOptions`, or defaults | Registered `JobStorage` | All registered `IBackgroundProcess` services |
| `AddNightShiftServer(Action<BackgroundJobServerOptions> optionsAction)` | New options passed to `optionsAction` | Registered `JobStorage` | All registered `IBackgroundProcess` services |
| `AddNightShiftServer(Action<IServiceProvider, BackgroundJobServerOptions> optionsAction)` | The same, with access to the container | Registered `JobStorage` | All registered `IBackgroundProcess` services |
| `AddNightShiftServer(IConfiguration configuration)` | New options read from the configuration section; see [Read the options from configuration](server-options.md#read-the-options-from-configuration) | Registered `JobStorage` | All registered `IBackgroundProcess` services |
| `AddNightShiftServer(IConfiguration configuration, Action<IServiceProvider, BackgroundJobServerOptions> optionsAction)` | Read from the section, then passed to `optionsAction` | Registered `JobStorage` | All registered `IBackgroundProcess` services |
| `AddNightShiftServer(Action<IServiceProvider, BackgroundJobServerOptions> optionsAction, JobStorage storage)` | As above | `storage` | All registered `IBackgroundProcess` services |
| `AddNightShiftServer(Action<IServiceProvider, BackgroundJobServerOptions> optionsAction, JobStorage storage, IEnumerable<IBackgroundProcess> additionalProcesses)` | As above | `storage` | Only `additionalProcesses` |
| `AddNightShiftServer(JobStorage storage)` | The registered `BackgroundJobServerOptions`, or defaults | `storage` | All registered `IBackgroundProcess` services |
| `AddNightShiftServer(JobStorage storage, IEnumerable<IBackgroundProcess> additionalProcesses)` | The registered `BackgroundJobServerOptions`, or defaults | `storage` | Only `additionalProcesses` |
| `AddNightShiftServer(Func<IServiceProvider, IBackgroundProcessingServer> implementationFactory)` | Yours | Yours | Yours: the factory creates the whole server |

For every overload but the last, options you leave unset are filled from the container: `Activator` from the `JobActivator` service, `FilterProvider` from `IJobFilterProvider`, `TimeZoneResolver` from `ITimeZoneResolver`. When `IBackgroundJobFactory`, `IBackgroundJobPerformer` and `IBackgroundJobStateChanger` are all registered, the server and the injected clients use them instead of the built-in ones.

The server's lifetime follows the host. When the host registers `IHostApplicationLifetime`, as every generic host does (`Host.CreateDefaultBuilder`, `WebApplication`, Worker Services) on every target framework, the server is created when the application has fully started (`ApplicationStarted`) and gets its stop signal as soon as the application starts stopping (`ApplicationStopping`). Without it, as with the ASP.NET Core 2.x `WebHost` on .NET Framework, the server is created in `StartAsync`. In `StopAsync` the hosted service sends the stop signal and waits for the server to stop, at most until the host's shutdown timeout. See [Graceful shutdown](queues-and-workers.md#graceful-shutdown).

### JobActivator

| Member | Meaning |
| --- | --- |
| `static JobActivator Current { get; set; }` | The activator used when a server has no `Activator` option. Setting `null` throws `ArgumentNullException`. `UseActivator` sets it. |
| `virtual object ActivateJob(Type jobType)` | Creates an instance. The default calls `Activator.CreateInstance(jobType)`, which needs a public parameterless constructor. |
| `virtual JobActivatorScope BeginScope(JobActivatorContext context)` | Starts the scope for one job run. The default returns a scope that calls `ActivateJob` and, when the scope ends, disposes the instances that implement `IDisposable`. |
| `virtual JobActivatorScope BeginScope(PerformContext context)` | Builds a `JobActivatorContext` and calls the overload above. |

| `JobActivatorScope` member | Meaning |
| --- | --- |
| `abstract object Resolve(Type type)` | Returns the instance to call the job method on. |
| `virtual void DisposeScope()` | Called when the job run ends. Release what the scope created. |
| `static JobActivatorScope Current` | The scope most recently started on the current thread and not yet disposed, or `null`. |

| `JobActivatorContext` member | Meaning |
| --- | --- |
| `BackgroundJob` | The job: ID, `Job` (type, method, arguments), creation time. |
| `CancellationToken` | The job's `IJobCancellationToken`. |
| `Connection` | The storage connection the worker uses. |
| `GetJobParameter<T>(string name)` | Reads a job parameter, for example one set by a client filter. |
| `SetJobParameter(string name, object value)` | Writes a job parameter. |

Set the global activator with `GlobalConfiguration.Configuration.UseActivator(activator)` (or in the `AddNightShift` callback), or one server's activator with `BackgroundJobServerOptions.Activator`. `UseDefaultActivator()` goes back to the built-in one.

## More examples

### Read services in the configuration callback

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

services.AddSingleton(new SqlServerStorageOptions
{
    SchemaName = "jobs"
});

services.AddNightShift((provider, config) => config
    .UseSqlServerStorage(
        provider.GetRequiredService<IConfiguration>().GetConnectionString("Jobs"),
        provider.GetRequiredService<SqlServerStorageOptions>()));
```

### Inject the job client and the recurring job manager

Both are singletons and safe to inject anywhere: controllers, minimal API handlers, hosted services, other singletons.

```csharp
public class OrdersController
{
    private readonly IBackgroundJobClient _jobs;

    public OrdersController(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public string? Confirm(int orderId)
    {
        return _jobs.Enqueue<EmailService>(x => x.SendReceipt(orderId));
    }
}
```

Register recurring jobs at start-up from a hosted service:

```csharp
public sealed class RecurringJobsRegistration : IHostedService
{
    private readonly IRecurringJobManager _recurringJobs;

    public RecurringJobsRegistration(IRecurringJobManager recurringJobs)
    {
        _recurringJobs = recurringJobs;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

```csharp
services.AddHostedService<RecurringJobsRegistration>();
```

To pause, resume or reschedule recurring jobs, inject `IRecurringJobManagerV3`:

```csharp
public sealed class ReportMaintenance
{
    private readonly IRecurringJobManagerV3 _recurringJobs;

    public ReportMaintenance(IRecurringJobManagerV3 recurringJobs)
    {
        _recurringJobs = recurringJobs;
    }

    public void Begin() => _recurringJobs.Pause("daily-report");

    public void End() => _recurringJobs.Resume("daily-report");
}
```

The static facade does the same without injection: `BackgroundJob.Enqueue<EmailService>(x => x.SendReceipt(orderId))`, `RecurringJob.AddOrUpdate(...)`. It uses `JobStorage.Current`, which is set once the configuration callback has run.

### Register the server options in the container

```csharp
services.AddSingleton(new BackgroundJobServerOptions
{
    WorkerCount = 8,
    Queues = new[] { "critical", "default" }
});

// No options callback: the registered BackgroundJobServerOptions is used
services.AddNightShiftServer();
```

To keep the options in `appsettings.json` instead, pass the section: `services.AddNightShiftServer(configuration.GetSection("NightShift:Server"))`. See [Read the options from configuration](server-options.md#read-the-options-from-configuration).

### A server for a second storage, and a client for it

`AddNightShiftServer(storage)` runs a server on another storage; the factories create clients for it:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

services.AddNightShift(config => config
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;"));

// The default server, on the default storage (JobStorage.Current)
services.AddNightShiftServer();

// A second server that processes the archive database
var archive = new SqlServerStorage("Server=.;Database=JobsArchive;Integrated Security=True;");
services.AddSingleton(new ArchiveStorage(archive));
services.AddNightShiftServer(archive);
```

```csharp
public sealed class ArchiveStorage
{
    public ArchiveStorage(JobStorage storage)
    {
        Storage = storage;
    }

    public JobStorage Storage { get; }
}

public class ArchiveScheduler
{
    private readonly IBackgroundJobClient _archiveJobs;

    public ArchiveScheduler(IBackgroundJobClientFactory clientFactory, ArchiveStorage archive)
    {
        _archiveJobs = clientFactory.GetClient(archive.Storage);
    }

    public string? QueueRebuild(int year, int month)
    {
        return _archiveJobs.Enqueue<ReportService>(x => x.BuildMonthly(year, month));
    }
}
```

The injected `IBackgroundJobClient` always uses the default storage. `IRecurringJobManagerFactory.GetManager(storage)` does the same for recurring jobs.

### Add your own background process

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;
using Masterly.NightShift.SqlServer;

services.AddNightShiftServer(
    (provider, options) => options.WorkerCount = 4,
    new SqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;"),
    new IBackgroundProcess[] { new CacheWarmupProcess() });

public sealed class CacheWarmupProcess : IBackgroundProcess
{
    public void Execute(BackgroundProcessContext context)
    {
        // Warm a cache, then wait; the server calls Execute again in a loop
        context.Wait(TimeSpan.FromMinutes(10));
    }
}
```

Or register it with `services.AddSingleton<IBackgroundProcess, CacheWarmupProcess>()` and use an overload without a process list: every such server runs all registered `IBackgroundProcess` services. The [keep-alive process](keep-alive.md) is registered this way.

### Create the server yourself

The factory overload hands the whole server to the host, which stops and disposes it:

```csharp
services.AddNightShiftServer(provider => new BackgroundJobServer(
    new BackgroundJobServerOptions { Queues = new[] { "reports" } },
    provider.GetRequiredService<JobStorage>()));
```

A server created this way does not get its options filled from the container. It uses `JobActivator.Current` (which `AddNightShift` sets to the container-backed activator once the configuration has run) and the global filters, unless you set `Activator` and `FilterProvider` in its options.

### A custom activator: a tenant per job

A client filter stores the tenant as a job parameter when the job is created; the activator reads it and sets up the job's scope before the job class is resolved:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Client;

public sealed class TenantContext
{
    public string? TenantId { get; set; }
}

public sealed class TenantClientFilter : IClientFilter
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantClientFilter(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void OnCreating(CreatingContext context)
    {
        var tenantId = _httpContextAccessor.HttpContext?.User.FindFirst("tenant_id")?.Value;
        if (tenantId != null)
        {
            context.SetJobParameter("TenantId", tenantId);
        }
    }

    public void OnCreated(CreatedContext context)
    {
    }
}

public sealed class TenantJobActivator : JobActivator
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TenantJobActivator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public override JobActivatorScope BeginScope(JobActivatorContext context)
    {
        var scope = _scopeFactory.CreateScope();

        // The client stored the tenant as a job parameter when the job was created
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.TenantId = context.GetJobParameter<string>("TenantId");

        return new TenantJobActivatorScope(scope);
    }

    private sealed class TenantJobActivatorScope : JobActivatorScope
    {
        private readonly IServiceScope _scope;

        public TenantJobActivatorScope(IServiceScope scope)
        {
            _scope = scope;
        }

        public override object Resolve(Type type)
        {
            return ActivatorUtilities.GetServiceOrCreateInstance(_scope.ServiceProvider, type);
        }

        public override void DisposeScope()
        {
            _scope.Dispose();
        }
    }
}
```

Register both in the configuration callback. `UseActivator` replaces the default `AspNetCoreJobActivator` for every server:

```csharp
services.AddHttpContextAccessor();
services.AddScoped<TenantContext>();

services.AddNightShift((provider, config) => config
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;")
    .UseFilter(new TenantClientFilter(provider.GetRequiredService<IHttpContextAccessor>()))
    .UseActivator(new TenantJobActivator(provider.GetRequiredService<IServiceScopeFactory>())));

services.AddNightShiftServer();
```

To use it for one server only, set the option instead:

```csharp
services.AddNightShiftServer((provider, options) =>
{
    options.Activator = new TenantJobActivator(provider.GetRequiredService<IServiceScopeFactory>());
});
```

### Without a container

In a console app, a Windows Service or an ASP.NET application on .NET Framework, configure the library once at start-up and create the server yourself. `new BackgroundJobServer()` uses `JobStorage.Current` and `JobActivator.Current`. The default activator needs a public parameterless constructor; for anything else, override `ActivateJob`:

```csharp
using System;
using Masterly.NightShift;

public class InvoiceService
{
    private readonly string _connectionString;

    public InvoiceService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void SendOverdueReminders() { }
}

public class InvoiceJobActivator : JobActivator
{
    private readonly string _connectionString;

    public InvoiceJobActivator(string connectionString)
    {
        _connectionString = connectionString;
    }

    public override object ActivateJob(Type jobType)
    {
        if (jobType == typeof(InvoiceService))
        {
            return new InvoiceService(_connectionString);
        }

        return base.ActivateJob(jobType);
    }
}

public static class Program
{
    public static void Main()
    {
        const string connectionString = "Server=.;Database=Jobs;Integrated Security=True;";

        GlobalConfiguration.Configuration
            .UseSqlServerStorage(connectionString)
            .UseActivator(new InvoiceJobActivator(connectionString));

        BackgroundJob.Enqueue<InvoiceService>(x => x.SendOverdueReminders());

        using (var server = new BackgroundJobServer())
        {
            Console.WriteLine("Server started. Press Enter to exit.");
            Console.ReadLine();
        }
    }
}
```

When the activator only overrides `ActivateJob`, instances that implement `IDisposable` are disposed after each job run. With a third-party container, override `BeginScope(JobActivatorContext)` and return a `JobActivatorScope` that wraps the container's own scope, as in the tenant example. See [Quick start: worker](../getting-started/quick-start-worker.md) for a complete console setup.

## Things to know

- **Job class lifetimes are respected.** A job class registered as a singleton is shared by all job runs, possibly on several workers at once, so it must be thread-safe and must not hold scoped services. Leave job classes unregistered, or register them as scoped or transient.
- **Call `AddNightShift` once.** It configures the process-wide `GlobalConfiguration`, which every server, client and dashboard in the process shares. A second call doesn't throw, but only the configuration callback of the last call runs; the first one is ignored.
- **The injected managers are separate instances.** `IRecurringJobManager`, `IRecurringJobManagerV2` and `IRecurringJobManagerV3` are each a singleton of their own, all for the same storage, so any of them sees the same recurring jobs. With `IRecurringJobManager` you can also use the `Pause`, `Resume` and `UpdateSchedule` extension methods; see [Pausing and updating](../recurring-jobs/pausing-and-updating.md).
- **Explicit process lists replace registered processes.** The `AddNightShiftServer` overloads with an `IEnumerable<IBackgroundProcess>` parameter ignore the `IBackgroundProcess` services in the container.
- **`app.UseNightShiftServer(...)` is obsolete.** Use `services.AddNightShiftServer(...)`. The `UseNightShiftServer(Func<IBackgroundProcessingServer>)` and `IServiceProvider.RegisterNightShiftServer(server)` helpers in `Masterly.NightShift.AspNetCore` still exist for servers created outside the container; they stop and dispose the server with the application.
- **Arguments are not resolved from the container.** Only the instance the method is called on comes from the activator. Method arguments are the values stored with the job; see [Job arguments](../jobs/job-arguments.md).

## See also

- [Server options](server-options.md)
- [Queues and workers](queues-and-workers.md)
- [Keep-alive](keep-alive.md)
- [Quick start: ASP.NET Core](../getting-started/quick-start-aspnetcore.md)
- [Quick start: worker](../getting-started/quick-start-worker.md)
- [Global configuration](../configuration/global-configuration.md)
- [Custom filters](../filters/custom-filters.md)
- [Job arguments](../jobs/job-arguments.md)
