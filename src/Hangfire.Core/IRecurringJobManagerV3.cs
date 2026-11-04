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
using Hangfire.Annotations;

namespace Hangfire
{
    /// <summary>
    /// Operations on recurring jobs that don't need the job's method: pausing, resuming and changing the
    /// schedule. The dashboard uses them. A separate interface keeps <see cref="IRecurringJobManager"/>
    /// implementations in other packages compiling; the extension methods in
    /// <see cref="RecurringJobManagerExtensions"/> forward to this interface when the manager implements it.
    /// </summary>
    public interface IRecurringJobManagerV3 : IRecurringJobManagerV2
    {
        /// <summary>
        /// Takes the job out of the schedule until <see cref="Resume"/>. Its definition is kept, and it can
        /// still be triggered by hand. Re-registering the job with <c>AddOrUpdate</c> doesn't resume it.
        /// </summary>
        /// <param name="recurringJobId">The recurring job identifier.</param>
        /// <returns><c>false</c> when the job doesn't exist.</returns>
        bool Pause([NotNull] string recurringJobId);

        /// <summary>
        /// Puts a paused job back on the schedule from now. Occurrences missed while paused aren't run.
        /// </summary>
        /// <param name="recurringJobId">The recurring job identifier.</param>
        /// <returns><c>false</c> when the job doesn't exist.</returns>
        bool Resume([NotNull] string recurringJobId);

        /// <summary>
        /// Changes when and where a job runs without touching what it runs. Arguments left <c>null</c> keep
        /// their current value.
        /// </summary>
        /// <param name="recurringJobId">The recurring job identifier.</param>
        /// <param name="cronExpression">New CRON expression, or <c>null</c> to keep the current one.</param>
        /// <param name="timeZone">New time zone, or <c>null</c> to keep the current one.</param>
        /// <param name="queue">New queue, or <c>null</c> to keep the current one.</param>
        /// <param name="misfireHandling">New misfire handling, or <c>null</c> to keep the current one.</param>
        /// <returns><c>false</c> when the job doesn't exist.</returns>
        /// <exception cref="ArgumentException">The CRON expression is invalid.</exception>
        bool UpdateSchedule(
            [NotNull] string recurringJobId,
            [CanBeNull] string? cronExpression,
            [CanBeNull] TimeZoneInfo? timeZone = null,
            [CanBeNull] string? queue = null,
            MisfireHandlingMode? misfireHandling = null);
    }
}
