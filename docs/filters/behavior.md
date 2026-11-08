# Behavior

These attributes change how a job is routed, named, run and shown: which queue it goes to (`Queue`), what the dashboard calls it (`JobDisplayName`), which culture it runs with (`CaptureCulture`), whether its start and end are written to its console (`LogInvocation`), and which arguments the dashboard hides (`Redacted`).

## Quick example

```csharp
using Masterly.NightShift;

[Queue("emails")]
public class EmailService
{
    public void SendWelcome(int userId) { }

    [Queue("critical")]
    public void SendPasswordReset(int userId) { }
}

jobs.Enqueue<EmailService>(x => x.SendWelcome(42));        // queue "emails"
jobs.Enqueue<EmailService>(x => x.SendPasswordReset(42));  // queue "critical"
```

A server only processes the queues it listens to, so make sure one does (see [Queues and workers](../server/queues-and-workers.md)).

## QueueAttribute

Puts a job on a named queue. Apply it to a method, or to a class or interface for all its methods; a method attribute replaces the class one. Without it, jobs go to the `default` queue.

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `queue` (constructor) | `string` | required | Queue name, or a template where `{0}`, `{1}` ... are the job's arguments. A plain name must be a valid queue name: lowercase letters, digits, `_` and `-` only. An invalid one throws `ArgumentException` (`ArgumentNullException` when it is `null`, empty or white space) as soon as the attribute is read, so creating the job fails at once. |
| `Queue` | `string` | | The name or template as given. |
| `Order` | `int` | `Int32.MaxValue` | Always the last election filter, so it has the final word on the queue. |

How it works: an election filter. Every time the candidate state is **Enqueued** (at creation, when a scheduled job or a retry becomes due, on requeue from the dashboard), it sets `EnqueuedState.Queue` to the name, or to the template formatted with the job's arguments (invariant culture). A template's result is checked then: when it isn't a valid queue name, the state change throws `InvalidOperationException` with a message that names the template and the result, such as `The queue template 'reports-{0}' produced 'reports-EU' ...`. A template that refers to a missing argument throws `FormatException`. A `[Redacted]` argument is used as it is, not hashed, because the queue name must match the queues servers listen to (see [Things to know about redaction](#things-to-know-about-redaction)).

### Route by argument

```csharp
public class TenantReportService
{
    // Build("eu", 7) goes to "reports-eu". Pass lowercase values.
    [Queue("reports-{0}")]
    public Task Build(string region, int reportId) => Task.CompletedTask;
}
```

### Things to know about queues

