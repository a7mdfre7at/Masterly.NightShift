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
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>One check of a <see cref="HealthReport"/>.</summary>
    internal sealed class HealthCheck
    {
        public HealthCheck(string name, HealthStatus status, string detail)
        {
            Name = name;
            Status = status;
            Detail = detail;
        }

        public string Name { get; }
        public HealthStatus Status { get; }
        public string Detail { get; }
    }

    internal enum HealthStatus
    {
        Healthy,
        Degraded,
        Unhealthy,
    }

    /// <summary>What <c>GET /health</c> answers.</summary>
    internal sealed class HealthReport
    {
        public HealthReport(HealthStatus status, DateTime checkedAt, IReadOnlyList<HealthCheck> checks, StatisticsDto? statistics, IReadOnlyList<QueueWithTopEnqueuedJobsDto> queues, int servers, bool paused)
        {
            Status = status;
            CheckedAt = checkedAt;
            Checks = checks;
            Statistics = statistics;
            Queues = queues;
            Servers = servers;
            Paused = paused;
        }

        public HealthStatus Status { get; }
        public DateTime CheckedAt { get; }
        public IReadOnlyList<HealthCheck> Checks { get; }
        public StatisticsDto? Statistics { get; }
        public IReadOnlyList<QueueWithTopEnqueuedJobsDto> Queues { get; }
        public int Servers { get; }
        public bool Paused { get; }

        public object ToJson()
        {
            return new
            {
                status = Describe(Status),
                checkedAt = JobHelper.ToTimestamp(CheckedAt),
                checks = Checks.Select(static c => new { name = c.Name, status = Describe(c.Status), detail = c.Detail }).ToArray(),
                servers = Servers,
                paused = Paused,
                queues = Queues.Select(static q => new { name = q.Name, length = q.Length, fetched = q.Fetched }).ToArray(),
                enqueued = Statistics?.Enqueued,
                scheduled = Statistics?.Scheduled,
                processing = Statistics?.Processing,
                failed = Statistics?.Failed,
                awaiting = Statistics?.Awaiting,
                recurring = Statistics?.Recurring,
                retries = Statistics?.Retries,
            };
        }

        public static string Describe(HealthStatus status)
        {
            switch (status)
            {
                case HealthStatus.Healthy: return "healthy";
                case HealthStatus.Degraded: return "degraded";
                default: return "unhealthy";
            }
        }
    }

    /// <summary>
    /// The checks behind <c>GET /health</c>: the storage answers, at least one server is alive, every queue
    /// with jobs is listened to by a live server, processing isn't paused, no recurring job is late or disabled
    /// by an error. <c>unhealthy</c> (HTTP 503) means jobs cannot be processed at all; <c>degraded</c> means
    /// they are, but something needs attention.
    /// </summary>
    internal static class DashboardHealth
    {
        /// <summary>How late a recurring job's next run may be before it counts as late.</summary>
        public static readonly TimeSpan RecurringLateThreshold = TimeSpan.FromMinutes(1);

        /// <summary>Whether a recurring job's next run was due a while ago and nobody started it.</summary>
        public static bool IsLate(RecurringJobDto job, DateTime utcNow)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return !job.Paused && !job.Removed && job.Error == null && job.NextExecution.HasValue && job.NextExecution.Value < utcNow - RecurringLateThreshold;
        }

        public static HealthReport Check(JobStorage storage, DashboardOptions options, DateTime utcNow)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var checks = new List<HealthCheck>();
            StatisticsDto? statistics = null;
            IList<ServerDto> servers = Array.Empty<ServerDto>();
            IList<QueueWithTopEnqueuedJobsDto> queues = Array.Empty<QueueWithTopEnqueuedJobsDto>();
            var paused = false;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var monitor = storage.GetMonitoringApi();
                statistics = monitor.GetStatistics();
                servers = monitor.Servers() ?? Array.Empty<ServerDto>();
                queues = monitor.Queues() ?? Array.Empty<QueueWithTopEnqueuedJobsDto>();
                checks.Add(new HealthCheck("storage", HealthStatus.Healthy, $"answered in {stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms"));
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                checks.Add(new HealthCheck("storage", HealthStatus.Unhealthy, $"{ex.GetType().Name}: {ex.Message}"));
                return new HealthReport(HealthStatus.Unhealthy, utcNow, checks, null, Array.Empty<QueueWithTopEnqueuedJobsDto>(), 0, false);
            }

            var alive = servers.Where(s => !s.Heartbeat.HasValue || s.Heartbeat.Value >= utcNow - options.ServerPossiblyAbortedThreshold).ToList();
            var stale = servers.Count - alive.Count;

            if (alive.Count == 0)
            {
                checks.Add(new HealthCheck("servers", HealthStatus.Unhealthy, servers.Count == 0 ? "no servers are registered" : $"all {servers.Count.ToString(CultureInfo.InvariantCulture)} servers missed their heartbeat"));
            }
            else if (stale > 0)
            {
                checks.Add(new HealthCheck("servers", HealthStatus.Degraded, $"{alive.Count.ToString(CultureInfo.InvariantCulture)} alive, {stale.ToString(CultureInfo.InvariantCulture)} missed their heartbeat"));
            }
            else
            {
                checks.Add(new HealthCheck("servers", HealthStatus.Healthy, $"{alive.Count.ToString(CultureInfo.InvariantCulture)} alive, {alive.Sum(static s => s.WorkersCount).ToString(CultureInfo.InvariantCulture)} workers"));
            }

            var served = new HashSet<string>(alive.SelectMany(static s => s.Queues ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
            var unserved = queues.Where(q => q.Length > 0 && !served.Contains(q.Name)).Select(static q => q.Name).ToList();

            if (unserved.Count > 0 && alive.Count > 0)
            {
                checks.Add(new HealthCheck("queues", HealthStatus.Degraded, $"no live server listens to: {String.Join(", ", unserved)}"));
            }
            else
            {
                checks.Add(new HealthCheck("queues", HealthStatus.Healthy, $"{queues.Count.ToString(CultureInfo.InvariantCulture)} queues, {queues.Sum(static q => q.Length).ToString(CultureInfo.InvariantCulture)} jobs waiting"));
            }

            try
            {
                using (var connection = storage.GetReadOnlyConnection())
                {
                    var drain = DrainMode.Read(connection);
                    paused = drain.Active;
                    if (paused)
                    {
                        checks.Add(new HealthCheck("processing", HealthStatus.Degraded, String.IsNullOrEmpty(drain.Reason) ? "paused from the dashboard" : $"paused: {drain.Reason}"));
                    }
                    else if (drain.PausedQueues.Count > 0 || drain.PausedServers.Count > 0)
                    {
                        var parts = new List<string>();
                        if (drain.PausedQueues.Count > 0) parts.Add($"queues paused: {String.Join(", ", drain.PausedQueues.Keys.OrderBy(static q => q, StringComparer.Ordinal))}");
                        if (drain.PausedServers.Count > 0) parts.Add($"servers paused: {String.Join(", ", drain.PausedServers.Keys.OrderBy(static q => q, StringComparer.Ordinal))}");
                        checks.Add(new HealthCheck("processing", HealthStatus.Degraded, String.Join("; ", parts)));
                    }
                    else
                    {
                        checks.Add(new HealthCheck("processing", HealthStatus.Healthy, "running"));
                    }

                    var recurringJobs = connection.GetRecurringJobs();
                    var late = recurringJobs.Where(j => IsLate(j, utcNow)).Select(static j => j.Id).ToList();
                    var disabled = recurringJobs.Where(static j => j.Error != null && !j.Removed).Select(static j => j.Id).ToList();

                    if (late.Count > 0)
                    {
                        checks.Add(new HealthCheck("recurring", HealthStatus.Degraded, $"{late.Count.ToString(CultureInfo.InvariantCulture)} late: {String.Join(", ", late.Take(5))}{(late.Count > 5 ? ", …" : String.Empty)}"));
                    }
                    else if (disabled.Count > 0)
                    {
                        checks.Add(new HealthCheck("recurring", HealthStatus.Degraded, $"{disabled.Count.ToString(CultureInfo.InvariantCulture)} disabled by errors: {String.Join(", ", disabled.Take(5))}{(disabled.Count > 5 ? ", …" : String.Empty)}"));
                    }
                    else
                    {
                        checks.Add(new HealthCheck("recurring", HealthStatus.Healthy, $"{recurringJobs.Count(static j => !j.Removed).ToString(CultureInfo.InvariantCulture)} on schedule"));
                    }
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                checks.Add(new HealthCheck("recurring", HealthStatus.Degraded, $"{ex.GetType().Name}: {ex.Message}"));
            }

            var status = checks.Any(static c => c.Status == HealthStatus.Unhealthy) ? HealthStatus.Unhealthy
                : checks.Any(static c => c.Status == HealthStatus.Degraded) ? HealthStatus.Degraded
                : HealthStatus.Healthy;

            return new HealthReport(status, utcNow, checks, statistics, queues.ToList(), alive.Count, paused);
        }
    }
}
