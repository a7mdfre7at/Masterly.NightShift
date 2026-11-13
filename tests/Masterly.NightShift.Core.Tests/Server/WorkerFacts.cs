using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;
using Moq.Sequences;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Server;

public class WorkerFacts
{
    private const string JobId = "my-job";

    private readonly string[] _queues;
    private readonly Mock<IStorageConnection> _connection;
    private readonly Mock<IBackgroundJobStateChanger> _stateChanger;
    private readonly Mock<IFetchedJob> _fetchedJob;
    private readonly Mock<IBackgroundJobPerformer> _performer;
    private readonly BackgroundProcessContextMock _context;

    public WorkerFacts()
    {
        _context = new BackgroundProcessContextMock();
        _queues = ["critical"];
        _performer = new Mock<IBackgroundJobPerformer>();

        _connection = new Mock<IStorageConnection>();
        _context.Storage.Setup(x => x.GetConnection()).Returns(_connection.Object);

        _fetchedJob = new Mock<IFetchedJob>();
        _fetchedJob.Setup(x => x.JobId).Returns(JobId);

        _connection
            .Setup(x => x.FetchNextJob(_queues, It.IsNotNull<CancellationToken>()))
            .Returns(_fetchedJob.Object);

        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData
            {
                Job = Job.FromExpression(() => Method()),
            });

