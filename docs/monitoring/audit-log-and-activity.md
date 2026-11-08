# Audit log and activity feed

Two rolling logs kept in the job storage. The **audit log** records who did what on the dashboard: requeues, deletes, pauses, imports, jobs run from the Catalog. It is on by default. The **activity feed** records every state transition of every job across the cluster, and drives the live feed on the dashboard's home page and the Timeline page. It is off by default, because it writes to one storage list on every state change; turn it on with `UseActivityFeed()`. Use this page to turn them on or off, size them, and read them from code.

## Quick example

Read the latest dashboard actions from code:

```csharp
using Masterly.NightShift;

public static class AuditReport
{
    public static void ReadAudit(JobStorage storage)
    {
        using (var connection = storage.GetReadOnlyConnection())
        {
            foreach (AuditEntry entry in AuditLog.Read(connection, 20))
            {
                Console.WriteLine(
                    $"{entry.OccurredAt:u} {entry.User ?? "anonymous"} {AuditLog.Describe(entry.Action)} " +
                    $"[{string.Join(", ", entry.Targets ?? Array.Empty<string>())}] -> {entry.Status}");
            }
        }
    }
}
```

Output looks like `2026-11-06 09:52:10Z alice@example.com Requeue failed jobs [118, 119] -> 204`.

## How it works

### Audit log

Every action posted to the dashboard calls `AuditLog.Record(...)` after the action ran. The entry is first written to the library's log at the Information level, by the logger `Masterly.NightShift.AuditLog`, for example `Dashboard action /jobs/failed/requeue by alice@example.com from 10.0.0.12 on 118, 119: HTTP 204.` Line breaks and other control characters in the user name, the targets and the detail (a note's text, a pause reason) are written as spaces in that line, so they can't start a forged entry in a plain-text log; the stored entry keeps the text as it was. Then it is serialized as JSON and pushed to the head of the storage list `audit`, which is trimmed to the newest `AuditLog.MaxEntries` entries. The log line is the permanent record: the list keeps only the newest entries. A storage purge keeps it by default (`StoragePurgeOptions.KeepAuditLog` is `true`, and the dashboard's **Clear jobs** and **Reset** always keep it); a purge from code with `KeepAuditLog = false` empties it. The **Audit** page of the dashboard (`/audit`) shows the newest 100 by default (`?count=` shows up to `MaxEntries`), with the time, user, a readable description of the action, its targets and its detail. Actions that answered with an HTTP status of 400 or more are shown in red.

What is recorded:

- **User:** from `DashboardContext.GetUserName()`. The ASP.NET Core adapter answers with `HttpContext.User.Identity.Name` when the user is authenticated, otherwise `null` (shown as anonymous).
- **Address:** the request's remote IP address. The Audit page shows it to users who can change things; read-only users see it only when `DashboardOptions.DisplayAuditIpAddressesToReadOnlyUsers` is `true`.
- **Action:** the dashboard path that was posted to, relative to the dashboard's base path, for example `/jobs/failed/requeue`.
- **Targets:** what the action was about: job ids, recurring job ids, a queue, a server, a tag, a circuit resource. For single-item commands they come from the route; for bulk actions, from the selected ids.
- **Detail:** free text, depending on the action: the reason given for a pause, `"EmailService.SendWelcome enqueued"` for a Catalog run, `"cron 0 3 * * *"` for an edited recurring job, the text of a job note.
- **Status:** the HTTP status the action answered with.

Recording never throws. If the storage fails, the action has already happened: its storage entry is lost, a warning with the error is logged, and the Information log line written just before is the only record. Read-only dashboard users cannot post actions, so nothing is recorded for them.

### Activity feed

Turn the feed on in every process that changes job states: the servers, and the apps that create jobs.

```csharp
builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
    .UseActivityFeed());
```

`UseActivityFeed()` sets `ActivityFeed.Enabled` to `true` and makes sure `ActivityFeedFilter` is in `GlobalJobFilters` (it is there by default, doing nothing while the feed is off). `UseActivityFeed(maxEntries)` also sets `ActivityFeed.MaxEntries`.

The filter is an apply-state filter, but it writes nothing into the state change's transaction. When a state is applied to a job, it builds an entry and queues it in memory once the state change is committed. A background writer, one per process and storage, takes the queued entries in batches of up to 100 and adds them to the head of the storage list `activity` in a short transaction of its own. Every 100 entries a process wrote (and on its first write) it also trims the list to `ActivityFeed.MaxEntries`.

