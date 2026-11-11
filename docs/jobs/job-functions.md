# Job functions

A job function is a job that can be created by name, with a payload, from code that doesn't reference the method's type: another service, a script, a message handler, an admin tool. The name is stable even when the class is renamed or moved, and the payload is checked against the function when the job is created, so a bad call fails where it is made and not hours later on a server.

A function is either a method marked with `[JobFunction]` or a delegate registered in a container.

## Quick example

```csharp
using Masterly.NightShift;

public class Maintenance(AppDbContext db)
{
    [JobFunction("maintenance.cleanup", Description = "Deletes rows older than the given number of days")]
    public Task CleanupAsync(int days, CancellationToken cancellationToken)
    {
        return db.Logs.Where(x => x.CreatedAt < DateTime.UtcNow.AddDays(-days)).ExecuteDeleteAsync(cancellationToken);
    }
}

builder.Services.AddNightShiftFunctions(typeof(Maintenance).Assembly);

// anywhere, by name
BackgroundJob.EnqueueFunction("maintenance.cleanup", 30);
BackgroundJob.ScheduleFunction("maintenance.cleanup", 90, TimeSpan.FromHours(6));
RecurringJob.AddOrUpdateFunction("nightly-cleanup", "maintenance.cleanup", 30, Cron.Daily(3));
```

## Methods marked with JobFunction

`[JobFunction("name")]` on a public method, static or instance, names it. Register the methods:

| Call | Registers |
|---|---|
| `JobFunctions.Add<T>()` / `JobFunctions.Add(Type)` | The marked methods of a type |
| `JobFunctions.AddAssembly(assembly)` | The marked methods of every class of an assembly |
| `services.AddNightShiftFunctions(params Assembly[])` | The same, and the classes with marked instance methods in the container as scoped services, unless registered already. Use it for assemblies the host doesn't reference directly |

The job created is an ordinary job of that method, as if you had written `Enqueue<Maintenance>(x => x.CleanupAsync(30, CancellationToken.None))`: it has the method's filters, queue and retries, and shows on the dashboard as that method.

Names are 1 to 100 letters, digits, `.`, `_`, `-` and `:`, starting with a letter or a digit, and are compared without case. A type that declares the same name twice throws `InvalidOperationException` when it is registered; registering a function under a name already used replaces it.

## The payload

The parameters the server fills itself (`CancellationToken`, `IJobCancellationToken`, `PerformContext`) don't count. The others take the payload:

| Parameters | Payload |
|---|---|
| None | `null` |
| One | A value of its type, or anything that converts to it through JSON: another object with the same shape, a JSON text, a `JsonElement` |
| Several | An object whose properties are the parameters, by name and without case: `new { Region = "eu", Days = 3 }`, or the same as JSON. A parameter with a default value may be left out |

A payload that doesn't fit throws `ArgumentException` when the job is created: a value of the wrong type, a missing parameter, a property the function has no parameter for, a payload for a function that takes none. The payload is converted with the application's serializer settings and stored as the job's arguments.

## Delegates

A delegate registered in the container is a function too, with the job's own scope, services and cancellation token:

```csharp
builder.Services.AddNightShiftFunction<ShipmentRequest>("orders.ship", async (services, request, cancellationToken) =>
{
    ShippingClient shipping = services.GetRequiredService<ShippingClient>();
    await shipping.ShipAsync(request.OrderId, request.Carrier, cancellationToken);
});

builder.Services.AddNightShiftFunction("cache.warm", (services, cancellationToken) =>
    services.GetRequiredService<CacheWarmer>().WarmAsync(cancellationToken));
```

The job is a call to `DelegateJobFunctionRunner.RunAsync(name, payload)`, with the payload as JSON. Every server that runs these jobs must register the same delegate under the same name: a server without it fails the job with `InvalidOperationException`.

## Creating jobs by name

| Method | Creates |
|---|---|
| `EnqueueFunction(name, payload = null, ScheduleOptions? options = null)` | A fire-and-forget job |
| `ScheduleFunction(name, payload, TimeSpan delay, options)` | A job due after a delay |
| `ScheduleFunction(name, payload, DateTimeOffset enqueueAt, options)` | A job due at a moment |
| `AddOrUpdateFunction(recurringJobId, name, payload, cron, RecurringJobOptions? options = null)` | A recurring job (`RecurringJob`, `IRecurringJobManager`) |

The first three are static methods on `BackgroundJob` and extension methods on `IBackgroundJobClient`. They return the job's ID, or `null` when a filter or a [schedule policy](schedule-policies.md) didn't let it be created, and throw `ArgumentException` for an unknown name or a payload that doesn't fit. [`ScheduleOptions`](schedule-policies.md#delayed-and-fire-and-forget-jobs) add a policy, a late-start limit and a queue. `JobFunctions.Find(name)` and `JobFunctions.GetAll()` list what is registered; `JobFunction.CreateJob(payload, queue)` gives the `Job` itself.

## Deleting pending jobs

To cancel work that hasn't started, by method, function or any condition, use the `JobStorage` extensions described in [Managing jobs](managing-jobs.md#delete-pending-jobs):

```csharp
storage.DeletePendingFunctionJobs("maintenance.cleanup");
storage.DeletePendingJobs<Mailer>();
```

## See also

- [Job classes](typed-handlers.md), for jobs created from an arguments object
- [Job catalog](job-catalog.md), to run any job method from the dashboard
- [Managing jobs](managing-jobs.md)
