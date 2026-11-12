using System.Globalization;
using Masterly.NightShift.Server;
using LogLevel = Masterly.NightShift.Logging.LogLevel;

namespace Masterly.NightShift.Core.Tests;

public class SkipIfLateAttributeFacts
{
    private readonly PerformContextMock _context = new();
    private readonly Dictionary<string, string> _parameters = [];

    public SkipIfLateAttributeFacts()
    {
        _context.BackgroundJob.ParametersSnapshot = _parameters;
    }

    [Theory]
    [InlineData("00:30:00", 1800)]
    [InlineData("1.00:00:00", 86400)]
    [InlineData("00:00:05", 5)]
    public void Ctor_ParsesTheWindow(string window, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), new SkipIfLateAttribute(window).Window);
    }

    [Fact]
    public void Ctor_TakesTheWindowInSeconds()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), new SkipIfLateAttribute(120).Window);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    public void Ctor_ThrowsAnException_WhenTheWindowIsNotATimeSpan(string window)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkipIfLateAttribute(window));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    public void Ctor_ThrowsAnException_WhenTheWindowIsNotPositive(string window)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkipIfLateAttribute(window));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_ThrowsAnException_WhenTheSecondsAreNotPositive(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkipIfLateAttribute(seconds));
    }

    [Fact]
    public void OnPerforming_LetsAJobThatIsOnTimeRun_AndRecordsTheCheck()
    {
        ScheduledAgo(TimeSpan.FromMinutes(1));

        new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"));
    }

    [Fact]
    public void OnPerforming_SkipsAJobThatStartedTooLate_AndRecordsTheCheck()
    {
        ScheduledAgo(TimeSpan.FromMinutes(45));

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext()));

        Assert.Equal(TerminateJobOutcome.Skipped, exception.Outcome);
        Assert.StartsWith("started 45 min late, more than the 10 min allowed", exception.Message, StringComparison.Ordinal);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"));
    }

    [Fact]
    public void OnPerforming_DoesNotCheckAgain_OnceChecked()
    {
        // A retry, or a job requeued by hand after it was skipped, runs however late it is
        ScheduledAgo(TimeSpan.FromDays(3));
        _parameters[SkipIfLateAttribute.CheckedParameter] = "true";

        new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnPerforming_MeasuresAJobWithoutScheduledFor_FromItsCreation()
    {
        _context.BackgroundJob.CreatedAt = DateTime.UtcNow.AddHours(-2);

        Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("01:00:00").OnPerforming(_context.GetPerformingContext()));
    }

    [Fact]
    public void OnPerforming_LetsARunExactlyAtTheWindowRun()
    {
        SkipIfLateAttribute attribute = new("01:00:00");
        _context.BackgroundJob.CreatedAt = DateTime.UtcNow.AddMinutes(-59);

        attribute.OnPerforming(_context.GetPerformingContext());
    }

    [Fact]
    public void OnPerforming_ThrowsAnException_WhenContextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SkipIfLateAttribute(60).OnPerforming(null));
    }

    [Fact]
    public void OnPerformed_DoesNothing()
    {
        new SkipIfLateAttribute(60).OnPerformed(_context.GetPerformedContext());

        _context.Connection.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(30, "30 s")]
    [InlineData(90, "1.5 min")]
    [InlineData(5400, "1.5 h")]
    [InlineData(259200, "3 days")]
    public void Format_UsesAReadableUnit(int seconds, string expected)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            Assert.Equal(expected, SkipIfLateAttribute.Format(TimeSpan.FromSeconds(seconds)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void LateStartFilter_SkipsARunLaterThanItsSkipAfterParameter()
    {
        ScheduledAgo(TimeSpan.FromMinutes(20));
        _parameters[SkipIfLateAttribute.WindowParameter] = "300";

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new LateStartFilter().OnPerforming(_context.GetPerformingContext()));

        Assert.Equal(TerminateJobOutcome.Skipped, exception.Outcome);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"));
    }

    [Fact]
    public void LateStartFilter_LetsARunOnTimeRun_AndRecordsTheCheck()
    {
        ScheduledAgo(TimeSpan.FromMinutes(1));
        _parameters[SkipIfLateAttribute.WindowParameter] = "300";

        new LateStartFilter().OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("soon")]
    public void LateStartFilter_DoesNothing_WithoutAValidSkipAfterParameter(string value)
    {
        ScheduledAgo(TimeSpan.FromDays(2));
        if (value != null)
        {
            _parameters[SkipIfLateAttribute.WindowParameter] = value;
        }

        new LateStartFilter().OnPerforming(_context.GetPerformingContext());

        _context.Connection.VerifyNoOtherCalls();
    }

    [Fact]
    public void LateStartFilter_DoesNotCheckAgain_OnceChecked()
    {
        ScheduledAgo(TimeSpan.FromDays(2));
        _parameters[SkipIfLateAttribute.WindowParameter] = "300";
        _parameters[SkipIfLateAttribute.CheckedParameter] = "true";

        new LateStartFilter().OnPerforming(_context.GetPerformingContext());

        _context.Connection.VerifyNoOtherCalls();
    }

    [Fact]
    public void LateStartFilter_ReadsNothingFromTheStorage_WithoutAParameterSnapshot()
    {
        PerformContextMock context = new();
        context.BackgroundJob.CreatedAt = DateTime.UtcNow.AddDays(-2);

        new LateStartFilter().OnPerforming(context.GetPerformingContext());

        context.Connection.VerifyNoOtherCalls();
    }

    [Fact]
    public void LateStartFilter_IsAGlobalFilterByDefault()
    {
        Assert.Contains(GlobalJobFilters.Filters, filter => filter.Instance is LateStartFilter);
    }

    [Fact]
    public void LateStartFilter_ThrowsAnException_WhenContextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new LateStartFilter().OnPerforming(null));
    }

    [Fact]
    public void OnPerforming_MeasuresAContinuation_FromWhenItBecameDue()
    {
        // Created 3 s ago, its parent ran meanwhile, and it became due just now: it is on time
        _context.BackgroundJob.CreatedAt = DateTime.UtcNow.AddSeconds(-3);
        _parameters[PerformContext.DueAtParameter] = Millis(DateTime.UtcNow);

        new SkipIfLateAttribute(1).OnPerforming(_context.GetPerformingContext());

        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"));
    }

    [Fact]
    public void OnPerforming_SkipsAContinuation_ThatWaitedTooLongAfterItBecameDue()
    {
        _context.BackgroundJob.CreatedAt = DateTime.UtcNow.AddHours(-3);
        _parameters[PerformContext.DueAtParameter] = Millis(DateTime.UtcNow.AddHours(-2));

        Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("01:00:00").OnPerforming(_context.GetPerformingContext()));
    }

    [Fact]
    public void Check_RecordsTheCheckOnce_WhenTheAttributeAndSkipAfterBothApply()
    {
        ScheduledAgo(TimeSpan.FromMinutes(1));
        _parameters[SkipIfLateAttribute.WindowParameter] = "300";
        PerformingContext context = _context.GetPerformingContext();

        new LateStartFilter().OnPerforming(context);
        new SkipIfLateAttribute("00:10:00").OnPerforming(context);

        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"), Times.Once);
    }

    [Fact]
    public void Check_TheStricterWindowWins_WhicheverFilterRunsFirst()
    {
        // 7 minutes late: within the attribute's 10 minutes, past SkipAfter's 5
        ScheduledAgo(TimeSpan.FromMinutes(7));
        _parameters[SkipIfLateAttribute.WindowParameter] = "300";
        PerformingContext context = _context.GetPerformingContext();

        new SkipIfLateAttribute("00:10:00").OnPerforming(context);
        TerminateJobException exception = Assert.Throws<TerminateJobException>(() => new LateStartFilter().OnPerforming(context));

        Assert.StartsWith("started 7 min late, more than the 5 min allowed", exception.Message, StringComparison.Ordinal);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"), Times.Once);
    }

    [Fact]
    public void Check_DoesNotCheckAgain_AgainstALooserWindow()
    {
        ScheduledAgo(TimeSpan.FromMinutes(7));
        _parameters[SkipIfLateAttribute.WindowParameter] = "600";
        PerformingContext context = _context.GetPerformingContext();

        new SkipIfLateAttribute("00:10:00").OnPerforming(context);
        new LateStartFilter().OnPerforming(context);

        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"), Times.Once);
    }

    [Fact]
    public void OnPerforming_StillChecksTheJob_WhenTheCheckCannotBeRecorded()
    {
        ScheduledAgo(TimeSpan.FromMinutes(45));
        _context.Connection
            .Setup(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage is down"));

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext()));

        Assert.Equal(TerminateJobOutcome.Skipped, exception.Outcome);
    }

    [Fact]
    public void OnPerforming_LetsAJobRun_WhenTheCheckCannotBeRecorded()
    {
        ScheduledAgo(TimeSpan.FromMinutes(1));
        _context.Connection
            .Setup(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage is down"));

        new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void OnPerforming_SaysThatTheJobWaitedForItsPolicy_WhenItSkipsAJobThatDid(string failedChecks)
    {
        // Zero: the policy could be evaluated again and let the job run. Two: enqueued by hand while it waited
        ScheduledAgo(TimeSpan.FromHours(3));
        WaitedForPolicy("business-hours", failedChecks);

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext()));

        Assert.Equal(TerminateJobOutcome.Skipped, exception.Outcome);
        Assert.Equal("started 3 h late: waited for policy 'business-hours', which couldn't be evaluated", exception.Message);
        Assert.Equal(
            "Skipped: started 3 h late: waited for policy 'business-hours', which couldn't be evaluated",
            ElectedReason(exception));
        _context.Connection.Verify(x => x.SetJobParameter("JobId", SkipIfLateAttribute.CheckedParameter, "true"));
    }

    [Fact]
    public void LateStartFilter_SaysThatTheJobWaitedForItsPolicy_WhenItSkipsAJobThatDid()
    {
        ScheduledAgo(TimeSpan.FromMinutes(20));
        _parameters[SkipIfLateAttribute.WindowParameter] = "300";
        WaitedForPolicy("business-hours", "0");

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new LateStartFilter().OnPerforming(_context.GetPerformingContext()));

        Assert.Equal("started 20 min late: waited for policy 'business-hours', which couldn't be evaluated", exception.Message);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "not a number")]
    public void OnPerforming_GivesThePlainReason_WhenTheJobDidNotWaitForAPolicy(bool hasParameter, string failedChecks)
    {
        // The job follows a policy, but never waited for it, or was moved by it since: the count is gone
        ScheduledAgo(TimeSpan.FromMinutes(45));
        _parameters[ScheduleOptions.PolicyParameter] = SerializationHelper.Serialize("business-hours");
        if (hasParameter)
        {
            _parameters[SchedulePolicyFilter.CheckAttemptsParameter] = failedChecks;
        }

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext()));

        Assert.Equal("started 45 min late, more than the 10 min allowed", exception.Message);
        Assert.Equal("Skipped: started 45 min late, more than the 10 min allowed", ElectedReason(exception));
    }

    [Fact]
    public void OnPerforming_GivesThePlainReason_WhenTheCountIsThereWithoutAPolicy()
    {
        ScheduledAgo(TimeSpan.FromMinutes(45));
        _parameters[SchedulePolicyFilter.CheckAttemptsParameter] = "0";

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext()));

        Assert.Equal("started 45 min late, more than the 10 min allowed", exception.Message);
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(45 * 60, "45 min")]
    [InlineData(3 * 3600, "3 h")]
    [InlineData(400 * 86400, "400 days")]
    public void OnPerforming_KeepsTheReasonWithinTheShortestLimitOfTheStorages_ByShorteningThePolicyName(int secondsLate, string late)
    {
        // SQL Server keeps the first 99 characters of a reason, and a policy name has up to 64
        string policy = $"business-hours-of-the-{new string('x', 35)}-office";
        ScheduledAgo(TimeSpan.FromSeconds(secondsLate));
        WaitedForPolicy(policy, "0");

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute(10).OnPerforming(_context.GetPerformingContext()));

        string reason = ElectedReason(exception);
        Assert.Equal(SchedulePolicy.MaxNameLength, policy.Length);
        Assert.Equal(99, reason.Length);
        Assert.StartsWith($"Skipped: started {late} late: waited for policy 'business-hours", reason, StringComparison.Ordinal);
        Assert.EndsWith("…', which couldn't be evaluated", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnPerforming_KeepsAPolicyNameWhole_WhenTheReasonHasRoomForIt()
    {
        // The name takes the room a lateness of six characters leaves, to the last character
        ScheduledAgo(TimeSpan.FromMinutes(45));
        WaitedForPolicy("trading-calendar-nyc", "0");

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute(10).OnPerforming(_context.GetPerformingContext()));

        string reason = ElectedReason(exception);
        Assert.Equal("Skipped: started 45 min late: waited for policy 'trading-calendar-nyc', which couldn't be evaluated", reason);
        Assert.Equal(99, reason.Length);
    }

    [Fact, GlobalLock(Reason = "Uses the log provider")]
    public void OnPerforming_LogsAWarningOnce_WhenItSkipsAJobThatWaitedForItsPolicy()
    {
        RecordingLogProvider recording = new();
        string policy = $"business-hours-of-the-{new string('x', 35)}-office";
        ScheduledAgo(TimeSpan.FromHours(3));
        _parameters[SkipIfLateAttribute.WindowParameter] = "300";
        WaitedForPolicy(policy, "0");

        using (recording.Use())
        {
            Assert.Throws<TerminateJobException>(() => new LateStartFilter().OnPerforming(_context.GetPerformingContext()));
        }

        // The whole name, which the reason had no room for
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Equal(
            "Background job 'JobId' is skipped: it started 3 h late, more than the 5 min allowed, after it waited for " +
            $"schedule policy '{policy}', which couldn't be evaluated. The job is dropped because of the policy, not " +
            "because of anything the job did: the warnings about the policy say what failed, and the job runs when it " +
            "is requeued by hand.",
            warning.Message);
    }

    [Fact, GlobalLock(Reason = "Uses the log provider")]
    public void OnPerforming_LogsNoWarning_WhenItSkipsAJobThatDidNotWaitForAPolicy()
    {
        RecordingLogProvider recording = new();
        ScheduledAgo(TimeSpan.FromMinutes(45));

        using (recording.Use())
        {
            Assert.Throws<TerminateJobException>(
                () => new SkipIfLateAttribute("00:10:00").OnPerforming(_context.GetPerformingContext()));
        }

        Assert.DoesNotContain(recording.Entries, static x => x.Level == LogLevel.Warn);
    }

    [Fact]
    public void OnPerforming_GivesThePlainReason_WhenTheParametersCanNotBeRead()
    {
        // No parameters were loaded with the job, and the storage fails: the job is skipped all the same
        PerformContextMock context = new();
        context.BackgroundJob.CreatedAt = DateTime.UtcNow.AddHours(-2);
        context.Connection
            .Setup(x => x.GetJobParameter("JobId", SchedulePolicyFilter.CheckAttemptsParameter))
            .Throws(new InvalidOperationException("storage is down"));

        TerminateJobException exception = Assert.Throws<TerminateJobException>(
            () => new SkipIfLateAttribute("01:00:00").OnPerforming(context.GetPerformingContext()));

        Assert.Equal("started 2 h late, more than the 1 h allowed", exception.Message);
    }

    // The reason of the Deleted state the exception ends the job with
    private static string ElectedReason(TerminateJobException exception)
    {
        ElectStateContextMock election = new();
        election.ApplyContext.NewStateObject = new FailedState(exception);

        new TerminateJobFilter().OnStateElection(election.Object);

        return Assert.IsType<DeletedState>(election.Object.CandidateState).Reason;
    }

    // What SchedulePolicyFilter leaves on a job that waited for a policy it couldn't evaluate
    private void WaitedForPolicy(string policy, string failedChecks)
    {
        _parameters[ScheduleOptions.PolicyParameter] = SerializationHelper.Serialize(policy);
        _parameters[SchedulePolicyFilter.CheckAttemptsParameter] = failedChecks;
    }

    private static string Millis(DateTime value)
    {
        return JobHelper.ToMillisecondTimestamp(value).ToString(CultureInfo.InvariantCulture);
    }

    private void ScheduledAgo(TimeSpan ago)
    {
        DateTime scheduledFor = DateTime.UtcNow - ago;
        _context.BackgroundJob.CreatedAt = scheduledFor.AddDays(-1);
        _parameters[PerformContext.ScheduledForParameter] = JobHelper.ToMillisecondTimestamp(scheduledFor).ToString(CultureInfo.InvariantCulture);
    }
}