So the feed can't slow down or fail a state change, whatever happens to the `activity` list. In exchange, it is best effort:

- **An entry can be missing.** If a batch can't be written (the storage is down, or the write lost a deadlock), its entries are dropped and a warning is logged (once per outage; further failures go to the Debug level). Entries still queued when the process stops are lost. At most 10,000 entries wait for the storage; more are dropped with a warning.
- **The list can be a little longer than `MaxEntries`.** Each process trims every 100 entries, so the list can hold up to about 100 more entries per process between trims. `Read` returns only what you ask for.
- **Entries appear a moment after the transition**, when the writer has written them.

The dashboard uses it in two places:

- **Home page, live activity.** The page polls `GET /activity?count=50&since={unix ms}` every couple of seconds and adds the new transitions on top: time, state, job, method, server and reason. The endpoint returns `{ "now": ..., "enabled": ..., "entries": [...] }`, at most 200 entries per call.
- **Timeline (`/timeline`).** Reads the whole feed and draws what every worker did in the last fifteen minutes, one lane per worker, one bar per run, coloured by outcome.

## Options / API

### `AuditLog` (static)

| Member | Type | Default | Meaning |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Whether dashboard actions are recorded. |
| `MaxEntries` | `int` | `1000` | How many entries the list keeps. |
| `ListKey` | `const string` | `"audit"` | The storage list the entries live in. |
| `MaxTargets` | `const int` | `25` | How many targets of one action are kept by id. |
| `Record(DashboardContext context, IEnumerable<string>? targets = null, string? detail = null, int status = 204)` | method | | Records an action taken through a dashboard request. Use it from your own dashboard dispatchers. |
| `Read(IStorageConnection connection, int count)` | method | | The newest `count` entries, newest first. Returns an empty list when `count` is zero or less, when the storage connection is not a `JobStorageConnection`, or when the storage can't read lists. |
| `Count(IStorageConnection connection)` | method | | How many entries are kept right now; 0 when the storage can't count lists. |
| `Describe(string action)` | method | | A short English description of an action path, for example `"Requeue failed jobs"`. Returns the path itself when it has no description. |

### `AuditEntry`

Stored as JSON with short property names to keep the list small.

| Property | JSON | Type | Meaning |
|---|---|---|---|
| `Timestamp` | `t` | `long` | When, as a Unix timestamp in seconds. |
| `OccurredAt` | (not stored) | `DateTime` | `Timestamp` as a UTC `DateTime`. |
| `User` | `u` | `string?` | The authenticated user. Truncated to 200 characters. |
| `RemoteIpAddress` | `ip` | `string?` | The client address. Truncated to 64 characters. |
| `Action` | `a` | `string` | The dashboard path that was posted to. |
| `Targets` | `g` | `IReadOnlyList<string>?` | Up to 25 targets. |
| `TargetCount` | `n` | `int?` | The real number of targets, when there were more than 25. |
| `Detail` | `d` | `string?` | A free-text detail. Truncated to 500 characters. |
| `Status` | `s` | `int` | The HTTP status the action answered with. |

### Recorded actions

`AuditLog.Describe` knows these actions. Other actions that record an entry (job notes, pausing a server) show their path.

