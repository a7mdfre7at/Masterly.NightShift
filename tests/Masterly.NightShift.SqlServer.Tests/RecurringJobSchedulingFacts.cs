using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Run history, once jobs, retired declarations, late-start limits and upcoming runs, through real storage.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class RecurringJobSchedulingFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static int _runs;

    [Fact, CleanDatabase]
    public async Task RunHistory_ListsEachTriggeredRun_NewestFirst_AndIsRemovedWithTheJob()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdate("history", Job.FromExpression(() => Answer()), "0 0 1 1 *", new RecurringJobOptions());

        string first = manager.TriggerJob("history");
        string second = manager.TriggerJob("history");

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
        {
            await storage.WaitForCompletionAsync(first, Timeout, TestContext.Current.CancellationToken);
            await storage.WaitForCompletionAsync(second, Timeout, TestContext.Current.CancellationToken);
        }

        IReadOnlyList<RecurringJobRun> runs = storage.GetRecurringJobRuns("history");

        Assert.Equal([second, first], runs.Select(static run => run.JobId));
        Assert.All(runs, static run => Assert.Equal(SucceededState.StateName, run.State));
        Assert.All(runs, static run => Assert.NotNull(run.StartedAt));
        Assert.All(runs, static run => Assert.NotNull(run.ScheduledFor));

        manager.RemoveIfExists("history");

        Assert.Empty(storage.GetRecurringJobRuns("history"));
    }

    [Fact, CleanDatabase]
    public void RunHistory_KeepsTheLastRunsOnly()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdate("busy", Job.FromExpression(() => Answer()), "0 0 1 1 *", new RecurringJobOptions());

        for (int i = 0; i < RecurringJobHistory.MaxRuns + 5; i++)
        {
            manager.TriggerJob("busy");
        }

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(RecurringJobHistory.MaxRuns, ((JobStorageConnection)connection).GetListCount(RecurringJobHistory.GetKey("busy")));
    }

    [Fact, CleanDatabase]
    public void Once_CreatesTheJobOnce_HoweverOftenTheSetIsApplied()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);

        RecurringJobSetResult first = new RecurringJobSet().Once("seed-roles", () => Answer()).Apply(manager, storage);
        RecurringJobSetResult second = new RecurringJobSet().Once("seed-roles", () => Answer()).Apply(manager, storage);

        Assert.Equal(["seed-roles"], first.CreatedOnce);
        Assert.Empty(second.CreatedOnce);

        using IStorageConnection connection = storage.GetConnection();
        string jobId = Assert.Single(connection.GetAllEntriesFromHash(RecurringJobSet.OnceHashKey)).Value;
        Assert.Equal(EnqueuedState.StateName, connection.GetStateData(jobId).Name);
    }

    [Fact, CleanDatabase]
    public void RetireUndeclared_PausesAJobNoLongerDeclared_AndDeclaringItAgainResumesIt()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);
        new RecurringJobSet().Add("kept", () => Answer(), "0 * * * *").Add("dropped", () => Answer(), "0 * * * *").Apply(manager, storage);
        manager.AddOrUpdate("made-at-runtime", Job.FromExpression(() => Answer()), "0 * * * *", new RecurringJobOptions());

        RecurringJobSetResult retired = new RecurringJobSet().Add("kept", () => Answer(), "0 * * * *").RetireUndeclared().Apply(manager, storage);

        Assert.Equal(["dropped"], retired.Retired);
        RecurringJobDto dropped = Find(storage, "dropped");
        Assert.True(dropped.Paused);
        Assert.True(dropped.Retired);
        Assert.False(Find(storage, "made-at-runtime").Paused);
        Assert.True(Find(storage, "kept").Declared);
        Assert.False(Find(storage, "made-at-runtime").Declared);

        new RecurringJobSet().Add("kept", () => Answer(), "0 * * * *").Add("dropped", () => Answer(), "0 * * * *").Apply(manager, storage);

        RecurringJobDto restored = Find(storage, "dropped");
        Assert.False(restored.Paused);
        Assert.False(restored.Retired);
        Assert.NotNull(restored.NextExecution);
    }

    [Fact, CleanDatabase]
    public void RetireUndeclared_WithoutAScope_PausesTheJobsOfAnotherApplicationsSet()
    {
        // Nothing tells whose set declared a job, so two applications that declare different sets in one storage
        // pause each other's jobs at each start. This is why such sets need a scope each (see the next test)
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);

        new RecurringJobSet().Add("billing:invoices", () => Answer(), "0 * * * *").RetireUndeclared().Apply(manager, storage);
        RecurringJobSetResult reports = new RecurringJobSet().Add("reports:daily", () => Answer(), "0 * * * *").RetireUndeclared().Apply(manager, storage);

        Assert.Equal(["billing:invoices"], reports.Retired);
        Assert.True(Find(storage, "billing:invoices").Paused);
    }

    [Fact, CleanDatabase]
    public void RetireUndeclared_WithAScopeEach_LeavesTheJobsOfAnotherApplicationsSetAlone()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);

        for (int start = 0; start < 2; start++)
        {
            Assert.Empty(new RecurringJobSet()
                .Add("billing:invoices", () => Answer(), "0 * * * *")
                .RetireUndeclared(static id => id.StartsWith("billing:", StringComparison.Ordinal))
                .Apply(manager, storage).Retired);
            Assert.Empty(new RecurringJobSet()
                .Add("reports:daily", () => Answer(), "0 * * * *")
                .RetireUndeclared(static id => id.StartsWith("reports:", StringComparison.Ordinal))
                .Apply(manager, storage).Retired);
        }

        Assert.False(Find(storage, "billing:invoices").Paused);
        Assert.False(Find(storage, "reports:daily").Paused);

        // A job one of them stops declaring is still retired
        RecurringJobSetResult billing = new RecurringJobSet()
            .Add("billing:reminders", () => Answer(), "0 * * * *")
            .RetireUndeclared(static id => id.StartsWith("billing:", StringComparison.Ordinal))
            .Apply(manager, storage);

        Assert.Equal(["billing:invoices"], billing.Retired);
        Assert.False(Find(storage, "reports:daily").Paused);
    }

    [Fact, CleanDatabase]
    public async Task SkipAfterParameter_SkipsARunThatStartsTooLate()
    {
        SqlServerStorage storage = CreateStorage();
        _runs = 0;
        Dictionary<string, object> parameters = new()
        {
            [PerformContext.ScheduledForParameter] = JobHelper.ToMillisecondTimestamp(DateTime.UtcNow.AddHours(-1)),
            [SkipIfLateAttribute.WindowParameter] = 60L,
        };

        string jobId = new BackgroundJobClient(storage).Create(Job.FromExpression(() => Count()), new EnqueuedState(), parameters);

        JobStateInfo state;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
        {
            state = await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(DeletedState.StateName, state.Name);
        Assert.StartsWith("Skipped: started 1 h late", state.Reason, StringComparison.Ordinal);
        Assert.Equal(0, _runs);
    }

    [Fact, CleanDatabase]
    public void SkipAfter_IsStoredWithTheRecurringJob_AndCopiedToItsRuns()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdate("late", Job.FromExpression(() => Answer()), "0 9 * * *", new RecurringJobOptions
        {
            SkipAfter = TimeSpan.FromMinutes(30),
            MisfireHandling = MisfireHandlingMode.RunLatest,
        });

        string jobId = manager.TriggerJob("late");

        Assert.Equal(TimeSpan.FromMinutes(30), Find(storage, "late").SkipAfter);
        Assert.Equal(MisfireHandlingMode.RunLatest, Find(storage, "late").MisfireHandling);
        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal("1800", connection.GetJobParameter(jobId, SkipIfLateAttribute.WindowParameter));
    }

    [Fact, CleanDatabase]
    public void GetNextOccurrences_ListsTheComingRuns()
    {
        SqlServerStorage storage = CreateStorage();
        new RecurringJobManager(storage).AddOrUpdate("hourly", Job.FromExpression(() => Answer()), "0 * * * *", new RecurringJobOptions());

        IReadOnlyList<DateTime> next = storage.GetNextOccurrences("hourly", 3);

        Assert.Equal(3, next.Count);
        Assert.All(next, static occurrence => Assert.Equal(0, occurrence.Minute));
        Assert.True(next[0] > DateTime.UtcNow);
        Assert.Equal(next[0].AddHours(1), next[1]);
    }

    [Fact, CleanDatabase]
    public void TriggerJob_FromAProcessWhoseClockIsBehind_DoesNotMakeTheSchedulerRunASlotAgain()
    {
        SqlServerStorage storage = CreateStorage();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdate("strict", Job.FromExpression(() => Answer()), "* * * * *", new RecurringJobOptions { MisfireHandling = MisfireHandlingMode.Strict });

        // The occurrence of the current minute has run already
        DateTime slot;
        using (IStorageConnection connection = storage.GetConnection())
        {
            DateTime databaseNow = ((JobStorageConnection)connection).GetUtcDateTime();
            slot = new DateTime(databaseNow.Ticks - (databaseNow.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetRangeInHash("recurring-job:strict", new Dictionary<string, string>
            {
                ["LastExecution"] = JobHelper.SerializeDateTime(slot),
                ["NextExecution"] = JobHelper.SerializeDateTime(slot.AddMinutes(1)),
                ["ScheduleChangedAt"] = JobHelper.SerializeDateTime(slot.AddHours(-1)),
                ["CreatedAt"] = JobHelper.SerializeDateTime(slot.AddHours(-1)),
            });
            transaction.AddToSet("recurring-jobs", "strict", JobHelper.ToTimestamp(slot.AddMinutes(1)));
            transaction.Commit();
        }

        string triggered;
        TimeProvider original = JobClock.TimeProvider;
        JobClock.TimeProvider = new OffsetClock(slot.AddSeconds(-10) - DateTime.UtcNow);
        try
        {
            triggered = manager.TriggerJob("strict");
        }
        finally
        {
            JobClock.TimeProvider = original;
        }

        using (CancellationTokenSource cts = new())
        {
            BackgroundProcessContext context = new("server", storage, new Dictionary<string, object>(), Guid.NewGuid(), cts.Token, cts.Token, cts.Token);
            new RecurringJobScheduler(new BackgroundJobFactory(JobFilterProviders.Providers), TimeSpan.FromMilliseconds(1)).Execute(context);
        }

        Assert.DoesNotContain(storage.GetRecurringJobRuns("strict"), run => run.JobId != triggered && run.ScheduledFor == slot);
        Assert.True(Find(storage, "strict").LastExecution >= slot);
    }

    public static int Answer() => 42;

    public static void Count()
    {
        Interlocked.Increment(ref _runs);
    }

    private static RecurringJobDto Find(SqlServerStorage storage, string id)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetRecurringJobs([id]).Single();
    }

    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }
}
