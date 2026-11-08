// UseTelemetry() and the filter exist on net8.0 and later only (ActivitySource and Meter are not on .NET Framework)
#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Diagnostics
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
        public void OnCreating_StoresTheCurrentTraceContext_EvenWhenNobodyListensToNightShift()
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
            Assert.Equal("nightshift", span.GetTagItem("messaging.system"));
            Assert.Equal("123", span.GetTagItem("messaging.message.id"));
            Assert.Equal("Enqueued", span.GetTagItem("nightshift.job.state"));
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
            Assert.Equal(TelemetryFilter.OutcomeSucceeded, span.GetTagItem("nightshift.job.outcome"));
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
            Assert.Equal(TelemetryFilter.OutcomeFailed, span.GetTagItem("nightshift.job.outcome"));
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
            Assert.Equal(TelemetryFilter.OutcomeCanceled, span.GetTagItem("nightshift.job.outcome"));
        }

        [Fact]
        public void OnPerformed_TreatsAJobStoppedByServerShutdown_AsCanceled()
        {
            ListenToActivities();
            _performContext.CancellationToken.Setup(x => x.ShutdownToken).Returns(new System.Threading.CancellationToken(canceled: true));
            var filter = new TelemetryFilter();

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext(exception: new OperationCanceledException()));

            var span = Assert.Single(_activities);
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Equal(TelemetryFilter.OutcomeCanceled, span.GetTagItem("nightshift.job.outcome"));
        }

        [Fact]
        public void OnPerformed_ReportsAJobThatCanceledItself_AsFailed()
        {
            // The performer wraps an OperationCanceledException thrown while the server isn't stopping
            ListenToActivities();
            var filter = new TelemetryFilter();
            var failure = new JobPerformanceException("Job failed", new OperationCanceledException("Timed out"));

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext(exception: failure));

            var span = Assert.Single(_activities);
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal(TelemetryFilter.OutcomeFailed, span.GetTagItem("nightshift.job.outcome"));
            Assert.Equal(typeof(OperationCanceledException).FullName, span.GetTagItem("error.type"));
        }

        [Fact]
        public void OnPerformed_ReportsRetryLater_WithoutMarkingTheSpanAsFailed()
        {
            ListenToActivities();
            ListenToMetrics();
            var filter = new TelemetryFilter();
            var retryLater = new JobPerformanceException("Job failed", new RetryLaterException(TimeSpan.FromHours(1), "partner busy"));

            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext(exception: retryLater));

            var span = Assert.Single(_activities);
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Equal(TelemetryFilter.OutcomeRetryLater, span.GetTagItem("nightshift.job.outcome"));
            Assert.Null(span.GetTagItem("error.type"));
            Assert.DoesNotContain(span.Events, x => x.Name == "exception");

            var duration = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.duration");
            Assert.Equal("retry_later", duration.Tags["nightshift.job.outcome"]);
            Assert.Null(duration.Tags["error.type"]);
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

            var active = _measurements.Where(x => x.Instrument == "nightshift.job.active").ToArray();
            Assert.Equal(new[] { 1.0, -1.0 }, active.Select(x => x.Value));
            Assert.All(active, x => Assert.Equal("BackgroundJobMock.SomeMethod", x.Tags["nightshift.job.name"]));

            var duration = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.duration");
            Assert.True(duration.Value >= 0);
            Assert.Equal(TelemetryFilter.OutcomeFailed, duration.Tags["nightshift.job.outcome"]);
            Assert.Equal(typeof(InvalidOperationException).FullName, duration.Tags["error.type"]);
        }

        [Fact]
        public void Metrics_CountCreatedJobs_WithTheirInitialState()
        {
            ListenToMetrics();
            var filter = new TelemetryFilter();

            filter.OnCreating(_createContext.GetCreatingContext());
            filter.OnCreated(_createContext.GetCreatedContext("123"));

            var created = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.created");
            Assert.Equal(1, created.Value);
            Assert.Equal("Enqueued", created.Tags["nightshift.job.state"]);
            Assert.Equal("CreateContextMock.Method", created.Tags["nightshift.job.name"]);
        }

        [Fact]
        public void Metrics_DoNotDecrementActiveJobs_ThatWereNotIncremented()
        {
            // .NET 8 keeps Instrument.Enabled true after the last MeterListener is disposed (fixed in .NET 9),
            // so once an earlier test listened, "nobody listens yet" can't be set up in this process
            Assert.SkipWhen(
                NightShiftTelemetry.ActiveJobs.Enabled,
                "The runtime reports the instrument as enabled although no listener is attached (.NET 8 behaviour).");
            var filter = new TelemetryFilter();

            filter.OnPerforming(_performContext.GetPerformingContext());
            ListenToMetrics(); // A listener attached while the job is running
            filter.OnPerformed(_performContext.GetPerformedContext());

            Assert.DoesNotContain(_measurements, x => x.Instrument == "nightshift.job.active");
        }

        [Fact]
        public void OnCreated_ReportsTheQueueChosenDuringStateElection()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();
            var initialState = new EnqueuedState();
            var create = new CreateContext(_createContext.Storage.Object, _createContext.Connection.Object, _createContext.Job, initialState);

            filter.OnCreating(new CreatingContext(create));
            initialState.Queue = "critical"; // What [Queue("critical")] does during the state election
            filter.OnCreated(new CreatedContext(create, new BackgroundJob("123", _createContext.Job, DateTime.UtcNow), false, null));

            var span = Assert.Single(_activities);
            Assert.Equal("critical", span.GetTagItem("messaging.destination.name"));
        }

        [Fact]
        public void OnCreated_ReportsTheJobsOwnQueue_WhenTheStateKeepsTheDefaultOne()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();
            var job = Job.FromExpression(() => CreateContextMock.Method(), "emails");
            var create = new CreateContext(_createContext.Storage.Object, _createContext.Connection.Object, job, new EnqueuedState());

            filter.OnCreating(new CreatingContext(create));
            filter.OnCreated(new CreatedContext(create, new BackgroundJob("123", job, DateTime.UtcNow), false, null));

            var span = Assert.Single(_activities);
            Assert.Equal("emails", span.GetTagItem("messaging.destination.name"));
        }

        [Fact]
        public void OnPerforming_ReportsTheQueueTheJobWasFetchedFrom()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();
            var applyContext = CreateProcessingContext();
            applyContext.Connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData
            {
                Name = EnqueuedState.StateName,
                Data = new Dictionary<string, string> { { "Queue", "critical" } }
            });

            filter.OnStateApplied(applyContext.Object, applyContext.Transaction.Object);
            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext());

            var span = Assert.Single(_activities);
            Assert.Equal("critical", span.GetTagItem("messaging.destination.name"));
        }

        [Fact]
        public void OnPerforming_FallsBackToTheJobsQueue_WhenTheEnqueuedStateCannotBeRead()
        {
            ListenToActivities();
            var filter = new TelemetryFilter();
            var applyContext = CreateProcessingContext();
            applyContext.Connection.Setup(x => x.GetStateData(JobId)).Throws(new InvalidOperationException("storage is down"));

            filter.OnStateApplied(applyContext.Object, applyContext.Transaction.Object);
            filter.OnPerforming(_performContext.GetPerformingContext());
            filter.OnPerformed(_performContext.GetPerformedContext());

            var span = Assert.Single(_activities);
            Assert.Null(span.GetTagItem("messaging.destination.name"));
        }

        [Fact]
        public void OnStateApplied_DoesNotQueryTheStorage_WhenNobodyListens()
        {
            var applyContext = CreateProcessingContext();

            new TelemetryFilter().OnStateApplied(applyContext.Object, applyContext.Transaction.Object);

            applyContext.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void OnStateApplied_IgnoresTransitionsOtherThanEnqueuedToProcessing()
        {
            ListenToActivities();
            var applyContext = new ApplyStateContextMock
            {
                BackgroundJob = _performContext.BackgroundJob,
                NewStateObject = new SucceededState(null, 1, 1),
                OldStateName = ProcessingState.StateName
            };

            new TelemetryFilter().OnStateApplied(applyContext.Object, applyContext.Transaction.Object);

            applyContext.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void UseTelemetry_RegistersTheFilterGlobally()
        {
            var configuration = new Mock<IGlobalConfiguration>();
            var before = GlobalJobFilters.Filters.Count(x => x.Instance is TelemetryFilter);

            try
            {
                configuration.Object.UseTelemetry();
                configuration.Object.UseTelemetry();

                // Several hosts in one process (tests, for example) configure it again: it is added once
                Assert.Equal(Math.Max(before, 1), GlobalJobFilters.Filters.Count(x => x.Instance is TelemetryFilter));
            }
            finally
            {
                GlobalJobFilters.Filters.Remove(typeof(TelemetryFilter));
            }
        }

        private ApplyStateContextMock CreateProcessingContext()
        {
            // The worker's state change shares its BackgroundJob instance with the perform context
            return new ApplyStateContextMock
            {
                BackgroundJob = _performContext.BackgroundJob,
                NewStateObject = new ProcessingState("server", "worker"),
                OldStateName = EnqueuedState.StateName
            };
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
                ShouldListenTo = source => source.Name == NightShiftTelemetry.ActivitySourceName,
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
                    if (instrument.Meter.Name == NightShiftTelemetry.MeterName)
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
#endif
