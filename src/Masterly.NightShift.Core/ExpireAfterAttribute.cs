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
using System.Globalization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Sets how long a job's data is kept after it finishes, per method: <c>[ExpireAfter(days: 30)]</c> keeps an
    /// audit-relevant job for a month, <c>[ExpireAfter("00:10:00")]</c> drops a chatty one after ten minutes.
    /// Without it the storage default applies (one day). Applies to succeeded and deleted jobs; failed jobs
    /// never expire, as before.
    /// </summary>
    public sealed class ExpireAfterAttribute : JobFilterAttribute, IApplyStateFilter
    {
        /// <summary>Keeps finished jobs for the given <see cref="TimeSpan"/>, written as <c>d.hh:mm:ss</c> or <c>hh:mm:ss</c>.</summary>
        public ExpireAfterAttribute([NotNull] string timeSpan)
        {
            if (timeSpan == null) throw new ArgumentNullException(nameof(timeSpan));
            if (!TimeSpan.TryParse(timeSpan, CultureInfo.InvariantCulture, out var parsed) || parsed <= TimeSpan.Zero)
            {
                throw new ArgumentException($"'{timeSpan}' is not a positive time span.", nameof(timeSpan));
            }

            Duration = parsed;
            Order = 50;
        }

        /// <summary>Keeps finished jobs for the given number of days.</summary>
        public ExpireAfterAttribute(int days)
        {
            if (days <= 0) throw new ArgumentOutOfRangeException(nameof(days), days, "The number of days must be positive.");

            Duration = TimeSpan.FromDays(days);
            Order = 50;
        }

        /// <summary>How long finished jobs are kept.</summary>
        public TimeSpan Duration { get; }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.NewState.IsFinal)
            {
                context.JobExpirationTimeout = Duration;
            }
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
