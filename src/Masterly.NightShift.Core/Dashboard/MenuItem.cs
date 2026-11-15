// This file is part of Hangfire. Copyright © 2015 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// An item of a dashboard menu: its text, link and icon, whether it is active, and its metrics.
/// </summary>
public class MenuItem(string text, string url)
{
    public string Text { get; } = text;
    public string Url { get; } = url;

    public bool Active { get; set; }

    /// <summary>
    /// Gets or sets the icon shown next to the item in the sidebar: the name of a Bootstrap Icon of the dashboard's
    /// icon sprite, without the <c>bi-</c> prefix, for example <c>"clock"</c>. A Glyphicon name the dashboard used
    /// before Bootstrap 5, such as <c>"time"</c>, gives the icon that replaced it. Items without an icon, or with a
    /// name the sprite doesn't hold, get a chevron.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Gets or sets the group of the navigation drawer the item is listed in: one of the <see cref="NavigationMenuGroup"/>
    /// names (<c>Jobs</c>, <c>Workflows</c>, <c>Observe</c>, <c>System</c>), or any other text, which lists the item
    /// in a group of that title after the built-in ones. Items without a group are listed last, under "More". Only
    /// <see cref="NavigationMenu"/> reads it.
    /// </summary>
    public string? Group { get; set; }

    public DashboardMetric? Metric { get; set; }
    public DashboardMetric[]? Metrics { get; set; }

    public IEnumerable<DashboardMetric> GetAllMetrics()
    {
        List<DashboardMetric> metrics = [Metric!]; // Nulls are removed below

        if (Metrics != null)
        {
            metrics.AddRange(Metrics);
        }

        return [.. metrics.Where(static x => x != null)];
    }
}