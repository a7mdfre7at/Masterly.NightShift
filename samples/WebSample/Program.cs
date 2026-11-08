// An ASP.NET Core app that creates jobs, runs a server, and serves the dashboard to signed-in operators only.
// It uses a SQL Server database, created on first run if it doesn't exist (with its tables): by default the LocalDB
// database named below. To use another one, set ConnectionStrings:NightShift, for example with the
// ConnectionStrings__NightShift environment variable.
//
//   dotnet run --project samples/WebSample
//
// Properties/launchSettings.json runs it in the Development environment on http://localhost:5000. Open
// http://localhost:5000/nightshift: you are sent to /login, which signs you in as an operator and exists only in
// Development; replace it with your real authentication. Without the launch profile (dotnet run --no-launch-profile,
// or the published app) the app runs in Production, where /login is not mapped: the dashboard redirects to it and
// gets 404 until you add a real sign-in.

using System.Security.Claims;
using Masterly.NightShift;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using NightShiftSamples;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("NightShift")
    ?? @"Server=(localdb)\MSSQLLocalDB;Database=Masterly.NightShift.WebSample;Integrated Security=True;TrustServerCertificate=True;";

// The storage creates its tables but not the database; the sample creates it on a fresh machine
SampleDatabase.EnsureExists(connectionString);

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString)
    .UseActivityFeed()
    .UseJobInsights());

builder.Services.AddNightShiftServer();

// Operators sign in with a cookie. The dashboard trusts that cookie, so antiforgery must be on (below).
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/login");

builder.Services.AddAuthorization(options =>
    options.AddPolicy("NightShiftOperators", policy => policy.RequireRole("Operations")));

// With IAntiforgery registered, every dashboard button sends a token and a POST without a valid one gets 403
builder.Services.AddAntiforgery();

// Liveness: the server in this process sends heartbeats. Readiness: the storage answers and a server is alive.
builder.Services.AddHealthChecks()
    .AddNightShift(options => options.Scope = NightShiftHealthCheckScope.Process, name: "nightshift-server", tags: ["live"])
    .AddNightShift(name: "nightshift-cluster", tags: ["ready"]);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate<ReportJobs>("daily-report", jobs => jobs.SendDailyReport(), Cron.Daily(6));

app.MapGet("/", () => Results.Text("Masterly.NightShift web sample. POST /reports to enqueue a job, open /nightshift for the dashboard."));

app.MapPost("/reports", (IBackgroundJobClient jobs) =>
{
    var jobId = jobs.Enqueue<ReportJobs>(x => x.SendDailyReport());
    return jobId == null ? Results.Problem("A filter canceled the job.") : Results.Accepted(value: new { jobId });
});

if (app.Environment.IsDevelopment())
{
    // Development only: signs the browser in as an operator. Use OpenID Connect, ASP.NET Core Identity or your
    // company's sign-in in a real app.
    app.MapGet("/login", async (HttpContext context, string? returnUrl) =>
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "developer"), new Claim(ClaimTypes.Role, "Operations")],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        // Only local paths, so the link can't send the browser to another site
        var isLocal = returnUrl is { Length: > 1 } && returnUrl[0] == '/' && returnUrl[1] != '/' && returnUrl[1] != '\\';
        return Results.LocalRedirect(isLocal ? returnUrl! : "/nightshift");
    });
}

// The policy replaces the default local-requests-only filter; the audit log records the operator's name
app.MapNightShiftDashboardWithAuthorizationPolicy("NightShiftOperators");

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

public sealed class ReportJobs
{
    private readonly ILogger<ReportJobs> _logger;

    public ReportJobs(ILogger<ReportJobs> logger)
    {
        _logger = logger;
    }

    public void SendDailyReport()
    {
        _logger.LogInformation("Sending the daily report.");
    }
}
