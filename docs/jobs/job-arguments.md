# Job arguments

A job is stored as data: the method to call and the argument values, serialized to JSON. This page explains what that means for the code you write: which arguments work, how the object whose method is called is created, which parameters the server fills in for you, and what happens to the value your method returns.

## Quick example

Pass small values, usually IDs, and let the job load what it needs. Take your dependencies in the constructor:

```csharp
using Masterly.NightShift;
using Microsoft.Extensions.Logging;

public class InvoiceJobs
{
    private readonly IOrderRepository _orders;
    private readonly IPdfRenderer _pdf;
    private readonly ILogger<InvoiceJobs> _logger;

    public InvoiceJobs(IOrderRepository orders, IPdfRenderer pdf, ILogger<InvoiceJobs> logger)
    {
        _orders = orders;
        _pdf = pdf;
        _logger = logger;
    }

    public async Task<int> RenderAsync(int orderId, CancellationToken cancellationToken)
    {
        Order order = await _orders.GetAsync(orderId, cancellationToken);
        byte[] pdf = await _pdf.RenderAsync(order, cancellationToken);
        _logger.LogInformation("Rendered invoice for order {OrderId}", orderId);
        return pdf.Length;
    }
}

// Program.cs
builder.Services.AddScoped<IOrderRepository, SqlOrderRepository>();
builder.Services.AddScoped<IPdfRenderer, PdfRenderer>();

// anywhere you have an IBackgroundJobClient
jobs.Enqueue<InvoiceJobs>(x => x.RenderAsync(orderId, CancellationToken.None));
```

Only `orderId` is stored. `CancellationToken.None` is a placeholder; the server passes a real token when the job runs.

## How it works

### The call is captured, not executed

`Enqueue`, `Schedule`, `ContinueJobWith` and the other creation methods take an expression tree, not a delegate. Masterly.NightShift reads three things from it and then discards it:

- the **type** that declares the method (`InvoiceJobs`, or `T` in `Enqueue<T>`),
- the **method** (`RenderAsync(int, CancellationToken)`),
- the **value of each argument**.

The method itself is not called on the client. But each argument expression is evaluated right away: in `x => x.RenderAsync(order.Id, CancellationToken.None)`, `order.Id` is read when you call `Enqueue`, and a call such as `GetCurrentUserId()` inside the argument list runs on the client at that moment. Only the resulting values are stored.

The lambda body must be a single method call. Blocks, `await`, property access as the body, and `async` lambdas are rejected (`ArgumentException`) or don't compile.

### Arguments are serialized to JSON

Each argument value is serialized with System.Text.Json, using your serializer options (`UseSerializerSettings` / `UseRecommendedSerializerSettings`, see [Serialization](../configuration/serialization.md)), with every member of its actual type. The type name, the method name, the parameter types and the JSON values are stored with the job. When the job runs, every value is deserialized back **as the parameter's declared type**, in whatever process picks the job up.

Consequences:

- An argument is a snapshot. Changing the object after `Enqueue` changes nothing in the job.
- A parameter declared as an interface or abstract class keeps the argument's actual type: it is stored in a `"$type"` property, and the job receives an instance of that type. This applies to the argument itself, not to its properties or to the items of a collection; declare concrete types there.
- A parameter declared as a concrete base class is read back as that class, so properties of a derived class are lost. Declare the type the job needs.
- A parameter declared as `object` keeps simple values (numbers, strings, booleans). With `UseRecommendedSerializerSettings()` other values also keep their type, through a `"$type"` property. Values typed in the dashboard's job forms are the exception: they are read as plain JSON, see [Job catalog](job-catalog.md#how-text-becomes-argument-values).
- Overloads are told apart by their parameter types. A job created for `Handle(OrderPlaced)` runs `Handle(OrderPlaced)`, even when `Handle(EventBase)` or `Handle(IEvent)` is declared before it. Only when no overload has exactly the stored parameter types (the method changed after the job was stored) does the server take the first one whose parameters accept them.
- The server must be able to load the type and find the method. Renaming a job class, method or parameter type breaks jobs already stored under the old name. Add a new method and keep the old one until the old jobs have run.

### The target instance is created on the server

| Lambda | What the server calls |
|---|---|
| `() => CacheMaintenance.Purge("products")` (static method) | The static method; no instance |
| `x => x.RenderAsync(...)` in `Enqueue<InvoiceJobs>` | A new `InvoiceJobs` created by the job activator |
| `x => x.SendAsync(...)` in `Enqueue<IEmailSender>` | Whatever the job activator returns for `IEmailSender` |
| `() => invoiceJobs.RenderAsync(...)` (a captured variable) | A new instance of the variable's runtime type, created by the job activator. The captured object itself is not stored |