| Path starts with | Description |
|---|---|
| `jobs/enqueued/delete-all`, `jobs/enqueued/delete`, `jobs/enqueued/requeue` | Delete every enqueued job / Delete enqueued jobs / Requeue enqueued jobs |
| `jobs/scheduled/delete-all`, `jobs/scheduled/enqueue`, `jobs/scheduled/delete` | Delete every scheduled job / Enqueue scheduled jobs now / Delete scheduled jobs |
| `jobs/processing/delete-all`, `jobs/processing/delete`, `jobs/processing/requeue` | Delete every processing job / Delete processing jobs / Requeue processing jobs |
| `jobs/succeeded/delete-all`, `jobs/succeeded/requeue` | Delete every succeeded job / Requeue succeeded jobs |
| `jobs/failed/delete-all`, `jobs/failed/requeue-all`, `jobs/failed/requeue`, `jobs/failed/delete` | Delete every failed job / Requeue every failed job / Requeue failed jobs / Delete failed jobs |
| `jobs/deleted/requeue` | Requeue deleted jobs |
| `jobs/awaiting/delete-all`, `jobs/awaiting/enqueue`, `jobs/awaiting/delete` | Delete every awaiting job / Enqueue awaiting jobs now / Delete awaiting jobs |
| `jobs/actions/requeue`, `jobs/actions/delete` | Requeue a job / Delete a job |
| `retries/delete-all` | Delete every retry |
| `recurring/remove-all`, `recurring/remove`, `recurring/trigger` | Remove every recurring job / Remove recurring jobs / Trigger recurring jobs |
| `recurring/pause`, `recurring/resume`, `recurring/update`, `recurring/import` | Pause / Resume recurring jobs, Edit a recurring job, Import recurring jobs |
| `maintenance/clear-jobs`, `maintenance/reset` | Clear all jobs / Reset the storage |
| `maintenance/pause`, `maintenance/resume` | Pause processing / Resume processing |
| `queues/pause`, `queues/resume` | Pause a queue / Resume a queue |
| `catalog/run` | Run a job from the catalog |
| `insights/circuits/reset` | Close a circuit |
| `tags/forget` | Forget a tag |
| `batches/requeue-failed`, `batches/delete` | Requeue a batch's failed jobs / Delete a batch's pending jobs |

### `ActivityFeed` (static)

| Member | Type | Default | Meaning |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Whether transitions are recorded. `UseActivityFeed()` sets it to `true`. |
| `MaxEntries` | `int` | `1000` | How many entries the list keeps (give or take one trim interval, see above). |
| `ListKey` | `const string` | `"activity"` | The storage list key. |
| `Read(IStorageConnection connection, int count)` | method | | The newest `count` entries, newest first. Returns an empty list when `count` is zero or less, when the connection is not a `JobStorageConnection`, or when the storage can't read lists. |

### `ActivityEntry`

| Property | JSON | Type | Meaning |
|---|---|---|---|
| `Timestamp` | `t` | `long` | Unix time in **milliseconds**. |
| `JobId` | `j` | `string?` | The job id. |
| `State` | `s` | `string?` | The state the job entered: `Enqueued`, `Processing`, `Succeeded`, ... |
| `OldState` | `o` | `string?` | The state it left. `null` when the job was just created. |
| `Method` | `m` | `string?` | `Type.Method` of the job, when its type could be loaded. |
| `Reason` | `r` | `string?` | The reason attached to the new state, if any. |
| `ServerId` | `v` | `string?` | The server that took the job. Set for Processing transitions. |
| `WorkerId` | `w` | `string?` | The worker that took the job. Set for Processing transitions. |

### `ActivityFeedFilter`

The filter that queues the entries. It has no options. It is registered in `GlobalJobFilters.Filters` by default and does nothing while `ActivityFeed.Enabled` is `false`.

### Configuration methods

| Method | Effect |
|---|---|
| `UseActivityFeed()` | Turns the feed on: `ActivityFeed.Enabled = true`, and adds `ActivityFeedFilter` to the global filters if it was removed. |
| `UseActivityFeed(int maxEntries)` | The same, and sets `ActivityFeed.MaxEntries`. `maxEntries` must be positive. |

### How entries are stored

Both lists hold JSON with the short property names in the tables above. The JSON is written with options of the library's own, never with the serializer settings you set with `UseSerializerSettings`, so a source-generated `JsonSerializerContext` that doesn't know these types can't break recording. Entries written by earlier versions with your settings are still read.

## More examples

### Size or turn off the logs

These are static settings. Set them at startup in every process: `AuditLog` matters in the process that hosts the dashboard, `ActivityFeed` in every process that changes job states (servers, and web apps that create jobs).

```csharp
AuditLog.MaxEntries = 5000;                                // keep more history
GlobalConfiguration.Configuration.UseActivityFeed(300);    // turn the feed on, with a shorter list
```

```csharp
AuditLog.Enabled = false;      // stop recording dashboard actions
ActivityFeed.Enabled = false;  // the default: the feed is off
```

With `ActivityFeed.Enabled = false` the filter still runs but does nothing. To remove it completely:

```csharp
GlobalJobFilters.Filters.Remove<ActivityFeedFilter>();
```

### Make sure every action has a user name

The user comes from ASP.NET Core authentication. Put the dashboard behind an authorization policy so only signed-in users reach it (see [Dashboard authorization](../dashboard/authorization.md)):

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("NightShiftOperators", policy => policy.RequireRole("Operations")));

