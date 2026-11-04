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
using System.Linq;
using Hangfire.States;
using Hangfire.Storage;

namespace Hangfire.Dashboard
{
    /// <summary>
    /// "Delete all" for the job lists: pages through a list with the monitoring API and moves every job to
    /// the Deleted state, until the list is empty or nothing more can be deleted.
    /// </summary>
    internal static class JobListCommands
    {
        private const int BatchSize = 500;
        private const int MaxBatches = 100;
        private const string Reason = "Deleted via Dashboard UI (delete all)";

        public static bool DeleteAll(
            DashboardContext context,
            Func<IMonitoringApi, int, int, IEnumerable<string>> page,
            string expectedState)
        {
            var client = context.GetBackgroundJobClient();
            var monitor = context.Storage.GetMonitoringApi();

            return DeleteAll(ids => client.ChangeState(ids, new DeletedState { Reason = Reason }, expectedState), (from, count) => page(monitor, from, count));
        }

        public static bool DeleteAllFromSet(DashboardContext context, string setKey, string expectedState)
        {
            var client = context.GetBackgroundJobClient();

            using (var connection = context.Storage.GetConnection())
            {
                if (connection is not JobStorageConnection storageConnection)
                {
                    return false;
                }

                return DeleteAll(
                    id => client.ChangeState(id, new DeletedState { Reason = Reason }, expectedState),
                    (from, count) => storageConnection.GetRangeFromSet(setKey, from, from + count - 1));
            }
        }

        public static bool RemoveAllRecurringJobs(DashboardContext context)
        {
            var manager = context.GetRecurringJobManager();
            HashSet<string> ids;

            using (var connection = context.Storage.GetConnection())
            {
                ids = connection.GetAllItemsFromSet("recurring-jobs");
            }

            foreach (var id in ids)
            {
                manager.RemoveIfExists(id);
            }

            return true;
        }

        private static bool DeleteAll(Func<string, bool> delete, Func<int, int, IEnumerable<string>> page)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var batch = 0; batch < MaxBatches; batch++)
            {
                // Always the first page: what was deleted has left the list
                var ids = page(0, BatchSize).Where(static id => id != null).ToList();
                if (ids.Count == 0) break;

                var progressed = false;

                foreach (var id in ids)
                {
                    if (!seen.Add(id)) continue;

                    progressed = true;
                    delete(id);
                }

                if (!progressed) break;
            }

            return true;
        }
    }
}
