using System.Globalization;
using System.Reflection;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class AboutPage
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(AboutPage));

    /// <summary>The settings in effect in this process, by the method that changes each.</summary>
    private Dictionary<string, string> GetSettings()
    {
        Assembly assembly = typeof(JobStorage).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;

        string none = Resources.Strings.AboutPage_None;
        (string? current, long? currentOrder) = ReadCurrentDeployment();

        Dictionary<string, string> settings = new(StringComparer.Ordinal)
        {
            ["Masterly.NightShift"] = version,
            ["JobStorage"] = Storage.ToString() ?? string.Empty,
            ["SetDataCompatibilityLevel"] = GlobalConfiguration.CompatibilityLevel.ToString(),
            ["UseDefaultRecurringJobTimeZone"] = RecurringJobOptions.DefaultTimeZone.Id,
            ["UseStaleRestartLimit"] = StaleJobFilter.MaxRestarts?.ToString(CultureInfo.CurrentCulture) ?? none,
            ["UseArgumentCompression"] = ArgumentCompression.MinLength?.ToString(CultureInfo.CurrentCulture) ?? none,
            ["UseDeploymentFencing"] = DeploymentFencing.DeploymentId is { } deployment
                ? DescribeDeployment(deployment, DeploymentFencing.DeploymentOrder, current, currentOrder)
                : none,
            ["UseScheduleDateProvider"] = ScheduleDateProviders.Names.Count > 0 ? string.Join(", ", ScheduleDateProviders.Names) : none,
            ["JobMonitors.CheckInterval"] = JobMonitors.CheckInterval.ToString(),
        };

        // What the storage says about itself can hold its connection string: shown only where the footer shows it
        if (!DashboardOptions.DisplayStorageConnectionString)
        {
            settings.Remove("JobStorage");
        }

        return settings;
    }

    /// <summary>
    /// This process's deployment, with its order when it has one, followed by the current deployment in brackets
    /// when it is another one: <c>2026.11.2 (order 41) (2026.11.3, order 42)</c>.
    /// </summary>
    internal static string DescribeDeployment(string deployment, long? order, string? current, long? currentOrder)
    {
        string own = DeploymentFencing.Describe(deployment, order);
        if (current == null || DeploymentFencing.IsSame(current, deployment))
        {
            return own;
        }

        return currentOrder.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{own} ({current}, order {currentOrder.Value})")
            : $"{own} ({current})";
    }

    private (string? Id, long? Order) ReadCurrentDeployment()
    {
        try
        {
            return DeploymentFencing.DeploymentId != null ? DeploymentFencing.ReadCurrent(Storage) : (null, null);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException("The current deployment couldn't be read.", ex);
            return (null, null);
        }
    }

    private IReadOnlyList<JobMonitorStatus> ReadMonitors()
    {
        try
        {
            return JobMonitors.GetStatus(Storage);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException("The monitors couldn't be read.", ex);
            return [];
        }
    }

    // A monitor's state as a chip in the colours of the job state it is like: healthy as succeeded, needing
    // attention as failed, unknown as processing, any other (waiting for its first check) as awaiting
    private NonEscapedString MonitorLabel(JobMonitorState state)
    {
        string like = state switch
        {
            JobMonitorState.Healthy => SucceededState.StateName,
            JobMonitorState.NeedsAttention => FailedState.StateName,
            JobMonitorState.Unknown => ProcessingState.StateName,
            _ => AwaitingState.StateName,
        };

        return Html.StateLabel(like, state.ToString());
    }

    // The optional features the storage reports, by id; asking reads its configuration, not the database
    private List<KeyValuePair<string, bool>> GetStorageFeatures()
    {
        string[] features =
        [
            JobStorageFeatures.ExtendedApi,
            JobStorageFeatures.JobQueueProperty,
            JobStorageFeatures.Purge,
            JobStorageFeatures.Connection.GetUtcDateTime,
            JobStorageFeatures.Connection.GetSetContains,
            JobStorageFeatures.Connection.LimitedGetSetCount,
            JobStorageFeatures.Connection.BatchedGetFirstByLowest,
            JobStorageFeatures.Connection.GetQueueEntryId,
            JobStorageFeatures.Transaction.AcquireDistributedLock,
            JobStorageFeatures.Transaction.CreateJob,
            JobStorageFeatures.Transaction.SetJobParameter,
            JobStorageFeatures.Monitoring.DeletedStateGraphs,
            JobStorageFeatures.Monitoring.AwaitingJobs,
            JobStorageFeatures.Monitoring.QueueCounts,
        ];

        List<KeyValuePair<string, bool>> result = new(features.Length);
        foreach (string feature in features)
        {
            bool supported;
            try
            {
                supported = Storage.HasFeature(feature);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.DebugException($"The storage couldn't say whether it supports '{feature}'.", ex);
                supported = false;
            }

            result.Add(new KeyValuePair<string, bool>(feature, supported));
        }

        return result;
    }
}
