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

using Masterly.NightShift.Server;

namespace Masterly.NightShift.Storage;

/// <summary>
/// The base class for storage connections. It implements <see cref="IStorageConnection"/> and adds optional members
/// whose default implementations throw <see cref="NotSupportedException"/>; check <see
/// cref="JobStorage.HasFeature"/> before calling them.
/// </summary>
public abstract class JobStorageConnection : IStorageConnection
{
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    // Common
    public abstract IWriteOnlyTransaction CreateWriteTransaction();
    public abstract IDisposable AcquireDistributedLock(string resource, TimeSpan timeout);

    // Background jobs
    public abstract string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn);

    /// <summary>
    /// Creates several jobs at once, as <see cref="CreateExpiredJob"/> does for one. The default creates them one
    /// by one; a storage overrides it to create them in fewer round trips.
    /// </summary>
    /// <param name="jobs">The jobs to create.</param>
    /// <param name="parameters">The parameters of each job, in the same order.</param>
    /// <param name="createdAt">The creation time of every job.</param>
    /// <param name="expireIn">How long the jobs live while they have no state.</param>
    /// <returns>The identifier of each job, in the same order.</returns>
    public virtual IReadOnlyList<string?> CreateExpiredJobs(
        [NotNull] IReadOnlyList<Job> jobs,
        [NotNull] IReadOnlyList<IDictionary<string, string?>> parameters,
        DateTime createdAt,
        TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(parameters);

        string?[] ids = new string?[jobs.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = CreateExpiredJob(jobs[i], parameters[i], createdAt, expireIn);
        }

        return ids;
    }

    public abstract IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken);
    public abstract void SetJobParameter(string id, string name, string? value);
    public abstract string? GetJobParameter(string id, string name);
    public abstract JobData? GetJobData(string jobId);
    public abstract StateData? GetStateData(string jobId);

    // Servers
    public abstract void AnnounceServer(string serverId, ServerContext context);
    public abstract void RemoveServer(string serverId);
    public abstract void Heartbeat(string serverId);
    public abstract int RemoveTimedOutServers(TimeSpan timeOut);

    // Sets
    public abstract HashSet<string> GetAllItemsFromSet(string key);
    public abstract string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore);

    public virtual List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Connection.BatchedGetFirstByLowest);
    }

    public virtual long GetSetCount([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual long GetSetCount([NotNull] IEnumerable<string> keys, int limit)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Connection.LimitedGetSetCount);
    }

    public virtual bool GetSetContains([NotNull] string key, [NotNull] string value)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Connection.GetSetContains);
    }

    public virtual List<string> GetRangeFromSet([NotNull] string key, int startingFrom, int endingAt)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual TimeSpan GetSetTtl([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    // Hashes
    public abstract void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs);
    public abstract Dictionary<string, string>? GetAllEntriesFromHash(string key);

    public virtual string? GetValueFromHash([NotNull] string key, [NotNull] string name)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual long GetHashCount([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual TimeSpan GetHashTtl([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    // Lists
    public virtual long GetListCount([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual List<string> GetAllItemsFromList([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual List<string> GetRangeFromList([NotNull] string key, int startingFrom, int endingAt)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual TimeSpan GetListTtl([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    // Counters
    public virtual long GetCounter([NotNull] string key)
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.ExtendedApi);
    }

    public virtual DateTime GetUtcDateTime()
    {
        throw JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Connection.GetUtcDateTime);
    }
}