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
        if (state == JobMonitorState.NeedsAttention)
        {
            Assert.Equal("No job of Reports.Build succeeded today by 07:00 (UTC).", reason);
        }
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
    public void RepeatedFailures_StaysOpen_WhileTheFailuresGoOn_UntilASuccess()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(1)));

        // A failure every 30 minutes: the first and the last of the row soon lie more than an hour apart
        for (int i = 0; i < 6; i++)
        {
            DateTime failedAt = Morning.AddMinutes(30.0 * i);
            JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, failedAt);
            JobMonitors.Check(_storage.Object, _storage.Connection.Object, failedAt.AddMinutes(1));
        }

        Assert.Equal(AlertKind.MonitorIncident, Assert.Single(_sink.Alerts).Kind);
        Assert.Equal(JobMonitorState.NeedsAttention, JobMonitors.GetStatus(_storage.Object).Single().State);

        // Hours without a job: still no success, so still open
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(8));
        Assert.Single(_sink.Alerts);

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddHours(9));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(9).AddMinutes(1));

        Assert.Equal([AlertKind.MonitorIncident, AlertKind.MonitorRecovered], _sink.Alerts.Select(static x => x.Kind));
        Assert.Equal(string.Empty, _storage.Hashes["monitor:failing"]["RecentFailures"]);
    }

    [Fact]
    public void RepeatedFailures_Recovers_WhenASuccessAndANewFailureCameBetweenTwoChecks()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(1)));
        for (int i = 0; i < 3; i++)
        {
            JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddMinutes(10.0 * i));
        }

        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddMinutes(21));
        Assert.Equal(AlertKind.MonitorIncident, Assert.Single(_sink.Alerts).Kind);

        // Before the next check the job succeeds once, then fails once: one failure is no longer an incident
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddMinutes(30));
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddMinutes(40));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddMinutes(41));

        Assert.Equal([AlertKind.MonitorIncident, AlertKind.MonitorRecovered], _sink.Alerts.Select(static x => x.Kind));
        Assert.Equal(JobMonitorState.Healthy, JobMonitors.GetStatus(_storage.Object).Single().State);
    }

    [Fact]
    public void RepeatedFailures_JudgesTheLastFailuresInARow_SoTheWindowSlides()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(1)));

        // Two failures long ago, then three within the hour
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddHours(-10));
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddHours(-5));
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning);
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddMinutes(20));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddMinutes(21));
        Assert.Empty(_sink.Alerts);

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddMinutes(40));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddMinutes(41));

        Assert.Equal(AlertKind.MonitorIncident, Assert.Single(_sink.Alerts).Kind);
    }

    [Fact]
    public void RecordOutcome_KeepsAtMostTwentyFailureTimes()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(1)));

        for (int i = 0; i < 25; i++)
        {
            JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning.AddMinutes(i));
        }

        string[] times = _storage.Hashes["monitor:failing"]["RecentFailures"].Split(',');
        Assert.Equal(20, times.Length);
        Assert.Equal(JobHelper.SerializeDateTime(Morning.AddMinutes(24)), times[^1]);
        Assert.Equal("25", _storage.Hashes["monitor:failing"]["ConsecutiveFailures"]);
    }

    [Fact]
    public void RecordOutcome_RecordsASuccessWithoutALock_AndAtMostEveryFewSeconds_WhileNothingFails()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(1)));

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning);
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddSeconds(1));
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddSeconds(2));

        Assert.Empty(_storage.Locks);
        Assert.Equal(1, _storage.Commits);
        Assert.Equal(JobHelper.SerializeDateTime(Morning), _storage.Hashes["monitor:failing"]["LastSuccessAt"]);

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddSeconds(10));

        Assert.Equal(2, _storage.Commits);
        Assert.Equal(JobHelper.SerializeDateTime(Morning.AddSeconds(10)), _storage.Hashes["monitor:failing"]["LastSuccessAt"]);
    }

    [Fact]
    public void RecordOutcome_TakesTheLock_ForFailures_AndForASuccessAfterThem()
    {
        JobMonitors.Add(new RepeatedFailuresMonitor("failing", "Reports.Build", 3, TimeSpan.FromHours(1)));

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: false, Morning);
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, Morning.AddSeconds(1));

        Assert.Equal(["lock:monitor:failing", "lock:monitor:failing"], _storage.Locks);
        Assert.Equal("0", _storage.Hashes["monitor:failing"]["ConsecutiveFailures"]);
        Assert.Equal(JobHelper.SerializeDateTime(Morning.AddSeconds(1)), _storage.Hashes["monitor:failing"]["LastSuccessAt"]);
    }

    [Fact]
    public void RecordOutcome_WritesTheFirstSuccessOfANewDay_ForADailyDeadline()
    {
        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)));
        DateTime beforeMidnight = Morning.Date.AddDays(1).AddSeconds(-1);

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, beforeMidnight);
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, beforeMidnight.AddSeconds(2));

        Assert.Equal(JobHelper.SerializeDateTime(beforeMidnight.AddSeconds(2)), _storage.Hashes["monitor:daily"]["LastSuccessAt"]);
    }

    [Fact]
    public void DailyDeadline_ThatFallsInADaylightSavingGap_IsDueAtTheFirstValidTime()
    {
        TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById(SchedulePolicyFacts.BerlinTimeZoneId);
        DailyDeadlineMonitor monitor = new("daily", "Reports.Build", new TimeSpan(2, 30, 0), berlin);

        // On 2026-03-29 Berlin's clocks jump from 02:00 to 03:00 (01:00 UTC), so 02:30 doesn't exist that day
        DateTime gapEnd = new(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc);

        Assert.Equal(JobMonitorState.Waiting, monitor.Evaluate(Data(), gapEnd.AddMinutes(-1), _storage.Object).State);
        Assert.Equal(JobMonitorState.NeedsAttention, monitor.Evaluate(Data(), gapEnd.AddMinutes(1), _storage.Object).State);
    }

    [Fact]
    public void DailyDeadline_ThatEndsAfterMidnight_NeedsAttention_WhenNoJobSucceeded()
    {
        DailyDeadlineMonitor monitor = new("nightly", "Reports.Build", TimeSpan.FromHours(23), grace: TimeSpan.FromHours(2));
        DateTime start = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        List<JobMonitorState> states = [];

        for (DateTime now = start; now < start.AddDays(2); now = now.AddMinutes(30))
        {
            states.Add(monitor.Evaluate(Data(), now, _storage.Object).State);
        }

        Assert.Contains(JobMonitorState.NeedsAttention, states);
    }

    [Theory]
    // Until the next night's deadline, the monitor tells about the night before
    [InlineData("2026-11-20 22:30", "2026-11-19 22:10", "Healthy")]
    [InlineData("2026-11-20 22:30", null, "NeedsAttention")]
    [InlineData("2026-11-21 00:30", "2026-11-19 22:10", "Waiting")]
    [InlineData("2026-11-21 00:30", null, "Waiting")]
    [InlineData("2026-11-21 01:00", null, "NeedsAttention")]
    [InlineData("2026-11-21 12:00", null, "NeedsAttention")]
    [InlineData("2026-11-21 00:30", "2026-11-20 22:10", "Healthy")]
    [InlineData("2026-11-21 12:00", "2026-11-20 22:10", "Healthy")]
    // Late, but inside the grace: the success after midnight counts for the night before
    [InlineData("2026-11-21 12:00", "2026-11-21 00:40", "Healthy")]
    // ...and not for the night after
    [InlineData("2026-11-22 01:30", "2026-11-21 00:40", "NeedsAttention")]
    // A success after the incident opened ends it
    [InlineData("2026-11-21 22:30", "2026-11-21 22:10", "Healthy")]
    public void DailyDeadline_ThatEndsAfterMidnight_JudgesTheNightBeforeUntilTheNextDeadline(string now, string lastSuccess, string expected)
    {
        DailyDeadlineMonitor monitor = new("nightly", "Reports.Build", TimeSpan.FromHours(23), grace: TimeSpan.FromHours(2));
        JobMonitorData data = Data(lastSuccess: lastSuccess == null ? null : Utc(lastSuccess));

        (JobMonitorState state, string reason) = monitor.Evaluate(data, Utc(now), _storage.Object);

        Assert.Equal(expected, state.ToString());
        if (state == JobMonitorState.NeedsAttention)
        {
            Assert.StartsWith("No job of Reports.Build succeeded by 23:00 on 2026-11-", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DailyDeadline_ThatEndsAtMidnight_NeedsAttention_AfterMidnight()
    {
        DailyDeadlineMonitor monitor = new("nightly", "Reports.Build", TimeSpan.FromHours(22), grace: TimeSpan.FromHours(2));
        JobMonitorData data = Data(lastSuccess: Utc("2026-11-19 21:00"));

        Assert.Equal(JobMonitorState.Healthy, monitor.Evaluate(data, Utc("2026-11-20 23:59"), _storage.Object).State);
        Assert.Equal(JobMonitorState.NeedsAttention, monitor.Evaluate(data, Utc("2026-11-21 00:01"), _storage.Object).State);
    }

    [Fact]
    public void DailyDeadline_ThatEndsAfterMidnight_FollowsTheClocks_OnDaylightSavingNights()
    {
        TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById(SchedulePolicyFacts.BerlinTimeZoneId);
        DailyDeadlineMonitor monitor = new("nightly", "Reports.Build", new TimeSpan(23, 30, 0), berlin, grace: TimeSpan.FromHours(4));

        // 2026-03-28 23:30 in Berlin is 22:30 UTC; four hours later it is 02:30 UTC, 04:30 after the clocks jumped
        Assert.Equal(JobMonitorState.Waiting, monitor.Evaluate(Data(), Utc("2026-03-29 02:29"), _storage.Object).State);
        Assert.Equal(JobMonitorState.NeedsAttention, monitor.Evaluate(Data(), Utc("2026-03-29 02:31"), _storage.Object).State);

        // A success at 02:00 UTC (04:00 in Berlin, later than 23:30 plus four hours on the clock) is inside that
        // night's grace, and counts for it, not for the next
        JobMonitorData late = Data(lastSuccess: Utc("2026-03-29 02:00"));
        Assert.Equal(JobMonitorState.Healthy, monitor.Evaluate(late, Utc("2026-03-29 12:00"), _storage.Object).State);
        Assert.Equal(JobMonitorState.NeedsAttention, monitor.Evaluate(late, Utc("2026-03-30 02:00"), _storage.Object).State);

        // 2026-10-24 23:30 in Berlin is 21:30 UTC; four hours later it is 01:30 UTC, 02:30 after the clocks went back
        Assert.Equal(JobMonitorState.Waiting, monitor.Evaluate(Data(), Utc("2026-10-25 01:29"), _storage.Object).State);
        Assert.Equal(JobMonitorState.NeedsAttention, monitor.Evaluate(Data(), Utc("2026-10-25 01:31"), _storage.Object).State);
    }

    [Fact]
    public void RecordOutcome_WritesTheFirstSuccessOfTheNextNight_ForADeadlineThatEndsAfterMidnight()
    {
        // A run in the half day after 23:00 is the night before's (in its grace, or late); one from 11:00 on is the next night's
        JobMonitors.Add(new DailyDeadlineMonitor("nightly", "Reports.Build", TimeSpan.FromHours(23), grace: TimeSpan.FromHours(2)));
        DateTime lastOfTheNightBefore = Utc("2026-11-21 11:00").AddSeconds(-1);

        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, lastOfTheNightBefore);
        JobMonitors.RecordOutcome(_storage.Object, "Reports.Build", succeeded: true, lastOfTheNightBefore.AddSeconds(2));

        Assert.Equal(JobHelper.SerializeDateTime(lastOfTheNightBefore.AddSeconds(2)), _storage.Hashes["monitor:nightly"]["LastSuccessAt"]);
    }

    [Fact]
    public void DailyDeadline_ThatEndsAfterMidnight_CountsALateRun_ForTheNightItWasLateFor_NotForTheNext()
    {
        // The night of the 20th is missed and its run comes at 01:30, after the grace; the night of the 21st is
        // missed too: that late run must not stand for it
        DailyDeadlineMonitor monitor = new("nightly", "Reports.Build", TimeSpan.FromHours(23), grace: TimeSpan.FromHours(2));
        JobMonitorData lateRun = Data(lastSuccess: Utc("2026-11-21 01:30"));

        (JobMonitorState afterTheLateRun, _) = monitor.Evaluate(lateRun, Utc("2026-11-21 02:00"), _storage.Object);
        (JobMonitorState afterTheNextNight, string? reason) = monitor.Evaluate(lateRun, Utc("2026-11-22 01:30"), _storage.Object);

        Assert.Equal(JobMonitorState.Healthy, afterTheLateRun);
        Assert.Equal(JobMonitorState.NeedsAttention, afterTheNextNight);
        Assert.Contains("2026-11-21", reason, StringComparison.Ordinal);
    }

    private static DateTime Utc(string text)
    {
        return DateTime.SpecifyKind(DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }

    [Fact]
    public void Check_GoesOnWithTheOtherMonitors_WhenOneCannotBeRead()
    {
        JobMonitors.Add(new DailyDeadlineMonitor("broken", "Reports.Build", TimeSpan.FromHours(7)));
        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)));
        _storage.Connection.Setup(x => x.GetAllEntriesFromHash("monitor:broken")).Throws(new InvalidOperationException("corrupt"));

        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(2));

        Assert.Equal("daily", Assert.Single(_sink.Alerts).Monitor);
        IReadOnlyList<JobMonitorStatus> statuses = JobMonitors.GetStatus(_storage.Object);
        Assert.Equal(JobMonitorState.Unknown, statuses.Single(static x => x.Name == "broken").State);
        Assert.Equal(JobMonitorState.NeedsAttention, statuses.Single(static x => x.Name == "daily").State);
    }

    [Fact]
    public void Check_ReadsAHashWithCorruptValues()
    {
        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)));
        _storage.Hashes["monitor:daily"] = new Dictionary<string, string>
        {
            ["State"] = "Bogus",
            ["CheckedAt"] = "not a date",
            ["LastSuccessAt"] = "99999999999999999999",
            ["RecentFailures"] = "x,,y",
        };

        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(2));

        Assert.Equal(AlertKind.MonitorIncident, Assert.Single(_sink.Alerts).Kind);
        Assert.Equal(JobMonitorState.NeedsAttention, JobMonitors.GetStatus(_storage.Object).Single().State);
    }

    [Fact]
    public void Check_KeepsAnOpenIncident_WhileTheMonitorIsPaused_AndRaisesNoNewOne_WhenItResumes()
    {
        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(2));
        string incidentSince = _storage.Hashes["monitor:daily"]["IncidentSince"];

        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)) { Paused = true });
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(3));

        Assert.Equal(JobMonitorState.Paused, JobMonitors.GetStatus(_storage.Object).Single().State);
        Assert.Equal("NeedsAttention", _storage.Hashes["monitor:daily"]["State"]);

        JobMonitors.Add(new DailyDeadlineMonitor("daily", "Reports.Build", TimeSpan.FromHours(7)));
        JobMonitors.Check(_storage.Object, _storage.Connection.Object, Morning.AddHours(4));

        Assert.Equal(AlertKind.MonitorIncident, Assert.Single(_sink.Alerts).Kind);
        Assert.Equal(incidentSince, _storage.Hashes["monitor:daily"]["IncidentSince"]);
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

        Assert.DoesNotContain("monitor:failing", _storage.Hashes);
        afterCommit.Run();
        Dictionary<string, string> monitor = Assert.Contains("monitor:failing", _storage.Hashes);
        Assert.Equal("1", monitor["ConsecutiveFailures"]);
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
