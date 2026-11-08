# Drain mode

Drain mode pauses job processing without stopping any server: running jobs finish, and jobs that workers pick up are held and tried again later. Turn it on before a deployment or a database maintenance window, pause a single queue when an external service is down, or pause one server before you take its machine offline.

## Quick example

```csharp
using Masterly.NightShift;

// Before the deployment
using (var connection = storage.GetConnection())
{
    DrainMode.Enable(connection, "deploying 2.4.0");
}

// After the deployment
using (var connection = storage.GetConnection())
{
    DrainMode.Disable(connection);
}
```

`storage` is the `JobStorage` (inject it, or use `JobStorage.Current`). The same switch is on the dashboard's Servers page as **Pause processing**, with an optional reason.

## How it works

Drain mode is stored in the storage, in the `hangfire:control` hash (the name kept from the earlier library so existing data keeps working). It has three independent parts:

- the **global switch**, which holds every job;
- **paused queues**, which hold the jobs of those queues only;
- **paused servers**, which make one server hold the jobs it picks up.

Nothing changes in the way jobs are created, scheduled or enqueued, apart from putting a held job back in its queue. The work happens when a worker picks a job up: the worker asks to move the job to Processing, and `DrainModeFilter`, an election filter registered globally by default, checks the switch. If the job must be held, the filter replaces Processing with Scheduled for `DrainMode.HoldInterval` (30 seconds), with one of these reasons:

| Reason | When |
| --- | --- |
| `Held: processing is paused` | The global switch is on. |
| `Held: server '{id}' is paused` | The server whose worker picked the job is paused. |
| `Held: queue '{name}' is paused` | The job's queue is paused. |

The worker then removes the job from the queue and moves on to the next one. When the hold is over, the delayed job scheduler puts the job back in its queue (within `SchedulePollingInterval`, 15 seconds by default), and a worker looks at it again. While the pause lasts, the job goes around this loop; once the pause ends, it runs normally. Jobs that were already running when you paused are not touched and finish normally.

The job's queue is found the way the Enqueued state finds it, so every way of choosing a queue counts: `BackgroundJob.Enqueue("emails", ...)` (or a recurring job registered with the `emails` queue), `[Queue("emails")]`, and `new EnqueuedState("emails")`. A job created with a queue of its own goes back to that queue after the hold. A job whose queue came only from its Enqueued state (`new EnqueuedState("emails")`) would go back to `default`, so the filter remembers its queue in the `HeldQueue` job parameter (`DrainModeFilter.HeldQueueParameter`) and puts the job back in `emails` when the hold ends. The parameter is cleared at that point, so a later retry of the job goes where it would have gone without the hold: to `default`, because the delayed job scheduler enqueues retries with a new Enqueued state. `RunWindow`, `Throttle`, `RateLimit` and `CircuitBreaker` keep the queue of the jobs they hold in the same way, and put those jobs back in their queue themselves, so that keeps working when `DrainModeFilter` is removed.

A job that was running when its server stopped (the shutdown reached `ShutdownTimeout`) or crashed goes back to its queue still in the Processing state, which names no queue. When the next worker picks it up, and only when something needs the queue (a queue is paused, or the global switch or a paused server holds the job), the filter reads the job's history and takes the queue of its last Enqueued state. So such a job is held by a paused `emails` queue whether its queue came from `[Queue("emails")]` or from `new EnqueuedState("emails")`, and goes back to `emails` after the hold. This costs one more read, on that path only; jobs picked up from their Enqueued state don't need it.

Each process caches the switch for `DrainMode.CacheDuration` (2 seconds), one cache per storage, so checking it costs one small read every couple of seconds per process and storage, not one per job. A change made in one process is seen by the others within that time.

## API

All members are static members of `DrainMode`. Every method except `ClearStaleServerPauses` takes an open `IStorageConnection`, which you get from `JobStorage.GetConnection()` and dispose when done.

