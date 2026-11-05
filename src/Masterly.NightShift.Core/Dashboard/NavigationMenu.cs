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

using System;
using System.Collections.Generic;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard
{
    public static class NavigationMenu
    {
        public static readonly List<Func<RazorPage, MenuItem>> Items = new List<Func<RazorPage, MenuItem>>();

        static NavigationMenu()
        {
            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Jobs, page.Url.LinkToQueues())
            {
                Icon = "tasks",
                Active = page.RequestPath.StartsWith("/jobs", StringComparison.OrdinalIgnoreCase),
                Metrics = new []
                {
                    DashboardMetrics.EnqueuedCountOrNull,
                    DashboardMetrics.FailedCountOrNull
                }
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Batches, page.Url.To("/batches"))
            {
                Icon = "th-large",
                Active = page.RequestPath.StartsWith("/batches", StringComparison.OrdinalIgnoreCase)
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Retries, page.Url.To("/retries"))
            {
                Icon = "repeat",
                Active = page.RequestPath.StartsWith("/retries", StringComparison.OrdinalIgnoreCase),
                Metric = DashboardMetrics.RetriesCount
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_RecurringJobs, page.Url.To("/recurring"))
            {
                Icon = "time",
                Active = page.RequestPath.StartsWith("/recurring", StringComparison.OrdinalIgnoreCase),
                Metric = DashboardMetrics.RecurringJobCount
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Timeline, page.Url.To("/timeline"))
            {
                Icon = "align-left",
                Active = page.RequestPath.StartsWith("/timeline", StringComparison.OrdinalIgnoreCase)
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Insights, page.Url.To("/insights"))
            {
                Icon = "stats",
                Active = page.RequestPath.StartsWith("/insights", StringComparison.OrdinalIgnoreCase)
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Catalog, page.Url.To("/catalog"))
            {
                Icon = "book",
                Active = page.RequestPath.StartsWith("/catalog", StringComparison.OrdinalIgnoreCase)
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Tags, page.Url.To("/tags"))
            {
                Icon = "tag",
                Active = page.RequestPath.StartsWith("/tags", StringComparison.OrdinalIgnoreCase)
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Servers, page.Url.To("/servers"))
            {
                Icon = "hdd",
                Active = page.RequestPath.Equals("/servers", StringComparison.OrdinalIgnoreCase),
                Metric = DashboardMetrics.ServerCount
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Audit, page.Url.To("/audit"))
            {
                Icon = "list-alt",
                Active = page.RequestPath.StartsWith("/audit", StringComparison.OrdinalIgnoreCase)
            });

            Items.Add(static page => new MenuItem(Strings.NavigationMenu_Wallboard, page.Url.To("/wallboard"))
            {
                Icon = "blackboard",
                Active = false
            });
        }
    }
}