using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift;

/// <summary>Applies the recurring jobs the registered providers declare when the host starts.</summary>
internal sealed class RecurringJobSetHostedService(
    IEnumerable<IRecurringJobProvider> providers,
    IRecurringJobManager manager,
    JobStorage storage,
    ILogger<RecurringJobSetHostedService> logger,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : IHostedService
{
    private static readonly Action<ILogger, int, int, Exception?> LogApplied = LoggerMessage.Define<int, int>(
        LogLevel.Information,
        new EventId(1, "RecurringJobsApplied"),
        "Applied {AppliedCount} recurring jobs declared in code and removed {RemovedCount} undeclared ones.");

    private static readonly Action<ILogger, string, string, Exception?> LogNameNotUsed = LoggerMessage.Define<string, string>(
        LogLevel.Debug,
        new EventId(2, "RecurringJobSetNameNotUsed"),
        "The application name '{ApplicationName}' can't name the recurring job set, which is named '{SetName}'.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RecurringJobSet jobs = CreateSet();

        foreach (IRecurringJobProvider provider in providers)
        {
            provider.DeclareJobs(jobs);
        }

        if (configuration != null)
        {
            jobs.ResolvePlaceholders(key => configuration[key]);
        }

        RecurringJobSetResult result = jobs.Apply(manager, storage);
        LogApplied(logger, result.Applied.Count, result.Removed.Count, null);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // Named after the host's application, which the providers can change with Named
    private RecurringJobSet CreateSet()
    {
        RecurringJobSet jobs = new();
        string? applicationName = environment?.ApplicationName;

        if (string.IsNullOrWhiteSpace(applicationName))
        {
            return jobs;
        }

        try
        {
            jobs.Named(applicationName);
        }
        catch (ArgumentException ex)
        {
            // A name a set can't have (longer than 100 characters): the set keeps the entry assembly's name
            LogNameNotUsed(logger, applicationName, jobs.Name, ex);
        }

        return jobs;
    }
}
