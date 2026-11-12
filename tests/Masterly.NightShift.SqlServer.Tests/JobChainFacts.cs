using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    public async Task Chain_ThatWasNeverRecorded_IsDeleted_AndNoneOfItsStepsRuns()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        JobChainResult chain;

        try
        {
            // As if the client had stopped after creating the jobs, before writing the record, a while ago
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(-10));
            chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Enqueue(new BackgroundJobClient(storage));
        }
        finally
        {
            JobClock.TimeProvider = TimeProvider.System;
        }

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveHash(JobChains.GetKey(chain!.ChainId));
            transaction.RemoveFromSet(JobChains.SetKey, chain.ChainId);
            transaction.Commit();
        }

        using (CreateServer(storage))
        {
            Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(chain.RootId, Timeout, TestContext.Current.CancellationToken)).Name);
            Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(chain.Steps["1.0"], Timeout, TestContext.Current.CancellationToken)).Name);
        }

        using IStorageConnection reader = storage.GetConnection();
        Assert.Equal($"Chain {chain.ChainId} isn't recorded: it was deleted or never completely created", reader.GetStateData(chain.RootId).Reason);
        Assert.Empty(Log);
    }

    [Fact, CleanDatabase]
    public async Task Job_ThatTheRecordOfItsChainDoesNotList_NeverRuns()
    {
        SqlServerStorage storage = CreateStorage();
        Log.Clear();
        BackgroundJobClient client = new(storage);
        JobChainResult chain = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Enqueue(client);
        string orphan;

        try
        {
            // Left behind by something that stopped halfway, long enough ago not to be a change in progress
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(-10));
            orphan = client.Create(
                Job.FromExpression(() => Record("orphan")),
                new EnqueuedState(),
                new Dictionary<string, object> { [JobChains.ChainIdParameter] = chain!.ChainId, [JobChains.ChainStepParameter] = "1.0" });
        }
        finally
        {
            JobClock.TimeProvider = TimeProvider.System;
        }

        using (CreateServer(storage))
        {
            Assert.Equal(DeletedState.StateName, (await storage.WaitForCompletionAsync(orphan, Timeout, TestContext.Current.CancellationToken)).Name);
            await WaitUntilSettledAsync(storage, chain.ChainId);
        }

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal($"Not a current step of chain {chain.ChainId}", connection.GetStateData(orphan).Reason);
        Assert.Equal(["a", "b"], Log);
    }

    [Fact, CleanDatabase]
    public void Record_IsKept_UntilLongAfterAScheduledFirstStepIsDue_AndLongerWhenAStepChangesState()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        JobChainResult scheduled = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Schedule(client, TimeSpan.FromDays(60));
        JobChainResult enqueued = JobChain.StartWith(() => Record("a")).Then(() => Record("b")).Enqueue(client);

        Assert.InRange(RecordTimeToLive(storage, scheduled!.ChainId), TimeSpan.FromDays(89.9), TimeSpan.FromDays(90));
        Assert.InRange(RecordTimeToLive(storage, enqueued!.ChainId), TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));

        try
        {
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(20));
            Assert.InRange(RecordTimeToLive(storage, enqueued.ChainId), TimeSpan.FromDays(9.9), TimeSpan.FromDays(10));

            Assert.True(client.ChangeState(enqueued.RootId, new SucceededState(null, 0, 0), null));
            Assert.InRange(RecordTimeToLive(storage, enqueued.ChainId), TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));

            // A step of the chain scheduled far ahead changes state: its record keeps its longer life
            Assert.True(client.ChangeState(scheduled.Steps["1.0"], new DeletedState(), null));
            Assert.InRange(RecordTimeToLive(storage, scheduled.ChainId), TimeSpan.FromDays(69.9), TimeSpan.FromDays(70));
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
                    if (!connection.GetAllItemsFromSet("chains:cleanup").Contains(chain.ChainId)) break;
                    if (stopwatch.Elapsed > Timeout) throw new TimeoutException("The replacement wasn't finished.");
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
                if (!connection.GetAllItemsFromSet(JobChains.SetKey).Any(static x => x.StartsWith("expired", StringComparison.Ordinal))) break;
                if (stopwatch.Elapsed > Timeout) throw new TimeoutException("The expired chains didn't leave the list.");
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

        if (!Log.Contains("sibling")) throw new TimeoutException("The processing continuation didn't start while the parent ran.");
    }

    private static async Task WaitUntilSettledAsync(SqlServerStorage storage, string chainId)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (storage.GetChain(chainId) is not { IsSettled: true })
        {
            if (stopwatch.Elapsed > Timeout) throw new TimeoutException($"Chain {chainId} didn't settle.");
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
