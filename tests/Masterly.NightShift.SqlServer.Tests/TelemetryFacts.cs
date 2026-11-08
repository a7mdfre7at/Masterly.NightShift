// UseTelemetry() exists on net8.0 and later only (ActivitySource and Meter are not on .NET Framework)
#if NET8_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Common;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
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
            var spans = new ConcurrentQueue<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == NightShiftTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = spans.Enqueue
            };
            ActivitySource.AddActivityListener(listener);

            var storage = new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });

            GlobalConfiguration.Configuration.UseTelemetry();
            TelemetryJob.Reset();

            try
            {
                // Arrange: the job is created while handling an incoming "request"
                ActivityTraceId requestTraceId;
                string jobId;

                using (var request = new Activity("GET /orders").SetIdFormat(ActivityIdFormat.W3C).Start())
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
                var create = Assert.Single(spans, x => x.Kind == ActivityKind.Producer);
                var process = Assert.Single(spans, x => x.Kind == ActivityKind.Consumer);

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
                foreach (var filter in GlobalJobFilters.Filters.Where(IsTelemetryFilter).ToArray())
                {
                    GlobalJobFilters.Filters.Remove(filter.Instance);
                }
            }
        }

        [Fact, CleanDatabase]
        public void UseTelemetry_ReportsTheQueueChosenByTheQueueAttribute_OnBothSpans()
        {
            var spans = new ConcurrentQueue<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == NightShiftTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = spans.Enqueue
            };
            ActivitySource.AddActivityListener(listener);

            var storage = new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });

            GlobalConfiguration.Configuration.UseTelemetry();

            try
            {
                // Arrange: [Queue] moves the job to "critical" during the state election
                new BackgroundJobClient(storage).Enqueue(() => TelemetryJob.RunInCriticalQueue());

                // Act
                using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = new[] { "critical" } }, storage))
                {
                    Assert.True(
                        SpinWait.SpinUntil(() => spans.Any(x => x.Kind == ActivityKind.Consumer), Timeout),
                        "The job wasn't performed in time.");
                }

                // Assert
                var create = Assert.Single(spans, x => x.Kind == ActivityKind.Producer);
                var process = Assert.Single(spans, x => x.Kind == ActivityKind.Consumer);

                Assert.Equal("critical", create.GetTagItem("messaging.destination.name"));
                Assert.Equal("critical", process.GetTagItem("messaging.destination.name"));
            }
            finally
            {
                foreach (var filter in GlobalJobFilters.Filters.Where(IsTelemetryFilter).ToArray())
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
}
#endif