        _stateChanger = new Mock<IBackgroundJobStateChanger>();
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx => ctx.NewState);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenQueuesCollectionNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new Worker(null, _performer.Object, _stateChanger.Object));

        Assert.Equal("queues", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenPerformanceProcessIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new Worker(_queues, null, _stateChanger.Object));

        Assert.Equal("performer", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStateChangeProcess_IsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new Worker(_queues, _performer.Object, null));

        Assert.Equal("stateChanger", exception.ParamName);
    }

    [Fact]
    public void Execute_TakesConnectionAndReleasesIt()
    {
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        _context.Storage.Verify(x => x.GetConnection(), Times.Once);
        _connection.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void Execute_FetchesAJobAndRemovesItFromQueue()
    {
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        _connection.Verify(
            x => x.FetchNextJob(_queues, _context.StoppingTokenSource.Token),
            Times.Once);

        _fetchedJob.Verify(x => x.RemoveFromQueue());
    }

    [Fact]
    public void Execute_RequeuesAJob_WhenThereWasAnException()
    {
        _stateChanger
            .Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Throws<InvalidOperationException>();

        Worker worker = CreateWorker(1);

        Assert.Throws<InvalidOperationException>(
            () => worker.Execute(_context.Object));

        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Never);
        _fetchedJob.Verify(x => x.Requeue());
    }

    [Fact]
    public void Execute_MovesAJobToTheFailedState_WithFiltersDisabled_WhenStateChangerThrowsAnException()
    {
        _stateChanger
            .Setup(x => x.ChangeState(It.Is<StateChangeContext>(y => y.NewState.Name != FailedState.StateName)))
            .Throws<InvalidOperationException>();

        Worker worker = CreateWorker(1);

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(y =>
            y.NewState.Name == FailedState.StateName &&
            y.DisableFilters)));

        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Once);
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
    }

    [Fact, Sequence]
    public void Execute_ExecutesDefaultWorkflow_WhenJobIsCorrect()
    {
        // Arrange
        _stateChanger
            .Setup(x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.BackgroundJobId == JobId && ctx.NewState is ProcessingState)))
            .InSequence()
            .Returns<StateChangeContext>(ctx => ctx.NewState);

        _performer.Setup(x => x.Perform(It.IsAny<PerformContext>()))
            .InSequence();

        _stateChanger
            .Setup(x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.BackgroundJobId == JobId && ctx.NewState is SucceededState)))
            .InSequence()
            .Returns<StateChangeContext>(context => context.NewState);

        Worker worker = CreateWorker();

        // Act
        worker.Execute(_context.Object);

        // Assert - see the `SequenceAttribute` class.
    }

    [Fact]
    public void Execute_SetsCurrentServer_ToProcessingState()
    {
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is ProcessingState && (((ProcessingState)ctx.NewState).ServerId == _context.ServerId))));
    }

    [Fact]
    public void Execute_ChecksTheLease_WithTheWorkerIdOfTheProcessingState()
    {
        List<StateChangeContext> contexts = [];
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Callback<StateChangeContext>(contexts.Add)
            .Returns<StateChangeContext>(ctx => ctx.NewState);
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        ProcessingState processing = Assert.IsType<ProcessingState>(contexts[0].NewState);
        StateChangeContext final = Assert.Single(contexts, ctx => ctx.NewState is SucceededState);
        Assert.False(string.IsNullOrEmpty(processing.WorkerId));
        Assert.Equal(processing.WorkerId, final.LeaseHolder);
        Assert.Null(contexts[0].LeaseHolder);
    }

    [Theory]
    [InlineData("7")]
    [InlineData(null)]
    public void Execute_NamesTheQueueEntryItTookTheJobFrom_WhenTheStorageTellsIt(string entry)
    {
        Mock<JobStorageConnection> connection = UseStorageConnection();
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetQueueEntryId)).Returns(true);
        connection.Setup(x => x.GetQueueEntryId(_fetchedJob.Object)).Returns(entry);
        List<StateChangeContext> contexts = [];
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Callback<StateChangeContext>(contexts.Add)
            .Returns<StateChangeContext>(ctx => ctx.NewState);

        CreateWorker().Execute(_context.Object);

        // In the Processing state, and for the check of the change to it; not for the final state change
        ProcessingState processing = Assert.IsType<ProcessingState>(contexts[0].NewState);
        Assert.Equal(entry, processing.QueueEntry);
        Assert.Equal(entry, contexts[0].QueueEntry);
        Assert.Null(Assert.Single(contexts, ctx => ctx.NewState is SucceededState).QueueEntry);
    }

    [Fact]
    public void Execute_DoesNotAskForTheQueueEntry_WhenTheStorageCannotTellIt()
    {
        Mock<JobStorageConnection> connection = UseStorageConnection();
        List<StateChangeContext> contexts = [];
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Callback<StateChangeContext>(contexts.Add)
            .Returns<StateChangeContext>(ctx => ctx.NewState);

        CreateWorker().Execute(_context.Object);

        connection.Verify(x => x.GetQueueEntryId(It.IsAny<IFetchedJob>()), Times.Never);
        Assert.Null(Assert.IsType<ProcessingState>(contexts[0].NewState).QueueEntry);
        Assert.Null(contexts[0].QueueEntry);
    }

    [Fact]
    public void Execute_RemovesTheEntryFromTheQueue_WhenTheJobIsRunFromAnotherEntry()
    {
        // The state changer refuses the change to Processing: another worker runs the job from another entry
        _stateChanger.Setup(x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.NewState is ProcessingState))).Returns((IState)null);

        CreateWorker().Execute(_context.Object);

        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Once);
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
        _performer.Verify(x => x.Perform(It.IsAny<PerformContext>()), Times.Never);
    }

    private Mock<JobStorageConnection> UseStorageConnection()
    {
        Mock<JobStorageConnection> connection = new();
        connection.Setup(x => x.FetchNextJob(_queues, It.IsNotNull<CancellationToken>())).Returns(_fetchedJob.Object);
        connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { Job = Job.FromExpression(() => Method()) });
        _context.Storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        return connection;
    }

    [Fact]
    public void Execute_ProcessesOnlyJobs_InEnqueued_Scheduled_AndProcessingStates()
    {
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is ProcessingState &&
            ctx.ExpectedStates.ElementAt(0) == EnqueuedState.StateName &&
            ctx.ExpectedStates.ElementAt(1) == ScheduledState.StateName &&
            ctx.ExpectedStates.ElementAt(2) == ProcessingState.StateName &&
            ctx.ExpectedStates.Count() == 3)));
    }

    [Fact]
    public void Execute_DoesNotDisableFilters_DuringNormalOperation()
    {
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            !ctx.DisableFilters)));
    }

    [Fact]
    public void Execute_DoesNotRun_PerformanceProcess_IfTransitionToProcessingStateFailed()
    {
        // Arrange
        _stateChanger
            .Setup(x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.NewState is ProcessingState)))
            .Returns<IState>(null);

        Worker worker = CreateWorker();

        // Act
        worker.Execute(_context.Object);

        // Assert
        _performer.Verify(x => x.Perform(It.IsAny<PerformContext>()), Times.Never);
    }

    [Fact]
    public void Execute_Runs_PerformanceProcess()
    {
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        _performer.Verify(x => x.Perform(It.Is<PerformContext>(ctx =>
            ctx.BackgroundJob.Id == JobId &&
            ctx.ServerId == _context.ServerId)));
    }

    [Fact]
    public void Execute_DoesNotMoveAJob_ToTheFailedState_ButRequeuesIt_WhenProcessThrowsOperationCanceled_DuringShutdownOnly()
    {
        // Arrange
        CancellationTokenSource cts = new();
        _context.StoppedTokenSource = cts;

        _performer.Setup(x => x.Perform(It.IsAny<PerformContext>()))
            .Callback(cts.Cancel)
            .Throws<OperationCanceledException>();

        Worker worker = CreateWorker();

        // Act
        Assert.Throws<OperationCanceledException>(() => worker.Execute(_context.Object));

        // Assert
        _stateChanger.Verify(
            x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.NewState is FailedState)),
            Times.Never);
        _fetchedJob.Verify(x => x.Requeue());
    }

    [Fact]
    public void Execute_MovesAJob_ToTheFailedState_AndNotRequeuesIt_WhenProcessThrowsOperationCanceled_WhenShutdownWasNotRequested()
    {
        // Arrange
        _performer.Setup(x => x.Perform(It.IsAny<PerformContext>()))
            .Throws<OperationCanceledException>();

        Worker worker = CreateWorker();

        // Act
        worker.Execute(_context.Object);

        // Assert
        _stateChanger.Verify(
            x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.NewState is FailedState)),
            Times.Once);
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
    }

    [Fact]
    public void Execute_DoesNotMoveAJobToFailedState_AndRemovesJobFromQueue_WhenProcessThrowsJobAbortedException()
    {
        // Arrange
        _performer.Setup(x => x.Perform(It.IsAny<PerformContext>()))
            .Throws<JobAbortedException>();

        Worker worker = CreateWorker();

        // Act
        worker.Execute(_context.Object);

        _stateChanger.Verify(
            x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.NewState is FailedState)),
            Times.Never);
        _fetchedJob.Verify(x => x.RemoveFromQueue());
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
    }

    [Fact]
    public void Execute_MovesJob_ToSuccessfulState_OnlyIfItIsInProcessingState()
    {
        Worker worker = CreateWorker();

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is SucceededState &&
            ctx.ExpectedStates.ElementAt(0) == ProcessingState.StateName)));
    }

    [Fact]
    public void Execute_PassesCustomData_BetweenContexts_OnSucceededStateTransition()
    {
        Worker worker = CreateWorker();
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx => ctx.Items.Add("Key", "Value"));

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is SucceededState &&
            ctx.CustomData["Key"].Equals("Value"))));
    }

    [Fact]
    public void Execute_MovesJob_ToFailedState_IfThereWasInternalException()
    {
        // Arrange
        InvalidOperationException exception = new();
        _performer
            .Setup(x => x.Perform(It.IsAny<PerformContext>()))
            .Throws(exception);

        Worker worker = CreateWorker();

        // Act
        worker.Execute(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.BackgroundJobId == JobId &&
            ctx.NewState is FailedState &&
            ((FailedState)ctx.NewState).Exception == exception &&
            !ctx.DisableFilters)));
    }

    [Fact]
    public void Execute_DoesNotPassCustomData_BetweenContexts_OnFailedStateTransition()
    {
        Worker worker = CreateWorker();
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx => ctx.Items.Add("Key", "Value"))
            .Throws<Exception>();

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is FailedState &&
            ctx.CustomData == null)));
    }

    [Fact]
    public void Execute_MovesJob_ToFailedState_IfThereWasUserException()
    {
        // Arrange
        InvalidOperationException exception = new();
        _performer
            .Setup(x => x.Perform(It.IsAny<PerformContext>()))
            .Throws(new JobPerformanceException("hello", exception));

        Worker worker = CreateWorker();

        // Act
        worker.Execute(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.BackgroundJobId == JobId &&
            ctx.NewState is FailedState &&
            !ctx.DisableFilters)));
    }

    [Fact]
    public void Execute_MovesJob_ToFailedState_IfThereWasJobLoadException()
    {
        // Arrange
        _connection.Setup(x => x.GetJobData(JobId))
            .Returns(new JobData { LoadException = new JobLoadException("asd", new Exception()) });

        Worker worker = CreateWorker();

        // Act
        worker.Execute(_context.Object);

        // Assert
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is FailedState &&
            !ctx.DisableFilters)));
    }

    [Fact]
    public void Execute_CopiesTheItems_UnderTheLockTheConsoleTakes()
    {
        Worker worker = CreateWorker();
        Thread background = null;
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx =>
            {
                // A thread the job started still works with the shared items (creating the console, for
                // example) when the job method returns
                using ManualResetEventSlim locked = new();
                background = new Thread(() =>
                {
                    lock (ctx.Items)
                    {
                        locked.Set();
                        Thread.Sleep(200);
                        ctx.Items["Late"] = "Value";
                    }
                });
                background.Start();
                locked.Wait();
            });

        worker.Execute(_context.Object);
        background.Join();

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is SucceededState &&
            ctx.CustomData.ContainsKey("Late"))));
    }

    [Fact]
    public void Execute_GivesFiltersItsStateChanger_ButDoesNotPassItOnAsCustomData()
    {
        Worker worker = CreateWorker();
        object seen = null;
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx => seen = ConcurrencyControl.GetStateChanger(ctx));

        worker.Execute(_context.Object);

        Assert.Same(_stateChanger.Object, seen);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is SucceededState &&
            !ctx.CustomData.ContainsKey(ConcurrencyControl.StateChangerItem))));
    }

    [Fact]
    public void Execute_DisposesWhatAFilterKeptOpen_AfterTheStateChangeThatFollowsTheJob()
    {
        Worker worker = CreateWorker();
        List<string> events = [];
        Mock<IDisposable> resource = new();
        resource.Setup(x => x.Dispose()).Callback(() => events.Add("disposed"));
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx => ((ICollection<IDisposable>)ctx.Items[Worker.DisposeAfterStateChangeItem]).Add(resource.Object));
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                events.Add(ctx.NewState.Name);
                return ctx.NewState;
            });

        worker.Execute(_context.Object);

        Assert.Equal(new[] { ProcessingState.StateName, SucceededState.StateName, "disposed" }, events);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is SucceededState &&
            !ctx.CustomData.ContainsKey(Worker.DisposeAfterStateChangeItem))));
    }

    [Fact]
    public void Execute_DisposesWhatAFilterKeptOpen_WhenTheStateChangeFails()
    {
        Worker worker = CreateWorker(1);
        Mock<IDisposable> resource = new();
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx => ((ICollection<IDisposable>)ctx.Items[Worker.DisposeAfterStateChangeItem]).Add(resource.Object));
        _stateChanger.Setup(x => x.ChangeState(It.Is<StateChangeContext>(ctx => !(ctx.NewState is ProcessingState))))
            .Throws<InvalidOperationException>();

        Assert.Throws<InvalidOperationException>(() => worker.Execute(_context.Object));

        resource.Verify(x => x.Dispose(), Times.Once);
        _fetchedJob.Verify(x => x.Requeue());
    }

    [Fact]
    public void Execute_LeavesTheStateAlone_WhenAFilterRescheduledTheJob()
    {
        Worker worker = CreateWorker();
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx =>
            {
                // What ConcurrencyControl.Reschedule leaves behind, together with the canceled run
                ctx.Items[ConcurrencyControl.RescheduledItem] = true;
                ctx.Items[BackgroundJobPerformer.ContextCanceledKey] = "ThrottleAttribute";
            });

        worker.Execute(_context.Object);

        // Only the transition to Processing; no "Canceled by filter" Deleted state that could hit the job once
        // another worker has picked it up again
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.NewState is DeletedState)), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Once);
        _fetchedJob.Verify(x => x.RemoveFromQueue());
    }

    [Fact]
    public void Execute_StillDeletesAJob_CanceledByAFilterWithoutRescheduling()
    {
        Worker worker = CreateWorker();
        _performer.Setup(x => x.Perform(It.IsNotNull<PerformContext>()))
            .Callback<PerformContext>(ctx => ctx.Items[BackgroundJobPerformer.ContextCanceledKey] = "ThrottleAttribute");

        worker.Execute(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx =>
            ctx.NewState is DeletedState && ctx.ExpectedStates.ElementAt(0) == ProcessingState.StateName)));
    }

    [Fact]
    public void Execute_OffersToLookAtTheJobAgain_OnlyWhenItMovesItToProcessing()
    {
        List<(string State, bool Offered)> offers = [];
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                offers.Add((ctx.NewState.Name, ctx.LookAgain != null));
                return ctx.NewState;
            });

        CreateWorker().Execute(_context.Object);

        Assert.Equal([(ProcessingState.StateName, true), (SucceededState.StateName, false)], offers);
    }

    [Fact]
    public void Execute_KeepsTheJob_AndChangesItsStateAgain_WhenAFilterAsksItToLookAgain()
    {
        // Three times "not now", with nothing stored; then the job may run
        int looks = 0;
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                if (ctx.NewState is ProcessingState && ++looks <= 3)
                {
                    ctx.LookAgain.Request(TimeSpan.FromMilliseconds(1));
                    return null;
                }

                return ctx.NewState;
            });

        CreateWorker().Execute(_context.Object);

        // Fetched once and never put back in its queue: it stays with this worker, which runs it in the end
        _connection.Verify(x => x.FetchNextJob(_queues, It.IsAny<CancellationToken>()), Times.Once);
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(ctx => ctx.NewState is ProcessingState)), Times.Exactly(4));
        _performer.Verify(x => x.Perform(It.IsAny<PerformContext>()), Times.Once);
        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Once);
        _fetchedJob.Verify(x => x.Dispose(), Times.Once);
        _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Execute_WaitsAsLongAsItWasAsked_BeforeItLooksAgain()
    {
        Stopwatch asked = new();
        TimeSpan waited = TimeSpan.Zero;
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                if (ctx.NewState is ProcessingState && !asked.IsRunning)
                {
                    ctx.LookAgain.Request(TimeSpan.FromMilliseconds(200));
                    asked.Start();
                    return null;
                }

                if (ctx.NewState is ProcessingState)
                {
                    waited = asked.Elapsed;
                }

                return ctx.NewState;
            });

        CreateWorker().Execute(_context.Object);

        Assert.InRange(waited, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Execute_LooksLessAndLessOften_AtAJobItIsAlwaysAskedToLookAtAgainAtOnce()
    {
        // A filter that goes by a clock that stands still asks for the shortest wait each time. The pool's clock
        // is the test's: it moves by exactly the pauses the worker takes, and by nothing else.
        long now = 0;
        List<double> pauses = [];
        List<bool> offers = [];
        WorkerWaits waits = new(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => now,
            (pause, _) =>
            {
                pauses.Add(Math.Round(pause.TotalMilliseconds));
                now += (long)(pause.TotalSeconds * Stopwatch.Frequency);
            });
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                offers.Add(ctx.LookAgain != null);
                if (ctx.LookAgain == null)
                {
                    return new ScheduledState(TimeSpan.FromSeconds(2));
                }

                ctx.LookAgain.Request(TimeSpan.Zero);
                return null;
            });

        CreateWorker(waits: waits).Execute(_context.Object);

        // Each pause is as long as all the ones before it, between 10 and 250 milliseconds, and the last one ends
        // the second a job is kept: nine looks with the offer and one without, not a hundred ten milliseconds apart
        Assert.Equal([10, 10, 20, 40, 80, 160, 250, 250, 180], pauses);
        Assert.Equal([.. Enumerable.Repeat(true, 9), false], offers);
    }

    [Fact]
    public void Execute_StopsOffering_OnceItWaitedForTheJobLongEnough_ByTheClockOfTheMachine()
    {
        // Whatever the filter goes by (the application's clock may stand still), the worker counts real time
        WorkerWaits waits = new(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300), Stopwatch.GetTimestamp);
        List<bool> offers = [];
        Stopwatch started = Stopwatch.StartNew();
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                offers.Add(ctx.LookAgain != null);
                if (ctx.LookAgain == null)
                {
                    // What the filter does when it can't ask the worker to wait: it holds the job
                    return new ScheduledState(TimeSpan.FromSeconds(2));
                }

                ctx.LookAgain.Request(TimeSpan.FromMilliseconds(10));
                return null;
            });

        CreateWorker(waits: waits).Execute(_context.Object);

        Assert.False(offers[^1]);
        Assert.All(offers.SkipLast(1), Assert.True);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Once);
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
        _performer.Verify(x => x.Perform(It.IsAny<PerformContext>()), Times.Never);
    }

    [Fact]
    public void Execute_StopsOffering_OnceItsPoolHasWaitedEnough_AndTheNextJobIsNotWaitedForAtAll()
    {
        // No other worker of the pool looks for a job while this one waits: the pool's budget is all it has
        WorkerWaits waits = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), Stopwatch.GetTimestamp);
        List<bool> offers = [];
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                offers.Add(ctx.LookAgain != null);
                if (ctx.LookAgain == null)
                {
                    return new ScheduledState(TimeSpan.FromSeconds(2));
                }

                ctx.LookAgain.Request(TimeSpan.FromMilliseconds(20));
                return null;
            });
        Worker worker = CreateWorker(waits: waits);
        Stopwatch started = Stopwatch.StartNew();

        worker.Execute(_context.Object);
        TimeSpan first = started.Elapsed;
        int firstLooks = offers.Count;
        worker.Execute(_context.Object);

        Assert.InRange(first, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
        Assert.False(offers[firstLooks - 1]);
        Assert.Equal([false], offers.Skip(firstLooks));
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
    }

    [Fact]
    public void Execute_PutsTheJobBackInItsQueue_Once_WhenTheServerStopsWhileItWaits()
    {
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                ctx.LookAgain.Request(TimeSpan.FromMinutes(5));
                _context.StoppingTokenSource.Cancel();
                return null;
            });
        Stopwatch started = Stopwatch.StartNew();

        CreateWorker().Execute(_context.Object);

        // At once, as it was: one state change that stored nothing, and the job isn't acknowledged
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Once);
        _fetchedJob.Verify(x => x.Requeue(), Times.Once);
        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Never);
        _fetchedJob.Verify(x => x.Dispose(), Times.Once);
        _performer.Verify(x => x.Perform(It.IsAny<PerformContext>()), Times.Never);
    }

    [Fact]
    public void Execute_TriesAgain_WhenTheJobCanNotBePutBackInItsQueue_WhileTheServerStops()
    {
        // A storage that fails for a moment: left fetched, the job would be invisible for the invisibility timeout
        int attempts = 0;
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                ctx.LookAgain.Request(TimeSpan.FromMinutes(5));
                _context.StoppingTokenSource.Cancel();
                return null;
            });
        _fetchedJob.Setup(x => x.Requeue()).Callback(() =>
        {
            if (++attempts < 3)
            {
                throw new InvalidOperationException("The storage is down.");
            }
        });

        CreateWorker().Execute(_context.Object);

        Assert.Equal(3, attempts);
        _fetchedJob.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void Execute_DoesNotThrow_WhenTheJobCanNotBePutBackInItsQueueAtAll_AndDisposesItOnce()
    {
        // The storage puts it back by itself in the end. What disposing the job throws must not leave Execute:
        // the thread would wait as after a process that failed, and nothing failed but the storage.
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                ctx.LookAgain.Request(TimeSpan.FromMinutes(5));
                _context.StoppingTokenSource.Cancel();
                return null;
            });
        _fetchedJob.Setup(x => x.Requeue()).Throws<InvalidOperationException>();
        _fetchedJob.Setup(x => x.Dispose()).Throws<InvalidOperationException>();

        CreateWorker().Execute(_context.Object);

        _fetchedJob.Verify(x => x.Requeue(), Times.Exactly(3));
        _fetchedJob.Verify(x => x.Dispose(), Times.Once);
        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Never);
    }

    [Fact]
    public void Execute_DoesNotWait_ForWhatAFailedAttemptAsked_OnceTheStateChangeOfLastResortFoundNothingToChange()
    {
        // The only attempt asks the worker to look again, and then throws; the state change of last resort, which
        // offers nothing, finds the job gone. What was asked before must not make the worker wait, or put the
        // job back.
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(static ctx =>
            {
                if (ctx.DisableFilters)
                {
                    return null;
                }

                ctx.LookAgain.Request(TimeSpan.FromMinutes(5));
                throw new InvalidOperationException("The storage is down.");
            });
        Stopwatch started = Stopwatch.StartNew();

        CreateWorker(1).Execute(_context.Object);

        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Exactly(2));
        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Once);
        _fetchedJob.Verify(x => x.Requeue(), Times.Never);
    }

    [Fact]
    public void Execute_CountsTheLooksThemselves_AgainstThePoolsBudget_NotOnlyThePausesBetweenThem()
    {
        // A slow storage: each look at the job takes a quarter of a second. The worker keeps the job, and takes no
        // other, for all of that time, so the pool's budget is spent by it too.
        WorkerWaits waits = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), Stopwatch.GetTimestamp);
        List<(bool Offered, TimeSpan At)> looks = [];
        Stopwatch started = Stopwatch.StartNew();
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                if (ctx.NewState is not ProcessingState)
                {
                    return ctx.NewState;
                }

                looks.Add((ctx.LookAgain != null, started.Elapsed));
                Thread.Sleep(250);

                if (ctx.LookAgain == null)
                {
                    return new ScheduledState(TimeSpan.FromSeconds(2));
                }

                ctx.LookAgain.Request(TimeSpan.FromMilliseconds(10));
                return null;
            });

        CreateWorker(waits: waits).Execute(_context.Object);

        // From the end of the first look, when the worker began to keep the job, to the look that offers nothing
        // more: the budget, and the look in progress when it ran out at most (counting only the pauses, it was
        // more than a second)
        TimeSpan held = looks[^1].At - (looks[0].At + TimeSpan.FromMilliseconds(250));
        Assert.False(looks[^1].Offered);
        Assert.InRange(held, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(300 + 250 + 100));
    }

    [Fact]
    public void Execute_StartsTheJob_WhenTheLookThatAppliesTheProcessingStateIsMade_NotWhenTheWorkerTookIt()
    {
        // The worker keeps the job for two looks first: the time it kept it isn't time the job ran
        List<ProcessingState> states = [];
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                if (ctx.NewState is not ProcessingState processing)
                {
                    return ctx.NewState;
                }

                states.Add(processing);
                if (states.Count <= 2)
                {
                    ctx.LookAgain.Request(TimeSpan.FromMilliseconds(200));
                    return null;
                }

                return ctx.NewState;
            });
        DateTime before = DateTime.UtcNow;

        CreateWorker().Execute(_context.Object);

        // A state of its own at each look, so the one applied says when the job really started
        Assert.Equal(3, states.Distinct().Count());
        Assert.InRange(states[^1].StartedAt, before.AddMilliseconds(350), DateTime.UtcNow);
        Assert.All(states, state => Assert.Equal(states[0].WorkerId, state.WorkerId));
    }

    [Fact]
    public void Execute_PutsTheJobBack_WithoutThrowing_WhenTheServerStopsWhileALookIsTriedAgain()
    {
        // The worker keeps the job; at its next look the storage fails, and the server stops while the state
        // change waits before it tries again. That is a stop while the worker keeps the job, as during a pause.
        int looks = 0;
        int requeues = 0;
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                if (++looks == 1)
                {
                    ctx.LookAgain.Request(TimeSpan.FromMilliseconds(1));
                    return null;
                }

                _context.StoppingTokenSource.Cancel();
                throw new InvalidOperationException("The storage is down.");
            });
        _fetchedJob.Setup(x => x.Requeue()).Callback(() =>
        {
            if (++requeues < 3)
            {
                throw new InvalidOperationException("The storage is still down.");
            }
        });

        CreateWorker().Execute(_context.Object);

        Assert.Equal(3, requeues);
        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Never);
        _fetchedJob.Verify(x => x.Dispose(), Times.Once);
        _performer.Verify(x => x.Perform(It.IsAny<PerformContext>()), Times.Never);
    }

    [Fact]
    public void Execute_StillRequeuesOnceAndThrows_WhenTheServerStopsWhileTheFirstLookIsTriedAgain()
    {
        // Not kept yet: the path of before, which BackgroundExecution expects when the server stops
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                _context.StoppingTokenSource.Cancel();
                throw new InvalidOperationException("The storage is down.");
            });

        Assert.ThrowsAny<OperationCanceledException>(() => CreateWorker().Execute(_context.Object));

        _fetchedJob.Verify(x => x.Requeue(), Times.Once);
    }

    [Fact]
    public void Execute_StillRequeuesOnceAndThrows_WhenTheServerStopsWhileTheJobRuns()
    {
        // A run that the server's stop cancels takes its existing path, also after the worker kept the job
        int looks = 0;
        _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>()))
            .Returns<StateChangeContext>(ctx =>
            {
                if (ctx.NewState is ProcessingState && ++looks == 1)
                {
                    ctx.LookAgain.Request(TimeSpan.FromMilliseconds(1));
                    return null;
                }

                return ctx.NewState;
            });
        _performer.Setup(x => x.Perform(It.IsAny<PerformContext>())).Callback(() =>
        {
            _context.StoppingTokenSource.Cancel();
            _context.StoppedTokenSource.Cancel();
            _context.ShutdownTokenSource.Cancel();
            throw new OperationCanceledException(_context.ShutdownTokenSource.Token);
        });

        Assert.ThrowsAny<OperationCanceledException>(() => CreateWorker().Execute(_context.Object));

        _fetchedJob.Verify(x => x.Requeue(), Times.Once);
        _fetchedJob.Verify(x => x.RemoveFromQueue(), Times.Never);
    }

    [Fact]
    public void Execute_CountsItselfAsLookingForAJob_OnlyWhileItFetchesOne()
    {
        // While it looks for a job, the waits of the other workers of its pool cost the pool nothing
        WorkerWaits waits = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), Stopwatch.GetTimestamp);
        TimeSpan duringFetch = TimeSpan.Zero;
        _connection.Setup(x => x.FetchNextJob(_queues, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // Another worker of the pool waits meanwhile, for longer than the pool's budget
                Assert.True(waits.Enter());
                duringFetch = waits.Allow(TimeSpan.FromMilliseconds(200));
                Thread.Sleep(200);
                waits.Leave();
                return _fetchedJob.Object;
            });

        CreateWorker(waits: waits).Execute(_context.Object);

        // Told the whole pause, and nothing of it counted
        Assert.Equal(TimeSpan.FromMilliseconds(200), duringFetch);
        Assert.True(waits.CanWait);

        // The same wait once this worker has its job costs the pool its budget
        Assert.True(waits.Enter());
        Assert.InRange(waits.Allow(TimeSpan.FromMilliseconds(200)), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(100));
        Thread.Sleep(150);
        waits.Leave();
        Assert.False(waits.CanWait);
    }

    private Worker CreateWorker(int maxStateChangeAttempts = 10, WorkerWaits waits = null)
    {
        return new Worker(_queues, _performer.Object, _stateChanger.Object, TimeSpan.FromSeconds(5), maxStateChangeAttempts, waits ?? new WorkerWaits());
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void Method() { }
}
