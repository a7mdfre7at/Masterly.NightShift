using System;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class JobTimeoutAttributeFacts
    {
        private readonly PerformContextMock _context = new PerformContextMock();

        public JobTimeoutAttributeFacts()
        {
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenTimeoutIsNotPositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new JobTimeoutAttribute(0));
        }

        [Fact, StaticLock]
        public void FastJobs_CompleteNormally()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Fast(default));

            var result = Perform();

            Assert.Equal("done", result);
        }

        [Fact, StaticLock]
        public void SlowJobs_WithCancellationToken_FailWithTimeoutException()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Slow(default));

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.IsType<TimeoutException>(exception.InnerException);
        }

        [Fact, StaticLock]
        public void SlowJobs_WithJobCancellationToken_FailWithTimeoutException()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SlowWithJobToken(null));

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.IsType<TimeoutException>(exception.InnerException);
        }

        [Fact, StaticLock]
        public void SlowJobs_WithoutCancellationParameter_Succeed_ButRecordTheOverrun()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SlowWithoutToken());

            var result = Perform();

            Assert.Equal("late", result);
            _context.Connection.Verify(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.IsNotNull<string>()));
        }

        private object Perform()
        {
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);
            return performer.Perform(_context.Object);
        }

        public class Jobs
        {
            [JobTimeout(5)]
            public string Fast(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return "done";
            }

            [JobTimeout(1)]
            public async Task<string> Slow(CancellationToken token)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return "never";
            }

            [JobTimeout(1)]
            public string SlowWithJobToken(IJobCancellationToken token)
            {
                for (var i = 0; i < 300; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Thread.Sleep(100);
                }

                return "never";
            }

            [JobTimeout(1)]
            public string SlowWithoutToken()
            {
                Thread.Sleep(1200);
                return "late";
            }
        }
    }
}
