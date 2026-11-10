# Job classes

A job class holds the work of one kind of job. The code that creates the job only names the class, or passes the arguments, and doesn't have to know what its method is called. There are two kinds:

- `IJob`: a job that takes no arguments.
- `IJobHandler<TArgs>`: a job that runs from a plain arguments object.

## Quick example

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;

public sealed class NightlyCleanup(ICleaner cleaner) : IJob
{
    public Task RunAsync(PerformContext context, CancellationToken cancellationToken)
    {
        return cleaner.RunAsync(cancellationToken);
    }
}

public sealed record SendInvoice(int InvoiceId);

[Queue("invoices")]
public sealed class SendInvoiceHandler(IMailer mailer) : IJobHandler<SendInvoice>
{
    [AutomaticRetry(Attempts = 3)]
    public Task HandleAsync(SendInvoice args, PerformContext context, CancellationToken cancellationToken)
    {
        return mailer.SendInvoiceAsync(args.InvoiceId, cancellationToken);
    }
}
```

```csharp
builder.Services.AddNightShiftJobHandler<NightlyCleanup>();
builder.Services.AddNightShiftJobHandler<SendInvoiceHandler>();
```

```csharp
app.MapPost("/invoices/{id:int}/send", (int id, IBackgroundJobClient jobs) =>
{
    jobs.Enqueue<NightlyCleanup>();

    string? jobId = jobs.Enqueue(new SendInvoice(id));
    return Results.Accepted(value: jobId);
});
```

## How it works

Creating a job stores an ordinary call of the class's method. Nothing wraps the class, so it behaves as any other job does:

- Attributes on the class or on its method apply: `[Queue]`, `[AutomaticRetry]`, `[DisableConcurrentExecution]`, `[JobDisplayName]` and the rest.
- The dashboard shows the class's name.
- The server creates the class through dependency injection, in a scope of its own.
- The `PerformContext` is the running job. Its most used members are `BackgroundJob.Id`, `RetryCount`, `Console` and `GetJobParameter<T>`. See [Job arguments](job-arguments.md).
- The `CancellationToken` is the server's shutdown token. [Cancellation](cancellation.md) explains what happens when it fires.
- The source generator emits an invoker for every class implementing `IJob` or `IJobHandler<TArgs>` in the assembly it compiles, so no reflection is used to call it. See [Source-generated invokers](source-generated-invokers.md).

The arguments are stored as JSON, under the same rules as any other job argument.

`Enqueue(args)` finds the handler from the argument's compile-time type, which needs the handler to be registered. Naming the class, as in `Enqueue<NightlyCleanup>()` or `Enqueue<SendInvoiceHandler, SendInvoice>(args)`, needs no registration.

## API

### The interfaces

| Member | Meaning |
|---|---|
| `IJob.RunAsync(PerformContext context, CancellationToken cancellationToken)` | Runs a job that takes no arguments. |
| `IJobHandler<TArgs>.HandleAsync(TArgs args, PerformContext context, CancellationToken cancellationToken)` | Runs a job with its arguments. |

Each must be a public method: an explicit interface implementation is rejected. For synchronous work, return `Task.CompletedTask`. A class can implement `IJobHandler<TArgs>` several times, once per arguments type.

### Creating jobs

These are extension methods on `IBackgroundJobClient`. The static `BackgroundJob` class has the same ones.

| What runs | Enqueue now | After a delay, or at a moment | After a parent job |
|---|---|---|---|
| The job class `TJob` | `Enqueue<TJob>()` | `Schedule<TJob>(delay)`, `Schedule<TJob>(enqueueAt)` | `ContinueJobWith<TJob>(parentId, options)` |
| The registered handler of `TArgs` | `Enqueue(args)` | `Schedule(args, delay)`, `Schedule(args, enqueueAt)` | `ContinueJobWith(parentId, args, options)` |
| The handler you name | `Enqueue<THandler, TArgs>(args)` | `Schedule<THandler, TArgs>(args, delay)`, `Schedule<THandler, TArgs>(args, enqueueAt)` | `ContinueJobWith<THandler, TArgs>(parentId, args, options)` |

`delay` is a `TimeSpan`, and `enqueueAt` a `DateTimeOffset`. `options` defaults to `JobContinuationOptions.OnlyOnSucceededState`.

Each returns the job's ID, or `null` when a filter canceled the creation. `Enqueue(args)` and its siblings throw `InvalidOperationException` when the arguments type has no registered handler, or has several; the message names the type and the handlers.

For anything that takes a `Job`, such as `IRecurringJobManager.AddOrUpdate`, use `JobHandlers.CreateJob<TJob>()`, `JobHandlers.CreateJob(args)` or `JobHandlers.CreateJob<THandler, TArgs>(args)`.

### Registering job classes

| Method | Effect |
|---|---|
| `services.AddNightShiftJobHandler<THandler>()` | Checks the class, registers it for its arguments types, and adds it to the container as scoped unless it is already there |
| `services.AddNightShiftJobHandlers(params Assembly[] assemblies)` | The same for every concrete job class in the assemblies |
| `JobHandlers.Add<THandler>()`, `JobHandlers.Add(Type)` | Checks the class and registers it, without a container |
| `JobHandlers.FindHandlers(Assembly)` | Lists an assembly's job classes. Types that can't be loaded are skipped, with a warning in the log. |
| `JobHandlers.GetHandlerTypes(Type argsType)` | The handlers of an arguments type |
| `JobHandlers.Clear()` | Forgets every handler, for tests |

## More examples

### Register every job class of an assembly

```csharp
builder.Services.AddNightShiftJobHandlers(typeof(SendInvoiceHandler).Assembly);
```

### Two jobs for the same arguments

Name the handler when several handle the arguments:

```csharp
jobs.Enqueue<SendInvoiceHandler, Invoice>(invoice);
jobs.Enqueue<ArchiveInvoiceHandler, Invoice>(invoice);
```

### Use the job's context

```csharp
public sealed class ImportHandler(IImporter importer) : IJobHandler<ImportFile>
{
    public Task HandleAsync(ImportFile args, PerformContext context, CancellationToken cancellationToken)
    {
        context.Console.WriteLine($"Attempt {context.RetryCount + 1} of job {context.BackgroundJob.Id}");
        return importer.RunAsync(args.Path, cancellationToken);
    }
}
```

### Run a job class on a schedule

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Add<NightlyCleanup>(Cron.Daily(3))
    .AddHandler("purge-sessions", new PurgeSessions(Days: 30), Cron.Daily(4)));
```

