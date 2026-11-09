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

using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// The base class of every storage, such as <c>SqlServerStorage</c>. It creates the connections and the
/// monitoring API that clients, servers and the dashboard use, says which optional features it supports
/// (<see cref="HasFeature"/>), and can add background processes to the servers that use it.
/// </summary>
public abstract class JobStorage
{
    private static readonly object LockObject = new();

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
                if (field == null)
                {
                    throw new InvalidOperationException(
                        "Current JobStorage instance has not been initialized yet. You must set it before using Masterly.NightShift Client or Server API. " +
                        "For .NET Core applications please call the `IServiceCollection.AddNightShift` extension method from Masterly.NightShift.NetCore or Masterly.NightShift.AspNetCore package depending on your application type when configuring the services and ensure service-based APIs are used instead of static ones, like `IBackgroundJobClient` instead of `BackgroundJob` and `IRecurringJobManager` instead of `RecurringJob`."
                        );
                }

                return field;
            }
        }
        set
        {
            lock (LockObject)
            {
                field = value;
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
        get;
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

            field = value;
        }
    } = TimeSpan.FromDays(1);

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

    /// <summary>
    /// Gets the processes every server that uses this storage runs, lightweight servers included. Default is none.
    /// </summary>
    public virtual IEnumerable<IBackgroundProcess> GetServerRequiredProcesses()
    {
        return [];
    }

    /// <summary>
    /// Gets the maintenance processes, such as cleaning up expired data, run by the servers that aren't
    /// lightweight. Default is none.
    /// </summary>
    public virtual IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
    {
        return [];
    }

    /// <summary>
    /// Gets state handlers the storage needs, used together with <see cref="GlobalStateHandlers"/> when jobs change state.
    /// Default is none.
    /// </summary>
    public virtual IEnumerable<IStateHandler> GetStateHandlers()
    {
        return [];
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
        ArgumentNullException.ThrowIfNull(featureId);
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
        ArgumentNullException.ThrowIfNull(options);
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Purge);
    }
}
