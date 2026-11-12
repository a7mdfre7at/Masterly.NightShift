# Exception handlers

An exception handler sees the exceptions of every background job in one place, so you can report them to an error tracker, count them, or log them in your own format, without a filter attribute on each job.

## Quick example

```csharp
using Masterly.NightShift;

public class SentryJobExceptionHandler(IHub sentry) : IJobExceptionHandler
{
    public void OnJobException(JobExceptionContext context)
    {
        sentry.CaptureException(context.Exception, scope =>
        {
            scope.SetTag("job.id", context.BackgroundJob.Id);
            scope.SetTag("job.retry", context.RetryCount.ToString());
        });
    }

    public void OnJobCanceled(JobExceptionContext context)
    {
    }
}

builder.Services.AddNightShiftJobExceptionHandler<SentryJobExceptionHandler>();
builder.Services.AddNightShiftServer();
```

Without dependency injection, register an instance or two delegates:

```csharp
GlobalConfiguration.Configuration.UseJobExceptionHandler(new SentryJobExceptionHandler(hub));

GlobalConfiguration.Configuration.UseJobExceptionHandler(
    onException: context => logger.LogError(context.Exception, "Job {JobId} failed", context.BackgroundJob.Id),
    onCanceled: context => logger.LogInformation("Job {JobId} was canceled", context.BackgroundJob.Id));
```

## When the handler is called

| Method | Called when |
|---|---|
| `OnJobException` | An attempt of a job failed: the job method threw, or a server filter gave up before calling it (a [`Throttle`](../filters/concurrency.md) slot or a lock that didn't come in time). It is called for every failed attempt, also the ones [`AutomaticRetry`](../filters/retries.md) will retry, so a job retried three times reaches it four times. A job that threw [`TerminateJobException`](../filters/retries.md#terminatejobexception) with the outcome `Failed` reaches it too. |
| `OnJobCanceled` | A run was canceled: a state change aborted it (the job was deleted or requeued while it ran), the server shut down while it ran, or the job ended with `TerminateJobOutcome.Canceled`, which includes `[JobTimeout(OnTimeout = JobTimeoutAction.Cancel)]`. |

Neither is called for a job that succeeded, a run that threw [`RetryLaterException`](../filters/retries.md#retrylaterexception), a run that ended as skipped (`TerminateJobException` with `Skipped`, or [`[SkipIfLate]`](../filters/timing.md#skipiflateattribute)), an exception a server filter marked as handled, or a run a filter canceled without an exception.

`JobExceptionContext` has:

| Member | Meaning |
|---|---|
| `Exception` | The exception the job method threw, not the `JobPerformanceException` that wraps it on its way to the server filters. |
| `BackgroundJob` | The job: `Id`, `CreatedAt` and `Job` (type, method, arguments). |
| `RetryCount` | How many times the job was retried before this attempt; 0 on its first run. |
| `PerformContext` | The whole context of the run: job parameters, the storage connection, the [console](../jobs/progress-and-console.md), `ScheduledFor` and `Lateness`. |

## How it works

`UseJobExceptionHandler` adds a `JobExceptionHandlerFilter` to the global filters; each call adds a handler. Adding the same handler instance again, or the same delegates, does nothing, so configuring Masterly.NightShift twice doesn't call a handler twice per exception. `AddNightShiftJobExceptionHandler<T>()` registers `T` as a scoped `IJobExceptionHandler`, and every server added with `AddNightShiftServer` in that service collection puts one filter in front of its filters that creates a scope for each call and calls every registered handler, so a handler can depend on scoped services such as a `DbContext`. Adding the same handler type twice adds it once; the servers pick up handlers added before or after `AddNightShiftServer`.

The filter runs in the server filters' `OnPerformed`, synchronously on the worker thread, after the job ran and before its state changes: until the handler returns, the worker takes no other job and the job's state doesn't change. Handlers must be fast: hand slow work, such as a request to an error tracker, to a queue or a client that sends in the background. An exception a handler throws is logged as a warning and ignored, and one handler that throws doesn't keep the other registered handlers from running.

To limit a handler to some jobs, add a `JobExceptionHandlerFilter` to a filter collection of your own, or check `context.BackgroundJob.Job.Type` in the handler.

## Things to know

- **A handler isn't a durable queue.** It runs in the server's process; if the process dies while the handler runs, the call is lost. Send what must not be lost to a durable store, or create a job for it.
- **Final failures have an alert.** To be told once when a job finally fails rather than on every attempt, use [alerts](alerts-and-webhooks.md), which fire when a job reaches the Failed state.
- **It sees server-side failures only.** An exception while creating a job is thrown to the code that creates it, and client filters see it as `IClientExceptionFilter`.

## See also

- [Alerts and webhooks](alerts-and-webhooks.md)
- [Retries](../filters/retries.md)
- [Custom filters](../filters/custom-filters.md)
- [Logging](logging.md)