The job activator (`JobActivator`) creates the instance:

- With `services.AddNightShift(...)` (Masterly.NightShift.NetCore), the activator is `AspNetCoreJobActivator`. It creates a DI scope for each job, resolves the type from it (`ActivatorUtilities.GetServiceOrCreateInstance`: the registered service, or a new instance with its constructor dependencies injected if the type itself is not registered), and disposes the scope when the job ends. Scoped services such as a `DbContext` are therefore fresh for every job. An interface must be registered, or resolution fails.
- Without DI, `JobActivator.Current` is the default `JobActivator`, which calls `Activator.CreateInstance` (the class needs a public parameterless constructor) and disposes the instance after the job if it is `IDisposable`.

See [Dependency injection](../server/dependency-injection.md) for custom activators.

### Some parameters are filled in by the server

Parameters of these types are not serialized. Pass a placeholder when you create the job; the server substitutes the real value when the job runs.

| Parameter type | Placeholder to pass | What the job receives |
|---|---|---|
| `CancellationToken` | `CancellationToken.None` | A token canceled on server shutdown or when the job is deleted or moved out of `Processing`. See [Cancellation](cancellation.md) |
| `IJobCancellationToken` | `JobCancellationToken.Null` | The older cancellation API, same triggers. See [Cancellation](cancellation.md) |
| `PerformContext` (`Masterly.NightShift.Server`) | `null!` | The current job: `BackgroundJob.Id`, job parameters, the storage connection, `Items` shared with filters |
| `IJobConsole` (`Masterly.NightShift.Server`) | `null!` | Progress, log lines and checkpoints shown on the job's dashboard page. See [Progress, console and checkpoints](progress-and-console.md) |

`null!` is the way to pass `null` to a non-nullable parameter when nullable reference types are on.

The members of `PerformContext` a job uses most:

| Member | Meaning |
|---|---|
| `BackgroundJob` (`BackgroundJob`) | The running job: `Id`, `CreatedAt` (UTC) and `Job`, its type, method, arguments and queue |
| `RetryCount` (`int`) | How many times [`[AutomaticRetry]`](../filters/retries.md) has retried the job; 0 on its first run |
| `ScheduledFor` (`DateTime?`) | When the job was meant to run, in UTC: the time a delayed job was scheduled for, or the occurrence of the recurring job that created it; `null` for other jobs. See [below](#the-runs-scheduled-time-and-lateness) |
| `StartedAt` (`DateTime`), `Lateness` (`TimeSpan`) | When this run started (UTC), and how long after `ScheduledFor` (or the moment a continuation became due, or the job's creation) that was |
| `RecurringJobId` (`string?`) | The id of the recurring job that created the job, or `null` |
| `GetJobParameter<T>(string name)` | Reads a job parameter with your serializer options; `default` when it isn't set |
| `SetJobParameter(string name, object value)` | Writes a job parameter right away |
| `Console` (`IJobConsole`) | The same console an `IJobConsole` parameter receives |
| `CancellationToken` (`IJobCancellationToken`) | The job's cancellation token, see [Cancellation](cancellation.md) |
| `Items` (`IDictionary<string, object>`) | Values shared with the server filters of this run |
| `Connection` (`IStorageConnection`), `Storage` (`JobStorage?`), `ServerId` (`string?`) | The storage connection, the storage and the server running the job |

### Async methods

Methods that return `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` are awaited; the job ends when the task completes. Use `Enqueue<T>(x => x.MethodAsync(...))` (the `Expression<Func<T, Task>>` overloads); don't put `async` or `await` in the lambda. `async void` methods are rejected with `NotSupportedException`, because the worker can't tell when they finish.

### Return values

When the method returns a value (or its `Task<T>` / `ValueTask<T>` completes with one), the value is serialized with your serializer options and stored in the `Succeeded` state's `Result` entry. It is shown in the job's history on the dashboard, and a continuation can receive it with `[FromResult]` (see [Continuations](continuations.md#passing-the-parents-result-or-exception)). A value that can't be serialized doesn't fail the job: the text "Can not serialize the return value" is stored instead. `void`, `Task` and `ValueTask` store nothing.

## What can and can't be passed

| Works | Doesn't work |
|---|---|
| Numbers, `bool`, `string`, `char`, enums, `Guid`, `decimal` | `ref`, `out` and `in` parameters (`NotSupportedException`) |
| `DateTime`, `DateTimeOffset`, `TimeSpan` | Delegates, lambdas and `Expression` parameters (`NotSupportedException`) |
| Nullable versions of all of these, and `null` | Objects System.Text.Json can't write and read back: streams, `HttpContext`, `DbContext`, open connections, object graphs with cycles |
| Arrays, `List<T>`, `Dictionary<string, T>` and other collections System.Text.Json handles | Types without a constructor or setters System.Text.Json can use to rebuild them |
| Records and classes with public properties (and public fields, with the default options) | Non-public methods, explicit interface implementations, methods with open generic parameters, `async void` methods |
| Parameters declared as an interface or abstract class (the actual type is stored in `"$type"`) | Properties, array elements and collection items declared as an interface or abstract class |

Methods must be `public`, declared on a class or an interface. Generic methods work when the lambda closes the type arguments (`x => x.Import<Customer>(fileId)`).

Rules of thumb:

- **Pass IDs, not entities.** An entity serialized into the job is stale when the job runs, may be large, and may not deserialize at all. Pass `orderId` and load the order inside the job.
- **Keep arguments small.** Every argument is stored with the job, read by every worker that fetches it, and kept until the job expires. Put files and large payloads in blob storage or a table and pass a key.
- **Don't capture services.** `() => _emailService.Send(...)` stores only the type of `_emailService`; the server creates a new instance. Write `Enqueue<EmailService>(x => x.Send(...))` to make that explicit.
- **Mark secrets.** Arguments are visible on the dashboard. Use `[Redacted]` on parameters that hold secrets or personal data, see [Behavior filters](../filters/behavior.md).

## More examples

### A record as an argument

```csharp
public record ExportRequest(string Customer, DateTime From, DateTime To, string[] Columns);

public class ExportJobs
{
    public Task RunAsync(ExportRequest request) => Task.CompletedTask;
}

jobs.Enqueue<ExportJobs>(x => x.RunAsync(new ExportRequest("contoso", new DateTime(2026, 10, 1), new DateTime(2026, 11, 1), new[] { "id", "total" })));
```

### A job resolved through an interface

```csharp
public interface IEmailSender
{
    Task SendAsync(string to, string subject);
}

builder.Services.AddTransient<IEmailSender, SmtpEmailSender>();

jobs.Enqueue<IEmailSender>(x => x.SendAsync("ana@example.com", "Your export is ready"));
```

The job stores `IEmailSender`. Whichever implementation is registered on the server when the job runs is used.

### Using PerformContext and IJobConsole

```csharp
using Masterly.NightShift.Server;

public class AuditJobs
{
    public void Record(string action, PerformContext context, IJobConsole console)
    {
        console.WriteLine($"Job {context.BackgroundJob.Id} recorded '{action}'");
    }
}

jobs.Enqueue<AuditJobs>(x => x.Record("password-reset", null!, null!));
```

Progress, console lines, checkpoints and resumable steps are described in [Progress, console and checkpoints](progress-and-console.md).

### The run's scheduled time and lateness

`PerformContext.ScheduledFor` tells a job the moment it was meant to run, which isn't always when it starts. A recurring job due at 09:00 that starts at 09:00:02 still sees 09:00, so a daily report covers the right day even when it starts late:

```csharp
using Masterly.NightShift.Server;

public class ReportJobs
{
    public void Daily(PerformContext context)
    {
        DateTime due = context.ScheduledFor ?? context.StartedAt;
        DateTime day = due.Date.AddDays(-1);

        if (context.Lateness > TimeSpan.FromHours(1))
        {
            context.Console.WriteLine(JobConsoleLevel.Warning, $"Started {context.Lateness} late");
        }

        // build the report for 'day'
    }
}

RecurringJob.AddOrUpdate<ReportJobs>("daily-report", x => x.Daily(null!), "0 9 * * *");
```

| Member | Meaning |
|---|---|
| `ScheduledFor` | For a delayed job, the time it was scheduled for (the `ScheduledFor` job parameter, written when the job is created in the Scheduled state). For a job created by a recurring job, the occurrence it was created for (the `Time` parameter). `null` for a fire-and-forget job, a continuation or a job created in any other state. Retries don't change it. |
| `StartedAt` | When this run started, in UTC: when the server created the context, before the server filters and the job method ran. |
| `Lateness` | `StartedAt` minus `ScheduledFor`. When `ScheduledFor` is `null`, a [continuation](continuations.md) (a fan-in continuation and a chain step too) is measured from the moment it became due, when its parents let it leave the Awaiting state (or the time its next state scheduled it for), so the time its parents waited and ran doesn't count; any other job from its creation. Never negative. A retry is measured from the same moment, so its lateness includes the earlier attempts. The time the job was held, after the moment it was meant to run, because the server that fetched it was paused on its own or fenced is left out (the `ServerHold` job parameter, [Drain mode](../server/drain-mode.md#a-paused-server-stops-fetching)); the time it was held by the global switch or a paused queue counts. |
| `RecurringJobId` | The id of the recurring job that created the job. |

All of them are read from the parameters the worker loaded with the job, so they cost no storage round trip. To skip runs that start too late, use [`[SkipIfLate]`](../filters/timing.md#skipiflateattribute).

### Reading job parameters

Job parameters are named values stored next to the arguments, set by filters or by `IBackgroundJobClientV2.Create(job, state, parameters)` (see [Fire-and-forget jobs](fire-and-forget.md#job-parameters-at-creation)). Read them through `PerformContext`:

```csharp
using Masterly.NightShift.Server;

public class TenantJobs
{
    public void Rebuild(int indexId, PerformContext context)
    {
        string? tenantId = context.GetJobParameter<string>("TenantId");
    }
}

jobs.Enqueue<TenantJobs>(x => x.Rebuild(7, null!));
```

`GetJobParameter<T>` returns `default` when the parameter isn't set.

Or let the server fill a method parameter with it. Mark the parameter with `[FromParameter("TenantId")]` (`FromParameterAttribute`) and pass a placeholder in the lambda. This needs `UseResultsInContinuations()` at startup, which registers the filter that fills these parameters (`JobParameterInjectionFilter`):

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

public class TenantIndexJobs
{
    public void Rebuild(int indexId, [FromParameter("TenantId")] string? tenantId)
    {
        // tenantId is "contoso" when the job was created with that parameter
    }
}

Job job = Job.FromExpression<TenantIndexJobs>(x => x.Rebuild(7, null));
jobs.Create(job, new EnqueuedState(), new Dictionary<string, object> { ["TenantId"] = "contoso" });
```

Here `jobs` is an `IBackgroundJobClientV2`. The value is read with your serializer options as the parameter's type. When the job parameter isn't set, the method receives the placeholder.

### Evaluated now, run later

```csharp
// order.Id is read now, when the job is created; RenderAsync runs later on a server.
jobs.Enqueue<InvoiceJobs>(x => x.RenderAsync(order.Id, CancellationToken.None));

// Only the type of invoiceJobs is stored. The server creates a new InvoiceJobs.
jobs.Enqueue(() => invoiceJobs.RenderAsync(orderId, CancellationToken.None));
```

## Things to know

- **Serialization errors surface at creation.** If an argument can't be serialized, `Enqueue` throws (wrapped in `BackgroundJobClientException`) and no job is created.
- **Deserialization errors surface on the server.** If a stored argument can't be read back (its type changed, or its assembly is missing), the server can't move the job to its next state. The reason is `Can not change the state to 'Processing': the job's arguments could not be deserialized.` (`'Enqueued'` instead of `'Processing'` when a scheduled or retried job was due), and the error is in the exception details. When the job's class or method is the problem, the reason says `the job's type or method was not found.` instead. The global `AutomaticRetry` filter retries it like any other failure (attributes on the job's method can't be read, so they don't apply), so the job shows under **Retries** before it reaches **Failed**. Fix the code: the next retry then succeeds, and failed jobs can be requeued. See [Troubleshooting](../troubleshooting.md#a-job-fails-because-it-cant-be-loaded).
- **The stored format depends on the compatibility level.** Servers running older versions can read jobs only if you keep a compatible level, see [Compatibility levels](../configuration/compatibility-levels.md).
- **Argument values are shown on the dashboard.** Don't pass passwords or tokens, or mark the parameter `[Redacted]`.

## See also

- [Fire-and-forget jobs](fire-and-forget.md)
- [Cancellation](cancellation.md)
- [Continuations](continuations.md)
- [Serialization](../configuration/serialization.md)
- [Dependency injection](../server/dependency-injection.md)
- [Source-generated invokers](source-generated-invokers.md)
