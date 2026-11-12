using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Chains, failure and processing continuations, the new fan-in joins and continuation parents, through real storage
/// and a real server.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class JobChainFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<string> Log = new();
    private static readonly ConcurrentDictionary<string, string> Parents = new();
    private static int _failures;

    [Fact, CleanDatabase]
    public async Task Chain_RunsItsStepsInOrder_AndTheSuccessJoin()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();

        JobChainResult chain = JobChain.StartWith(() => Record("a"))
            .Then(() => Record("b"))
            .ThenMany(branches => branches.Branch(() => Record("c")).Branch(() => Record("d")))
            .ThenAfterAll(() => Record("done"))
            .ThenAfterAnyFailed(() => Record("alert"))
            .Enqueue(new BackgroundJobClient(storage));

        Assert.NotNull(chain);
        using (CreateServer(storage))
        {
            await WaitUntilSettledAsync(storage, chain.ChainId);
        }

        List<string> log = [.. Log];
        Assert.Equal(["a", "b"], log.Take(2));
        Assert.Equal(["c", "d"], log.Skip(2).Take(2).Order(StringComparer.Ordinal));
        Assert.Equal("done", log[4]);
        Assert.DoesNotContain("alert", log);

        JobChainInfo info = storage.GetChain(chain.ChainId);
        Assert.True(info?.IsSettled);
        Assert.Equal(DeletedState.StateName, info?.Steps.Single(static x => x.Key == "3.1").State);
    }

    [Fact, CleanDatabase]
    public async Task Chain_TakesTheFailurePath_AndCanBeRetriedFromTheFailedStep()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        _failures = 1;

        JobChainResult chain = JobChain.StartWith(() => Record("a"))
            .ThenMany(branches => branches.Branch(() => FailOnce("b")).Branch(() => Record("c")))
            .ThenAfterAll(() => Record("done"))
            .ThenAfterAnyFailed(() => Record("alert"))
            .Enqueue(new BackgroundJobClient(storage));

        using (CreateServer(storage))
        {
            await WaitUntilSettledAsync(storage, chain!.ChainId);

            Assert.Contains("alert", Log);
            Assert.DoesNotContain("done", Log);

            Log.Clear();
            JobChainResult retried = storage.RetryChainFrom(chain.Steps["1.0"]);

            Assert.Equal(chain.Steps["1.0"], retried.Steps["1.0"]);
            Assert.Equal(chain.Steps["1.1"], retried.Steps["1.1"]);
            Assert.NotEqual(chain.Steps["2.0"], retried.Steps["2.0"]);
            await WaitUntilSettledAsync(storage, chain.ChainId);
        }

        Assert.Equal(["b", "done"], Log.Order(StringComparer.Ordinal));
        Assert.Equal(2, storage.GetChain(chain.ChainId)?.Generation);
    }

    [Fact, CleanDatabase]
    public void RetryChainFrom_Refuses_WhileTheChainIsRunning_OrForAJobOutsideAChain()
    {
        SqlServerStorage storage = CreateStorage();
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Enqueue(new BackgroundJobClient(storage));
        string plain = new BackgroundJobClient(storage).Enqueue(() => Record("x"));

        Assert.Throws<InvalidOperationException>(() => storage.RetryChainFrom(chain!.RootId));
        Assert.Throws<InvalidOperationException>(() => storage.RetryChainFrom(plain));
    }

    [Fact, CleanDatabase]
    public void DeleteChain_DeletesTheStepsThatHaveNotFinished_AndTheRecord()
    {
        SqlServerStorage storage = CreateStorage();
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c"))
            .Schedule(new BackgroundJobClient(storage), TimeSpan.FromHours(1));

        Assert.Equal(3, storage.DeleteChain(chain!.ChainId));

        using IStorageConnection connection = storage.GetConnection();
        Assert.All(chain.Steps.Values, id => Assert.Equal(DeletedState.StateName, connection.GetStateData(id).Name));
        Assert.Null(storage.GetChain(chain.ChainId));
        Assert.Equal(-1, storage.DeleteChain(chain.ChainId));
    }

    [Fact, CleanDatabase]
    public void ReplaceChain_ReplacesAChainThatHasNotStarted_KeepingItsTime()
    {
        SqlServerStorage storage = CreateStorage();
        DateTimeOffset at = DateTimeOffset.UtcNow.AddHours(3);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(new BackgroundJobClient(storage), at);

        JobChainResult replaced = storage.ReplaceChain(chain!.ChainId, JobChain.StartWith(() => Record("x")).Then(() => Record("y")).Then(() => Record("z")));

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(chain.ChainId, replaced.ChainId);
        Assert.Equal(3, replaced.Steps.Count);
        Assert.Equal(DeletedState.StateName, connection.GetStateData(chain.RootId).Name);
        StateData root = connection.GetStateData(replaced.RootId);
        Assert.Equal(ScheduledState.StateName, root.Name);
        Assert.Equal(JobHelper.SerializeDateTime(at.UtcDateTime), root.Data["EnqueueAt"]);
    }

    [Fact, CleanDatabase]
    public async Task ReplaceChain_Refuses_OnceTheChainStarted()
    {
        SqlServerStorage storage = CreateStorage();
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Enqueue(new BackgroundJobClient(storage));

        using (CreateServer(storage))
        {
            await WaitUntilSettledAsync(storage, chain!.ChainId);
        }

        Assert.Throws<InvalidOperationException>(() => storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Record("x"))));
    }

    [Fact, CleanDatabase]
    public async Task FailureContinuation_StartsWhenTheParentFailsForGood_AndTheSuccessOneKeepsWaiting()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        _failures = 1;
        BackgroundJobClient client = new(storage);

        string parent = client.Enqueue(() => FailOnce("parent"));
        string onFailure = client.ContinueJobWith(parent, () => RecordParent("on-failure", null!), JobContinuationOptions.OnlyOnFailedState);
        string onSuccess = client.ContinueJobWith(parent, () => Record("on-success"));

        using (CreateServer(storage))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(onFailure, Timeout, TestContext.Current.CancellationToken)).Name);
        }

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(FailedState.StateName, connection.GetStateData(parent).Name);
        Assert.Equal(AwaitingState.StateName, connection.GetStateData(onSuccess).Name);
        Assert.Equal(parent, Parents["on-failure"]);
    }

    [Fact, CleanDatabase]
    public async Task ProcessingContinuation_StartsWhileTheParentRuns()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);

        string parent = client.Enqueue(() => WaitForSibling());
        string sibling = client.ContinueJobWith(parent, () => Record("sibling"), JobContinuationOptions.OnlyOnProcessingState);

        using (CreateServer(storage, workers: 2))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(parent, Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(sibling, Timeout, TestContext.Current.CancellationToken)).Name);
        }
    }

    [Fact, CleanDatabase]
    public async Task RecurringChain_CreatesAChainAtEachTrigger()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdateChain("etl", JobChain.StartWith(() => Record("extract")).Then(() => Record("load")), "0 0 1 1 *", skipWhileRunning: true);

        string first = manager.TriggerJob("etl");
        using (CreateServer(storage))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(first, Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.True(storage.TryGetResult(first, out string chainId));
            await WaitUntilSettledAsync(storage, chainId);
        }

        Assert.Equal(["extract", "load"], Log);
    }

    [Fact, CleanDatabase]
    public async Task Chain_CreatedWhileAServerRuns_StartsAsSoonAsItIsRecorded()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();

        // The slow polling of a real server: a first step held until the chain is recorded would wait for it,
        // were it not enqueued by the client that records the chain
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = 2, SchedulePollingInterval = TimeSpan.FromMinutes(1) }, storage);

        for (int i = 0; i < 5; i++)
        {
            JobChainResult chain = JobChain.StartWith(() => Record("a"))
                .Then(() => Record("b"))
                .ThenMany(branches => branches.Branch(() => Record("c")).Branch(() => Record("d")))
                .ThenAfterAll(() => Record("done"))
                .Enqueue(new BackgroundJobClient(storage));

            await WaitUntilSettledAsync(storage, chain!.ChainId);

            // Only the first step can be taken before the chain is recorded
            Assert.All(
                chain.Steps.Where(static x => x.Key != "0.0"),
                step => Assert.DoesNotContain(storage.GetMonitoringApi().JobDetails(step.Value).History, static x => x.StateName == ScheduledState.StateName));
        }

        Assert.Equal(25, Log.Count);
    }

    [Fact, CleanDatabase]
    public async Task Chain_WhoseCreationStoppedHalfway_HasItsFirstStepHeld_ThenDeleted_AndNoneOfItsStepsRuns()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        ChainHooks hooks = new() { CrashOnCreating = "c" };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            // The client stops for good after creating two of three steps: the record it started lists none
            Assert.Throws<OutOfMemoryException>(
                () => JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c")).Enqueue(new BackgroundJobClient(storage)));

            string first = hooks.Created.Single(static x => x.Name == "a").Id;
            string second = hooks.Created.Single(static x => x.Name == "b").Id;
            string chainId;
            using (IStorageConnection connection = storage.GetConnection())
            {
                chainId = connection.GetJobParameter(first, JobChains.ChainIdParameter).Trim('"');
            }

            using (CreateServer(storage))
            {
                // While the client's lease lasts, by the clock of the database, the first step waits
                StateData held = await WaitForStateAsync(storage, first, static x => x.Name == ScheduledState.StateName);
                Assert.Equal($"Waiting for chain {chainId} to be recorded", held.Reason);
                Assert.Empty(Log);

                // As if the two minutes of the lease had passed
                LapseLease(storage, chainId);

                Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(first, Timeout, TestContext.Current.CancellationToken)).Name);
                Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(second, Timeout, TestContext.Current.CancellationToken)).Name);
            }

            using IStorageConnection reader = storage.GetConnection();
            Assert.Equal($"Chain {chainId} isn't recorded: it was deleted or never completely created", reader.GetStateData(first).Reason);
            Assert.DoesNotContain(chainId, reader.GetAllItemsFromSet(JobChains.SetKey));
            Assert.InRange(((JobStorageConnection)reader).GetHashTtl(JobChains.GetKey(chainId)), TimeSpan.Zero, TimeSpan.FromDays(1));
            Assert.Empty(Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task Job_ThatTheRecordOfItsChainDoesNotList_NeverRuns()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Enqueue(client);

        // Left behind by something that stopped halfway
        string orphan = client.Create(
            Job.FromExpression(() => Record("orphan")),
            new EnqueuedState(),
            new Dictionary<string, object> { [JobChains.ChainIdParameter] = chain!.ChainId, [JobChains.ChainStepParameter] = "1.0" });

        using (CreateServer(storage))
        {
            Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(orphan, Timeout, TestContext.Current.CancellationToken)).Name);
            await WaitUntilSettledAsync(storage, chain.ChainId);
        }

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal($"Not a current step of chain {chain.ChainId}", connection.GetStateData(orphan).Reason);
        Assert.Equal(["a", "b"], Log);
    }

    [Theory, CleanDatabase]
    [InlineData("x")]
    [InlineData("y")]
    public async Task ReplaceChain_ThatStopsBeforeItIsRecorded_LeavesTheOldChainToRunAtItsTime_AndNothingOfTheNewOneRuns(string stopsAt)
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        DateTimeOffset due = DateTimeOffset.UtcNow.AddDays(30);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(client, due);
        ChainHooks hooks = new() { CrashOnCreating = stopsAt };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            // The process stops while it replaces the chain: nothing is rolled back
            Assert.Throws<OutOfMemoryException>(() => storage.ReplaceChain(chain!.ChainId, JobChain.StartWith(() => Record("x")).Then(() => Record("y"))));

            // The old first step wasn't touched: still scheduled for next month, and still what the record lists
            using (IStorageConnection connection = storage.GetConnection())
            {
                StateData root = connection.GetStateData(chain.RootId);
                Assert.Equal(ScheduledState.StateName, root.Name);
                Assert.Equal(JobHelper.SerializeDateTime(due.UtcDateTime), root.Data["EnqueueAt"]);
            }

            Assert.Equal(chain.Steps.Values, storage.GetChain(chain.ChainId).Steps.Select(static x => x.JobId));

            // As if the lease of the process that stopped had lapsed, and the old chain were due
            LapseLease(storage, chain.ChainId);
            Assert.True(client.ChangeState(chain.RootId, new EnqueuedState(), ScheduledState.StateName));
            foreach ((string _, string id) in hooks.Created)
            {
                client.ChangeState(id, new EnqueuedState(), ScheduledState.StateName);
            }

            using (CreateServer(storage))
            {
                await WaitUntilSettledAsync(storage, chain.ChainId);
                foreach ((string _, string id) in hooks.Created)
                {
                    Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(id, Timeout, TestContext.Current.CancellationToken)).Name);
                }
            }

            Assert.Equal(["a", "b"], Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task RetryChainFrom_ThatStopsBeforeItIsRecorded_RunsTheStepAgain_WithTheStepsThatFollowedItBefore()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        // The second step doesn't retry by itself, so the failure given to it below is final
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => RecordWithoutRetries("b")).Then(() => Record("c")).Then(() => Record("d")).Enqueue(client);
        Assert.True(client.ChangeState(chain!.Steps["0.0"], new SucceededState(null, 0, 0), null));
        Assert.True(client.ChangeState(chain.Steps["1.0"], new FailedState(new InvalidOperationException("failed")), null));
        ChainHooks hooks = new() { CrashOnCreating = "d" };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            // The process stops after it created the first of the new steps: nothing is rolled back
            Assert.Throws<OutOfMemoryException>(() => storage.RetryChainFrom(chain.Steps["1.0"]));

            using (IStorageConnection connection = storage.GetConnection())
            {
                StateData held = connection.GetStateData(chain.Steps["1.0"]);
                Assert.Equal(ScheduledState.StateName, held.Name);
                Assert.InRange(JobHelper.DeserializeDateTime(held.Data["EnqueueAt"]) - DateTime.UtcNow, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5));
            }

            Assert.Equal(chain.Steps.Values, storage.GetChain(chain.ChainId).Steps.Select(static x => x.JobId));

            // As if the lease of the process that stopped had lapsed, and the hold of the step had ended
            LapseLease(storage, chain.ChainId);
            Assert.True(client.ChangeState(chain.Steps["1.0"], new EnqueuedState(), ScheduledState.StateName));

            using (CreateServer(storage))
            {
                await WaitUntilSettledAsync(storage, chain.ChainId);
                string created = hooks.Created.Single(static x => x.Name == "c").Id;
                Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(created, Timeout, TestContext.Current.CancellationToken)).Name);
            }

            // The step ran again, once, and the steps the record still lists after it followed
            Assert.Equal(["b", "c", "d"], Log);
            using IStorageConnection reader = storage.GetConnection();
            Assert.All(chain.Steps.Values, id => Assert.Equal(SucceededState.StateName, reader.GetStateData(id).Name));
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task Join_ThatWasNeverToldToStart_IsStartedByAServer_AndTheChainIsNotUnsettledMeanwhile()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        JobChainResult chain = JobChain.StartWith(() => Record("a"))
            .ThenMany(branches => branches.Branch(() => Record("c")).Branch(() => Record("d")))
            .ThenAfterAll(() => Record("done"))
            .Enqueue(new BackgroundJobClient(storage));
        ChainHooks hooks = new() { FailStartOf = "done" };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            // Both branches finish, and the step after them can't be started: it stays where it is
            using (CreateServer(storage, workers: 2))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (hooks.FailedStarts < 3)
                {
                    if (stopwatch.Elapsed > Timeout)
                    {
                        throw new TimeoutException("The start of the join didn't fail.");
                    }

                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
            }

            hooks.FailStartOf = null;
            using (IStorageConnection connection = storage.GetConnection())
            {
                Assert.Equal(AwaitingState.StateName, connection.GetStateData(chain!.Steps["2.0"]).Name);
            }

            Assert.False(storage.GetChain(chain.ChainId).IsSettled);

            // Later nobody is about to start it any more: the chain counts as settled rather than running for good,
            // and a server finds the step and starts it
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(6));
            Assert.True(storage.GetChain(chain.ChainId).IsSettled);

            using (CreateServer(storage))
            {
                Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(chain.Steps["2.0"], Timeout, TestContext.Current.CancellationToken)).Name);
            }

            Assert.Equal(1, Log.Count(static x => x == "done"));
        }
        finally
        {
            JobClock.TimeProvider = TimeProvider.System;
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task Job_HeldInAQueue_KeepsIt_WhenItsSchedulePolicyBlocksItAgain()
    {
        // A queue given only by the Enqueued state is remembered when a policy moves the job forward, and must
        // still be when the policy moves it forward a second time
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        storage.SaveSchedulePolicy(new SchedulePolicy("closed").ExcludeDate(today).ExcludeDate(today.AddDays(1)));

        string jobId = client.Create(
            Job.FromExpression(() => Record("queued")),
            new EnqueuedState("critical"),
            new Dictionary<string, object> { [ScheduleOptions.PolicyParameter] = "closed" });

        using (IStorageConnection connection = storage.GetConnection())
        {
            Assert.Equal(ScheduledState.StateName, connection.GetStateData(jobId).Name);
            Assert.Equal("\"critical\"", connection.GetJobParameter(jobId, "HeldQueue"));
        }

        // The server listens to the default queue only
        using (CreateServer(storage))
        {
            // Due while the policy still blocks: moved forward again
            Assert.True(client.ChangeState(jobId, new ScheduledState(TimeSpan.FromMilliseconds(100)), ScheduledState.StateName));
            await WaitForStateAsync(storage, jobId, static x => x.Reason?.StartsWith("Moved by policy", StringComparison.Ordinal) == true);

            using (IStorageConnection connection = storage.GetConnection())
            {
                Assert.Equal("\"critical\"", connection.GetJobParameter(jobId, "HeldQueue"));
            }

            // Due once the policy allows it: enqueued where it was to go
            storage.SaveSchedulePolicy(new SchedulePolicy("closed"));
            Assert.True(client.ChangeState(jobId, new ScheduledState(TimeSpan.FromMilliseconds(100)), ScheduledState.StateName));
            StateData enqueued = await WaitForStateAsync(storage, jobId, static x => x.Name == EnqueuedState.StateName);

            Assert.Equal("critical", enqueued.Data["Queue"]);
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Empty(Log);
    }

    [Fact, CleanDatabase]
    public void Record_IsKept_UntilLongAfterAScheduledFirstStepIsDue_AndLongerWhenAStepChangesState()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);

        // The record's life is set from this process's clock and read back against SQL Server's: they differ a little
        TimeSpan tolerance = TimeSpan.FromSeconds(5);
        JobChainResult scheduled = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(client, TimeSpan.FromDays(60));
        JobChainResult enqueued = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Enqueue(client);

        Assert.InRange(RecordTimeToLive(storage, scheduled!.ChainId), TimeSpan.FromDays(89.9), TimeSpan.FromDays(90) + tolerance);
        Assert.InRange(RecordTimeToLive(storage, enqueued!.ChainId), TimeSpan.FromDays(29.9), TimeSpan.FromDays(30) + tolerance);

        try
        {
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(20));
            Assert.InRange(RecordTimeToLive(storage, enqueued.ChainId), TimeSpan.FromDays(9.9), TimeSpan.FromDays(10) + tolerance);

            Assert.True(client.ChangeState(enqueued.RootId, new SucceededState(null, 0, 0), null));
            Assert.InRange(RecordTimeToLive(storage, enqueued.ChainId), TimeSpan.FromDays(29.9), TimeSpan.FromDays(30) + tolerance);

            // A step of the chain scheduled far ahead changes state: its record keeps its longer life
            Assert.True(client.ChangeState(scheduled.Steps["1.0"], new DeletedState(), null));
            Assert.InRange(RecordTimeToLive(storage, scheduled.ChainId), TimeSpan.FromDays(69.9), TimeSpan.FromDays(70) + tolerance);
        }
        finally
        {
            JobClock.TimeProvider = TimeProvider.System;
        }
    }

    [Fact, CleanDatabase]
    public async Task ReplaceChain_Succeeds_WhenTheOldStepsCanNotBeDeleted_AndAServerFinishesItLater()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(client, TimeSpan.FromHours(1));
        FailingDeletions deletions = new() { FailWithReason = "The chain was replaced" };
        GlobalJobFilters.Filters.Add(deletions);

        try
        {
            JobChainResult replaced = storage.ReplaceChain(chain!.ChainId, JobChain.StartWith(() => Record("x")).Then(() => Record("y")));

            // The replacement is recorded, with what is left to delete
            Assert.Equal(replaced.Steps.Values, storage.GetChain(chain.ChainId).Steps.Select(static x => x.JobId));
            using (IStorageConnection connection = storage.GetConnection())
            {
                Assert.Equal(ScheduledState.StateName, connection.GetStateData(chain.RootId).Name);
                Assert.Equal(AwaitingState.StateName, connection.GetStateData(chain.Steps["1.0"]).Name);
                Assert.Contains(chain.ChainId, connection.GetAllItemsFromSet("chains:cleanup"));
            }

            deletions.FailWithReason = null;

            using (CreateServer(storage))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (true)
                {
                    using IStorageConnection connection = storage.GetConnection();
                    if (!connection.GetAllItemsFromSet("chains:cleanup").Contains(chain.ChainId))
                    {
                        break;
                    }

                    if (stopwatch.Elapsed > Timeout)
                    {
                        throw new TimeoutException("The replacement wasn't finished.");
                    }

                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
            }

            using IStorageConnection reader = storage.GetConnection();
            Assert.All(chain.Steps.Values, id => Assert.Equal(DeletedState.StateName, reader.GetStateData(id).Name));
            Assert.Equal(string.Empty, reader.GetAllEntriesFromHash(JobChains.GetKey(chain.ChainId))["Cleanup"]);
            Assert.Empty(Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(deletions);
        }
    }

    [Fact, CleanDatabase]
    public async Task Server_RemovesChainsWhoseRecordExpired_FromTheList_PastTheOldOnesStillRecorded()
    {
        SqlServerStorage storage = CreateStorage();
        long old = JobHelper.ToTimestamp(DateTime.UtcNow - JobChain.RecordRetention - TimeSpan.FromDays(10));

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            // More old chains still recorded (retried, scheduled far ahead) than a page of the list holds
            for (int i = 0; i < 120; i++)
            {
                transaction.AddToSet(JobChains.SetKey, $"kept-{i:000}", old + i);
                transaction.SetRangeInHash(JobChains.GetKey($"kept-{i:000}"), new Dictionary<string, string>
                {
                    ["Jobs"] = "{}",
                    ["CreatedAt"] = JobHelper.SerializeDateTime(JobHelper.FromTimestamp(old + i)),
                });
            }

            for (int i = 0; i < 20; i++)
            {
                transaction.AddToSet(JobChains.SetKey, $"expired-{i:00}", old + 1000 + i);
            }

            transaction.Commit();
        }

        JobChainResult chain = JobChain.StartWith(() => Record("a")).Schedule(new BackgroundJobClient(storage), TimeSpan.FromHours(1));

        using (CreateServer(storage))
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                using IStorageConnection connection = storage.GetConnection();
                if (!connection.GetAllItemsFromSet(JobChains.SetKey).Any(static x => x.StartsWith("expired", StringComparison.Ordinal)))
                {
                    break;
                }

                if (stopwatch.Elapsed > Timeout)
                {
                    throw new TimeoutException("The expired chains didn't leave the list.");
                }

                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        using IStorageConnection reader = storage.GetConnection();
        HashSet<string> listed = reader.GetAllItemsFromSet(JobChains.SetKey);
        Assert.Equal(121, listed.Count);
        Assert.Contains(chain!.ChainId, listed);
    }

    private static TimeSpan RecordTimeToLive(SqlServerStorage storage, string chainId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return ((JobStorageConnection)connection).GetHashTtl(JobChains.GetKey(chainId));
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

    /// <summary>
    /// Rewrites the lease in a chain's record as one that lapsed long ago, as time would: the lease is told with
    /// the clock of the database, which a test can't move.
    /// </summary>
    private static void LapseLease(SqlServerStorage storage, string chainId)
    {
        using IStorageConnection connection = storage.GetConnection();
        string jobs = connection.GetAllEntriesFromHash(JobChains.GetKey(chainId))["Jobs"];

        int start = jobs.IndexOf("\"changing\":\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The chain's record carries no lease.");
        int valueStart = start + "\"changing\":\"".Length;
        int valueEnd = jobs.IndexOf('"', valueStart);
        string lapsed = string.Concat(jobs.AsSpan(0, valueStart), "2000-01-01T00:00:00.0000000Z/2000-01-01T00:02:00.0000000Z", jobs.AsSpan(valueEnd));

        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(JobChains.GetKey(chainId), new Dictionary<string, string> { ["Jobs"] = lapsed });
        transaction.Commit();
    }

    /// <summary>
    /// What a test makes happen while a chain is created or changed: the process stopping at the creation of a
    /// step (an exception no rollback catches), or the start of a waiting step failing.
    /// </summary>
    private sealed class ChainHooks : IClientFilter, IElectStateFilter
    {
        private int _failedStarts;

        public string CrashOnCreating { get; set; }
        public string FailStartOf { get; set; }
        public int FailedStarts => Volatile.Read(ref _failedStarts);
        public ConcurrentQueue<(string Name, string Id)> Created { get; } = new();

        public void OnCreating(CreatingContext context)
        {
            if (CrashOnCreating != null && context.Job.Type == typeof(JobChainFacts) && (string)context.Job.Args[0] == CrashOnCreating)
            {
                throw new OutOfMemoryException("The process stops here.");
            }
        }

        public void OnCreated(CreatedContext context)
        {
            if (CrashOnCreating != null && context.Job.Type == typeof(JobChainFacts) && context.BackgroundJob != null)
            {
                Created.Enqueue(((string)context.Job.Args[0], context.BackgroundJob.Id));
            }
        }

        public void OnStateElection(ElectStateContext context)
        {
            if (FailStartOf != null && context.CandidateState is EnqueuedState && context.CurrentState == AwaitingState.StateName &&
                context.BackgroundJob.Job?.Type == typeof(JobChainFacts) && (string)context.BackgroundJob.Job.Args[0] == FailStartOf)
            {
                Interlocked.Increment(ref _failedStarts);
                throw new InvalidOperationException("The storage is down.");
            }
        }
    }

    /// <summary>The system clock, moved by a fixed time.</summary>
    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    /// <summary>Makes the deletions with a given reason fail, as a storage error at that moment would.</summary>
    private sealed class FailingDeletions : IElectStateFilter
    {
        public string FailWithReason { get; set; }

        public void OnStateElection(ElectStateContext context)
        {
            if (FailWithReason != null && context.CandidateState is DeletedState deleted && deleted.Reason == FailWithReason)
            {
                throw new InvalidOperationException("The storage is down.");
            }
        }
    }

    public static void Record(string name)
    {
        Log.Enqueue(name);
    }

    [AutomaticRetry(Attempts = 0)]
    public static void RecordWithoutRetries(string name)
    {
        Log.Enqueue(name);
    }

    [AutomaticRetry(Attempts = 0)]
    public static void FailOnce(string name)
    {
        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            throw new InvalidOperationException($"{name} failed");
        }

        Log.Enqueue(name);
    }

    public static void RecordParent(string name, PerformContext context)
    {
        Parents[name] = context.ParentId;
        Log.Enqueue(name);
    }

    public static void WaitForSibling()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!Log.Contains("sibling") && stopwatch.Elapsed < Timeout)
        {
            Thread.Sleep(50);
        }

        if (!Log.Contains("sibling"))
        {
            throw new TimeoutException("The processing continuation didn't start while the parent ran.");
        }
    }

    private static async Task WaitUntilSettledAsync(SqlServerStorage storage, string chainId)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (storage.GetChain(chainId) is not { IsSettled: true })
        {
            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException($"Chain {chainId} didn't settle.");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static BackgroundJobServer CreateServer(SqlServerStorage storage, int workers = 1)
    {
        return new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = workers, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage);
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }
}
