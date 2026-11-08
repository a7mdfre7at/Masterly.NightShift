# Fire-and-forget jobs

A fire-and-forget job runs once, as soon as a worker is free. Use it to move slow or unreliable work (sending e-mail, resizing images, calling a partner API) out of a web request, so the request returns right away and the work is retried if it fails.

## Quick example

```csharp
using Masterly.NightShift;

public class EmailService
{
    public void SendWelcome(int userId)
    {
        // send the e-mail
    }
}

app.MapPost("/users/{id:int}/welcome", (int id, IBackgroundJobClient jobs) =>
{
    string? jobId = jobs.Enqueue<EmailService>(x => x.SendWelcome(id));
    return Results.Accepted(value: new { jobId });
});
```

`IBackgroundJobClient` is registered by `services.AddNightShift(...)`. Without dependency injection, use the static facade, which talks to `JobStorage.Current`:

```csharp
BackgroundJob.Enqueue<EmailService>(x => x.SendWelcome(id));
```

## How it works

1. `Enqueue` reads the lambda expression. It records the type (`EmailService`), the method (`SendWelcome`) and the argument values (`id`), but it does not call the method. See [Job arguments](job-arguments.md) for what is captured.
2. The client runs the client filters (`IClientFilter`) and stores the job with its arguments and parameters. The job is created in the `Enqueued` state, and its ID is added to a queue (`default` unless you name one). The call returns the job ID.
3. A `Worker` on any server that listens to that queue fetches the job, moves it to `Processing`, creates an `EmailService` through the job activator (your DI container in ASP.NET Core) and calls `SendWelcome`.
4. When the method returns, the job moves to `Succeeded`. When it throws, the job moves to `Failed`, and the `AutomaticRetry` filter (on by default, 10 retries) moves it to `Scheduled` to try again later. See [Retries](../filters/retries.md).

Delivery is at least once: if a server dies while the job is running, the job is put back on its queue and runs again. Write jobs so that running them twice does no harm.

## API

### Enqueue

