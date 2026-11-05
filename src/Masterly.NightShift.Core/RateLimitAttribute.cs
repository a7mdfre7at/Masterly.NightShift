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
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>
    /// Limits how many jobs sharing a resource may <i>start</i> within a sliding time window, across all
    /// servers. For example <c>[RateLimit("sms", Limit = 100, PerSeconds = 60)]</c> starts at most 100 SMS
    /// jobs per minute; the rest are rescheduled to the moment the oldest start leaves the window.
    /// </summary>
    /// <remarks>
    /// The resource is a template like in <see cref="ThrottleAttribute"/>. Starts are members of the set
    /// <c>ratelimit:{resource}</c> and expire with the window, so nothing has to be released when the job ends.
    /// </remarks>
    public sealed class RateLimitAttribute : JobFilterAttribute, IServerFilter
    {
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="resource">Resource template. <c>null</c> means the job's type and method.</param>
        /// <param name="limit">How many jobs may start per window.</param>
        /// <param name="perSeconds">Length of the sliding window, in seconds.</param>
        public RateLimitAttribute([CanBeNull] string? resource, int limit, int perSeconds)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit should be greater than zero.");
            if (perSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(perSeconds), "Window should be greater than zero.");

            Resource = resource;
            Limit = limit;
            PerSeconds = perSeconds;
            Action = ConcurrencyAction.Reschedule;
            WaitTimeoutSeconds = 30;
        }

        /// <summary>Gets the resource template.</summary>
        [CanBeNull]
        public string? Resource { get; }

        /// <summary>Gets how many jobs may start per window.</summary>
        public int Limit { get; }

        /// <summary>Gets the window length in seconds.</summary>
        public int PerSeconds { get; }

        /// <summary>Gets or sets what happens when the limit is reached. Default is <see cref="ConcurrencyAction.Reschedule"/>.</summary>
        public ConcurrencyAction Action { get; set; }

        /// <summary>Gets or sets how long <see cref="ConcurrencyAction.Wait"/> holds the worker before failing the job. Default is 30 seconds.</summary>
        public int WaitTimeoutSeconds { get; set; }

        /// <inheritdoc />
        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var resource = ConcurrencyControl.FormatResource(Resource, context.BackgroundJob.Job!);
            var key = $"ratelimit:{resource}";
            var waitUntil = ConcurrencyControl.Clock().AddSeconds(WaitTimeoutSeconds);

            while (true)
            {
                if (TryRecordStart(context, key, out var slotFreesAt))
                {
                    return;
                }

                switch (Action)
                {
                    case ConcurrencyAction.Skip:
                        ConcurrencyControl.Skip(context, $"{nameof(RateLimitAttribute)}: limit of {Limit} per {PerSeconds}s reached for '{resource}'");
                        return;

                    case ConcurrencyAction.Wait:
                        if (ConcurrencyControl.Clock() >= waitUntil)
                        {
                            throw new ConcurrencyLimitException(resource, $"Rate limit of {Limit} per {PerSeconds} seconds for resource '{resource}' stayed exhausted for {WaitTimeoutSeconds} seconds.");
                        }

                        var delay = slotFreesAt - ConcurrencyControl.Clock();
                        ConcurrencyControl.Wait(context, delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(250));
                        continue;

                    default:
                        ConcurrencyControl.Reschedule(
                            context,
                            slotFreesAt.Add(ConcurrencyControl.GetJitter()),
                            $"Rate limited: {Limit} job(s) per {PerSeconds}s already started for '{resource}'");
                        return;
                }
            }
        }

        /// <inheritdoc />
        public void OnPerformed(PerformedContext context)
        {
        }

        private bool TryRecordStart(PerformingContext context, string key, out DateTime slotFreesAt)
        {
            var now = ConcurrencyControl.Clock();
            var timestamp = ConcurrencyControl.ToUnixSeconds(now);
            var windowStart = timestamp - PerSeconds;

            using (context.Connection.AcquireDistributedLock($"{key}:lock", LockTimeout))
            {
                var members = context.Connection.GetAllItemsFromSet(key);
                ConcurrencyControl.Partition(members, windowStart, ownJobId: null, out var live, out var stale);

                var allowed = live.Count < Limit;

                if (allowed || stale.Count > 0)
                {
                    using (var transaction = context.Connection.CreateWriteTransaction())
                    {
                        foreach (var member in stale)
                        {
                            transaction.RemoveFromSet(key, member);
                        }

                        if (allowed)
                        {
                            transaction.AddToSet(key, ConcurrencyControl.CreateMember(timestamp, context.BackgroundJob.Id), timestamp);
                        }

                        transaction.Commit();
                    }
                }

                slotFreesAt = allowed
                    ? now
                    : ConcurrencyControl.FromUnixSeconds(live.Min(static x => x.Timestamp) + PerSeconds + 1);

                return allowed;
            }
        }
    }
}
