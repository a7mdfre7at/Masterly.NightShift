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
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Storage
{
    /// <summary>
    /// A connection to a <see cref="JobStorage"/>: reads, job creation and fetching, server registration and
    /// distributed locks. Writes that must be atomic go through <see cref="CreateWriteTransaction"/>. Storages
    /// implement it, preferably by deriving from <see cref="JobStorageConnection"/>, which adds the optional
    /// members advertised through <see cref="JobStorage.HasFeature"/>. Dispose it when done.
    /// </summary>
    public interface IStorageConnection : IDisposable
    {
        /// <summary>
        /// Creates a transaction whose writes are applied together when it is committed.
        /// </summary>
        IWriteOnlyTransaction CreateWriteTransaction();

        /// <summary>
        /// Acquires a lock on <paramref name="resource"/> shared by every process that uses the storage.
        /// Dispose the result to release it.
        /// </summary>
        /// <param name="resource">The name of the locked resource.</param>
        /// <param name="timeout">How long to wait for the lock.</param>
        /// <returns>The lock.</returns>
        /// <exception cref="DistributedLockTimeoutException">The lock wasn't acquired within <paramref name="timeout"/>.</exception>
        IDisposable AcquireDistributedLock(string resource, TimeSpan timeout);

        /// <summary>
        /// Creates a job with its parameters, without a state. The job expires after <paramref name="expireIn"/>
        /// unless a state change makes it persistent, so a job whose first state change never happens is removed.
        /// </summary>
        /// <param name="job">The method call to store.</param>
        /// <param name="parameters">The job parameters.</param>
        /// <param name="createdAt">The creation time, UTC.</param>
        /// <param name="expireIn">How long the job is kept without a state change.</param>
        /// <returns>The new job's identifier.</returns>
        string? CreateExpiredJob(
            Job job, 
            IDictionary<string, string?> parameters, 
            DateTime createdAt,
            TimeSpan expireIn);

        /// <summary>
        /// Waits for a job in one of <paramref name="queues"/> and takes it, so other workers don't get it.
        /// </summary>
        /// <param name="queues">The queues to fetch from.</param>
        /// <param name="cancellationToken">Stops the wait.</param>
        /// <returns>The fetched job; remove it from the queue or requeue it when done.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken);

        /// <summary>
        /// Sets a job parameter, adding or replacing it.
        /// </summary>
        /// <param name="id">The job identifier.</param>
        /// <param name="name">The parameter name.</param>
        /// <param name="value">The serialized value, or <c>null</c>.</param>
        void SetJobParameter(string id, string name, string? value);

        /// <summary>
        /// Gets a job parameter.
        /// </summary>
        /// <param name="id">The job identifier.</param>
        /// <param name="name">The parameter name.</param>
        /// <returns>The serialized value, or <c>null</c> when the parameter or the job doesn't exist.</returns>
        string? GetJobParameter(string id, string name);

        /// <summary>
        /// Gets a job's method call, current state name, parameters and creation time.
        /// </summary>
        /// <param name="jobId">The job identifier.</param>
        /// <returns>The job, or <c>null</c> when it doesn't exist.</returns>
        [CanBeNull]
        JobData? GetJobData([NotNull] string jobId);

        /// <summary>
        /// Gets a job's current state with its data.
        /// </summary>
        /// <param name="jobId">The job identifier.</param>
        /// <returns>The state, or <c>null</c> when the job doesn't exist or has no state.</returns>
        [CanBeNull]
        StateData? GetStateData([NotNull] string jobId);

        /// <summary>
        /// Registers a server, or updates its registration, with its worker count and queues.
        /// </summary>
        /// <param name="serverId">The server identifier.</param>
        /// <param name="context">The server's worker count and queues.</param>
        void AnnounceServer(string serverId, ServerContext context);

        /// <summary>
        /// Removes a server's registration.
        /// </summary>
        /// <param name="serverId">The server identifier.</param>
        void RemoveServer(string serverId);

        /// <summary>
        /// Records that the server is alive.
        /// </summary>
        /// <param name="serverId">The server identifier.</param>
        /// <exception cref="BackgroundServerGoneException">The server isn't registered any more (a storage may
        /// throw it, so the server registers itself again).</exception>
        void Heartbeat(string serverId);

        /// <summary>
        /// Removes the servers whose last heartbeat is older than <paramref name="timeOut"/>.
        /// </summary>
        /// <param name="timeOut">How long a server may go without a heartbeat.</param>
        /// <returns>How many servers were removed.</returns>
        int RemoveTimedOutServers(TimeSpan timeOut);

        // Set operations

        /// <summary>
        /// Gets every value of a sorted set.
        /// </summary>
        /// <param name="key">The set key.</param>
        /// <returns>The values; empty when the set doesn't exist.</returns>
        [NotNull]
        HashSet<string> GetAllItemsFromSet([NotNull] string key);

        /// <summary>
        /// Gets the value with the lowest score between <paramref name="fromScore"/> and <paramref name="toScore"/>
        /// (both included) in a sorted set.
        /// </summary>
        /// <param name="key">The set key.</param>
        /// <param name="fromScore">The lowest score to consider.</param>
        /// <param name="toScore">The highest score to consider.</param>
        /// <returns>The value, or <c>null</c> when no value has a score in the range.</returns>
        string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore);

        // Hash operations

        /// <summary>
        /// Sets fields of a hash, adding or replacing them, outside of a transaction.
        /// </summary>
        /// <param name="key">The hash key.</param>
        /// <param name="keyValuePairs">The fields and their values.</param>
        void SetRangeInHash([NotNull] string key, [NotNull] IEnumerable<KeyValuePair<string, string>> keyValuePairs);

        /// <summary>
        /// Gets every field of a hash.
        /// </summary>
        /// <param name="key">The hash key.</param>
        /// <returns>The fields and their values, or <c>null</c> when the hash doesn't exist.</returns>
        [CanBeNull]
        Dictionary<string, string>? GetAllEntriesFromHash([NotNull] string key);
    }
}