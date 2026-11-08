using System;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class JobTimeoutAttributeFacts
    {
        private static int _attempts;
        private readonly PerformContextMock _context = new PerformContextMock();

        public JobTimeoutAttributeFacts()
        {
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
            _attempts = 0;
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenTimeoutIsNotPositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new JobTimeoutAttribute(0));
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenTimeoutIsLongerThanATimerAccepts()
        {
            // 60 days: CancelAfter would throw on every run, before the job method is called
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new JobTimeoutAttribute(60 * 24 * 3600));

            Assert.Contains("2147483", exception.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentOutOfRangeException>(() => new JobTimeoutAttribute(JobTimeoutAttribute.MaxTimeoutInSeconds + 1));
        }

        [Fact, StaticLock]
        public void TheLongestTimeout_Works()
        {
            Assert.Equal(2147483, JobTimeoutAttribute.MaxTimeoutInSeconds);
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.LongestTimeout(default));

            var result = Perform();

            Assert.Equal("done", result);
        }

        [Fact, StaticLock]
        public void SlowJobs_WithoutCancellationParameter_Succeed_WhenTheOverrunCannotBeRecorded()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SlowWithoutToken());
            _context.Connection
                .Setup(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.IsAny<string>()))
                .Throws(new InvalidOperationException("storage is down"));

            var result = Perform();

            Assert.Equal("late", result);
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

        [Fact, StaticLock]
        public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithJobCancellationToken()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithJobToken(null));

            var result = Perform();

            Assert.Equal("second attempt", result);
            Assert.Equal(2, _attempts);
        }

        [Fact, StaticLock]
        public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithCancellationToken()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithToken(default));

            var result = Perform();

            Assert.Equal("second attempt", result);
            Assert.Equal(2, _attempts);
        }

        [Fact, StaticLock]
        public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithJobCancellationToken_Async()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithJobTokenAsync(null));

            var result = Perform();

            Assert.Equal("second attempt", result);
            Assert.Equal(2, _attempts);
        }

        [Fact, StaticLock]
        public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithCancellationToken_Async()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithTokenAsync(default));

            var result = Perform();

            Assert.Equal("second attempt", result);
            Assert.Equal(2, _attempts);
        }

        [Fact, StaticLock]
        public void Perform_RestoresTheOriginalTokens_AfterTheCall()
        {
            var original = new Mock<IJobCancellationToken>().Object;
            var arguments = new object[] { original, CancellationToken.None };
            var job = Job.FromExpression<Jobs>(x => x.TakesBothTokens(null, default));
            _context.BackgroundJob.Job = job;
            var invoker = new ReflectionJobInvoker(job.Method);
            var invocation = new JobInvocation(_context.Object, invoker, new Jobs(), arguments, new IJobInterceptor[] { new JobTimeoutAttribute(5) });

            invocation.Proceed();

            Assert.Same(original, arguments[0]);
            Assert.Equal(CancellationToken.None, arguments[1]);
        }

        [Fact, StaticLock]
        public void NestedTimeouts_TheShorterOuterOne_ReachesTheCancellationToken_AndNamesItself()
        {
            _context.BackgroundJob.Job = Job.FromExpression<ShortClassTimeout>(x => x.WaitForToken(default));

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.Equal("Timed out after 1 seconds (JobTimeout)", timeout.Message);
        }

        [Fact, StaticLock]
        public void NestedTimeouts_TheShorterOuterOne_ReachesTheShutdownTokenOfAJobCancellationToken()
        {
            _context.BackgroundJob.Job = Job.FromExpression<ShortClassTimeout>(x => x.WaitForJobToken(null));

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.Equal("Timed out after 1 seconds (JobTimeout)", timeout.Message);
        }

        [Fact, StaticLock]
        public void NestedTimeouts_TheShorterInnerOne_Wins_AndNamesItself()
        {
            _context.BackgroundJob.Job = Job.FromExpression<LongClassTimeout>(x => x.WaitForToken(default));

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.Equal("Timed out after 1 seconds (JobTimeout)", timeout.Message);
        }

        [Fact, StaticLock]
        public void NestedTimeouts_WithoutCancellationParameter_RecordTheShorterTimeout()
        {
            // The outer two seconds are exceeded too, but the inner second is what the parameter keeps
            _context.BackgroundJob.Job = Job.FromExpression<TwoSecondClassTimeout>(x => x.SleepWithoutToken());

            var result = Perform();

            Assert.Equal("late", result);
            _context.Connection.Verify(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.Is<string>(v => v.Contains("00:00:01"))), Times.Once);
            _context.Connection.Verify(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.Is<string>(v => v.Contains("00:00:02"))), Times.Never);
        }

        [Fact]
        public void IsAborted_SeesThroughTheTokensWrappedByNestedTimeouts()
        {
            var serverId = $"job-timeout-facts-{Guid.NewGuid()}";
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetStateData("JobId")).Returns((StateData)null);
            ServerJobCancellationToken.AddServer(serverId);

            try
            {
                using (var serverToken = new ServerJobCancellationToken(connection.Object, "JobId", serverId, "worker", CancellationToken.None))
                {
                    var wrapped = new JobTimeoutAttribute.LinkedJobCancellationToken(
                        new JobTimeoutAttribute.LinkedJobCancellationToken(serverToken, serverToken.ShutdownToken),
                        serverToken.ShutdownToken);

                    Assert.False(wrapped.IsAborted());

                    // The job's state is gone: the watcher aborts the job
                    ServerJobCancellationToken.CheckAllCancellationTokens(serverId, connection.Object, CancellationToken.None);

                    Assert.True(wrapped.IsAborted());
                }
            }
            finally
            {
                ServerJobCancellationToken.RemoveServer(serverId);
            }
        }

        private object Perform()
        {
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);
            return performer.Perform(_context.Object);
        }

        [JobTimeout(1)]
        public class ShortClassTimeout
        {
            [JobTimeout(30)]
            public string WaitForToken(CancellationToken token)
            {
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) return "not canceled";

                token.ThrowIfCancellationRequested();
                return "never";
            }

            [JobTimeout(30)]
            public string WaitForJobToken(IJobCancellationToken token)
            {
                if (!token.ShutdownToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) return "not canceled";

                token.ThrowIfCancellationRequested();
                return "never";
            }
        }

        [JobTimeout(30)]
        public class LongClassTimeout
        {
            [JobTimeout(1)]
            public string WaitForToken(CancellationToken token)
            {
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) return "not canceled";

                token.ThrowIfCancellationRequested();
                return "never";
            }
        }

        [JobTimeout(2)]
        public class TwoSecondClassTimeout
        {
            [JobTimeout(1)]
            public string SleepWithoutToken()
            {
                Thread.Sleep(2200);
                return "late";
            }
        }

        public class Jobs
        {
            [JobTimeout(5)]
            public string Fast(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return "done";
            }

            [JobTimeout(JobTimeoutAttribute.MaxTimeoutInSeconds)]
            public string LongestTimeout(CancellationToken token)
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

            [RetryInProcess(1, DelayMilliseconds = 1, Order = 0), JobTimeout(1, Order = 1)]
            public string TimesOutOnceWithJobToken(IJobCancellationToken token)
            {
                if (Interlocked.Increment(ref _attempts) == 1)
                {
                    WaitForCancellation(token.ShutdownToken);
                    token.ThrowIfCancellationRequested();
                }

                token.ThrowIfCancellationRequested();
                return "second attempt";
            }

            [RetryInProcess(1, DelayMilliseconds = 1, Order = 0), JobTimeout(1, Order = 1)]
            public string TimesOutOnceWithToken(CancellationToken token)
            {
                if (Interlocked.Increment(ref _attempts) == 1)
                {
                    WaitForCancellation(token);
                }

                token.ThrowIfCancellationRequested();
                return "second attempt";
            }

            [RetryInProcess(1, DelayMilliseconds = 1, Order = 0), JobTimeout(1, Order = 1)]
            public async Task<string> TimesOutOnceWithJobTokenAsync(IJobCancellationToken token)
            {
                if (Interlocked.Increment(ref _attempts) == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token.ShutdownToken);
                }

                await Task.Yield();
                token.ThrowIfCancellationRequested();
                return "second attempt";
            }

            [RetryInProcess(1, DelayMilliseconds = 1, Order = 0), JobTimeout(1, Order = 1)]
            public async Task<string> TimesOutOnceWithTokenAsync(CancellationToken token)
            {
                if (Interlocked.Increment(ref _attempts) == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }

                await Task.Delay(1, token);
                return "second attempt";
            }

            public string TakesBothTokens(IJobCancellationToken jobToken, CancellationToken token)
            {
                return "done";
            }

            private static void WaitForCancellation(CancellationToken token)
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
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
