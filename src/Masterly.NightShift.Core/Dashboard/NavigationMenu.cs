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

using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// The items of the dashboard's top navigation bar. Extensions can add items.
/// </summary>
public static class NavigationMenu
{
    public static readonly List<Func<RazorPage, MenuItem>> Items = [];

    static NavigationMenu()
    {
        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Jobs, page.Url.LinkToQueues())
        {
            Icon = "list-task",
            Active = page.RequestPath.StartsWith("/jobs", StringComparison.OrdinalIgnoreCase),
            Metrics =
            [
                DashboardMetrics.EnqueuedCountOrNull,
                DashboardMetrics.FailedCountOrNull
            ]
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Batches, page.Url.To("/batches"))
        {
            Icon = "grid-fill",
            Active = page.RequestPath.StartsWith("/batches", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Chains, page.Url.To("/chains"))
        {
            Icon = "link-45deg",
            Active = page.RequestPath.StartsWith("/chains", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Retries, page.Url.To("/retries"))
        {
            Icon = "arrow-repeat",
            Active = page.RequestPath.StartsWith("/retries", StringComparison.OrdinalIgnoreCase),
            Metric = DashboardMetrics.RetriesCount
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_RecurringJobs, page.Url.To("/recurring"))
        {
            Icon = "clock",
            Active = page.RequestPath.StartsWith("/recurring", StringComparison.OrdinalIgnoreCase),
            Metric = DashboardMetrics.RecurringJobCount
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Policies, page.Url.To("/policies"))
        {
            Icon = "calendar3",
            Active = page.RequestPath.StartsWith("/policies", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Timeline, page.Url.To("/timeline"))
        {
            Icon = "text-left",
            Active = page.RequestPath.StartsWith("/timeline", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Insights, page.Url.To("/insights"))
        {
            Icon = "bar-chart-fill",
            Active = page.RequestPath.StartsWith("/insights", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Catalog, page.Url.To("/catalog"))
        {
            Icon = "book-fill",
            Active = page.RequestPath.StartsWith("/catalog", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Tags, page.Url.To("/tags"))
        {
            Icon = "tag-fill",
            Active = page.RequestPath.StartsWith("/tags", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Servers, page.Url.To("/servers"))
        {
            Icon = "hdd-fill",
            Active = page.RequestPath.Equals("/servers", StringComparison.OrdinalIgnoreCase),
            Metric = DashboardMetrics.ServerCount
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Audit, page.Url.To("/audit"))
        {
            Icon = "card-list",
            Active = page.RequestPath.StartsWith("/audit", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_About, page.Url.To("/about"))
        {
            Icon = "info-circle-fill",
            Active = page.RequestPath.StartsWith("/about", StringComparison.OrdinalIgnoreCase)
        });

        Items.Add(static page => new MenuItem(Strings.NavigationMenu_Wallboard, page.Url.To("/wallboard"))
        {
            Icon = "easel-fill",
            Active = false
        });
    }
}