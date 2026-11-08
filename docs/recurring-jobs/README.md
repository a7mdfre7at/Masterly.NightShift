# Recurring jobs

A recurring job is a method call stored together with a schedule. You register it once under an ID (`daily-report`, `purge-sessions`), and the recurring job scheduler, which runs inside every regular `BackgroundJobServer`, creates and enqueues a normal background job each time the schedule comes due. Use recurring jobs for nightly reports, cleanups, polling an external system, digests and anything else that runs on a timetable. The schedule is a CRON expression, which you can write by hand, build with `Cron` helpers or `Cron.Builder()`, or write in plain English with `Cron.FromText("weekdays at 9:30")`.

| Page | What it covers |
|------|----------------|
| [Creating recurring jobs](creating-recurring-jobs.md) | `AddOrUpdate` overloads, recurring job IDs, `RecurringJobOptions` (including start and end dates), triggering a run now, removing jobs, what changes when you register the same ID again |
| [CRON and schedules](cron-and-schedules.md) | The CRON format, every `Cron.*` helper, `Cron.Builder()`, plain-English schedules with `Cron.FromText`, `Cron.Describe` and previews |
| [Pausing and updating](pausing-and-updating.md) | `Pause`, `Resume` and `UpdateSchedule`: change when and where a job runs without redeploying |
| [Time zones and misfires](time-zones-and-misfires.md) | Running a schedule in a time zone, daylight saving changes, server downtime, `MisfireHandlingMode`, how start and end dates bound the schedule, `ITimeZoneResolver` |
| [Recurring job manifest](recurring-job-manifest.md) | Export every recurring job to JSON and apply it back, to keep schedules in source control and move them between environments |

The most common case: register a job at application start-up, using the `IRecurringJobManager` that `AddNightShift` puts in the service container.

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage("Server=.;Database=Jobs;Integrated Security=True;TrustServerCertificate=True;"));
builder.Services.AddNightShiftServer();
builder.Services.AddScoped<ReportService>();

var app = builder.Build();

var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobs.AddOrUpdate<ReportService>(
    "daily-report",
    service => service.BuildDaily(),
    Cron.Daily(6));             // "0 6 * * *": every day at 06:00 UTC

app.Run();

public class ReportService
{
    public void BuildDaily()
    {
        // build and send the report
    }
}
```

Without dependency injection, the static facade does the same: `RecurringJob.AddOrUpdate<ReportService>("daily-report", service => service.BuildDaily(), Cron.Daily(6));`.

Recurring jobs are listed on the dashboard's **Recurring Jobs** page, where you can trigger, pause, resume, edit, remove, export and import them, and see a forecast of upcoming runs. See [Dashboard pages](../dashboard/pages.md).
