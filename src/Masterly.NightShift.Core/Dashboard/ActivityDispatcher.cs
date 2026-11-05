// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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
using System.Globalization;
using System.Threading.Tasks;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /activity?count=50&amp;since={unix ms}</c>: the newest entries of the <see cref="ActivityFeed"/>,
    /// newest first, optionally only those after <c>since</c>. Read-only.
    /// </summary>
    internal sealed class ActivityDispatcher : IDashboardDispatcher
    {
        private const int DefaultCount = 50;
        private const int MaxCount = 200;

        public Task Dispatch(DashboardContext context)
        {
            var count = DefaultCount;
            if (Int32.TryParse(context.Request.GetQuery("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested))
            {
                count = Math.Min(Math.Max(1, requested), MaxCount);
            }

            long since = 0;
            Int64.TryParse(context.Request.GetQuery("since"), NumberStyles.Integer, CultureInfo.InvariantCulture, out since);

            IReadOnlyList<ActivityEntry> entries;
            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                entries = ActivityFeed.Read(connection, count);
            }

            if (since > 0)
            {
                var fresh = new List<ActivityEntry>();
                foreach (var entry in entries)
                {
                    if (entry.Timestamp > since) fresh.Add(entry);
                }

                entries = fresh;
            }

            return DashboardJson.WriteAsync(context, 200, new
            {
                now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                enabled = ActivityFeed.Enabled,
                entries,
            });
        }
    }
}
