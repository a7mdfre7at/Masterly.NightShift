# Progress, console and checkpoints

A running job can tell the dashboard what it is doing: a progress bar, console lines with a severity, and named checkpoints that mark the steps it finished. Checkpoints also make a job resumable: when it fails and is retried, it skips the steps an earlier attempt already finished. All of this goes through `IJobConsole` (namespace `Masterly.NightShift.Server`).

## Quick example

Add an `IJobConsole` parameter to the job method and pass `null!` when you create the job. The server passes the real console when the job runs.

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;

public class ImportJob
{
    public async Task RunAsync(string file, IJobConsole console, CancellationToken cancellationToken)
    {
        string[] rows = await File.ReadAllLinesAsync(file, cancellationToken);

        for (int i = 0; i < rows.Length; i++)
        {
            await SaveAsync(rows[i], cancellationToken);

            if (i % 100 == 0)
            {
                console.SetProgress(100.0 * i / rows.Length, $"Row {i} of {rows.Length}");
            }
        }

        console.SetProgress(100, "Done");
        console.WriteLine($"Imported {rows.Length} rows");
    }

    private Task SaveAsync(string row, CancellationToken cancellationToken) => Task.CompletedTask;
}

jobs.Enqueue<ImportJob>(x => x.RunAsync("orders.csv", null!, CancellationToken.None));
```

The job details page shows the progress bar, the console lines and the checkpoint timeline live while the job runs, and the Processing page shows the progress of every running job.

## Getting the console

- **As a job parameter.** Declare a parameter of type `IJobConsole` and pass `null!` (or `null` without nullable reference types) when you create the job. The argument isn't serialized; see [Job arguments](job-arguments.md#some-parameters-are-filled-in-by-the-server).
- **From a filter or an interceptor.** `PerformContext.Console` (also on `PerformingContext` and `PerformedContext`, and `JobInvocation.Context.Console` in an interceptor) is the same console the job gets. Every context of one run shares it.

The console can be used from several threads at once. Calls run one at a time.

## Members

### IJobConsole

| Member | What it does |
|---|---|
| `SetProgress(double percent, string? message = null)` | Reports how far the job got, 0 to 100, with an optional short description of the current step |
| `WriteLine(string message)` | Writes an `Info` line |
| `WriteLine(JobConsoleLevel level, string message)` | Writes a line with a severity: `Debug`, `Info`, `Warning` or `Error` |
| `Checkpoint(string name)` | Records that the step `name` was reached. The dashboard shows it on the timeline with how long each step took |
| `Checkpoints` (`IReadOnlyList<string>`) | The checkpoints recorded by earlier attempts of this job and by this one so far, in the order they were first recorded |
| `HasCheckpoint(string name)` | Whether a checkpoint with this name was recorded, by this attempt or an earlier one |

### JobConsoleExtensions

| Member | What it does |
|---|---|
| `Resume(this IJobConsole console, string checkpoint, Action step)` | Runs `step` unless `checkpoint` is already recorded, then records `checkpoint`. A skipped step writes the line "Skipped '{checkpoint}': already done by an earlier attempt" |
| `ResumeAsync(this IJobConsole console, string checkpoint, Func<Task> step)` | The same for an asynchronous step |

### JobConsole (static members)

| Member | Default | What it does |
|---|---|---|
| `DefaultMaxLines` | `500` | How many console entries (lines and checkpoints) a job keeps. Older ones are dropped. `0` or less keeps everything. The checkpoints `Resume` reads are kept apart and never dropped |
| `MaxMessageLength` | `4000` | The longest line, progress message or checkpoint name kept, in characters. Longer ones are cut. `0` or less keeps everything |
| `ReadEntries(IStorageConnection connection, string jobId)` | | A job's console entries (`JobConsoleEntry`: `Timestamp`, `Sequence`, `Level`, `Message`, `Checkpoint`), oldest first. Empty when the storage can't read lists |
| `ReadProgress(IStorageConnection connection, string jobId)` | | The last `JobProgress` (`Percent`, `Message`, `Timestamp`) a job reported, or `null` |
| `FromTimestamp(long milliseconds)` | | Converts an entry's or a progress report's Unix millisecond timestamp to a UTC `DateTime` |
| `GetConsoleKey(string jobId)`, `GetCheckpointsKey(string jobId)` | | The storage list keys: `job:{id}:console` and `job:{id}:checkpoints` |
| `ProgressParameter`, `MarkerParameter` | | The job parameters the console writes: `Progress`, and `Console`, which marks a job whose console wrote lines or checkpoints |

`DefaultMaxLines` and `MaxMessageLength` apply to every job in the process. Set them once at startup:

```csharp
using Masterly.NightShift.Server;

