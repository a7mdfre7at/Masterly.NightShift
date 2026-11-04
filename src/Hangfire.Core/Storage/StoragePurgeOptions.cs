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

namespace Hangfire.Storage
{
    /// <summary>
    /// What <see cref="JobStorage.Purge"/> keeps. Everything else, background jobs with their states,
    /// parameters, queues, counters, lists, sets and hashes, is removed.
    /// </summary>
    public sealed class StoragePurgeOptions
    {
        /// <summary>
        /// Gets or sets whether recurring job definitions are kept, so they don't have to be registered again
        /// by the application. Default is <c>true</c>.
        /// </summary>
        public bool KeepRecurringJobs { get; set; } = true;

        /// <summary>
        /// Gets or sets whether server registrations and heartbeats are kept. Default is <c>true</c>; running
        /// servers re-announce themselves anyway, but keeping them avoids a gap on the Servers page.
        /// </summary>
        public bool KeepServers { get; set; } = true;
    }
}
