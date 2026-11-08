# Queues and workers

Workers are the threads that run your jobs, and queues are named lists that jobs wait in until a worker takes them. Use several queues when some jobs must not wait behind others, and choose the worker count to match the kind of work your jobs do.

## Quick example

```csharp
using Masterly.NightShift;

// The server: 10 workers, listening to two queues
builder.Services.AddNightShiftServer(options =>
{
    options.WorkerCount = 10;
    options.Queues = new[] { "critical", "default" };
});
```

```csharp
using Masterly.NightShift;

// Every job of this class goes to the "critical" queue
[Queue("critical")]
public class PaymentService
{
    public void CapturePayment(int orderId) { }

    public void RefundPayment(int orderId) { }
}
```

Enqueue it as usual; `jobs` is an injected `IBackgroundJobClient`:

```csharp
jobs.Enqueue<PaymentService>(x => x.CapturePayment(orderId));
```

## How it works

A job is put in a queue when it enters the Enqueued state: right away for a fire-and-forget job, when the delay is over for a delayed job, after the parent finishes for a continuation, and on each run of a recurring job. A job that is not given a queue goes to `default` (`EnqueuedState.DefaultQueue`).

Each server starts `WorkerCount` workers. Every worker repeats the same loop:

1. Ask the storage for the next job from any of the server's `Queues`, waiting until one is available or the server stops.
2. Move the job to the Processing state. Election filters can change that state; this is how [drain mode](drain-mode.md) holds jobs.
3. Create the job's instance through the [job activator](dependency-injection.md) and call the method.
4. Move the job to Succeeded, or to Failed (which `AutomaticRetry` turns into Scheduled for another attempt), and remove it from the queue.

A worker runs one job at a time, so `WorkerCount` is the maximum number of jobs a server runs at once. Several servers can listen to the same queue, on one machine or many; the storage gives each job to one worker at a time.

### Queue order and priority

`Queues` lists the queues a server listens to. Whether the order of that list means priority depends on the storage.

The SQL Server storage does **not** use the order of the list. A worker fetches with a single query that takes the first available job from any of the listed queues (`WHERE Queue IN (...)`, without `ORDER BY`). The queue table is clustered by queue name and then by job queue ID, so in practice the queue whose name sorts first alphabetically tends to be served first, and jobs within a queue come out roughly oldest first. That is a side effect of the query plan, not a guarantee. Other storages may use the order of the list; check their documentation.

