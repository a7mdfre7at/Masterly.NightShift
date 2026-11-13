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
    private const string DueSet = "chains:due";
    private const string ExpirySet = "chains:expiring";
    private const string SoonSet = "chains:soon";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentQueue<string> Ran = new();

    // Tests move the clock: locks and fetched jobs mustn't look abandoned for it
    private readonly InMemoryStorage _storage = new(new InMemoryStorageOptions
    {
        DistributedLockLifetime = TimeSpan.FromDays(1),
        InvisibilityTimeout = TimeSpan.FromDays(1),
    });
    private readonly BackgroundJobClient _client;
    private readonly ThrowingFilter _filter = new();
    private readonly FailingDeletions _deletions = new();
    private readonly FailingStarts _starts = new();

    public JobChainStorageFacts()
    {
        _client = new BackgroundJobClient(_storage);
        GlobalJobFilters.Filters.Add(_filter);
        GlobalJobFilters.Filters.Add(_deletions);
        GlobalJobFilters.Filters.Add(_starts);
        Ran.Clear();
    }

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        GlobalJobFilters.Filters.Remove(_filter);
        GlobalJobFilters.Filters.Remove(_deletions);
        GlobalJobFilters.Filters.Remove(_starts);
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Step(string name) => _ = name;

    /// <summary>A step that says it ran.</summary>
    public static void Run(string name) => Ran.Enqueue(name);

    /// <summary>A first step that returns something.</summary>
    public static int Produce(string name) => name.Length + 41;

    /// <summary>A step that says what it was given: the result of the step before it.</summary>
    public static void Consume(string name, [FromResult] int value) => Ran.Enqueue($"{name} {value}");

    /// <summary>A step that takes a moment, and says it ran once it has.</summary>
    public static void Slow(string name)
    {
        Thread.Sleep(1000);
        Ran.Enqueue(name);
    }

    /// <summary>A step that says it ran, and that a test can fail for good by hand.</summary>
    [AutomaticRetry(Attempts = 0)]
    public static void Once(string name) => Ran.Enqueue(name);

    /// <summary>A step that says it ran, and is tried again three times when it fails.</summary>
    [AutomaticRetry(Attempts = 3)]
    public static void Thrice(string name) => Ran.Enqueue(name);

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
    public void Chain_WhoseFirstStepAWorkerTakesWhileItIsCreated_StartsWithoutAnyTraceOfIt()
    {
        // Idle workers take the first step while the client is still creating the others: for a moment, here made
        // long enough to be sure of it
        using BackgroundJobServer server = CreateServer();
        _filter.PauseOn = "c";
        _filter.WhilePaused = static () => Thread.Sleep(400);

        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client)!;
        WaitUntilSettled(chain.ChainId);

        // The worker waited with the step, which wasn't held: its history reads like any job's, and it was neither
        // counted as picked up again nor given a queue to go back to
        Assert.Equal(["a", "b", "c"], Ran);
        Assert.Equal(
            [EnqueuedState.StateName, ProcessingState.StateName, SucceededState.StateName],
            History(chain.RootId).Reverse().Select(static x => x.StateName));
        Assert.Null(Parameter(chain.RootId, "HeldQueue"));
        Assert.Null(Parameter(chain.RootId, "StaleRestarts"));
        Assert.Null(Parameter(chain.RootId, "Lease"));
        Assert.Null(Parameter(chain.RootId, "RetryCount"));
    }

    [Fact]
    public void Chains_CreatedWhileWorkersAreIdle_NeverShowAStepWaitingForItsChain()
    {
        using BackgroundJobServer server = new(new BackgroundJobServerOptions { WorkerCount = 4, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) }, _storage);
        List<JobChainResult> chains = [];

        for (int number = 0; number < 60; number++)
        {
            int current = number;
            chains.Add(JobChain.StartWith(() => Run($"{current}:a")).Then(() => Run($"{current}:b")).Then(() => Run($"{current}:c")).Enqueue(_client)!);
        }

        foreach (JobChainResult chain in chains)
        {
            WaitUntilSettled(chain.ChainId);
        }

        Assert.Equal(180, Ran.Count);
        Assert.All(
            chains.SelectMany(static x => x.Steps.Values),
            id => Assert.DoesNotContain(History(id), static x => x.StateName == ScheduledState.StateName));
        Assert.All(
            chains,
            chain => Assert.Equal(
                [EnqueuedState.StateName, ProcessingState.StateName, SucceededState.StateName],
                History(chain.RootId).Reverse().Select(static x => x.StateName)));
    }

    [Fact]
    public void Chain_WhoseCreatorTakesUnusuallyLong_HasItsFirstStepHeld_AsBefore_AndStillRuns()
    {
        using BackgroundJobServer server = CreateServer();
        string? waiting = null;
        _filter.PauseOn = "c";
        _filter.WhilePaused = () =>
        {
            // Seconds pass, by the clock the lease is read with: the worker no longer waits with the first step
            string first = _filter.Created.Single(static x => x.Name == "a").Id;
            Thread.Sleep(300);
            Assert.Equal(EnqueuedState.StateName, StateOf(first));
            MoveClock(TimeSpan.FromSeconds(6));
            WaitUntil(() => StateOf(first) == ScheduledState.StateName, "the first step to be held");
            waiting = ReasonOf(first);
        };

        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client)!;
        WaitUntilSettled(chain.ChainId);

        Assert.Equal($"Waiting for chain {chain.ChainId} to be recorded", waiting);
        Assert.Equal(["a", "b", "c"], Ran);
        Assert.Contains(History(chain.RootId), static x => x.StateName == EnqueuedState.StateName && x.Reason == "The chain is recorded");
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
    public void Chain_WhoseClientStalled_IsNotReturnedAsCreated_WhenItsFirstStepWasDeletedMeanwhile()
    {
        using BackgroundJobServer server = CreateServer();
        _filter.PauseOn = "c";
        _filter.WhilePaused = () =>
        {
            // The client stalls for longer than its lease on the chain lasts: a server takes the chain for abandoned
            string first = _filter.Created.Single(static x => x.Name == "a").Id;
            MoveClock(TimeSpan.FromMinutes(3));
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
    public void Chain_WhoseFirstStepWaitedWhileTheChainWasCreated_RunsToTheEnd()
    {
        using BackgroundJobServer server = CreateServer();
        _filter.PauseOn = "c";
        _filter.WhilePaused = () =>
        {
            // The first step waits meanwhile; the clock moves, less than the lease lasts, as often as it takes
            string first = _filter.Created.Single(static x => x.Name == "a").Id;
            MoveClock(TimeSpan.FromSeconds(6));
            WaitUntil(() => StateOf(first) == ScheduledState.StateName, "the first step to be held");
            MoveClock(TimeSpan.FromSeconds(90));
            Thread.Sleep(300);
        };

        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client)!;
        WaitUntilSettled(chain.ChainId);

        Assert.Equal(["a", "b", "c"], Ran);
    }

    [Fact]
    public void Chain_WhoseCreationStoppedHalfway_HasItsFirstStepHeld_ThenDeleted_AndTheStepsAfterItDoNotRun()
    {
        // The client stops for good after creating two of three steps: the record it started lists none
        _filter.CrashOn = "c";
        Assert.Throws<OutOfMemoryException>(() => JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client));

        string first = _filter.Created.Single(static x => x.Name == "a").Id;
        string second = _filter.Created.Single(static x => x.Name == "b").Id;
        string chainId = Parameter(first, JobChains.ChainIdParameter)!.Trim('"');

        using BackgroundJobServer server = CreateServer();

        // While the client's lease lasts, the chain may still be recorded: the first step waits, in its queue
        // for the first seconds, then held
        MoveClock(TimeSpan.FromSeconds(6));
        string waiting = $"Waiting for chain {chainId} to be recorded";
        WaitUntil(() => History(first).Any(x => x.StateName == ScheduledState.StateName && x.Reason == waiting), "the first step to be held");
        Assert.Empty(Ran);
        Assert.Equal(AwaitingState.StateName, StateOf(second));
        Assert.Null(_storage.GetChain(chainId));
        Assert.Empty(SetItems(JobChains.SetKey));

        // The lease lapsed: the client isn't creating the chain any more
        MoveClock(TimeSpan.FromMinutes(3));

        WaitUntil(() => StateOf(first) == DeletedState.StateName && StateOf(second) == DeletedState.StateName, "the steps to be deleted");
        Assert.Equal($"Chain {chainId} isn't recorded: it was deleted or never completely created", ReasonOf(first));
        Assert.Empty(Ran);

        // What it started of the record doesn't stay for good, and none of the jobs is marked as recorded
        Assert.InRange(RecordTimeToLive(chainId), TimeSpan.FromDays(29.9), JobChain.RecordRetention);
        Assert.Null(Parameter(first, JobChains.ChainRecordedParameter));
    }

    [Fact]
    public void Chain_ScheduledFarAhead_WhoseCreationStoppedHalfway_DoesNotRunAsHalfAChain_WhenItComesDue()
    {
        // Scheduled in 45 days by a client that stops while it creates the third step
        _filter.CrashOn = "c";
        Assert.Throws<OutOfMemoryException>(
            () => JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Schedule(_client, TimeSpan.FromDays(45)));

        string first = _filter.Created.Single(static x => x.Name == "a").Id;
        string second = _filter.Created.Single(static x => x.Name == "b").Id;
        string chainId = Parameter(first, JobChains.ChainIdParameter)!.Trim('"');

        // What it started of the record is kept until long after the first step is due
        Assert.InRange(RecordTimeToLive(chainId), TimeSpan.FromDays(74.9), TimeSpan.FromDays(75));

        using BackgroundJobServer server = CreateServer();
        MoveClock(TimeSpan.FromDays(46));

        WaitUntil(() => StateOf(first) == DeletedState.StateName && StateOf(second) == DeletedState.StateName, "the steps to be deleted");
        Assert.Equal($"Chain {chainId} isn't recorded: it was deleted or never completely created", ReasonOf(first));
        Assert.Empty(Ran);
    }

    [Fact]
    public void Chain_ScheduledFarAhead_WhoseCreationStoppedHalfway_DoesNotRun_EvenWhenWhatItStartedOfTheRecordIsGone()
    {
        // The storage dropped it (a cap on expiration times), or it expired: the first step is old enough for a
        // record to have expired, but was never listed in one
        _filter.CrashOn = "c";
        Assert.Throws<OutOfMemoryException>(
            () => JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Schedule(_client, TimeSpan.FromDays(45)));

        string first = _filter.Created.Single(static x => x.Name == "a").Id;
        string second = _filter.Created.Single(static x => x.Name == "b").Id;
        string chainId = Parameter(first, JobChains.ChainIdParameter)!.Trim('"');
        RemoveRecord(chainId);

        using BackgroundJobServer server = CreateServer();
        MoveClock(TimeSpan.FromDays(46));

        WaitUntil(() => StateOf(first) == DeletedState.StateName && StateOf(second) == DeletedState.StateName, "the steps to be deleted");
        Assert.Empty(Ran);
    }

    [Fact]
    public void Chain_ThatWasRecorded_StillRuns_OnceItsRecordExpired()
    {
        // Every step is marked when the chain is recorded: a step that is still to run when the record is gone,
        // long after, runs as it always did
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Schedule(_client, TimeSpan.FromDays(45))!;

        // The mark says how long the record is kept, in seconds
        Assert.Equal("2592000", Parameter(chain.RootId, JobChains.ChainRecordedParameter));
        Assert.Equal("2592000", Parameter(chain.Steps["1.0"], JobChains.ChainRecordedParameter));
        RemoveRecord(chain.ChainId);

        using BackgroundJobServer server = CreateServer();
        MoveClock(TimeSpan.FromDays(46));

        WaitUntil(() => Ran.Count == 2, "the steps to run");
        Assert.Equal(["a", "b"], Ran);
    }

    [Fact]
    public void Chain_ThatCanNotDeleteItsScheduledFirstStep_WhenItsCreationFails_LeavesNothingThatRuns()
    {
        _filter.ThrowOn = "c";
        _deletions.FailWithReason = "The chain couldn't be created";

        Assert.ThrowsAny<Exception>(
            () => JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Schedule(_client, TimeSpan.FromDays(45)));
        _deletions.FailWithReason = null;

        // The steps are still there, and what was started of the record is kept for them, without any step
        string first = _filter.Created.Single(static x => x.Name == "a").Id;
        string chainId = Parameter(first, JobChains.ChainIdParameter)!.Trim('"');
        Assert.Equal(ScheduledState.StateName, StateOf(first));
        Assert.Equal("{}", RecordField(chainId, "Jobs"));
        Assert.Empty(SetItems(JobChains.SetKey));

        using BackgroundJobServer server = CreateServer();
        MoveClock(TimeSpan.FromDays(46));

        WaitUntil(() => _filter.Created.All(x => StateOf(x.Id) == DeletedState.StateName), "the steps to be deleted");
        Assert.Empty(Ran);
    }

    [Fact]
    public void Chains_WhoseClientStallsWhileAServerLooksAtThem_AreEitherTakenBackOrRunToTheEnd()
    {
        // The race a chain must never lose: its lease lapsed, a server deletes its first step for it, and the
        // client records the chain at the same moment. Whoever is first, the client never returns a chain that
        // doesn't run, and nothing runs of a chain it didn't return.
        using BackgroundJobServer server = CreateServer();
        List<(int Number, JobChainResult? Chain)> outcomes = [];

        for (int number = 0; number < 150; number++)
        {
            int current = number;
            _filter.PauseOn = $"{current}:c";
            _filter.WhilePaused = () =>
            {
                MoveClock(TimeSpan.FromMinutes(3));
                Thread.Sleep(current % 5 * 30);
            };

            try
            {
                outcomes.Add((current, JobChain.StartWith(() => Run($"{current}:a")).Then(() => Run($"{current}:b")).Then(() => Run($"{current}:c")).Enqueue(_client)));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("wasn't created", StringComparison.Ordinal))
            {
                outcomes.Add((current, null));
            }
        }

        foreach ((int number, JobChainResult? chain) in outcomes.Where(static x => x.Chain != null))
        {
            WaitUntil(() => Ran.Contains($"{number}:c"), $"chain {number}, returned as created, to run to the end (its first step is {StateOf(chain!.RootId)}: {ReasonOf(chain.RootId)})");
            Assert.Contains($"{number}:a", Ran);
            Assert.Contains($"{number}:b", Ran);
        }

        WaitUntil(() => _filter.Created.All(x => StateOf(x.Id) is "Succeeded" or "Deleted"), "every job to finish");

        foreach ((int number, _) in outcomes.Where(static x => x.Chain == null))
        {
            Assert.DoesNotContain(Ran, x => x.StartsWith($"{number}:", StringComparison.Ordinal));
        }

        Assert.Equal(outcomes.Count(static x => x.Chain != null), SetItems(JobChains.SetKey).Count);
    }

    [Fact]
    public void Job_ThatTheRecordOfItsChainDoesNotList_NeverRuns_HoweverYoungItIs()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;

        // Left behind by something that stopped halfway
        string orphan = _client.Create(
            Job.FromExpression(() => Run("orphan")),
            new EnqueuedState(),
            new Dictionary<string, object> { [JobChains.ChainIdParameter] = chain.ChainId, [JobChains.ChainStepParameter] = "1.0" })!;

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

        Assert.True(_client.ChangeState(chain.RootId, new EnqueuedState(), DeletedState.StateName));

        using BackgroundJobServer server = CreateServer();

        WaitUntil(() => History(chain.RootId).Count(static x => x.StateName == DeletedState.StateName) == 2, "the step to be deleted again");
        Assert.Equal($"Chain {chain.ChainId} isn't recorded: it was deleted or never completely created", ReasonOf(chain.RootId));
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
        Assert.Equal(EnqueuedState.StateName, StateOf(chain.RootId));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Contains(chain.RootId, RecordField(chain.ChainId, "Cleanup"), StringComparison.Ordinal);
        Assert.Contains(chain.ChainId, SetItems(CleanupSet));

        // A worker takes the old first step, which is still in its queue while its deletion fails
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
    public void ReplaceChain_NeverMovesTheOldFirstStep_OutOfItsStateOrItsTime()
    {
        DateTimeOffset nextMonth = DateTimeOffset.UtcNow.AddDays(30);
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Schedule(_client, nextMonth)!;
        int states = History(chain.RootId).Count;
        _filter.ThrowOn = "y";

        Assert.ThrowsAny<Exception>(() => _storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Run("x")).Then(() => Run("y"))));

        Assert.Equal(states, History(chain.RootId).Count);
        Assert.Equal(nextMonth.UtcDateTime, ScheduledFor(chain.RootId));
        Assert.DoesNotContain("changing", RecordField(chain.ChainId, "Jobs"), StringComparison.Ordinal);

        _filter.ThrowOn = null;
        JobChainResult replaced = _storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Run("x")).Then(() => Run("y")));

        // Deleted from the state it was in all along, and the new first step is due when the old one was
        Assert.Equal([ScheduledState.StateName, DeletedState.StateName], History(chain.RootId).Reverse().Select(static x => x.StateName).TakeLast(2));
        Assert.Equal(nextMonth.UtcDateTime, ScheduledFor(replaced.RootId));
    }

    [Theory]
    [InlineData("before the first new job")]
    [InlineData("between two new jobs")]
    public void ReplaceChain_ThatStopsBeforeItIsRecorded_LeavesTheOldChainToRun_AndNothingOfTheNewOneRuns(string stage)
    {
        DateTimeOffset due = DateTimeOffset.UtcNow.AddDays(30);
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Schedule(_client, due)!;
        _filter.CrashOn = stage == "before the first new job" ? "x" : "y";

        // The process stops: nothing is rolled back
        Assert.Throws<OutOfMemoryException>(() => _storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Run("x")).Then(() => Run("y"))));

        // The old first step wasn't touched: still due next month
        Assert.Equal(due.UtcDateTime, ScheduledFor(chain.RootId));
        Assert.Equal(chain.Steps.Values, _storage.GetChain(chain.ChainId)!.Steps.Select(static x => x.JobId));

        using BackgroundJobServer server = CreateServer();

        // An hour later nothing ran: not the old chain before its time, not the new one
        MoveClock(TimeSpan.FromHours(1));
        Thread.Sleep(700);
        Assert.Empty(Ran);
        Assert.Equal(ScheduledState.StateName, StateOf(chain.RootId));

        // When the old chain is due, it runs, and only it
        MoveClock(TimeSpan.FromDays(30));
        WaitUntilSettled(chain.ChainId);
        WaitUntil(() => _filter.Created.Where(static x => x.Name is "x" or "y").All(x => StateOf(x.Id) == DeletedState.StateName), "the new jobs to be deleted");

        Assert.Equal(["a", "b"], Ran);
    }

    [Fact]
    public void ReplaceChain_ThatStopsOnceItIsRecorded_IsFinishedByAServer_AndOnlyTheNewChainRuns()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Enqueue(_client)!;
        _deletions.CrashWithReason = "The chain was replaced";

        Assert.Throws<OutOfMemoryException>(() => _storage.ReplaceChain(chain.ChainId, JobChain.StartWith(() => Run("x")).Then(() => Run("y"))));
        _deletions.CrashWithReason = null;

        using BackgroundJobServer server = CreateServer();
        WaitUntilSettled(chain.ChainId);
        WaitUntil(() => chain.Steps.Values.All(id => StateOf(id) == DeletedState.StateName), "the old steps to be deleted");
        WaitUntil(() => !SetItems(CleanupSet).Contains(chain.ChainId), "the replacement to be finished");

        Assert.Equal(["x", "y"], Ran);
        Assert.Equal(2, _storage.GetChain(chain.ChainId)?.Generation);
    }

    [Theory]
    [InlineData("before the first new job")]
    [InlineData("between two new jobs")]
    public void RetryChainFrom_ThatStopsBeforeItIsRecorded_PutsItsStepBackAsItWas_AndRunsNothing(string stage)
    {
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Then(() => Once("c")).Then(() => Once("d")).Enqueue(_client)!;
        string step = chain.Steps["1.0"];
        Succeed(chain.Steps["0.0"]);
        Fail(step);
        StateData failed = StateData(step)!;
        long failures = FailedToday();
        _filter.Created.Clear();
        _filter.CrashOn = stage == "before the first new job" ? "c" : "d";

        // The step failed by hand left its place in the queue behind: the server finds nothing to run there, and
        // clears it, before the retry begins
        using BackgroundJobServer server = CreateServer();
        Thread.Sleep(500);

        // The process stops: nothing is rolled back, the step is out of its Failed state, and says what the retry
        // started from
        Assert.Throws<OutOfMemoryException>(() => _storage.RetryChainFrom(step));
        _filter.CrashOn = null;

        Assert.Equal(ScheduledState.StateName, StateOf(step));
        Assert.Contains("\"generation\":1", Parameter(step, "ChainRetry"), StringComparison.Ordinal);
        Assert.Equal(chain.Steps.Values, _storage.GetChain(chain.ChainId)!.Steps.Select(static x => x.JobId));

        // While the lease of the process that stopped lasts, and the step's hold, nothing runs
        Thread.Sleep(500);
        Assert.Empty(Ran);

        // Then the step goes back to the state it was in: the retry never happened
        MoveClock(TimeSpan.FromMinutes(3));
        WaitUntil(() => StateOf(step) == FailedState.StateName, "the step to be put back");

        StateData restored = StateData(step)!;
        Assert.Equal($"The retry of chain {chain.ChainId} was abandoned", restored.Reason);
        Assert.Equal(failed.Data.OrderBy(static x => x.Key, StringComparer.Ordinal), restored.Data.OrderBy(static x => x.Key, StringComparer.Ordinal));
        Assert.Null(Parameter(step, "ChainRetry"));
        Assert.Equal(failures, FailedToday());

        // Its history tells what happened, and not that it ran
        Assert.Equal(
            [FailedState.StateName, ScheduledState.StateName, EnqueuedState.StateName, FailedState.StateName],
            History(step).Reverse().Select(static x => x.StateName).TakeLast(4));

        // The chain is as it was: the same steps, the ones after the failure still waiting, and settled
        JobChainInfo info = _storage.GetChain(chain.ChainId)!;
        Assert.Equal(1, info.Generation);
        Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
        Assert.True(info.IsSettled);
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["3.0"]));

        // What the retry had created is deleted, and nothing runs, now or later
        WaitUntil(() => _filter.Created.All(x => StateOf(x.Id) == DeletedState.StateName), "the jobs the retry created to be deleted");
        MoveClock(TimeSpan.FromMinutes(30));
        Thread.Sleep(700);
        Assert.Empty(Ran);
        Assert.Equal(FailedState.StateName, StateOf(step));

        // And the chain can be retried again, completely this time
        _storage.RetryChainFrom(step);
        WaitUntil(() => Ran.Count == 3, "the retried steps to run");

        Assert.Equal(["b", "c", "d"], Ran);
        Assert.Null(Parameter(step, "ChainRetry"));
    }

    [Fact]
    public void RetryChainFrom_WhoseClientStalledUntilAServerPutItsStepBack_Fails_AndLeavesTheChainAsItWas()
    {
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Then(() => Once("c")).Then(() => Once("d")).Enqueue(_client)!;
        string step = chain.Steps["1.0"];
        Succeed(chain.Steps["0.0"]);
        Fail(step);
        _filter.Created.Clear();

        using BackgroundJobServer server = CreateServer();
        Thread.Sleep(300);
        _filter.PauseOn = "d";
        _filter.WhilePaused = () =>
        {
            // The client stalls for longer than its lease and the hold of the step last: a server takes the retry
            // for abandoned and puts the step back
            MoveClock(TimeSpan.FromMinutes(3));
            WaitUntil(() => StateOf(step) == FailedState.StateName, "the step to be put back");
        };

        // The client wakes up: it finds that the step is no longer held for it, and takes the retry back
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _storage.RetryChainFrom(step));

        Assert.Contains("wasn't retried", exception.Message, StringComparison.Ordinal);
        Assert.Contains("as it was", exception.Message, StringComparison.Ordinal);
        JobChainInfo info = _storage.GetChain(chain.ChainId)!;
        Assert.Equal(1, info.Generation);
        Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
        Assert.Equal(FailedState.StateName, StateOf(step));
        Assert.Equal($"The retry of chain {chain.ChainId} was abandoned", ReasonOf(step));
        Assert.All(_filter.Created, created => Assert.Equal(DeletedState.StateName, StateOf(created.Id)));
        Assert.DoesNotContain("changing", RecordField(chain.ChainId, "Jobs"), StringComparison.Ordinal);
        Assert.Null(Parameter(step, "ChainRetry"));
        Assert.Empty(Ran);
    }

    [Fact]
    public void RetryChainFrom_ThatTakesLongerThanItsStepIsHeld_Succeeds_WhileItsClientStillSaysItIsAtIt()
    {
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Then(() => Once("c")).Then(() => Once("d")).Enqueue(_client)!;
        string step = chain.Steps["1.0"];
        Succeed(chain.Steps["0.0"]);
        Fail(step);

        using BackgroundJobServer server = CreateServer();
        Thread.Sleep(300);
        _filter.PauseOn = "d";
        _filter.WhilePaused = () =>
        {
            // The client says again that it is at it, as it does between two steps of a long retry; then more
            // time passes than the step is held for. The step comes up, and waits: the retry isn't abandoned.
            RenewLease(chain.ChainId, TimeSpan.FromMinutes(5));
            MoveClock(TimeSpan.FromMinutes(3));
            WaitUntil(() => ReasonOf(step) == $"Waiting for chain {chain.ChainId} to be recorded", "the step to wait for the retry");
        };

        JobChainResult retried = _storage.RetryChainFrom(step);
        WaitUntil(() => Ran.Count == 3, "the retried steps to run");

        Assert.Equal(["b", "c", "d"], Ran);
        Assert.Equal(2, _storage.GetChain(chain.ChainId)?.Generation);
        Assert.Equal(step, retried.Steps["1.0"]);
        Assert.Null(Parameter(step, "ChainRetry"));
    }

    [Fact]
    public void RetryChainFrom_ThatIsAbandoned_DoesNotRunTheStepsItCreatedForAFailure_WhenItsStepGoesBackToFailed()
    {
        // The retried step is put back to Failed: a new step that waits for a failure would start for it
        JobChainResult chain = JobChain.StartWith(() => Once("a"))
            .Then(() => Once("b"))
            .ThenAfterAnyFailed(() => Once("alert"))
            .Then(() => Once("end"), JobContinuationOptions.OnAnyFinishedState)
            .Enqueue(_client)!;
        string step = chain.Steps["1.0"];
        Succeed(chain.Steps["0.0"]);
        Fail(step);
        Succeed(chain.Steps["2.0"]);
        Succeed(chain.Steps["3.0"]);
        _filter.Created.Clear();
        _filter.CrashOn = "end";

        // The steps moved by hand left their place in the queue behind: the server finds nothing to run there,
        // and clears it, before the retry begins
        using BackgroundJobServer server = CreateServer();
        Thread.Sleep(500);

        Assert.Throws<OutOfMemoryException>(() => _storage.RetryChainFrom(step));
        _filter.CrashOn = null;
        string alert = _filter.Created.Single(static x => x.Name == "alert").Id;

        MoveClock(TimeSpan.FromMinutes(3));

        WaitUntil(() => StateOf(step) == FailedState.StateName, "the step to be put back");
        WaitUntil(() => StateOf(alert) == DeletedState.StateName, "the step the retry created to be deleted");
        Thread.Sleep(500);

        Assert.Empty(Ran);
        Assert.Equal(SucceededState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.Equal(SucceededState.StateName, StateOf(chain.Steps["3.0"]));
    }

    [Fact]
    public void RetryChainFrom_ASucceededStep_ThatIsAbandoned_PutsItBackAsSucceeded_WithoutCountingOrStartingAnythingAgain()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client)!;
        string step = chain.Steps["1.0"];

        using (CreateServer())
        {
            WaitUntilSettled(chain.ChainId);
        }

        Assert.Equal(["a", "b", "c"], Ran);
        StateData succeeded = StateData(step)!;
        long counted = Counter("stats:succeeded");
        int states = History(chain.Steps["2.0"]).Count;
        DateTime expiresAt = ExpiresAt(step)!.Value;
        _filter.CrashOn = "c";

        // Held for a retry that stops before it created anything: the job is kept while it is held
        Assert.Throws<OutOfMemoryException>(() => _storage.RetryChainFrom(step));
        _filter.CrashOn = null;
        Assert.Equal(ScheduledState.StateName, StateOf(step));
        Assert.Null(ExpiresAt(step));

        using BackgroundJobServer server = CreateServer();
        MoveClock(TimeSpan.FromMinutes(3));
        WaitUntil(() => StateOf(step) == SucceededState.StateName, "the step to be put back");
        Thread.Sleep(500);

        // Succeeded as it was, with its result and its time: it expires again, isn't counted a second time, and
        // the step after it, which it started the first time, isn't started again
        StateData restored = StateData(step)!;
        Assert.Equal($"The retry of chain {chain.ChainId} was abandoned", restored.Reason);
        Assert.Equal(succeeded.Data.OrderBy(static x => x.Key, StringComparer.Ordinal), restored.Data.OrderBy(static x => x.Key, StringComparer.Ordinal));
        Assert.InRange(ExpiresAt(step)!.Value, expiresAt.AddSeconds(-5), expiresAt.AddSeconds(5));
        Assert.Equal(counted, Counter("stats:succeeded"));
        Assert.Equal(states, History(chain.Steps["2.0"]).Count);
        Assert.Equal(["a", "b", "c"], Ran);
        Assert.Null(Parameter(step, "ChainRetry"));
    }

    [Fact]
    public void RetryChainFrom_ThatFails_PutsASucceededStepBack_ToExpireWhenItWasTo()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a")).Then(() => Run("b")).Then(() => Run("c")).Enqueue(_client)!;
        string step = chain.Steps["1.0"];

        using (CreateServer())
        {
            WaitUntilSettled(chain.ChainId);
        }

        // Two hours later, a retry that can't create its new step: the step goes back to Succeeded
        DateTime expiresAt = ExpiresAt(step)!.Value;
        long counted = Counter("stats:succeeded");
        MoveClock(TimeSpan.FromHours(2));
        _filter.ThrowOn = "c";

        Assert.ThrowsAny<Exception>(() => _storage.RetryChainFrom(step));

        // As it was: not kept for a whole day from now, and not counted again
        Assert.Equal(SucceededState.StateName, StateOf(step));
        Assert.InRange(ExpiresAt(step)!.Value, expiresAt.AddSeconds(-5), expiresAt.AddSeconds(5));
        Assert.Equal(counted, Counter("stats:succeeded"));
        Assert.Null(Parameter(step, "ChainRetry"));
    }

    [Fact]
    public void Step_PutBackAfterAnAbandonedRetry_IsNotToldAboutAsIfItHadFailedAgain()
    {
        CountingSink sink = new();
        AlertsFilter alerts = new();
        GlobalJobFilters.Filters.Add(alerts);
        Alerts.AddSink(sink);
        ActivityFeed.Enabled = true;

        try
        {
            JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Thrice("b")).Then(() => Once("c")).Enqueue(_client)!;
            string step = chain.Steps["1.0"];
            Succeed(chain.Steps["0.0"]);

            // The step fails for good: its attempts are used up
            SetParameter(step, "RetryCount", "3");
            Fail(step);
            Assert.Equal(FailedState.StateName, StateOf(step));
            Assert.Equal(1, sink.Failures);

            // Were its failure taken for a new one later, it would be retried: attempts are left again
            SetParameter(step, "RetryCount", "0");

            // The step failed by hand left its place in the queue behind: the server finds nothing to run there,
            // and clears it, before the retry begins
            using BackgroundJobServer server = CreateServer();
            Thread.Sleep(500);
            _filter.CrashOn = "c";
            Assert.Throws<OutOfMemoryException>(() => _storage.RetryChainFrom(step));
            _filter.CrashOn = null;

            MoveClock(TimeSpan.FromMinutes(3));
            WaitUntil(() => ReasonOf(step) == $"The retry of chain {chain.ChainId} was abandoned", "the step to be put back");
            WaitUntil(() => Feed().Any(x => x.JobId == step && x.State == EnqueuedState.StateName), "the feed to tell that the hold of the step ended");
            Thread.Sleep(500);

            // Failed as it was: no retry of its own, no alert, and the feed doesn't say that it failed again
            Assert.Equal(FailedState.StateName, StateOf(step));
            Assert.Equal("0", Parameter(step, "RetryCount"));
            Assert.Equal(1, sink.Failures);
            Assert.Single(Feed(), x => x.JobId == step && x.State == FailedState.StateName);
            Assert.Empty(Ran);
        }
        finally
        {
            ActivityFeed.Enabled = false;
            Alerts.Clear();
            GlobalJobFilters.Filters.Remove(alerts);
        }
    }

    [Fact]
    public void Step_RequeuedByHand_Runs_ThoughARetryThatStoppedBeforeItHeldTheStep_LeftItsNoteOnTheJob()
    {
        // What a retry leaves on a storage that can't write a job parameter with a state, when its process stops
        // between the two: the note of what it started from, on a step it never held
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Enqueue(_client)!;
        Fail(chain.RootId);

        // The step failed by hand left its place in the queue behind: the server finds nothing to run there, and
        // clears it
        using BackgroundJobServer server = CreateServer();
        Thread.Sleep(500);
        SetParameter(chain.RootId, "ChainRetry", "{\"id\":\"0123\",\"generation\":1,\"state\":\"Failed\",\"data\":{}}");

        // Somebody requeues the failed step: it must run, not be put back as the step of an abandoned retry
        Assert.True(_client.ChangeState(chain.RootId, new EnqueuedState(), FailedState.StateName));
        WaitUntilSettled(chain.ChainId);
        Assert.Null(Parameter(chain.RootId, "ChainRetry"));

        Assert.Equal(["a", "b"], Ran);
    }

    [Fact]
    public void Step_HeldForARetry_ThatSomebodyDeletesAndRequeues_RunsAsAnyRequeuedStep()
    {
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Enqueue(_client)!;
        Fail(chain.RootId);

        // The step failed by hand left its place in the queue behind: the server finds nothing to run there, and
        // clears it, before the retry begins
        using BackgroundJobServer server = CreateServer();
        Thread.Sleep(500);
        _filter.CrashOn = "b";
        Assert.Throws<OutOfMemoryException>(() => _storage.RetryChainFrom(chain.RootId));
        _filter.CrashOn = null;

        // The held step is deleted by hand, and requeued later: that is no longer the retry's business
        Assert.True(_client.ChangeState(chain.RootId, new DeletedState(), ScheduledState.StateName));
        Assert.Null(Parameter(chain.RootId, "ChainRetry"));
        MoveClock(TimeSpan.FromMinutes(3));
        Assert.True(_client.ChangeState(chain.RootId, new EnqueuedState(), DeletedState.StateName));

        // It runs, once; the step after it was skipped when it was deleted
        WaitUntil(() => StateOf(chain.RootId) == SucceededState.StateName, "the requeued step to run");
        Assert.Equal(["a"], Ran);
    }

    [Fact]
    public void RetryChainFrom_ThatStopsOnceItIsRecorded_IsFinishedByAServer_AndOnlyTheNewStepsRun()
    {
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Then(() => Once("c")).Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Fail(chain.Steps["1.0"]);
        _deletions.CrashWithReason = "Replaced by a retry of the chain";

        Assert.Throws<OutOfMemoryException>(() => _storage.RetryChainFrom(chain.Steps["1.0"]));
        _deletions.CrashWithReason = null;

        // Recorded, the step still held and the old step after it not deleted yet
        JobChainInfo retried = _storage.GetChain(chain.ChainId)!;
        Assert.Equal(2, retried.Generation);
        Assert.Equal(ScheduledState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["2.0"]));

        // A server releases the step and deletes the old one, without waiting for the hold to end
        using BackgroundJobServer server = CreateServer();
        WaitUntilSettled(chain.ChainId);

        Assert.Equal(["b", "c"], Ran);
        Assert.Equal(DeletedState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.Equal(SucceededState.StateName, StateOf(retried.Steps.Single(static x => x.Key == "2.0").JobId));
    }

    [Fact]
    public void RetryChainFrom_IsTakenBack_WhenAServerDeletedOneOfItsNewSteps_WhileTheClientStalled()
    {
        // b failed and is retried; c, beside it, failed too, so the new step for "any failure" starts as soon as it
        // is created, and waits for the retry to be recorded
        JobChainResult chain = JobChain.StartWith(() => Once("a"))
            .ThenMany(branches => branches.Branch(() => Once("b")).Branch(() => Once("c")))
            .ThenAfterAnyFailed(() => Once("alert"))
            .Then(() => Once("end"), JobContinuationOptions.OnAnyFinishedState)
            .Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Fail(chain.Steps["1.0"]);
        Fail(chain.Steps["1.1"]);
        Succeed(chain.Steps["2.0"]);
        Succeed(chain.Steps["3.0"]);
        _filter.Created.Clear();

        // The steps failed by hand left their place in the queue behind: the server finds nothing to run there,
        // and clears it, before the retry begins
        using BackgroundJobServer server = CreateServer();
        Thread.Sleep(500);
        _filter.PauseOn = "end";
        _filter.WhilePaused = () =>
        {
            // The client stalls inside the creation of the last step for longer than its lease lasts: a server
            // takes the retry for abandoned, and deletes the new step that was waiting to run
            string alert = _filter.Created.Single(static x => x.Name == "alert").Id;
            WaitUntil(() => StateOf(alert) == ScheduledState.StateName, "the new step to wait for the retry");
            MoveClock(TimeSpan.FromMinutes(3));
            WaitUntil(() => StateOf(alert) == DeletedState.StateName, "the new step to be deleted");
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _storage.RetryChainFrom(chain.Steps["1.0"]));

        // Not returned as retried with a step that never runs: nothing changed
        Assert.Contains("wasn't retried", exception.Message, StringComparison.Ordinal);
        JobChainInfo info = _storage.GetChain(chain.ChainId)!;
        Assert.Equal(1, info.Generation);
        Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
        Assert.Equal(FailedState.StateName, StateOf(chain.Steps["1.0"]));
        Assert.All(_filter.Created, step => Assert.Equal(DeletedState.StateName, StateOf(step.Id)));
        Assert.DoesNotContain("changing", RecordField(chain.ChainId, "Jobs"), StringComparison.Ordinal);
        Assert.Empty(Ran);
    }

    [Fact]
    public void RetryChainFrom_HoldsItsStep_ALittleLongerThanItsClientCountsAsBeingAtIt()
    {
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Enqueue(_client)!;
        Fail(chain.RootId);
        _filter.CrashOn = "b";

        Assert.Throws<OutOfMemoryException>(() => _storage.RetryChainFrom(chain.RootId));

        // Two minutes, and a few seconds: when the hold ends, a client that stopped no longer counts as at it
        Assert.InRange(ScheduledFor(chain.RootId) - DateTime.UtcNow, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2.5));
    }

    [Fact]
    public void Join_ThatWasNeverToldToStart_IsStartedByAServer_AndTheChainIsNotUnsettledMeanwhile()
    {
        JobChainResult chain = JobChain.StartWith(() => Run("a"))
            .ThenMany(branches => branches.Branch(() => Run("c")).Branch(() => Run("d")))
            .ThenAfterAll(() => Run("done"))
            .Enqueue(_client)!;
        _starts.FailFor = "done";

        // Both branches finish, and the step after them can't be started: it stays where it is
        using (CreateServer())
        {
            WaitUntilTheJoinIsGivenUp();
        }

        Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["2.0"]));
        Assert.False(_storage.GetChain(chain.ChainId)!.IsSettled);
        _starts.FailFor = null;

        // Later nobody is about to start it any more: the chain counts as settled rather than running for good
        MoveClock(TimeSpan.FromMinutes(6));
        Assert.True(_storage.GetChain(chain.ChainId)!.IsSettled);

        // A server finds it and starts it
        using (CreateServer())
        {
            WaitUntil(() => Ran.Contains("done"), "the join to run");
            WaitUntilSettled(chain.ChainId);
        }

        Assert.Equal(1, Ran.Count(static x => x == "done"));
        Assert.Equal(SucceededState.StateName, StateOf(chain.Steps["2.0"]));
    }

    [Fact]
    public void RecurringChain_StartsTheJoinOfThePreviousChain_ThatWasNeverToldToStart_RatherThanSkippingForGood()
    {
        RecurringJobManager manager = new(_storage);
        manager.AddOrUpdateChain(
            "etl",
            JobChain.StartWith(() => Run("a")).ThenMany(branches => branches.Branch(() => Run("c")).Branch(() => Run("d"))).ThenAfterAll(() => Slow("done")),
            "0 0 1 1 *",
            skipWhileRunning: true);
        _starts.FailFor = "done";

        using BackgroundJobServer server = CreateServer();

        string first = manager.TriggerJob("etl")!;
        WaitUntilTheJoinIsGivenUp();
        _starts.FailFor = null;
        Assert.DoesNotContain("done", Ran);

        // The next occurrence, long after: the join is started, and the occurrence skipped while it runs
        MoveClock(TimeSpan.FromMinutes(6));
        string second = manager.TriggerJob("etl")!;
        WaitUntil(() => StateOf(second) == DeletedState.StateName, "the occurrence to be skipped");
        Assert.Contains("still running", ReasonOf(second), StringComparison.Ordinal);
        WaitUntil(() => Ran.Contains("done"), "the join of the previous chain to run");

        // And the one after it runs
        Assert.True(_storage.TryGetResult(first, out string? previous));
        WaitUntilSettled(previous!);
        string third = manager.TriggerJob("etl")!;
        WaitUntil(() => Ran.Count(static x => x == "done") == 2, "the next chain to run");

        Assert.Equal(SucceededState.StateName, StateOf(first));
        Assert.Equal(SucceededState.StateName, StateOf(third));
        Assert.Equal(2, Ran.Count(static x => x == "a"));
    }

    [Fact]
    public void RecurringChain_GoesOn_AfterADeployRenamedOrRemovedAMethodOfThePreviousChain()
    {
        RecurringJobManager manager = new(_storage);
        manager.AddOrUpdateChain("etl", JobChain.StartWith(() => Run("extract")).Then(() => Run("load")), "0 0 1 1 *", skipWhileRunning: true);

        using BackgroundJobServer server = CreateServer();

        string first = manager.TriggerJob("etl")!;
        WaitUntil(() => StateOf(first) == SucceededState.StateName, "the recurring chain to be created");
        Assert.True(_storage.TryGetResult(first, out string? previous));
        WaitUntilSettled(previous!);

        // The record of the previous chain now names a method that can't be loaded
        ForgetTheMethods(previous!);

        string second = manager.TriggerJob("etl")!;
        WaitUntil(() => Ran.Count == 4, "the next chain to run");

        Assert.Equal(SucceededState.StateName, StateOf(second));
        Assert.Equal(["extract", "load", "extract", "load"], Ran);
    }

    [Fact]
    public void Chain_WhoseStepsMethodsWereRenamedOrRemoved_CanStillBeRead_Retried_WhereItNeedsNoneOfThem_AndDeleted()
    {
        JobChainResult chain = JobChain.StartWith(() => Once("a")).Then(() => Once("b")).Then(() => Once("c")).Enqueue(_client)!;
        Succeed(chain.Steps["0.0"]);
        Succeed(chain.Steps["1.0"]);
        Fail(chain.Steps["2.0"]);
        ForgetTheMethods(chain.ChainId);

        // Read: the steps and their states, without their method calls
        JobChainInfo info = _storage.GetChain(chain.ChainId)!;
        Assert.True(info.IsSettled);
        Assert.Equal(chain.Steps.Values, info.Steps.Select(static x => x.JobId));
        Assert.All(info.Steps, static step => Assert.Null(step.Job));

        // Retried from a step whose following steps must be created again: refused, and nothing changes
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _storage.RetryChainFrom(chain.Steps["1.0"]));
        Assert.Contains("can't be created again", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SucceededState.StateName, StateOf(chain.Steps["1.0"]));

        // Retried from its last step: no method is needed for that
        _storage.RetryChainFrom(chain.Steps["2.0"]);
        Assert.Equal(EnqueuedState.StateName, StateOf(chain.Steps["2.0"]));

        // Deleted
        Assert.Equal(1, _storage.DeleteChain(chain.ChainId));
        Assert.Null(_storage.GetChain(chain.ChainId));
    }

    [Fact]
    public void RecurringChain_RunAgainAfterItsChainWasRecorded_DoesNotCreateASecondChain()
    {
        // The job that creates the chain is run again, as after a process that stopped before the job finished
        RecurringJobManager manager = new(_storage);
        manager.AddOrUpdateChain("etl", JobChain.StartWith(() => Run("extract")).Then(() => Run("load")), "0 0 1 1 *");

        using BackgroundJobServer server = CreateServer();

        string occurrence = manager.TriggerJob("etl")!;
        WaitUntil(() => StateOf(occurrence) == SucceededState.StateName, "the recurring chain to be created");
        Assert.True(_storage.TryGetResult(occurrence, out string? chainId));
        WaitUntilSettled(chainId!);

        Assert.True(_client.ChangeState(occurrence, new EnqueuedState(), SucceededState.StateName));
        WaitUntil(() => History(occurrence).Count(static x => x.StateName == SucceededState.StateName) == 2, "the job to run again");

        Assert.True(_storage.TryGetResult(occurrence, out string? again));
        Assert.Equal(chainId, again);
        Assert.Single(SetItems(JobChains.SetKey));
        Thread.Sleep(300);
        Assert.Equal(["extract", "load"], Ran);
    }

    [Fact]
    public void Job_HeldInAQueue_KeepsIt_WhenItsSchedulePolicyBlocksItAgain()
    {
        // A queue given only by the Enqueued state is remembered when a policy moves the job forward, and must
        // still be when the policy moves it forward a second time
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        _storage.SaveSchedulePolicy(new SchedulePolicy("closed").ExcludeDate(today).ExcludeDate(today.AddDays(1)));
        string jobId = _client.Create(
            Job.FromExpression(() => Run("queued")),
            new EnqueuedState("critical"),
            new Dictionary<string, object> { [ScheduleOptions.PolicyParameter] = "closed" })!;

        Assert.Equal(ScheduledState.StateName, StateOf(jobId));
        Assert.Equal("\"critical\"", Parameter(jobId, "HeldQueue"));

        // The server listens to the default queue only
        using BackgroundJobServer server = CreateServer();

        // Due while the policy still blocks: moved forward again
        Assert.True(_client.ChangeState(jobId, new ScheduledState(TimeSpan.FromMilliseconds(100)), ScheduledState.StateName));
        WaitUntil(() => ReasonOf(jobId)?.StartsWith("Moved by policy", StringComparison.Ordinal) == true, "the job to be moved forward again");
        Assert.Equal("\"critical\"", Parameter(jobId, "HeldQueue"));

        // Due once the policy allows it: enqueued where it was to go
        _storage.SaveSchedulePolicy(new SchedulePolicy("closed"));
        Assert.True(_client.ChangeState(jobId, new ScheduledState(TimeSpan.FromMilliseconds(100)), ScheduledState.StateName));
        WaitUntil(() => StateOf(jobId) == EnqueuedState.StateName, "the job to be enqueued");

        Assert.Equal("critical", StateData(jobId)?.Data["Queue"]);
        Thread.Sleep(300);
        Assert.Empty(Ran);
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
        Assert.Null(Parameter(chain.Steps["1.0"], "ChainRetry"));
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
    public void Server_KeepsAChainWithAFailedStep_ForAsLongAsTheJobExists_AndUnlistsAChainThatFinished_OnceItsRecordExpired()
    {
        JobChainResult failed = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;
        JobChainResult finished = JobChain.StartWith(() => Step("x")).Then(() => Step("y")).Enqueue(_client)!;
        Fail(failed.RootId);
        Succeed(finished.RootId);
        Succeed(finished.Steps["1.0"]);

        // A server looks at both a few minutes after they were recorded: nothing of the second is left to run
        MoveClock(TimeSpan.FromMinutes(6));
        using (CreateServer())
        {
            WaitUntil(() => SetItems(ExpirySet).Contains(finished.ChainId), "the chain that finished to be left to expire");
        }

        Assert.Contains(failed.ChainId, SetItems(DueSet));
        Assert.DoesNotContain(finished.ChainId, SetItems(DueSet));

        // Nobody touches the failed chain: before its record expires, a server keeps it longer
        MoveClock(TimeSpan.FromDays(25));
        Assert.InRange(RecordTimeToLive(failed.ChainId), TimeSpan.FromDays(4.9), TimeSpan.FromDays(5));
        using (CreateServer())
        {
            WaitUntil(() => RecordTimeToLive(failed.ChainId) > TimeSpan.FromDays(29.9), "the record of the failed chain to be kept longer");
        }

        // The record of the chain that finished expires, and the chain leaves the list
        MoveClock(TimeSpan.FromDays(6));
        using (CreateServer())
        {
            WaitUntil(() => !SetItems(JobChains.SetKey).Contains(finished.ChainId), "the chain whose record expired to leave the list");
        }

        Assert.Empty(SetItems(ExpirySet));
        Assert.Contains(failed.ChainId, SetItems(JobChains.SetKey));

        // And so on, for as long as servers run: months later the failed chain can still be read, and run again
        for (int i = 0; i < 4; i++)
        {
            MoveClock(TimeSpan.FromDays(23));
            using (CreateServer())
            {
                WaitUntil(() => RecordTimeToLive(failed.ChainId) > TimeSpan.FromDays(29.9), "the record of the failed chain to be kept longer");
            }
        }

        MoveClock(TimeSpan.FromDays(20));
        Assert.Equal(1, _storage.GetChain(failed.ChainId)?.FailedCount);

        JobChainResult retried = _storage.RetryChainFrom(failed.RootId);

        Assert.Equal(EnqueuedState.StateName, StateOf(failed.RootId));
        Assert.Equal(AwaitingState.StateName, StateOf(retried.Steps["1.0"]));
    }

    [Fact]
    public void Server_LooksSoonAtAChain_WhoseFailedStepIsRequeued_HoweverLongItStoodStill()
    {
        JobChainResult chain = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;
        Fail(chain.RootId);

        // Ten days after the failure a server looks at the chain, and won't again for as long
        MoveClock(TimeSpan.FromDays(10));
        using (CreateServer())
        {
            WaitUntil(() => !IsDueWithin(chain.ChainId, TimeSpan.FromDays(9)), "the chain to be looked at");
        }

        // The failed step is requeued, and succeeds, by a process that stops before it starts the next step. The
        // requeue asks for a look in five minutes, and leaves the chain's own place where it is.
        Assert.True(_client.ChangeState(chain.RootId, new EnqueuedState(), FailedState.StateName));
        Assert.True(IsAskedForWithin(chain.ChainId, TimeSpan.FromMinutes(5)));
        Assert.False(IsAskedForWithin(chain.ChainId, TimeSpan.FromMinutes(4)));
        Assert.False(IsDueWithin(chain.ChainId, TimeSpan.FromDays(9)));
        SucceedWithoutTellingTheNextStep(chain.RootId, null);

        MoveClock(TimeSpan.FromMinutes(6));
        using (CreateServer())
        {
            WaitUntil(() => StateOf(chain.Steps["1.0"]) != AwaitingState.StateName, "the step that was never told to start to be started");
            WaitUntil(() => SetItems(SoonSet).Count == 0, "what asked for the look to be forgotten");
        }
    }

    [Fact]
    public void Server_DoesNotLoseTheLookAskedFor_WhenAStepIsRequeued_WhileItsChainIsLookedAt()
    {
        // Ten days ago a branch failed for good, by a process that stopped before it started the step for a
        // failure: the chain stood still since
        JobChainResult chain = JobChain.StartWith(() => Step("a"))
            .ThenMany(branches => branches.Branch(() => Step("b")).Branch(() => Step("c")))
            .ThenAfterAll(() => Step("done"))
            .ThenAfterAnyFailed(() => Step("alert"))
            .Enqueue(_client)!;
        string failed = chain.Steps["1.1"];
        Succeed(chain.RootId);
        Succeed(chain.Steps["1.0"]);
        FailWithoutTellingTheNextSteps(failed);
        MoveClock(TimeSpan.FromDays(10));

        // A server looks at the chain and starts that step; while it does, after it read the chain, somebody
        // requeues the branch that failed
        DuringTheStartOf during = new("alert", () => Assert.True(_client.ChangeState(failed, new EnqueuedState(), FailedState.StateName)));
        GlobalJobFilters.Filters.Add(during);

        try
        {
            using BackgroundJobServer server = CreateServer();
            WaitUntil(() => during.Done, "the chain to be looked at");
            WaitUntil(() => !IsDueWithin(chain.ChainId, TimeSpan.FromMinutes(1)), "the look to give the chain its next place");

            // The look wrote the chain's next place from what it had read before the requeue. The look that the
            // requeue asked for isn't lost to it: the chain is looked at again five minutes after the requeue,
            // wherever that is written.
            Assert.True(
                IsAskedForWithin(chain.ChainId, TimeSpan.FromMinutes(5)) || IsDueWithin(chain.ChainId, TimeSpan.FromMinutes(5)),
                "The look that the requeue asked for was lost to the look in progress.");
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(during);
        }
    }

    [Fact]
    public void Server_StartsAStepAfterASingleStep_ThatWasNeverToldToStart_WithTheResultOfTheStepBeforeIt()
    {
        // A chain without any group, with results passed to continuations (UseResultsInContinuations)
        List<object> before = [.. GlobalJobFilters.Filters.Select(static x => x.Instance)];
        GlobalConfiguration.Configuration.UseResultsInContinuations();

        try
        {
            JobChainResult chain = JobChain.StartWith(() => Produce("p")).Then(() => Consume("consumed", 0)).Enqueue(_client)!;
            SucceedWithoutTellingTheNextStep(chain.RootId, 42);
            Assert.Equal(AwaitingState.StateName, StateOf(chain.Steps["1.0"]));

            // Later nobody is about to start it any more, and a server does, handing it what its parent returned
            MoveClock(TimeSpan.FromMinutes(6));
            using (CreateServer())
            {
                WaitUntil(() => Ran.Contains("consumed 42"), "the second step to run with the result of the first");
                WaitUntilSettled(chain.ChainId);
            }

            Assert.Equal(["consumed 42"], Ran);
            Assert.Equal(SucceededState.StateName, StateOf(chain.Steps["1.0"]));
        }
        finally
        {
            foreach (object filter in GlobalJobFilters.Filters.Select(static x => x.Instance).Except(before).ToList())
            {
                GlobalJobFilters.Filters.Remove(filter);
            }
        }
    }

    [Fact]
    public void Server_GivesTheChainsListedByAnEarlierBuild_ALook_AndRemembersThatItDid()
    {
        // As an earlier build left them: listed, and only the one with a step after a group among the chains to
        // look at, in a set of its own
        JobChainResult line = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Enqueue(_client)!;
        JobChainResult joined = JobChain.StartWith(() => Step("a"))
            .ThenMany(branches => branches.Branch(() => Step("b")).Branch(() => Step("c")))
            .ThenAfterAll(() => Step("d"))
            .Enqueue(_client)!;
        Fail(line.RootId);
        Fail(joined.RootId);

        using (IStorageConnection connection = _storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveFromSet(DueSet, line.ChainId);
            transaction.RemoveFromSet(DueSet, joined.ChainId);
            transaction.AddToSet("chains:joins", joined.ChainId, JobHelper.ToTimestamp(JobClock.UtcNow));
            transaction.Commit();
        }

        using (CreateServer())
        {
            WaitUntil(() => HashField("chains:index", "Listed") == "done", "the chains listed to be gone through");
        }

        Assert.Equal(new[] { line.ChainId, joined.ChainId }.Order(StringComparer.Ordinal), SetItems(DueSet).Order(StringComparer.Ordinal));
        Assert.Empty(SetItems("chains:joins"));
        Assert.Equal(2, SetItems(JobChains.SetKey).Count);
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

    // What a process does that stops between the commit of a step's state and the start of the steps after it:
    // the state is stored, and nobody is told
    private void SucceedWithoutTellingTheNextStep(string jobId, object? result)
    {
        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetJobState(jobId, new SucceededState(result, 0, 0));
        transaction.Commit();
    }

    // What a process does that stops between the commit of a step's failure and the start of the steps after it
    private void FailWithoutTellingTheNextSteps(string jobId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetJobState(jobId, new FailedState(new InvalidOperationException("failed")));
        transaction.Commit();
    }

    // Whether a step of the chain asked for a look by a server within a time from now
    private bool IsAskedForWithin(string chainId, TimeSpan time)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return ((JobStorageConnection)connection)
            .GetFirstByLowestScoreFromSet(SoonSet, 0, JobHelper.ToTimestamp(JobClock.UtcNow + time), 1000)
            .Exists(entry => entry.StartsWith(chainId + "|", StringComparison.Ordinal));
    }

    // Whether a server is to look at a chain within a time from now
    private bool IsDueWithin(string chainId, TimeSpan time)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return ((JobStorageConnection)connection)
            .GetFirstByLowestScoreFromSet(DueSet, 0, JobHelper.ToTimestamp(JobClock.UtcNow + time), 1000)
            .Contains(chainId);
    }

    private string? HashField(string key, string field)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetAllEntriesFromHash(key)?.GetValueOrDefault(field);
    }

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

    private StateData? StateData(string jobId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return connection.GetStateData(jobId);
    }

    private void SetParameter(string jobId, string name, string value)
    {
        using IStorageConnection connection = _storage.GetConnection();
        connection.SetJobParameter(jobId, name, value);
    }

    private DateTime? ExpiresAt(string jobId) => _storage.GetMonitoringApi().JobDetails(jobId)?.ExpireAt;

    private IReadOnlyList<ActivityEntry> Feed()
    {
        using IStorageConnection connection = _storage.GetConnection();
        return ActivityFeed.Read(connection, 500);
    }

    private long Counter(string key)
    {
        using IStorageConnection connection = _storage.GetConnection();
        return ((JobStorageConnection)connection).GetCounter(key);
    }

    // What a client does between two steps of a long change: it says again that it is at it, for a while more
    private void RenewLease(string chainId, TimeSpan lasts)
    {
        string jobs = RecordField(chainId, "Jobs")!;
        int start = jobs.IndexOf("\"changing\":\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The chain's record carries no lease.");
        int valueStart = start + "\"changing\":\"".Length;
        int separator = jobs.IndexOf('/', valueStart);
        int valueEnd = jobs.IndexOf('"', valueStart);
        string renewed = string.Concat(jobs.AsSpan(0, separator + 1), JobHelper.SerializeDateTime(JobClock.UtcNow + lasts), jobs.AsSpan(valueEnd));

        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(JobChains.GetKey(chainId), new Dictionary<string, string> { ["Jobs"] = renewed });
        transaction.Commit();
    }

    private DateTime ScheduledFor(string jobId) => JobHelper.DeserializeDateTime(StateData(jobId)!.Data["EnqueueAt"]);

    // Moves the clock on from where it is
    private static void MoveClock(TimeSpan by)
    {
        TimeSpan shift = JobClock.UtcNow - DateTime.UtcNow + by;
        JobClock.TimeProvider = new ShiftedClock(shift);
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

    // As if the record were gone: expired, or dropped by the storage
    private void RemoveRecord(string chainId)
    {
        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.RemoveHash(JobChains.GetKey(chainId));
        transaction.RemoveFromSet(JobChains.SetKey, chainId);
        transaction.Commit();
    }

    // What a deploy does that renames or removes the methods of the steps: the stored definition names methods
    // that can't be loaded
    private void ForgetTheMethods(string chainId)
    {
        string definition = RecordField(chainId, "Definition")!;
        string forgotten = definition.Replace("Run", "Gone", StringComparison.Ordinal).Replace("Once", "Gone", StringComparison.Ordinal);
        Assert.NotEqual(definition, forgotten);

        using IStorageConnection connection = _storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(JobChains.GetKey(chainId), new Dictionary<string, string> { ["Definition"] = forgotten });
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

    // Each branch that finishes last tries to start the join a few times, a second apart at most: it is given up
    // once the tries have stopped for longer than that
    private void WaitUntilTheJoinIsGivenUp()
    {
        WaitUntil(() => _starts.Failed >= 3, "the start of the join to fail");

        int failed;
        do
        {
            failed = _starts.Failed;
            Thread.Sleep(2500);
        }
        while (failed != _starts.Failed);
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

    /// <summary>Counts the alerts about failed jobs.</summary>
    private sealed class CountingSink : IAlertSink
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public void Publish(Alert alert)
        {
            if (alert.Kind == AlertKind.JobFailed)
            {
                Interlocked.Increment(ref _failures);
            }
        }
    }

    /// <summary>The system clock, moved by a fixed time.</summary>
    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    /// <summary>Does something, once, while the job with a given argument is started from the Awaiting state.</summary>
    private sealed class DuringTheStartOf(string argument, Action action) : IElectStateFilter
    {
        private int _done;

        public bool Done => Volatile.Read(ref _done) == 2;

        public void OnStateElection(ElectStateContext context)
        {
            if (context.CandidateState is EnqueuedState && context.CurrentState == AwaitingState.StateName &&
                context.BackgroundJob.Job?.Type == typeof(JobChainStorageFacts) && (string?)context.BackgroundJob.Job.Args[0] == argument &&
                Interlocked.CompareExchange(ref _done, 1, 0) == 0)
            {
                action();
                Volatile.Write(ref _done, 2);
            }
        }
    }

    /// <summary>Makes the start of the job with a given argument, from the Awaiting state, fail.</summary>
    private sealed class FailingStarts : IElectStateFilter
    {
        private int _failed;

        public string? FailFor { get; set; }
        public int Failed => Volatile.Read(ref _failed);

        public void OnStateElection(ElectStateContext context)
        {
            if (FailFor != null && context.CandidateState is EnqueuedState && context.CurrentState == AwaitingState.StateName &&
                context.BackgroundJob.Job?.Type == typeof(JobChainStorageFacts) && (string?)context.BackgroundJob.Job.Args[0] == FailFor)
            {
                Interlocked.Increment(ref _failed);
                throw new InvalidOperationException("The storage is down.");
            }
        }
    }

    /// <summary>
    /// Makes the deletions with a given reason fail, as a storage error at that moment would, or stops there as a
    /// process that dies does: with an exception no rollback catches.
    /// </summary>
    private sealed class FailingDeletions : IElectStateFilter
    {
        public string? FailWithReason { get; set; }
        public string? CrashWithReason { get; set; }

        public void OnStateElection(ElectStateContext context)
        {
            if (CrashWithReason != null && context.CandidateState is DeletedState crashed && crashed.Reason == CrashWithReason)
            {
                throw new OutOfMemoryException("The process stops here.");
            }

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

        /// <summary>The step at whose creation the process stops: an exception no rollback catches.</summary>
        public string? CrashOn { get; set; }

        public List<(string Name, string Id)> Created { get; } = [];

        public void OnCreating(CreatingContext context)
        {
            if (context.Job.Type == typeof(JobChainStorageFacts) && (string?)context.Job.Args[0] == CrashOn)
            {
                throw new OutOfMemoryException("The process stops here.");
            }

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
