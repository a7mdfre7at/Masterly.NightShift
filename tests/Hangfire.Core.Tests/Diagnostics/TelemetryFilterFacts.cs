using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using Hangfire.Diagnostics;
using Hangfire.Server;
using Moq;
using Xunit;

namespace Hangfire.Core.Tests.Diagnostics
{
    public sealed class TelemetryFilterFacts : IDisposable
    {
        private const string JobId = "JobId";
        private const string ParentTraceId = "0af7651916cd43dd8448eb211c80319c";
        private const string ParentSpanId = "b7ad6b7169203331";
        private const string TraceParent = "00-" + ParentTraceId + "-" + ParentSpanId + "-01";

        private readonly CreateContextMock _createContext = new CreateContextMock();
        private readonly PerformContextMock _performContext = new PerformContextMock();
        private readonly List<Activity> _activities = new List<Activity>();
        private readonly List<(string Instrument, double Value, Dictionary<string, object> Tags)> _measurements =
            new List<(string, double, Dictionary<string, object>)>();

        private ActivityListener _activityListener;
        private MeterListener _meterListener;

        public TelemetryFilterFacts()
        {
            Activity.Current = null;
            _createContext.InitialState.Setup(x => x.Name).Returns("Enqueued");
        }

        public void Dispose()
        {
            _activityListener?.Dispose();
            _meterListener?.Dispose();
            Activity.Current = null;
        }

        [Fact]
        public void OnCreating_StoresTheCurrentTraceContext_EvenWhenNobodyListensToHangfire()
        {
            using var parent = new Activity("Parent").SetIdFormat(ActivityIdFormat.W3C).Start();
            parent.TraceStateString = "vendor=value";
            var context = _createContext.GetCreatingContext();

            new TelemetryFilter().OnCreating(context);

            Assert.Equal(parent.Id, context.GetJobParameter<string>(TelemetryFilter.TraceParentParameter));
            Assert.Equal("vendor=value", context.GetJobParameter<string>(TelemetryFilter.TraceStateParameter));
        }

        [Fact]
        public void OnCreating_DoesNotStoreTraceContext_WhenThereIsNoActivity()
        {
            var context = _createContext.GetCreatingContext();

            new TelemetryFilter().OnCreating(context);

            Assert.False(context.Parameters.ContainsKey(TelemetryFilter.TraceParentParameter));
            Assert.False(context.Parameters.ContainsKey(TelemetryFilter.TraceStateParameter));
        }

        [Fact]
        public void OnCreatingAndOnCreated_ProduceAProducerSpan_ThatIsPropagatedToTheJob()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();
            var creating = _createContext.GetCreatingContext();

            filter.OnCreating(creating);
            filter.OnCreated(_createContext.GetCreatedContext("123"));

            var span = Assert.Single(_activities);
            Assert.Equal("create CreateContextMock.Method", span.DisplayName);
            Assert.Equal(ActivityKind.Producer, span.Kind);
            Assert.Equal("hangfire", span.GetTagItem("messaging.system"));
            Assert.Equal("123", span.GetTagItem("messaging.message.id"));
            Assert.Equal("Enqueued", span.GetTagItem("hangfire.job.state"));
            Assert.Equal(span.Id, creating.GetJobParameter<string>(TelemetryFilter.TraceParentParameter));
            Assert.Null(Activity.Current);
        }

        [Fact]
        public void OnCreated_MarksTheSpanAsFailed_WhenCreationFailed()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();

            filter.OnCreating(_createContext.GetCreatingContext());
            filter.OnCreated(_createContext.GetCreatedContext("123", exception: new InvalidOperationException("Storage is down")));

