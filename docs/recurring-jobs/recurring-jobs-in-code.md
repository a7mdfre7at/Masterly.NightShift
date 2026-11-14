# Recurring jobs declared in code

Declare the application's recurring jobs next to its services, and they are registered when the host starts. A declaration that can't work stops the start instead of leaving a job that never runs.

## Quick example

```csharp
using Masterly.NightShift;

builder.Services.AddNightShift(config => config.UseSqlServerStorage(connectionString));
builder.Services.AddNightShiftServer();

builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Add<ReportService>("daily-report", service => service.BuildDaily(), Cron.Daily(2))
    .Add<CacheService>("refresh-cache", service => service.RefreshAsync(), Cron.Every(TimeSpan.FromMinutes(5))));
```

## How it works

When the host starts, every declaration is collected into one `RecurringJobSet`, checked, and applied with `IRecurringJobManager.AddOrUpdate`. Each process applies the set, which is safe: registering a job takes a lock on its ID, and an unchanged job keeps its next run and its paused state.

Each of these stops the host from starting, with a message naming the job:

| Problem | Exception |
|---|---|
| The CRON expression is invalid | `InvalidOperationException` |
| A `%Section:Key%` schedule has no value, or its value isn't a valid CRON expression | `InvalidOperationException` |
| The same ID is declared twice, even by two providers | `InvalidOperationException` |
| The method can't be a job, or `AddHandler<TArgs>` finds no registered handler for the arguments type, or several | `InvalidOperationException` |

An error from the storage while applying, such as an unreachable database, also stops the start. It is the original exception.

`AddNightShift` must be called too, because the jobs are applied through its `IRecurringJobManager` and `JobStorage`. A dashboard-only process that doesn't call `AddNightShiftRecurringJobs` applies nothing.

## API

### Declaring jobs

| Method | Effect |
|---|---|
| `services.AddNightShiftRecurringJobs(Action<RecurringJobSet> declare)` | Adds declarations. Can be called many times: all of them form one set. |
| `services.AddNightShiftRecurringJobs<TProvider>()` | Adds a class implementing `IRecurringJobProvider`, created from the container, so it can read its schedule from its dependencies. Adding the same provider twice has no effect. |

### RecurringJobSet

