# Recurring job manifest

`RecurringJobManifest` exports every recurring job to one JSON document and applies such a document back. Use it to keep schedules in source control, review schedule changes in pull requests, copy the recurring jobs of staging to production, or back them up before a risky change. The dashboard's Recurring Jobs page has **Export** and **Import** buttons for the same document.

## Quick example

```csharp
using Masterly.NightShift;

// Every recurring job of JobStorage.Current as JSON
string json = RecurringJobManifest.Export();
File.WriteAllText("recurring-jobs.json", json);

// Register every entry of the document (add new IDs, update existing ones)
RecurringJobManifestResult result = RecurringJobManifest.Apply(File.ReadAllText("recurring-jobs.json"));
Console.WriteLine($"{result.Added.Count} added, {result.Updated.Count} updated, {result.Errors.Count} failed");
```

## How it works

**Export** reads every recurring job from storage, sorts them by ID and writes, for each one, its ID, CRON expression, time zone, queue (unless it is the default queue), paused flag, misfire handling, start and end dates (when set) and the method call in the storage's own serialized form (type, method, parameter types, arguments). Because the method call is written exactly as the storage keeps it, a manifest exported from one environment applies to another environment that runs the same code. A job whose type or method can't be loaded by the exporting application (a dashboard-only app that doesn't reference your job assemblies, for example) is still exported, with its method call copied as stored. Only a job that has no stored method call at all is left out, and a warning naming it is logged.

**Apply** reads the document, then for each entry calls `AddOrUpdate` on a recurring job manager with the entry's ID, method call, CRON expression, time zone, queue, misfire handling and start and end dates. If the entry has a `paused` field, it then calls `Pause` (`true`) or `Resume` (`false`); without the field, the job keeps its paused state. So it follows the [AddOrUpdate rules](creating-recurring-jobs.md#what-happens-when-you-register-the-same-id-again): an entry that matches the stored job changes nothing and keeps its next run, a changed CRON expression, time zone or start and end date reschedules from now, and new IDs are created. An entry without `startAt` or `endAt` clears that date, like options without them do. With `prune: true`, recurring jobs that exist in storage but not in the document are removed.

Time zone IDs are resolved with the manager's `ITimeZoneResolver` (the one from the container, or the one you passed to the `RecurringJobManager` constructor). For a manager that isn't a `RecurringJobManager`, and for `Apply(json)`, the resolver set with `GlobalConfiguration.Configuration.UseTimeZoneResolver(...)` is used (`DefaultTimeZoneResolver` unless you set one).

A `queue` other than `default` becomes part of the job's definition, like the `AddOrUpdate(id, queue, ...)` overloads do, on storages that support per-job queues (SQL Server does). On other storages it is stored as the recurring job's own queue, which [`UpdateSchedule`](pausing-and-updating.md) also sets, so applying never needs per-job queue support; there it applies only to the first enqueue of each run, and `AutomaticRetry` retries a failed run on `default` unless the method has `[Queue]`. A missing queue, an empty one or `default` means the default queue.

An entry that can't be applied (no ID, an ID that appears twice, no CRON expression, no job, an unknown type or method, a method the application doesn't use as a job, an invalid CRON expression, an unknown time zone, a `misfireHandling` other than 0, 1 or 2, an `endAt` that isn't later than `startAt`, a date before 1970) is reported in the result's `Errors` and doesn't stop the other entries. A document that can't be read at all, including one with a `startAt` or `endAt` that isn't a date, throws `FormatException` before anything is applied.

### Which methods a manifest may use

A manifest names a type and a method, and applying it makes that method run on a schedule. So that a manifest can't turn any public method of any loaded assembly into a job (for example `System.Diagnostics.Process.Start`), `Apply` accepts an entry only when its type and method are already used as a job by the application:

- the method of a recurring job that already exists in the storage, with the same type (the manifest may register it under another ID, with other arguments);
- a method in the [Job Catalog](../jobs/job-catalog.md), such as a method of a type added with `JobCatalog.Add<T>()`;
- a method with a generated job invoker: any method used in an `Enqueue`, `Schedule`, `AddOrUpdate` or similar call in a project compiled with the source generator, and every public method of a type marked `[GenerateJobInvokers]`. See [Source-generated invokers](../jobs/source-generated-invokers.md).

Any other entry is reported in `Errors` with a message that names the method, and nothing is registered for it. Mark the classes your manifests use with `[GenerateJobInvokers]`, or add them to the Job Catalog, so a manifest applies to a fresh environment too. To accept any method the application can load, pass `allowAnyMethod: true` to `Apply`, or, for the dashboard's **Import**, set `DashboardOptions.AllowAnyMethodInManifestImport` to `true`. Do that only when everyone who can write the manifest, or import one on the dashboard, may run any code in your application.

## The document

```json
{
  "version": 1,
  "recurringJobs": [
    {
      "id": "daily-report",
      "cron": "0 6 * * *",
      "timeZone": "Europe/Berlin",
      "paused": false,
      "job": {
        "type": "Shop.Jobs.ReportService, Shop",
        "method": "BuildDaily",
        "parameterTypes": "[]",
        "arguments": "[]"
      }
    },
    {
      "id": "invoice-reminders-eu",
      "cron": "0 9 * * 1-5",
      "queue": "billing",
      "paused": true,
      "misfireHandling": 1,
      "startAt": "2026-11-16T00:00:00Z",
      "endAt": "2026-12-31T22:59:59Z",
      "job": {
        "type": "Shop.Jobs.InvoiceService, Shop",
        "method": "SendReminders",
        "parameterTypes": "[\"System.String\",\"System.Int32\"]",
        "arguments": "[\"\\\"eu\\\"\",\"14\"]"
      }
    }
  ]
}
```

| Field | Type | Required | Meaning |
|-------|------|----------|---------|
| `version` | number | no | The format version. Export writes `1` (`RecurringJobManifest.CurrentVersion`). A document with a higher version is rejected. |
| `recurringJobs` | array | yes | The entries. |
| `id` | string | yes | The recurring job ID. Leading and trailing spaces are trimmed. |
| `cron` | string | yes | The CRON expression. Plain English is not accepted here. |
| `timeZone` | string | no | A time zone ID, resolved as described above. Omitted (or empty) means UTC; export omits it for UTC jobs. |
| `queue` | string | no | The queue runs are enqueued to. Omitted, empty or `default` means the default queue; export omits it then. |
| `paused` | boolean | no | `true` pauses the job, `false` resumes it if it was paused. Omitted leaves the paused state as it is (a new job starts active). Export always writes it. |
| `misfireHandling` | number or name | no | `0`/`Relaxed` (default, omitted on export), `1`/`Strict`, `2`/`Ignorable`. Export writes numbers; names are accepted on import, in any case. Another number is reported as an error for that entry. |
| `startAt` | string | no | The instant before which the job doesn't run (`RecurringJobOptions.StartAt`), as ISO 8601. Export writes UTC (`2026-11-16T00:00:00Z`) and omits it when the job has no start. On import a value with an offset (`2026-11-16T00:00:00+01:00`) is converted to UTC, and a value without one (`2026-11-16T00:00:00`, `2026-11-16`) is read as UTC. Omitted means no start. |
| `endAt` | string | no | The instant after which the job doesn't run (`RecurringJobOptions.EndAt`), written and read like `startAt`. Omitted means no end. |
| `job.type` | string | yes | The job's type, as the configured type serializer writes it. With `UseSimpleAssemblyNameTypeSerializer()` this is `Namespace.Type, Assembly`; otherwise it is the full assembly-qualified name. A job the exporting application can't load is written with the type name as stored. |
| `job.method` | string | yes | The method name. |
| `job.parameterTypes` | string | no | A JSON array of type names, as a string. Omitted means `[]`. |
| `job.arguments` | string | no | A JSON array of serialized argument values, as a string (each value is itself JSON, so a string argument `eu` appears as `"\"eu\""`). Omitted means `[]`. |

Property names are matched case-insensitively, and the reader allows comments and trailing commas, so a hand-edited manifest can carry notes. The `parameterTypes` and `arguments` strings are the hardest part to write by hand: register the job once in a development environment and export it rather than typing them.

## Options / API

| Member | Returns | Meaning |
|--------|---------|---------|
| `RecurringJobManifest.Export()` | `string` | Exports the recurring jobs of `JobStorage.Current`. |
| `RecurringJobManifest.Export(IStorageConnection connection)` | `string` | Exports the recurring jobs visible through `connection`. |
| `RecurringJobManifest.Parse(string json)` | `IReadOnlyList<RecurringJobManifestEntry>` | Reads a document without applying it. Throws `FormatException` for invalid JSON, a document without a `recurringJobs` array, or a newer `version`. |
| `RecurringJobManifest.Apply(string json, bool prune = false)` | `RecurringJobManifestResult` | Applies the document to `JobStorage.Current` through a new `RecurringJobManager`, which uses the time zone resolver set with `UseTimeZoneResolver`. Accepts only [methods the application uses as jobs](#which-methods-a-manifest-may-use). |
| `RecurringJobManifest.Apply(string json, bool prune, bool allowAnyMethod)` | `RecurringJobManifestResult` | The same, with `allowAnyMethod`. |
| `RecurringJobManifest.Apply(IRecurringJobManager manager, IStorageConnection connection, string json, bool prune = false)` | `RecurringJobManifestResult` | Applies the document through `manager`. `connection` is used to list the existing jobs (to tell added from updated, for pruning, and to know which methods are already used by recurring jobs). Accepts only methods the application uses as jobs. |
| `RecurringJobManifest.Apply(IRecurringJobManager manager, IStorageConnection connection, string json, bool prune, bool allowAnyMethod)` | `RecurringJobManifestResult` | The same, with `allowAnyMethod`. |
| `RecurringJobManifest.CurrentVersion` | `int` (const) | `1`. |

`Apply` parameters:

| Parameter | Type | Default | Meaning |
|-----------|------|---------|---------|
| `manager` | `IRecurringJobManager` | `new RecurringJobManager(JobStorage.Current)` | Registers, pauses, resumes and removes the jobs. If it doesn't implement `IRecurringJobManagerV3`, the `paused` fields are ignored. When it is a `RecurringJobManager`, its time zone resolver resolves the `timeZone` fields. When it implements `IRecurringJobManagerV2`, its `Storage` decides where a `queue` is stored. |
| `connection` | `IStorageConnection` | a connection to `JobStorage.Current` | Lists the jobs that already exist. Use a connection to the same storage the manager writes to. |
| `json` | `string` | required | The document. |
| `prune` | `bool` | `false` | `true` removes every stored recurring job whose ID isn't in the document. Entries that are in the document but fail still count as present, so they aren't pruned. |
| `allowAnyMethod` | `bool` | `false` | `true` accepts an entry for any public method the application can load; `false` accepts only the methods the application already uses as jobs. See [Which methods a manifest may use](#which-methods-a-manifest-may-use). |

`RecurringJobManifestResult`:

| Property | Type | Meaning |
|----------|------|---------|
| `Added` | `IReadOnlyList<string>` | IDs that didn't exist before. |
| `Updated` | `IReadOnlyList<string>` | IDs that existed and were registered again from the document (whether or not anything changed). |
| `Removed` | `IReadOnlyList<string>` | IDs removed by pruning. |
| `Errors` | `IReadOnlyList<RecurringJobManifestError>` | Entries that failed. Each has `Id` (`?` when the entry had none) and `Message`. |

`RecurringJobManifestEntry` has the properties `Id`, `Cron`, `TimeZone`, `Queue`, `Paused` (`bool?`, `null` when the field is missing), `MisfireHandling`, `StartAt` and `EndAt` (`DateTimeOffset?`, `null` when the field is missing) and `Job` (a `RecurringJobManifestJob` with `Type`, `Method`, `ParameterTypes` and `Arguments`), matching the document fields above.

## More examples

### Apply the manifest from source control on start-up

Keep `recurring-jobs.json` in the repository, copy it to the output, and apply it after the app is built, through the container's manager and storage:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Storage;

var storage = app.Services.GetRequiredService<JobStorage>();
var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

string json = File.ReadAllText(Path.Combine(app.Environment.ContentRootPath, "recurring-jobs.json"));

using (var connection = storage.GetConnection())
{
    RecurringJobManifestResult result = RecurringJobManifest.Apply(recurringJobs, connection, json, prune: true);

    foreach (var error in result.Errors)
    {
        logger.LogError("Recurring job {RecurringJobId} was not applied: {Reason}", error.Id, error.Message);
    }

    logger.LogInformation(
        "Recurring jobs: {Added} added, {Updated} updated, {Removed} removed",
        result.Added.Count, result.Updated.Count, result.Removed.Count);
}
```

With `prune: true` the file is the whole truth: jobs removed from it are removed from storage on the next deploy. Don't combine this with `AddOrUpdate` calls in code for jobs that aren't in the file: pruning removes them on every start (or right after your code registered them, depending on the order), which also loses their run history.

Leave the `paused` fields out of that file, so a deploy doesn't resume jobs that someone paused in the dashboard. Add `"paused": true` only to jobs that must stay paused in every environment.

### Export from a specific storage

```csharp
using (var connection = storage.GetConnection())
{
    File.WriteAllText("recurring-jobs.json", RecurringJobManifest.Export(connection));
}
```

### Review a manifest before applying it

```csharp
foreach (RecurringJobManifestEntry entry in RecurringJobManifest.Parse(json))
{
    string paused = entry.Paused == true ? " [paused]" : "";
    Console.WriteLine($"{entry.Id}: {entry.Cron} ({entry.TimeZone ?? "UTC"}) {entry.Job.Type} {entry.Job.Method}{paused}");
}
```

### From the dashboard

On the **Recurring Jobs** page, **Export** downloads the manifest of all recurring jobs (`GET {dashboard}/recurring/export`). **Import** opens a dialog where you paste a manifest or pick a file, optionally tick "Remove the recurring jobs that are not in the manifest" (prune), and see which jobs were added, updated, removed or failed. Import is a `POST` to `{dashboard}/recurring/import`, is refused for read-only dashboard users, and is recorded in the audit log. It accepts only [methods the application uses as jobs](#which-methods-a-manifest-may-use) unless `DashboardOptions.AllowAnyMethodInManifestImport` is `true`:

```csharp
app.MapNightShiftDashboard("/nightshift", new DashboardOptions
{
    // Only if every dashboard user who can import may run any method of the application
    AllowAnyMethodInManifestImport = true,
});
```

See [Dashboard pages](../dashboard/pages.md) and [Audit log](../monitoring/audit-log-and-activity.md).

## Things to know

- **An exported manifest carries every job's paused flag.** Applying it pauses the jobs that were paused when it was exported and resumes the others, including jobs someone paused in the dashboard since. To leave the paused state to the dashboard, remove the `paused` fields from the file you keep in source control.
- **A fresh environment knows fewer job methods.** In an empty storage no recurring job vouches for a method yet, so an entry is accepted only through the Job Catalog or a generated invoker. If applying reports "is not a method this application uses as a job" there, mark the job classes with `[GenerateJobInvokers]` (or add them to the Job Catalog) rather than turning on `allowAnyMethod`.
- **Manifests from earlier versions still apply.** The `startAt` and `endAt` fields are new and optional, and the format version stays `1`. An entry without them registers the job without a window, which also clears a window the job had. An earlier version applying a manifest that has them ignores the two fields.
- **The manifest describes method calls, not code.** Applying it in an environment where the type or method doesn't exist reports an error for that entry (and, with `prune: true`, doesn't remove the job). Applying it where they exist but behave differently runs the new code.
- **Export doesn't need your job assemblies.** Jobs whose type or method the exporting application can't load are written as stored, so exporting from a dashboard-only app still includes every job. Their `job.type` is the stored name, which may be written differently (for example with an assembly version) than names your type serializer writes.
- **Arguments are applied as stored.** They are deserialized with the current serializer settings; see [Serialization](../configuration/serialization.md).

## See also

- [Creating recurring jobs](creating-recurring-jobs.md)
- [Pausing and updating](pausing-and-updating.md)
- [CRON and schedules](cron-and-schedules.md)
- [Dashboard pages](../dashboard/pages.md)
- [Serialization](../configuration/serialization.md)
