# Global configuration

`GlobalConfiguration.Configuration` is the single object that every `Use*` and `Set*` extension method hangs off. Use it to pick the storage, the job activator, the log provider, global filters, serialization settings, telemetry and dashboard extensions, once, when the application starts.

## Quick example

In ASP.NET Core or a Worker Service, configure through `AddNightShift`:

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")!));

builder.Services.AddNightShiftServer();

var app = builder.Build();
app.Run();
```

Without a DI container (console app, .NET Framework service), call the same methods on `GlobalConfiguration.Configuration` directly:

```csharp
using Masterly.NightShift;

GlobalConfiguration.Configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseColouredConsoleLogProvider()
    .UseSqlServerStorage("Server=.;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;");

using (var server = new BackgroundJobServer())
{
    Console.ReadLine();
}
```

## How it works

`IGlobalConfiguration` is an empty marker interface, and `GlobalConfiguration.Configuration` is its only built-in instance. Each extension method writes to process-wide state and returns the configuration, so calls chain:

- `UseStorage` sets `JobStorage.Current`, `UseActivator` sets `JobActivator.Current`, `UseLogProvider` sets the current log provider, `UseTimeZoneResolver` sets the global time zone resolver.
- `UseFilter` adds to `GlobalJobFilters.Filters`, `UseFilterProvider` to `JobFilterProviders.Providers`, `UseJobInterceptor` to the global interceptors.
- The serialization methods change the options used for job arguments and the functions that write and read type names.
- The dashboard methods register metrics, renderers and static files in the dashboard's route table.

Some methods return `IGlobalConfiguration<T>`, which also exposes the configured object as `Entry`. That is how `WithJobExpirationTimeout` can follow `UseStorage` or `UseSqlServerStorage` and change the storage just configured.

`AddNightShift(...)` (from `Masterly.NightShift.NetCore`) registers `IGlobalConfiguration` as a singleton. The first time it is resolved, which happens when the server, the client or the dashboard is first used, it:

1. sets an `ILoggerFactory`-based log provider (`AspNetCoreLogProvider`) and a scope-per-job activator (`AspNetCoreJobActivator`) when those services exist;
2. runs your callback, so anything you configure overrides those defaults;
3. registers `JobStorage.Current` and `JobActivator.Current` as the `JobStorage` and `JobActivator` services.

The callback runs once. The overload `AddNightShift((provider, config) => ...)` also gives you the `IServiceProvider`, so the configuration can use registered services.

Because the state is process-wide, configure once at startup, before you create jobs or start a server. Two `AddNightShift` calls in one process share the same `GlobalConfiguration`.

## Options / API

Every public extension method on `IGlobalConfiguration`, grouped by purpose. "Without it" says what happens when you never call the method.

### Storage

| Method | Parameters | Without it | Meaning |
|---|---|---|---|
| `UseStorage<TStorage>(storage)` | a `JobStorage` | `JobStorage.Current` throws `InvalidOperationException` when first used | Makes `storage` the storage every client, server and dashboard uses by default. Returns `IGlobalConfiguration<TStorage>`. |
| `WithJobExpirationTimeout(timeout)` | `TimeSpan`, zero or more | 1 day | Called on the result of `UseStorage`/`UseSqlServerStorage`. Sets `JobStorage.JobExpirationTimeout`: how long a job in a final state (Succeeded, Deleted) is kept before it expires. `[ExpireAfter]` overrides it per job. Failed jobs aren't affected: see `UseFailedJobRetention`. |
| `UseSqlServerStorage(connectionString)` | `string` | | `Masterly.NightShift.SqlServer` package. Creates a `SqlServerStorage`, calls `UseStorage`, and adds the SQL Server dashboard metrics (schema version, connections, transactions, file sizes) once per process. |
| `UseSqlServerStorage(connectionString, options)` | `string`, `SqlServerStorageOptions` | | Same, with [options](../storage/sql-server-options.md). |
| `UseSqlServerStorage(connectionFactory)` | `Func<DbConnection>` | | Same, with connections created by your factory. |
| `UseSqlServerStorage(connectionFactory, options)` | `Func<DbConnection>`, `SqlServerStorageOptions` | | Same, with options. |

### Job activation

| Method | Parameters | Without it | Meaning |
|---|---|---|---|
| `UseActivator<TActivator>(activator)` | a `JobActivator` | `JobActivator`, which calls the job type's public parameterless constructor; with `AddNightShift`, `AspNetCoreJobActivator` (one DI scope per job) | Sets `JobActivator.Current`, which creates the instance a job method runs on. |
| `UseDefaultActivator()` | | | Goes back to the plain `JobActivator`. |

### Logging

| Method | Parameters | Without it | Meaning |
|---|---|---|---|
| `UseLogProvider<TLogProvider>(provider)` | an `ILogProvider` | The first of Serilog, NLog, log4net, Enterprise Library, Loupe and Elmah found loaded; with `AddNightShift`, `AspNetCoreLogProvider` (`ILoggerFactory`) | Sends the library's log messages to `provider`, including those of loggers created earlier (on .NET 8 and later; see [Logging](../monitoring/logging.md)). |
| `UseNoOpLogProvider()` | | | Turns logging off. Meant for tests, not production. |
| `UseColouredConsoleLogProvider()` | | | Writes to the console in colour, from `Info` up. |
| `UseColouredConsoleLogProvider(minLevel)` | `Masterly.NightShift.Logging.LogLevel` | | Same, from `minLevel` up. |
| `UseSerilogLogProvider()` | | | Serilog. Like the other providers below, it is found by reflection, so no extra NightShift package is needed. |
| `UseNLogLogProvider()` | | | NLog. |
| `UseLog4NetLogProvider()` | | | log4net. |
| `UseEntLibLogProvider()` | | | Enterprise Library logging. |
| `UseLoupeLogProvider()` | | | Gibraltar Loupe. |
| `UseElmahLogProvider()` | | | Elmah, from `Error` up. |
| `UseElmahLogProvider(minLevel)` | `LogLevel` | | Elmah, from `minLevel` up. |

See [Logging](../monitoring/logging.md) for log levels and what is logged.

### Filters and job execution

| Method | Parameters | Without it | Meaning |
|---|---|---|---|
| `UseFilter<TFilter>(filter)` | any filter object | The default global filters (culture capture, automatic retry, statistics history, continuations, fan-in, job console, drain mode, activity feed (idle until `UseActivityFeed()`), batches) | Adds `filter` to `GlobalJobFilters.Filters`, so it applies to every job. An attribute filter that doesn't allow multiple instances replaces the default one of the same type. |
| `UseFilterProvider<TFilterProvider>(provider)` | an `IJobFilterProvider` | Global filters, attribute filters | Adds a provider that decides per job which filters apply. |
| `UseJobInterceptor(interceptor)` | an `IJobInterceptor` | None | Runs `interceptor` around every job method, before the interceptors declared with attributes. |
| `UseDefaultCulture(culture)` | `CultureInfo?` | The culture of the creating thread is captured and restored | Adds `CaptureCultureAttribute` with `culture` as the default, used when a job has no captured culture. |
| `UseDefaultCulture(culture, captureDefault)` | `CultureInfo?`, `bool` | | With `captureDefault: false`, the culture isn't stored with the job when it equals the default, which saves a job parameter. |
| `UseDefaultCulture(culture, uiCulture)` | `CultureInfo?`, `CultureInfo?` | | Separate defaults for `CurrentCulture` and `CurrentUICulture`. |
| `UseDefaultCulture(culture, uiCulture, captureDefault)` | `CultureInfo?`, `CultureInfo?`, `bool` | | All of the above. |
| `UseFailedJobRetention(retention)` | `TimeSpan`, positive, at most 36,500 days | Failed jobs are kept until someone retries or deletes them | Adds a global filter (once; calling again changes the period) that expires a job when it ends up **Failed**, after its retries are used up, in the same transaction. Requeuing or rescheduling it before then keeps it. `[ExpireAfter(..., IncludeFailed = true)]` on a method or class wins. Invalid periods throw `ArgumentOutOfRangeException`. See [Failed jobs](../filters/timing.md#failed-jobs). |
| `UseResultsInContinuations()` | | Results are not passed on | Adds the filters that let a continuation receive its parent's return value through `[FromResult]` and job parameters through `[FromParameter]`. See [Continuations](../jobs/continuations.md). |
| `UseCompiledJobInvokers(enabled)` | `bool` | `true` | Methods without a source-generated invoker are called through a delegate compiled once per method. `false` uses reflection for them. See [Source-generated invokers](../jobs/source-generated-invokers.md). |
| `UseTimeZoneResolver(resolver)` | `ITimeZoneResolver?` | `DefaultTimeZoneResolver`; with `AddNightShift`, a custom `ITimeZoneResolver` registered in the container | The resolver used where none is passed: the static `RecurringJob` class, `RecurringJobManager` and `RecurringJobScheduler` created without one, `RecurringJobManifest.Apply(json)`, a `BackgroundJobServer` whose `TimeZoneResolver` option is `null`, and a dashboard whose `TimeZoneResolver` option is `null`. `null` restores the default. With `AddNightShift`, register the resolver in the container instead (the server and injected managers use that one). See [Time zones and misfires](../recurring-jobs/time-zones-and-misfires.md#a-custom-time-zone-resolver). |

### Serialization and type names

| Method | Parameters | Without it | Meaning |
|---|---|---|---|
| `SetDataCompatibilityLevel(level)` | `CompatibilityLevel` | `Version_110` | Chooses the format of stored data. The default is the oldest format, kept so that a rolling upgrade is safe; a new application should use `Version_180`. Throws `InvalidEnumArgumentException` for an undefined value. See [Compatibility levels](compatibility-levels.md). |
| `UseSerializerSettings(settings)` | `JsonSerializerOptions?` | Lenient default options | Options for user data: job arguments, job parameters, return values. A copy is used. Data is written as they say and always read leniently (property names in any case, quoted numbers, public fields), so queued jobs keep working when you change them. `null` restores the defaults. See [Serialization](serialization.md). |
| `UseRecommendedSerializerSettings()` | | | Serializes user data with a copy of the library's internal options. Arguments declared as `object` keep their type in a `"$type"` property. |
| `UseRecommendedSerializerSettings(configure)` | `Action<JsonSerializerOptions>?` | | Same, then lets you change the copy (naming policy, converters). Converters you add win over the built-in ones. |
| `UseSimpleAssemblyNameTypeSerializer()` | | Assembly-qualified names with version, culture and public key | Writes type names as `Namespace.Type, AssemblyName`, so a job created by one build of your assembly can run on another build. |
| `UseIgnoredAssemblyVersionTypeResolver()` | | Exact names, falling back to the assembly's short name if that version can't be loaded | Strips `Version`, `Culture` and `PublicKeyToken` from stored names before resolving them. |
| `UseTypeSerializer(serializer)` | `Func<Type, string>?` | `TypeHelper.DefaultTypeSerializer` | Your own function for writing type names. `null` restores the default. |
| `UseTypeResolver(resolver)` | `Func<string, Type>?` | `TypeHelper.DefaultTypeResolver` | Your own function for reading type names. `null` restores the default. |
| `UseDefaultTypeSerializer()` | | | Same as `UseTypeSerializer(null)`. |
| `UseDefaultTypeResolver()` | | | Same as `UseTypeResolver(null)`. |

Type names are written for the job's type, the method's parameter types, states (`"$type"`), arguments declared as an interface or abstract class (`"$type"`) and, with the recommended settings, values declared as `object`. Whatever serializer you choose, the resolver must be able to read what it writes, and every server must use the same pair.

### Monitoring and alerts

| Method | Parameters | Without it | Meaning |
|---|---|---|---|
| `UseTelemetry()` | | No spans or metrics | net8.0 and later only. Emits OpenTelemetry spans and metrics from the `Masterly.NightShift` `ActivitySource` and `Meter`. Costs nothing while no listener is attached. See [OpenTelemetry](../monitoring/opentelemetry.md). |
| `UseJobInsights()` | | The Insights page stays empty | Records per-method run counts, failures and durations, and per-queue latency and throughput, for the dashboard's Insights page. See [Insights](../monitoring/insights.md). |
| `UseActivityFeed()` | | The home page's live activity and the Timeline stay empty | Records every state transition in the `activity` list, in the background after each state change commits. Call it in every process that changes job states. See [Audit log and activity feed](../monitoring/audit-log-and-activity.md). |
| `UseActivityFeed(maxEntries)` | `int`, positive | 1000 entries | Same, and sets how many entries the list keeps (`ActivityFeed.MaxEntries`). |
| `UseAlerts(sink)` | an `IAlertSink` | No alerts are delivered | Registers a sink and adds `AlertsFilter` to the global filters if it isn't there yet, so final failures raise alerts. |
| `UseWebhookAlerts(url, configure)` | `string`, `Action<WebhookAlertOptions>?` (optional) | | Posts alerts to `url` through a `WebhookAlertSink`. `configure` picks the events (`AlertKind`), the body format (`WebhookFormat.Json`, `Slack` or `Discord`), headers, timeout and concurrency. See [Alerts and webhooks](../monitoring/alerts-and-webhooks.md). |

### Dashboard

| Method | Parameters | Without it | Meaning |
|---|---|---|---|
| `UseDashboardMetric(metric)` | `DashboardMetric` | No metric blocks on the home page | Shows `metric` on the dashboard home page and registers it by name, so pages can request its live value. |
| `UseDashboardMetrics(metrics)` | `params DashboardMetric[]` | | Same, for several metrics. `DashboardMetrics` has the built-in ones, such as `ServerCount` and `RecurringJobCount`. |
| `UseJobDetailsRenderer(order, renderer)` | `int`, `Func<JobDetailsRendererDto, NonEscapedString>` | | Adds a block of HTML to the job details page. Blocks are shown by `order`. |
| `UseDashboardStylesheet(assembly, resource)` | `Assembly`, `string` | | Serves an embedded CSS resource with the dashboard's own stylesheets. |
| `UseDashboardStylesheetDarkMode(assembly, resource)` | `Assembly`, `string` | | Same, used only in dark mode. |
| `UseDashboardJavaScript(assembly, resource)` | `Assembly`, `string` | | Serves an embedded script with the dashboard's own scripts. |
| `UseMaxArgumentSizeToRender(size)` | `int`, zero or more | 4096 | An argument whose stored JSON is longer than `size` characters is shown as `<VALUE IS TOO BIG>` in the dashboard's method call view. |
| `UseMaxLinesInExceptionDetails(numberOfLines)` | `int?`, more than 1, or `null` | 100 | How many lines of an exception's details a Failed state keeps. `null` keeps them all. |

See [Dashboard customization](../dashboard/customization.md) for examples of each.

### For extension authors

| Method | Parameters | Meaning |
|---|---|---|
| `Use<T>(entry, entryAction)` | `T`, `Action<T>` | Runs `entryAction(entry)` and returns an `IGlobalConfiguration<T>` whose `Entry` is `entry`. Hidden from IntelliSense; it's the building block the other methods use. |

## More examples

### Storage with a longer retention

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

var storage = new SqlServerStorage("Server=.;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;");

GlobalConfiguration.Configuration
    .UseStorage(storage)
    .WithJobExpirationTimeout(TimeSpan.FromDays(7))
    .UseFailedJobRetention(TimeSpan.FromDays(30));
```

