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

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// The base class of every storage, such as <c>SqlServerStorage</c>. It creates the connections and the
    /// monitoring API that clients, servers and the dashboard use, says which optional features it supports
    /// (<see cref="HasFeature"/>), and can add background processes to the servers that use it.
    /// </summary>
    public abstract class JobStorage
    {
        private static readonly object LockObject = new object();
        private static JobStorage? _current;

        private TimeSpan _jobExpirationTimeout = TimeSpan.FromDays(1);

        /// <summary>
        /// Gets or sets the storage used by the static APIs (<see cref="BackgroundJob"/>, <see cref="RecurringJob"/>)
        /// and by servers and clients created without one. <c>UseStorage</c> and <c>AddNightShift</c> set it.
        /// </summary>
        /// <exception cref="InvalidOperationException">Getting it before it was set.</exception>
        public static JobStorage Current
        {
            get
            {
                lock (LockObject)
                {
                    if (_current == null)
                    {
                        throw new InvalidOperationException(
                            "Current JobStorage instance has not been initialized yet. You must set it before using Masterly.NightShift Client or Server API. " +
                            "For .NET Core applications please call the `IServiceCollection.AddNightShift` extension method from Masterly.NightShift.NetCore or Masterly.NightShift.AspNetCore package depending on your application type when configuring the services and ensure service-based APIs are used instead of static ones, like `IBackgroundJobClient` instead of `BackgroundJob` and `IRecurringJobManager` instead of `RecurringJob`."
                            );
                    }

                    return _current;
                }
            }
            set
            {
                lock (LockObject)
                {
                    _current = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets how long finished jobs (succeeded or deleted) are kept before they expire. Default is 1 day,
        /// at most <see cref="ExpireAfterAttribute.MaxDays"/> days. <c>WithJobExpirationTimeout</c> sets it.
        /// </summary>
        /// <exception cref="ArgumentException">The value is negative.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is longer than <see cref="ExpireAfterAttribute.MaxDays"/> days.</exception>
        public TimeSpan JobExpirationTimeout
        {
            get
            {
                return _jobExpirationTimeout;
            }
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentException("JobStorage.JobExpirationTimeout value should be equal or greater than zero.", nameof(value));
                }

                // A longer value would put the expiration past what storages can store (TimeSpan.MaxValue overflows
                // DateTime), and every job reaching a final state would then fail to commit and stay Processing
                if (value > TimeSpan.FromDays(ExpireAfterAttribute.MaxDays))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value,
                        $"JobStorage.JobExpirationTimeout can't be longer than {ExpireAfterAttribute.MaxDays} days.");
                }

                _jobExpirationTimeout = value;
            }
        }

        /// <summary>
        /// Gets whether a read always sees the writes committed before it. When <c>true</c>, a state change of a
        /// job that has no state yet isn't retried in the hope that a lagging read catches up. Default is <c>false</c>.
        /// </summary>
        public virtual bool LinearizableReads => false;

        /// <summary>
        /// Gets the read queries used by the dashboard and the monitoring endpoints.
        /// </summary>
        public abstract IMonitoringApi GetMonitoringApi();

        /// <summary>
        /// Opens a connection to the storage. Dispose it when done.
        /// </summary>
        public abstract IStorageConnection GetConnection();

        /// <summary>
        /// Opens a connection used only for reads, for example by the dashboard. The default implementation calls
        /// <see cref="GetConnection"/>; a storage may return a connection to a read replica. Dispose it when done.
        /// </summary>
        public virtual IStorageConnection GetReadOnlyConnection()
        {
            return GetConnection();
        }

#pragma warning disable 618
        /// <summary>
        /// Gets the storage's background processes the old way. Override <see cref="GetServerRequiredProcesses"/>
        /// and <see cref="GetStorageWideProcesses"/> instead.
        /// </summary>
        [Obsolete($"Use the `{nameof(GetStorageWideProcesses)}` and/or `{nameof(GetServerRequiredProcesses)}` methods instead, and enable `{nameof(JobStorageFeatures)}.{nameof(JobStorageFeatures.ProcessesInsteadOfComponents)}`. Kept for code ported from Hangfire.")]
        public virtual IEnumerable<IServerComponent> GetComponents()
        {
            return Enumerable.Empty<IServerComponent>();
        }
#pragma warning restore 618

        /// <summary>
        /// Gets the processes every server that uses this storage runs, lightweight servers included. Used when
        /// the storage reports <see cref="JobStorageFeatures.ProcessesInsteadOfComponents"/>. Default is none.
        /// </summary>
        public virtual IEnumerable<IBackgroundProcess> GetServerRequiredProcesses()
        {
            return Enumerable.Empty<IBackgroundProcess>();
        }

        /// <summary>
        /// Gets the maintenance processes, such as cleaning up expired data, run by the servers that aren't
        /// lightweight. Used when the storage reports <see cref="JobStorageFeatures.ProcessesInsteadOfComponents"/>.
        /// Default is none.
        /// </summary>
        public virtual IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
        {
            return Enumerable.Empty<IBackgroundProcess>();
        }

        /// <summary>
        /// Gets state handlers the storage needs, used together with <see cref="GlobalStateHandlers"/> when jobs change state.
        /// Default is none.
        /// </summary>
        public virtual IEnumerable<IStateHandler> GetStateHandlers()
        {
            return Enumerable.Empty<IStateHandler>();
        }

        /// <summary>
        /// Logs the storage's options when a server starts. The default implementation logs nothing.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        public virtual void WriteOptionsToLog(ILog logger)
        {
        }

        /// <summary>
        /// Says whether the storage supports an optional feature, one of the <see cref="JobStorageFeatures"/>
        /// identifiers. Check it before calling an optional member of <see cref="JobStorageConnection"/>,
        /// <see cref="JobStorageTransaction"/> or <see cref="JobStorageMonitor"/>. The default implementation
        /// returns <c>false</c>.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        public virtual bool HasFeature([NotNull] string featureId)
        {
            if (featureId == null) throw new ArgumentNullException(nameof(featureId));
            return false;
        }

        /// <summary>
        /// Removes all stored job data: jobs with their states, parameters and queue entries, counters, lists,
        /// sets and hashes. <paramref name="options"/> says what survives: recurring job definitions, server
        /// registrations, the audit log and the drain-mode switches. Storages that support it report the <see cref="JobStorageFeatures.Purge"/>
        /// feature; the dashboard's maintenance actions call this method.
        /// </summary>
        /// <param name="options">What to keep.</param>
        /// <exception cref="NotSupportedException">The storage doesn't implement purging.</exception>
        public virtual void Purge([NotNull] StoragePurgeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Purge);
        }
    }
}
