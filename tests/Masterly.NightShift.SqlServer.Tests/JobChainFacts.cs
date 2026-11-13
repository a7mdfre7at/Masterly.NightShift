extern alias ReferencedDapper;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;
using Masterly.NightShift.Server;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Chains, failure and processing continuations, the new fan-in joins and continuation parents, through real storage
/// and a real server.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class JobChainFacts
{
    private const string DueSet = "chains:due";
    private const string ExpirySet = "chains:expiring";
    private const string SoonSet = "chains:soon";

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

        // What the step said about the retry while it was held went when it was let run
        await WaitUntilAsync(() => Parameter(storage, chain.Steps["1.0"], "ChainRetry") == null, "The note of the retry stayed on the step.");
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
    public async Task Chains_CreatedWhileWorkersAreIdle_NeverShowAStepWaitingForItsChain()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);

        // Idle workers fetch a first step within milliseconds of its creation, before the chain is recorded
        using BackgroundJobServer server = CreateServer(storage, workers: 4);
        List<JobChainResult> chains = [];

        for (int i = 0; i < 40; i++)
        {
            chains.Add(JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c")).Enqueue(client));
        }

        foreach (JobChainResult chain in chains)
        {
            await WaitUntilSettledAsync(storage, chain.ChainId);
        }

        Assert.Equal(120, Log.Count);
        IMonitoringApi monitoring = storage.GetMonitoringApi();

        foreach (JobChainResult chain in chains)
        {
            // Not held, not enqueued a second time: the history of any job
            Assert.Equal(
                [EnqueuedState.StateName, ProcessingState.StateName, SucceededState.StateName],
                monitoring.JobDetails(chain.RootId).History.Reverse().Select(static x => x.StateName));
            Assert.All(
                chain.Steps.Values,
                id => Assert.DoesNotContain(monitoring.JobDetails(id).History, static x => x.StateName == ScheduledState.StateName));
            Assert.Null(Parameter(storage, chain.RootId, "StaleRestarts"));
            Assert.Null(Parameter(storage, chain.RootId, "RetryCount"));
        }
    }

    [Theory, CleanDatabase]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chain_WhoseFirstStepAWorkerTakesWhileItIsCreated_StartsWithoutAnyTraceOfIt(bool slidingInvisibilityTimeout)
    {
        // Both ways a worker holds a fetched job: a row marked as fetched, or a transaction that is rolled back
        SqlServerStorage storage = slidingInvisibilityTimeout
            ? CreateStorage()
            : new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, SlidingInvisibilityTimeout = null });
        Log.Clear();
        ChainHooks hooks = new() { PauseOnCreating = "c", WhilePaused = static () => Thread.Sleep(700) };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            using BackgroundJobServer server = CreateServer(storage, workers: 2);

            JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c")).Enqueue(new BackgroundJobClient(storage));
            await WaitUntilSettledAsync(storage, chain.ChainId);

            Assert.Equal(["a", "b", "c"], Log);
            Assert.Equal(
                [EnqueuedState.StateName, ProcessingState.StateName, SucceededState.StateName],
                storage.GetMonitoringApi().JobDetails(chain.RootId).History.Reverse().Select(static x => x.StateName));
            Assert.Null(Parameter(storage, chain.RootId, "StaleRestarts"));
            Assert.Null(Parameter(storage, chain.RootId, "Lease"));
            Assert.Null(Parameter(storage, chain.RootId, "HeldQueue"));
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task Chain_WhoseCreatorTakesUnusuallyLong_HasItsFirstStepHeld_AsBefore_AndStillRuns()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        string waiting = null;
        ChainHooks hooks = new() { PauseOnCreating = "c" };
        hooks.WhilePaused = () =>
        {
            // Seconds pass, by the clock of the database: the first step stops going back to its queue
            string first = hooks.Created.Single(static x => x.Name == "a").Id;
            Thread.Sleep(1000);
            Assert.Equal(EnqueuedState.StateName, StateName(storage, first));
            WaitForState(storage, first, ScheduledState.StateName);
            using IStorageConnection connection = storage.GetConnection();
            waiting = connection.GetStateData(first).Reason;
        };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            using BackgroundJobServer server = CreateServer(storage, workers: 2);

            JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c")).Enqueue(new BackgroundJobClient(storage));
            await WaitUntilSettledAsync(storage, chain.ChainId);

            Assert.Equal($"Waiting for chain {chain.ChainId} to be recorded", waiting);
            Assert.Equal(["a", "b", "c"], Log);
            Assert.Contains(
                storage.GetMonitoringApi().JobDetails(chain.RootId).History,
                static x => x.StateName == EnqueuedState.StateName && x.Reason == "The chain is recorded");
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
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

            // What the client started of the record is kept as long as a record would be (set from this process's
            // clock, read against SQL Server's), and neither job is marked as a step that was recorded
            Assert.InRange(
                ((JobStorageConnection)reader).GetHashTtl(JobChains.GetKey(chainId)),
                TimeSpan.FromDays(29.9),
                JobChain.RecordRetention + TimeSpan.FromSeconds(5));
            Assert.Null(reader.GetJobParameter(first, JobChains.ChainRecordedParameter));
            Assert.Null(reader.GetJobParameter(second, JobChains.ChainRecordedParameter));
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
    public async Task RetryChainFrom_ThatStopsBeforeItIsRecorded_PutsItsStepBackAsItWas_AndRunsNothing_WithTheRealScheduler()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        // The second step doesn't retry by itself, so the failure given to it below is final
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => RecordWithoutRetries("b")).Then(() => Record("c")).Then(() => Record("d")).Enqueue(client);
        string step = chain!.Steps["1.0"];
        Assert.True(client.ChangeState(chain.Steps["0.0"], new SucceededState(null, 0, 0), null));
        Assert.True(client.ChangeState(step, new FailedState(new InvalidOperationException("failed")), null));
        StateData failed;
        using (IStorageConnection connection = storage.GetConnection())
        {
            failed = connection.GetStateData(step);
        }

        // The step failed by hand left its place in the queue behind: a server finds nothing to run there, and
        // clears it, before the retry begins
        using (CreateServer(storage))
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        ChainHooks hooks = new() { CrashOnCreating = "d" };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            // The process stops after it created the first of the new steps: nothing is rolled back
            Assert.Throws<OutOfMemoryException>(() => storage.RetryChainFrom(step));

            // The step is held for a little longer than the lease of the retry, and says what the retry started from
            using (IStorageConnection connection = storage.GetConnection())
            {
                StateData held = connection.GetStateData(step);
                Assert.Equal(ScheduledState.StateName, held.Name);
                Assert.Equal("Chain retried from this step", held.Reason);
                Assert.InRange(JobHelper.DeserializeDateTime(held.Data["EnqueueAt"]) - DateTime.UtcNow, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2.5));
                Assert.Contains("\"generation\":1", connection.GetJobParameter(step, "ChainRetry"), StringComparison.Ordinal);
            }

            Assert.Equal(chain.Steps.Values, storage.GetChain(chain.ChainId).Steps.Select(static x => x.JobId));

            // As if the lease of the process that stopped had lapsed, and the hold of the step were about to end:
            // its time is brought forward, and the server's scheduler and a worker do the rest
            LapseLease(storage, chain.ChainId);
            Assert.True(client.ChangeState(step, new ScheduledState(TimeSpan.FromSeconds(1)) { Reason = "Chain retried from this step" }, ScheduledState.StateName));
            Assert.NotNull(Parameter(storage, step, "ChainRetry"));

            using (CreateServer(storage))
            {
                // Back in the state it was in, with its data: the retry never happened
                StateData restored = await WaitForStateAsync(storage, step, static x => x.Name == FailedState.StateName);
                Assert.Equal($"The retry of chain {chain.ChainId} was abandoned", restored.Reason);
                Assert.Equal(failed.Data.OrderBy(static x => x.Key, StringComparer.Ordinal), restored.Data.OrderBy(static x => x.Key, StringComparer.Ordinal));

                // What the retry had created is deleted, and the note of the retry goes
                string created = hooks.Created.Single(static x => x.Name == "c").Id;
                Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(created, Timeout, TestContext.Current.CancellationToken)).Name);
                await WaitUntilAsync(() => Parameter(storage, step, "ChainRetry") == null, "The note of the retry stayed on the step.");
                await Task.Delay(1500, TestContext.Current.CancellationToken);
            }

            // Nothing ran, and the chain is as it was: the same steps, the ones after the failure still waiting
            Assert.Empty(Log);
            JobChainInfo info = storage.GetChain(chain.ChainId);
            Assert.Equal(1, info.Generation);
            Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
            Assert.True(info.IsSettled);
            Assert.Equal(AwaitingState.StateName, StateName(storage, chain.Steps["2.0"]));
            Assert.Equal(AwaitingState.StateName, StateName(storage, chain.Steps["3.0"]));
            Assert.Equal(
                [FailedState.StateName, ScheduledState.StateName, ScheduledState.StateName, EnqueuedState.StateName, FailedState.StateName],
                storage.GetMonitoringApi().JobDetails(step).History.Reverse().Select(static x => x.StateName).TakeLast(5));

            // The step is still what a requeue by hand runs, with the steps that followed it before
            Assert.True(client.ChangeState(step, new EnqueuedState(), FailedState.StateName));
            using (CreateServer(storage))
            {
                await WaitUntilSettledAsync(storage, chain.ChainId);
            }

            Assert.Equal(["b", "c", "d"], Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task RetryChainFrom_WhoseClientStalledUntilAServerPutItsStepBack_Fails_AndLeavesTheChainAsItWas()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => RecordWithoutRetries("b")).Then(() => Record("c")).Then(() => Record("d")).Enqueue(client);
        string step = chain!.Steps["1.0"];
        Assert.True(client.ChangeState(chain.Steps["0.0"], new SucceededState(null, 0, 0), null));
        Assert.True(client.ChangeState(step, new FailedState(new InvalidOperationException("failed")), null));

        using BackgroundJobServer server = CreateServer(storage);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        ChainHooks hooks = new()
        {
            PauseOnCreating = "d",
            WhilePaused = () =>
            {
                // The client stalls: its lease lapses and the hold of the step ends, so a server takes the retry
                // for abandoned and puts the step back
                LapseLease(storage, chain.ChainId);
                Assert.True(client.ChangeState(step, new ScheduledState(TimeSpan.FromSeconds(1)) { Reason = "Chain retried from this step" }, ScheduledState.StateName));
                WaitForState(storage, step, FailedState.StateName);
            },
        };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            // It wakes up, and finds that the step is no longer held for it
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => storage.RetryChainFrom(step));

            Assert.Contains("wasn't retried", exception.Message, StringComparison.Ordinal);
            Assert.Contains("as it was", exception.Message, StringComparison.Ordinal);
            JobChainInfo info = storage.GetChain(chain.ChainId);
            Assert.Equal(1, info.Generation);
            Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
            Assert.Equal(FailedState.StateName, StateName(storage, step));
            Assert.All(hooks.Created, created => Assert.Equal(DeletedState.StateName, StateName(storage, created.Id)));
            Assert.DoesNotContain("changing", HashField(storage, JobChains.GetKey(chain.ChainId), "Jobs"), StringComparison.Ordinal);
            await WaitUntilAsync(() => Parameter(storage, step, "ChainRetry") == null, "The note of the retry stayed on the step.");
            Assert.Empty(Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public void RetryChainFrom_ThatFails_PutsItsStepBack_AndTakesItsNoteOff()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => RecordWithoutRetries("b")).Then(() => Record("c")).Enqueue(client);
        string step = chain!.Steps["1.0"];
        Assert.True(client.ChangeState(chain.Steps["0.0"], new SucceededState(null, 0, 0), null));
        Assert.True(client.ChangeState(step, new FailedState(new InvalidOperationException("failed")), null));
        ChainHooks hooks = new() { PauseOnCreating = "c", WhilePaused = static () => throw new InvalidOperationException("The step can't be created.") };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            Assert.ThrowsAny<Exception>(() => storage.RetryChainFrom(step));
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }

        Assert.Equal(FailedState.StateName, StateName(storage, step));
        Assert.Null(Parameter(storage, step, "ChainRetry"));
        Assert.Equal(1, storage.GetChain(chain.ChainId).Generation);
        Assert.DoesNotContain("changing", HashField(storage, JobChains.GetKey(chain.ChainId), "Jobs"), StringComparison.Ordinal);
    }

    [Fact, CleanDatabase]
    public async Task Step_RequeuedByHand_Runs_ThoughARetryThatStoppedBeforeItHeldTheStep_LeftItsNoteOnTheJob()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        JobChainResult chain = JobChain.StartWith(() => RecordWithoutRetries("a")).Then(() => Record("b")).Enqueue(client);
        Assert.True(client.ChangeState(chain!.RootId, new FailedState(new InvalidOperationException("failed")), null));

        // The step failed by hand left its place in the queue behind: a server finds nothing to run there, and
        // clears it
        using (CreateServer(storage))
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        // SQL Server can't write a job parameter with a state: a retry notes what it starts from first, and one
        // whose process stops right there leaves the note on a step it never held
        using (IStorageConnection connection = storage.GetConnection())
        {
            connection.SetJobParameter(chain.RootId, "ChainRetry", "{\"id\":\"0123\",\"generation\":1,\"state\":\"Failed\",\"data\":{}}");
        }

        // Somebody requeues the failed step: it must run, not be put back as the step of an abandoned retry
        Assert.True(client.ChangeState(chain.RootId, new EnqueuedState(), FailedState.StateName));
        Assert.Null(Parameter(storage, chain.RootId, "ChainRetry"));

        using (CreateServer(storage))
        {
            await WaitUntilSettledAsync(storage, chain.ChainId);
        }

        Assert.Equal(["a", "b"], Log);
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
    public async Task Server_KeepsAChainWithAFailedStep_AndUnlistsAChainThatFinished_WhoseRecordIsPastItsTime()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        Log.Clear();
        _failures = 1;

        JobChainResult failed = JobChain.StartWith(() => FailOnce("a")).Then(() => Record("b")).Enqueue(client);
        JobChainResult finished = JobChain.StartWith(() => Record("x")).Then(() => Record("y")).Enqueue(client);

        using (CreateServer(storage))
        {
            await WaitUntilSettledAsync(storage, failed!.ChainId);
            await WaitUntilSettledAsync(storage, finished!.ChainId);
        }

        Assert.Equal(["x", "y"], Log);

        // As when nobody touched them for weeks: both records expire in three days, and both chains are due for
        // a look by a server
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            ((JobStorageTransaction)transaction).ExpireHash(JobChains.GetKey(failed.ChainId), TimeSpan.FromDays(3));
            ((JobStorageTransaction)transaction).ExpireHash(JobChains.GetKey(finished.ChainId), TimeSpan.FromDays(3));
            transaction.AddToSet(DueSet, failed.ChainId, 0);
            transaction.AddToSet(DueSet, finished.ChainId, 0);
            transaction.Commit();
        }

        using (CreateServer(storage))
        {
            await WaitUntilAsync(() => RecordTimeToLive(storage, failed.ChainId) > TimeSpan.FromDays(29.9), "The record of the chain with a failed step wasn't kept longer.");
            await WaitUntilAsync(() => SetItems(storage, ExpirySet).Contains(finished.ChainId), "The chain that finished wasn't left to expire.");
        }

        Assert.InRange(RecordTimeToLive(storage, finished.ChainId), TimeSpan.FromDays(2.9), TimeSpan.FromDays(3));
        Assert.Contains(failed.ChainId, SetItems(storage, DueSet));
        Assert.DoesNotContain(finished.ChainId, SetItems(storage, DueSet));

        // The record of the chain that finished is past its time. SQL Server still holds it, until its
        // expiration manager comes by: the chain leaves the list all the same.
        using (DbConnection sql = ConnectionUtils.CreateConnection())
        {
            sql.Execute(
                $"update [{Constants.DefaultSchema}].Hash set ExpireAt = dateadd(minute, -5, getutcdate()) where [Key] = @key",
                new { key = JobChains.GetKey(finished.ChainId) });
        }

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet(ExpirySet, finished.ChainId, 0);
            transaction.Commit();
        }

        using (CreateServer(storage))
        {
            await WaitUntilAsync(() => !SetItems(storage, JobChains.SetKey).Contains(finished.ChainId), "The chain whose record expired didn't leave the list.");
        }

        Assert.Empty(SetItems(storage, ExpirySet));
        Assert.Equal(failed.ChainId, Assert.Single(SetItems(storage, JobChains.SetKey)));

        // The failed chain can still be read and run again from the step that failed
        Assert.Equal(1, storage.GetChain(failed.ChainId)?.FailedCount);
        JobChainResult retried = storage.RetryChainFrom(failed.RootId);

        using (CreateServer(storage))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(retried.Steps["1.0"], Timeout, TestContext.Current.CancellationToken)).Name);
        }

        Assert.Equal(["x", "y", "a", "b"], Log);
    }

    [Fact, CleanDatabase]
    public async Task Step_AfterASingleStep_ThatWasNeverToldToStart_IsStartedByAServer_WithTheResultOfTheStepBeforeIt()
    {
        // A chain without any group, with results passed to continuations (UseResultsInContinuations)
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        List<object> before = [.. GlobalJobFilters.Filters.Select(static x => x.Instance)];
        GlobalConfiguration.Configuration.UseResultsInContinuations();

        try
        {
            JobChainResult chain = JobChain.StartWith(() => Produce("p")).Then(() => Consume("consumed", 0)).Enqueue(new BackgroundJobClient(storage));

            // What a process leaves that stops between the commit of a step's state and the start of the step
            // after it: the state is stored, and nobody is told
            using (IStorageConnection connection = storage.GetConnection())
            using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
            {
                transaction.SetJobState(chain!.RootId, new SucceededState(42, 0, 0));
                transaction.Commit();
            }

            Assert.Equal(AwaitingState.StateName, StateName(storage, chain.Steps["1.0"]));

            // Later nobody is about to start it any more, and a server does, handing it what its parent returned
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(6));
            using (CreateServer(storage))
            {
                Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(chain.Steps["1.0"], Timeout, TestContext.Current.CancellationToken)).Name);
            }

            Assert.Equal(["consumed 42"], Log);
        }
        finally
        {
            JobClock.TimeProvider = TimeProvider.System;
            foreach (object filter in GlobalJobFilters.Filters.Select(static x => x.Instance).Except(before).ToList())
            {
                GlobalJobFilters.Filters.Remove(filter);
            }
        }
    }

    [Fact, CleanDatabase]
    public async Task Chain_IsLookedAtSoon_WhenAStepLeavesTheStateItFinishedIn_HoweverFarOffItsNextLookWas()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        Log.Clear();
        _failures = 1;

        JobChainResult failed = JobChain.StartWith(() => FailOnce("a")).Then(() => Record("b")).Enqueue(client);
        JobChainResult finished = JobChain.StartWith(() => Record("x")).Enqueue(client);

        using (CreateServer(storage))
        {
            await WaitUntilSettledAsync(storage, failed!.ChainId);
            await WaitUntilSettledAsync(storage, finished!.ChainId);
        }

        // As after the looks of a server: the failed chain is looked at again in ten days, the other left to expire
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet(DueSet, failed.ChainId, JobHelper.ToTimestamp(DateTime.UtcNow.AddDays(10)));
            transaction.RemoveFromSet(DueSet, finished.ChainId);
            transaction.AddToSet(ExpirySet, finished.ChainId, JobHelper.ToTimestamp(DateTime.UtcNow.AddDays(30)));
            transaction.Commit();
        }

        // Somebody requeues the failed step, and the step of the chain that had finished: each asks for a look in
        // five minutes, in an entry of its own, and leaves the place of its chain where it is
        Assert.True(client.ChangeState(failed.RootId, new EnqueuedState(), FailedState.StateName));
        Assert.True(client.ChangeState(finished.RootId, new EnqueuedState(), SucceededState.StateName));

        Assert.True(IsAskedForWithin(storage, failed.ChainId, TimeSpan.FromMinutes(5)));
        Assert.False(IsAskedForWithin(storage, failed.ChainId, TimeSpan.FromMinutes(4)));
        Assert.True(IsAskedForWithin(storage, finished.ChainId, TimeSpan.FromMinutes(5)));
        Assert.False(IsDueWithin(storage, failed.ChainId, TimeSpan.FromDays(9)));
        Assert.Equal([finished.ChainId], SetItems(storage, ExpirySet));
        Assert.Equal(2, SetItems(storage, SoonSet).Count);

        // A state change of a step that runs asks for nothing
        using (CreateServer(storage))
        {
            await WaitUntilSettledAsync(storage, failed.ChainId);
            await WaitUntilSettledAsync(storage, finished.ChainId);
        }

        Assert.Equal(SucceededState.StateName, StateName(storage, failed.Steps["1.0"]));
        Assert.False(IsDueWithin(storage, failed.ChainId, TimeSpan.FromDays(9)));
        Assert.Equal(2, SetItems(storage, SoonSet).Count);

        // As if the five minutes had passed: a server does the looks that were asked for, gives each chain its
        // place again (both have finished: they are left to expire), and forgets what asked
        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            foreach (string entry in SetItems(storage, SoonSet))
            {
                transaction.AddToSet(SoonSet, entry, 0);
            }

            transaction.Commit();
        }

        using (CreateServer(storage))
        {
            await WaitUntilAsync(() => SetItems(storage, SoonSet).Count == 0, "The looks that were asked for weren't done.");
        }

        Assert.Equal(new[] { failed.ChainId, finished.ChainId }.Order(StringComparer.Ordinal), SetItems(storage, ExpirySet).Order(StringComparer.Ordinal));
        Assert.Empty(SetItems(storage, DueSet));
    }

    [Fact, CleanDatabase]
    public async Task Server_GivesTheChainsListedByAnEarlierBuild_ALook_AndRemembersThatItDid()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);

        // As an earlier build left them: listed, and only the one with a step after a group among the chains to
        // look at, in a set of its own
        JobChainResult line = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(client, TimeSpan.FromDays(1));
        JobChainResult joined = JobChain.StartWith(() => Record("a"))
            .ThenMany(branches => branches.Branch(() => Record("b")).Branch(() => Record("c")))
            .ThenAfterAll(() => Record("d"))
            .Schedule(client, TimeSpan.FromDays(1));

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveFromSet(DueSet, line!.ChainId);
            transaction.RemoveFromSet(DueSet, joined!.ChainId);
            transaction.AddToSet("chains:joins", joined.ChainId, JobHelper.ToTimestamp(DateTime.UtcNow));
            transaction.Commit();
        }

        using (CreateServer(storage))
        {
            await WaitUntilAsync(() => HashField(storage, "chains:index", "Listed") == "done", "The chains listed weren't gone through.");
            await WaitUntilAsync(
                () => !IsDueWithin(storage, line.ChainId, TimeSpan.FromMinutes(4)) && !IsDueWithin(storage, joined.ChainId, TimeSpan.FromMinutes(4)),
                "The chains listed by an earlier build weren't looked at.");
        }

        Assert.Equal(new[] { line.ChainId, joined.ChainId }.Order(StringComparer.Ordinal), SetItems(storage, DueSet).Order(StringComparer.Ordinal));
        Assert.Empty(SetItems(storage, "chains:joins"));
        Assert.Equal(2, SetItems(storage, JobChains.SetKey).Count);

        // Each was looked at, and is again a while later
        Assert.True(IsDueWithin(storage, line.ChainId, TimeSpan.FromMinutes(11)));
        Assert.True(IsDueWithin(storage, joined.ChainId, TimeSpan.FromMinutes(11)));
    }

    [Fact, CleanDatabase]
    public async Task Record_IsKeptForTheTimeTheProcessIsSetTo_AndItsStepsGoByThatTime_WhateverTheServerIsSetTo()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        Log.Clear();
        JobChainResult short7;
        JobChainResult long30;

        try
        {
            // A client that keeps the records of chains for a week
            GlobalConfiguration.Configuration.UseChainRetention(TimeSpan.FromDays(7));
            short7 = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(client, TimeSpan.FromDays(1));

            Assert.InRange(RecordTimeToLive(storage, short7!.ChainId), TimeSpan.FromDays(7.9), TimeSpan.FromDays(8) + TimeSpan.FromSeconds(5));
            Assert.Equal("604800", Parameter(storage, short7.RootId, JobChains.ChainRecordedParameter));
        }
        finally
        {
            GlobalConfiguration.Configuration.UseChainRetention(TimeSpan.FromDays(30));
        }

        // And one that keeps them for thirty days, as the server does
        long30 = JobChain.StartWith(() => Record("x")).Then(() => Record("y")).Schedule(client, TimeSpan.FromDays(1));
        Assert.Equal("2592000", Parameter(storage, long30!.RootId, JobChains.ChainRecordedParameter));

        // Nine days later both chains have no record. The first one's expired, for all the server knows: its steps
        // run. The second one's can't have expired: the chain was deleted, and its steps don't run.
        RemoveRecord(storage, short7.ChainId);
        RemoveRecord(storage, long30.ChainId);
        Backdate(9, short7.RootId, short7.Steps["1.0"], long30.RootId, long30.Steps["1.0"]);
        Assert.True(client.ChangeState(short7.RootId, new EnqueuedState(), ScheduledState.StateName));
        Assert.True(client.ChangeState(long30.RootId, new EnqueuedState(), ScheduledState.StateName));

        using (CreateServer(storage))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(short7.Steps["1.0"], Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(long30.RootId, Timeout, TestContext.Current.CancellationToken)).Name);
        }

        Assert.Equal(["a", "b"], Log);
    }

    [Fact, CleanDatabase]
    public async Task Chain_ScheduledFarAhead_WhoseCreationStoppedHalfway_IsDeleted_WhenItsFirstStepComesDue()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        ChainHooks hooks = new() { CrashOnCreating = "c" };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            Assert.Throws<OutOfMemoryException>(
                () => JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c")).Schedule(client, TimeSpan.FromDays(45)));

            string first = hooks.Created.Single(static x => x.Name == "a").Id;
            string chainId = Parameter(storage, first, JobChains.ChainIdParameter).Trim('"');

            // What the client started of the record is kept until long after the first step is due, so the step
            // finds it then: 45 days and the retention, set from this process's clock and read against SQL Server's
            Assert.InRange(RecordTimeToLive(storage, chainId), TimeSpan.FromDays(74.9), TimeSpan.FromDays(45) + JobChain.RecordRetention + TimeSpan.FromSeconds(5));
            Assert.Null(Parameter(storage, first, JobChains.ChainRecordedParameter));

            // The step comes due long after the client's lease lapsed: the record lists no step
            LapseLease(storage, chainId);
            Log.Clear();
            Assert.True(client.ChangeState(first, new EnqueuedState(), ScheduledState.StateName));

            // The step is refused when a worker takes it
            using (CreateServer(storage))
            {
                JobStateInfo finished = await storage.WaitForCompletionAsync(first, Timeout, TestContext.Current.CancellationToken);
                Assert.Equal(DeletedState.StateName, finished.Name);
                Assert.Equal($"Chain {chainId} isn't recorded: it was deleted or never completely created", finished.Reason);
            }

            Assert.Empty(Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task Job_OfAChainThatWasNeverRecorded_DoesNotRun_ForBeingOlderThanARecordIsKept()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        ChainHooks hooks = new() { CrashOnCreating = "c" };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            Assert.Throws<OutOfMemoryException>(
                () => JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c")).Schedule(client, TimeSpan.FromDays(45)));

            string first = hooks.Created.Single(static x => x.Name == "a").Id;
            string second = hooks.Created.Single(static x => x.Name == "b").Id;
            string chainId = Parameter(storage, first, JobChains.ChainIdParameter).Trim('"');

            // What was started of the record is gone, and the jobs are older than a record is kept: as old as the
            // steps of a chain whose record expired, but they were never listed in one
            RemoveRecord(storage, chainId);
            Backdate(46, first, second);

            Log.Clear();
            Assert.True(client.ChangeState(first, new EnqueuedState(), ScheduledState.StateName));

            // The step is refused when a worker takes it
            using (CreateServer(storage))
            {
                JobStateInfo finished = await storage.WaitForCompletionAsync(first, Timeout, TestContext.Current.CancellationToken);
                Assert.Equal(DeletedState.StateName, finished.Name);
                Assert.Equal($"Chain {chainId} isn't recorded: it was deleted or never completely created", finished.Reason);
            }

            Assert.Empty(Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public async Task Chain_ThatWasRecorded_StillRuns_OnceItsRecordExpired()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(client, TimeSpan.FromDays(45));

        // Every step is marked once the chain is recorded (right after the record here: this storage can't
        // write a job parameter in a transaction)
        // The mark says how long the record is kept, in seconds
        Assert.Equal("2592000", Parameter(storage, chain!.RootId, JobChains.ChainRecordedParameter));
        Assert.Equal("2592000", Parameter(storage, chain.Steps["1.0"], JobChains.ChainRecordedParameter));

        // Long after: the record expired, and the steps are still to run
        RemoveRecord(storage, chain.ChainId);
        Backdate(46, chain.RootId, chain.Steps["1.0"]);
        client.ChangeState(chain.RootId, new EnqueuedState(), ScheduledState.StateName);

        using (CreateServer(storage))
        {
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(chain.RootId, Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(chain.Steps["1.0"], Timeout, TestContext.Current.CancellationToken)).Name);
        }

        Assert.Equal(["a", "b"], Log);
    }

    [Fact, CleanDatabase]
    public void Chain_WhoseClientStalled_IsNotReturnedAsCreated_WhenAServerDeletedItsFirstStepMeanwhile()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        ChainHooks hooks = new() { PauseOnCreating = "c" };
        hooks.WhilePaused = () =>
        {
            // The client stalls for longer than its lease on the chain lasts (rewritten as lapsed: the clock of
            // the database can't be moved), and a server takes the chain for abandoned
            string first = hooks.Created.Single(static x => x.Name == "a").Id;
            LapseLease(storage, Parameter(storage, first, JobChains.ChainIdParameter).Trim('"'));
            WaitForState(storage, first, DeletedState.StateName);
        };
        GlobalJobFilters.Filters.Add(hooks);

        try
        {
            using (CreateServer(storage))
            {
                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    () => JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c")).Enqueue(new BackgroundJobClient(storage)));

                Assert.Contains("wasn't created", exception.Message, StringComparison.Ordinal);
            }

            using IStorageConnection connection = storage.GetConnection();
            Assert.All(hooks.Created, step => Assert.Equal(DeletedState.StateName, connection.GetStateData(step.Id).Name));
            Assert.Empty(connection.GetAllItemsFromSet(JobChains.SetKey));
            Assert.Empty(Log);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(hooks);
        }
    }

    [Fact, CleanDatabase]
    public void Chain_WhoseStepsMethodsWereRenamedOrRemoved_CanStillBeRead_AndDeleted()
    {
        SqlServerStorage storage = CreateStorage();
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Then(() => Record("c"))
            .Schedule(new BackgroundJobClient(storage), TimeSpan.FromHours(1));
        ForgetTheMethods(storage, chain!.ChainId);

        // Read: the steps and their states, without their method calls
        JobChainInfo info = storage.GetChain(chain.ChainId);
        Assert.NotNull(info);
        Assert.False(info.IsSettled);
        Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
        Assert.All(info.Steps, static step => Assert.Null(step.Job));

        Assert.Equal(3, storage.DeleteChain(chain.ChainId));
        Assert.Null(storage.GetChain(chain.ChainId));

        using IStorageConnection connection = storage.GetConnection();
        Assert.All(chain.Steps.Values, id => Assert.Equal(DeletedState.StateName, connection.GetStateData(id).Name));
    }

    [Fact, CleanDatabase]
    public async Task RecurringChain_GoesOn_AfterADeployRenamedOrRemovedAMethodOfThePreviousChain()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdateChain("etl", JobChain.StartWith(() => Record("extract")).Then(() => Record("load")), "0 0 1 1 *", skipWhileRunning: true);

        using (CreateServer(storage))
        {
            string first = manager.TriggerJob("etl");
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(first, Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.True(storage.TryGetResult(first, out string previous));
            await WaitUntilSettledAsync(storage, previous);

            // The record of the previous chain now names a method that can't be loaded
            ForgetTheMethods(storage, previous);

            string second = manager.TriggerJob("etl");
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(second, Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.True(storage.TryGetResult(second, out string next));
            Assert.NotEqual(previous, next);
            await WaitUntilSettledAsync(storage, next);
        }

        Assert.Equal(["extract", "load", "extract", "load"], Log);
    }

    [Fact, CleanDatabase]
    public async Task RecurringChain_RunAgainAfterItsChainWasRecorded_DoesNotCreateASecondChain()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdateChain("etl", JobChain.StartWith(() => Record("extract")).Then(() => Record("load")), "0 0 1 1 *");

        using (CreateServer(storage))
        {
            string occurrence = manager.TriggerJob("etl");
            Assert.Equal(SucceededState.StateName, (await storage.WaitForCompletionAsync(occurrence, Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.True(storage.TryGetResult(occurrence, out string chainId));
            await WaitUntilSettledAsync(storage, chainId);

            // The job that creates the chain runs again, as after a process that stopped before the job finished
            Assert.True(new BackgroundJobClient(storage).ChangeState(occurrence, new EnqueuedState(), SucceededState.StateName));

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (storage.GetMonitoringApi().JobDetails(occurrence).History.Count(static x => x.StateName == SucceededState.StateName) < 2)
            {
                Assert.True(stopwatch.Elapsed < Timeout, "The job that creates the chain didn't run again.");
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.True(storage.TryGetResult(occurrence, out string again));
            Assert.Equal(chainId, again);
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        using IStorageConnection connection = storage.GetConnection();
        Assert.Single(connection.GetAllItemsFromSet(JobChains.SetKey));
        Assert.Equal(["extract", "load"], Log);
    }

    private static string Parameter(SqlServerStorage storage, string jobId, string name)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetJobParameter(jobId, name);
    }

    /// <summary>Removes the record of a chain and its entry in the list, as its expiry does in time.</summary>
    private static void RemoveRecord(SqlServerStorage storage, string chainId)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.RemoveHash(JobChains.GetKey(chainId));
        transaction.RemoveFromSet(JobChains.SetKey, chainId);
        transaction.Commit();
    }

    /// <summary>Makes jobs as old as if they had been created that many days earlier.</summary>
    private static void Backdate(int days, params string[] jobIds)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        foreach (string jobId in jobIds)
        {
            sql.Execute(
                $"update [{Constants.DefaultSchema}].Job set CreatedAt = dateadd(day, @days, CreatedAt) where Id = @id",
                new { days = -days, id = long.Parse(jobId) });
        }
    }

    /// <summary>
    /// What a deploy does that renames or removes the method of the steps: the stored definition of the chain
    /// names a method that can't be loaded.
    /// </summary>
    private static void ForgetTheMethods(SqlServerStorage storage, string chainId)
    {
        using IStorageConnection connection = storage.GetConnection();
        string definition = connection.GetAllEntriesFromHash(JobChains.GetKey(chainId))["Definition"];
        string forgotten = definition.Replace("Record", "Gone", StringComparison.Ordinal);
        Assert.NotEqual(definition, forgotten);

        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(JobChains.GetKey(chainId), new Dictionary<string, string> { ["Definition"] = forgotten });
        transaction.Commit();
    }

    /// <summary>Waits, on the calling thread, until a job is in a state.</summary>
    private static void WaitForState(SqlServerStorage storage, string jobId, string stateName)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using (IStorageConnection connection = storage.GetConnection())
            {
                if (connection.GetStateData(jobId)?.Name == stateName)
                {
                    return;
                }
            }

            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException($"Job {jobId} didn't reach the {stateName} state.");
            }

            Thread.Sleep(100);
        }
    }

    private static HashSet<string> SetItems(SqlServerStorage storage, string key)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetAllItemsFromSet(key);
    }

    private static string HashField(SqlServerStorage storage, string key, string field)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetAllEntriesFromHash(key)?.GetValueOrDefault(field);
    }

    private static string StateName(SqlServerStorage storage, string jobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetStateData(jobId)?.Name;
    }

    /// <summary>Whether a step of the chain asked for a look by a server within a time from now.</summary>
    private static bool IsAskedForWithin(SqlServerStorage storage, string chainId, TimeSpan time)
    {
        using IStorageConnection connection = storage.GetConnection();
        return ((JobStorageConnection)connection)
            .GetFirstByLowestScoreFromSet(SoonSet, 0, JobHelper.ToTimestamp(DateTime.UtcNow + time), 1000)
            .Exists(entry => entry.StartsWith(chainId + "|", StringComparison.Ordinal));
    }

    /// <summary>Whether a server is to look at a chain within a time from now.</summary>
    private static bool IsDueWithin(SqlServerStorage storage, string chainId, TimeSpan time)
    {
        using IStorageConnection connection = storage.GetConnection();
        return ((JobStorageConnection)connection)
            .GetFirstByLowestScoreFromSet(DueSet, 0, JobHelper.ToTimestamp(DateTime.UtcNow + time), 1000)
            .Contains(chainId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
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
        public string PauseOnCreating { get; set; }
        public Action WhilePaused { get; set; }
        public string FailStartOf { get; set; }
        public int FailedStarts => Volatile.Read(ref _failedStarts);
        public ConcurrentQueue<(string Name, string Id)> Created { get; } = new();

        public void OnCreating(CreatingContext context)
        {
            if (CrashOnCreating != null && context.Job.Type == typeof(JobChainFacts) && (string)context.Job.Args[0] == CrashOnCreating)
            {
                throw new OutOfMemoryException("The process stops here.");
            }

            if (PauseOnCreating != null && context.Job.Type == typeof(JobChainFacts) && (string)context.Job.Args[0] == PauseOnCreating)
            {
                WhilePaused?.Invoke();
            }
        }

        public void OnCreated(CreatedContext context)
        {
            if ((CrashOnCreating != null || PauseOnCreating != null) && context.Job.Type == typeof(JobChainFacts) && context.BackgroundJob != null)
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

    public static int Produce(string name) => name.Length + 41;

    public static void Consume(string name, [FromResult] int value) => Log.Enqueue($"{name} {value}");

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
