using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Client;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// Retrying, replacing and deleting chains through a real storage, state machine and continuation filters. Most tests
/// run without a server and move the steps to their outcomes themselves; the ones about which steps may run start
/// a real server.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public sealed class JobChainStorageFacts : IDisposable
{
    private const string CleanupSet = "chains:cleanup";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<string> Ran = new();

    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobClient _client;
    private readonly ThrowingFilter _filter = new();
    private readonly FailingDeletions _deletions = new();

    public JobChainStorageFacts()
    {
        _client = new BackgroundJobClient(_storage);
        GlobalJobFilters.Filters.Add(_filter);
        GlobalJobFilters.Filters.Add(_deletions);
        Ran.Clear();
    }

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        GlobalJobFilters.Filters.Remove(_filter);
        GlobalJobFilters.Filters.Remove(_deletions);
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Step(string name) => _ = name;

    /// <summary>A step that says it ran.</summary>
    public static void Run(string name) => Ran.Enqueue(name);

    [Fact]
    public void Chain_ThatIsRecorded_RunsToTheEnd_AndNoStepIsHeld()
    {
        JobChainResult chain = CreateWorkflow();

        using BackgroundJobServer server = CreateServer();
        WaitUntilSettled(chain.ChainId);

        AssertWorkflowRan();
        Assert.All(chain.Steps.Values, id => Assert.DoesNotContain(History(id), static x => x.StateName == ScheduledState.StateName));
    }

    [Fact]
    public void Chain_CreatedWhileAServerRuns_StartsAsSoonAsItIsRecorded()
    {
        // The slow polling of a real server: a first step held until the chain is recorded would wait for it,
        // were it not enqueued by the client that records the chain
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = 2, SchedulePollingInterval = TimeSpan.FromMinutes(1) }, _storage);

        for (int i = 0; i < 5; i++)
        {
            Ran.Clear();
            Stopwatch started = Stopwatch.StartNew();
            JobChainResult chain = CreateWorkflow();
            WaitUntilSettled(chain.ChainId);

            AssertWorkflowRan();
            Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(20));

            // Only the first step can be taken before the chain is recorded
            Assert.All(chain.Steps.Where(static x => x.Key != "0.0"), step => Assert.DoesNotContain(History(step.Value), static x => x.StateName == ScheduledState.StateName));
        }
    }

    [Fact]
    public void Chain_WhoseCreationFailsMidway_LeavesNothingThatRuns_EvenWhenAWorkerTookItsFirstStep()
    {
        using BackgroundJobServer server = CreateServer();
        _filter.ThrowOn = "c";
        _filter.BeforeThrowing = static () => Thread.Sleep(300);

        Assert.ThrowsAny<Exception>(() => JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client));

        string first = _filter.Created.Single(static x => x.Name == "a").Id;
        string second = _filter.Created.Single(static x => x.Name == "b").Id;
        WaitUntil(() => StateOf(first) == DeletedState.StateName && StateOf(second) == DeletedState.StateName, "the steps created to be deleted");

        Thread.Sleep(500);
        Assert.Empty(Ran);
    }

    [Fact]
    public void Chain_ThatTakesTooLongToCreate_IsNotReturnedAsCreated_WhenItsFirstStepWasDeletedMeanwhile()
    {
        using BackgroundJobServer server = CreateServer();
        _filter.PauseOn = "c";
        _filter.WhilePaused = () =>
        {
            // The client stalls for longer than a step waits for its chain's record
            string first = _filter.Created.Single(static x => x.Name == "a").Id;
            JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(6));
            WaitUntil(() => StateOf(first) == DeletedState.StateName, "the first step to be deleted");
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client));

        Assert.Contains("wasn't created", exception.Message, StringComparison.Ordinal);
        Assert.All(_filter.Created, step => Assert.Equal(DeletedState.StateName, StateOf(step.Id)));
        Assert.Empty(SetItems(JobChains.SetKey));
        Assert.Empty(Ran);
    }

    [Fact]
    public void Chain_ThatWasNeverRecorded_HasItsFirstStepHeld_ThenDeleted_AndTheStepsAfterItDoNotRun()
    {
        // As if the client had stopped after creating the jobs, before writing the record
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;
        RemoveRecord(chain.ChainId);

        using BackgroundJobServer server = CreateServer();

        string waiting = $"Waiting for chain {chain.ChainId} to be recorded";
        WaitUntil(() => History(chain.RootId).Any(x => x.StateName == ScheduledState.StateName && x.Reason == waiting), "the first step to be held");
        Assert.Empty(Ran);
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["1.0"]));

        // The time a client may take to record a chain is over
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(6));

        WaitUntil(() => StateOf(chain.RootId) == DeletedState.StateName && StateOf(chain.Steps["1.0"]) == DeletedState.StateName, "the steps to be deleted");
        Assert.Equal($"Chain {chain.ChainId} isn't recorded: it was deleted or never completely created", ReasonOf(chain.RootId));
        Assert.Empty(Ran);
    }

    [Fact]
    public void Job_ThatTheRecordOfItsChainDoesNotList_NeverRuns()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;

        // Left behind by something that stopped halfway, long enough ago not to be a change in progress
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(-10));
        string orphan = _client.Create(
            Job.FromExpression(() => Run("orphan")),
            new EnqueuedState(),
            new Dictionary<string, object> { [JobChains.ChainIdParameter] = chain.ChainId, [JobChains.ChainStepParameter] = "1.0" })!;
        JobClock.TimeProvider = TimeProvider.System;

        using BackgroundJobServer server = CreateServer();

        WaitUntil(() => StateOf(orphan) == DeletedState.StateName, "the job to be deleted");
        WaitUntilSettled(chain.ChainId);

        Assert.Equal($"Not a current step of chain {chain.ChainId}", ReasonOf(orphan));
        Assert.Equal(["a", "b"], Ran);
    }

    [Fact]
    public void Step_OfADeletedChain_ThatIsRequeued_DoesNotRun()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;
        _storage.DeleteChain(chain.ChainId);

        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(6));
        Assert.True(_client.ChangeState(chain.RootId, new EnqueuedState(), DeletedState.StateName));

        using BackgroundJobServer server = CreateServer();

        WaitUntil(() => StateOf(chain.RootId) == DeletedState.StateName, "the step to be deleted again");
        Assert.Empty(Ran);
    }

    [Fact]
    public void Chain_WhoseRecordTheStorageLostEarly_StillRuns_BecauseItIsListed()
    {
        // What a cap on expiration times or an eviction does: the record is gone, the chain is still listed
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveHash(JobChains.GetKey(chain.ChainId));
            transaction.Commit();
        }

        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromHours(2));
        using BackgroundJobServer server = CreateServer();

        WaitUntil(() => Ran.Count == 2, "the steps to run");
        Assert.Equal(["a", "b"], Ran);
    }

    [Fact]
    public void Step_RequeuedByHand_RunsAgain_WhenTheRecordListsIt()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;

        using BackgroundJobServer server = CreateServer();
        WaitUntilSettled(chain.ChainId);

        Assert.True(_client.ChangeState(chain.Steps["1.0"], new EnqueuedState(), SucceededState.StateName));
        WaitUntil(() => Ran.Count == 3, "the step to run again");

        Assert.Equal(["a", "b", "b"], Ran);
    }

    [Fact]
    public void ReplaceChain_Succeeds_WhenTheOldStepsCanNotBeDeleted_AndTheyNeverRun()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;
        _deletions.FailWithReason = "The chain was replaced";

        JobChainResult replaced = _storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Run("x")).Then(() => Run("y")));

        // The replacement is recorded, with what is left to delete
        Assert.Equal(replaced.Steps.Values, _storage.GetChain(chain.ChainId)!.Steps.Select(static x => x.JobId));
        Assert.Equal(ScheduledState.StateName, StateOf(chain.RootId));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Contains(chain.RootId, RecordField(chain.ChainId, "Cleanup"), StringComparison.Ordinal);
        Assert.Contains(chain.ChainId, SetItems(CleanupSet));

        // The old first step's hold ends while its deletion still fails, long after the replacement: a worker
        // takes it
        Assert.True(_client.ChangeState(chain.RootId, new EnqueuedState(), ScheduledState.StateName));
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(6));

        using (CreateServer())
        {
            WaitUntilSettled(chain.ChainId);
            WaitUntil(() => StateOf(chain.RootId) == DeletedState.StateName, "the old first step to be deleted");
            Assert.Equal($"Not a current step of chain {chain.ChainId}", ReasonOf(chain.RootId));
        }

        Assert.Equal(["x", "y"], Ran);

        // A server finishes what was left once the storage lets it
        _deletions.FailWithReason = null;
        using (CreateServer())
        {
            WaitUntil(() => !SetItems(CleanupSet).Contains(chain.ChainId), "the replacement to be finished");
        }

        Assert.All(chain.Steps.Values, id => Assert.Equal(DeletedState.StateName, StateOf(id)));
        Assert.Equal(string.Empty, RecordField(chain.ChainId, "Cleanup"));
        Assert.Equal(["x", "y"], Ran);
    }

    [Fact]
    public void RetryChainFrom_Succeeds_WhenTheReplacedStepsCanNotBeDeleted_AndTheNextChangeDeletesThem()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")).Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Fail(chain.Steps["1.0"]);
        _deletions.FailWithReason = "Replaced by a retry of the chain";

        JobChainResult retried = _storage.RetryChainFrom(chain.Steps["1.0"]);

        // The retry went through: the step runs again, and the old step after it waits to be deleted
        Assert.Equal(EnqueuedState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.NotEqual(chain.Steps["2.0"], retried.Steps["2.0"]);
        Assert.Equal(2, _storage.GetChain(chain.ChainId)?.Generation);
        Assert.Contains(chain.ChainId, SetItems(CleanupSet));

        _deletions.FailWithReason = null;
        Assert.Equal(3, _storage.DeleteChain(chain.ChainId));

        Assert.Equal(DeletedState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.Equal(DeletedState.StateName, StateOf(retried.Steps["2.0"]));
        Assert.DoesNotContain(chain.ChainId, SetItems(CleanupSet));
    }

    [Fact]
    public void RetryChainFrom_DeletesWhatAnEarlierRetryLeft()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;
        Fail(chain.RootId);
        _deletions.FailWithReason = "Replaced by a retry of the chain";
        JobChainResult first = _storage.RetryChainFrom(chain.RootId);
        Fail(chain.RootId);
        _deletions.FailWithReason = null;

        JobChainResult second = _storage.RetryChainFrom(chain.RootId);

        Assert.Equal(DeletedState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(DeletedState.StateName, StateOf(first.Steps["1.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(second.Steps["1.0"]));
        Assert.DoesNotContain(chain.ChainId, SetItems(CleanupSet));
        Assert.Equal(string.Empty, RecordField(chain.ChainId, "Cleanup"));
    }

    [Fact]
    public void RetryChainFrom_ThatFails_DoesNotCountTheStepsFailureAgain()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c")).Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Fail(chain.Steps["1.0"]);
        long failed = FailedToday();
        _filter.ThrowOn = "c";

        Assert.ThrowsAny<Exception>(() => _storage.RetryChainFrom(chain.Steps["1.0"]));

        Assert.Equal(FailedState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(failed, FailedToday());
        Assert.Equal(1, failed);
    }

    [Fact]
    public void Schedule_KeepsTheRecord_UntilLongAfterTheFirstStepIsDue()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Schedule(_client, TimeSpan.FromDays(60))!;

        Assert.InRange(RecordTimeToLive(chain.ChainId), TimeSpan.FromDays(89.9), TimeSpan.FromDays(90));
    }

    [Fact]
    public void Enqueue_KeepsTheRecord_ForTheRetention()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;

        Assert.InRange(RecordTimeToLive(chain.ChainId), TimeSpan.FromDays(29.9), JobChain.RecordRetention);
    }

    [Fact]
    public void StateChange_OfAStep_KeepsTheRecordLonger()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;

        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(20));
        Assert.InRange(RecordTimeToLive(chain.ChainId), TimeSpan.FromDays(9.9), TimeSpan.FromDays(10));

        Succeed(chain.RootId);

        Assert.InRange(RecordTimeToLive(chain.ChainId), TimeSpan.FromDays(29.9), JobChain.RecordRetention);
    }

    [Fact]
    public void StateChange_ToScheduled_KeepsTheRecord_UntilAfterTheStepIsDue()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;

        Assert.True(_client.ChangeState(chain.RootId, new ScheduledState(TimeSpan.FromDays(45)), null));

        Assert.InRange(RecordTimeToLive(chain.ChainId), TimeSpan.FromDays(74.9), TimeSpan.FromDays(75));
    }

    [Fact]
    public void StateChange_NeverShortensTheLifeOfTheRecord()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Schedule(_client, TimeSpan.FromDays(60))!;

        Assert.True(_client.ChangeState(chain.Steps["1.0"], new DeletedState(), null));

        Assert.InRange(RecordTimeToLive(chain.ChainId), TimeSpan.FromDays(89.9), TimeSpan.FromDays(90));
    }

    [Fact]
    public void RetryChainFrom_NeverShortensTheLifeOfTheRecord()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Schedule(_client, TimeSpan.FromDays(60))!;
        Fail(chain.RootId);

        _storage.RetryChainFrom(chain.RootId);

        Assert.InRange(RecordTimeToLive(chain.ChainId), TimeSpan.FromDays(89.9), TimeSpan.FromDays(90));
    }

    [Fact]
    public void RecurringChain_RunsAChainAtEachTrigger()
    {
        RecurringJobManager manager = new(_storage);
        manager.AddOrUpdateChain("etl", JobChain.StartWith(() => Run("extract")).Then(() => Run("load")), "0 0 1 1 *", skipWhileRunning: true);

        using BackgroundJobServer server = CreateServer();

        foreach (int occurrence in (int[])[1, 2])
        {
            string trigger = manager.TriggerJob("etl")!;
            WaitUntil(() => StateOf(trigger) == SucceededState.StateName, "the recurring chain to be created");
            Assert.True(_storage.TryGetResult(trigger, out string? chainId));
            WaitUntilSettled(chainId!);
            Assert.Equal(2 * occurrence, Ran.Count);
        }

        Assert.Equal(["extract", "load", "extract", "load"], Ran);
    }

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
    public void Server_RemovesChainsWhoseRecordExpired_FromTheList_PastTheOldOnesStillRecorded()
    {
        long old = JobHelper.ToTimestamp(JobClock.UtcNow - JobChain.RecordRetention - TimeSpan.FromDays(10));
        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            // More old chains still recorded (retried, scheduled far ahead) than a page of the list holds
            for (int i = 0; i < 150; i++)
            {
                transaction.AddToSet(JobChains.SetKey, $"kept-{i:000}", old + i);
                transaction.SetRangeInHash(JobChains.GetKey($"kept-{i:000}"), new Dictionary<string, string>
                {
                    ["Jobs"] = "{}",
                    ["CreatedAt"] = JobHelper.SerializeDateTime(JobHelper.FromTimestamp(old + i)),
                });
            }

            for (int i = 0; i < 30; i++)
            {
                transaction.AddToSet(JobChains.SetKey, $"expired-{i:00}", old + 1000 + i);
            }

            transaction.Commit();
        }

        JobChainResult chain = JobChain.StartWith(() => Step("a")).Schedule(_client, TimeSpan.FromHours(1))!;

        using (CreateServer())
        {
            WaitUntil(() => !SetItems(JobChains.SetKey).Any(static x => x.StartsWith("expired", StringComparison.Ordinal)), "the expired chains to leave the list");
        }

        HashSet<string> listed = SetItems(JobChains.SetKey);
        Assert.Equal(151, listed.Count);
        Assert.Contains("kept-149", listed);
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

    private string? ReasonOf(string jobId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetStateData(jobId)?.Reason;
    }

    private IList<StateHistoryDto> History(string jobId) => _storage.GetMonitoringApi().JobDetails(jobId)?.History ?? [];

    private HashSet<string> SetItems(string key)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetAllItemsFromSet(key);
    }

    private string? RecordField(string chainId, string field)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetAllEntriesFromHash(JobChains.GetKey(chainId))?.GetValueOrDefault(field);
    }

    private TimeSpan RecordTimeToLive(string chainId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return ((JobStorageConnection)connection).GetHashTtl(JobChains.GetKey(chainId));
    }

    private long FailedToday()
    {
        using IStorageConnection connection = _storage.GetConnection();
        return ((JobStorageConnection)connection).GetCounter($"stats:failed:{JobClock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
    }

    private void RemoveRecord(string chainId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.RemoveHash(JobChains.GetKey(chainId));
        transaction.RemoveFromSet(JobChains.SetKey, chainId);
        transaction.Commit();
    }

    private JobChainResult CreateWorkflow()
    {
        return JobChain.StartWith(() => Run("a"))
            .Then(() => Run("b"))
            .ThenMany(branches => branches.Branch(() => Run("c")).Branch(() => Run("d")))
            .ThenAfterAll(() => Run("done"))
            .ThenAfterAnyFailed(() => Run("alert"))
            .Enqueue(_client)!;
    }

    private static void AssertWorkflowRan()
    {
        List<string> ran = [.. Ran];
        Assert.Equal(5, ran.Count);
        Assert.Equal(["a", "b"], ran.Take(2));
        Assert.Equal(["c", "d"], ran.Skip(2).Take(2).Order(StringComparer.Ordinal));
        Assert.Equal("done", ran[4]);
    }

    private BackgroundJobServer CreateServer()
    {
        return new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 2, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) }, _storage);
    }

    private void WaitUntilSettled(string chainId) => WaitUntil(() => _storage.GetChain(chainId) is { IsSettled: true }, $"chain {chainId} to settle");

    private static void WaitUntil(Func<bool> condition, string what)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > Timeout)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
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
        public string? FailWithReason { get; set; }

        public void OnStateElection(ElectStateContext context)
        {
            if (FailWithReason != null && context.CandidateState is DeletedState deleted && deleted.Reason == FailWithReason)
            {
                throw new InvalidOperationException("The storage is down.");
            }
        }
    }

    /// <summary>Records the steps created, and fails the creation of the one named <see cref="ThrowOn"/>.</summary>
    private sealed class ThrowingFilter : IClientFilter
    {
        public string? ThrowOn { get; set; }
        public Action? BeforeThrowing { get; set; }
        public string? PauseOn { get; set; }
        public Action? WhilePaused { get; set; }
        public List<(string Name, string Id)> Created { get; } = [];

        public void OnCreating(CreatingContext context)
        {
            if (context.Job.Type == typeof(JobChainStorageFacts) && (string?)context.Job.Args[0] == PauseOn)
            {
                WhilePaused?.Invoke();
            }

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
