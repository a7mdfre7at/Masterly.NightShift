using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class JobInsightsAttributeFacts
{
    private readonly PerformContextMock _context = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly string _key;

    public JobInsightsAttributeFacts()
    {
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _context.BackgroundJob.Job = Job.FromExpression(() => Sample());
        _key = JobInsightsAttribute.GetKey(_context.BackgroundJob.Job);
    }

    [Fact]
    public void GetKey_IsTypeAndMethod()
    {
        Assert.Equal("JobInsightsAttributeFacts.Sample", _key);
    }

    [Fact, StaticLock]
    public void OnPerformed_RecordsASuccessfulRun()
    {
        JobInsightsAttribute attribute = new();
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);

        attribute.OnPerformed(new Masterly.NightShift.Server.PerformedContext(performing, "result", false, null));

        _transaction.Verify(x => x.AddToSet(JobInsightsAttribute.IndexKey, _key));
        _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:ok"));
        _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:failed"), Times.Never);
        _transaction.Verify(x => x.InsertToList($"insights:{_key}:durations", It.IsAny<string>()));
        _transaction.Verify(x => x.TrimList($"insights:{_key}:durations", 0, JobInsightsAttribute.DurationSampleSize - 1));
        _transaction.Verify(x => x.SetRangeInHash($"insights:{_key}", It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs =>
            pairs.Any(p => p.Key == "LastJobId" && p.Value == "JobId")
            && pairs.Any(p => p.Key == "LastFailed" && p.Value == "0")
            && pairs.Any(p => p.Key == "Invoker" && (p.Value == "Generated" || p.Value == "Compiled" || p.Value == "Reflection")))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact, StaticLock]
    public void OnPerformed_RecordsAFailedRun()
    {
        JobInsightsAttribute attribute = new();
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);

        attribute.OnPerformed(new Masterly.NightShift.Server.PerformedContext(performing, null, false, new InvalidOperationException()));

        _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:failed"));
        _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:ok"), Times.Never);
        _transaction.Verify(x => x.SetRangeInHash($"insights:{_key}", It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs =>
            pairs.Any(p => p.Key == "LastFailed" && p.Value == "1"))));
    }

    [Fact, StaticLock]
    public void OnPerformed_RecordsNothing_ForCanceledJobs()
    {
        JobInsightsAttribute attribute = new();

        attribute.OnPerformed(_context.GetPerformedContext(canceled: true));

        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_RecordsNothing_ForAbortedJobs()
    {
        JobInsightsAttribute attribute = new();
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);

        attribute.OnPerformed(new Masterly.NightShift.Server.PerformedContext(performing, null, false, new Masterly.NightShift.Server.JobAbortedException()));

        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_RecordsNothing_WhenTheServerShutsDownWhileTheJobRuns()
    {
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new System.Threading.CancellationToken(canceled: true));
        JobInsightsAttribute attribute = new();
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);

        attribute.OnPerformed(new Masterly.NightShift.Server.PerformedContext(performing, null, false, new OperationCanceledException()));

        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_RecordsNothing_WhenTheJobAskedToBeRetriedLater()
    {
        JobInsightsAttribute attribute = new();
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);
        JobPerformanceException exception = new("Job failed", new RetryLaterException(TimeSpan.FromHours(1), "partner busy"));

        attribute.OnPerformed(new Masterly.NightShift.Server.PerformedContext(performing, null, false, exception));

        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_RecordsAFailedRun_WhenTheJobCanceledItself()
    {
        JobInsightsAttribute attribute = new();
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);
        JobPerformanceException exception = new("Job failed", new OperationCanceledException());

        attribute.OnPerformed(new Masterly.NightShift.Server.PerformedContext(performing, null, false, exception));

        _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:failed"));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void ReadAll_ReturnsEmpty_ForConnectionsWithoutListSupport()
    {
        Assert.Empty(JobInsightsAttribute.ReadAll(_context.Connection.Object));
    }

    [Fact]
    public void ReadAll_ComputesStatistics_FromStoredData()
    {
        Mock<JobStorageConnection> connection = new();
        connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(["Jobs.Run"]);
        connection.Setup(x => x.GetAllEntriesFromHash("insights:Jobs.Run")).Returns(new Dictionary<string, string>
        {
            { "LastRunAt", JobHelper.SerializeDateTime(new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc)) },
            { "LastDurationMs", "300" },
            { "LastJobId", "42" },
            { "LastServerId", "server-a" },
            { "LastFailed", "1" },
            { "Invoker", "Generated" },
        });
        connection.Setup(x => x.GetAllItemsFromList("insights:Jobs.Run:durations")).Returns(["300", "100", "200"]);
        connection.Setup(x => x.GetCounter("insights:Jobs.Run:ok")).Returns(7);
        connection.Setup(x => x.GetCounter("insights:Jobs.Run:failed")).Returns(3);

        JobInsight insight = Assert.Single(JobInsightsAttribute.ReadAll(connection.Object));

        Assert.Equal("Jobs.Run", insight.Key);
        Assert.Equal(7, insight.Succeeded);
        Assert.Equal(3, insight.Failed);
        Assert.Equal(10, insight.Total);
        Assert.Equal(0.3, insight.FailureRate, 3);
        Assert.Equal(new long[] { 200, 100, 300 }, insight.Durations);
        Assert.Equal(200, insight.AverageMs);
        Assert.Equal(200, insight.MedianMs);
        Assert.Equal(300, insight.P95Ms);
        Assert.Equal(300, insight.MaxMs);
        Assert.Equal(300, insight.LastDurationMs);
        Assert.Equal("42", insight.LastJobId);
        Assert.Equal("server-a", insight.LastServerId);
        Assert.True(insight.LastFailed);
        Assert.Equal("Generated", insight.Invoker);
        Assert.False(insight.LastRunWasSlow);
    }

    [Fact]
    public void Read_FlagsSlowRuns_WithEnoughSamples()
    {
        Mock<JobStorageConnection> connection = new();
        List<string> durations = [.. Enumerable.Repeat("100", 12)];
        durations.Insert(0, "5000"); // newest first
        connection.Setup(x => x.GetAllEntriesFromHash("insights:Jobs.Slow")).Returns(new Dictionary<string, string> { { "LastDurationMs", "5000" } });
        connection.Setup(x => x.GetAllItemsFromList("insights:Jobs.Slow:durations")).Returns(durations);

        JobInsight? insight = JobInsightsAttribute.Read(connection.Object, "Jobs.Slow");

        Assert.True(insight.LastRunWasSlow);
    }

    [Fact]
    public void Read_ReturnsNull_WhenNothingWasRecorded()
    {
        Mock<JobStorageConnection> connection = new();
        connection.Setup(x => x.GetAllEntriesFromHash("insights:Nothing")).Returns(default(Dictionary<string, string>));

        Assert.Null(JobInsightsAttribute.Read(connection.Object, "Nothing"));
    }

    [Fact, StaticLock]
    public void OnPerformed_DoesNotFailTheJob_WhenTheStorageFails_AndLogsAWarning()
    {
        RecordingLogProvider provider = new();
        JobInsightsAttribute attribute = new();
        PerformingContext performing = _context.GetPerformingContext();
        attribute.OnPerforming(performing);
        _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("deadlock victim"));

        using (provider.Use())
        {
            attribute.OnPerformed(new Masterly.NightShift.Server.PerformedContext(performing, "result", false, null));
        }

        Assert.Contains(provider.Entries, e => e.Level == Masterly.NightShift.Logging.LogLevel.Warn && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void GetKey_BoundsLongKeys_KeepingTheMethodName_AndTellsTypesApartWithAHash()
    {
        string key = JobInsightsAttribute.GetKey(Job.FromExpression<LongGenericTypeNameForTheInsightsKeyLimit<Dictionary<string, List<KeyValuePair<string, int>>>>>(x => x.Process()));
        string other = JobInsightsAttribute.GetKey(Job.FromExpression<LongGenericTypeNameForTheInsightsKeyLimit<Dictionary<string, List<KeyValuePair<string, long>>>>>(x => x.Process()));

        Assert.True(key.Length <= JobInsightsAttribute.MaxKeyLength, key);
        Assert.StartsWith("JobInsightsAttributeFacts.LongGenericTypeName", key);
        Assert.EndsWith(".Process", key);
        Assert.Matches("~[0-9a-f]{8}\\.Process$", key);
        Assert.NotEqual(key, other);
        Assert.Equal(key, JobInsightsAttribute.GetKey(Job.FromExpression<LongGenericTypeNameForTheInsightsKeyLimit<Dictionary<string, List<KeyValuePair<string, int>>>>>(x => x.Process())));
        Assert.True($"insights:{key}:durations".Length <= 100);
    }

    [Fact]
    public void Read_ReturnsNull_WhenTheStorageCannotReadListsAndCounters()
    {
        Mock<JobStorageConnection> connection = new() { CallBase = true };
        connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(["Jobs.Run"]);
        connection.Setup(x => x.GetAllEntriesFromHash("insights:Jobs.Run")).Returns(new Dictionary<string, string> { { "LastDurationMs", "5" } });

        Assert.Null(JobInsightsAttribute.Read(connection.Object, "Jobs.Run"));
        Assert.Empty(JobInsightsAttribute.ReadAll(connection.Object));
    }

    [Fact, StaticLock]
    public void Perform_RecordsNothing_WhenDisableConcurrentExecutionGivesUpWaitingForTheLock()
    {
        _context.BackgroundJob.Job = Job.FromExpression<LockedJobs>(x => x.Locked());
        _context.Connection
            .Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new DistributedLockTimeoutException("locked-jobs"));

        // The worker still records the attempt as failed
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => CreatePerformer().Perform(_context.Object));

        Assert.IsType<DistributedLockTimeoutException>(exception.InnerException);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact, StaticLock]
    public void Perform_RecordsNothing_WhenAFilterGivesUpWaitingForASlot()
    {
        _context.BackgroundJob.Job = Job.FromExpression<LockedJobs>(x => x.Throttled());

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => CreatePerformer().Perform(_context.Object));

        Assert.IsType<ConcurrencyLimitException>(exception.InnerException);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact, StaticLock]
    public void Perform_LeavesTheLockWaitOutOfTheDuration()
    {
        _context.BackgroundJob.Job = Job.FromExpression<LockedJobs>(x => x.Locked());
        _context.Connection
            .Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                Thread.Sleep(600);
                return new Mock<IDisposable>().Object;
            });
        List<long> durations = [];
        _transaction
            .Setup(x => x.InsertToList($"insights:{LockedJobsKey}:durations", It.IsAny<string>()))
            .Callback<string, string>((_, value) => durations.Add(long.Parse(value, CultureInfo.InvariantCulture)));

        CreatePerformer().Perform(_context.Object);

        _transaction.Verify(x => x.IncrementCounter($"insights:{LockedJobsKey}:ok"));
        long duration = Assert.Single(durations);
        Assert.InRange(duration, 0, 300);
    }

    [Fact, StaticLock]
    public void Perform_RecordsAFailure_WhenTheMethodItselfTimesOutOnALock()
    {
        _context.BackgroundJob.Job = Job.FromExpression<LockedJobs>(x => x.ThrowsItsOwnLockTimeout());

        Assert.Throws<JobPerformanceException>(() => CreatePerformer().Perform(_context.Object));

        _transaction.Verify(x => x.IncrementCounter($"insights:{JobInsightsAttribute.GetKey(_context.BackgroundJob.Job)}:failed"));
    }

    private static readonly string LockedJobsKey = JobInsightsAttribute.GetKey(Job.FromExpression<LockedJobs>(x => x.Locked()));

    private static BackgroundJobPerformer CreatePerformer()
    {
        // Insights as UseJobInsights() adds it (global, order -1), around the method's concurrency filters (order -1)
        JobFilterCollection globalFilters = new() { new JobInsightsAttribute() };
        JobFilterProviderCollection filters = new(globalFilters, new JobFilterAttributeFilterProvider());

        return new BackgroundJobPerformer(filters, new CoreBackgroundJobPerformer(new JobActivator(), null));
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Sample()
    {
    }

    public class LockedJobs
    {
        [DisableConcurrentExecution(1)]
        public void Locked()
        {
        }

        [GivesUpWaitingForASlot]
        public void Throttled()
        {
        }

        public void ThrowsItsOwnLockTimeout()
        {
            throw new DistributedLockTimeoutException("own-lock");
        }
    }

    /// <summary>Fails like <c>[Throttle]</c> does when no slot frees up in time.</summary>
    private sealed class GivesUpWaitingForASlotAttribute : JobFilterAttribute, IServerFilter
    {
        public void OnPerforming(PerformingContext context)
        {
            throw new ConcurrencyLimitException("slots", "All 1 slot(s) of resource 'slots' stayed in use for 1 seconds.");
        }

        public void OnPerformed(PerformedContext context)
        {
        }
    }

    [SuppressMessage("ReSharper", "UnusedTypeParameter")]
    public sealed class LongGenericTypeNameForTheInsightsKeyLimit<T>
    {
        public void Process()
        {
        }
    }
}
