using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class JobTimeoutAttributeFacts
{
    private static int _attempts;
    private readonly PerformContextMock _context = new();

    public JobTimeoutAttributeFacts()
    {
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
        ResetAttempts();
    }

    private static void ResetAttempts()
    {
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
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => new JobTimeoutAttribute(60 * 24 * 3600));

        Assert.Contains("2147483", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobTimeoutAttribute(JobTimeoutAttribute.MaxTimeoutInSeconds + 1));
    }

    [Fact, StaticLock]
    public void TheLongestTimeout_Works()
    {
        Assert.Equal(2147483, JobTimeoutAttribute.MaxTimeoutInSeconds);
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.LongestTimeout(default));

        object result = Perform();

        Assert.Equal("done", result);
    }

    [Fact, StaticLock]
    public void SlowJobs_WithoutCancellationParameter_Succeed_WhenTheOverrunCannotBeRecorded()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SlowWithoutToken());
        _context.Connection
            .Setup(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage is down"));

        object result = Perform();

        Assert.Equal("late", result);
    }

    [Fact, StaticLock]
    public void FastJobs_CompleteNormally()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Fast(default));

        object result = Perform();

        Assert.Equal("done", result);
    }

    [Fact, StaticLock]
    public void SlowJobs_WithCancellationToken_FailWithTimeoutException()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Slow(default));

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact, StaticLock]
    public void SlowJobs_WithJobCancellationToken_FailWithTimeoutException()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SlowWithJobToken(null));

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact, StaticLock]
    public void SlowJobs_FailWithoutRetries_WhenOnTimeoutIsFail()
    {
        _context.BackgroundJob.Job = Job.FromExpression<OnTimeoutJobs>(x => x.SlowFail(default));

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        TerminateJobException terminate = Assert.IsType<TerminateJobException>(exception.InnerException);
        Assert.Equal(TerminateJobOutcome.Failed, terminate.Outcome);
        Assert.IsType<TimeoutException>(terminate.InnerException);
        Assert.StartsWith("Timed out after 1 seconds", terminate.Message, StringComparison.Ordinal);
    }

    [Fact, StaticLock]
    public void SlowJobs_AreCanceled_WhenOnTimeoutIsCancel()
    {
        _context.BackgroundJob.Job = Job.FromExpression<OnTimeoutJobs>(x => x.SlowCancel(default));

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        TerminateJobException terminate = Assert.IsType<TerminateJobException>(exception.InnerException);
        Assert.Equal(TerminateJobOutcome.Canceled, terminate.Outcome);
        Assert.IsType<TimeoutException>(terminate.InnerException);
    }

    [Fact, StaticLock]
    public void FastJobs_CompleteNormally_WhateverOnTimeoutSays()
    {
        _context.BackgroundJob.Job = Job.FromExpression<OnTimeoutJobs>(x => x.FastCancel(default));

        Assert.Equal("done", Perform());
    }

    [Fact]
    public void OnTimeout_DefaultsToRetry()
    {
        Assert.Equal(JobTimeoutAction.Retry, new JobTimeoutAttribute(5).OnTimeout);
    }

    [Fact]
    public void OnTimeout_ThrowsAnException_WhenTheValueIsUnknown()
    {
        JobTimeoutAttribute attribute = new(5);

        Assert.Throws<ArgumentOutOfRangeException>(() => attribute.OnTimeout = (JobTimeoutAction)42);
    }

    [Fact, StaticLock]
    public void SlowJobs_WithoutCancellationParameter_Succeed_ButRecordTheOverrun()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.SlowWithoutToken());

        object result = Perform();

        Assert.Equal("late", result);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.IsNotNull<string>()));
    }

    [Fact, StaticLock]
    public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithJobCancellationToken()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithJobToken(null));

        object result = Perform();

        Assert.Equal("second attempt", result);
        Assert.Equal(2, _attempts);
    }

    [Fact, StaticLock]
    public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithCancellationToken()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithToken(default));

        object result = Perform();

        Assert.Equal("second attempt", result);
        Assert.Equal(2, _attempts);
    }

    [Fact, StaticLock]
    public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithJobCancellationToken_Async()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithJobTokenAsync(null));

        object result = Perform();

        Assert.Equal("second attempt", result);
        Assert.Equal(2, _attempts);
    }

    [Fact, StaticLock]
    public void InProcessRetry_AfterATimeout_GetsAFreshTimeout_WithCancellationToken_Async()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.TimesOutOnceWithTokenAsync(default));

        object result = Perform();

        Assert.Equal("second attempt", result);
        Assert.Equal(2, _attempts);
    }

    [Fact, StaticLock]
    public void Perform_RestoresTheOriginalTokens_AfterTheCall()
    {
        IJobCancellationToken original = new Mock<IJobCancellationToken>().Object;
        object[] arguments = [original, CancellationToken.None];
        Job job = Job.FromExpression<Jobs>(x => x.TakesBothTokens(null, default));
        _context.BackgroundJob.Job = job;
        ReflectionJobInvoker invoker = new(job.Method);
        JobInvocation invocation = new(_context.Object, invoker, new Jobs(), arguments, [new JobTimeoutAttribute(5)]);

        invocation.Proceed();

        Assert.Same(original, arguments[0]);
        Assert.Equal(CancellationToken.None, arguments[1]);
    }

    [Fact, StaticLock]
    public void NestedTimeouts_TheShorterOuterOne_ReachesTheCancellationToken_AndNamesItself()
    {
        _context.BackgroundJob.Job = Job.FromExpression<ShortClassTimeout>(x => x.WaitForToken(default));

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        TimeoutException timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal("Timed out after 1 seconds (JobTimeout)", timeout.Message);
    }

    [Fact, StaticLock]
    public void NestedTimeouts_TheShorterOuterOne_ReachesTheShutdownTokenOfAJobCancellationToken()
    {
        _context.BackgroundJob.Job = Job.FromExpression<ShortClassTimeout>(x => x.WaitForJobToken(null));

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        TimeoutException timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal("Timed out after 1 seconds (JobTimeout)", timeout.Message);
    }

    [Fact, StaticLock]
    public void NestedTimeouts_TheShorterInnerOne_Wins_AndNamesItself()
    {
        _context.BackgroundJob.Job = Job.FromExpression<LongClassTimeout>(x => x.WaitForToken(default));

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        TimeoutException timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal("Timed out after 1 seconds (JobTimeout)", timeout.Message);
    }

    [Fact, StaticLock]
    public void NestedTimeouts_WithoutCancellationParameter_RecordTheShorterTimeout()
    {
        // The outer two seconds are exceeded too, but the inner second is what the parameter keeps
        _context.BackgroundJob.Job = Job.FromExpression<TwoSecondClassTimeout>(x => x.SleepWithoutToken());

        object result = Perform();

        Assert.Equal("late", result);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.Is<string>(v => v.Contains("00:00:01"))), Times.Once);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobTimeoutAttribute.TimeoutExceededParameter, It.Is<string>(v => v.Contains("00:00:02"))), Times.Never);
    }

    [Fact, StaticLock]
    public void AShutdownDuringTheTimeout_IsNotReportedAsATimeout()
    {
        // The server stops first; the job then runs past its timeout too before it gives up
        using CancellationTokenSource shutdown = new(TimeSpan.FromMilliseconds(200));
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(shutdown.Token);
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.StopsLateAfterShutdown(default));

        OperationCanceledException exception = Assert.ThrowsAny<OperationCanceledException>(() => Perform());

        Assert.True(shutdown.IsCancellationRequested);
        Assert.IsNotType<TimeoutException>(exception.InnerException);
    }

    [Fact, StaticLock]
    public void Perform_RestoresTheOriginalTokens_WhenTheMethodThrows()
    {
        IJobCancellationToken original = new Mock<IJobCancellationToken>().Object;
        object[] arguments = [original, CancellationToken.None];
        Job job = Job.FromExpression<Jobs>(x => x.ThrowsWithBothTokens(null, default));
        _context.BackgroundJob.Job = job;
        ReflectionJobInvoker invoker = new(job.Method);
        JobInvocation invocation = new(_context.Object, invoker, new Jobs(), arguments, [new JobTimeoutAttribute(5)]);

        Assert.ThrowsAny<Exception>(invocation.Proceed);

        Assert.Same(original, arguments[0]);
        Assert.Equal(CancellationToken.None, arguments[1]);
    }

    [Fact, StaticLock]
    public void OnTimeoutFail_FailsTheJob_WithoutARetry()
    {
        _context.BackgroundJob.Job = Job.FromExpression<OnTimeoutJobs>(x => x.SlowFail(default));
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        IState state = Elect(new FailedState(exception.InnerException));

        FailedState failed = Assert.IsType<FailedState>(state);
        Assert.IsType<TerminateJobException>(failed.Exception);
    }

    [Fact, StaticLock]
    public void OnTimeoutCancel_DeletesTheJob_AsCanceled()
    {
        _context.BackgroundJob.Job = Job.FromExpression<OnTimeoutJobs>(x => x.SlowCancel(default));
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        IState state = Elect(new FailedState(exception.InnerException));

        DeletedState deleted = Assert.IsType<DeletedState>(state);
        Assert.Equal("Canceled: Timed out after 1 seconds (JobTimeout)", deleted.Reason);
    }

    [Fact, StaticLock]
    public void OnTimeoutRetry_RetriesTheJob()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Slow(default));
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        IState state = Elect(new FailedState(exception.InnerException));

        Assert.IsType<ScheduledState>(state);
        Assert.StartsWith("Retry attempt 1 of", state.Reason, StringComparison.Ordinal);
    }

    // The failed state goes through the default filters that decide the outcome, in their order
    private static IState Elect(FailedState failed)
    {
        ElectStateContextMock context = new();
        context.ApplyContext.NewStateObject = failed;

        new TerminateJobFilter().OnStateElection(context.Object);
        new AutomaticRetryAttribute { LogEvents = false }.OnStateElection(context.Object);

        return context.Object.CandidateState;
    }

    [Fact]
    public void IsAborted_SeesThroughTheTokensWrappedByNestedTimeouts()
    {
        string serverId = $"job-timeout-facts-{Guid.NewGuid()}";
        Mock<IStorageConnection> connection = new();
        connection.Setup(x => x.GetStateData("JobId")).Returns(default(StateData));
        ServerJobCancellationToken.AddServer(serverId);

        try
        {
            using ServerJobCancellationToken serverToken = new(connection.Object, "JobId", serverId, "worker", CancellationToken.None);
            JobTimeoutAttribute.LinkedJobCancellationToken wrapped = new(
                new JobTimeoutAttribute.LinkedJobCancellationToken(serverToken, serverToken.ShutdownToken),
                serverToken.ShutdownToken);

            Assert.False(wrapped.IsAborted());

            // The job's state is gone: the watcher aborts the job
            ServerJobCancellationToken.CheckAllCancellationTokens(serverId, connection.Object, CancellationToken.None);

            Assert.True(wrapped.IsAborted());
        }
        finally
        {
            ServerJobCancellationToken.RemoveServer(serverId);
        }
    }

    private object Perform()
    {
        CoreBackgroundJobPerformer performer = new(new JobActivator(), null);
        return performer.Perform(_context.Object);
    }

    [JobTimeout(1)]
    public class ShortClassTimeout
    {
        [JobTimeout(30)]
        public string WaitForToken(CancellationToken token)
        {
            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
            {
                return "not canceled";
            }

            token.ThrowIfCancellationRequested();
            return "never";
        }

        [JobTimeout(30)]
        public string WaitForJobToken(IJobCancellationToken token)
        {
            if (!token.ShutdownToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
            {
                return "not canceled";
            }

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
            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
            {
                return "not canceled";
            }

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

    public class OnTimeoutJobs
    {
        [JobTimeout(1, OnTimeout = JobTimeoutAction.Fail)]
        public async Task<string> SlowFail(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return "never";
        }

        [JobTimeout(1, OnTimeout = JobTimeoutAction.Cancel)]
        public async Task<string> SlowCancel(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return "never";
        }

        [JobTimeout(5, OnTimeout = JobTimeoutAction.Cancel)]
        public string FastCancel(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return "done";
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
            for (int i = 0; i < 300; i++)
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

        public string ThrowsWithBothTokens(IJobCancellationToken jobToken, CancellationToken token)
        {
            throw new InvalidOperationException("boom");
        }

        [JobTimeout(1)]
        public string StopsLateAfterShutdown(CancellationToken token)
        {
            // Canceled by the shutdown, then past the timeout as well before it throws
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            Thread.Sleep(1200);
            token.ThrowIfCancellationRequested();
            return "never";
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