            var span = Assert.Single(_activities);
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        }

        [Fact]
        public void OnPerformingAndOnPerformed_ProduceAConsumerSpan_ThatContinuesTheStoredTrace()
        {
            SetupStoredTraceParent();
            ListenToActivities();
            var filter = new TelemetryFilter();

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext());

            var span = Assert.Single(_activities);
            Assert.Equal("process BackgroundJobMock.SomeMethod", span.DisplayName);
            Assert.Equal(ActivityKind.Consumer, span.Kind);
            Assert.Equal(ParentTraceId, span.TraceId.ToHexString());
            Assert.Equal(ParentSpanId, span.ParentSpanId.ToHexString());
            Assert.Equal(JobId, span.GetTagItem("messaging.message.id"));
            Assert.Equal(TelemetryFilter.OutcomeSucceeded, span.GetTagItem("hangfire.job.outcome"));
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Null(Activity.Current);
        }

        [Fact]
        public void OnPerforming_StartsANewTrace_WhenTheJobHasNoStoredTraceContext()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext());

            var span = Assert.Single(_activities);
            Assert.Equal(default, span.ParentSpanId);
        }

        [Fact]
        public void OnPerformed_ReportsTheOriginalException_WhenTheJobFailed()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();
            var failure = new JobPerformanceException("Job failed", new InvalidOperationException("Boom"));

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext(exception: failure));

            var span = Assert.Single(_activities);
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal("Boom", span.StatusDescription);
            Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
            Assert.Equal(TelemetryFilter.OutcomeFailed, span.GetTagItem("hangfire.job.outcome"));
            Assert.Contains(span.Events, x => x.Name == "exception");
        }

        [Fact]
        public void OnPerformed_TreatsAnAbortedJob_AsCanceled_NotFailed()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext(exception: new JobAbortedException()));

            var span = Assert.Single(_activities);
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Equal(TelemetryFilter.OutcomeCanceled, span.GetTagItem("hangfire.job.outcome"));
        }

        [Fact]
        public void OnPerforming_DoesNotQueryTheStorage_WhenNobodyListens()
        {
            var filter = new TelemetryFilter();

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext());

            _performContext.Connection.Verify(
                x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public void Metrics_RecordTheDurationAndOutcome_AndBalanceActiveJobs()
        {
            ListenToMetrics();
            var filter = new TelemetryFilter();
            var failure = new JobPerformanceException("Job failed", new InvalidOperationException("Boom"));

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext(exception: failure));

            var active = _measurements.Where(x => x.Instrument == "hangfire.job.active").ToArray();
            Assert.Equal(new[] { 1.0, -1.0 }, active.Select(x => x.Value));
            Assert.All(active, x => Assert.Equal("BackgroundJobMock.SomeMethod", x.Tags["hangfire.job.name"]));

            var duration = Assert.Single(_measurements, x => x.Instrument == "hangfire.job.duration");
            Assert.True(duration.Value >= 0);
            Assert.Equal(TelemetryFilter.OutcomeFailed, duration.Tags["hangfire.job.outcome"]);
            Assert.Equal(typeof(InvalidOperationException).FullName, duration.Tags["error.type"]);
        }

        [Fact]
        public void Metrics_CountCreatedJobs_WithTheirInitialState()
        {
            ListenToMetrics();
            var filter = new TelemetryFilter();

            filter.OnCreating(_createContext.GetCreatingContext());
            filter.OnCreated(_createContext.GetCreatedContext("123"));

            var created = Assert.Single(_measurements, x => x.Instrument == "hangfire.job.created");
            Assert.Equal(1, created.Value);
            Assert.Equal("Enqueued", created.Tags["hangfire.job.state"]);
            Assert.Equal("CreateContextMock.Method", created.Tags["hangfire.job.name"]);
        }

        [Fact]
        public void Metrics_DoNotDecrementActiveJobs_ThatWereNotIncremented()
        {
            var filter = new TelemetryFilter();

            filter.OnPerforming(_performContext.GetPerformingContext());
            ListenToMetrics(); // A listener attached while the job is running
            filter.OnPerformed(_performContext.GetPerformedContext());

            Assert.DoesNotContain(_measurements, x => x.Instrument == "hangfire.job.active");
        }

        [Fact]
        public void UseTelemetry_RegistersTheFilterGlobally()
        {
            var configuration = new Mock<IGlobalConfiguration>();
            var before = GlobalJobFilters.Filters.Count(x => x.Instance is TelemetryFilter);

            try
            {
                configuration.Object.UseTelemetry();

                Assert.Equal(before + 1, GlobalJobFilters.Filters.Count(x => x.Instance is TelemetryFilter));
            }
            finally
            {
                GlobalJobFilters.Filters.Remove(typeof(TelemetryFilter));
            }
        }

        private void SetupStoredTraceParent()
        {
            _performContext.Connection
                .Setup(x => x.GetJobParameter(JobId, TelemetryFilter.TraceParentParameter))
                .Returns($"\"{TraceParent}\"");
        }

        private void ListenToActivities()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == HangfireTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _activities.Add(activity)
            };

            ActivitySource.AddActivityListener(_activityListener);
        }

        private void ListenToMetrics()
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == HangfireTelemetry.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };

            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _meterListener.Start();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            var tagMap = new Dictionary<string, object>();
            foreach (var tag in tags) tagMap[tag.Key] = tag.Value;

            _measurements.Add((instrument.Name, value, tagMap));
        }
    }
}
