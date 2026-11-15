using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// The groups of the dashboard's navigation drawer, in the order it lists them. Set <see cref="MenuItem.Group"/> of an
/// item added to <see cref="NavigationMenu.Items"/> to one of them to list it there; an item without a group is listed
/// last, under "More".
/// </summary>
public static class NavigationMenuGroup
{
    /// <summary>Jobs and their lists: the job states, retries, recurring jobs, the catalog, tags.</summary>
    public const string Jobs = "Jobs";

    /// <summary>Work made of several jobs: batches, chains, schedule policies.</summary>
    public const string Workflows = "Workflows";

    /// <summary>Watching the cluster: insights, the timeline, the audit log, the wallboard.</summary>
    public const string Observe = "Observe";

    /// <summary>The servers and the dashboard itself.</summary>
    public const string System = "System";

    /// <summary>The built-in groups, in the order of the drawer.</summary>
    internal static readonly string[] BuiltIn = [Jobs, Workflows, Observe, System];

    /// <summary>The title the drawer shows for a group: the localized title of a built-in group, else the name itself.</summary>
    internal static string TitleOf(string? group)
    {
        return group switch
        {
            null => Strings.NavigationMenu_Group_More,
            Jobs => Strings.NavigationMenu_Group_Jobs,
            Workflows => Strings.NavigationMenu_Group_Workflows,
            Observe => Strings.NavigationMenu_Group_Observe,
            System => Strings.NavigationMenu_Group_System,
            _ => group,
        };
    }
}
