using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Server
{
    /// <summary>
    /// Asynchronous interceptors around asynchronous job methods proceed after an <c>await</c>, when the outer
    /// <see cref="JobInvocation.Proceed"/> call has already returned. They must still continue with the next step
    /// of the chain, not start it again.
    /// </summary>
    public class JobInvocationAsyncFacts : IDisposable
    {
        private static readonly List<string> Log = new List<string>();
        private static int _calls;
        private static int _entries;
        private readonly PerformContextMock _context = new PerformContextMock();

        public JobInvocationAsyncFacts()
        {
            GlobalJobInterceptors.Clear();
            Log.Clear();
            _calls = 0;
            _entries = 0;
        }

        public void Dispose()
        {
            GlobalJobInterceptors.Clear();
        }

        [Fact, StaticLock]
        public void RetryInProcess_OnAnAlwaysFailingAsyncMethod_StopsAfterItsAttempts()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.AlwaysFailsAsync());

            var exception = Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal(3, Volatile.Read(ref _calls));
        }

        [Fact, StaticLock]
        public void RetryInProcess_OnAnAsyncMethod_ThatSucceedsOnTheLastAttempt_ReturnsItsResult()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SucceedsOnThirdCallAsync());

            var result = Perform();

            Assert.Equal(3, result);
            Assert.Equal(3, Volatile.Read(ref _calls));
        }

        [Fact, StaticLock]
        public void RetryInProcess_OnASyncMethod_StillStopsAfterItsAttempts()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.AlwaysFailsSync());

            Assert.Throws<JobPerformanceException>(() => Perform());

            Assert.Equal(3, Volatile.Read(ref _calls));
        }

        [Fact, StaticLock]
        public void Interceptor_ThatAwaitsBeforeProceeding_RunsOnce_AndTheMethodRunsOnce()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.AwaitedAsync());

            var result = Perform();

            Assert.Equal("done", result);
            Assert.Equal(1, Volatile.Read(ref _entries));
            Assert.Equal(1, Volatile.Read(ref _calls));
        }

        [Fact, StaticLock]
        public void NestedInterceptors_ThatAwaitBeforeProceeding_RunInOrder()
        {
            GlobalJobInterceptors.Add(new AwaitingLoggingInterceptor("global"));
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.LoggedAsync());

            Perform();

            Assert.Equal(
                new[] { "global:before", "first:before", "second:before", "method", "second:after", "first:after", "global:after" },
                Log);
        }

        [Fact, StaticLock]
        public void ArgumentsReplacedAfterAnAwait_AreSeenByInnerInterceptors_AndTheMethod()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.EchoAsync("original"));

            var result = Perform();

            Assert.Equal("replaced", result);
            Assert.Equal(1, Volatile.Read(ref _entries));
            Assert.Equal(new[] { "inner saw replaced" }, Log);
        }

        [Fact, StaticLock]
        public void SynchronousInterceptor_CanStillProceedSeveralTimes()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.CountedSync());

            var result = Perform();

            Assert.Equal(2, result);
            Assert.Equal(2, Volatile.Read(ref _calls));
        }

        private object Perform()
        {
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);
            return performer.Perform(_context.Object);
        }

        public class Jobs
        {
            [RetryInProcess(2, DelayMilliseconds = 0)]
            public async Task AlwaysFailsAsync()
            {
                var calls = Interlocked.Increment(ref _calls);
                await Task.Yield();

                // Before the fix the chain restarted on every retry; stop the test from running forever
                if (calls > 10) return;
                throw new InvalidOperationException("boom");
            }

            [RetryInProcess(2, DelayMilliseconds = 0)]
            public async Task<int> SucceedsOnThirdCallAsync()
            {
                var calls = Interlocked.Increment(ref _calls);
                await Task.Yield();

                if (calls < 3) throw new InvalidOperationException("boom");
                return calls;
            }

            [RetryInProcess(2, DelayMilliseconds = 0)]
            public void AlwaysFailsSync()
            {
                Interlocked.Increment(ref _calls);
                throw new InvalidOperationException("boom");
            }

            [AwaitFirst]
            public async Task<string> AwaitedAsync()
            {
                Interlocked.Increment(ref _calls);
                await Task.Yield();
                return "done";
            }

            [AwaitingLogging("first", Order = 1), AwaitingLogging("second", Order = 2)]
            public async Task LoggedAsync()
            {
                await Task.Yield();
                Log.Add("method");
            }

            [ReplaceAfterAwait("replaced", Order = 1), LogFirstArgument(Order = 2)]
            public async Task<string> EchoAsync(string value)
            {
                await Task.Yield();
                return value;
            }

            [ProceedTwice]
            public int CountedSync()
            {
                Interlocked.Increment(ref _calls);
                return 1;
            }
        }

        private sealed class AwaitFirstAttribute : AsyncJobInterceptorAttribute
        {
            protected override async Task<object> InterceptAsync(JobInvocation invocation)
            {
                // Before the fix the interceptor entered itself again and again; stop the test from running forever
                if (Interlocked.Increment(ref _entries) > 10) throw new InvalidOperationException("The interceptor re-entered itself.");

                await Task.Delay(10);
                return await invocation.ProceedAsync();
            }
        }

        private sealed class AwaitingLoggingAttribute : AsyncJobInterceptorAttribute
        {
            private readonly AwaitingLoggingInterceptor _interceptor;

            public AwaitingLoggingAttribute(string name)
            {
                _interceptor = new AwaitingLoggingInterceptor(name);
            }

            protected override Task<object> InterceptAsync(JobInvocation invocation)
            {
                return _interceptor.InterceptAsync(invocation);
            }
        }

        private sealed class AwaitingLoggingInterceptor : IJobInterceptor
        {
            private readonly string _name;

            public AwaitingLoggingInterceptor(string name)
            {
                _name = name;
            }

            public object Intercept(JobInvocation invocation)
            {
                return InterceptAsync(invocation);
            }

            public async Task<object> InterceptAsync(JobInvocation invocation)
            {
                if (Log.Count > 20) throw new InvalidOperationException("The chain restarted.");

                await Task.Yield();
                Log.Add(_name + ":before");
                var result = await invocation.ProceedAsync();
                Log.Add(_name + ":after");
                return result;
            }
        }

        private sealed class ReplaceAfterAwaitAttribute : AsyncJobInterceptorAttribute
        {
            private readonly string _value;

            public ReplaceAfterAwaitAttribute(string value)
            {
                _value = value;
            }

            protected override async Task<object> InterceptAsync(JobInvocation invocation)
            {
                if (Interlocked.Increment(ref _entries) > 10) throw new InvalidOperationException("The interceptor re-entered itself.");

                await Task.Yield();
                invocation.Arguments[0] = _value;
                return await invocation.ProceedAsync();
            }
        }

        private sealed class LogFirstArgumentAttribute : AsyncJobInterceptorAttribute
        {
            protected override async Task<object> InterceptAsync(JobInvocation invocation)
            {
                if (Log.Count > 10) throw new InvalidOperationException("The chain restarted.");

                Log.Add("inner saw " + invocation.Arguments[0]);
                return await invocation.ProceedAsync();
            }
        }

        private sealed class ProceedTwiceAttribute : JobInterceptorAttribute
        {
            public override object Intercept(JobInvocation invocation)
            {
                return (int)invocation.Proceed() + (int)invocation.Proceed();
            }
        }
    }
}