| Member | Type / returns | Default | Meaning |
| --- | --- | --- | --- |
| `Enable(connection, reason = null)` | `void` | | Turns the global switch on and records the time and the optional reason. Running jobs finish; new ones are held. |
| `Disable(connection)` | `void` | | Turns the global switch off. Held jobs run as soon as their hold ends. Paused queues and servers stay paused. |
| `PauseQueue(connection, queue, reason = null)` | `void` | | Holds the jobs of one queue; other queues keep flowing. The name is trimmed and lowercased. |
| `ResumeQueue(connection, queue)` | `void` | | Lets the jobs of a paused queue run again. |
| `PauseServer(connection, serverId, reason = null)` | `void` | | Makes one server hold every job it picks up: the job goes to Scheduled for `HoldInterval`, then any server may take it. `serverId` is the full server ID, `{name}:{process id}:{guid}`, as shown on the Servers page; the server name alone matches nothing. |
| `ResumeServer(connection, serverId)` | `void` | | Lets a paused server work again. With the old ID of a server that has stopped, clears its leftover pause. |
| `ClearStaleServerPauses(storage)` | `IReadOnlyList<string>` | | Takes a `JobStorage`, not a connection. Clears the pause of every server that the storage no longer lists as running, and returns their IDs. Pauses of running servers stay. |
| `Read(connection)` | `DrainModeStatus` | | Reads the current state directly from the storage. |
| `GetStatus(storage, connection)` | `DrainModeStatus` | | Returns the current state of `storage`, using the cache this process keeps for that storage. `DrainModeFilter` uses it. |
| `IsActive(storage, connection)` | `bool` | | Whether the global switch of `storage` is on, using the cache kept for that storage. |
| `GetStatus(connection)` | `DrainModeStatus` | | Returns the current state, using one cache shared by every caller that doesn't name the storage. With more than one storage in a process, use `GetStatus(storage, connection)`. |
| `IsActive(connection)` | `bool` | | Whether the global switch is on, using that shared cache. |
| `InvalidateCache()` | `void` | | Forgets the cached state of every storage, so the next check reads the storage. The methods that change the state call it for you. |
| `HoldInterval` | `TimeSpan` | 30 seconds | How long a held job waits in the Scheduled state before it is enqueued again. |
| `CacheDuration` | `TimeSpan` | 2 seconds | How long a process trusts the last state it read. `TimeSpan.Zero` reads the storage on every check. |
| `HashKey` | `const string` | `"hangfire:control"` | The storage hash that holds the state. |
| `QueueFieldPrefix` | `const string` | `"Queue:"` | Prefix of the hash fields for paused queues. |
| `ServerFieldPrefix` | `const string` | `"Server:"` | Prefix of the hash fields for paused servers: `Server:{id}`. A server ID of more than 93 characters would make a field longer than the 100 characters SQL Server keeps, so it is stored as `Server:#` followed by the SHA-256 of the lowercased ID, with the full ID at the start of the value. |

`DrainModeStatus`, returned by `Read` and `GetStatus`:

| Member | Type | Meaning |
| --- | --- | --- |
| `Active` | `bool` | Whether the global switch is on. |
| `Since` | `DateTime?` | When the global switch was turned on (UTC), while it is on. |
| `Reason` | `string?` | The reason given when the global switch was turned on, while it is on. |
| `PausedQueues` | `IReadOnlyDictionary<string, string?>` | Paused queues and the reason for each. Keys are case-insensitive. |
| `PausedServers` | `IReadOnlyDictionary<string, string?>` | Paused server IDs and the reason for each. |
| `IsQueuePaused(queue)` | `bool` | Whether jobs of the queue are held, by the global switch or on their own. |
| `IsServerPaused(serverId)` | `bool` | Whether the server holds the jobs it picks up, by the global switch or on its own. Takes the full server ID. |
| `Inactive` | `DrainModeStatus` | Static. The state with nothing paused. |

### On the dashboard

| Page | Control |
| --- | --- |
| Servers | **Pause processing** (global switch, with an optional reason of up to 200 characters), **Pause** next to each server, and a **Clear** button for each pause left by a stopped server (under **Pauses left by stopped servers**). |
| Queues | **Pause** and **Resume** next to each queue. |
| Every page | A banner while the global switch is on or any queue or running server is paused. |

