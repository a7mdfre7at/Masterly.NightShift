using System;
using System.Collections.Generic;
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

        [Fact]
        public void DelayMilliseconds_RejectsNegativeValues_AndValuesAboveAnHour()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryInProcessAttribute(1) { DelayMilliseconds = -1 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryInProcessAttribute(1) { DelayMilliseconds = RetryInProcessAttribute.MaxDelayMilliseconds + 1 });
            Assert.Equal(0, new RetryInProcessAttribute(1) { DelayMilliseconds = 0 }.DelayMilliseconds);
            Assert.Equal(3600000, new RetryInProcessAttribute(1) { DelayMilliseconds = RetryInProcessAttribute.MaxDelayMilliseconds }.DelayMilliseconds);
        }

        [Theory]
        [InlineData(0, 200)]
        [InlineData(1, 400)]
        [InlineData(4, 1000)]
        public void GetDelay_GrowsLinearly(int attempt, int expectedMilliseconds)
        {
            var attribute = new RetryInProcessAttribute(10);

            Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), attribute.GetDelay(attempt));
        }

        [Fact]
        public void GetDelay_NeverExceedsWhatAWaitAccepts()
        {
            // An hour times a million attempts would overflow the wait, which would then throw instead of the job's exception
            var attribute = new RetryInProcessAttribute(Int32.MaxValue) { DelayMilliseconds = RetryInProcessAttribute.MaxDelayMilliseconds };

            Assert.Equal(TimeSpan.FromMilliseconds(Int32.MaxValue), attribute.GetDelay(1000000));
            Assert.Equal(TimeSpan.FromMilliseconds(Int32.MaxValue), attribute.GetDelay(Int32.MaxValue));
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

        [Fact, StaticLock]
        public void RetryLaterException_IsNotRetriedInProcess_ByDefault()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.AsksToRetryLater());

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.IsType<RetryLaterException>(exception.InnerException);
            Assert.Equal(1, _calls);
        }

        [Fact, StaticLock]
        public void ConcurrencyLimitException_IsNotRetriedInProcess_ByDefault()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.HitsAConcurrencyLimit());

            Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.Equal(1, _calls);
        }

        [Fact, StaticLock]
        public void RetryLaterException_IsRetriedInProcess_WhenListed()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.AsksToRetryLaterWhenListed());

            Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.Equal(3, _calls);
        }

        [Fact, StaticLock]
        public void SynchronousJob_RunsEveryAttempt_OnTheWorkerThread()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.RecordsItsThread());
            Threads.Clear();
            var workerThreadId = 0;
            Exception failure = null;

            var worker = new Thread(() =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                failure = Record.Exception(() => Perform());
            });
            worker.Start();
            worker.Join();

            Assert.IsType<JobPerformanceException>(failure);
            Assert.Equal(new[] { workerThreadId, workerThreadId, workerThreadId }, Threads);
        }

        [Fact, StaticLock]
        public void SynchronousJob_StopsWaitingForTheNextAttempt_WhenTheServerShutsDown()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.FailsWithALongDelay());
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));

            Assert.Throws<OperationCanceledException>(() => Perform());

            Assert.Equal(1, _calls);
        }

        private object Perform()
        {
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);
            return performer.Perform(_context.Object);
        }

        private static readonly List<int> Threads = new List<int>();

        public class Jobs
        {
            [RetryInProcess(2, DelayMilliseconds = 20)]
            public void RecordsItsThread()
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                throw new IOException();
            }

            [RetryInProcess(2, DelayMilliseconds = 60000)]
            public void FailsWithALongDelay()
            {
                _calls++;
                throw new IOException();
            }

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

            [RetryInProcess(3, DelayMilliseconds = 1)]
            public void AsksToRetryLater()
            {
                _calls++;
                throw new RetryLaterException(TimeSpan.FromHours(1), "partner answered 429");
            }

            [RetryInProcess(3, DelayMilliseconds = 1)]
            public void HitsAConcurrencyLimit()
            {
                _calls++;
                throw new ConcurrencyLimitException("resource", "busy");
            }

            [RetryInProcess(2, DelayMilliseconds = 1, ExceptionTypes = new[] { typeof(RetryLaterException) })]
            public void AsksToRetryLaterWhenListed()
            {
                _calls++;
                throw new RetryLaterException(TimeSpan.FromHours(1), "partner answered 429");
            }
        }
    }
}
