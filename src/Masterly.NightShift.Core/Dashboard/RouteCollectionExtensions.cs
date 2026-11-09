// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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
using System.Text.RegularExpressions;
using Masterly.NightShift.Annotations;
using System.ComponentModel;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// Adds Razor pages, commands and other dispatchers to the dashboard's <see cref="RouteCollection"/>.
    /// </summary>
    public static class RouteCollectionExtensions
    {
        public static void AddRazorPage(
            [NotNull] this RouteCollection routes, 
            [NotNull] string pathTemplate, 
            [NotNull] Func<Match, RazorPage> pageFunc)
        {
            ArgumentNullException.ThrowIfNull(routes);
            ArgumentNullException.ThrowIfNull(pathTemplate);
            ArgumentNullException.ThrowIfNull(pageFunc);

            routes.Add(pathTemplate, new RazorPageDispatcher(pageFunc));
        }

        public static void AddCommand(
            [NotNull] this RouteCollection routes,
            [NotNull] string pathTemplate,
            [NotNull] Func<DashboardContext, bool> command)
        {
            ArgumentNullException.ThrowIfNull(routes);
            ArgumentNullException.ThrowIfNull(pathTemplate);
            ArgumentNullException.ThrowIfNull(command);

            routes.Add(pathTemplate, new CommandDispatcher(command));
        }

        public static void AddBatchCommand(
            [NotNull] this RouteCollection routes,
            [NotNull] string pathTemplate,
            [NotNull] Action<DashboardContext, string> command)
        {
            ArgumentNullException.ThrowIfNull(routes);
            ArgumentNullException.ThrowIfNull(pathTemplate);
            ArgumentNullException.ThrowIfNull(command);

            routes.Add(pathTemplate, new BatchCommandDispatcher(command));
        }

        public static void AddClientBatchCommand(
            this RouteCollection routes,
            string pathTemplate, 
            [NotNull] Action<IBackgroundJobClient, string> command)
        {
            ArgumentNullException.ThrowIfNull(command);

            routes.AddBatchCommand(pathTemplate, (context, jobId) =>
            {
                var client = context.GetBackgroundJobClient();
                command(client, jobId);
            });
        }

        public static void AddRecurringBatchCommand(
            this RouteCollection routes,
            string pathTemplate,
            [NotNull] Action<IRecurringJobManager, string> command)
        {
            ArgumentNullException.ThrowIfNull(command);

            routes.AddBatchCommand(pathTemplate, (context, jobId) =>
            {
                var manager = context.GetRecurringJobManager();
                command(manager, jobId);
            });
        }
    }
}