- The attribute also wins over a queue passed to `Enqueue(queue, ...)` or set on a recurring job, because it runs last.
- Retries come back to the same queue. To send retries elsewhere, write an election filter; [Custom filters](custom-filters.md#election-filter) has one.
- Attributes on the implementation class are not seen when the job is created through an interface (`Enqueue<IEmailService>(...)`); put them on the interface.

## JobDisplayNameAttribute

Sets the name the dashboard shows for a job instead of `EmailService.SendWelcome`. The text is a format string: `{0}`, `{1}` ... are the job's arguments. An argument marked `[Redacted]` is shown as `"••••••"`, like everywhere else in the dashboard.

```csharp
using Masterly.NightShift;

public class InvoiceService
{
    // Shown as "Send invoice 1042 to ana@example.com".
    [JobDisplayName("Send invoice {0} to {1}")]
    public Task Send(int invoiceId, string customerEmail) => Task.CompletedTask;
}
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `displayName` (constructor) | `string` | required | The name or format string. Empty values throw. |
| `DisplayName` | `string` | | The value as given. |
| `ResourceType` | `Type?` | `null` | A resource class to localize `DisplayName`: the text is looked up as a resource key with the current UI culture, and used as is when no resource is found. |

It is a plain attribute (not a filter) and applies to methods only. The dashboard formats it with `Format(DashboardContext context, Job job)`, which is `virtual`, so you can derive from the attribute to build names in code. An override that reads `job.Args` sees the raw values, redacted or not. If formatting throws, the raw `DisplayName` is shown.

How the dashboard picks a job's name, first match wins:

1. `[JobDisplayName]` on the method.
2. `[System.ComponentModel.DisplayName]` on the method, formatted with the arguments the same way.
3. `DashboardOptions.DisplayNameFunc`, a `Func<DashboardContext, Job, string>` you set when mapping the dashboard ([Dashboard setup](../dashboard/setup.md)).
4. The default: type and method name.

### Localized names

```csharp
using System.Resources;

public class InvoiceService
{
    // Looks up the "InvoiceReminder" resource, for example "Rappel pour la facture {0}" in French.
    [JobDisplayName("InvoiceReminder", ResourceType = typeof(JobNames))]
    public Task Remind(int invoiceId) => Task.CompletedTask;
}

public static class JobNames
{
    public static ResourceManager ResourceManager { get; } = new ResourceManager(typeof(JobNames));
}
```

A static `ResourceManager` property on the resource type (as generated `.resx` designer classes have) is used when present; otherwise a `ResourceManager` is created for the type.

### A name built in code

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;

public sealed class TicketDisplayNameAttribute : JobDisplayNameAttribute
{
    public TicketDisplayNameAttribute()
        : base("Ticket {0}")
    {
    }

    public override string Format(DashboardContext context, Job job)
    {
        return $"Ticket #{job.Args[0]} ({job.Queue ?? "default"})";
    }
}
```

### One rule for every job

```csharp
var options = new DashboardOptions
{
    DisplayNameFunc = (context, job) => $"{job.Type.Name}.{job.Method.Name}",
};
```

## CaptureCultureAttribute

Runs a job with the culture of the code that created it. A job enqueued during a French request formats dates, numbers and resource strings in French when it runs, even though the server's own culture is different. It is registered globally by default, so you rarely apply it yourself.

```csharp
using System.Globalization;

public class LetterService
{
    public Task PrintLetter(int customerId)
    {
        // Uses fr-FR when the job was created under fr-FR.
        var date = DateTime.Today.ToString("D", CultureInfo.CurrentCulture);
        return Task.CompletedTask;
    }
}

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
jobs.Enqueue<LetterService>(x => x.PrintLetter(7));
```

| Parameter / property | Type | Default | Meaning |
|----------------------|------|---------|---------|
| `defaultCultureName` (constructor) | `string?` | `null` | Culture used for jobs that carry no `CurrentCulture` parameter, such as jobs created by older versions. |
| `defaultUICultureName` (constructor) | `string?` | same as `defaultCultureName` | UI culture used for jobs that carry neither culture parameter. |
| `captureDefault` (constructor) | `bool` | `true` | `false`: don't store a culture parameter when the creating code's culture equals the default, which saves a parameter per job. |
| `DefaultCultureName`, `DefaultUICultureName`, `CaptureDefault` | | | The constructor values. |
| `CachedCulture` | `bool` | `false` | `true`: use `CultureInfo.GetCultureInfo` (cached, ignores user overrides set in the operating system) instead of `new CultureInfo(name)`. |
| `Order` | `int` | `-1` | Filter order. |

Constructors: `CaptureCulture()`, `CaptureCulture(defaultCultureName, captureDefault = true)`, `CaptureCulture(defaultCultureName, defaultUICultureName, captureDefault = true)`.

How it works: a client filter and a server filter.

- `OnCreating` stores `CultureInfo.CurrentCulture.Name` in the `CurrentCulture` job parameter and `CultureInfo.CurrentUICulture.Name` in `CurrentUICulture`. Under [compatibility level](../configuration/compatibility-levels.md) `Version_180`, `CurrentUICulture` is left out when it equals `CurrentCulture`.
- `OnPerforming` sets the worker thread's culture and UI culture from those parameters (the UI culture falls back to the culture, then to the defaults) and remembers the previous ones; `OnPerformed` restores them. An unknown culture name is logged as a warning and the job runs with the thread's current culture.

### Set a default culture

`UseDefaultCulture` registers another `CaptureCultureAttribute` with your defaults. Because it is added after the built-in one, it replaces it.

```csharp
using System.Globalization;
using Masterly.NightShift;

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Jobs"))
    .UseDefaultCulture(CultureInfo.GetCultureInfo("en-US")));
```

The overloads are `UseDefaultCulture(culture)`, `UseDefaultCulture(culture, captureDefault)`, `UseDefaultCulture(culture, uiCulture)` and `UseDefaultCulture(culture, uiCulture, captureDefault)`:

```csharp
config.UseDefaultCulture(CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("en-US"), captureDefault: false);
```

### A fixed culture for one job

```csharp
public class FixedCultureService
{
    // Falls back to en-GB for jobs without culture parameters.
    [CaptureCulture("en-GB", captureDefault: true)]
    public Task ExportCsv(int reportId) => Task.CompletedTask;
}
```

To stop capturing cultures entirely, remove the filter: `GlobalJobFilters.Filters.Remove<CaptureCultureAttribute>()`.

## LogInvocationAttribute

Writes the start of the job method, its end with the elapsed time, and any exception to the job's console, so the dashboard's job details page shows them live without code in the job.

```csharp
using Masterly.NightShift;

[LogInvocation]
public class BillingService
{
    public Task ChargeCard(int orderId) => Task.CompletedTask;

    public Task IssueRefund(int orderId) => Task.CompletedTask;
}
```

The console then shows lines like:

```text
Starting BillingService.ChargeCard (Generated invoker)
Finished in 182 ms
```

or, on failure, `Failed after 3,015 ms: HttpRequestException: Connection refused` at the Error level.

It has no parameters; `Order` is `0`. It is an interceptor (`AsyncJobInterceptorAttribute`): apply it to a method or a class, or to every job with `GlobalConfiguration.Configuration.UseJobInterceptor(new LogInvocationAttribute())`. The start and end lines are written at the `Debug` console level, the failure line at `Error`. The exception is re-thrown, so retries work as usual. For the application log (as opposed to the job console), see [Logging](../monitoring/logging.md).

## RedactedAttribute

Marks a job parameter whose value the dashboard must not show: passwords, tokens, personal data.

```csharp
using Masterly.NightShift;

public class FtpService
{
    public Task Upload(string host, string userName, [Redacted] string password, string path) => Task.CompletedTask;
}

jobs.Enqueue<FtpService>(x => x.Upload("ftp.example.com", "reports", secret, "/daily.csv"));
```

What the dashboard does with a redacted parameter:

- Every page that renders the method call shows `"••••••"` (`RedactedAttribute.Mask`) instead of the value, and so do a `[JobDisplayName]` and a `[System.ComponentModel.DisplayName]` that refer to the argument.
- "Copy as code" writes `/* password: redacted */ ""` (or `default` for non-strings) instead of the value.
- The JSON export of a job writes the mask.
- "Run again with changes" and the Job Catalog run form leave the field empty, so you type it again.

It has no properties. `RedactedAttribute.IsRedacted(ParameterInfo)` tells whether a parameter is marked. It applies to parameters only, and it is not a filter, so it needs no registration.

### On an interface

When jobs are created through an interface, mark the interface's parameter, because that is the method the job records:

```csharp
public interface IIdentityService
{
    Task ResetPassword(int userId, [Redacted] string temporaryPassword);
}

jobs.Enqueue<IIdentityService>(x => x.ResetPassword(42, secret));
```

### Things to know about redaction

- **The value is still stored.** Arguments are serialized into the job in storage as usual. Redaction protects dashboard viewers, not the database. For real secrets, pass a reference (a key vault name, a token id) instead of the secret.
- **Templates get a hash, not the value.** Resource, fingerprint and tag templates (`Throttle`, `RateLimit`, `CircuitBreaker`, `DisableConcurrentExecution`, `PreventDuplicates`, `Tag`) end up in storage keys, lock names and job history. A redacted argument is replaced there with the first 16 hexadecimal digits of the SHA-256 of its text, so different values still get different keys. A hash of a short or guessable value (a PIN, a phone number) can be guessed back, so don't key on secrets when people who must not know them can read the storage or the dashboard.
- **Queue templates get the real value.** A `[Queue]` template must produce a queue name that servers listen to, so a redacted argument in it is used as it is, and the queue name shows it on the dashboard. Don't build queue names from secrets.
- **Some display names still see the raw value.** `DashboardOptions.DisplayNameFunc` and a `JobDisplayNameAttribute` subclass that overrides `Format` get the job's real arguments. Don't put a redacted argument in them.

## See also

- [Queues and workers](../server/queues-and-workers.md)
- [Dashboard setup](../dashboard/setup.md)
- [Dashboard pages](../dashboard/pages.md)
- [Logging](../monitoring/logging.md)
- [Compatibility levels](../configuration/compatibility-levels.md)
- [Job arguments](../jobs/job-arguments.md)
- [Custom filters](custom-filters.md)