JobConsole.DefaultMaxLines = 2000;
JobConsole.MaxMessageLength = 1000;
```

## Progress

`SetProgress` stores one value in the job's `Progress` parameter and overwrites it on every call. The percentage is rounded to one decimal and kept between 0 and 100; `NaN` counts as 0. A call that changes neither the percentage (by 0.05 or more) nor the message writes nothing.

## Console lines

Each line is one entry in the storage list `job:{id}:console`, with its time, its severity and its order. The list keeps the newest `DefaultMaxLines` entries. Checkpoints are entries of this list too, so the timeline on the dashboard shows lines and steps together.

```csharp
console.WriteLine("Downloading the feed");
console.WriteLine(JobConsoleLevel.Warning, "3 rows skipped: no customer number");
console.WriteLine(JobConsoleLevel.Error, "The partner API refused the upload, retrying");
```

`WriteLine` with a `null` message throws `ArgumentNullException`. `[LogInvocation]` writes the start, the end and any exception of a job method to this console without code in the job, see [Logging](../monitoring/logging.md#loginvocation).

## Checkpoints and resumable jobs

A checkpoint is a name that says a step is done. Record one after each step, and an attempt that runs again after a failure can tell which steps it may skip. `Resume` and `ResumeAsync` do both:

```csharp
using Masterly.NightShift.Server;

public class MonthlyReport
{
    public async Task BuildAsync(int month, IJobConsole console)
    {
        await console.ResumeAsync("collect", () => CollectAsync(month));
        await console.ResumeAsync("render", () => RenderAsync(month));
        console.Resume("archive", () => Archive(month));
    }

    private Task CollectAsync(int month) => Task.CompletedTask;

    private Task RenderAsync(int month) => Task.CompletedTask;

    private void Archive(int month)
    {
    }
}
```

When `RenderAsync` throws, the job fails and `[AutomaticRetry]` retries it. The retry skips `collect`, runs `render` again from its start, and then `archive`. Make each step safe to repeat from its start: a crash in the middle of a step runs the whole step again.

Use `HasCheckpoint` and `Checkpoint` directly when a step doesn't fit in a delegate:

```csharp
if (!console.HasCheckpoint("charged"))
{
    payments.Charge(orderId);
    console.Checkpoint("charged");
}
```

Names are compared exactly (ordinal, case-sensitive). Recording a name a second time adds a line to the console but doesn't add it to `Checkpoints` again.

### When checkpoints are kept

| What happens to the job | Checkpoints |
|---|---|
| It fails and is retried (`[AutomaticRetry]`, `RetryLaterException`) | Kept: the retry skips the finished steps |
| A failed job is requeued from the dashboard or with `BackgroundJob.Requeue` | Kept |
| The server shuts down while it runs and the job runs again | Kept |
| A succeeded or deleted job is requeued | Cleared: it runs every step again |
| It reaches a final state | They expire with the job's data |

### Storage requirement

Checkpoints are read from the list `job:{id}:checkpoints`. Reading a list needs a storage whose connection derives from `JobStorageConnection` and implements `GetAllItemsFromList` (the extended API, see [Writing a custom storage](../storage/custom-storage.md)). SQL Server does. On a storage that can't read lists, `Checkpoints` is empty and `HasCheckpoint` returns `false`, so `Resume` runs every step on every attempt. The lines are still written, and a storage without `JobStorageFeatures.ExtendedApi` keeps the lists after the job expires.

## When the storage fails

Progress and lines are best-effort. When the storage fails to write them (a timeout, a lost connection), `SetProgress` and `WriteLine` drop the write and the job carries on: a job that did its work must not fail, and run again, because a console line couldn't be saved. A warning with the error goes to the application log the first time, then at most once a minute for the same run of the job, with the number of writes dropped since the last warning. A dropped progress report is sent again by the next `SetProgress` call, even with the same value.

Checkpoints are not best-effort. `Checkpoint`, `Checkpoints`, `HasCheckpoint`, `Resume` and `ResumeAsync` throw the storage's exception, so the attempt fails and is retried. Treating a failed read as "no checkpoints" would redo finished steps, and losing a checkpoint write would redo the step on the next attempt.

## Things to know

- **Each call is a storage round trip.** Every `WriteLine`, `Checkpoint` and changed `SetProgress` writes to the storage, on a short-lived connection of its own. Report progress in steps (every 100 rows, every file), not for every item.
- **Writes after the run ended are dropped**, and so are writes after the job's cancellation token found the job deleted or requeued while it ran. They would land in lists that are already expiring.
- **The data lives as long as the job.** The global `JobConsoleStateFilter` expires the console and checkpoint lists with the job when it reaches a final state (or a failed state with a retention period, see [Timing](../filters/timing.md)), and keeps them again when the job is requeued. Only jobs whose console wrote to storage are touched.
- **The console is not the application log.** Lines go to the job's storage and the dashboard. Use `ILogger<T>` for the application log, see [Log from inside a job](../monitoring/logging.md#log-from-inside-a-job).

## Reading the console from code

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

using (IStorageConnection connection = JobStorage.Current.GetConnection())
{
    foreach (JobConsoleEntry entry in JobConsole.ReadEntries(connection, jobId))
    {
        DateTime time = JobConsole.FromTimestamp(entry.Timestamp);
        Console.WriteLine(entry.Checkpoint != null
            ? $"{time:u} reached {entry.Checkpoint}"
            : $"{time:u} {entry.Level}: {entry.Message}");
    }

    JobProgress? progress = JobConsole.ReadProgress(connection, jobId);
    Console.WriteLine($"{progress?.Percent ?? 0}% {progress?.Message}");
}
```

## See also

- [Job arguments](job-arguments.md)
- [Cancellation](cancellation.md)
- [Retries](../filters/retries.md)
- [Logging](../monitoring/logging.md)
