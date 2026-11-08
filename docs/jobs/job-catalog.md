# Job catalog

The job catalog is the list of job methods the dashboard can run on its own. Its **Catalog** page shows every method with its parameters and filters, and a **Run…** form that creates the job now, later, or as a recurring job, without writing or deploying code. Use it for maintenance tasks, one-off fixes and re-running a job with corrected arguments.

## Quick example

Methods you already enqueue somewhere in your code are in the catalog automatically. To add a class whose methods are never enqueued from code, register it once at startup:

```csharp
using Masterly.NightShift;

public class MaintenanceJobs
{
    public Task RebuildSearchIndexAsync(string indexName, bool dropFirst = false) => Task.CompletedTask;
    public void PurgeTempFiles(int olderThanDays) { }
}

JobCatalog.Add<MaintenanceJobs>();
```

Open the dashboard, go to **Catalog**, find `MaintenanceJobs.RebuildSearchIndexAsync`, press **Run…**, fill in `indexName`, and choose when to run it.

## How it works

The catalog is built from two sources:

1. **Generated invokers.** The source generator emits an invoker for every method used in `Enqueue`, `Schedule`, `ContinueJobWith`, `AddOrUpdate` and similar calls, and for every public method of a class marked `[GenerateJobInvokers]`. Each loaded assembly registers its invokers, and each one is a catalog entry. See [Source-generated invokers](source-generated-invokers.md).
2. **Types you add** with `JobCatalog.Add<T>()` or `JobCatalog.Add(Type)`: all their public instance and static methods declared on the type itself.

