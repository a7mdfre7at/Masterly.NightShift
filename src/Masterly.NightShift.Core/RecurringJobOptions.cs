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

        [Obsolete("Please use non-obsolete AddOrUpdate with the explicit `queue` parameter instead. Will be removed in 2.0.0.")]
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
    }
}
