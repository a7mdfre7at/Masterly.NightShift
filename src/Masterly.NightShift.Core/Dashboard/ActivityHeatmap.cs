#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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

        // A grid costs up to 336 counter reads, so each storage's grid is kept for a minute
        private static readonly ConditionalWeakTable<JobStorage, CachedGrid> Cache = new ConditionalWeakTable<JobStorage, CachedGrid>();

        internal static TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Like <see cref="Read(IStorageConnection, DateTime)"/>, but reuses the grid read for the same storage less
        /// than <see cref="CacheDuration"/> ago, within the same hour.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<HeatmapCell>> Read(JobStorage storage, DateTime utcNow)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            var cached = Cache.GetValue(storage, static _ => new CachedGrid());
            lock (cached)
            {
                if (cached.Rows != null && cached.ReadAt <= utcNow && utcNow - cached.ReadAt < CacheDuration && Hour(cached.ReadAt) == Hour(utcNow))
                {
                    return cached.Rows;
                }
            }

            IReadOnlyList<IReadOnlyList<HeatmapCell>> rows;
            using (var connection = storage.GetReadOnlyConnection())
            {
                // Counters need the extended API; without it the grid is empty
                rows = Read(storage.HasFeature(JobStorageFeatures.ExtendedApi) ? connection as JobStorageConnection : null, utcNow);
            }

            lock (cached)
            {
                cached.Rows = rows;
                cached.ReadAt = utcNow;
            }

            return rows;
        }

        /// <summary>
        /// Rows are days (oldest first), each with 24 hourly cells, in UTC. Every cell is empty when the storage
        /// can't read counters.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<HeatmapCell>> Read(IStorageConnection connection, DateTime utcNow)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            try
            {
                return Read(connection as JobStorageConnection, utcNow);
            }
            catch (NotSupportedException)
            {
                // A storage without the extended API (JobStorageFeatures.ExtendedApi) can't read counters
                return Read((JobStorageConnection?)null, utcNow);
            }
        }

        private static IReadOnlyList<IReadOnlyList<HeatmapCell>> Read(JobStorageConnection? storageConnection, DateTime utcNow)
        {
            var rows = new List<IReadOnlyList<HeatmapCell>>(Days);
            var currentHour = Hour(utcNow);

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

        private static DateTime Hour(DateTime utcNow)
        {
            return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);
        }

        private sealed class CachedGrid
        {
            public IReadOnlyList<IReadOnlyList<HeatmapCell>>? Rows { get; set; }
            public DateTime ReadAt { get; set; }
        }
    }
}
