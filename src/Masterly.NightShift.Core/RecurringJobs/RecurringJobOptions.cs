// This file is part of Hangfire. Copyright © 2016 Hangfire OÜ.
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
using Masterly.NightShift.Annotations;
using Masterly.NightShift.States;

namespace Masterly.NightShift
{
    /// <summary>
    /// Options of a recurring job: its time zone, queue, misfire handling and start and end dates.
    /// </summary>
    public class RecurringJobOptions
    {
        private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
        private string _queueName = EnqueuedState.DefaultQueue;

        public RecurringJobOptions()
        {
            TimeZone = TimeZoneInfo.Utc;
#pragma warning disable 618
            QueueName = EnqueuedState.DefaultQueue;
#pragma warning restore 618
            MisfireHandling = MisfireHandlingMode.Relaxed;
        }

        [NotNull]
        public TimeZoneInfo TimeZone
        {
            get { return _timeZone; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));

                _timeZone = value;
            }
        }

        [Obsolete("Use non-obsolete AddOrUpdate with the explicit `queue` parameter instead. Kept for code ported from Hangfire.")]
        [NotNull]
        public string QueueName
        {
            get { return _queueName; }
            set
            {
                EnqueuedState.ValidateQueueName(nameof(value), value);
                _queueName = value;
            }
        }

        public MisfireHandlingMode MisfireHandling { get; set; }

        /// <summary>
        /// Gets or sets the instant before which the job doesn't run, or <c>null</c> (the default) for no start.
        /// Occurrences of the CRON expression earlier than this instant are skipped, and misfire handling never
        /// catches up on them. An occurrence exactly at this instant runs.
        /// </summary>
        /// <remarks>
        /// The value is an absolute instant, stored in UTC with a precision of one millisecond; <see cref="TimeZone"/>
        /// only decides when the CRON occurrences fall. To start at midnight of a local date, give the offset of
        /// that zone, for example <c>new DateTimeOffset(date, timeZone.GetUtcOffset(date))</c>. Only servers of
        /// this version or later respect it.
        /// </remarks>
        public DateTimeOffset? StartAt { get; set; }

        /// <summary>
        /// Gets or sets the instant after which the job doesn't run, or <c>null</c> (the default) for no end.
        /// Once the next occurrence of the CRON expression is later than this instant, the job leaves the
        /// schedule (its next execution is cleared) but stays registered, so it can still be triggered, updated
        /// or removed. An occurrence exactly at this instant runs. Must be later than <see cref="StartAt"/>.
        /// </summary>
        /// <remarks>
        /// The value is an absolute instant, stored in UTC with a precision of one millisecond, like
        /// <see cref="StartAt"/>. Only servers of this version or later respect it.
        /// </remarks>
        public DateTimeOffset? EndAt { get; set; }
    }
}
