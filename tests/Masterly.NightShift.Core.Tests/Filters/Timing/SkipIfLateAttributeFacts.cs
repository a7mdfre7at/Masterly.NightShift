using System.Globalization;
using Masterly.NightShift.Server;

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

    private void ScheduledAgo(TimeSpan ago)
    {
        DateTime scheduledFor = DateTime.UtcNow - ago;
        _context.BackgroundJob.CreatedAt = scheduledFor.AddDays(-1);
        _parameters[PerformContext.ScheduledForParameter] = JobHelper.ToMillisecondTimestamp(scheduledFor).ToString(CultureInfo.InvariantCulture);
    }
}
