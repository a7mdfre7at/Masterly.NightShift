// The Masterly.NightShift Playground: one page with a button for every feature, an event log, and the dashboard.
//
//   dotnet run --project samples/Playground
//
// Then open http://localhost:5080. Jobs live in memory, so every restart starts clean.

using System.Text;
using Masterly.NightShift;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Playground;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.IncludeScopes = true);

// Alerts are also sent as webhooks to this app's own /webhook-inbox, so you can see the payloads
string baseUrl = builder.Configuration["Playground:Url"] ?? "http://localhost:5080";

// Monitors are checked every 5 seconds instead of 30, to see them change quickly
JobMonitors.CheckInterval = TimeSpan.FromSeconds(5);

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage()
    .UseActivityFeed()                       // home page activity, Timeline page
    .UseJobInsights()                        // Insights page, durations, ETA
    .UseTelemetry()                          // OpenTelemetry spans and metrics (watched by TelemetryWatcher)
    .UseResultsInContinuations()             // [FromResult] / [FromException] parameters
    .UseStaleRestartLimit(3)
    .UseArgumentCompression(1024)
    .UseDeploymentFencing("playground-1")
    .UseJobRetention(o => o.Succeeded = TimeSpan.FromHours(2))
    .UseAlerts(new EventLogAlertSink())
    .UseWebhookAlerts($"{baseUrl}/webhook-inbox", o => o.Format = WebhookFormat.Json)
    .UseJobMonitor(new LongRunningMonitor("slow-report", "MonitoredJobs.SlowReport", TimeSpan.FromSeconds(10)))
    .UseJobMonitor(new RepeatedFailuresMonitor("flaky-sync", "MonitoredJobs.FlakySync", failures: 2, window: TimeSpan.FromHours(1)))
    .UseJobMonitor(new DailyDeadlineMonitor("daily-close", "MonitoredJobs.DailyClose", DailyCloseDeadline.Value, TimeZoneInfo.Utc)));

builder.Services.AddNightShiftServer(o =>
{
    o.ServerName = "playground";
    o.WorkerCount = 8;
    o.Queues = ["critical", "default"];        // in priority order; "nowhere" is left out on purpose
    o.LongRunningWorkerCount = 1;              // a separate worker for [LongRunning] jobs
    o.SchedulePollingInterval = TimeSpan.FromSeconds(2);
    o.CancellationCheckInterval = TimeSpan.FromSeconds(2);
});

// ILogger lines written inside a job show in its console on the dashboard
builder.Services.AddNightShiftJobLogs(o => o.LifecycleLines = true);
builder.Services.AddNightShiftJobExceptionHandler<EventLogExceptionHandler>();

// Typed jobs and job functions
builder.Services.AddNightShiftJobHandler<SendInvoiceHandler>();
builder.Services.AddNightShiftFunctions(typeof(MaintenanceJobs).Assembly);
builder.Services.AddNightShiftFunction("cache.warm", (provider, token) =>
{
    PlaygroundLog.Instance.Add("function", "cache.warm ran: a job function registered as a delegate");
    return Task.CompletedTask;
});

// Recurring jobs and schedule policies declared in code: they get the "From code" badge
builder.Services.AddNightShiftRecurringJobs(jobs => jobs
    .AddPolicy(new SchedulePolicy("business-hours")
        .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
        .AllowBetween("09:00", "17:00")
        .ExcludeAnnualDate(12, 25)
        .WithDescription("Weekdays 09:00-17:00 UTC, not on Christmas"))
    .Add<RecurringJobs>("code:heartbeat", x => x.Heartbeat(), Cron.Every(TimeSpan.FromMinutes(2)))
    .Add<RecurringJobs>("code:business-report", x => x.BusinessReport(), Cron.Hourly(),
        new RecurringJobOptions { Policy = "business-hours" }));

// The Catalog page offers these methods in its Run... form
JobCatalog.Add<MaintenanceJobs>();

builder.Services.AddHostedService<TelemetryWatcher>();
builder.Services.AddAntiforgery();
builder.Services.AddHealthChecks()
    .AddNightShift(o => o.Scope = NightShiftHealthCheckScope.Process, name: "nightshift-server", tags: ["live"])
    .AddNightShift(name: "nightshift-cluster", tags: ["ready"]);

WebApplication app = builder.Build();

app.MapNightShiftDashboard(new DashboardOptions { DashboardTitle = "NightShift Playground", AppPath = "/" });
app.MapNightShiftDashboard("/nightshift-view", new DashboardOptions { DashboardTitle = "Read-only view", IsReadOnlyFunc = static _ => true });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapGet("/", () => Results.Content(HomePage.Render(Experiments.All), "text/html; charset=utf-8"));
app.MapGet("/events", (long? after) => Results.Json(PlaygroundLog.Instance.Since(after ?? 0)));
app.MapPost("/events/clear", () => PlaygroundLog.Instance.Clear());

app.MapPost("/try/{id}", async (string id, IServiceProvider services) =>
{
    Experiment? experiment = Experiments.All.FirstOrDefault(x => x.Id == id);
    if (experiment?.Run == null) return Results.NotFound();

    try
    {
        string html = await experiment.Run(new PlaygroundContext(services));
        return Results.Json(new { ok = true, html });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, html = System.Net.WebUtility.HtmlEncode($"{ex.GetType().Name}: {ex.Message}") });
    }
});

app.MapPost("/webhook-inbox", async (HttpRequest request) =>
{
    using StreamReader reader = new(request.Body, Encoding.UTF8);
    string body = await reader.ReadToEndAsync();
    PlaygroundLog.Instance.Add("webhook", body.Length > 600 ? body[..600] + "…" : body);
    return Results.Ok();
});

app.Run();