If some jobs must never wait behind others, give them their own workers: a separate server that listens only to their queue (see [Keep important jobs moving](#keep-important-jobs-moving)). That works the same on every storage.

## API

### Choosing the queue of a job

| API | Where | Meaning |
| --- | --- | --- |
| `[Queue(string queue)]` | class or method | Puts the job on `queue` every time it is enqueued, including retries, continuations and recurring runs. An attribute on the method wins over one on the class. The name may contain `{0}`, `{1}`, ... placeholders, filled from the job's arguments with `String.Format` (with their real values, `[Redacted]` ones included). A plain name is validated when the attribute is read; a template's result when the job is enqueued. |
| `Enqueue(string queue, ...)` | `IBackgroundJobClient` extension, `BackgroundJob` | Creates a fire-and-forget job on `queue`. |
| `Schedule(string queue, ..., TimeSpan delay)` and `Schedule(string queue, ..., DateTimeOffset enqueueAt)` | `IBackgroundJobClient` extension, `BackgroundJob` | Creates a delayed job that goes to `queue` when its time comes. |
| `ContinueJobWith(string parentId, string queue, ...)` | `IBackgroundJobClient` extension, `BackgroundJob` | Creates a continuation that goes to `queue` when the parent finishes. |
| `AddOrUpdate(string recurringJobId, string queue, ..., string cronExpression)` | `IRecurringJobManager` extension, `RecurringJob` | Every run of the recurring job goes to `queue`. |

### Server settings

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `BackgroundJobServerOptions.Queues` | `string[]` | `{ "default" }` | The queues this server's workers fetch from. Must contain at least one name. |
| `BackgroundJobServerOptions.WorkerCount` | `int` | `Math.Min(Environment.ProcessorCount * 5, 20)` | How many jobs this server runs at once. Must be greater than 0. |
| `BackgroundJobServerOptions.StopTimeout` | `TimeSpan` | 500 ms | Time between the stop signal and the cancellation of running jobs' tokens. |
| `BackgroundJobServerOptions.ShutdownTimeout` | `TimeSpan` | 15 seconds | Time between the stop signal and the moment the server gives up waiting for running jobs. |

All options are described in [Server options](server-options.md).

### Stopping a server you created

| Member | Meaning |
| --- | --- |
| `SendStop()` | Sends the stop signal and returns at once. Workers stop taking new jobs; the stop and shutdown timeouts start counting. |
| `WaitForShutdown(TimeSpan timeout)` | Blocks until the server has stopped or the timeout passes. Returns `true` when it stopped. |
| `WaitForShutdownAsync(CancellationToken cancellationToken)` | The same, without blocking a thread. |
| `Dispose()` | Sends the stop signal if it was not sent yet and, unless you already waited, waits until the server has stopped. |

With `AddNightShiftServer` the host does all of this for you.

## More examples

### Put a whole class or one method on a queue

```csharp
using Masterly.NightShift;

public class NotificationService
{
    [Queue("emails")]
    public void SendOrderConfirmation(int orderId) { }

    // No attribute: goes to "default"
    public void SendDigest(int userId) { }
}
```

### Choose the queue from an argument

The queue name is a format string over the job's arguments:

```csharp
public class TenantReportService
{
    [Queue("tenant-{0}")]
    public void Rebuild(string tenantId) { }
}
```

```csharp
// Enqueued to "tenant-contoso"
jobs.Enqueue<TenantReportService>(x => x.Rebuild("contoso"));
```

The resulting name must still be a valid queue name, so this only works when the argument contains lowercase letters, digits, `_` and `-` only. A server must listen to every queue that can come out of it.

### Pick the queue when you create the job

```csharp
// Fire-and-forget on the "emails" queue
jobs.Enqueue<EmailService>("emails", x => x.SendWelcome(userId));

// Delayed: goes to "emails" when the delay is over
jobs.Schedule<EmailService>("emails", x => x.SendWelcome(userId), TimeSpan.FromMinutes(30));

// Recurring: each run is enqueued to "reports"
recurringJobs.AddOrUpdate<ReportService>("daily-report", "reports", x => x.BuildDaily(), Cron.Daily());
```

Here `jobs` is an `IBackgroundJobClient` and `recurringJobs` an `IRecurringJobManager`, both injected (see [Dependency injection](dependency-injection.md)). The static facade has the same overloads:

```csharp
BackgroundJob.Enqueue<EmailService>("emails", x => x.SendWelcome(userId));
```

### Keep important jobs moving

Run a separate server for the queue that must not wait. Its workers serve nothing else, so a burst of reports can't delay payments:

```csharp
// Fast, important jobs: their own workers, never stuck behind reports
services.AddNightShiftServer(options =>
{
    options.ServerName = "critical";
    options.Queues = new[] { "critical" };
    options.WorkerCount = 5;
});

// Everything else
services.AddNightShiftServer(options =>
{
    options.ServerName = "general";
    options.Queues = new[] { "default", "emails", "reports" };
    options.WorkerCount = 10;
    options.IsLightweightServer = true;
});
```

Each `AddNightShiftServer` call adds one more server to the process. `IsLightweightServer` on the second one avoids running the schedulers and storage maintenance twice; see [Server options](server-options.md#a-second-lightweight-server-in-the-same-process).

### Choosing the worker count

Size the workers by what your jobs spend their time on:

- **CPU-bound** jobs (image resizing, PDF rendering, compression) are limited by cores. More workers than cores only adds switching between threads. Start with `Environment.ProcessorCount`.
- **I/O-bound** jobs (HTTP calls, sending emails, database work) spend most of the time waiting. A worker that waits uses almost no CPU, so you can run many more workers than cores, for example 20 to 100. Raise the number until the service you call, the database or the network becomes the limit.

When one application has both kinds, put them on different queues and give each its own server:

```csharp
// CPU-bound work (image resizing, PDF rendering): about one worker per core
services.AddNightShiftServer(options =>
{
    options.ServerName = "cpu";
    options.Queues = new[] { "images" };
    options.WorkerCount = Environment.ProcessorCount;
});

// I/O-bound work (HTTP calls, emails, database): many more workers than cores
services.AddNightShiftServer(options =>
{
    options.ServerName = "io";
    options.Queues = new[] { "default", "emails" };
    options.WorkerCount = 50;
    options.IsLightweightServer = true;
});
```

`async` job methods do not free their worker while they wait: the worker stays busy until the returned task completes, so the worker count still limits how many run at once. Workers also use storage connections to fetch jobs and change their states, so with very large counts check that the database and its connection pool keep up. To limit a particular kind of job rather than a whole server, see the [concurrency filters](../filters/concurrency.md).

### Run servers on several machines

Point every machine at the same storage and start a server on each. They share the queues: each job runs on exactly one of them. A machine can listen to a subset of queues, for example only `images` on a machine with a GPU:

```csharp
services.AddNightShiftServer(options =>
{
    options.ServerName = "gpu-1";
    options.Queues = new[] { "images" };
    options.WorkerCount = Environment.ProcessorCount;
});
```

Every machine that can pick up a job must have the job's assembly and version deployed. The dashboard's Servers page shows each server with its queues and worker count.

### Graceful shutdown

When the host stops (Ctrl+C, a deployment, `SendStop()` or `Dispose()`), the server stops in steps:

1. **Right away**, workers stop taking new jobs and the schedulers stop. Jobs that are already running continue.
2. **After `StopTimeout`** (500 ms by default), the cancellation token of every running job is canceled.
3. A job that observes its token and throws `OperationCanceledException` is put back in its queue at once, so it runs again later on this or another server. A job that finishes in time is completed normally.
4. **After `ShutdownTimeout`** (15 seconds by default), the server stops waiting, logs a warning that it stopped non-gracefully, and removes itself from the storage. Jobs still running are abandoned in the Processing state; the storage gives them to another worker later (with SQL Server, after the invisibility timeout).

To shut down cleanly, take a `CancellationToken` in long-running jobs and pass it on. Pass `CancellationToken.None` when you enqueue; the worker substitutes the real token:

```csharp
public class NewsletterService
{
    private readonly IOrderRepository _orders;

    public NewsletterService(IOrderRepository orders)
    {
        _orders = orders;
    }

    public async Task SendRemindersAsync(CancellationToken cancellationToken)
    {
        var orderIds = await _orders.GetUnpaidOrderIdsAsync(cancellationToken);

        foreach (var orderId in orderIds)
        {
            // Throws OperationCanceledException when the server is stopping;
            // the job goes back to its queue and runs again later
            cancellationToken.ThrowIfCancellationRequested();

            await SendReminderAsync(orderId, cancellationToken);
        }
    }

    private Task SendReminderAsync(int orderId, CancellationToken cancellationToken) =>
        Task.Delay(100, cancellationToken);
}
```

```csharp
jobs.Enqueue<NewsletterService>(x => x.SendRemindersAsync(CancellationToken.None));
```

To let jobs finish their current step, raise the timeouts, and give the host at least as long:

```csharp
services.AddNightShiftServer(options =>
{
    options.StopTimeout = TimeSpan.FromSeconds(5);
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

// Give the host at least as long as the server needs
services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(45));
```

In a generic host or ASP.NET Core app, the server is stopped by the host and waits at most the host's `HostOptions.ShutdownTimeout` (30 seconds by default). If that is shorter than the server's `ShutdownTimeout`, the host stops waiting first. When the host registers `IHostApplicationLifetime`, as every generic host does on every target framework, the stop signal is sent as soon as the application starts stopping, before other hosted services are stopped. With the ASP.NET Core 2.x `WebHost` on .NET Framework, it is sent when the host stops the server's hosted service.

The same token is also canceled when a running job is deleted or moved to another state, for example from the dashboard; see [Cancellation](../jobs/cancellation.md).

## Things to know

- **Queue names** may contain only lowercase letters, digits, `_` and `-`. Creating a job for any other name throws `ArgumentException`; a server whose `Queues` contains one logs a warning and never receives jobs for it. The SQL Server storage stores at most 50 characters.
- **A queue no server listens to** keeps its jobs in Enqueued forever. The dashboard's Queues page shows them; add the queue to a server's `Queues`.
- **`[Queue]` wins over the queue you pass when creating the job.** The `queue` argument of `Enqueue`, `Schedule` and `ContinueJobWith` is used only when no `Queue` attribute or filter picks another queue for the job.
- **Retries keep the job's own queue.** A failed job that `AutomaticRetry` reschedules goes back to its `[Queue]` attribute, or to the queue it was created with: the `queue` argument of `Enqueue`, `Schedule` and `ContinueJobWith` becomes part of the job. A queue named only by the Enqueued state isn't kept: a job created with `Create(job, new EnqueuedState("emails"))` is retried on `default`. A recurring job registered with `AddOrUpdate(id, queue, ...)`, or given a queue with `UpdateSchedule`, the dashboard's **Edit** dialog or a [manifest](../recurring-jobs/recurring-job-manifest.md), carries that queue in its job on storages that support per-job queues (SQL Server does), so its runs are retried on that queue. On other storages, and with the obsolete `AddOrUpdate` overloads that take the queue after the time zone, only the first enqueue of each run uses it: retries go to `default` unless the method has `[Queue]`.
- **One queue implementation per server.** With SQL Server, all queues a server listens to must use the same queue provider; mixing providers throws `InvalidOperationException` when the workers fetch.
- **Pausing a queue** without stopping servers is done with [drain mode](drain-mode.md#pause-one-queue).

## See also

- [Server options](server-options.md)
- [Drain mode](drain-mode.md)
- [Behavior filters](../filters/behavior.md) (the `Queue` attribute)
- [Concurrency filters](../filters/concurrency.md)
- [Cancellation](../jobs/cancellation.md)
- [Fire-and-forget jobs](../jobs/fire-and-forget.md)
- [Delayed jobs](../jobs/delayed-jobs.md)
