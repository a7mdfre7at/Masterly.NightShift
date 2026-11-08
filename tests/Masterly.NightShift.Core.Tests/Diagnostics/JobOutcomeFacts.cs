using System;
using System.Diagnostics;
using System.Threading;
using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Diagnostics
{
    public class JobOutcomeFacts
    {
        private readonly PerformContextMock _context = new PerformContextMock();

        [Fact]
        public void Classify_ReturnsSucceeded_WhenTheJobReturned()
        {
            Assert.Equal(JobOutcome.Succeeded, JobOutcomes.Classify(_context.GetPerformedContext()));
        }

        [Fact]
        public void Classify_ReturnsSucceeded_WhenAFilterHandledTheException()
        {
            var context = _context.GetPerformedContext(exception: Wrap(new InvalidOperationException()));
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

            var context = _context.GetPerformedContext(exception: new OperationCanceledException());

            Assert.Equal(JobOutcome.Canceled, JobOutcomes.Classify(context));
        }

        [Fact]
        public void Classify_ReturnsFailed_ForAnUnwrappedOperationCanceledException_WithoutShutdown()
        {
            // The worker records it as a failure when the server isn't stopping
            var context = _context.GetPerformedContext(exception: new OperationCanceledException());

            Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
        }

        [Fact]
        public void Classify_ReturnsFailed_WhenTheJobCanceledItself()
        {
            // The performer wraps an OperationCanceledException the job throws before shutdown starts, and the
            // worker records a failure, even when the server begins to stop before the filters see it
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(canceled: true));

            var context = _context.GetPerformedContext(exception: Wrap(new OperationCanceledException()));

            Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
        }

        [Fact]
        public void Classify_ReturnsRetryLater_WhenTheJobThrewRetryLaterException()
        {
            var context = _context.GetPerformedContext(exception: Wrap(new RetryLaterException(TimeSpan.FromMinutes(5))));

            Assert.Equal(JobOutcome.RetryLater, JobOutcomes.Classify(context));
        }

        [Fact]
        public void Classify_ReturnsFailed_ForAnyOtherException()
        {
            var context = _context.GetPerformedContext(exception: Wrap(new InvalidOperationException()));

            Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
        }

        [Fact]
        public void Classify_ReturnsNotRun_WhenAFilterGaveUpWaitingForASlot_BeforeTheMethodRan()
        {
            var context = _context.GetPerformedContext(exception: Wrap(new ConcurrencyLimitException("resource", "All slots stayed in use.")));

            Assert.Equal(JobOutcome.NotRun, JobOutcomes.Classify(context));
        }

        [Fact]
        public void Classify_ReturnsNotRun_WhenAFilterGaveUpWaitingForALock_BeforeTheMethodRan()
        {
            var context = _context.GetPerformedContext(exception: Wrap(new DistributedLockTimeoutException("resource")));

            Assert.Equal(JobOutcome.NotRun, JobOutcomes.Classify(context));
        }

        [Fact]
        public void Classify_ReturnsFailed_WhenTheMethodItselfThrewALockTimeout()
        {
            _context.Object.Items[ConcurrencyControl.MethodInvokedItem] = Stopwatch.GetTimestamp();

            var context = _context.GetPerformedContext(exception: Wrap(new DistributedLockTimeoutException("resource")));

            Assert.Equal(JobOutcome.Failed, JobOutcomes.Classify(context));
        }

        [Fact]
        public void GetMethodInvokedTimestamp_ReturnsNull_UntilThePerformerCallsTheMethod()
        {
            Assert.Null(JobOutcomes.GetMethodInvokedTimestamp(_context.Object));

            _context.Object.Items[ConcurrencyControl.MethodInvokedItem] = 42L;

            Assert.Equal(42L, JobOutcomes.GetMethodInvokedTimestamp(_context.Object));
        }

        private static JobPerformanceException Wrap(Exception exception)
        {
            return new JobPerformanceException("An exception occurred during performance of the job.", exception);
        }
    }
}
