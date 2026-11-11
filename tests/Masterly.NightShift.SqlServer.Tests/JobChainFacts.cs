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
