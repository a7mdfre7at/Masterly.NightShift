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
    IConfiguration? configuration = null) : IHostedService
{
    private static readonly Action<ILogger, int, int, Exception?> LogApplied = LoggerMessage.Define<int, int>(
        LogLevel.Information,
        new EventId(1, "RecurringJobsApplied"),
        "Applied {AppliedCount} recurring jobs declared in code and removed {RemovedCount} undeclared ones.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RecurringJobSet jobs = new();

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
}
