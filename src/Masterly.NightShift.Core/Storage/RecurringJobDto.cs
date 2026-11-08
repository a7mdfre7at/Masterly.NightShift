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

using System;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Storage
{
    /// <summary>
    /// A recurring job as read from the storage: its schedule, job, queue, window, and its last and next executions.
    /// </summary>
    public class RecurringJobDto
    {
        public string Id { get; set; }
        public string Cron { get; set; }
        public string Queue { get; set; }
        public Job Job { get; set; }
        public JobLoadException LoadException { get; set; }
        public DateTime? NextExecution { get; set; }
        public string LastJobId { get; set; }
        public string LastJobState { get; set; }
        public DateTime? LastExecution { get; set; }
        public DateTime? CreatedAt { get; set; }
        public bool Removed { get; set; }
        public string TimeZoneId { get; set; }
        public string Error { get; set; }
        public int RetryAttempt { get; set; }

        /// <summary>Gets or sets whether the job is paused from the dashboard or by <c>IRecurringJobManagerV3.Pause</c>.</summary>
        public bool Paused { get; set; }

        /// <summary>Gets or sets how missed occurrences are handled.</summary>
        public MisfireHandlingMode MisfireHandling { get; set; }

        /// <summary>
        /// Gets or sets the UTC instant before which the job doesn't run (<see cref="RecurringJobOptions.StartAt"/>),
        /// or <c>null</c> when it has no start.
        /// </summary>
        public DateTime? StartAt { get; set; }

        /// <summary>
        /// Gets or sets the UTC instant after which the job doesn't run (<see cref="RecurringJobOptions.EndAt"/>),
        /// or <c>null</c> when it has no end. A job past its end has no <see cref="NextExecution"/>.
        /// </summary>
        public DateTime? EndAt { get; set; }
    }
}
