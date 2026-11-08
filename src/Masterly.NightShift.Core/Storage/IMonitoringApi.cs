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
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Storage
{
    /// <summary>
    /// The read queries behind the dashboard and the monitoring endpoints: queues, servers, job lists and
    /// counts, and statistics. Get it from <see cref="JobStorage.GetMonitoringApi"/>. Storages implement it,
    /// preferably by deriving from <see cref="JobStorageMonitor"/>, which adds optional queries. The list methods
    /// return one page; a <c>null</c> entry stands for a job that disappeared while the page was read.
    /// </summary>
    public interface IMonitoringApi
    {
        /// <summary>Gets every queue with its length, fetched count and first enqueued jobs.</summary>
        IList<QueueWithTopEnqueuedJobsDto> Queues();

        /// <summary>Gets every registered server.</summary>
        IList<ServerDto> Servers();

        /// <summary>Gets a job with its parameters and state history.</summary>
        /// <param name="jobId">The job identifier.</param>
        /// <returns>The job, or <c>null</c> when it doesn't exist.</returns>
        JobDetailsDto? JobDetails(string jobId);

        /// <summary>Gets the numbers of jobs in each state, of servers, of queues and of recurring jobs.</summary>
        StatisticsDto GetStatistics();

        /// <summary>Gets a page of the jobs waiting in a queue.</summary>
        /// <param name="queue">The queue name.</param>
        /// <param name="from">The index of the first job.</param>
        /// <param name="perPage">The page size.</param>
        JobList<EnqueuedJobDto?> EnqueuedJobs(string queue, int from, int perPage);

        /// <summary>Gets a page of the jobs of a queue that workers have fetched.</summary>
        /// <param name="queue">The queue name.</param>
        /// <param name="from">The index of the first job.</param>
        /// <param name="perPage">The page size.</param>
        JobList<FetchedJobDto?> FetchedJobs(string queue, int from, int perPage);

        /// <summary>Gets a page of the jobs in the Processing state.</summary>
        /// <param name="from">The index of the first job.</param>
        /// <param name="count">The page size.</param>
        JobList<ProcessingJobDto?> ProcessingJobs(int from, int count);

        /// <summary>Gets a page of the jobs in the Scheduled state.</summary>
        /// <param name="from">The index of the first job.</param>
        /// <param name="count">The page size.</param>
        JobList<ScheduledJobDto?> ScheduledJobs(int from, int count);

        /// <summary>Gets a page of the jobs in the Succeeded state.</summary>
        /// <param name="from">The index of the first job.</param>
        /// <param name="count">The page size.</param>
        JobList<SucceededJobDto?> SucceededJobs(int from, int count);

        /// <summary>Gets a page of the jobs in the Failed state.</summary>
        /// <param name="from">The index of the first job.</param>
        /// <param name="count">The page size.</param>
        JobList<FailedJobDto?> FailedJobs(int from, int count);

        /// <summary>Gets a page of the jobs in the Deleted state.</summary>
        /// <param name="from">The index of the first job.</param>
        /// <param name="count">The page size.</param>
        JobList<DeletedJobDto?> DeletedJobs(int from, int count);

        /// <summary>Counts the jobs in the Scheduled state.</summary>
        long ScheduledCount();

        /// <summary>Counts the jobs waiting in a queue.</summary>
        /// <param name="queue">The queue name.</param>
        long EnqueuedCount(string queue);

        /// <summary>Counts the jobs of a queue that workers have fetched.</summary>
        /// <param name="queue">The queue name.</param>
        long FetchedCount(string queue);

        /// <summary>Counts the jobs in the Failed state.</summary>
        long FailedCount();

        /// <summary>Counts the jobs in the Processing state.</summary>
        long ProcessingCount();

        /// <summary>Counts the jobs in the Succeeded state that are still stored.</summary>
        long SucceededListCount();

        /// <summary>Counts the jobs in the Deleted state that are still stored.</summary>
        long DeletedListCount();

        /// <summary>Gets the number of jobs that succeeded on each of the last 7 days, keyed by date (UTC).</summary>
        IDictionary<DateTime, long> SucceededByDatesCount();

        /// <summary>Gets the number of jobs that failed on each of the last 7 days, keyed by date (UTC).</summary>
        IDictionary<DateTime, long> FailedByDatesCount();

        /// <summary>Gets the number of jobs that succeeded in each of the last 24 hours, keyed by time (UTC).</summary>
        IDictionary<DateTime, long> HourlySucceededJobs();

        /// <summary>Gets the number of jobs that failed in each of the last 24 hours, keyed by time (UTC).</summary>
        IDictionary<DateTime, long> HourlyFailedJobs();
    }
}