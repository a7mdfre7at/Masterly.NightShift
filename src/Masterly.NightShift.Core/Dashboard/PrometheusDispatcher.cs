using System.Globalization;
using System.Text;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// <c>GET /metrics</c>: the dashboard's numbers in the Prometheus text exposition format, so Grafana and
/// alert rules see what the home page shows. Job counts by state, queue lengths, servers, recurring jobs
/// (late ones separately), the paused flag, the health status, and the per-method and per-queue statistics
/// from Insights when they are recorded. The text is reused for <see cref="DashboardHealth.CacheDuration"/>
/// per storage, so several scrapers cost no more storage queries than one. Subject to the dashboard's
/// authorization filters like every route.
/// </summary>
internal sealed class PrometheusDispatcher : IDashboardDispatcher
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(PrometheusDispatcher));
    private static readonly StorageCache<string> Texts = new(DashboardHealth.CacheDuration);

    /// <summary>Replaceable for tests.</summary>
    internal static Func<DateTime> Clock { get; set; } = static () => JobClock.UtcNow;

    public Task Dispatch(DashboardContext context)
    {
        string text = RenderCached(context.Storage, context.Options, Clock());

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        return context.Response.WriteAsync(text);
    }

    /// <summary>The text of <see cref="Render(JobStorage, DashboardOptions, DateTime)"/>, computed at most once every <see cref="DashboardHealth.CacheDuration"/>.</summary>
    internal static string RenderCached(JobStorage storage, DashboardOptions options, DateTime utcNow)
    {
        return Texts.Get(storage, options, utcNow, () => Render(storage, DashboardHealth.GetReport(storage, options, utcNow)));
    }

    internal static string Render(JobStorage storage, DashboardOptions options, DateTime utcNow)
    {
        return Render(storage, DashboardHealth.Check(storage, options, utcNow));
    }

    private static string Render(JobStorage storage, HealthReport report)
    {
        StringBuilder builder = new();

        Gauge(builder, "nightshift_health_status", "0 healthy, 1 degraded, 2 unhealthy", (int)report.Status);
        Gauge(builder, "nightshift_servers", "Servers that reported a heartbeat recently", report.Servers);
        Gauge(builder, "nightshift_processing_paused", "1 while processing is paused from the dashboard", report.Paused ? 1 : 0);

        StatisticsDto? statistics = report.Statistics;
        if (statistics != null)
        {
            Help(builder, "nightshift_jobs", "Jobs by state", "gauge");
            Sample(builder, "nightshift_jobs", "state=\"enqueued\"", statistics.Enqueued);
            Sample(builder, "nightshift_jobs", "state=\"scheduled\"", statistics.Scheduled);
            Sample(builder, "nightshift_jobs", "state=\"processing\"", statistics.Processing);
            Sample(builder, "nightshift_jobs", "state=\"failed\"", statistics.Failed);
            Sample(builder, "nightshift_jobs", "state=\"awaiting\"", statistics.Awaiting ?? 0);
            Sample(builder, "nightshift_jobs", "state=\"retries\"", statistics.Retries ?? 0);

            Help(builder, "nightshift_jobs_finished_total", "Jobs that finished since the storage was created", "counter");
            Sample(builder, "nightshift_jobs_finished_total", "state=\"succeeded\"", statistics.Succeeded);
            Sample(builder, "nightshift_jobs_finished_total", "state=\"deleted\"", statistics.Deleted);

            Gauge(builder, "nightshift_recurring_jobs", "Recurring jobs", statistics.Recurring);
        }

        Gauge(builder, "nightshift_recurring_jobs_late", "Recurring jobs whose next run is more than a minute overdue", report.LateRecurringJobs);

        if (report.Queues.Count > 0)
        {
            Help(builder, "nightshift_queue_length", "Jobs waiting in each queue", "gauge");
            foreach (QueueWithTopEnqueuedJobsDto queue in report.Queues)
            {
                Sample(builder, "nightshift_queue_length", $"queue=\"{Escape(queue.Name)}\"", queue.Length);
            }

            Help(builder, "nightshift_queue_fetched", "Jobs fetched from each queue and not yet acknowledged", "gauge");
            foreach (QueueWithTopEnqueuedJobsDto? queue in report.Queues.Where(static q => q.Fetched.HasValue))
            {
                Sample(builder, "nightshift_queue_fetched", $"queue=\"{Escape(queue.Name)}\"", queue.Fetched!.Value);
            }
        }

        // Insights are kept in lists and counters, which only storages with the extended API can read
        if (!storage.HasFeature(JobStorageFeatures.ExtendedApi))
        {
            return builder.ToString();
        }

        try
        {
            using IStorageConnection connection = storage.GetReadOnlyConnection();
            // Every family is written as one group, HELP and TYPE first: strict parsers reject interleaved families
            List<QueueHealth> queues = [.. QueueInsights.ReadAll(connection).Values.OrderBy(static q => q.Queue, StringComparer.Ordinal)];
            if (queues.Count > 0)
            {
                Help(builder, "nightshift_queue_latency_seconds", "Time jobs waited in the queue before a worker took them, over the last 200 starts", "gauge");
                foreach (QueueHealth health in queues)
                {
                    Sample(builder, "nightshift_queue_latency_seconds", $"queue=\"{Escape(health.Queue)}\",quantile=\"0.5\"", health.MedianLatencyMs / 1000.0);
                    Sample(builder, "nightshift_queue_latency_seconds", $"queue=\"{Escape(health.Queue)}\",quantile=\"0.95\"", health.P95LatencyMs / 1000.0);
                }

                Help(builder, "nightshift_queue_started_per_minute", "Jobs taken from the queue per minute over the last five minutes", "gauge");
                foreach (QueueHealth health in queues)
                {
                    Sample(builder, "nightshift_queue_started_per_minute", $"queue=\"{Escape(health.Queue)}\"", health.PerMinute);
                }
            }

            List<JobInsight> insights = [.. JobInsightsAttribute.ReadAll(connection).OrderBy(static i => i.Key, StringComparer.Ordinal)];
            if (insights.Count > 0)
            {
                Help(builder, "nightshift_method_runs_total", "Runs per job method and outcome", "counter");
                foreach (JobInsight insight in insights)
                {
                    string method = Escape(insight.Key);
                    Sample(builder, "nightshift_method_runs_total", $"method=\"{method}\",result=\"succeeded\"", insight.Succeeded);
                    Sample(builder, "nightshift_method_runs_total", $"method=\"{method}\",result=\"failed\"", insight.Failed);
                }

                Help(builder, "nightshift_method_duration_seconds", "Run duration per job method, over the last 200 runs", "gauge");
                foreach (JobInsight? insight in insights.Where(static i => i.Durations.Length > 0))
                {
                    string method = Escape(insight.Key);
                    Sample(builder, "nightshift_method_duration_seconds", $"method=\"{method}\",quantile=\"0.5\"", insight.MedianMs / 1000.0);
                    Sample(builder, "nightshift_method_duration_seconds", $"method=\"{method}\",quantile=\"0.95\"", insight.P95Ms / 1000.0);
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException("Metrics: could not read the insights.", ex);
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
        if (!string.IsNullOrEmpty(labels)) builder.Append('{').Append(labels).Append('}');
        builder.Append(' ').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }
}
