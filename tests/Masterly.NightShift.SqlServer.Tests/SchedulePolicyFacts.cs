using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Schedule policies through real storage: revisions, references, delayed jobs moved, skipped or rejected at
/// creation and when due, and recurring jobs that follow a policy.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class SchedulePolicyFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Today and tomorrow (UTC) are excluded, so a test running around midnight still finds them blocked. Read once
    // per test (xUnit creates the class for each one), so every statement of a test agrees on which day it is.
    private DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

    private DateTime DayAfterTomorrow => Today.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    [Fact, CleanDatabase]
    public void Policies_AreStoredWithRevisions_AndAreNotDeletedWhileUsed()
    {
        SqlServerStorage storage = CreateStorage();

        Assert.Equal(1, storage.SaveSchedulePolicy(new SchedulePolicy("trading").ExcludeAnnualDate(12, 31)));
        Assert.Equal(1, storage.SaveSchedulePolicy(new SchedulePolicy("business-hours").Require("trading").AllowBetween("09:00", "17:00")));
        Assert.Equal(1, storage.SaveSchedulePolicy(new SchedulePolicy("business-hours").Require("trading").AllowBetween("09:00", "17:00")));

        SchedulePolicy read = storage.GetSchedulePolicy("business-hours");
        Assert.Equal(1, read?.Revision);
        Assert.Equal(["trading"], read?.Required);
        Assert.Equal(["business-hours", "trading"], storage.GetSchedulePolicies().Select(static x => x.Name));

        Assert.Null(storage.UpdateSchedulePolicy(new SchedulePolicy("business-hours").AllowBetween("08:00", "17:00"), 0));
        Assert.Equal(2, storage.UpdateSchedulePolicy(new SchedulePolicy("business-hours").Require("trading").AllowBetween("08:00", "17:00"), 1));

        Assert.Throws<InvalidOperationException>(() => storage.DeleteSchedulePolicy("trading", 1));
        Assert.Throws<ArgumentException>(() => storage.SaveSchedulePolicy(new SchedulePolicy("trading").Require("business-hours")));

        Assert.False(storage.DeleteSchedulePolicy("business-hours", 1));
        Assert.True(storage.DeleteSchedulePolicy("business-hours", 2));
        Assert.True(storage.DeleteSchedulePolicy("trading", 1));
        Assert.Empty(storage.GetSchedulePolicies());
    }

    [Fact, CleanDatabase]
    public void DelayedJob_IsMovedForward_SkippedOrRejected_WhenThePolicyBlocksItsTime()
    {
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(BlockTodayAndTomorrow("closed"));
        BackgroundJobClient client = new(storage);
        DateTimeOffset dueAt = new(Today.AddDays(1).ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc));

        string moved = client.Schedule(() => Answer(), dueAt, new ScheduleOptions { Policy = "closed" });
        string skipped = client.Schedule(() => Answer(), dueAt, new ScheduleOptions { Policy = "closed", WhenBlocked = WhenBlocked.Skip });
        string rejected = client.Schedule(() => Answer(), dueAt, new ScheduleOptions { Policy = "closed", WhenBlocked = WhenBlocked.Reject });
        string allowed = client.Schedule(() => Answer(), DayAfterTomorrow.AddHours(1), new ScheduleOptions { Policy = "closed", WhenBlocked = WhenBlocked.Reject });

        using IStorageConnection connection = storage.GetConnection();

        StateData movedState = connection.GetStateData(moved);
        Assert.Equal(ScheduledState.StateName, movedState.Name);
        Assert.Equal(DayAfterTomorrow, JobHelper.DeserializeDateTime(movedState.Data["EnqueueAt"]));
        Assert.StartsWith("Moved by policy: policy 'closed':", movedState.Reason, StringComparison.Ordinal);
        Assert.Equal(
            JobHelper.ToMillisecondTimestamp(DayAfterTomorrow).ToString(CultureInfo.InvariantCulture),
            connection.GetJobParameter(moved, PerformContext.ScheduledForParameter));

        StateData skippedState = connection.GetStateData(skipped);
        Assert.Equal(DeletedState.StateName, skippedState.Name);
        Assert.StartsWith("Skipped: policy 'closed':", skippedState.Reason, StringComparison.Ordinal);

        Assert.Null(rejected);
        Assert.Equal(ScheduledState.StateName, connection.GetStateData(allowed).Name);
    }

    [Fact, CleanDatabase]
    public async Task DueJob_IsMovedForward_WhenThePolicyChangedSinceItWasScheduled()
    {
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(new SchedulePolicy("closed"));
        string jobId = new BackgroundJobClient(storage).Schedule(() => Answer(), TimeSpan.FromSeconds(1), new ScheduleOptions { Policy = "closed" });
        storage.SaveSchedulePolicy(BlockTodayAndTomorrow("closed"));

        StateData state;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage))
        {
            state = await WaitForStateAsync(storage, jobId, static x => x.Reason?.StartsWith("Moved by policy", StringComparison.Ordinal) == true);
        }

        Assert.Equal(ScheduledState.StateName, state.Name);
        Assert.Equal(DayAfterTomorrow, JobHelper.DeserializeDateTime(state.Data["EnqueueAt"]));
    }

    [Fact, CleanDatabase]
    public async Task DueJobWithItsOwnQueue_IsMovedForward_WhenThePolicyChangedSinceItWasScheduled()
    {
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(new SchedulePolicy("closed"));
        string jobId = new BackgroundJobClient(storage).Schedule(
            () => Answer(),
            TimeSpan.FromSeconds(1),
            new ScheduleOptions { Policy = "closed", Queue = "critical" });
        storage.SaveSchedulePolicy(BlockTodayAndTomorrow("closed"));

        StateData state;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = ["critical"], SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage))
        {
            state = await WaitForStateAsync(storage, jobId, static x => x.Reason?.StartsWith("Moved by policy", StringComparison.Ordinal) == true);

            // Long enough for a worker to run it, had it been let through
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        Assert.Equal(ScheduledState.StateName, state.Name);
        Assert.Equal(DayAfterTomorrow, JobHelper.DeserializeDateTime(state.Data["EnqueueAt"]));
        Assert.DoesNotContain(
            storage.GetMonitoringApi().JobDetails(jobId).History,
            static x => x.StateName == SucceededState.StateName || x.StateName == ProcessingState.StateName);

        // Back in the schedule for its new time, under its queue, and in no queue
        using IStorageConnection connection = storage.GetConnection();
        Assert.Contains($"critical:{jobId}", connection.GetAllItemsFromSet("schedule"));
        Assert.Equal(0, storage.GetMonitoringApi().EnqueuedCount("critical"));
    }

    [Fact, CleanDatabase]
    public async Task DueJobWithItsOwnQueue_Runs_WhenThePolicyAllowsIt()
    {
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(new SchedulePolicy("open"));
        string jobId = new BackgroundJobClient(storage).Schedule(
            () => Answer(),
            TimeSpan.FromSeconds(1),
            new ScheduleOptions { Policy = "open", Queue = "critical" });

        JobStateInfo state;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, Queues = ["critical"], SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage))
        {
            state = await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SucceededState.StateName, state.Name);
        Assert.Single(storage.GetMonitoringApi().JobDetails(jobId).History, static x => x.StateName == ProcessingState.StateName);
    }

    [Fact, CleanDatabase]
    public async Task DueJob_IsJudgedByTheStorageClock_SoAProcessClockBehindItDoesNotMoveItAgainAndAgain()
    {
        SqlServerStorage storage = CreateStorage();
        DateTime databaseNow;
        using (IStorageConnection connection = storage.GetConnection())
        {
            databaseNow = ((JobStorageConnection)connection).GetUtcDateTime();
        }

        DateTime windowStart = new DateTime(databaseNow.Ticks - (databaseNow.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddSeconds(-5);
        // The end as a time of day: within half an hour of midnight it wraps past it, as a window may
        storage.SaveSchedulePolicy(new SchedulePolicy("open-now").AllowBetween(windowStart.TimeOfDay, windowStart.AddMinutes(30).TimeOfDay));
        string jobId = new BackgroundJobClient(storage).Schedule(() => Answer(), new DateTimeOffset(windowStart), new ScheduleOptions { Policy = "open-now" });

        TimeProvider original = JobClock.TimeProvider;
        JobClock.TimeProvider = new OffsetClock(TimeSpan.FromSeconds(-30));
        try
        {
            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromSeconds(5) }, storage))
            {
                await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            JobClock.TimeProvider = original;
        }

        int moves = storage.GetMonitoringApi().JobDetails(jobId).History
            .Count(static x => x.Reason?.StartsWith("Moved by policy", StringComparison.Ordinal) == true);
        Assert.InRange(moves, 0, 2);
    }

    [Fact, CleanDatabase]
    public async Task RunWindow_JudgesByTheStorageClock_SoAProcessClockBehindItDoesNotPutTheJobBackAgainAndAgain()
    {
        SqlServerStorage storage = CreateStorage();
        DateTime databaseNow;
        using (IStorageConnection connection = storage.GetConnection())
        {
            databaseNow = ((JobStorageConnection)connection).GetUtcDateTime();
        }

        DateTime windowStart = new DateTime(databaseNow.Ticks - (databaseNow.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddSeconds(-5);
        RunWindowAttribute window = new($"{windowStart:HH:mm:ss}-{windowStart.AddMinutes(30):HH:mm:ss}");
        string jobId = new BackgroundJobClient(storage).Enqueue(() => Answer());

        TimeProvider original = JobClock.TimeProvider;
        GlobalJobFilters.Filters.Add(window);
        JobClock.TimeProvider = new OffsetClock(TimeSpan.FromSeconds(-30));
        try
        {
            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromSeconds(5) }, storage))
            {
                await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            JobClock.TimeProvider = original;
            GlobalJobFilters.Filters.Remove(window);
        }

        IList<StateHistoryDto> history = storage.GetMonitoringApi().JobDetails(jobId).History;
        Assert.InRange(history.Count(static x => x.Reason?.StartsWith("Outside the run window", StringComparison.Ordinal) == true), 0, 1);
        Assert.Equal(SucceededState.StateName, history[0].StateName);
    }

    [Fact, CleanDatabase]
    public async Task DueJob_Runs_WhenEnqueuedByHand_DespiteThePolicy()
    {
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(BlockTodayAndTomorrow("closed"));
        BackgroundJobClient client = new(storage);
        string jobId = client.Schedule(() => Answer(), DayAfterTomorrow, new ScheduleOptions { Policy = "closed" });

        Assert.True(client.Requeue(jobId));

        JobStateInfo state;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
        {
            state = await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SucceededState.StateName, state.Name);
    }

    [Fact, CleanDatabase]
    public void RecurringJob_SkipsTheOccurrencesItsPolicyBlocks_AndIsRescheduledWhenThePolicyChanges()
    {
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(BlockTodayAndTomorrow("closed"));
        RecurringJobManager manager = new(storage);

        manager.AddOrUpdate("minutely", Job.FromExpression(() => Answer()), "* * * * *", new RecurringJobOptions { Policy = "closed" });

        RecurringJobDto blocked = Find(storage, "minutely");
        Assert.Equal("closed", blocked.Policy);
        Assert.Equal(DayAfterTomorrow, blocked.NextExecution);
        Assert.Equal(DayAfterTomorrow, storage.GetNextOccurrences("minutely", 1)[0]);
        Assert.Throws<InvalidOperationException>(() => storage.DeleteSchedulePolicy("closed", 1));

        storage.SaveSchedulePolicy(new SchedulePolicy("closed"));

        RecurringJobDto open = Find(storage, "minutely");
        Assert.InRange(open.NextExecution!.Value, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact, CleanDatabase]
    public void RecurringJobSet_SavesItsPolicies_BeforeTheJobsThatFollowThem()
    {
        SqlServerStorage storage = CreateStorage();

        RecurringJobSetResult result = new RecurringJobSet()
            .AddPolicy(BlockTodayAndTomorrow("closed").Require("base"))
            .AddPolicy(new SchedulePolicy("base"))
            .Add("report", () => Answer(), "0 * * * *", new RecurringJobOptions { Policy = "closed" })
            .Apply(new RecurringJobManager(storage), storage);

        Assert.Equal(["base", "closed"], result.PoliciesSaved);
        Assert.Equal(DayAfterTomorrow, Find(storage, "report").NextExecution);

        RecurringJobSetResult again = new RecurringJobSet()
            .AddPolicy(BlockTodayAndTomorrow("closed").Require("base"))
            .AddPolicy(new SchedulePolicy("base"))
            .Add("report", () => Answer(), "0 * * * *", new RecurringJobOptions { Policy = "closed" })
            .Apply(new RecurringJobManager(storage), storage);

        Assert.Empty(again.PoliciesSaved);
    }

    public static int Answer() => 42;

    private SchedulePolicy BlockTodayAndTomorrow(string name)
    {
        return new SchedulePolicy(name).ExcludeDate(Today).ExcludeDate(Today.AddDays(1));
    }

    private static async Task<StateData> WaitForStateAsync(SqlServerStorage storage, string jobId, Func<StateData, bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using (IStorageConnection connection = storage.GetConnection())
            {
                StateData state = connection.GetStateData(jobId);
                if (state != null && condition(state))
                {
                    return state;
                }
            }

            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException($"Job {jobId} didn't reach the expected state.");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static RecurringJobDto Find(SqlServerStorage storage, string id)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetRecurringJobs([id]).Single();
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }

    [Fact, CleanDatabase]
    public async Task DueJob_WaitsWhileItsDateProviderThrows_AndRunsOnceTheProviderWorks()
    {
        FlakyDates provider = new() { Down = true };
        GlobalConfiguration.Configuration.UseScheduleDateProvider("flaky-dates", provider);
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(new SchedulePolicy("flaky-days").ExcludeDatesFrom("flaky-dates"));
        BackgroundJobClient client = new(storage);

        // Skip must not delete it, at creation or when it is due: the policy blocks nothing, it can't be evaluated
        string jobId = client.Schedule(
            () => Answer(),
            TimeSpan.FromSeconds(1),
            new ScheduleOptions { Policy = "flaky-days", WhenBlocked = WhenBlocked.Skip });
        Assert.NotNull(jobId);

        JobStateInfo final;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage))
        {
            StateData waiting = await WaitForStateAsync(storage, jobId, static x => x.Reason?.StartsWith("Waiting for policy", StringComparison.Ordinal) == true);

            Assert.Equal(ScheduledState.StateName, waiting.Name);
            Assert.StartsWith(
                "Waiting for policy, next check in 1 min: policy 'flaky-days': date provider 'flaky-dates' failed",
                waiting.Reason,
                StringComparison.Ordinal);
            Assert.InRange(
                JobHelper.DeserializeDateTime(waiting.Data["EnqueueAt"]),
                DateTime.UtcNow.AddSeconds(20),
                DateTime.UtcNow.AddSeconds(70));

            // Written once the state is committed, so it may come a moment after the state
            await WaitForParameterAsync(storage, jobId, SchedulePolicyFilter.CheckAttemptsParameter, static x => x == "1");

            // The provider works again. The next check is a minute away, so the job is made due now
            provider.Down = false;
            Assert.True(client.Reschedule(jobId, TimeSpan.FromSeconds(1), ScheduledState.StateName));

            final = await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SucceededState.StateName, final.Name);

        // The policy was evaluated and let the job run, so no check failed in a row any more: the zero that stays
        // records that the job waited
        await WaitForParameterAsync(storage, jobId, SchedulePolicyFilter.CheckAttemptsParameter, static x => x == "0");
    }

    [Fact, CleanDatabase]
    public async Task DueJob_SkippedAsLateAfterItWaitedForItsPolicy_SaysSoInItsReason()
    {
        FlakyDates provider = new() { Down = true };
        GlobalConfiguration.Configuration.UseScheduleDateProvider("late-dates", provider);
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(new SchedulePolicy("late-days").ExcludeDatesFrom("late-dates"));
        BackgroundJobClient client = new(storage);

        string jobId = client.Schedule(
            () => Answer(),
            TimeSpan.FromSeconds(1),
            new ScheduleOptions { Policy = "late-days", SkipAfter = TimeSpan.FromSeconds(1) });
        Assert.NotNull(jobId);

        JobStateInfo final;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage))
        {
            await WaitForStateAsync(storage, jobId, static x => x.Reason?.StartsWith("Waiting for policy", StringComparison.Ordinal) == true);
            await WaitForParameterAsync(storage, jobId, SchedulePolicyFilter.CheckAttemptsParameter, static x => x == "1");

            // The provider works again only when the job is later than its own window allows
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            provider.Down = false;
            Assert.True(client.Reschedule(jobId, TimeSpan.FromSeconds(1), ScheduledState.StateName));

            final = await storage.WaitForCompletionAsync(jobId, Timeout, TestContext.Current.CancellationToken);
        }

        // Its own window decides, so it is skipped, but the reason names what made it late
        Assert.Equal(DeletedState.StateName, final.Name);
        Assert.Matches("^Skipped: started [0-9.,]+ s late: waited for policy 'late-days', which couldn't be evaluated$", final.Reason);
        await WaitForParameterAsync(storage, jobId, SchedulePolicyFilter.CheckAttemptsParameter, static x => x == "0");
    }

    [Fact, CleanDatabase]
    public async Task RecurringJob_OccurrencesSkippedAsLateAfterTheJobWaitedForItsPolicy_SaySoInTheirReason()
    {
        FlakyDates provider = new() { Down = true };
        GlobalConfiguration.Configuration.UseScheduleDateProvider("report-dates", provider);
        SqlServerStorage storage = CreateStorage();
        storage.SaveSchedulePolicy(new SchedulePolicy("report-days").ExcludeDatesFrom("report-dates"));

        // Registered ten minutes ago, so its first occurrences are long due when the policy can be evaluated at last.
        // Strict: each missed occurrence runs, and one that starts more than a minute late is skipped
        DateTime registeredAt = DateTime.UtcNow.AddMinutes(-10);
        RecurringJobManager manager = new(storage, JobFilterProviders.Providers, new DefaultTimeZoneResolver(), () => registeredAt);
        manager.AddOrUpdate(
            "late-report",
            Job.FromExpression(() => Answer()),
            "* * * * *",
            new RecurringJobOptions { Policy = "report-days", MisfireHandling = MisfireHandlingMode.Strict, SkipAfter = TimeSpan.FromMinutes(1) });
        DateTime firstOccurrence = Find(storage, "late-report").NextExecution!.Value;

        List<RecurringJobRun> runs;
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage))
        {
            // The scheduler has looked at the job, couldn't evaluate its policy, and looks again later
            await WaitForRecurringJobAsync(storage, "late-report", x => x.NextExecution > firstOccurrence && x.Error != null);
            Assert.Null(Find(storage, "late-report").LastExecution);

            provider.Down = false;

            // Every occurrence of the last ten minutes is created now
            await WaitForRecurringJobAsync(storage, "late-report", static x => x.LastExecution > DateTime.UtcNow.AddMinutes(-1) && x.Error == null);
            runs = [.. storage.GetRecurringJobRuns("late-report")];
            foreach (RecurringJobRun run in runs)
            {
                await storage.WaitForCompletionAsync(run.JobId, Timeout, TestContext.Current.CancellationToken);
            }
        }

        // Their own window decides, so the late ones are skipped, but the reason names what made them late
        List<RecurringJobRun> late = [.. storage.GetRecurringJobRuns("late-report").Where(static x => x.ScheduledFor < DateTime.UtcNow.AddMinutes(-2))];
        Assert.InRange(runs.Count, 9, 11);
        Assert.InRange(late.Count, 7, 10);
        Assert.All(late, static run => Assert.Equal(DeletedState.StateName, run.State));
        Assert.All(late, static run => Assert.Matches(
            "^Skipped: started [0-9.,]+ min late: waited for policy 'report-days', which couldn't be evaluated$",
            run.Reason));
    }

    private static async Task WaitForRecurringJobAsync(SqlServerStorage storage, string id, Func<RecurringJobDto, bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition(Find(storage, id)))
        {
            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException($"Recurring job {id} didn't reach the expected state.");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static async Task WaitForParameterAsync(SqlServerStorage storage, string jobId, string name, Func<string, bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            string value;
            using (IStorageConnection connection = storage.GetConnection())
            {
                value = connection.GetJobParameter(jobId, name);
            }

            if (condition(value))
            {
                return;
            }

            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException($"Parameter {name} of job {jobId} is '{value}', not the expected value.");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    private sealed class FlakyDates : IScheduleDateProvider
    {
        private volatile bool _down;

        public bool Down
        {
            get => _down;
            set => _down = value;
        }

        public bool Contains(DateOnly day) => _down ? throw new InvalidOperationException("holiday database down") : false;
    }
}