// ...

app.UseAuthentication();
app.UseAuthorization();

// Only signed-in operators reach the dashboard, so every audit entry has a user name
app.MapNightShiftDashboardWithAuthorizationPolicy("NightShiftOperators");
```

A host other than ASP.NET Core can subclass `DashboardContext` and override `GetUserName()`.

### Copy audit entries to your log

Every entry is already written to the library's log (logger `Masterly.NightShift.AuditLog`, Information level), which goes to your `ILoggerFactory` with `AddNightShift`. Route that category to a durable sink and you have a permanent record. If you need the entries in another shape, copy them from the list, for example from a recurring job:

```csharp
using Masterly.NightShift;
using Microsoft.Extensions.Logging;

public class AuditExportJob
{
    private readonly JobStorage _storage;
    private readonly ILogger<AuditExportJob> _logger;

    public AuditExportJob(JobStorage storage, ILogger<AuditExportJob> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public void Export(long sinceUnixSeconds)
    {
        IReadOnlyList<AuditEntry> entries;
        using (var connection = _storage.GetReadOnlyConnection())
        {
            entries = AuditLog.Read(connection, AuditLog.MaxEntries);
        }

        foreach (var entry in entries)
        {
            if (entry.Timestamp <= sinceUnixSeconds) break; // newest first

            _logger.LogInformation(
                "Dashboard action {Action} by {User} from {RemoteIp} on {Targets}: HTTP {Status} {Detail}",
                AuditLog.Describe(entry.Action), entry.User, entry.RemoteIpAddress,
                entry.Targets, entry.Status, entry.Detail);
        }
    }
}
```

### Print the latest transitions

```csharp
using (var connection = storage.GetReadOnlyConnection())
{
    foreach (ActivityEntry entry in ActivityFeed.Read(connection, 50))
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp);
        Console.WriteLine($"{at:HH:mm:ss.fff} job {entry.JobId} {entry.OldState} -> {entry.State} {entry.Method} {entry.Reason}");
    }
}
```

## Things to know

- **Reading needs a `JobStorageConnection` and the extended API.** `Read` returns nothing on storages whose connection does not derive from `JobStorageConnection`, or that don't support `JobStorageFeatures.ExtendedApi` (SQL Server does both). Writing uses only `IWriteOnlyTransaction` list commands, which every storage has.
- **Activity feed cost.** A job usually goes through three states (Enqueued, Processing, Succeeded), so the feed records three entries per job. They are written in batches, one transaction per batch, by one writer per process, and never inside a state change. On SQL Server every write to the `activity` list locks all its rows for a moment, so writers of several processes take turns; the state changes don't wait for them. Writing the list inside each state change would make all the state changes of the cluster wait for each other, and deadlock under load; that is why the feed is written this way, and off unless you need it.
- **The Timeline only sees what the feed still holds.** It reads `ActivityFeed.MaxEntries` entries. If your cluster makes more than 1000 transitions in fifteen minutes, the Timeline covers less than fifteen minutes; raise `MaxEntries` to fill it.
- **The audit log holds personal data:** user names and IP addresses, in the storage list and in your logs. The list is limited by count, not by age; your log retention decides for the log lines. Account for both in your data-protection policy, and lower `MaxEntries`, filter the `Masterly.NightShift.AuditLog` log category, or turn recording off if needed.
- **Units differ:** `AuditEntry.Timestamp` is in seconds, `ActivityEntry.Timestamp` in milliseconds.
- **Audit entries are written after the action.** An action that failed is still recorded, with its error status: 422 when a command returned `false` or the input was refused (a Catalog run, an edited schedule, a manifest import), 500 when it failed midway, with what was done before (for example `1 of 3 done`). Error messages that could quote what was typed stay out of the entry. A failed storage purge is logged as an error with the storage's message, and the browser only gets a generic message.

## See also

- [Monitoring overview](README.md)
- [Dashboard pages](../dashboard/pages.md): the home page, Audit and Timeline
- [Dashboard authorization](../dashboard/authorization.md)
- [Dashboard customization](../dashboard/customization.md): your own dispatchers can call `AuditLog.Record`
- [Managing jobs](../jobs/managing-jobs.md): job states explained
- [Custom storage](../storage/custom-storage.md): `JobStorageConnection`
