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
using System.Globalization;
using System.Text.Json.Serialization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>
    /// Keeps a job method from running on more than one worker at a time, by holding a distributed lock on the method
    /// (or on <see cref="Resource"/>) while it runs. A worker that can't get the lock within the timeout fails the job.
    /// </summary>
    /// <remarks>
    /// The lock name is the formatted <see cref="Resource"/> (or the job's type and method). A name longer than
    /// 200 characters keeps its first characters and ends with <c>~</c> and a hash of the whole name, because storages
    /// cut long lock names (SQL Server at 255 characters, its schema included), and two resources that start alike
    /// would otherwise share one lock.
    /// </remarks>
    public class DisableConcurrentExecutionAttribute : JobFilterAttribute, IServerFilter
    {
        // Leaves room for the "{schema}:" SQL Server puts in front of a lock name, which it cuts at 255 characters
        internal const int MaxResourceLength = 200;

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(DisableConcurrentExecutionAttribute));

        public DisableConcurrentExecutionAttribute(int timeoutInSeconds)
        {
            if (timeoutInSeconds < 0) throw new ArgumentException("Timeout argument value should be greater than or equal to zero.", nameof(timeoutInSeconds));

            TimeoutSec = timeoutInSeconds;
        }
        
        [JsonConstructor]
        public DisableConcurrentExecutionAttribute(string resource, int timeoutSec)
            : this(timeoutSec)
        {
            Resource = resource;
        }

        [CanBeNull]
        public string Resource { get; }
        public int TimeoutSec { get; }

        public void OnPerforming(PerformingContext context)
        {
            var resource = GetResource(context.BackgroundJob.Job);
            var timeout = TimeSpan.FromSeconds(TimeoutSec);

            var distributedLock = context.Connection.AcquireDistributedLock(resource, timeout);
            context.Items["DistributedLock"] = distributedLock;
        }

        public void OnPerformed(PerformedContext context)
        {
            if (!context.Items.TryGetValue("DistributedLock", out var value))
            {
                throw new InvalidOperationException("Can not release a distributed lock: it was not acquired.");
            }

            var distributedLock = (IDisposable)value;

            // The job has run: a failure to release doesn't change its outcome. A storage lets go of a lock whose
            // connection is closed (SQL Server does), so the lock isn't held forever.
            try
            {
                distributedLock.Dispose();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Could not release the distributed lock of background job '{context.BackgroundJob.Id}'.", ex);
            }
        }

        private string GetResource(Job job)
        {
            // A name that fits stays as it is, so existing lock names don't change
            return ConcurrencyControl.ShortenResource(FormatResource(job), MaxResourceLength);
        }

        private string FormatResource(Job job)
        {
            if (!String.IsNullOrWhiteSpace(Resource))
            {
                try
                {
                    // A [Redacted] argument is replaced with a hash of its value, so it doesn't show in the lock name
                    return String.Format(CultureInfo.InvariantCulture, Resource, RedactedAttribute.GetKeyArguments(job)).ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    throw new FormatException($"Unable to obtain resource identifier: {ex.Message}");
                }
            }

            return $"{job.Type.ToGenericTypeString()}.{job.Method.Name}";
        }
    }
}
