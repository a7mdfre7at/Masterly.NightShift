using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class RetryInProcessAttributeFacts
    {
        private static int _calls;
        private readonly PerformContextMock _context = new PerformContextMock();

        public RetryInProcessAttributeFacts()
        {
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
            _calls = 0;
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenAttemptsIsNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryInProcessAttribute(-1));
        }

        [Fact, StaticLock]
        public void FailingJob_IsRetriedInProcess_UntilItSucceeds()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SucceedsOnThirdCall());

            var result = Perform();

            Assert.Equal("ok", result);
            Assert.Equal(3, _calls);
        }

        [Fact, StaticLock]
        public void FailingAsyncJob_IsRetriedInProcess()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.AsyncSucceedsOnSecondCall());

            var result = Perform();

            Assert.Equal(2, result);
            Assert.Equal(2, _calls);
        }

        [Fact, StaticLock]
        public void FailingJob_ThrowsAfterAttemptsAreExhausted()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.AlwaysFails());

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal(3, _calls);
        }

        [Fact, StaticLock]
        public void OnlyListedExceptionTypes_AreRetried()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.FailsWithUnlistedException());

            Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.Equal(1, _calls);
        }

        [Fact, StaticLock]
        public void Cancellation_IsNeverRetried()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Cancels());

            Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.Equal(1, _calls);
        }

        private object Perform()
        {
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);
            return performer.Perform(_context.Object);
        }

        public class Jobs
        {
            [RetryInProcess(5, DelayMilliseconds = 1)]
            public string SucceedsOnThirdCall()
            {
                if (++_calls < 3) throw new IOException();
                return "ok";
            }

            [RetryInProcess(5, DelayMilliseconds = 1)]
            public async Task<int> AsyncSucceedsOnSecondCall()
            {
                await Task.Yield();
                if (++_calls < 2) throw new IOException();
                return _calls;
            }

            [RetryInProcess(2, DelayMilliseconds = 1)]
            public void AlwaysFails()
            {
                _calls++;
                throw new InvalidOperationException();
            }

            [RetryInProcess(3, DelayMilliseconds = 1, ExceptionTypes = new[] { typeof(IOException) })]
            public void FailsWithUnlistedException()
            {
                _calls++;
                throw new InvalidOperationException();
            }

            [RetryInProcess(3, DelayMilliseconds = 1)]
            public void Cancels()
            {
                _calls++;
                throw new OperationCanceledException();
            }
        }
    }
}