Generic methods, methods with `ref`/`out` parameters, property accessors, `object` methods and their overrides (`Equals`, `GetHashCode`, `ToString`), and methods the compiler generates (a record's `Equals` or `<Clone>$`) are left out.

The **Run…** form has one field per parameter that the user supplies. Parameters the server fills in (`CancellationToken`, `IJobCancellationToken`, `PerformContext`, `IJobConsole`) are marked and get no field. When the form is sent (`POST /catalog/run`), the dashboard turns the text fields into argument values (see the table below), builds a `Job` and:

- **now**: creates it in the `Enqueued` state, on the chosen queue or `default` (when running a job again, the job's own queue);
- **later**: creates it in the `Scheduled` state; the delay is a number of minutes (0 to one year) or a phrase such as `in 2 hours` or `tomorrow at 9am`, read in `TimeText.DefaultTimeZone` (see [Delayed jobs](delayed-jobs.md#timetext));
- **recurring**: calls `AddOrUpdate` with the given recurring job ID and schedule (CRON, or plain English through `Cron.FromText`), in UTC.

Each run is recorded in the audit log (on by default, see [Audit log and activity](../monitoring/audit-log-and-activity.md)), and so is a run the dashboard refuses. The job details page has **Run again with changes…**, the same form pre-filled with that job's arguments, for any job whether it is in the catalog or not. The queue starts as the job's own queue (`Enqueue("emails", ...)`), or the queue it was last enqueued to; leaving the queue empty keeps the job's own queue (`Job.Queue`). A job whose type the dashboard's process can't load is refused with `422` and the load error, for example `Could not resolve assembly 'Shop.Web'`.

## API

### JobCatalog

| Member | Meaning |
|---|---|
| `Add<T>()`, `Add(Type type)` | Adds every public instance and static method declared on the type. Adding a type twice has no effect |
| `Clear()` | Removes the types added with `Add` (generated entries stay) |
| `GetEntries()` | Every entry, ordered by type name, method name and parameter count |
| `Find(string id)` | The entry with that `Id`, or `null` |
| `CreateEntry(Job job)` | Describes an existing job's method, catalogued or not |
| `FormatArguments(Job job)` | A job's arguments as the texts the form takes, in parameter order; `null` for injected and `[Redacted]` parameters |
| `CreateJob(JobCatalogEntry entry, IReadOnlyDictionary<string, string?> arguments, string? queue = null)` | Builds a `Job` from an entry and the arguments as text, keyed by parameter name. Throws `ArgumentException` for a missing or invalid value |

### JobCatalogEntry

| Property | Type | Meaning |
|---|---|---|
| `Id` | `string` | Stable identifier: `{type}\|{method}\|{parameter types}` (full type names) |
| `Type`, `Method` | `Type`, `MethodInfo` | The job's type and method |
| `Parameters` | `IReadOnlyList<JobCatalogParameter>` | All parameters, in order |
| `UserParameters` | `IEnumerable<JobCatalogParameter>` | The parameters a user has to supply (not injected) |
| `Filters` | `IReadOnlyList<string>` | Names of the filter and interceptor attributes on the method and its type, such as `AutomaticRetry` |
| `Invoker` | `JobInvokerKind?` | `Generated` for generated entries, `null` for types added with `Add` |
| `IsAsync` | `bool` | Whether the method returns a task |
| `InsightsKey` | `string` | The key the Insights page uses for the method |

### JobCatalogParameter

| Property | Type | Meaning |
|---|---|---|
| `Name` | `string` | The parameter name (the form field is `arg:{Name}`) |
| `Type` | `Type` | The parameter type |
| `TypeName` | `string` | A short name for the form, such as `int?` or `List<string>` |
| `Injected` | `bool` | Whether the server fills it in |
| `HasDefaultValue`, `DefaultValue` | `bool`, `object?` | The default declared in C# |
| `InputKind` | `string` | The form input: `checkbox` (`bool`), `number`, `datetime`, `text`, `select` (enums and `bool?`; a multiple choice for a `[Flags]` enum) or `json` |

### How text becomes argument values

| Parameter type | Input | Text accepted |
|---|---|---|
| `string` | `text` | Taken as is |
| `bool` | `checkbox` | `on`, `true`, `1`, `yes` mean `true`; anything else `false`. The form sends a hidden `false` with each checkbox, and when a field comes more than once the server takes `true` if any value is, so an unchecked box gives `false` even when the default is `true` |
| `bool?` | `select` | `true`, `false`, or `null` (the form's "(null)" choice) |
| Enums | `select` | A member name, case-insensitive. A nullable enum also takes `null` (the form's "(null)" choice). A `[Flags]` enum takes names separated by commas, such as `Instance, Public`; the form offers a multiple choice, sends one `arg:{Name}` field per choice and the server combines them. The form also sends a hidden `0`, so choosing nothing gives no flags rather than the default |
| `DateTime` | `datetime` | `2026-11-04T18:30` and other JSON date formats. A trailing `Z` gives a UTC value (`DateTimeKind.Utc`), an offset such as `+05:00` a local one, nothing an unspecified one |
| `DateTimeOffset` | `datetime` | The same formats. With an offset (`2026-11-04T18:30+05:00`, or `Z`) that exact moment and offset; without one, such as what the date picker gives, the time is read as UTC, whatever the server's time zone |
| Numbers | `number` | JSON numbers |
| `Guid`, `TimeSpan`, `char` | `text` | The plain value: `3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10` (any format `Guid` reads, braces allowed), `00:30:00` or `1.02:00:00` (`[d.]hh:mm[:ss]`, as `TimeSpan.Parse` reads it; a number without a colon is rejected rather than read as days), one character. A JSON string with the quotes, such as `"00:30:00"`, works too, and `null` for a nullable parameter |
| `object` | `json` | Plain JSON: a number, string, boolean or date becomes that value, an object or array becomes a `JsonElement`. A `"$type"` property is refused, here and in any member of type `object` of another argument |
| An interface or abstract class | `json` | A JSON object whose `"$type"` property names a class that implements it, for example `{"$type":"MyApp.OrderPlaced, MyApp","OrderId":42}` |
| `Type` | `json` | Nothing: a `Type` value is refused, here and in any member of another argument, because resolving it would load whatever type the text names. Run such a job from code; an empty field still gives the declared default or `null` |
| Anything else | `json` | A JSON value of the type |

An empty field gives the parameter's declared default; otherwise an empty `string`, `null` for reference and nullable types, or `false` for `bool`. Any other value type without a value is an error.

`FormatArguments` writes values in the same notation, so a form pre-filled from a job is accepted as it is and gives the same values back: a `Guid`, `TimeSpan` or `char` appears without quotes, an interface argument keeps its `"$type"`, an `object` value is written as plain JSON, without a type name, a `DateTimeOffset` keeps its offset (`2026-11-08T10:00:00+05:00`), and a `DateTime` keeps its kind (`2026-11-08T10:00:00Z` for UTC). Fractions of a second are written only when there are some. In the form, a date that fits the date picker (a local date and time to the second) gets one; any other date, with an offset, a `Z` or a fraction, gets a text field holding the value as it is.

## More examples

### Listing the catalog

```csharp
foreach (JobCatalogEntry entry in JobCatalog.GetEntries())
{
    string parameters = String.Join(", ", entry.UserParameters.Select(p => $"{p.TypeName} {p.Name}"));
    Console.WriteLine($"{entry.Type.Name}.{entry.Method.Name}({parameters}) [{entry.Invoker}]");
}
```

### Running catalog entries from your own admin API

```csharp
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

app.MapPost("/admin/jobs/run", (string entryId, Dictionary<string, string?> arguments, IBackgroundJobClient jobs) =>
{
    JobCatalogEntry? entry = JobCatalog.Find(entryId);
    if (entry == null)
    {
        return Results.NotFound();
    }

    Job job = JobCatalog.CreateJob(entry, arguments, queue: "maintenance");
    string? jobId = jobs.Create(job, new EnqueuedState("maintenance"));
    return Results.Ok(new { jobId });
});
```

Protect such an endpoint as strictly as the dashboard.

### Pre-filling a form from an existing job

```csharp
JobCatalogEntry entry = JobCatalog.CreateEntry(job);
IReadOnlyList<string?> values = JobCatalog.FormatArguments(job);
```

## Things to know

- **Anyone who can write on the dashboard can run any catalogued method with any arguments.** That includes every method your code enqueues. Restrict write access with dashboard authorization, and give read-only access to everyone else; see [Dashboard authorization](../dashboard/authorization.md). A read-only dashboard refuses runs with 401.
- **Entries depend on the loaded assemblies.** Generated entries come from assemblies already loaded in the dashboard's process. A dashboard hosted apart from the code that enqueues jobs shows only what it has loaded; add types with `JobCatalog.Add` there.
- **The dashboard needs the job assemblies to run jobs.** Catalog entries come from the types loaded in the dashboard's process, and **Run again with changes** builds the job from its stored type. A dashboard hosted without the job's assemblies answers `422` with what it couldn't load.
- **The job must still run on a server.** The catalog only creates jobs. A server that can load the type (and listens to the chosen queue) runs them.
- **Recurring jobs created from the form use UTC.** Change the time zone afterwards with `AddOrUpdate` from code if you need another one.
- **Arguments given as JSON are read with your serializer options**, like stored job arguments, with one exception: text typed in the form can't name a type for a value of type `object`. With `UseRecommendedSerializerSettings()` a stored `object` argument keeps its type through `"$type"`, but typed text is read as plain JSON and a `"$type"` in it is refused, so the form can't make the server create a type of the operator's choice. **Run again with changes** therefore gives such an argument as a `JsonElement`, not as its original type.
- **A `Type` parameter takes any type name the server can load.** There is no constraint to check typed text against. A job method that creates an instance of a `Type` argument therefore lets anyone with write access to the dashboard choose what it creates; check the type inside the method.

## See also

- [Source-generated invokers](source-generated-invokers.md)
- [Dashboard pages](../dashboard/pages.md)
- [Dashboard authorization](../dashboard/authorization.md)
- [Creating recurring jobs](../recurring-jobs/creating-recurring-jobs.md)
- [Behavior filters](../filters/behavior.md) (`[Redacted]`)