Succeeded and deleted jobs are kept for a week, failed jobs (retries used up) for 30 days.

### Using registered services in the configuration

```csharp
services.AddNightShift((provider, config) => config
    .UseSqlServerStorage(
        "Server=.;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;",
        provider.GetRequiredService<SqlServerStorageOptions>()));
```

### A custom job activator

Outside ASP.NET Core, give the server your own container through a `JobActivator` subclass:

```csharp
using Masterly.NightShift;

GlobalConfiguration.Configuration.UseActivator(new TenantJobActivator(services));

public class TenantJobActivator : JobActivator
{
    private readonly IServiceProvider _services;

    public TenantJobActivator(IServiceProvider services)
    {
        _services = services;
    }

    public override object ActivateJob(Type jobType)
    {
        return _services.GetService(jobType) ?? base.ActivateJob(jobType);
    }
}
```

With `AddNightShift` you don't need this: jobs are resolved from the container, one scope per job. See [Dependency injection](../server/dependency-injection.md).

### Choosing a log provider

```csharp
using Masterly.NightShift;
using NightShiftLogLevel = Masterly.NightShift.Logging.LogLevel;

GlobalConfiguration.Configuration.UseColouredConsoleLogProvider(NightShiftLogLevel.Info);
```

The alias avoids a clash with `Microsoft.Extensions.Logging.LogLevel`, which ASP.NET Core projects import implicitly.