| Member | Meaning |
|---|---|
| `Add(id, methodCall, cron, options)` | Declares a job from a static method. |
| `Add<T>(id, methodCall, cron, options)` | Declares a job from a method of `T`, created through dependency injection when it runs. |
| `Add<TJob>(cron, options)`, `Add<TJob>(id, cron, options)` | Declares a run of a [job class](../jobs/typed-handlers.md). Without an ID, the name of the class is used. |
| `AddHandler<TArgs>(id, args, cron, options)`, `AddHandler<TArgs>(args, cron, options)` | Declares a job run by the only registered [handler](../jobs/typed-handlers.md) of `TArgs`, which gets `args` every time. Without an ID, the name of the handler is used. |
| `AddHandler<THandler, TArgs>(id, args, cron, options)`, `AddHandler<THandler, TArgs>(args, cron, options)` | The same with the handler named, which doesn't need registering. |
| `ResolvePlaceholders(getValue)` | Replaces each `%Section:Key%` schedule with its value. A host does this for you. |
| `Named(name)` | Names the set, up to 100 characters. See [Set names](#set-names). |
| `Name` | The set's name. |
| `RemoveUndeclared(scope)` | After applying, also removes the jobs a set of the same name declared before that this one doesn't. See below. |
| `RetireUndeclared(scope)` | After applying, pauses the jobs a set of the same name declared before that this one doesn't, instead of removing them. See below. Can't be combined with `RemoveUndeclared`. |
| `Once(key, methodCall, delay)` | Declares a job created once for `key`, the first time a set with it is applied. See below. |
| `AddPolicy(policy)` | Declares a [schedule policy](../jobs/schedule-policies.md#declaring-policies-in-code), saved before the jobs. |
| `DeclaredIds` | The identifiers declared so far. |
| `Apply()` | Applies the set to `JobStorage.Current`, for applications without a host. |
| `Apply(manager, storage)` | Applies the set through the given manager and storage. |

`cron` is a CRON expression or a `%Section:Key%` placeholder. `options` is a [`RecurringJobOptions`](creating-recurring-jobs.md#recurringjoboptions), for the time zone, queue, misfire handling and dates. It is optional. `Apply` returns a `RecurringJobSetResult` with the `Applied`, `Removed` and `Retired` identifiers, the `CreatedOnce` keys and the `PoliciesSaved` names.

Applying a set writes its name into the `Declared` field of each declared job's hash, so `RecurringJobDto.Declared` tells a job declared in code from one created any other way, and `RecurringJobDto.DeclaredBy` names the set. The field is written only when it changes.

### Set names

Each set has a name, and `RetireUndeclared` and `RemoveUndeclared` act only on the jobs a set of that name declared. So applications that share a storage, a web host and a worker host for example, each declaring its own jobs, never pause or delete each other's jobs, while the instances and the new versions of one application, which have the same name, are one set.

- **The default** is the simple name of the entry assembly (`Assembly.GetEntryAssembly()`), or `default` when there is none (`RecurringJobSet.DefaultName`). With `AddNightShiftRecurringJobs` it is the host's `IHostEnvironment.ApplicationName`, which is the entry assembly's name unless the host sets another.
- **`Named(name)`** gives another one, for example to keep the name when the assembly is renamed, or to split one application's jobs in two sets. It throws `ArgumentException` for a name that is empty, longer than 100 characters, or `1`. With a host, call it in a declaration: `jobs.Named("billing")`.
- **The last declaration wins.** A job declared by a set of another name moves to this set, logged at debug level, and from then on only this set retires or removes it.
- **Jobs marked by earlier builds**, whose `Declared` field holds `1` instead of a name, belong to no set: no set retires or removes them, and the first set that declares one again writes its name into it. `RecurringJobDto.Declared` is `true` for them and `DeclaredBy` is `null`.

The dashboard shows the name next to the job's **From code** badge.

## More examples

### Declare a job class

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Add<NightlyCleanup>(Cron.Daily(3)));
```

The recurring job's ID is the name of the class, here `NightlyCleanup`. Give an ID to run one class on two schedules, or to keep the ID when the class is renamed.

### Read a schedule from configuration

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Add<NightlyCleanup>("%Billing:CleanupCron%"));
```

```json
{ "Billing": { "CleanupCron": "0 3 * * *" } }
```

The value is read from the host's `IConfiguration` when the host starts, and is checked like any other schedule. Without a host, call `ResolvePlaceholders` before `Apply`:

```csharp
new RecurringJobSet()
    .Add<NightlyCleanup>("%Billing:CleanupCron%")
    .ResolvePlaceholders(key => configuration[key])
    .Apply();
```

### Let each module declare its own jobs

```csharp
public sealed class BillingJobs(IOptions<BillingOptions> options) : IRecurringJobProvider
{
    public void DeclareJobs(RecurringJobSet jobs)
    {
        jobs.Add<InvoiceRun>("billing:invoice-run", run => run.ExecuteAsync(), options.Value.InvoiceSchedule);
    }
}
```

```csharp
builder.Services.AddNightShiftRecurringJobs<BillingJobs>();
```

### Remove jobs that are no longer in the code

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Add<ReportService>("app:daily-report", service => service.BuildDaily(), Cron.Daily(2))
    .RemoveUndeclared());
```

or, to remove only the jobs with a prefix, `RemoveUndeclared(static id => id.StartsWith("app:", StringComparison.Ordinal))`.

A job a set of the same [name](#set-names) declared before that this one doesn't is removed. Jobs another set declared, jobs created any other way (from the dashboard, a [manifest](recurring-job-manifest.md) or other code with `AddOrUpdate`) and jobs marked by earlier builds are never removed. A `scope`, as above, limits the removal further.

Removal is off unless you ask for it, because **during a rolling deployment, versions delete each other's jobs**: the old version removes what only the new one declares, and the other way around, until the old instances are gone. Use it where one version runs at a time, or apply the removal as a step of the deployment, or use `RetireUndeclared` instead.

Nothing is removed when applying a job failed, or when no job was declared at all, since an empty set is more likely a mistake. Each removed job is logged at info level.

### Pause jobs that are no longer in the code

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Add<ReportService>("daily-report", service => service.BuildDaily(), Cron.Daily(2))
    .RetireUndeclared());
```

A job a set of the same [name](#set-names) declared before that this one doesn't is paused and marked as retired (`RecurringJobDto.Retired`), in one transaction, and logged at info level: it keeps its definition and history, and declaring it again resumes it. Jobs another set declared, jobs that no set declared (created from the dashboard, a manifest or other code) and jobs marked by earlier builds are never touched, so applications that share a storage need no `scope` to keep apart; a `scope` limits it further. `RetireUndeclared` is safer than `RemoveUndeclared`: during a rolling deployment the old version resumes what the new one retired, until it stops, so the last version to start decides, and nothing is lost meanwhile. Pausing needs a manager that implements `IRecurringJobManagerV3`, as the container's does.

### Run start-up work once

```csharp
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .Once<RoleSeeder>("seed-roles-v1", seeder => seeder.SeedAsync())
    .Once<SearchIndex>("rebuild-index-2026-11", index => index.RebuildAsync(), TimeSpan.FromMinutes(10)));
```

The job is created the first time a set with its key is applied, enqueued at once or scheduled after the `delay`, and the key is recorded with the job's ID in the storage hash `recurring-job-set:once` (`RecurringJobSet.OnceHashKey`). Later applications, on any replica, skip it: creating these jobs takes a distributed lock, so replicas starting together create each one once. To run the work again, use a new key. The key is at most 100 characters; a key declared twice throws `InvalidOperationException`, and a negative delay `ArgumentOutOfRangeException`. A job a client filter refuses to create isn't recorded, so the next application tries again. The job is created without a state and enqueued (or scheduled) in the transaction that records its key, so a failure in between leaves a job that never runs, and the next application creates it again: the work doesn't run twice. Deleting the job doesn't make it run again.

### Without a host

```csharp
GlobalConfiguration.Configuration.UseSqlServerStorage(connectionString);

new RecurringJobSet()
    .Add<ReportService>("daily-report", service => service.BuildDaily(), Cron.Daily(2))
    .Apply();
```

## Things to know

- **The database must be reachable at start-up.** Otherwise the host doesn't start. An orchestrator will restart it until the database is up.
- **A job edited on the dashboard is overwritten** at the next start, because the code is what declares it. Its paused state is kept, except that a retired job declared again is resumed.
- **Overlapping runs aren't prevented.** If a run can take longer than the interval, add [`[DisableConcurrentExecution]`](../filters/concurrency.md) to the method.

## See also

- [Creating recurring jobs](creating-recurring-jobs.md)
- [CRON and schedules](cron-and-schedules.md)
- [Recurring job manifest](recurring-job-manifest.md)
- [Job classes](../jobs/typed-handlers.md)
