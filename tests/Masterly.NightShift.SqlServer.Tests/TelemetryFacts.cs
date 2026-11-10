using System.Collections.Concurrent;
using System.Diagnostics;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// End-to-end check of <see cref="GlobalConfigurationExtensions.UseTelemetry"/>: a job created
/// inside a trace is performed by a real server, through real storage, within the same trace.
/// </summary>
public class TelemetryFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact, CleanDatabase]
    public void UseTelemetry_ContinuesTheTraceThatCreatedTheJob_WhenTheJobIsPerformed()
    {
        ConcurrentQueue<Activity> spans = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == NightShiftTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        SqlServerStorage storage = new(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });

        GlobalConfiguration.Configuration.UseTelemetry();
        TelemetryJob.Reset();

        try
        {
            // Arrange: the job is created while handling an incoming "request"
            ActivityTraceId requestTraceId;
            string jobId;

            using (Activity request = new Activity("GET /orders").SetIdFormat(ActivityIdFormat.W3C).Start())
            {
                requestTraceId = request.TraceId;
                jobId = new BackgroundJobClient(storage).Enqueue(() => TelemetryJob.Run());
            }

            // Act
            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
            {
                Assert.True(
                    SpinWait.SpinUntil(() => spans.Any(x => x.Kind == ActivityKind.Consumer), Timeout),
                    "The job wasn't performed in time.");
            }

            // Assert
            Activity create = Assert.Single(spans, x => x.Kind == ActivityKind.Producer);
            Activity process = Assert.Single(spans, x => x.Kind == ActivityKind.Consumer);

            Assert.Equal(requestTraceId, create.TraceId);
            Assert.Equal(requestTraceId, process.TraceId);
            Assert.Equal(create.SpanId, process.ParentSpanId);
            Assert.Equal(jobId, process.GetTagItem("messaging.message.id"));
            Assert.Equal("succeeded", process.GetTagItem("nightshift.job.outcome"));
            Assert.Equal("default", create.GetTagItem("messaging.destination.name"));
            Assert.Equal("default", process.GetTagItem("messaging.destination.name"));

            // Code inside the job runs under the job's span
            Assert.Equal(process.SpanId.ToHexString(), TelemetryJob.ObservedParentSpanId);
        }
        finally
        {
            foreach (JobFilter? filter in GlobalJobFilters.Filters.Where(IsTelemetryFilter).ToArray())
            {
                GlobalJobFilters.Filters.Remove(filter.Instance);
            }
        }
    }

    [Fact, CleanDatabase]
    public void UseTelemetry_ReportsTheQueueChosenByTheQueueAttribute_OnBothSpans()
    {
        ConcurrentQueue<Activity> spans = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == NightShiftTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        SqlServerStorage storage = new(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });

        GlobalConfiguration.Configuration.UseTelemetry();

        try
        {
            // Arrange: [Queue] moves the job to "critical" during the state election
            new BackgroundJobClient(storage).Enqueue(() => TelemetryJob.RunInCriticalQueue());

            // Act
            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = ["critical"] }, storage))
            {
                Assert.True(
                    SpinWait.SpinUntil(() => spans.Any(x => x.Kind == ActivityKind.Consumer), Timeout),
                    "The job wasn't performed in time.");
            }

            // Assert
            Activity create = Assert.Single(spans, x => x.Kind == ActivityKind.Producer);
            Activity process = Assert.Single(spans, x => x.Kind == ActivityKind.Consumer);

            Assert.Equal("critical", create.GetTagItem("messaging.destination.name"));
            Assert.Equal("critical", process.GetTagItem("messaging.destination.name"));
        }
        finally
        {
            foreach (JobFilter? filter in GlobalJobFilters.Filters.Where(IsTelemetryFilter).ToArray())
            {
                GlobalJobFilters.Filters.Remove(filter.Instance);
            }
        }
    }

    private static bool IsTelemetryFilter(JobFilter filter)
    {
        return filter.Instance.GetType().Name == "TelemetryFilter";
    }

    public static class TelemetryJob
    {
        public static string ObservedParentSpanId { get; private set; }

        public static void Reset()
        {
            ObservedParentSpanId = null;
        }

        public static void Run()
        {
            ObservedParentSpanId = Activity.Current?.SpanId.ToHexString();
        }

        [Queue("critical")]
        public static void RunInCriticalQueue()
        {
        }
    }
}