### Global filters, a filter provider and an interceptor

```csharp
using System.Globalization;
using Masterly.NightShift;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

GlobalConfiguration.Configuration
    .UseFilter(new AutomaticRetryAttribute { Attempts = 3 })
    .UseFilterProvider(new BillingRetryFilterProvider())
    .UseJobInterceptor(new AuditInterceptor())
    .UseDefaultCulture(CultureInfo.GetCultureInfo("en-US"))
    .UseResultsInContinuations();

public class AuditInterceptor : IJobInterceptor
{
    public object? Intercept(JobInvocation invocation)
    {
        Console.WriteLine($"Running {invocation.Job.Method.Name}");
        return invocation.Proceed();
    }
}

// More retries for jobs from the Billing namespace only
public class BillingRetryFilterProvider : IJobFilterProvider
{
    public IEnumerable<JobFilter> GetFilters(Job job)
    {
        if (job.Type.Namespace?.StartsWith("Billing", StringComparison.Ordinal) == true)
        {
            yield return new JobFilter(new AutomaticRetryAttribute { Attempts = 20 }, JobFilterScope.Global, null);
        }
    }
}
```

`UseFilter(new AutomaticRetryAttribute { Attempts = 3 })` replaces the default retry filter, because only one `AutomaticRetryAttribute` applies to a job and, among global filters, the last one registered wins. An attribute on the job's class or method still wins over both. See [Custom filters](../filters/custom-filters.md).

