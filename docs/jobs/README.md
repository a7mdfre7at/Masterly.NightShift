# Jobs

A background job is a method call that Masterly.NightShift stores and runs later on a server: right away (fire-and-forget), at a given time (delayed), after other jobs (continuations, fan-in, batches), or on a schedule (see [Recurring jobs](../recurring-jobs/README.md)). This section covers how to create one-off jobs, what you can pass to them, how they are canceled, how to find and organize them with tags and notes, how to run them from the dashboard, and how to manage them once they exist.

| Page | What it covers |
|---|---|
| [Fire-and-forget jobs](fire-and-forget.md) | `Enqueue` in all its forms, queues, `Create` with an explicit state, `BackgroundJobClient`, why a job ID can be `null` |
| [Delayed jobs](delayed-jobs.md) | `Schedule` with a `TimeSpan`, a `DateTimeOffset` or a plain-English phrase such as `"tomorrow at 9am"` (`TimeText`) |
| [Continuations](continuations.md) | `ContinueJobWith`, `JobContinuationOptions`, passing a parent's result or exception with `[FromResult]` / `[FromException]` |
| [Fan-in and batches](fan-in-and-batches.md) | `ContinueJobWithAll` / `ContinueJobWithAny`, and `BatchJob` for groups of jobs with progress and a continuation |
| [Job arguments](job-arguments.md) | What can be passed, how arguments are serialized, how the target instance is created, injected parameters, async methods, return values |
| [Cancellation](cancellation.md) | `CancellationToken` and `IJobCancellationToken`, server shutdown, deleting a running job |
| [Progress, console and checkpoints](progress-and-console.md) | `IJobConsole`: progress bars, console lines, checkpoints and resumable steps (`Resume`), what happens when the storage fails |
| [Tags and notes](tags-and-notes.md) | `[Tag]` and `JobTags` to find jobs by what they are about, `JobNotes` for notes and acknowledgements |
| [Job catalog](job-catalog.md) | The dashboard's Catalog page and the `JobCatalog` API: run any job method from the browser |
| [Managing jobs](managing-jobs.md) | `Delete`, `Requeue`, `Reschedule`, `ChangeState`, every job state, querying jobs through the monitoring API |
| [Source-generated invokers](source-generated-invokers.md) | How job methods are called without reflection, `[GenerateJobInvokers]`, turning generation off |
| [Testing](testing.md) | Unit tests for code that creates jobs, job classes and filters; integration tests against LocalDB |

## The most common example

Inject `IBackgroundJobClient` and enqueue a call to one of your services. The server creates the service through dependency injection and calls the method in the background:

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

Outside dependency injection, the static facade does the same: `BackgroundJob.Enqueue<EmailService>(x => x.SendWelcome(id));`. Both need storage and a server to be configured first, see [Quick start: ASP.NET Core](../getting-started/quick-start-aspnetcore.md) or [Quick start: console app or Worker Service](../getting-started/quick-start-worker.md).

## See also

- [Core concepts](../getting-started/core-concepts.md)
- [Recurring jobs](../recurring-jobs/README.md)
- [Filters](../filters/README.md)
- [Queues and workers](../server/queues-and-workers.md)
