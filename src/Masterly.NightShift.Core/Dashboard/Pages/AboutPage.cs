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
        string? current = ReadCurrentDeployment();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Masterly.NightShift"] = version,
            ["JobStorage"] = Storage.ToString() ?? string.Empty,
            ["SetDataCompatibilityLevel"] = GlobalConfiguration.CompatibilityLevel.ToString(),
            ["UseDefaultRecurringJobTimeZone"] = RecurringJobOptions.DefaultTimeZone.Id,
            ["UseStaleRestartLimit"] = StaleJobFilter.MaxRestarts?.ToString(CultureInfo.CurrentCulture) ?? none,
            ["UseArgumentCompression"] = ArgumentCompression.MinLength?.ToString(CultureInfo.CurrentCulture) ?? none,
            ["UseDeploymentFencing"] = DeploymentFencing.DeploymentId is { } deployment
                ? current != null && current != deployment ? $"{deployment} ({current})" : deployment
                : none,
            ["UseScheduleDateProvider"] = ScheduleDateProviders.Names.Count > 0 ? string.Join(", ", ScheduleDateProviders.Names) : none,
            ["JobMonitors.CheckInterval"] = JobMonitors.CheckInterval.ToString(),
        };
    }

    private string? ReadCurrentDeployment()
    {
        try
        {
            return DeploymentFencing.DeploymentId != null ? DeploymentFencing.GetCurrentDeployment(Storage) : null;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException("The current deployment couldn't be read.", ex);
            return null;
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
            JobMonitorState.Healthy => "label-success",
            JobMonitorState.NeedsAttention => "label-danger",
            JobMonitorState.Unknown => "label-warning",
            _ => "label-default",
        };

        return new NonEscapedString($"<span class=\"label {css}\">{state}</span>");
    }
}
