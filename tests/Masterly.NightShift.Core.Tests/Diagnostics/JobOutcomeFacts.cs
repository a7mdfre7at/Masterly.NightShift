using System.Diagnostics;
using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Diagnostics;

public class JobOutcomeFacts
{
    private readonly PerformContextMock _context = new();

    [Fact]
    public void Classify_ReturnsSucceeded_WhenTheJobReturned()
    {
        Assert.Equal(JobOutcome.Succeeded, JobOutcomes.Classify(_context.GetPerformedContext()));
    }

    [Fact]
    public void Classify_ReturnsSucceeded_WhenAFilterHandledTheException()
    {
        PerformedContext context = _context.GetPerformedContext(exception: Wrap(new InvalidOperationException()));
        context.ExceptionHandled = true;

        Assert.Equal(JobOutcome.Succeeded, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsCanceled_WhenAFilterCanceledTheExecution()
    {
        Assert.Equal(JobOutcome.Canceled, JobOutcomes.Classify(_context.GetPerformedContext(canceled: true)));
    }

    [Fact]
    public void Classify_ReturnsCanceled_WhenAStateChangeAbortedTheJob()
    {
        Assert.Equal(JobOutcome.Canceled, JobOutcomes.Classify(_context.GetPerformedContext(exception: new JobAbortedException())));
    }

    [Fact]
    public void Classify_ReturnsCanceled_WhenTheServerShutDownWhileTheJobRan()
    {
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(canceled: true));

        PerformedContext context = _context.GetPerformedContext(exception: new OperationCanceledException());

        Assert.Equal(JobOutcome.Canceled, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsFailed_ForAnUnwrappedOperationCanceledException_WithoutShutdown()
    {
        // The worker records it as a failure when the server isn't stopping
        PerformedContext context = _context.GetPerformedContext(exception: new OperationCanceledException());

        Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsFailed_WhenTheJobCanceledItself()
    {
        // The performer wraps an OperationCanceledException the job throws before shutdown starts, and the
        // worker records a failure, even when the server begins to stop before the filters see it
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(canceled: true));

        PerformedContext context = _context.GetPerformedContext(exception: Wrap(new OperationCanceledException()));

        Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsRetryLater_WhenTheJobThrewRetryLaterException()
    {
        PerformedContext context = _context.GetPerformedContext(exception: Wrap(new RetryLaterException(TimeSpan.FromMinutes(5))));

        Assert.Equal(JobOutcome.RetryLater, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsSkipped_ForAJobTerminatedAsSkipped()
    {
        Assert.Equal(JobOutcome.Skipped, ClassifyTerminated(TerminateJobOutcome.Skipped));
    }

    [Fact]
    public void Classify_ReturnsCanceled_ForAJobTerminatedAsCanceled()
    {
        Assert.Equal(JobOutcome.Canceled, ClassifyTerminated(TerminateJobOutcome.Canceled));
    }

    [Fact]
    public void Classify_ReturnsFailed_ForAJobTerminatedAsFailed()
    {
        Assert.Equal(JobOutcome.Failed, ClassifyTerminated(TerminateJobOutcome.Failed));
    }

    private JobOutcome ClassifyTerminated(TerminateJobOutcome outcome)
    {
        return JobOutcomes.Classify(_context.GetPerformedContext(exception: Wrap(new TerminateJobException("reason", outcome))));
    }

    [Fact]
    public void Classify_ReturnsFailed_ForAnyOtherException()
    {
        PerformedContext context = _context.GetPerformedContext(exception: Wrap(new InvalidOperationException()));

        Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsNotRun_WhenAFilterGaveUpWaitingForASlot_BeforeTheMethodRan()
    {
        PerformedContext context = _context.GetPerformedContext(exception: Wrap(new ConcurrencyLimitException("resource", "All slots stayed in use.")));

        Assert.Equal(JobOutcome.NotRun, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsNotRun_WhenAFilterGaveUpWaitingForALock_BeforeTheMethodRan()
    {
        PerformedContext context = _context.GetPerformedContext(exception: Wrap(new DistributedLockTimeoutException("resource")));

        Assert.Equal(JobOutcome.NotRun, JobOutcomes.Classify(context));
    }

    [Fact]
    public void Classify_ReturnsFailed_WhenTheMethodItselfThrewALockTimeout()
    {
        _context.Object.BackgroundJob.MethodInvokedTimestamp = Stopwatch.GetTimestamp();

        PerformedContext context = _context.GetPerformedContext(exception: Wrap(new DistributedLockTimeoutException("resource")));

        Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
    }

    [Fact]
    public void GetMethodInvokedTimestamp_ReturnsNull_UntilThePerformerCallsTheMethod()
    {
        Assert.Null(JobOutcomes.GetMethodInvokedTimestamp(_context.Object));

        _context.Object.BackgroundJob.MethodInvokedTimestamp = 42L;

        Assert.Equal(42L, JobOutcomes.GetMethodInvokedTimestamp(_context.Object));
    }

    private static JobPerformanceException Wrap(Exception exception)
    {
        return new JobPerformanceException("An exception occurred during performance of the job.", exception);
    }
}
