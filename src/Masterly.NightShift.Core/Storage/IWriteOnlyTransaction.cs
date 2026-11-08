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
using Masterly.NightShift.Annotations;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Storage
{
    /// <summary>
    /// A set of writes to a <see cref="JobStorage"/> that are applied together by <see cref="Commit"/>. Nothing
    /// is written before the commit, and disposing the transaction without committing discards the writes.
    /// Storages implement it, preferably by deriving from <see cref="JobStorageTransaction"/>, which adds the
    /// optional members advertised through <see cref="JobStorage.HasFeature"/>.
    /// </summary>
    public interface IWriteOnlyTransaction : IDisposable
    {
        // Job operations

        /// <summary>Makes a job, with its parameters and state history, expire after <paramref name="expireIn"/>.</summary>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="expireIn">How long the job is kept.</param>
        void ExpireJob([NotNull] string jobId, TimeSpan expireIn);

        /// <summary>Removes a job's expiration, so it is kept until it expires again or is removed.</summary>
        /// <param name="jobId">The job identifier.</param>
        void PersistJob([NotNull] string jobId);

        /// <summary>Adds a state to a job's history and makes it the job's current state.</summary>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="state">The new state.</param>
        void SetJobState([NotNull] string jobId, [NotNull] IState state);

        /// <summary>Adds a state to a job's history without changing its current state.</summary>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="state">The state to record.</param>
        void AddJobState([NotNull] string jobId, [NotNull] IState state);

        // Queue operations

        /// <summary>Adds a job to a queue, where a worker can fetch it.</summary>
        /// <param name="queue">The queue name.</param>
        /// <param name="jobId">The job identifier.</param>
        void AddToQueue([NotNull] string queue, [NotNull] string jobId);

        // Counter operations

        /// <summary>Adds one to a counter.</summary>
        /// <param name="key">The counter key.</param>
        void IncrementCounter([NotNull] string key);

        /// <summary>Adds one to a counter that expires after <paramref name="expireIn"/>.</summary>
        /// <param name="key">The counter key.</param>
        /// <param name="expireIn">How long the counter is kept.</param>
        void IncrementCounter([NotNull] string key, TimeSpan expireIn);

        /// <summary>Subtracts one from a counter.</summary>
        /// <param name="key">The counter key.</param>
        void DecrementCounter([NotNull] string key);

        /// <summary>Subtracts one from a counter that expires after <paramref name="expireIn"/>.</summary>
        /// <param name="key">The counter key.</param>
        /// <param name="expireIn">How long the counter is kept.</param>
        void DecrementCounter([NotNull] string key, TimeSpan expireIn);

        // Set operations

        /// <summary>Adds a value to a sorted set with a score of 0.</summary>
        /// <param name="key">The set key.</param>
        /// <param name="value">The value.</param>
        void AddToSet([NotNull] string key, [NotNull] string value);

        /// <summary>Adds a value to a sorted set, or changes its score when it is already there.</summary>
        /// <param name="key">The set key.</param>
        /// <param name="value">The value.</param>
        /// <param name="score">The score the set is sorted by.</param>
        void AddToSet([NotNull] string key, [NotNull] string value, double score);

        /// <summary>Removes a value from a sorted set.</summary>
        /// <param name="key">The set key.</param>
        /// <param name="value">The value.</param>
        void RemoveFromSet([NotNull] string key, [NotNull] string value);

        // List operations

        /// <summary>Adds a value to a list. Lists are read newest first.</summary>
        /// <param name="key">The list key.</param>
        /// <param name="value">The value.</param>
        void InsertToList([NotNull] string key, [NotNull] string value);

        /// <summary>Removes every occurrence of a value from a list.</summary>
        /// <param name="key">The list key.</param>
        /// <param name="value">The value.</param>
        void RemoveFromList([NotNull] string key, [NotNull] string value);

        /// <summary>
        /// Keeps only the elements of a list from index <paramref name="keepStartingFrom"/> to index
        /// <paramref name="keepEndingAt"/> (both included, counted from the newest element) and removes the others.
        /// </summary>
        /// <param name="key">The list key.</param>
        /// <param name="keepStartingFrom">The index of the first element kept.</param>
        /// <param name="keepEndingAt">The index of the last element kept.</param>
        void TrimList([NotNull] string key, int keepStartingFrom, int keepEndingAt);

        // Hash operations

        /// <summary>Sets fields of a hash, adding or replacing them.</summary>
        /// <param name="key">The hash key.</param>
        /// <param name="keyValuePairs">The fields and their values.</param>
        void SetRangeInHash([NotNull] string key, [NotNull] IEnumerable<KeyValuePair<string, string>> keyValuePairs);

        /// <summary>Removes a hash with all its fields.</summary>
        /// <param name="key">The hash key.</param>
        void RemoveHash([NotNull] string key);

        /// <summary>Applies every write of the transaction, all together.</summary>
        void Commit();
    }
}