using System.Diagnostics;
using System.Diagnostics.Metrics;
using Masterly.NightShift.Client;
using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Diagnostics;

public sealed class TelemetryFilterFacts : IDisposable
{
    private const string JobId = "JobId";
    private const string ParentTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string ParentSpanId = "b7ad6b7169203331";
    private const string TraceParent = "00-" + ParentTraceId + "-" + ParentSpanId + "-01";

    private readonly CreateContextMock _createContext = new();
    private readonly PerformContextMock _performContext = new();
    private readonly List<Activity> _activities = [];
    private readonly List<(string Instrument, double Value, Dictionary<string, object> Tags)> _measurements =
        [];

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
        using Activity parent = new Activity("Parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        parent.TraceStateString = "vendor=value";
        CreatingContext context = _createContext.GetCreatingContext();

        new TelemetryFilter().OnCreating(context);

        Assert.Equal(parent.Id, context.GetJobParameter<string>(TelemetryFilter.TraceParentParameter));
        Assert.Equal("vendor=value", context.GetJobParameter<string>(TelemetryFilter.TraceStateParameter));
    }

    [Fact]
    public void OnCreating_DoesNotStoreTraceContext_WhenThereIsNoActivity()
    {
        CreatingContext context = _createContext.GetCreatingContext();

        new TelemetryFilter().OnCreating(context);

        Assert.False(context.Parameters.ContainsKey(TelemetryFilter.TraceParentParameter));
        Assert.False(context.Parameters.ContainsKey(TelemetryFilter.TraceStateParameter));
    }

    [Fact]
    public void Filter_AllocatesNothing_WhenNobodyListens()
    {
        // .NET 8 keeps Instrument.Enabled true after the last MeterListener is disposed (fixed in .NET 9),
        // so once an earlier test listened, "nobody listens" can't be set up in this process
        Assert.SkipWhen(
            NightShiftTelemetry.ActiveJobs.Enabled || NightShiftTelemetry.JobsCompleted.Enabled || NightShiftTelemetry.JobDuration.Enabled ||
            NightShiftTelemetry.JobLateness.Enabled || NightShiftTelemetry.JobsCreated.Enabled,
            "The runtime reports the instruments as enabled although no listener is attached (.NET 8 behaviour).");
        TelemetryFilter filter = new();
        CreatingContext warmCreating = _createContext.GetCreatingContext();
        CreatingContext creating = _createContext.GetCreatingContext();
        CreatedContext created = _createContext.GetCreatedContext("123");
        PerformingContext performing = _performContext.GetPerformingContext();
        PerformedContext performed = _performContext.GetPerformedContext(exception: new JobPerformanceException("Job failed", new InvalidOperationException("Boom")));
        filter.OnCreating(warmCreating);
        filter.OnCreated(created);
        filter.OnPerforming(performing);
        filter.OnPerformed(performed);

        long before = GC.GetAllocatedBytesForCurrentThread();
        filter.OnCreating(creating);
        filter.OnCreated(created);
        filter.OnPerforming(performing);
        filter.OnPerformed(performed);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void OnCreatingAndOnCreated_ProduceAProducerSpan_ThatIsPropagatedToTheJob()
    {
        ListenToActivities();
        TelemetryFilter filter = new();
        CreatingContext creating = _createContext.GetCreatingContext();

        filter.OnCreating(creating);
        filter.OnCreated(_createContext.GetCreatedContext("123"));

        Activity span = Assert.Single(_activities);
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
        TelemetryFilter filter = new();

        filter.OnCreating(_createContext.GetCreatingContext());
        filter.OnCreated(_createContext.GetCreatedContext("123", exception: new InvalidOperationException("Storage is down")));

        Activity span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
    }

    [Fact]
    public void OnPerformingAndOnPerformed_ProduceAConsumerSpan_ThatContinuesTheStoredTrace()
    {
        SetupStoredTraceParent();
        ListenToActivities();
        TelemetryFilter filter = new();

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext());

        Activity span = Assert.Single(_activities);
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
        TelemetryFilter filter = new();

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext());

