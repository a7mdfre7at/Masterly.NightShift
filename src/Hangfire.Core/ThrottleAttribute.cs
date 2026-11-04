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
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.Server;

namespace Hangfire
{
    /// <summary>
    /// Limits how many jobs sharing a resource run at the same time across all servers: a distributed
    /// semaphore. With <see cref="MaxConcurrency"/> of 1 it is a mutex that, unlike
    /// <see cref="DisableConcurrentExecutionAttribute"/>, doesn't hold a worker thread while waiting: by default
    /// the job goes back to the schedule and the worker takes the next one.
    /// </summary>
    /// <remarks>
    /// <para>The resource is a template: <c>{0}</c>..<c>{n}</c> are replaced with the job's arguments, so
    /// <c>[Throttle("tenant-{0}", MaxConcurrency = 2)]</c> allows two jobs per tenant. Without a template
    /// the resource is the job's type and method.</para>
    /// <para>Slots are members of the set <c>throttle:{resource}</c>. A slot whose job crashed without
    /// releasing it is reclaimed after <see cref="StaleTimeoutMinutes"/>, or as soon as the same job runs again.</para>
    /// </remarks>
    public sealed class ThrottleAttribute : JobFilterAttribute, IServerFilter
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(ThrottleAttribute));
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Initializes the attribute with the job's type and method as the resource.
        /// </summary>
        public ThrottleAttribute()
            : this(null)
        {
        }

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="resource">Resource template, see the class remarks. <c>null</c> means the job's type and method.</param>
        public ThrottleAttribute([CanBeNull] string? resource)
        {
            Resource = resource;
            MaxConcurrency = 1;
            Action = ConcurrencyAction.Reschedule;
            RetryDelaySeconds = 15;
            WaitTimeoutSeconds = 30;
            StaleTimeoutMinutes = 60;
        }

        /// <summary>Gets the resource template.</summary>
        [CanBeNull]
        public string? Resource { get; }

        /// <summary>Gets or sets how many jobs may hold the resource at once. Default is 1.</summary>
        public int MaxConcurrency { get; set; }

        /// <summary>Gets or sets what happens when every slot is taken. Default is <see cref="ConcurrencyAction.Reschedule"/>.</summary>
        public ConcurrencyAction Action { get; set; }

        /// <summary>Gets or sets how long a rescheduled job waits before it is tried again. Default is 15 seconds.</summary>
        public int RetryDelaySeconds { get; set; }

        /// <summary>Gets or sets how long <see cref="ConcurrencyAction.Wait"/> holds the worker before failing the job. Default is 30 seconds.</summary>
        public int WaitTimeoutSeconds { get; set; }

        /// <summary>Gets or sets after how long an unreleased slot is reclaimed. Default is 60 minutes. Set it above your longest job.</summary>
        public int StaleTimeoutMinutes { get; set; }

        /// <inheritdoc />
        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (MaxConcurrency <= 0) throw new InvalidOperationException("MaxConcurrency should be greater than zero.");

            var resource = ConcurrencyControl.FormatResource(Resource, context.BackgroundJob.Job!);
            var key = GetKey(resource);
            var waitUntil = ConcurrencyControl.Clock().Add(TimeSpan.FromSeconds(WaitTimeoutSeconds));
            var attempt = 0;

            while (true)
            {
                if (TryAcquire(context, key, out var inUse))
                {
                    return;
                }

                switch (Action)
                {
                    case ConcurrencyAction.Skip:
                        ConcurrencyControl.Skip(context, $"{nameof(ThrottleAttribute)}: all {MaxConcurrency} slot(s) of '{resource}' are in use");
                        return;

                    case ConcurrencyAction.Wait:
                        if (ConcurrencyControl.Clock() >= waitUntil)
                        {
                            throw new ConcurrencyLimitException(resource, $"All {MaxConcurrency} slot(s) of resource '{resource}' stayed in use for {WaitTimeoutSeconds} seconds.");
                        }

                        ConcurrencyControl.Wait(context, TimeSpan.FromMilliseconds(Math.Min(250 * (1 << Math.Min(attempt++, 4)), 4000)));
                        continue;

                    default:
                        var enqueueAt = ConcurrencyControl.Clock().AddSeconds(RetryDelaySeconds).Add(ConcurrencyControl.GetJitter());
                        ConcurrencyControl.Reschedule(context, enqueueAt, $"Throttled: {inUse} of {MaxConcurrency} slot(s) of '{resource}' in use");
                        return;
                }
            }
        }

        /// <inheritdoc />
        public void OnPerformed(PerformedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var itemKey = GetItemKey(context.BackgroundJob.Job!);

            if (!context.Items.TryGetValue(itemKey, out var value) || !(value is ValueTuple<string, string> slot))
            {
                return;
            }

            context.Items.Remove(itemKey);

            using (var transaction = context.Connection.CreateWriteTransaction())
            {
                transaction.RemoveFromSet(slot.Item1, slot.Item2);
                transaction.Commit();
            }
        }

        private bool TryAcquire(PerformingContext context, string key, out int inUse)
        {
            var now = ConcurrencyControl.Clock();
            var timestamp = ConcurrencyControl.ToUnixSeconds(now);
            var staleBefore = timestamp - (long)TimeSpan.FromMinutes(StaleTimeoutMinutes).TotalSeconds;

            using (context.Connection.AcquireDistributedLock($"{key}:lock", LockTimeout))
            {
                var members = context.Connection.GetAllItemsFromSet(key);
                ConcurrencyControl.Partition(members, staleBefore, context.BackgroundJob.Id, out var live, out var stale);

                inUse = live.Count;
                var acquired = live.Count < MaxConcurrency;

                if (!acquired && stale.Count == 0)
                {
                    return false;
                }

                using (var transaction = context.Connection.CreateWriteTransaction())
                {
                    foreach (var member in stale)
                    {
                        Logger.Debug($"Reclaiming stale or own slot '{member}' of throttled resource '{key}'.");
                        transaction.RemoveFromSet(key, member);
                    }

                    if (acquired)
                    {
                        var member = ConcurrencyControl.CreateMember(timestamp, context.BackgroundJob.Id);
                        transaction.AddToSet(key, member, timestamp);
                        context.Items[GetItemKey(context.BackgroundJob.Job!)] = (key, member);
                    }

                    transaction.Commit();
                }

                return acquired;
            }
        }

        private static string GetKey(string resource) => $"throttle:{resource}";

        private string GetItemKey(Job job) => $"Throttle:{Resource ?? job.Method.Name}";
    }
}