These controls are hidden from read-only dashboard users, and the routes behind them (`/maintenance/pause`, `/maintenance/resume`, `/servers/pause/{id}`, `/servers/resume/{id}`, `/queues/pause/{queue}`, `/queues/resume/{queue}`) refuse read-only requests. Changes made from the dashboard are recorded in the [audit log](../monitoring/audit-log-and-activity.md). The health endpoint reports `Degraded` while anything is paused (pauses left by servers that are no longer registered don't count), and the job page's diagnosis explains why a held job is waiting.

## More examples

### Drain during a deployment

A small console tool that the deployment pipeline runs before (`drain on "release 2.4.0"`) and after (`drain off`) the release:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

public static class Program
{
    public static int Main(string[] args)
    {
        var storage = new SqlServerStorage(
            Environment.GetEnvironmentVariable("JOBS_CONNECTION_STRING"));

        using (var connection = storage.GetConnection())
        {
            if (args.Length > 0 && args[0] == "on")
            {
                DrainMode.Enable(connection, args.Length > 1 ? args[1] : "deploy");
            }
            else
            {
                DrainMode.Disable(connection);
            }
        }

        return 0;
    }
}
```

Drain mode does not wait for running jobs. Before you stop the old version, check that the Processing count on the dashboard has dropped to zero, or rely on [graceful shutdown](queues-and-workers.md#graceful-shutdown) for jobs that are still running.

### Pause one queue

When the email provider is down, hold the `emails` queue and let everything else run:

```csharp
using (var connection = storage.GetConnection())
{
    DrainMode.PauseQueue(connection, "emails", "SMTP provider outage");
}

// Later
using (var connection = storage.GetConnection())
{
    DrainMode.ResumeQueue(connection, "emails");
}
```

### Pause every server on one machine

Server IDs start with the server name (by default the machine name), lowercased, followed by a colon. Look them up in the monitoring API and pause the ones you need:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Storage.Monitoring;

public static void PauseServersOn(JobStorage storage, string machineName)
{
    var prefix = machineName.ToLowerInvariant() + ":";

    using (var connection = storage.GetConnection())
    {
        foreach (ServerDto server in storage.GetMonitoringApi().Servers())
        {
            if (server.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                DrainMode.PauseServer(connection, server.Name, "patching the machine");
            }
        }
    }
}
```

### Clear pauses left by stopped servers

A server that stops while paused leaves its pause behind, under an ID that no server will use again. It holds nothing and the dashboard's health checks ignore it, but it stays in storage. Clear such entries from the Servers page, or from code, for example after a deployment:

```csharp
IReadOnlyList<string> cleared = DrainMode.ClearStaleServerPauses(storage);

foreach (var serverId in cleared)
{
    Console.WriteLine($"Cleared the pause of stopped server '{serverId}'");
}
```

A server counts as running while the storage lists it, as on the Servers page. A server that crashed is listed until the server watchdog removes it, after the server timeout (5 minutes by default), so its pause is cleared only after that.

### Show the current state

```csharp
using (var connection = storage.GetConnection())
{
    DrainModeStatus status = DrainMode.Read(connection);

    if (status.Active)
    {
        Console.WriteLine($"Processing paused since {status.Since:u}: {status.Reason}");
    }

    foreach (var queue in status.PausedQueues)
    {
        Console.WriteLine($"Queue '{queue.Key}' paused: {queue.Value}");
    }

    foreach (var server in status.PausedServers)
    {
        Console.WriteLine($"Server '{server.Key}' paused: {server.Value}");
    }
}
```

### Retry held jobs sooner

`HoldInterval` is a process-wide setting read by the servers. Set it at startup in every process that runs a server:

```csharp
// Held jobs come back after 10 seconds instead of 30
DrainMode.HoldInterval = TimeSpan.FromSeconds(10);
```

## Things to know

- **A held job keeps cycling.** Every hold adds a Scheduled and an Enqueued entry to the job's history, and a few storage writes. For a pause of hours with many waiting jobs, consider a longer `HoldInterval`.
- **A held job keeps its queue.** After a hold, a job goes back to the queue it was in, however that queue was chosen, including a job that was interrupted while it ran and comes back in the Processing state; see [How it works](#how-it-works). A job whose queue came only from `new EnqueuedState("emails")` keeps it in the `HeldQueue` job parameter until the hold ends. Its later retries go to `default`, as they would without the hold; give the job a queue of its own (`Enqueue("emails", ...)` or `[Queue("emails")]`) to keep retries in `emails`.
- **Long server IDs work.** Server IDs built from long host names (Kubernetes pod names can have 63 characters) are stored under a hashed field, so pausing, resuming, clearing and the dashboard listing work with the full ID as usual.
- **A paused server still takes jobs from the queue**, then holds them: each goes to Scheduled for the hold interval. After the hold, any server may pick the job up, including the paused one, which holds it again. Other servers end up running the work, but a job may be delayed by more than one hold.
- **Server pauses don't survive a restart.** The server ID contains a GUID that is new every time the server starts, including the automatic restart after its entry was removed from the storage, so a restarted server is not paused. Pause it again if needed. The old ID stays recorded as paused, but it has no effect: the dashboard banner, the Wallboard and the health endpoint ignore pauses of servers that are no longer registered. The Servers page lists them under **Pauses left by stopped servers** with a **Clear** button. From code, clear one with `DrainMode.ResumeServer(connection, "<old server id>")`, or with `DrainMode.ClearStaleServerPauses(storage)` for every stopped server at once.
- **Pause by the full server ID.** `PauseServer` and `IsServerPaused` compare the whole ID (`{name}:{process id}:{guid}`). Pausing `"web-1"` doesn't pause the server `web-1:4242:0f8fad5b-...`; look the ID up on the Servers page or in `IMonitoringApi.Servers()`, as in [Pause every server on one machine](#pause-every-server-on-one-machine).
- **The global switch, queue pauses and server pauses are independent.** `Disable` turns off only the global switch.
- **Drain mode needs the global filters.** `DrainModeFilter` is in `GlobalJobFilters.Filters` by default. If you remove it, or give a server a `FilterProvider` that does not include the global filters, that server ignores drain mode. Servers running a version without drain mode ignore it too.
- **Scheduling continues.** Recurring jobs are still triggered and delayed jobs still enqueued while processing is paused; they are held when a worker picks them up, and run after you resume.

## See also

- [Queues and workers](queues-and-workers.md)
- [Server options](server-options.md)
- [Dashboard pages](../dashboard/pages.md)
- [Dashboard authorization](../dashboard/authorization.md)
- [Managing jobs](../jobs/managing-jobs.md)
- [Custom filters](../filters/custom-filters.md)
