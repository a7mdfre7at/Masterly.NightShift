using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Typed handlers and recurring job sets through a real server and real storage: what is written can be read
/// back and run, and the attributes on the handler class take effect.
/// </summary>
public class JobHandlerFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact, CleanDatabase]
    public void Enqueue_RunsTheHandler_WithItsArguments_OnTheQueueOfItsAttribute()
    {
        SqlServerStorage storage = CreateStorage();
        JobHandlers.Add<RecordingHandler>();
        RecordingHandler.Reset();

        try
        {
            string jobId = new BackgroundJobClient(storage).Enqueue(new RecordingArgs("invoice-7", 3));

            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = [RecordingHandler.Queue] }, storage))
            {
                Assert.True(RecordingHandler.Handled.Wait(Timeout, TestContext.Current.CancellationToken), "The handler wasn't run in time. A queue other than its attribute's would leave it waiting.");
                Assert.True(SpinWait.SpinUntil(() => HasSucceeded(storage, jobId), Timeout), "The job didn't succeed in time.");
            }

            Assert.Equal(new RecordingArgs("invoice-7", 3), RecordingHandler.Received);
            Assert.True(RecordingHandler.TokenCouldBeCanceled, "The handler should get the server's token, not CancellationToken.None.");
        }
        finally
        {
            JobHandlers.Clear();
        }
    }

    [Fact, CleanDatabase]
    public void Enqueue_ForAJobClass_RunsIt_WithTheContextOfTheJob()
    {
        SqlServerStorage storage = CreateStorage();
        ContextRecordingJob.Reset();

        string jobId = new BackgroundJobClient(storage).Enqueue<ContextRecordingJob>();

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = [ContextRecordingJob.Queue] }, storage))
        {
            Assert.True(ContextRecordingJob.Ran.Wait(Timeout, TestContext.Current.CancellationToken), "The job wasn't run in time.");
            Assert.True(SpinWait.SpinUntil(() => HasSucceeded(storage, jobId), Timeout), "The job didn't succeed in time.");
        }

        Assert.Equal(jobId, ContextRecordingJob.ReceivedJobId);
        Assert.Equal(0, ContextRecordingJob.ReceivedRetryCount);
        Assert.True(ContextRecordingJob.TokenCouldBeCanceled, "The job should get the server's token, not CancellationToken.None.");
    }

    [Fact, CleanDatabase]
    public void RetryCount_IsOne_OnTheFirstRetry()
    {
        SqlServerStorage storage = CreateStorage();
        FailingOnceJob.Reset();

        string jobId = new BackgroundJobClient(storage).Enqueue<FailingOnceJob>();

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = [FailingOnceJob.Queue], SchedulePollingInterval = TimeSpan.FromMilliseconds(100) }, storage))
        {
            Assert.True(SpinWait.SpinUntil(() => HasSucceeded(storage, jobId), Timeout), "The job didn't succeed in time.");
        }

        Assert.Equal([0, 1], FailingOnceJob.RetryCounts);
    }

    [Fact, CleanDatabase]
    public void Enqueue_WithAChosenHandler_RunsThatHandler_WhenTwoShareTheArguments()
    {
        SqlServerStorage storage = CreateStorage();
        JobHandlers.Add<RecordingHandler>();
        JobHandlers.Add<SecondRecordingHandler>();
        SecondRecordingHandler.Reset();

        try
        {
            string jobId = new BackgroundJobClient(storage).Enqueue<SecondRecordingHandler, RecordingArgs>(new RecordingArgs("chosen", 1));

            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = [SecondRecordingHandler.Queue] }, storage))
            {
                Assert.True(SecondRecordingHandler.Handled.Wait(Timeout, TestContext.Current.CancellationToken), "The chosen handler wasn't run in time.");
                Assert.True(SpinWait.SpinUntil(() => HasSucceeded(storage, jobId), Timeout), "The job didn't succeed in time.");
            }

            Assert.Equal(new RecordingArgs("chosen", 1), SecondRecordingHandler.Received);
        }
        finally
        {
            JobHandlers.Clear();
        }
    }

    [Fact, CleanDatabase]
    public void RecurringJobSet_RegistersDeclaredJobs_AndRemovesOnlyUndeclaredOnesInScope()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);
        JobHandlers.Add<RecordingHandler>();

        try
        {
            // Removed only because a set of the same name declared it before
            new RecurringJobSet().Add("app:stale", () => Nothing(), "0 * * * *").Apply(manager, storage);
            manager.AddOrUpdate("manual", Job.FromExpression(() => Nothing()), "0 * * * *", new RecurringJobOptions());

            RecurringJobSetResult result = new RecurringJobSet()
                .AddHandler("app:report", new RecordingArgs("daily", 1), Cron.Every(TimeSpan.FromMinutes(5)))
                .RemoveUndeclared(static id => id.StartsWith("app:", StringComparison.Ordinal))
                .Apply(manager, storage);

            using IStorageConnection connection = storage.GetConnection();
            List<RecurringJobDto> jobs = connection.GetRecurringJobs();

            Assert.Equal(["app:report"], result.Applied);
            Assert.Equal(["app:stale"], result.Removed);
            Assert.Equal(["app:report", "manual"], jobs.Select(static job => job.Id).Order(StringComparer.Ordinal));

            RecurringJobDto report = jobs.Single(static job => job.Id == "app:report");
            Assert.Equal("*/5 * * * *", report.Cron);
            Assert.Equal(typeof(RecordingHandler), report.Job.Type);
            Assert.Equal(new RecordingArgs("daily", 1), report.Job.Args[0]);
        }
        finally
        {
            JobHandlers.Clear();
        }
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }

    private static bool HasSucceeded(SqlServerStorage storage, string jobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetStateData(jobId)?.Name == "Succeeded";
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Nothing()
    {
    }

    public sealed record RecordingArgs(string Name, int Attempt);

    [Queue(Queue)]
    public sealed class RecordingHandler : IJobHandler<RecordingArgs>
    {
        public const string Queue = "handlers";

        public static ManualResetEventSlim Handled { get; private set; } = new();

        public static RecordingArgs Received { get; private set; }

        public static bool TokenCouldBeCanceled { get; private set; }

        public static void Reset()
        {
            Handled = new ManualResetEventSlim();
            Received = null;
            TokenCouldBeCanceled = false;
        }

        public Task HandleAsync(RecordingArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            Received = args;
            TokenCouldBeCanceled = cancellationToken.CanBeCanceled;
            Handled.Set();
            return Task.CompletedTask;
        }
    }

    [Queue(Queue)]
    public sealed class SecondRecordingHandler : IJobHandler<RecordingArgs>
    {
        public const string Queue = "chosen";

        public static ManualResetEventSlim Handled { get; private set; } = new();

        public static RecordingArgs Received { get; private set; }

        public static void Reset()
        {
            Handled = new ManualResetEventSlim();
            Received = null;
        }

        public Task HandleAsync(RecordingArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            Received = args;
            Handled.Set();
            return Task.CompletedTask;
        }
    }

    [Queue(Queue)]
    public sealed class ContextRecordingJob : IJob
    {
        public const string Queue = "job-classes";

        public static ManualResetEventSlim Ran { get; private set; } = new();

        public static string ReceivedJobId { get; private set; }

        public static int ReceivedRetryCount { get; private set; }

        public static bool TokenCouldBeCanceled { get; private set; }

        public static void Reset()
        {
            Ran = new ManualResetEventSlim();
            ReceivedJobId = null;
            ReceivedRetryCount = -1;
            TokenCouldBeCanceled = false;
        }

        public Task RunAsync(PerformContext context, CancellationToken cancellationToken)
        {
            ReceivedJobId = context.BackgroundJob.Id;
            ReceivedRetryCount = context.RetryCount;
            TokenCouldBeCanceled = cancellationToken.CanBeCanceled;
            Ran.Set();
            return Task.CompletedTask;
        }
    }

    [Queue(Queue)]
    public sealed class FailingOnceJob : IJob
    {
        public const string Queue = "failing-once";

        public static List<int> RetryCounts { get; private set; } = [];

        public static void Reset()
        {
            RetryCounts = [];
        }

        [AutomaticRetry(Attempts = 1, DelaysInSeconds = [0])]
        public Task RunAsync(PerformContext context, CancellationToken cancellationToken)
        {
            RetryCounts.Add(context.RetryCount);
            return RetryCounts.Count == 1 ? throw new InvalidOperationException("Fails the first run.") : Task.CompletedTask;
        }
    }
}
