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
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Common;

namespace Hangfire.Dashboard
{
    /// <summary>
    /// <c>GET /recurring/preview?cron=...&amp;timezone=...</c>: resolves plain-English schedules and
    /// returns the next occurrences, so the schedule editor can show what it is about to save.
    /// Always answers <c>200</c>; problems are reported in the <c>error</c> field.
    /// </summary>
    internal sealed class CronPreviewDispatcher : IDashboardDispatcher
    {
        private const int OccurrenceCount = 5;

        public Task Dispatch(DashboardContext context)
        {
            var text = context.Request.GetQuery("cron");
            var timeZoneId = context.Request.GetQuery("timezone");

            if (String.IsNullOrWhiteSpace(text))
            {
                return DashboardJson.WriteAsync(context, 200, new { error = "A schedule is required." });
            }

            try
            {
                var cron = Cron.FromText(text.Trim());
                var timeZone = String.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Utc
                    : RecurringJobUpdateDispatcher.GetTimeZoneResolver(context).GetTimeZoneById(timeZoneId.Trim());

                var next = Cron.GetNextOccurrences(cron, timeZone, DateTime.UtcNow, OccurrenceCount)
                    .Select(static x => JobHelper.ToTimestamp(x))
                    .ToArray();

                var hasSeconds = cron.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length == 6;

                return DashboardJson.WriteAsync(context, 200, new { cron, description = Cron.TryDescribe(cron), hasSeconds, timeZone = timeZone.Id, next });
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
            {
                return DashboardJson.WriteAsync(context, 200, new { error = RecurringJobUpdateDispatcher.GetMessage(ex) });
            }
        }
    }
}
