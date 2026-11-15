using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

// Markup the dashboard writes in many places, for the assertions on pages rendered at the root (no path prefix)
internal static class DashboardMarkup
{
    // What Html.Icon writes for an icon of the sprite
    public static string Icon(string name, string? cssClass = null)
    {
        string css = cssClass == null ? "bi" : $"bi {cssClass}";
        return $"<svg class=\"{css}\" aria-hidden=\"true\"><use href=\"{DashboardIcons.Path}#bi-{name}\"></use></svg>";
    }

    // An icon that says something on its own: the wrapper carries the title and the accessible name
    public static string TitledIcon(string name, string title, string? cssClass = null)
    {
        string css = cssClass == null ? "hf-icon" : $"hf-icon {cssClass}";
        return $"<span class=\"{css}\" title=\"{title}\" role=\"img\" aria-label=\"{title}\">{Icon(name)}</span>";
    }
}
