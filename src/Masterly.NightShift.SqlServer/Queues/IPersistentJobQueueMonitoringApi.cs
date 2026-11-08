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

using System.Collections.Generic;

namespace Masterly.NightShift.SqlServer
{
    /// <summary>
    /// The monitoring queries of a queue implementation, used by the dashboard.
    /// </summary>
    public interface IPersistentJobQueueMonitoringApi
    {
        /// <summary>Gets the names of the queues that hold jobs.</summary>
        IEnumerable<string> GetQueues();

        /// <summary>Gets a page of the identifiers of the jobs waiting in a queue.</summary>
        /// <param name="queue">The queue name.</param>
        /// <param name="from">The index of the first job.</param>
        /// <param name="perPage">The page size.</param>
        IEnumerable<long> GetEnqueuedJobIds(string queue, int from, int perPage);

        // TODO: Extend return type by including DateTime to allow getting the FetchedAt value in 2.0
        /// <summary>Gets a page of the identifiers of the jobs of a queue that workers have fetched.</summary>
        /// <param name="queue">The queue name.</param>
        /// <param name="from">The index of the first job.</param>
        /// <param name="perPage">The page size.</param>
        IEnumerable<long> GetFetchedJobIds(string queue, int from, int perPage);

        /// <summary>Counts the waiting and the fetched jobs of a queue.</summary>
        /// <param name="queue">The queue name.</param>
        EnqueuedAndFetchedCountDto GetEnqueuedAndFetchedCount(string queue);
    }
}
