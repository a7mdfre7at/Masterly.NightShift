using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Terminating a job, skipping late runs, exception handlers, the run's scheduled time and waiting for a job,
/// through a real server and real storage.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class JobExecutionControlFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<DateTime?> ScheduledForSeen = new();
    private static int _runs;

    [Fact, CleanDatabase]
    public async Task TerminateJobException_DeletesASkippedJob_WithoutRetrying()
    {
        SqlServerStorage storage = CreateStorage();
        _runs = 0;
        string jobId = new BackgroundJobClient(storage).Enqueue(() => SkipItself());

        JobStateInfo state = await RunUntilCompleted(storage, jobId);

        Assert.Equal(DeletedState.StateName, state.Name);
        Assert.Equal("Skipped: The invoice is already paid", state.Reason);
        Assert.Equal(1, _runs);
        Assert.Null(GetParameter(storage, jobId, "RetryCount"));
    }

    [Fact, CleanDatabase]
    public async Task TerminateJobException_FailsAJob_WithoutRetrying()
    {
        SqlServerStorage storage = CreateStorage();
        _runs = 0;
        string jobId = new BackgroundJobClient(storage).Enqueue(() => FailForGood());

        JobStateInfo state = await RunUntilCompleted(storage, jobId);

        Assert.Equal(FailedState.StateName, state.Name);
        Assert.Equal(typeof(TerminateJobException).FullName, state.Data["ExceptionType"]);
        Assert.Equal(1, _runs);
        Assert.Null(GetParameter(storage, jobId, "RetryCount"));
    }

    [Fact, CleanDatabase]
    public async Task SkipIfLate_SkipsARunThatStartsTooLate_AndARequeueRunsIt()
    {
        SqlServerStorage storage = CreateStorage();
        _runs = 0;
        BackgroundJobClient client = new(storage);
        string jobId = client.Schedule(() => DueHourly(), DateTimeOffset.UtcNow.AddHours(-2));

        JobStateInfo skipped = await RunUntilCompleted(storage, jobId);

        Assert.Equal(DeletedState.StateName, skipped.Name);
        Assert.StartsWith("Skipped: started 2 h late", skipped.Reason, StringComparison.Ordinal);
        Assert.Equal(0, _runs);
        Assert.Equal("true", GetParameter(storage, jobId, SkipIfLateAttribute.CheckedParameter));

        Assert.True(client.Requeue(jobId));
        JobStateInfo requeued = await RunUntilCompleted(storage, jobId);

        Assert.Equal(SucceededState.StateName, requeued.Name);
        Assert.Equal(1, _runs);
    }

    [Fact, CleanDatabase]
    public async Task SkipIfLate_LetsARunOnTimeRun()
    {
        SqlServerStorage storage = CreateStorage();
        _runs = 0;
        string jobId = new BackgroundJobClient(storage).Schedule(() => DueHourly(), TimeSpan.FromMilliseconds(200));

        JobStateInfo state = await RunUntilCompleted(storage, jobId);

        Assert.Equal(SucceededState.StateName, state.Name);
        Assert.Equal(1, _runs);
    }

    [Fact, CleanDatabase]
    public async Task ScheduledFor_IsTheMomentADelayedJobWasScheduledFor()
    {
        SqlServerStorage storage = CreateStorage();
        ScheduledForSeen.Clear();
        DateTimeOffset enqueueAt = DateTimeOffset.UtcNow.AddMilliseconds(300);
        BackgroundJobClient client = new(storage);

        string delayed = client.Schedule(() => RecordScheduledFor(null), enqueueAt);
        string immediate = client.Enqueue(() => RecordScheduledFor(null));

        await RunUntilCompleted(storage, delayed);
        await RunUntilCompleted(storage, immediate);

        DateTime?[] seen = [.. ScheduledForSeen];
        Assert.Equal(2, seen.Length);
        Assert.Contains(seen, x => x.HasValue && Math.Abs((x.Value - enqueueAt.UtcDateTime).TotalMilliseconds) < 1);
        Assert.Contains(seen, x => !x.HasValue);
    }

    [Fact, CleanDatabase]
    public async Task ExceptionHandler_IsCalledForEachFailedAttempt()
    {
        SqlServerStorage storage = CreateStorage();
        _runs = 0;
        RecordingHandler handler = new();
        JobFilterCollection filters = [];
        filters.Add(new JobExceptionHandlerFilter(handler));
        string jobId = new BackgroundJobClient(storage).Enqueue(() => FailTwiceThenSucceed());

        JobStateInfo state = await RunUntilCompleted(storage, jobId, new JobFilterProviderCollection(JobFilterProviders.Providers, filters));

        Assert.Equal(SucceededState.StateName, state.Name);
        Assert.Equal(3, _runs);
        Assert.Equal(new[] { 0, 1 }, handler.RetryCounts.ToArray());
        Assert.All(handler.Exceptions, x => Assert.IsType<InvalidOperationException>(x));
    }

    [Fact, CleanDatabase]
    public async Task WaitForCompletionAsync_ReturnsTheSucceededState_WithTheResult()
    {
        SqlServerStorage storage = CreateStorage();
        string jobId = new BackgroundJobClient(storage).Enqueue(() => Answer());

        JobStateInfo state = await RunUntilCompleted(storage, jobId);

        Assert.True(state.IsCompleted);
        Assert.True(storage.TryGetResult(jobId, out int answer));
        Assert.Equal(42, answer);
    }

    public static void SkipItself()
    {
        Interlocked.Increment(ref _runs);
        throw new TerminateJobException("The invoice is already paid");
    }

    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [0])]
    public static void FailForGood()
    {
        Interlocked.Increment(ref _runs);
        throw new TerminateJobException("The address is invalid", TerminateJobOutcome.Failed);
    }

    [SkipIfLate("01:00:00")]
    public static void DueHourly()
    {
        Interlocked.Increment(ref _runs);
    }

    public static void RecordScheduledFor(PerformContext context)
    {
        ScheduledForSeen.Enqueue(context.ScheduledFor);
    }

    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [0])]
    public static void FailTwiceThenSucceed()
    {
        if (Interlocked.Increment(ref _runs) <= 2)
        {
            throw new InvalidOperationException("Not yet");
        }
    }

    public static int Answer() => 42;

    private static async Task<JobStateInfo> RunUntilCompleted(SqlServerStorage storage, string jobId, IJobFilterProvider filterProvider = null)
    {
        BackgroundJobServerOptions options = new()
        {
            WorkerCount = 1,
            SchedulePollingInterval = TimeSpan.FromMilliseconds(100),
            FilterProvider = filterProvider,
        };

        using (new BackgroundJobServer(options, storage))
        {
            JobStateInfo state = await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken);

            Assert.NotNull(state);
            Assert.True(state.IsCompleted, $"The job didn't complete in time; it is {state.Name}.");
            return state;
        }
    }

    private static string GetParameter(SqlServerStorage storage, string jobId, string name)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetJobParameter(jobId, name);
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }

    private sealed class RecordingHandler : IJobExceptionHandler
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();

        public ConcurrentQueue<int> RetryCounts { get; } = new();

        public void OnJobException(JobExceptionContext context)
        {
            Exceptions.Enqueue(context.Exception);
            RetryCounts.Enqueue(context.RetryCount);
        }

        public void OnJobCanceled(JobExceptionContext context)
        {
        }
    }
}
