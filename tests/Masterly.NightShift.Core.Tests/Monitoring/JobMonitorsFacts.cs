using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests;

public sealed class JobMonitorsFacts : IDisposable
{
    // Friday 2026-11-20
    private static readonly DateTime Morning = new(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);

    private readonly PolicyStorage _storage = new();
    private readonly Recording _sink = new();

    public JobMonitorsFacts()
    {
        JobMonitors.Clear();
        Alerts.Clear();
        Alerts.AddSink(_sink);
    }

    public void Dispose()
    {
        JobMonitors.Clear();
        Alerts.Clear();
    }

    [Fact]
    public void Monitors_ValidateTheirSettings()
    {
        Assert.Throws<ArgumentException>("name", () => new LongRunningMonitor("bad name", "A.B", TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>("method", () => new LongRunningMonitor("m", "NoDot", TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>("threshold", () => new LongRunningMonitor("m", "A.B", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>("by", () => new DailyDeadlineMonitor("m", "A.B", TimeSpan.FromHours(24)));
        Assert.Throws<ArgumentOutOfRangeException>("grace", () => new DailyDeadlineMonitor("m", "A.B", TimeSpan.FromHours(7), grace: TimeSpan.FromHours(13)));
        Assert.Throws<ArgumentOutOfRangeException>("failures", () => new RepeatedFailuresMonitor("m", "A.B", 1, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>("window", () => new RepeatedFailuresMonitor("m", "A.B", 3, TimeSpan.FromMinutes(30)));
        Assert.Equal("daily", new DailyDeadlineMonitor(" Daily ", "Reports.Build", TimeSpan.FromHours(7)).Name);
    }

    [Theory]
    [InlineData(6, false, "Waiting")]
    [InlineData(8, false, "NeedsAttention")]
    [InlineData(8, true, "Healthy")]
    public void DailyDeadline_JudgesFromTodaysSuccess(int hour, bool succeededToday, string expected)
    {
        DailyDeadlineMonitor monitor = new("daily", "Reports.Build", TimeSpan.FromHours(7), grace: TimeSpan.FromMinutes(30));
        JobMonitorData data = Data(lastSuccess: succeededToday ? Morning.AddHours(1) : Morning.AddDays(-1));

        (JobMonitorState state, string reason) = monitor.Evaluate(data, Morning.Date.AddHours(hour), _storage.Object);

        Assert.Equal(expected, state.ToString());
        if (state == JobMonitorState.NeedsAttention) Assert.Equal("No job of Reports.Build succeeded today by 07:00 (UTC).", reason);
    }

    [Fact]
    public void DailyDeadline_ReadsTheDeadlineInItsTimeZone()
    {
        TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById(SchedulePolicyFacts.BerlinTimeZoneId);
        DailyDeadlineMonitor monitor = new("daily", "Reports.Build", TimeSpan.FromHours(7), berlin);

        // 06:30 UTC is 07:30 in Berlin
        Assert.Equal(JobMonitorState.NeedsAttention, monitor.Evaluate(Data(), Morning.AddMinutes(30), _storage.Object).State);
        Assert.Equal(JobMonitorState.Waiting, monitor.Evaluate(Data(), Morning.AddMinutes(-30), _storage.Object).State);
    }

    [Fact]
    public void LongRunning_FindsAJobRunningTooLong()
    {
        Mock<JobStorage> storage = new();
        Mock<IMonitoringApi> monitoring = new();
        storage.Setup(x => x.GetMonitoringApi()).Returns(monitoring.Object);
        monitoring.Setup(x => x.ProcessingJobs(0, 1000)).Returns(new JobList<ProcessingJobDto>([
            new("1", new ProcessingJobDto { Job = Job.FromExpression<Reports>(x => x.Build()), StartedAt = Morning.AddMinutes(-90) }),
            new("2", new ProcessingJobDto { Job = Job.FromExpression<Reports>(x => x.Ping()), StartedAt = Morning.AddDays(-1) }),
        ]));

        (JobMonitorState state, string reason) = new LongRunningMonitor("long", "Reports.Build", TimeSpan.FromHours(1)).Evaluate(Data(), Morning, storage.Object);
        (JobMonitorState fine, _) = new LongRunningMonitor("long", "Reports.Build", TimeSpan.FromHours(2)).Evaluate(Data(), Morning, storage.Object);

        Assert.Equal(JobMonitorState.NeedsAttention, state);
        Assert.StartsWith("Job 1 of Reports.Build has run for", reason, StringComparison.Ordinal);
        Assert.Equal(JobMonitorState.Healthy, fine);
    }

    [Fact]
    public void RepeatedFailures_NeedsAttention_ForFailuresInARowWithinTheWindow()
    {
        RepeatedFailuresMonitor monitor = new("failing", "Reports.Build", 3, TimeSpan.FromHours(2));

        Assert.Equal(JobMonitorState.NeedsAttention, monitor.Evaluate(Data(failures: 3, first: Morning.AddHours(-1), last: Morning), Morning, _storage.Object).State);
        Assert.Equal(JobMonitorState.Healthy, monitor.Evaluate(Data(failures: 2, first: Morning.AddHours(-1), last: Morning), Morning, _storage.Object).State);
        Assert.Equal(JobMonitorState.Healthy, monitor.Evaluate(Data(failures: 3, first: Morning.AddHours(-5), last: Morning), Morning, _storage.Object).State);
    }

    [Fact]
    public void RecordOutcome_CountsFailuresInARow_AndASuccessResetsThem()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(2)));

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning);
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddMinutes(5));
        JobMonitors.RecordOutcome(_storage.Object, "Other.Method", succeeded: false, Morning);

        Dictionary<string, string> hash = _storage.Hashes["monitor:failing"];
        Assert.Equal("2", hash["ConsecutiveFailures"]);
        Assert.Equal(JobHelper.SerializeDateTime(Morning), hash["FirstFailureAt"]);
        Assert.Equal(JobHelper.SerializeDateTime(Morning.AddMinutes(5)), hash["LastFailureAt"]);

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddMinutes(10));

        Assert.Equal("0", _storage.Hashes["monitor:failing"]["ConsecutiveFailures"]);
        Assert.Equal(JobHelper.SerializeDateTime(Morning.AddMinutes(10)), _storage.Hashes["monitor:failing"]["LastSuccessAt"]);
    }

    [Fact]
    public void Check_RaisesOneIncident_KeepsItOpen_AndRaisesARecovery()
    {
        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)));

        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning);
        Assert.Empty(_sink.Alerts);

        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(2));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(3));

        Alert incident = Assert.Single(_sink.Alerts);
        Assert.Equal(AlertKind.MonitorIncident, incident.Kind);
        Assert.Equal("daily", incident.Monitor);
        Assert.Equal("Reports.Build", incident.Method);

        // The next morning, before the deadline, the incident is still open
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddDays(1));
        Assert.Equal(JobMonitorState.NeedsAttention, JobMonitors.GetStatus(_storage.Object).Single().State);

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddDays(1).AddMinutes(5));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddDays(1).AddMinutes(10));

        Assert.Equal([AlertKind.MonitorIncident, AlertKind.MonitorRecovered], _sink.Alerts.Select(static x => x.Kind));
        JobMonitorStatus status = JobMonitors.GetStatus(_storage.Object).Single();
        Assert.Equal(JobMonitorState.Healthy, status.State);
        Assert.Null(status.IncidentSince);
    }

    [Fact]
    public void Check_ShowsAPausedMonitor_AndRaisesNothing()
    {
        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)) { Paused = true });

        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(5));

        Assert.Empty(_sink.Alerts);
        Assert.Equal(JobMonitorState.Paused, JobMonitors.GetStatus(_storage.Object).Single().State);
    }

    [Fact]
    public void Check_ShowsUnknown_WhenAMonitorCannotBeChecked()
    {
        Mock<JobStorage> broken = new();
        broken.Setup(x => x.GetMonitoringApi()).Throws(new InvalidOperationException("down"));
        JobMonitors.Add(new LongRunningMonitor("long", "Reports.Build", TimeSpan.FromHours(1)));

        JobMonitors.Check(broken.Object, _storage.Connection.Object, Morning);

        Assert.Equal("Unknown", _storage.Hashes["monitor:long"]["State"]);
        Assert.Equal("down", _storage.Hashes["monitor:long"]["Reason"]);
    }

    [Fact]
    public void Filter_RecordsTheOutcomeOfAMonitoredMethod_AfterTheCommit()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(2)));
        ApplyStateContextMock context = new()
        {
            NewStateObject = new FailedState(new InvalidOperationException()),
            Storage = _storage.Storage,
            BackgroundJob = new BackgroundJobMock { Job = Job.FromExpression<Reports>(x => x.Build()) },
        };
        PostCommitActions afterCommit = new();
        context.Object.AfterCommit = afterCommit;

        new JobMonitorFilter().OnStateApplied(context.Object, null);

        Assert.False(_storage.Hashes.ContainsKey("monitor:failing"));
        afterCommit.Run();
        Assert.Equal("1", _storage.Hashes["monitor:failing"]["ConsecutiveFailures"]);
    }

    [Fact]
    public void Configuration_RegistersTheMonitorAndItsFilter_AndTheCheckInterval()
    {
        try
        {
            new Mock<IGlobalConfiguration>().Object.UseJobMonitor(new LongRunningMonitor("long", "Reports.Build", TimeSpan.FromHours(1)));

            Assert.Equal(["long"], JobMonitors.GetAll().Select(static x => x.Name));
            Assert.Contains(GlobalJobFilters.Filters, static x => x.Instance is JobMonitorFilter);
            Assert.True(JobMonitors.Remove("LONG"));
            Assert.Throws<ArgumentOutOfRangeException>("value", () => JobMonitors.CheckInterval = TimeSpan.FromSeconds(1));
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(typeof(JobMonitorFilter));
        }
    }

    private static JobMonitorData Data(DateTime? lastSuccess = null, int failures = 0, DateTime? first = null, DateTime? last = null)
    {
        return new JobMonitorData(null, null, null, null, lastSuccess, failures, first, last);
    }

    public sealed class Reports
    {
        public void Build()
        {
        }

        public void Ping()
        {
        }
    }

    private sealed class Recording : IAlertSink
    {
        public List<Alert> Alerts { get; } = [];

        public void Publish(Alert alert) => Alerts.Add(alert);
    }
}