See [Recurring jobs declared in code](../recurring-jobs/recurring-jobs-in-code.md).

### Test code that creates jobs

These methods end in `IBackgroundJobClient.Create`, so a mock can check the job:

```csharp
Mock<IBackgroundJobClient> client = new();

client.Object.Enqueue<SendInvoiceHandler, SendInvoice>(new SendInvoice(7));

client.Verify(x => x.Create(
    It.Is<Job>(job => job.Type == typeof(SendInvoiceHandler) && job.Args[0].Equals(new SendInvoice(7))),
    It.IsAny<EnqueuedState>()));
```

## Things to know

- **`Enqueue(args)` needs exactly one handler.** Several handlers can be registered for one arguments type, but then the job must name the one to run. The check happens when the job is created.
- **The lookup uses the compile-time type.** A `SendInvoice` held in an `object` variable is looked up as `object`, and no handler is found.
- **Handlers are registered for the whole process**, like `GlobalJobFilters`. Register them at start-up, on every process that creates jobs with `Enqueue(args)`. A process that only runs them doesn't need the registration, only the class.
- **Renaming a job class or its arguments type changes what is stored.** Jobs already in the storage still name the old type, as with any job. Let them finish first, or map the old name with `UseTypeResolver`.

## See also

- [Fire-and-forget jobs](fire-and-forget.md)
- [Job arguments](job-arguments.md)
- [Recurring jobs declared in code](../recurring-jobs/recurring-jobs-in-code.md)
- [Custom filters](../filters/custom-filters.md)
