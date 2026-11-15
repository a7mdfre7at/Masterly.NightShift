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

        return new Dictionary<string, string>(StringComparer.Ordinal)
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

    private static NonEscapedString MonitorLabel(JobMonitorState state)
    {
        string css = state switch
        {
            JobMonitorState.Healthy => "text-bg-success",
            JobMonitorState.NeedsAttention => "text-bg-danger",
            JobMonitorState.Unknown => "text-bg-warning",
            _ => "text-bg-secondary",
        };

        return new NonEscapedString($"<span class=\"badge {css}\">{state}</span>");
    }
}