### Serialization settings for a new application

```csharp
using System.Text.Json;
using Masterly.NightShift;

GlobalConfiguration.Configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseIgnoredAssemblyVersionTypeResolver()
    .UseRecommendedSerializerSettings(options =>
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
```

### Telemetry, insights and alerts

```csharp
using Masterly.NightShift;

GlobalConfiguration.Configuration
    .UseTelemetry()
    .UseJobInsights()
    .UseWebhookAlerts("https://hooks.slack.com/services/T000/B000/XXXX", options =>
    {
        options.Format = WebhookFormat.Slack;
        options.Events = AlertKind.JobFailed | AlertKind.DeadlineMissed;
    });
```

### Dashboard extensions

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Dashboard;

var assembly = typeof(Program).Assembly;

GlobalConfiguration.Configuration
    .UseDashboardMetrics(DashboardMetrics.ServerCount, DashboardMetrics.RecurringJobCount)
    .UseJobDetailsRenderer(10, dto => new NonEscapedString($"<p>Job {dto.JobId}</p>"))
    .UseDashboardStylesheet(assembly, "MyApp.Dashboard.custom.css")
    .UseDashboardStylesheetDarkMode(assembly, "MyApp.Dashboard.custom-dark.css")
    .UseDashboardJavaScript(assembly, "MyApp.Dashboard.custom.js")
    .UseMaxArgumentSizeToRender(16 * 1024)
    .UseMaxLinesInExceptionDetails(30);
