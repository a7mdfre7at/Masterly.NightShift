using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// Retrying, replacing and deleting chains through a real storage, state machine and continuation filters, without a
/// server: the tests move the steps to their outcomes themselves.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class JobChainStorageFacts : IDisposable
{
    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobClient _client;
    private readonly ThrowingFilter _filter = new();

    public JobChainStorageFacts()
    {
        _client = new BackgroundJobClient(_storage);
        GlobalJobFilters.Filters.Add(_filter);
    }

    public void Dispose()
    {
        GlobalJobFilters.Filters.Remove(_filter);
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Step(string name) => _ = name;

    [Fact]
    public void RetryChainFrom_RunsAChainAgain_WhoseStepsWaitBehindAFailure()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")).Then(() => Step("d")).Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Fail(chain.Steps["1.0"]);

        Assert.True(_storage.GetChain(chain.ChainId)?.IsSettled);
        JobChainResult retried = _storage.RetryChainFrom(chain.Steps["1.0"]);

        Assert.Equal(EnqueuedState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(DeletedState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.Equal(DeletedState.StateName, StateOf(chain.Steps["3.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(retried.Steps["2.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(retried.Steps["3.0"]));
        Assert.Equal(2, _storage.GetChain(chain.ChainId)?.Generation);
    }

    [Fact]
    public void RetryChainFrom_ChangesNothing_WhenANewStepCanNotBeCreated()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")).Then(() => Step("d")).Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Fail(chain.Steps["1.0"]);
        _filter.ThrowOn = "d";

        Assert.ThrowsAny<Exception>(() => _storage.RetryChainFrom(chain.Steps["1.0"]));

        JobChainInfo info = _storage.GetChain(chain.ChainId)!;
        Assert.Equal(1, info.Generation);
        Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
        Assert.Equal(FailedState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["3.0"]));
        Assert.Equal(DeletedState.StateName, StateOf(_filter.Created.Last(x => x.Name == "c").Id));

        _filter.ThrowOn = null;
        JobChainResult retried = _storage.RetryChainFrom(chain.Steps["1.0"]);
        Assert.Equal(EnqueuedState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(retried.Steps["3.0"]));
    }

    [Fact]
    public void RetryChainFrom_AStepOfAGroup_WaitsForItAgain_BeforeDecidingTheJoins()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a"))
            .ThenMany(branches => branches.Branch(() => Step("b")).Branch(() => Step("c")))
            .ThenAfterAll(() => Step("done"))
            .ThenAfterAnyFailed(() => Step("alert"))
            .Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Succeed(chain.Steps["1.1"]);
        Fail(chain.Steps["1.0"]);
        Assert.Equal(EnqueuedState.StateName, StateOf(chain.Steps["2.1"]));
        Succeed(chain.Steps["2.1"]);

        JobChainResult retried = _storage.RetryChainFrom(chain.Steps["1.0"]);

        Assert.Equal(DeletedState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(retried.Steps["2.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(retried.Steps["2.1"]));

        Succeed(chain.Steps["1.0"]);

        Assert.Equal(EnqueuedState.StateName, StateOf(retried.Steps["2.0"]));
        Assert.Equal(DeletedState.StateName, StateOf(retried.Steps["2.1"]));
    }

    [Fact]
    public void RetryChainFrom_ADeletedStep_DoesNotSkipTheNewStepsAfterIt()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;
        Assert.True(_client.ChangeState(chain.RootId, new DeletedState(), null));
        Assert.Equal(DeletedState.StateName, StateOf(chain.Steps["1.0"]));

        JobChainResult retried = _storage.RetryChainFrom(chain.RootId);

        Assert.Equal(EnqueuedState.StateName, StateOf(chain.RootId));
        Assert.Equal(AwaitingState.StateName, StateOf(retried.Steps["1.0"]));
    }

    [Fact]
    public void ReplaceChain_DeletesTheOldSteps_WithoutStartingTheOnesThatFollowADeletion()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a"))
            .ThenAfterAllCompleted(() => Step("always"))
            .Then(() => Step("finally"), JobContinuationOptions.OnAnyFinishedState)
            .Enqueue(_client, new ScheduleOptions { SkipAfter = TimeSpan.FromHours(1), Queue = "critical" })!;

        JobChainResult replaced = _storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Step("x")).Then(() => Step("y")));

        Assert.All(chain.Steps.Values, id => Assert.Equal(DeletedState.StateName, StateOf(id)));
        Assert.Equal(EnqueuedState.StateName, StateOf(replaced.RootId));
        Assert.Equal(AwaitingState.StateName, StateOf(replaced.Steps["1.0"]));
        Assert.Equal("3600", Parameter(replaced.RootId, SkipIfLateAttribute.WindowParameter));
        Assert.Equal("critical", JobOf(replaced.RootId)?.Queue);
        Assert.Equal(2, _storage.GetChain(chain.ChainId)?.Generation);
    }

    [Fact]
    public void ReplaceChain_KeepsTheOldChain_WhenTheNewOneCanNotBeCreated()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;
        _filter.ThrowOn = "y";

        Assert.ThrowsAny<Exception>(() => _storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Step("x")).Then(() => Step("y"))));

        Assert.Equal(EnqueuedState.StateName, StateOf(chain.RootId));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(chain.Steps.Values, _storage.GetChain(chain.ChainId)!.Steps.Select(static x => x.JobId));
        Assert.Equal(DeletedState.StateName, StateOf(_filter.Created.Single(x => x.Name == "x").Id));
    }

    [Fact]
    public void Enqueue_LeavesTheFirstStep_WhenItFinishedBeforeALaterStepFailedToBeCreated()
    {
        _filter.ThrowOn = "b";
        _filter.BeforeThrowing = () => Succeed(_filter.Created.Single(x => x.Name == "a").Id);

        Assert.ThrowsAny<Exception>(() => JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client));

        Assert.Equal(SucceededState.StateName, StateOf(_filter.Created.Single(x => x.Name == "a").Id));
    }

    [Fact]
    public void Enqueue_RemovesChainsWhoseRecordExpired_FromTheList()
    {
        double old = JobHelper.ToTimestamp(JobClock.UtcNow - JobChain.RecordRetention - TimeSpan.FromDays(1));
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet(JobChains.SetKey, "expired", old);
            transaction.AddToSet(JobChains.SetKey, "retried", old);
            transaction.SetRangeInHash(JobChains.GetKey("retried"), new Dictionary<string, string> { ["Jobs"] = "{}" });
            transaction.Commit();
        }

        JobChainResult chain = JobChain.StartWith(() => Step("a")).Enqueue(_client)!;

        using IStorageConnection reader = _storage.GetConnection();
        HashSet<string> listed = reader.GetAllItemsFromSet(JobChains.SetKey);
        Assert.DoesNotContain("expired", listed);
        Assert.Contains("retried", listed);
        Assert.Contains(chain.ChainId, listed);
    }

    [Fact]
    public void DeleteChain_DeletesEveryStep_AndTheRecord()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a"))
            .ThenAfterAllCompleted(() => Step("always"))
            .Then(() => Step("b"))
            .Enqueue(_client)!;

        Assert.Equal(3, _storage.DeleteChain(chain.ChainId));

        Assert.All(chain.Steps.Values, id => Assert.Equal(DeletedState.StateName, StateOf(id)));
        Assert.Null(_storage.GetChain(chain.ChainId));
    }

    private void Succeed(string jobId) => Assert.True(_client.ChangeState(jobId, new SucceededState(null, 0, 0), null));

    private void Fail(string jobId) => Assert.True(_client.ChangeState(jobId, new FailedState(new InvalidOperationException("failed")), null));

    private string? StateOf(string jobId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetStateData(jobId)?.Name;
    }

    private Job? JobOf(string jobId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetJobData(jobId)?.Job;
    }

    private string? Parameter(string jobId, string name)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetJobParameter(jobId, name);
    }

    /// <summary>Records the steps created, and fails the creation of the one named <see cref="ThrowOn"/>.</summary>
    private sealed class ThrowingFilter : IClientFilter
    {
        public string? ThrowOn { get; set; }
        public Action? BeforeThrowing { get; set; }
        public List<(string Name, string Id)> Created { get; } = [];

        public void OnCreating(CreatingContext context)
        {
            if (context.Job.Type == typeof(JobChainStorageFacts) && (string?)context.Job.Args[0] == ThrowOn)
            {
                BeforeThrowing?.Invoke();
                throw new InvalidOperationException($"Step '{ThrowOn}' can't be created.");
            }
        }

        public void OnCreated(CreatedContext context)
        {
            if (context.Job.Type == typeof(JobChainStorageFacts) && context.BackgroundJob != null)
            {
                Created.Add(((string)context.Job.Args[0], context.BackgroundJob.Id));
            }
        }
    }
}
