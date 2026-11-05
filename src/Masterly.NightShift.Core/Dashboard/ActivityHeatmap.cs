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

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>One hour of the heatmap.</summary>
    internal sealed class HeatmapCell
    {
        public HeatmapCell(DateTime hourUtc, long succeeded, long failed)
        {
            HourUtc = hourUtc;
            Succeeded = succeeded;
            Failed = failed;
        }

        public DateTime HourUtc { get; }
        public long Succeeded { get; }
        public long Failed { get; }
        public long Total => Succeeded + Failed;
    }

    /// <summary>
    /// The last seven days hour by hour, from the <c>stats:succeeded:yyyy-MM-dd-HH</c> and <c>stats:failed:...</c>
    /// counters that <see cref="StatisticsHistoryAttribute"/> writes (kept for eight days). Drawn on the Insights
    /// page as a day × hour grid.
    /// </summary>
    internal static class ActivityHeatmap
    {
        public const int Days = 7;

        /// <summary>Rows are days (oldest first), each with 24 hourly cells, in UTC.</summary>
        public static IReadOnlyList<IReadOnlyList<HeatmapCell>> Read(IStorageConnection connection, DateTime utcNow)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var storageConnection = connection as JobStorageConnection;
            var rows = new List<IReadOnlyList<HeatmapCell>>(Days);
            var currentHour = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);

            for (var day = Days - 1; day >= 0; day--)
            {
                var date = currentHour.Date.AddDays(-day);
                var cells = new List<HeatmapCell>(24);

                for (var hour = 0; hour < 24; hour++)
                {
                    var moment = date.AddHours(hour);
                    if (moment > currentHour || storageConnection == null)
                    {
                        cells.Add(new HeatmapCell(moment, 0, 0));
                        continue;
                    }

                    var suffix = moment.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
                    cells.Add(new HeatmapCell(moment, storageConnection.GetCounter("stats:succeeded:" + suffix), storageConnection.GetCounter("stats:failed:" + suffix)));
                }

                rows.Add(cells);
            }

            return rows;
        }

        /// <summary>The busiest hour's total, used to scale the colours; at least 1.</summary>
        public static long Max(IReadOnlyList<IReadOnlyList<HeatmapCell>> rows)
        {
            return Math.Max(1, rows.SelectMany(static r => r).Max(static c => c.Total));
        }

        public static bool IsEmpty(IReadOnlyList<IReadOnlyList<HeatmapCell>> rows)
        {
            return rows.SelectMany(static r => r).All(static c => c.Total == 0);
        }
    }
}
