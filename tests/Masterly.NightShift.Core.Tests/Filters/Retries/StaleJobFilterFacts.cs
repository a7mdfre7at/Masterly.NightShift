using System.Diagnostics.Metrics;

namespace Masterly.NightShift.Core.Tests;

public sealed class StaleJobFilterFacts : IDisposable
{
    private readonly StaleJobFilter _filter = new();
    private readonly ElectStateContextMock _context = new();
    private readonly Dictionary<string, string> _snapshot = [];

    public StaleJobFilterFacts()
    {
        _context.ApplyContext.BackgroundJob.ParametersSnapshot = _snapshot;
        _context.ApplyContext.NewStateObject = new ProcessingState("server-b", "worker-b");
    }

    public void Dispose()
    {
        StaleJobFilter.MaxRestarts = null;
    }

    [Fact]
    public void Filter_IsAGlobalFilter_RunAfterTheHoldingFilters()
    {
        Assert.Contains(GlobalJobFilters.Filters, static x => x.Instance is StaleJobFilter);
        Assert.Equal(999, _filter.Order);
    }

    [Fact]
    public void OnStateElection_DoesNothing_ForAJobStartingNormally()
    {
        _context.ApplyContext.OldStateName = EnqueuedState.StateName;

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_DoesNothing_ForAnotherState()
    {
        _context.ApplyContext.OldStateName = ProcessingState.StateName;
        _context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);

        _filter.OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_HandsAnOldLeaseToTheNewWorker_WhenTheJobStartsAgainNormally()
    {
        _context.ApplyContext.OldStateName = EnqueuedState.StateName;
        _snapshot[StaleJobFilter.LeaseParameter] = "\"worker-a\"";

        _filter.OnStateElection(_context.Object);

        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", StaleJobFilter.LeaseParameter, "\"worker-b\""));
    }

    [Fact]
    public void OnStateElection_CountsARestart_AndTakesTheLease()
    {
        _context.ApplyContext.OldStateName = ProcessingState.StateName;
        _snapshot[StaleJobFilter.RestartsParameter] = "2";

        _filter.OnStateElection(_context.Object);

        Assert.IsType<ProcessingState>(_context.Object.CandidateState);
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", StaleJobFilter.RestartsParameter, "3"));
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", StaleJobFilter.LeaseParameter, "\"worker-b\""));
    }

    [Fact, StaticLock]
    public void OnStateElection_FailsTheJobForGood_PastTheLimit()
    {
        StaleJobFilter.MaxRestarts = 2;
        _context.ApplyContext.OldStateName = ProcessingState.StateName;
        _snapshot[StaleJobFilter.RestartsParameter] = "2";

        _filter.OnStateElection(_context.Object);

        FailedState failed = Assert.IsType<FailedState>(_context.Object.CandidateState);
        TerminateJobException exception = Assert.IsType<TerminateJobException>(failed.Exception);
        Assert.Equal(TerminateJobOutcome.Failed, exception.Outcome);
        Assert.Equal("Failed: stale: its server stopped 3 times before it finished", failed.Reason);
    }

    [Fact, StaticLock]
    public void OnStateElection_CountsTheRestart_ThatFailsTheJob()
    {
        StaleJobFilter.MaxRestarts = 2;
        _context.ApplyContext.OldStateName = ProcessingState.StateName;
        _snapshot[StaleJobFilter.RestartsParameter] = "2";

        _filter.OnStateElection(_context.Object);

        Assert.IsType<FailedState>(_context.Object.CandidateState);
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", StaleJobFilter.RestartsParameter, "3"));
        _context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", StaleJobFilter.LeaseParameter, It.IsAny<string>()), Times.Never);
    }

    [Theory, StaticLock]
    [InlineData(null, "restarted")]
    [InlineData(0, "failed")]
    public void OnStateElection_TagsTheRestartMetric_WithWhatBecameOfTheJob(int? limit, string outcome)
    {
        StaleJobFilter.MaxRestarts = limit;
        _context.ApplyContext.OldStateName = ProcessingState.StateName;
        List<KeyValuePair<string, object>[]> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NightShiftTelemetry.MeterName && instrument.Name == "nightshift.job.stale_restarts")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        listener.Start();

        _filter.OnStateElection(_context.Object);

        KeyValuePair<string, object>[] tags = Assert.Single(measurements);
        Assert.Contains(new KeyValuePair<string, object>("nightshift.job.outcome", outcome), tags);
    }

    [Fact, StaticLock]
    public void OnStateElection_FailsTheFirstRestart_WithALimitOfZero()
    {
        StaleJobFilter.MaxRestarts = 0;
        _context.ApplyContext.OldStateName = ProcessingState.StateName;

        _filter.OnStateElection(_context.Object);

        Assert.Equal("Failed: stale: its server stopped before it finished", Assert.IsType<FailedState>(_context.Object.CandidateState).Reason);
    }

    [Fact, StaticLock]
    public void MaxRestarts_RefusesANegativeLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>("value", () => StaleJobFilter.MaxRestarts = -1);
    }

    [Fact, StaticLock]
    public void UseStaleRestartLimit_SetsTheLimit()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;

        Assert.Same(configuration, configuration.UseStaleRestartLimit(5));
        Assert.Equal(5, StaleJobFilter.MaxRestarts);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("\"worker-a\"", true)]
    [InlineData("\"worker-b\"", false)]
    public void IsLeaseHeldBy_ComparesTheLease_WithTheWorker(string lease, bool expected)
    {
        Dictionary<string, string> parameters = [];
        if (lease != null) parameters[StaleJobFilter.LeaseParameter] = lease;

        Assert.Equal(expected, StaleJobFilter.IsLeaseHeldBy(parameters, "worker-a"));
        Assert.True(StaleJobFilter.IsLeaseHeldBy(null, "worker-a"));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1, 2]")]
    public void IsLeaseHeldBy_IgnoresALeaseThatCannotBeRead(string lease)
    {
        // Read inside the state change: throwing there would keep the job from ever leaving Processing
        Dictionary<string, string> parameters = new() { [StaleJobFilter.LeaseParameter] = lease };

        Assert.True(StaleJobFilter.IsLeaseHeldBy(parameters, "worker-a"));
    }

    [Fact]
    public void LongRunningAttribute_SendsTheJobToItsQueue()
    {
        EnqueuedState enqueued = new("critical");
        _context.ApplyContext.NewStateObject = enqueued;

        new LongRunningAttribute().OnStateElection(_context.Object);

        Assert.Equal(LongRunningAttribute.Queue, enqueued.Queue);
        Assert.Equal(int.MaxValue, new LongRunningAttribute().Order);
    }

    [Fact]
    public void BackgroundJobServerOptions_ValidatesTheNewOptions()
    {
        BackgroundJobServerOptions options = new();

        Assert.Equal(0, options.LongRunningWorkerCount);
        Assert.Equal(ServerStartMode.Automatic, options.StartMode);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.LongRunningWorkerCount = -1);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.StartMode = (ServerStartMode)5);
    }
}
