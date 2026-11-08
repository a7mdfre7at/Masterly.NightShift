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
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// Maps dashboard paths, matched as regular expressions, to the dispatchers that answer them.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Public API, can not change in minor versions.")]
    public class RouteCollection
    {
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        // Each pattern is parsed once: the static Regex cache only holds 15 patterns, fewer than the routes
        private readonly List<Tuple<Regex, IDashboardDispatcher>> _dispatchers
            = new List<Tuple<Regex, IDashboardDispatcher>>();

        public void Add([NotNull] string pathTemplate, [NotNull] IDashboardDispatcher dispatcher)
        {
            if (pathTemplate == null) throw new ArgumentNullException(nameof(pathTemplate));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));

            var pattern = pathTemplate;

            if (!pattern.StartsWith("^", StringComparison.OrdinalIgnoreCase))
                pattern = "^" + pattern;
            if (!pattern.EndsWith("$", StringComparison.OrdinalIgnoreCase))
                pattern += "$";

            var regex = new Regex(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
                MatchTimeout);

            _dispatchers.Add(new Tuple<Regex, IDashboardDispatcher>(regex, dispatcher));
        }

        public Tuple<IDashboardDispatcher, Match> FindDispatcher(string path)
        {
            return FindDispatcher(path, null);
        }

        /// <summary>
        /// Finds the first route that matches the path. For a <c>GET</c> or <c>HEAD</c> request, a route that only
        /// accepts <c>POST</c> (a command) is skipped when a later route matches too, so a page whose path looks like
        /// a command (the "delete" queue's <c>/jobs/enqueued/delete</c>) stays reachable.
        /// </summary>
        /// <param name="path">The request path relative to the dashboard, like <c>"/jobs/failed"</c>.</param>
        /// <param name="method">The HTTP method, or <c>null</c> to take the first match whatever it is.</param>
        /// <returns>The dispatcher and the match, or <c>null</c> when no route matches.</returns>
        public Tuple<IDashboardDispatcher, Match> FindDispatcher(string path, [CanBeNull] string method)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            if (path.Length == 0) path = "/";
            else if (path.Length > 1) path = path.TrimEnd('/');

            var skipCommands = "GET".Equals(method, StringComparison.OrdinalIgnoreCase)
                || "HEAD".Equals(method, StringComparison.OrdinalIgnoreCase);

            Tuple<IDashboardDispatcher, Match> command = null;

            foreach (var dispatcher in _dispatchers)
            {
                var match = dispatcher.Item1.Match(path);
                if (!match.Success) continue;

                if (skipCommands && dispatcher.Item2 is IDashboardCommandDispatcher)
                {
                    // Answers 405 if nothing else matches
                    command ??= new Tuple<IDashboardDispatcher, Match>(dispatcher.Item2, match);
                    continue;
                }

                return new Tuple<IDashboardDispatcher, Match>(dispatcher.Item2, match);
            }

            return command;
        }
    }

    /// <summary>A dispatcher that only accepts <c>POST</c>, so <c>GET</c> requests may go to a later route.</summary>
    internal interface IDashboardCommandDispatcher : IDashboardDispatcher
    {
    }
}