        Activity span = Assert.Single(_activities);
        Assert.Equal(default, span.ParentSpanId);
    }

    [Fact]
    public void OnPerformed_ReportsTheOriginalException_WhenTheJobFailed()
    {
        ListenToActivities();
        TelemetryFilter filter = new();
        JobPerformanceException failure = new("Job failed", new InvalidOperationException("Boom"));

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: failure));

        Activity span = Assert.Single(_activities);
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
        TelemetryFilter filter = new();

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: new JobAbortedException()));

        Activity span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(TelemetryFilter.OutcomeCanceled, span.GetTagItem("nightshift.job.outcome"));
    }

    [Fact]
    public void OnPerformed_TreatsAJobStoppedByServerShutdown_AsCanceled()
    {
        ListenToActivities();
        _performContext.CancellationToken.Setup(x => x.ShutdownToken).Returns(new System.Threading.CancellationToken(canceled: true));
        TelemetryFilter filter = new();

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: new OperationCanceledException()));

        Activity span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(TelemetryFilter.OutcomeCanceled, span.GetTagItem("nightshift.job.outcome"));
    }

    [Fact]
    public void OnPerformed_ReportsAJobThatCanceledItself_AsFailed()
    {
        // The performer wraps an OperationCanceledException thrown while the server isn't stopping
        ListenToActivities();
        TelemetryFilter filter = new();
        JobPerformanceException failure = new("Job failed", new OperationCanceledException("Timed out"));

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: failure));

        Activity span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(TelemetryFilter.OutcomeFailed, span.GetTagItem("nightshift.job.outcome"));
        Assert.Equal(typeof(OperationCanceledException).FullName, span.GetTagItem("error.type"));
    }

    [Fact]
    public void OnPerformed_ReportsRetryLater_WithoutMarkingTheSpanAsFailed()
    {
        ListenToActivities();
        ListenToMetrics();
        TelemetryFilter filter = new();
        JobPerformanceException retryLater = new("Job failed", new RetryLaterException(TimeSpan.FromHours(1), "partner busy"));

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: retryLater));

        Activity span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(TelemetryFilter.OutcomeRetryLater, span.GetTagItem("nightshift.job.outcome"));
        Assert.Null(span.GetTagItem("error.type"));
        Assert.DoesNotContain(span.Events, x => x.Name == "exception");

        (string Instrument, double Value, Dictionary<string, object> Tags) duration = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.duration");
        Assert.Equal("retry_later", duration.Tags["nightshift.job.outcome"]);
        Assert.Null(duration.Tags["error.type"]);
    }

    [Fact]
    public void OnPerformed_ReportsSkipped_WithoutMarkingTheSpanAsFailed()
    {
        ListenToActivities();
        ListenToMetrics();
        TelemetryFilter filter = new();
        JobPerformanceException skipped = new("Job failed", new TerminateJobException("nothing to do"));

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: skipped));

        Activity span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(TelemetryFilter.OutcomeSkipped, span.GetTagItem("nightshift.job.outcome"));
        Assert.Null(span.GetTagItem("error.type"));

        (string Instrument, double Value, Dictionary<string, object> Tags) duration = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.duration");
        Assert.Equal("skipped", duration.Tags["nightshift.job.outcome"]);
    }

    [Fact]
    public void OnPerformed_ReportsAJobTerminatedAsFailed_AsFailed()
    {
        ListenToActivities();
        TelemetryFilter filter = new();
        JobPerformanceException failed = new("Job failed", new TerminateJobException("invalid input", TerminateJobOutcome.Failed));

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: failed));

        Activity span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(TelemetryFilter.OutcomeFailed, span.GetTagItem("nightshift.job.outcome"));
        Assert.Equal(typeof(TerminateJobException).FullName, span.GetTagItem("error.type"));
    }

    [Fact]
    public void OnPerforming_DoesNotQueryTheStorage_WhenNobodyListens()
    {
        TelemetryFilter filter = new();

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
        TelemetryFilter filter = new();
        JobPerformanceException failure = new("Job failed", new InvalidOperationException("Boom"));

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: failure));

        (string Instrument, double Value, Dictionary<string, object> Tags)[] active = [.. _measurements.Where(x => x.Instrument == "nightshift.job.active")];
        Assert.Equal([1.0, -1.0], active.Select(x => x.Value));
        Assert.All(active, x => Assert.Equal("BackgroundJobMock.SomeMethod", x.Tags["nightshift.job.name"]));

        (string Instrument, double Value, Dictionary<string, object> Tags) duration = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.duration");
        Assert.True(duration.Value >= 0);
        Assert.Equal(TelemetryFilter.OutcomeFailed, duration.Tags["nightshift.job.outcome"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, duration.Tags["error.type"]);
    }

    [Fact]
    public void Metrics_CountCreatedJobs_WithTheirInitialState()
    {
        ListenToMetrics();
        TelemetryFilter filter = new();

        filter.OnCreating(_createContext.GetCreatingContext());
        filter.OnCreated(_createContext.GetCreatedContext("123"));

        (string Instrument, double Value, Dictionary<string, object> Tags) created = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.created");
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
        TelemetryFilter filter = new();

        filter.OnPerforming(_performContext.GetPerformingContext());
        ListenToMetrics(); // A listener attached while the job is running
        filter.OnPerformed(_performContext.GetPerformedContext());

        Assert.DoesNotContain(_measurements, x => x.Instrument == "nightshift.job.active");
    }

    [Fact]
    public void OnCreated_ReportsTheQueueChosenDuringStateElection()
    {
        ListenToActivities();
        TelemetryFilter filter = new();
        EnqueuedState initialState = new();
        CreateContext create = new(_createContext.Storage.Object, _createContext.Connection.Object, _createContext.Job, initialState);

        filter.OnCreating(new CreatingContext(create));
        initialState.Queue = "critical"; // What [Queue("critical")] does during the state election
        filter.OnCreated(new CreatedContext(create, new BackgroundJob("123", _createContext.Job, DateTime.UtcNow), false, null));

        Activity span = Assert.Single(_activities);
        Assert.Equal("critical", span.GetTagItem("messaging.destination.name"));
    }

    [Fact]
    public void OnCreated_ReportsTheJobsOwnQueue_WhenTheStateKeepsTheDefaultOne()
    {
        ListenToActivities();
        TelemetryFilter filter = new();
        Job job = Job.FromExpression(() => CreateContextMock.Method(), "emails");
        CreateContext create = new(_createContext.Storage.Object, _createContext.Connection.Object, job, new EnqueuedState());

        filter.OnCreating(new CreatingContext(create));
        filter.OnCreated(new CreatedContext(create, new BackgroundJob("123", job, DateTime.UtcNow), false, null));

        Activity span = Assert.Single(_activities);
        Assert.Equal("emails", span.GetTagItem("messaging.destination.name"));
    }

    [Fact]
    public void OnPerforming_ReportsTheQueueTheJobWasFetchedFrom()
    {
        ListenToActivities();
        TelemetryFilter filter = new();
        ApplyStateContextMock applyContext = CreateProcessingContext();
        applyContext.Connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData
        {
            Name = EnqueuedState.StateName,
            Data = new Dictionary<string, string> { { "Queue", "critical" } }
        });

        filter.OnStateApplied(applyContext.Object, applyContext.Transaction.Object);
        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext());

        Activity span = Assert.Single(_activities);
        Assert.Equal("critical", span.GetTagItem("messaging.destination.name"));
    }

    [Fact]
    public void OnPerforming_FallsBackToTheJobsQueue_WhenTheEnqueuedStateCannotBeRead()
    {
        ListenToActivities();
        TelemetryFilter filter = new();
        ApplyStateContextMock applyContext = CreateProcessingContext();
        applyContext.Connection.Setup(x => x.GetStateData(JobId)).Throws(new InvalidOperationException("storage is down"));

        filter.OnStateApplied(applyContext.Object, applyContext.Transaction.Object);
        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext());

        Activity span = Assert.Single(_activities);
        Assert.Null(span.GetTagItem("messaging.destination.name"));
    }

    [Fact]
    public void OnStateApplied_DoesNotQueryTheStorage_WhenNobodyListens()
    {
        ApplyStateContextMock applyContext = CreateProcessingContext();

        new TelemetryFilter().OnStateApplied(applyContext.Object, applyContext.Transaction.Object);

        applyContext.Connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_IgnoresTransitionsOtherThanEnqueuedToProcessing()
    {
        ListenToActivities();
        ApplyStateContextMock applyContext = new()
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
        Mock<IGlobalConfiguration> configuration = new();
        int before = GlobalJobFilters.Filters.Count(x => x.Instance is TelemetryFilter);

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
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };

        ActivitySource.AddActivityListener(_activityListener);
    }

    [Fact]
    public void OnPerformed_CountsTheCompletedRun_WithItsOutcome_AndAddsASkipEvent()
    {
        ListenToActivities();
        ListenToMetrics();
        TelemetryFilter filter = new();

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext(exception: new JobPerformanceException("Job failed", new TerminateJobException("nothing to do"))));

        (string Instrument, double Value, Dictionary<string, object> Tags) completed = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.completed");
        Assert.Equal(1, completed.Value);
        Assert.Equal("skipped", completed.Tags["nightshift.job.outcome"]);

        ActivityEvent skipped = Assert.Single(Assert.Single(_activities).Events, x => x.Name == "nightshift.job.skipped");
        Assert.Equal("nothing to do", skipped.Tags.Single(x => x.Key == "nightshift.job.reason").Value);
    }

    [Fact]
    public void OnPerforming_RecordsTheLateness_OfAJobScheduledForAMoment()
    {
        ListenToMetrics();
        _performContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [PerformContext.ScheduledForParameter] = JobHelper.ToMillisecondTimestamp(DateTime.UtcNow.AddMinutes(-2)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        new TelemetryFilter().OnPerforming(_performContext.GetPerformingContext());

        (string Instrument, double Value, Dictionary<string, object> Tags) lateness = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.lateness");
        Assert.InRange(lateness.Value, 110, 200);
    }

    [Fact]
    public void OnPerforming_RecordsTheLateness_WithoutTheRecurringJob()
    {
        // One series per recurring job would grow without bound
        ListenToMetrics();
        _performContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            ["Time"] = JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(-1)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RecurringJobId"] = "\"nightly-report\"",
        };

        new TelemetryFilter().OnPerforming(_performContext.GetPerformingContext());

        (string Instrument, double Value, Dictionary<string, object> Tags) lateness = Assert.Single(_measurements, x => x.Instrument == "nightshift.job.lateness");
        Assert.False(lateness.Tags.ContainsKey("nightshift.recurring_job.id"));
    }

    [Fact]
    public void OnPerforming_PutsTheRecurringJob_OnTheProcessSpan()
    {
        ListenToActivities();
        _performContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            ["RecurringJobId"] = "\"nightly-report\"",
        };

        TelemetryFilter filter = new();

        filter.OnPerforming(_performContext.GetPerformingContext());
        filter.OnPerformed(_performContext.GetPerformedContext());

        Assert.Equal("nightly-report", Assert.Single(_activities).GetTagItem("nightshift.recurring_job.id"));
    }

    [Theory]
    [InlineData("RetryCount", "1")]
    [InlineData("StaleRestarts", "1")]
    public void OnPerforming_RecordsNoLateness_ForARetryOrARestart(string parameter, string value)
    {
        // The first run recorded it already: a later run would count the earlier attempts again
        ListenToMetrics();
        _performContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [PerformContext.ScheduledForParameter] = JobHelper.ToMillisecondTimestamp(DateTime.UtcNow.AddMinutes(-2)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [parameter] = value,
        };

        new TelemetryFilter().OnPerforming(_performContext.GetPerformingContext());

        Assert.DoesNotContain(_measurements, x => x.Instrument == "nightshift.job.lateness");
    }

    [Fact]
    public void OnStateApplied_CountsARetry_OnlyOnceTheStateChangeIsCommitted()
    {
        ListenToMetrics();
        ApplyStateContextMock context = new()
        {
            OldStateName = ProcessingState.StateName,
            NewStateObject = new ScheduledState(TimeSpan.FromMinutes(1)) { Reason = "Retry attempt 1 of 10: boom" },
        };
        PostCommitActions afterCommit = new();
        context.Object.AfterCommit = afterCommit;

        new TelemetryFilter().OnStateApplied(context.Object, null);

        Assert.DoesNotContain(_measurements, x => x.Instrument == "nightshift.job.retried");

        afterCommit.Run();

        Assert.Single(_measurements, x => x.Instrument == "nightshift.job.retried");
    }

    [Fact]
    public void OnStateApplied_DoesNotCountARetry_WhoseStateChangeFailed()
    {
        ListenToMetrics();
        ApplyStateContextMock context = new()
        {
            OldStateName = ProcessingState.StateName,
            NewStateObject = new ScheduledState(TimeSpan.FromMinutes(1)) { Reason = "Retry attempt 1 of 10: boom" },
        };
        PostCommitActions afterCommit = new();
        context.Object.AfterCommit = afterCommit;

        new TelemetryFilter().OnStateApplied(context.Object, null);
        afterCommit.RunAfterFailure();

        Assert.DoesNotContain(_measurements, x => x.Instrument == "nightshift.job.retried");
    }

    [Fact]
    public void OnPerforming_RecordsNoLateness_ForAFireAndForgetJob()
    {
        ListenToMetrics();
        _performContext.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();

        new TelemetryFilter().OnPerforming(_performContext.GetPerformingContext());

        Assert.DoesNotContain(_measurements, x => x.Instrument == "nightshift.job.lateness");
    }

    [Fact]
    public void OnStateApplied_CountsARetry()
    {
        ListenToMetrics();
        ApplyStateContextMock context = new()
        {
            OldStateName = ProcessingState.StateName,
            NewStateObject = new ScheduledState(TimeSpan.FromMinutes(1)) { Reason = "Retry attempt 1 of 10: boom" },
        };

        new TelemetryFilter().OnStateApplied(context.Object, null);

        Assert.Single(_measurements, x => x.Instrument == "nightshift.job.retried");
    }

    [Fact]
    public void OnStateApplied_DoesNotCountAHold_AsARetry()
    {
        ListenToMetrics();
        ApplyStateContextMock context = new()
        {
            OldStateName = ProcessingState.StateName,
            NewStateObject = new ScheduledState(TimeSpan.FromMinutes(1)) { Reason = "Held: processing is paused" },
        };

        new TelemetryFilter().OnStateApplied(context.Object, null);

        Assert.DoesNotContain(_measurements, x => x.Instrument == "nightshift.job.retried");
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
        Dictionary<string, object> tagMap = [];
        foreach (KeyValuePair<string, object> tag in tags)
        {
            tagMap[tag.Key] = tag.Value;
        }

        _measurements.Add((instrument.Name, value, tagMap));
    }
}
