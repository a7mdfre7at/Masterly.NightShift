# Playground

A page with a button for every feature of Masterly.NightShift except the storages, to learn it by trying it.

```
dotnet run --project samples/Playground
```

Open http://localhost:5080 (the launch profile opens it for you). Each card says what the feature is, shows the code
that uses it, and says where to look in the dashboard (`/nightshift`, links open in a new tab). The event log on the
right shows what happens outside the dashboard: alerts, the webhook payloads the app sends to itself, the job exception
handler, job functions and OpenTelemetry spans.

Jobs are kept in memory (`UseInMemoryStorage`), so nothing needs installing and every restart starts clean. If you run
it on another URL, set `Playground:Url` so the webhook alerts reach `/webhook-inbox`.

The sections, in the order to try them the first time:

1. Start here: the dashboard
2. Creating jobs: fire and forget, delayed, plain-English delays, queues, bulk, results, initial states and parameters
3. Progress and console: progress bars, console lines, checkpoints that resume after a crash, ILogger capture
4. Failures and retries: automatic retries, retry later, terminating a job, in-process retries, timeouts, cancellation
5. Workflows: continuations, results passed on, fan-in, batches, chains (with failures, schedules and CRON)
6. Job classes and functions: `IJob`, typed handlers, job functions by name, the Catalog
7. Scheduling rules: schedule policies, late starts, run windows, throttling, rate limits, duplicates, circuit
   breakers, deadlines, long-running jobs, display names, redaction, tags and notes
8. Recurring jobs: CRON schedules, trigger/pause/resume, jobs declared in code, plain-English CRON, forecast, export
9. Managing jobs: reschedule, wait for a result, delete pending jobs, requeue failed jobs
10. Monitoring: job monitors, OpenTelemetry, insights, activity, timeline, audit, health checks and metrics
11. Operations: drain mode, two servers with their busy workers, a paused server whose heartbeat is late, stopping
    the servers, a read-only dashboard, the About page and the command palette

The app runs two servers: `playground` (8 workers, queues `critical` and `default`, plus a worker for long-running
jobs) and `playground-reports` (3 workers, queues `reports` and `default`). The step "A paused server whose heartbeat
is late" adds a third, `playground-stalled:{n}`, through the storage API (`IStorageConnection.AnnounceServer`, then
`DrainMode.PauseServer`, as the Servers page pauses a server) and never sends it a heartbeat: the Servers page says
Paused at once, and Possibly aborted after a minute (`DashboardOptions.ServerPossiblyAbortedThreshold`). The server
watchdog removes it 5 to 10 minutes later (`ServerTimeout` and `ServerCheckInterval`, 5 minutes each) and leaves its
pause behind; the step after it removes the server and its pause at once. The configuration that turns all of this
on is in `Program.cs`; the jobs are in `Jobs.cs`, and each step is an
`Experiment` in `Experiments.cs`.
