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

namespace Masterly.NightShift
{
    /// <summary>
    /// What a concurrency filter (<see cref="ThrottleAttribute"/>, <see cref="RateLimitAttribute"/>,
    /// <see cref="CircuitBreakerAttribute"/>) does with a job that may not run right now.
    /// </summary>
    public enum ConcurrencyAction
    {
        /// <summary>
        /// Moves the job back to the Scheduled state until a slot is expected to be free, so the worker is
        /// released at once. The job keeps its id, parameters and history, and runs later on any server.
        /// </summary>
        Reschedule = 0,

        /// <summary>
        /// Holds the worker thread, polling with back-off, until a slot frees up or the wait timeout
        /// expires. The timeout fails the job with <see cref="ConcurrencyLimitException"/>.
        /// </summary>
        Wait = 1,

        /// <summary>
        /// Cancels the job: it is moved to the Deleted state and never runs. For work that is pointless
        /// once another instance is already running, such as a cache refresh.
        /// </summary>
        Skip = 2,
    }
}