```

The resource names are the manifest names of files marked as `EmbeddedResource` in your project.

### Back to reflection for job methods

```csharp
GlobalConfiguration.Configuration.UseCompiledJobInvokers(false);
```

### Your own configuration extension

Bundle the settings your services share into one method:

```csharp
using Masterly.NightShift;

services.AddNightShift(config => config
    .UseMyAppDefaults()
    .UseSqlServerStorage("Server=.;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;"));

public static class MyAppNightShiftExtensions
{
    public static IGlobalConfiguration UseMyAppDefaults(this IGlobalConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseFilter(new AutomaticRetryAttribute { Attempts = 5 })
            .UseJobInsights();
    }
}
```

## Things to know

- **Settings are global.** They apply to the whole process, not to one `BackgroundJobServer` or one `IServiceCollection`. Tests that change them should restore them, or run one at a time.
- **Configure before use.** Type serializers, serializer options and the compatibility level affect data as it is written. Change them before the first job is created, not while jobs are being created.
- **Every server must agree.** Clients and servers that share a storage must use the same type serializer/resolver pair and serializer options, and a compatibility level that every server can read. See [Compatibility levels](compatibility-levels.md).
- **`AddNightShift` runs your callback lazily**, the first time `IGlobalConfiguration` is resolved. Code that uses the static `BackgroundJob` facade before the host starts may find `JobStorage.Current` not set yet.
- **Calling a method twice** usually replaces the setting (storage, activator, log provider, serializer options) but adds a second entry for list-like settings (`UseFilter`, `UseJobInterceptor`, `UseAlerts`, dashboard metrics and files).

## See also

- [Serialization](serialization.md)
- [Compatibility levels](compatibility-levels.md)
- [Dependency injection](../server/dependency-injection.md)
- [Custom filters](../filters/custom-filters.md)
- [Logging](../monitoring/logging.md)
- [Dashboard customization](../dashboard/customization.md)
- [Source: GlobalConfigurationExtensions.cs](../../src/Masterly.NightShift.Core/Configuration/GlobalConfigurationExtensions.cs)
