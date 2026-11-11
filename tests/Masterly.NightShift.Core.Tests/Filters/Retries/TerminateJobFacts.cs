using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class TerminateJobFacts
{
    private readonly ElectStateContextMock _context = new();

    [Fact]
    public void Ctor_DefaultsToSkipped()
    {
        TerminateJobException exception = new("nothing to do");

        Assert.Equal(TerminateJobOutcome.Skipped, exception.Outcome);
        Assert.Equal("nothing to do", exception.Message);
        Assert.True(exception.DeletesJob);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(TerminateJobOutcome.Skipped, true)]
    [InlineData(TerminateJobOutcome.Canceled, true)]
    [InlineData(TerminateJobOutcome.Failed, false)]
    public void DeletesJob_IsFalseOnlyForFailed(TerminateJobOutcome outcome, bool deletes)
    {
        Assert.Equal(deletes, new TerminateJobException("reason", outcome).DeletesJob);
    }

    [Fact]
    public void Ctor_KeepsTheInnerException()
    {
        TimeoutException inner = new();

        TerminateJobException exception = new("reason", TerminateJobOutcome.Failed, inner);

        Assert.Same(inner, exception.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_ThrowsAnException_WhenReasonIsMissing(string reason)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TerminateJobException(reason));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenOutcomeIsUnknown()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminateJobException("reason", (TerminateJobOutcome)42));
    }

    [Fact]
    public void Find_ReturnsTheException_WhenThrownDirectly()
    {
        TerminateJobException exception = new("reason");

        Assert.Same(exception, TerminateJobFilter.Find(exception));
        Assert.Same(exception, TerminateJobFilter.Find(new FailedState(exception)));
    }

    [Fact]
    public void Find_ReturnsTheException_WhenWrappedByThePerformer()
    {
        TerminateJobException exception = new("reason");

        Assert.Same(exception, TerminateJobFilter.Find(new JobPerformanceException("wrapped", exception)));
    }

    [Fact]
    public void Find_ReturnsNull_ForOtherExceptionsAndStates()
    {
        Assert.Null(TerminateJobFilter.Find((Exception)null));
        Assert.Null(TerminateJobFilter.Find(new InvalidOperationException()));
        Assert.Null(TerminateJobFilter.Find(new JobPerformanceException("wrapped", new InvalidOperationException())));
        Assert.Null(TerminateJobFilter.Find((IState)null));
        Assert.Null(TerminateJobFilter.Find(new SucceededState(null, 0, 0)));
    }

    [Fact]
    public void OnStateElection_DeletesASkippedJob_WithTheReason()
    {
        _context.ApplyContext.NewStateObject = new FailedState(new TerminateJobException("The invoice is already paid"));

        new TerminateJobFilter().OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal("Skipped: The invoice is already paid", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_DeletesACanceledJob_WithTheReason()
    {
        _context.ApplyContext.NewStateObject = new FailedState(new TerminateJobException("Stopped by the operator", TerminateJobOutcome.Canceled));

        new TerminateJobFilter().OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal("Canceled: Stopped by the operator", deleted.Reason);
    }

    [Fact]
    public void OnStateElection_KeepsAJobTerminatedAsFailed_Failed()
    {
        FailedState failed = new(new TerminateJobException("Invalid input", TerminateJobOutcome.Failed));
        _context.ApplyContext.NewStateObject = failed;

        new TerminateJobFilter().OnStateElection(_context.Object);

        Assert.Same(failed, _context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_IgnoresOtherFailures_AndOtherStates()
    {
        FailedState failed = new(new InvalidOperationException());
        _context.ApplyContext.NewStateObject = failed;

        new TerminateJobFilter().OnStateElection(_context.Object);

        Assert.Same(failed, _context.Object.CandidateState);

        ElectStateContextMock succeeded = new();
        SucceededState state = new(null, 0, 0);
        succeeded.ApplyContext.NewStateObject = state;

        new TerminateJobFilter().OnStateElection(succeeded.Object);

        Assert.Same(state, succeeded.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_ShortensALongReason()
    {
        _context.ApplyContext.NewStateObject = new FailedState(new TerminateJobException(new string('x', 300)));

        new TerminateJobFilter().OnStateElection(_context.Object);

        DeletedState deleted = Assert.IsType<DeletedState>(_context.Object.CandidateState);
        Assert.Equal(100, deleted.Reason.Length);
        Assert.StartsWith("Skipped: xxx", deleted.Reason, StringComparison.Ordinal);
        Assert.EndsWith("…", deleted.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnStateElection_ThrowsAnException_WhenContextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new TerminateJobFilter().OnStateElection(null));
    }

    [Fact]
    public void Filter_RunsBeforeAutomaticRetry()
    {
        Assert.True(new TerminateJobFilter().Order < new AutomaticRetryAttribute().Order);
    }

    [Fact]
    public void Filter_IsAGlobalFilterByDefault()
    {
        Assert.Contains(GlobalJobFilters.Filters, filter => filter.Instance is TerminateJobFilter);
    }

    [Theory]
    [InlineData(TerminateJobOutcome.Failed)]
    [InlineData(TerminateJobOutcome.Skipped)]
    [InlineData(TerminateJobOutcome.Canceled)]
    public void AutomaticRetry_DoesNotRetryATerminatedJob(TerminateJobOutcome outcome)
    {
        Mock<IStorageConnection> connection = new();
        _context.ApplyContext.Connection = connection;
        FailedState failed = new(new TerminateJobException("reason", outcome));
        _context.ApplyContext.NewStateObject = failed;

        new AutomaticRetryAttribute { Attempts = 3 }.OnStateElection(_context.Object);

        Assert.Same(failed, _context.Object.CandidateState);
        connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), "RetryCount", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void AutomaticRetry_StillRetriesOtherFailures()
    {
        Mock<IStorageConnection> connection = new();
        _context.ApplyContext.Connection = connection;
        _context.ApplyContext.NewStateObject = new FailedState(new InvalidOperationException("boom"));

        new AutomaticRetryAttribute { Attempts = 3, DelaysInSeconds = [10] }.OnStateElection(_context.Object);

        Assert.IsType<ScheduledState>(_context.Object.CandidateState);
    }
}
