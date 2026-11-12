using System.Globalization;

namespace Masterly.NightShift.Dashboard;

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
        int count = DefaultCount;
        if (int.TryParse(context.Request.GetQuery("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested))
        {
            count = Math.Min(Math.Max(1, requested), MaxCount);
        }

        long.TryParse(context.Request.GetQuery("since"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long since);

        IReadOnlyList<ActivityEntry> entries;
        using (IStorageConnection connection = context.Storage.GetReadOnlyConnection())
        {
            entries = ActivityFeed.Read(connection, count);
        }

        if (since > 0)
        {
            List<ActivityEntry> fresh = [];
            foreach (ActivityEntry entry in entries)
            {
                if (entry.Timestamp > since)
                {
                    fresh.Add(entry);
                }
            }

            entries = fresh;
        }

        return DashboardJson.WriteAsync(context, 200, new
        {
            now = (long)(JobClock.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
            enabled = ActivityFeed.Enabled,
            entries,
        });
    }
}
