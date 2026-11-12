using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Server;

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
                if (state != null && condition(state)) return state;
            }

            if (stopwatch.Elapsed > Timeout) throw new TimeoutException($"Job {jobId} didn't reach the expected state.");
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

        // The policy was evaluated, so the count of failed checks is gone
        await WaitForParameterAsync(storage, jobId, SchedulePolicyFilter.CheckAttemptsParameter, static x => string.IsNullOrEmpty(x));
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

            if (condition(value)) return;

            if (stopwatch.Elapsed > Timeout) throw new TimeoutException($"Parameter {name} of job {jobId} is '{value}', not the expected value.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
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
