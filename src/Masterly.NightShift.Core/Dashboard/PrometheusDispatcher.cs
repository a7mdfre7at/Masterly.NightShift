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
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /metrics</c>: the dashboard's numbers in the Prometheus text exposition format, so Grafana and
    /// alert rules see what the home page shows. Job counts by state, queue lengths, servers, recurring jobs
    /// (late ones separately), the paused flag, the health status, and the per-method and per-queue statistics
    /// from Insights when they are recorded. Subject to the dashboard's authorization filters like every route.
    /// </summary>
    internal sealed class PrometheusDispatcher : IDashboardDispatcher
    {
        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        public Task Dispatch(DashboardContext context)
        {
            var text = Render(context.Storage, context.Options, Clock());

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            return context.Response.WriteAsync(text);
        }

        internal static string Render(JobStorage storage, DashboardOptions options, DateTime utcNow)
        {
            var report = DashboardHealth.Check(storage, options, utcNow);
            var builder = new StringBuilder();

            Gauge(builder, "nightshift_health_status", "0 healthy, 1 degraded, 2 unhealthy", (int)report.Status);
            Gauge(builder, "nightshift_servers", "Servers that reported a heartbeat recently", report.Servers);
            Gauge(builder, "nightshift_processing_paused", "1 while processing is paused from the dashboard", report.Paused ? 1 : 0);

            var statistics = report.Statistics;
            if (statistics != null)
            {
                Help(builder, "nightshift_jobs", "Jobs by state", "gauge");
                Sample(builder, "nightshift_jobs", "state=\"enqueued\"", statistics.Enqueued);
                Sample(builder, "nightshift_jobs", "state=\"scheduled\"", statistics.Scheduled);
                Sample(builder, "nightshift_jobs", "state=\"processing\"", statistics.Processing);
                Sample(builder, "nightshift_jobs", "state=\"failed\"", statistics.Failed);
                Sample(builder, "nightshift_jobs", "state=\"awaiting\"", statistics.Awaiting ?? 0);
                Sample(builder, "nightshift_jobs", "state=\"retries\"", statistics.Retries ?? 0);

                Help(builder, "nightshift_jobs_total", "Jobs that finished since the storage was created", "counter");
                Sample(builder, "nightshift_jobs_total", "state=\"succeeded\"", statistics.Succeeded);
                Sample(builder, "nightshift_jobs_total", "state=\"deleted\"", statistics.Deleted);

                Gauge(builder, "nightshift_recurring_jobs", "Recurring jobs", statistics.Recurring);
            }

            var lateRecurring = report.Checks.FirstOrDefault(static c => c.Name == "recurring" && c.Status == HealthStatus.Degraded && c.Detail.Contains("late"));
            Gauge(builder, "nightshift_recurring_jobs_late", "Recurring jobs whose next run is more than a minute overdue", lateRecurring != null ? ParseLeadingNumber(lateRecurring.Detail) : 0);

            if (report.Queues.Count > 0)
            {
                Help(builder, "nightshift_queue_length", "Jobs waiting in each queue", "gauge");
                foreach (var queue in report.Queues)
                {
                    Sample(builder, "nightshift_queue_length", $"queue=\"{Escape(queue.Name)}\"", queue.Length);
                }

                Help(builder, "nightshift_queue_fetched", "Jobs fetched from each queue and not yet acknowledged", "gauge");
                foreach (var queue in report.Queues.Where(static q => q.Fetched.HasValue))
                {
                    Sample(builder, "nightshift_queue_fetched", $"queue=\"{Escape(queue.Name)}\"", queue.Fetched!.Value);
                }
            }

            try
            {
                using (var connection = storage.GetReadOnlyConnection())
                {
                    var queues = QueueInsights.ReadAll(connection);
                    if (queues.Count > 0)
                    {
                        Help(builder, "nightshift_queue_latency_seconds", "Time jobs waited in the queue before a worker took them, over the last 200 starts", "gauge");
                        Help(builder, "nightshift_queue_started_per_minute", "Jobs taken from the queue per minute over the last five minutes", "gauge");
                        foreach (var health in queues.Values.OrderBy(static q => q.Queue, StringComparer.Ordinal))
                        {
                            Sample(builder, "nightshift_queue_latency_seconds", $"queue=\"{Escape(health.Queue)}\",quantile=\"0.5\"", health.MedianLatencyMs / 1000.0);
                            Sample(builder, "nightshift_queue_latency_seconds", $"queue=\"{Escape(health.Queue)}\",quantile=\"0.95\"", health.P95LatencyMs / 1000.0);
                            Sample(builder, "nightshift_queue_started_per_minute", $"queue=\"{Escape(health.Queue)}\"", health.PerMinute);
                        }
                    }

                    var insights = JobInsightsAttribute.ReadAll(connection);
                    if (insights.Count > 0)
                    {
                        Help(builder, "nightshift_method_runs_total", "Runs per job method and outcome", "counter");
                        Help(builder, "nightshift_method_duration_seconds", "Run duration per job method, over the last 200 runs", "gauge");
                        foreach (var insight in insights.OrderBy(static i => i.Key, StringComparer.Ordinal))
                        {
                            var method = Escape(insight.Key);
                            Sample(builder, "nightshift_method_runs_total", $"method=\"{method}\",result=\"succeeded\"", insight.Succeeded);
                            Sample(builder, "nightshift_method_runs_total", $"method=\"{method}\",result=\"failed\"", insight.Failed);
                            if (insight.Durations.Length > 0)
                            {
                                Sample(builder, "nightshift_method_duration_seconds", $"method=\"{method}\",quantile=\"0.5\"", insight.MedianMs / 1000.0);
                                Sample(builder, "nightshift_method_duration_seconds", $"method=\"{method}\",quantile=\"0.95\"", insight.P95Ms / 1000.0);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                builder.Append("# insights unavailable: ").Append(ex.GetType().Name).Append('\n');
            }

            return builder.ToString();
        }

        private static void Gauge(StringBuilder builder, string name, string help, double value)
        {
            Help(builder, name, help, "gauge");
            Sample(builder, name, null, value);
        }

        private static void Help(StringBuilder builder, string name, string help, string type)
        {
            builder.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            builder.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }

        private static void Sample(StringBuilder builder, string name, string? labels, double value)
        {
            builder.Append(name);
            if (!String.IsNullOrEmpty(labels)) builder.Append('{').Append(labels).Append('}');
            builder.Append(' ').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }

        private static int ParseLeadingNumber(string text)
        {
            var digits = new string(text.TakeWhile(Char.IsDigit).ToArray());
            return Int32.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
        }
    }
}