Every `Enqueue` overload returns `string?`: the new job ID, or `null` when a client filter canceled the creation (see [Things to know](#things-to-know)). The same overloads exist as static methods on `BackgroundJob` and as extension methods on `IBackgroundJobClient` (`BackgroundJobClientExtensions`).

| Lambda type | Example | Use it for |
|---|---|---|
| `Expression<Action>` | `Enqueue(() => CacheMaintenance.Purge("products"))` | Static methods, or an instance method on a captured variable (only its type is kept) |
| `Expression<Func<Task>>` | `Enqueue(() => CacheMaintenance.PurgeAsync("products"))` | The same, for async methods |
| `Expression<Action<T>>` | `Enqueue<EmailService>(x => x.SendWelcome(id))` | Instance methods of `T`; the server creates `T` through the job activator |
| `Expression<Func<T, Task>>` | `Enqueue<ImageService>(x => x.ResizeAsync(imageId, 800))` | The same, for async methods |

Each of the four has a queue-specific twin that takes the queue name first:

| Overload | Meaning |
|---|---|
| `Enqueue(methodCall)` | Places the job on the `default` queue, or on the queue named by a `[Queue]` attribute on the method or class |
| `Enqueue(string queue, methodCall)` | Stores `queue` with the job and places it there |

Queue names may contain only lowercase letters, digits, `_` and `-`. A `[Queue]` attribute that names a queue other than `default` takes priority over the `queue` argument. The storage must support per-job queues; SQL Server does. See [Queues and workers](../server/queues-and-workers.md).

### Create with an explicit state

`Enqueue` is a shortcut for "create in the `Enqueued` state". To create a job in any state, use `Create`:

| Method | Where | Meaning |
|---|---|---|
| `Create(methodCall, IState state)` | `BackgroundJobClientExtensions`, four lambda types | Creates the job in `state` |
| `Create(string queue, methodCall, IState state)` | `BackgroundJobClientExtensions`, four lambda types | The same, with a queue stored on the job |
| `Create(Job job, IState state)` | `IBackgroundJobClient` | Creates a job from a `Job` object (build one with `Job.FromExpression(...)`) |
| `Create(Job job, IState state, IDictionary<string, object>? parameters)` | `IBackgroundJobClientV2` | The same, and stores the given job parameters with it |
| `ChangeState(string jobId, IState state, string? expectedState)` | `IBackgroundJobClient` | Moves an existing job to another state, see [Managing jobs](managing-jobs.md) |

`IBackgroundJobClientV2` also exposes `Storage`, the `JobStorage` the client writes to. Both interfaces are registered in DI.

### BackgroundJobClient

`BackgroundJobClient` is the default implementation of both interfaces. You need it only outside DI.

| Member | Meaning |
|---|---|
| `BackgroundJobClient()` | Uses `JobStorage.Current` and the global filters |
| `BackgroundJobClient(JobStorage storage)` | Uses the given storage and the global filters |
| `BackgroundJobClient(JobStorage storage, IJobFilterProvider filterProvider)` | Uses the given storage and filter provider |
| `BackgroundJobClient(JobStorage storage, IBackgroundJobFactory factory, IBackgroundJobStateChanger stateChanger)` | Full control over creation and state changes |
| `Storage` (`JobStorage`) | The storage the client uses |
| `RetryAttempts` (`int`, default `0`) | How many more times creation is tried when the storage throws |

The class is thread-safe; keep one instance and reuse it.

## More examples

### Async methods

Methods that return `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` are awaited by the worker. The job succeeds when the task completes.

```csharp
public class ImageService
{
    public Task ResizeAsync(Guid imageId, int width)
    {
        // resize and store the image
        return Task.CompletedTask;
    }
}

jobs.Enqueue<ImageService>(x => x.ResizeAsync(imageId, 800));
```

Don't write `async` in the lambda (`x => await x.ResizeAsync(...)` is not an expression tree). Don't use `async void` methods: `Enqueue` throws `NotSupportedException` for them.

### Static methods

```csharp
public static class CacheMaintenance
{
    public static void Purge(string region)
    {
        // clear the cache region
    }
}

jobs.Enqueue(() => CacheMaintenance.Purge("products"));
```

### A named queue

```csharp
jobs.Enqueue<ImageService>("images", x => x.ResizeAsync(imageId, 800));
```

Only servers that list `images` in `BackgroundJobServerOptions.Queues` process this job. If no server does, it waits on the queue forever.

### Handling a canceled creation

```csharp
string? jobId = jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));
if (jobId == null)
{
    // A client filter (for example [PreventDuplicates]) decided not to create the job.
    return;
}
```

### Job parameters at creation

Job parameters are named values stored next to the job. Filters use them (tags, trace context, batch IDs), and your job can read them through `PerformContext`:

```csharp
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

Job job = Job.FromExpression<EmailService>(x => x.SendWelcome(userId));
var parameters = new Dictionary<string, object>
{
    ["TenantId"] = "contoso",
};

string? jobId = jobs.Create(job, new EnqueuedState(), parameters);
```

Here `jobs` is an `IBackgroundJobClientV2`. Reading the value inside the job is shown in [Job arguments](job-arguments.md#reading-job-parameters).

### Creating directly in another state

```csharp
jobs.Create<EmailService>(x => x.SendWelcome(userId), new EnqueuedState("critical") { Reason = "VIP sign-up" });
```

The reason appears in the job's history on the dashboard.

### Without dependency injection

```csharp
var client = new BackgroundJobClient(JobStorage.Current) { RetryAttempts = 3 };
client.Enqueue<EmailService>(x => x.SendWelcome(userId));
```

## Things to know

- **The returned ID can be `null`.** `IBackgroundJobClient.Create` may return `null` when a client filter sets `CreatingContext.Canceled`; `[PreventDuplicates]` does this for a duplicate. Every method built on it (`Enqueue`, `Schedule`, `ContinueJobWith`, `ContinueJobWithAll`, `Create`) returns `string?` for that reason. Check for `null` before you store the ID or pass it to `ContinueJobWith`.
- **Errors are wrapped.** When the storage or a filter throws during creation, the client throws `BackgroundJobClientException`; the original exception is in `InnerException`.
- **Creating a job is not part of your database transaction.** If you enqueue inside a transaction that later rolls back, the job still exists. Enqueue after the commit, or pass only IDs and let the job check that the data exists.
- **A server must listen to the queue.** Jobs on a queue that no server processes stay `Enqueued`. The dashboard's Queues page shows them.
- **`Enqueue` does not wait for the job.** It writes the job to storage synchronously and returns; the job runs later on whichever server fetches it, possibly in another process.

## See also

- [Delayed jobs](delayed-jobs.md)
- [Continuations](continuations.md)
- [Job arguments](job-arguments.md)
- [Managing jobs](managing-jobs.md)
- [Concurrency filters](../filters/concurrency.md) (`[PreventDuplicates]`)
- [Queues and workers](../server/queues-and-workers.md)
