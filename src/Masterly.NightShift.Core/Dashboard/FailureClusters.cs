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
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>Failed jobs that share an exception type and a method.</summary>
    internal sealed class FailureCluster
    {
        public FailureCluster(string exceptionType, string method)
        {
            ExceptionType = exceptionType;
            Method = method;
            JobIds = new List<string>();
        }

        /// <summary>The exception type name, as recorded in the failed state.</summary>
        public string ExceptionType { get; }

        /// <summary><c>Type.Method</c>, or the "cannot find target method" marker.</summary>
        public string Method { get; }

        /// <summary>The ids of the failed jobs in the cluster, newest first.</summary>
        public List<string> JobIds { get; }

        public int Count => JobIds.Count;

        /// <summary>The message of the newest failure.</summary>
        public string? SampleMessage { get; set; }

        public DateTime? FirstFailedAt { get; set; }

        public DateTime? LastFailedAt { get; set; }

        /// <summary>A short name for the cluster, used as a CSS/data key.</summary>
        public string Key => $"{ExceptionType}|{Method}";
    }

    /// <summary>
    /// Groups the latest failed jobs by exception type and method, so an operator sees "214 × SqlException in
    /// Billing.Charge" instead of 214 rows, and can requeue or delete the whole group at once.
    /// </summary>
    internal static class FailureClusters
    {
        /// <summary>How many of the latest failures are examined.</summary>
        public const int SampleSize = 500;

        /// <summary>How many clusters the page shows.</summary>
        public const int MaxClusters = 8;

        /// <summary>The smallest group worth showing as a cluster.</summary>
        public const int MinClusterSize = 2;

        private const int PageSize = 100;

        public static IReadOnlyList<FailureCluster> Build(IMonitoringApi monitor, int sampleSize = SampleSize, int maxClusters = MaxClusters)
        {
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));

            var clusters = new Dictionary<string, FailureCluster>(StringComparer.Ordinal);

            for (var from = 0; from < sampleSize; from += PageSize)
            {
                var page = monitor.FailedJobs(from, Math.Min(PageSize, sampleSize - from));
                if (page == null || page.Count == 0) break;

                foreach (var pair in page)
                {
                    var job = pair.Value;
                    if (job == null || !job.InFailedState) continue;

                    var exceptionType = String.IsNullOrEmpty(job.ExceptionType) ? "?" : job.ExceptionType!;
                    var method = job.Job != null ? $"{JobMethodCallRenderer.GetNameWithoutGenericArity(job.Job.Type)}.{job.Job.Method.Name}" : "?";
                    var key = exceptionType + "|" + method;

                    if (!clusters.TryGetValue(key, out var cluster))
                    {
                        clusters[key] = cluster = new FailureCluster(exceptionType, method)
                        {
                            SampleMessage = job.ExceptionMessage,
                            LastFailedAt = job.FailedAt,
                        };
                    }

                    cluster.JobIds.Add(pair.Key);

                    if (job.FailedAt.HasValue)
                    {
                        if (!cluster.FirstFailedAt.HasValue || job.FailedAt < cluster.FirstFailedAt) cluster.FirstFailedAt = job.FailedAt;
                        if (!cluster.LastFailedAt.HasValue || job.FailedAt > cluster.LastFailedAt) cluster.LastFailedAt = job.FailedAt;
                    }
                }

                if (page.Count < PageSize) break;
            }

            return clusters.Values
                .Where(static x => x.Count >= MinClusterSize)
                .OrderByDescending(static x => x.Count)
                .ThenByDescending(static x => x.LastFailedAt)
                .Take(maxClusters)
                .ToList();
        }
    }
}
