# Core concepts

This page is the mental model behind Masterly.NightShift: what a job is, where it is kept, who creates and who runs it, which states it goes through, and what the guarantees are. Read it once before the detail pages; most surprises with background jobs come from one of the points here.

## Quick example

The four kinds of jobs, created through the injected client:

```csharp
using Masterly.NightShift;

public class JobTypes
{
    private readonly IBackgroundJobClient _jobs;
    private readonly IRecurringJobManager _recurringJobs;

    public JobTypes(IBackgroundJobClient jobs, IRecurringJobManager recurringJobs)
    {
        _jobs = jobs;
        _recurringJobs = recurringJobs;
    }

    public void CreateJobs(int userId, int orderId)
    {
        // Fire-and-forget: Enqueued now, runs as soon as a worker is free
        _jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));

        // Delayed: Scheduled now, Enqueued in 30 minutes
        _jobs.Schedule<EmailService>(x => x.SendWelcome(userId), TimeSpan.FromMinutes(30));

        // Continuation: Awaiting until the parent succeeds
        var chargeId = _jobs.Enqueue<OrderService>(x => x.Charge(orderId));
        if (chargeId != null)
        {
            _jobs.ContinueJobWith<OrderService>(chargeId, x => x.SendReceipt(orderId));
        }

        // Recurring: a schedule that creates a new fire-and-forget job each time it fires
        _recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6));
    }
}
```

The static `BackgroundJob` and `RecurringJob` classes offer the same methods without injection.

## How it works

### A job is a method call, written down

When you call `Enqueue<EmailService>(x => x.SendWelcome(userId))`, the lambda is not run. Masterly.NightShift reads the expression and stores:

- the type (`EmailService`) and the method (`SendWelcome`, with its parameter types),
- the argument values, evaluated now and serialized to JSON,
- the queue, the creation time, any job parameters, and the first state.

Later, a worker in some server process reads this record, creates an `EmailService`, deserializes the arguments and calls the method. That process can be a different one, on a different machine, after a restart. So:

- Arguments are copied at creation time. Pass small, serializable values, typically IDs, and load current data inside the job. A whole entity is a snapshot that may be stale when the job runs.
- The method must be public, must not be `async void`, and can't have `out`, `ref` or delegate parameters.
- Every process that runs the job needs the same type and method. Renaming or moving a job method breaks jobs that are already stored (see [Troubleshooting](../troubleshooting.md#a-job-fails-because-it-cant-be-loaded)).

Instance methods are called on an object created by the job activator: with `AddNightShift` in a `Microsoft.Extensions` host, that's the service container, in a new scope per job; without it, `Activator.CreateInstance`. Static methods need no instance. Methods can return `Task` and take a `CancellationToken`. See [Job arguments](../jobs/job-arguments.md).

### Storage is the source of truth

Jobs, their state history, queues, recurring job definitions, server heartbeats and statistics all live in the storage, a `JobStorage` implementation. `Masterly.NightShift.SqlServer` keeps them in SQL Server tables in the `HangFire` schema. Because everything is in the database:

- Jobs survive restarts and deploys of every process.
- Clients and servers don't talk to each other; they only talk to the storage. You can create jobs in one app and process them in another.
- Several servers can share one database to spread the load.

See [Storage](../storage/README.md).

### Clients create, servers process

The **client** is any code that creates or changes jobs: `IBackgroundJobClient` / `BackgroundJob` for jobs, `IRecurringJobManager` / `RecurringJob` for recurring jobs. Creating a job is a single database write; it doesn't need a server to be running.

The **server** (`BackgroundJobServer`, or `AddNightShiftServer()` in a host) runs a set of background processes:

| Process | What it does |
|---|---|
| Workers (`WorkerCount` of them) | Fetch a job from the queues the server listens to, move it to Processing, run it, move it to Succeeded or Failed |
| Delayed job scheduler | Moves Scheduled jobs whose time has come to Enqueued (checks every `SchedulePollingInterval`, 15 seconds by default) |
| Recurring job scheduler | Creates a job from each recurring job whose next occurrence has come |
| Heartbeat and watchdog | Report this server as alive every 30 seconds; remove servers that stopped reporting for 5 minutes |
| Storage processes | Storage housekeeping; for SQL Server, deleting expired data and aggregating counters |

No server, no processing: jobs stay Enqueued and Scheduled until one starts. See [Server](../server/README.md) and [Queues and workers](../server/queues-and-workers.md).

### States

Every job is in exactly one state, and every change of state is recorded in its history, which the dashboard shows. The built-in states:

| State | Meaning | Final |
|---|---|---|
| Enqueued | Waiting in a queue for a free worker | No |
| Scheduled | Waiting for a point in time: a delayed job, or a failed job waiting for its next retry | No |
| Awaiting | A continuation waiting for its parent job (or jobs) to finish | No |
| Processing | A worker is running it | No |
| Succeeded | The method returned without an exception | Yes |
| Failed | The method threw, and no retry is left | No |
| Deleted | Removed on purpose: by you, by the dashboard, or by a filter | Yes |

How a job moves between them:

```
 Enqueue() -----------------------+
                                  |
                                  v
 Schedule() --> Scheduled --> Enqueued --> Processing --> Succeeded
                    ^  (due)      ^             |
                    |             |             | exception
                    |             |             v
                    +-------------|------ retries left?
                         yes      |             | no
                                  |             v
 ContinueJobWith() --> Awaiting --+          Failed
                     (parent finished)
```

And, from almost anywhere:

- Any job that isn't finished can be **deleted**: `Delete` on the client, the dashboard, `AttemptsExceededAction.Delete`, or a continuation whose parent ended in a state it doesn't wait for.
- Failed, Succeeded and Deleted jobs can be **requeued** (moved back to Enqueued) from the dashboard or with `Requeue`.

Succeeded and Deleted jobs are final: they are kept for one day (`JobStorage.JobExpirationTimeout`) and then removed. Failed jobs are not final and stay until you requeue or delete them. Filters can change the next state during a transition, which is how retries work. See [Managing jobs](../jobs/managing-jobs.md).

### Queues

An enqueued job sits in a named queue, `default` unless you choose another one, for example with `[Queue("critical")]` on the method or an overload that takes a queue name. A server only takes jobs from the queues in its `Queues` option, which is `{ "default" }` by default. Queues let you give some work its own servers or workers, or keep slow jobs from blocking fast ones. A job in a queue that no running server listens to waits forever. Queue names are lowercase letters, digits, underscores and dashes. See [Queues and workers](../server/queues-and-workers.md).

### Filters, and retries

Filters are attributes or global objects that run around job creation, job execution and state changes. Most built-in behaviour is a filter. The important one to know from day one is `AutomaticRetry`: it is registered globally, and when a job throws it turns the Failed state into Scheduled, with a delay that grows with each attempt (about 15 seconds at first, a couple of hours for the last), up to 10 retries. Only then does the job end up Failed. You can change the number of attempts or turn retries off per method:

```csharp
public class InvoiceService
{
    [Queue("critical")]
    [AutomaticRetry(Attempts = 3)]
    public void Send(int invoiceId)
    {
    }
}
```

Other filters add timeouts, throttling, rate limits, duplicate prevention and more. See [Filters](../filters/README.md) and [Retries](../filters/retries.md).

### At-least-once execution

A job is guaranteed to run at least once, not exactly once. It can run more than once when:

- it threw and was retried (the first attempt may have done part of the work before the exception);
- the server was stopped or crashed while the job was running, so it went back to its queue and another worker ran it again (with SQL Server, a job held by a crashed server becomes available again after `SlidingInvisibilityTimeout`, 5 minutes by default);
- the process stopped after the work was done but before Succeeded was recorded.

So write jobs that are safe to run twice (idempotent): check whether the work was already done, use upserts instead of inserts, and pass an idempotency key to external APIs that support one. Filters such as `[DisableConcurrentExecution]`, `[PreventDuplicates]` and `[IdempotentCompletion]` help in specific cases (see [Concurrency](../filters/concurrency.md)), but they don't replace an idempotent job.

### The dashboard

The dashboard is a web UI served by `Masterly.NightShift.AspNetCore` that reads the same storage. It shows live and historical counts, every job by state with its arguments, history and exception details, the recurring jobs, the servers and their workers, and lets you requeue, delete and trigger jobs. The details page of a job also explains why a job isn't running, for example when no server listens to its queue. It only needs the storage, so it can run in an app that doesn't process jobs. See [Dashboard](../dashboard/README.md).

## Options / API

The main types and where they are covered:

| Type | Role | Details |
|---|---|---|
| `IBackgroundJobClient`, `BackgroundJob` | Create, delete, requeue and change the state of jobs | [Jobs](../jobs/README.md) |
| `IRecurringJobManager`, `RecurringJob` | Add, update, trigger, pause and remove recurring jobs | [Recurring jobs](../recurring-jobs/README.md) |
| `BackgroundJobServer`, `BackgroundJobServerOptions`, `AddNightShiftServer` | Process jobs | [Server options](../server/server-options.md) |
| `JobStorage`, `SqlServerStorage` | Keep jobs | [Storage](../storage/README.md) |
| `GlobalConfiguration`, `AddNightShift` | Configure storage, serialization, logging, filters | [Global configuration](../configuration/global-configuration.md) |
| `JobFilterAttribute`, `GlobalJobFilters` | Change behaviour around creation, execution and state changes | [Custom filters](../filters/custom-filters.md) |
| `EnqueuedState`, `ScheduledState`, `AwaitingState`, `ProcessingState`, `SucceededState`, `FailedState`, `DeletedState` (namespace `Masterly.NightShift.States`) | The built-in states | [Managing jobs](../jobs/managing-jobs.md) |
| `JobActivator` | Create job class instances | [Dependency injection](../server/dependency-injection.md) |

## More examples

### Pass IDs, not objects

```csharp
var user = new User { Id = 42 };

// Stored: the type EmailService, the method SendWelcome(int), and the argument 42
_jobs.Enqueue<EmailService>(x => x.SendWelcome(user.Id));
```

`user.Id` is evaluated when `Enqueue` is called, so the job stores `42`, not a reference to `user`.

### Make a job safe to run twice

```csharp
public class PaymentService
{
    private readonly IPaymentGateway _gateway;
    private readonly IPaymentRepository _payments;

    public PaymentService(IPaymentGateway gateway, IPaymentRepository payments)
    {
        _gateway = gateway;
        _payments = payments;
    }

    public async Task Capture(int paymentId)
    {
        var payment = await _payments.Find(paymentId);
        if (payment.Captured)
        {
            return; // a previous run already did the work
        }

        // The idempotency key makes the gateway ignore a second capture of the same payment
        await _gateway.Capture(payment.Amount, idempotencyKey: $"payment-{paymentId}");
        await _payments.MarkCaptured(paymentId);
    }
}
```

If the process dies between the capture and `MarkCaptured`, the job runs again, and the gateway recognizes the key instead of charging twice.

## Things to know

- Creating a job inside your own database transaction (a `DbContext` transaction or a `SqlTransaction`) doesn't make it part of that transaction: the storage writes through its own connection. If your transaction rolls back, the job still exists. Create the job after you commit, or make the job check that its data exists.
- Recurring jobs are not jobs: a recurring job is a schedule. Each time it fires it creates an ordinary job, which you'll find under **Jobs** in the dashboard.
- Job IDs returned by `Enqueue`, `Schedule` and `ContinueJobWith` are `string?`: `null` means a client filter canceled the creation.
- Time in Masterly.NightShift is UTC unless you say otherwise. Recurring jobs run in UTC unless you set `RecurringJobOptions.TimeZone` (see [Time zones and misfires](../recurring-jobs/time-zones-and-misfires.md)).

## See also

- [Quick start: ASP.NET Core](quick-start-aspnetcore.md)
- [Quick start: console and Worker Service](quick-start-worker.md)
- [Fire-and-forget jobs](../jobs/fire-and-forget.md), [Delayed jobs](../jobs/delayed-jobs.md), [Continuations](../jobs/continuations.md)
- [Creating recurring jobs](../recurring-jobs/creating-recurring-jobs.md)
- [Retries](../filters/retries.md), [Concurrency](../filters/concurrency.md)
- [Troubleshooting](../troubleshooting.md)
