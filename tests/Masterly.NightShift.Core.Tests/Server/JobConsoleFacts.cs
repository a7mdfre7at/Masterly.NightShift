using System.Collections.Concurrent;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class JobConsoleFacts : IDisposable
{
    private const string Key = "job:JobId:console";

    private readonly PerformContextMock _context = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly List<string> _inserted = [];
    private readonly DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    public JobConsoleFacts()
    {
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _transaction.Setup(x => x.InsertToList(Key, It.IsAny<string>())).Callback<string, string>((_, value) => _inserted.Add(value));
        JobConsole.Clock = () => _now;
    }

    public void Dispose()
    {
        JobConsole.Clock = () => DateTime.UtcNow;
        JobConsole.DefaultMaxLines = 500;
    }

    [Fact]
    public void Console_IsSharedBetweenContextsOfTheSamePerformance()
    {
        PerformContext context = _context.Object;
        PerformingContext performing = new(context);

        Assert.Same(context.Console, performing.Console);
        Assert.Same(context.Console, context.Console);
    }

    [Fact]
    public void SetProgress_WritesTheProgressParameter()
    {
        _context.Object.Console.SetProgress(42.46, "Importing");

        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter,
            It.Is<string>(json => json.Contains("42.5") && json.Contains("Importing"))));
    }

    [Fact]
    public void SetProgress_ClampsToTheValidRange()
    {
        IJobConsole console = _context.Object.Console;

        console.SetProgress(250);
        console.SetProgress(-3);

        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.Is<string>(json => json.Contains("\"p\":100"))));
        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.Is<string>(json => json.Contains("\"p\":0"))));
    }

    [Fact]
    public void SetProgress_SkipsWrites_WhenNothingChanged()
    {
        IJobConsole console = _context.Object.Console;

        console.SetProgress(10, "step");
        console.SetProgress(10, "step");
        console.SetProgress(10.01, "step");

        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void WriteLine_InsertsASerializedEntry_AndTrimsTheList()
    {
        JobConsole.DefaultMaxLines = 50;

        _context.Object.Console.WriteLine("hello");

        JobConsoleEntry? entry = SerializationHelper.Deserialize<JobConsoleEntry>(_inserted.Single(), SerializationOption.Internal);
        Assert.Equal("hello", entry.Message);
        Assert.Equal(JobConsoleLevel.Info, entry.Level);
        Assert.Null(entry.Checkpoint);
        Assert.Equal(1, entry.Sequence);
        Assert.Equal(_now, JobConsole.FromTimestamp(entry.Timestamp));
        _transaction.Verify(x => x.TrimList(Key, 0, 49));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void WriteLine_KeepsTheLevel_AndIncrementsTheSequence()
    {
        IJobConsole console = _context.Object.Console;

        console.WriteLine(JobConsoleLevel.Warning, "careful");
        console.WriteLine(JobConsoleLevel.Error, "boom");

        List<JobConsoleEntry?> entries = [.. _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal))];
        Assert.Equal(JobConsoleLevel.Warning, entries[0].Level);
        Assert.Equal(JobConsoleLevel.Error, entries[1].Level);
        Assert.Equal([1, 2], entries.Select(x => x.Sequence));
    }

    [Fact]
    public void WriteLine_ThrowsAnException_WhenMessageIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => _context.Object.Console.WriteLine(null));
    }

    [Fact, StaticLock]
    public void WriteLine_DropsTheLine_AndLogsAWarning_WhenTheStorageFails()
    {
        _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("SQL timeout"));
        RecordingLogProvider log = new();

        using (log.Use())
        {
            _context.Object.Console.WriteLine("line");
        }

        RecordingLogProvider.LogEntry entry = Assert.Single(log.Entries, static x => x.Logger == typeof(JobConsole).FullName);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("'JobId'", entry.Message, StringComparison.Ordinal);
        Assert.IsType<TimeoutException>(entry.Exception);
    }

    [Fact, StaticLock]
    public void WriteLine_LogsDroppedWrites_AtMostOnceAMinute_WithTheirCount()
    {
        _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("SQL timeout"));
        _context.Connection.Setup(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.IsAny<string>())).Throws(new TimeoutException("SQL timeout"));
        DateTime now = _now;
        JobConsole.Clock = () => now;
        IJobConsole console = _context.Object.Console;
        RecordingLogProvider log = new();

        using (log.Use())
        {
            console.WriteLine("first");
            console.WriteLine("second");
            console.SetProgress(10);
            now = now.AddSeconds(59);
            console.WriteLine("third");
            now = now.AddSeconds(1);
            console.WriteLine("fourth");
        }

        string[] messages = [.. log.Entries.Where(static x => x.Logger == typeof(JobConsole).FullName).Select(static x => x.Message)];
        Assert.Equal(2, messages.Length);
        Assert.Contains(" 1 progress report(s) or line(s)", messages[0], StringComparison.Ordinal);
        Assert.Contains(" 4 progress report(s) or line(s)", messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void WriteLine_WritesAgain_OnceTheStorageRecovers()
    {
        _transaction.SetupSequence(x => x.Commit()).Throws(new TimeoutException("SQL timeout")).Pass();
        IJobConsole console = _context.Object.Console;

        console.WriteLine("lost");
        console.WriteLine("kept");

        _transaction.Verify(x => x.Commit(), Times.Exactly(2));
        Assert.Contains("kept", _inserted.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void SetProgress_DropsTheReport_AndSendsItAgainNextTime_WhenTheStorageFails()
    {
        _context.Connection.SetupSequence(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.IsAny<string>()))
            .Throws(new TimeoutException("SQL timeout"))
            .Pass();
        IJobConsole console = _context.Object.Console;

        console.SetProgress(10, "step");
        console.SetProgress(10, "step");

        // The failed report isn't remembered as written, so the same report is sent again
        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public void Checkpoint_StillThrows_WhenTheStorageFails()
    {
        _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("SQL timeout"));

        // Resume relies on checkpoints; a lost one would make a retry redo the finished step
        Assert.Throws<TimeoutException>(() => _context.Object.Console.Checkpoint("step"));
    }

    [Fact]
    public void Checkpoint_InsertsACheckpointEntry()
    {
        _context.Object.Console.Checkpoint("loaded");

        JobConsoleEntry? entry = SerializationHelper.Deserialize<JobConsoleEntry>(_inserted.Single(), SerializationOption.Internal);
        Assert.Equal("loaded", entry.Checkpoint);
        Assert.Null(entry.Message);
        Assert.Null(entry.Level);
    }

    [Fact]
    public void HasCheckpoint_Throws_WhenTheStorageFails_AndReadsAgainOnTheNextCall()
    {
        Mock<JobStorageConnection> connection = new() { CallBase = true };
        string entry = SerializationHelper.Serialize(new JobConsoleEntry { Checkpoint = "step", Timestamp = 1 }, SerializationOption.Internal);
        connection.SetupSequence(x => x.GetAllItemsFromList(JobConsole.GetCheckpointsKey("JobId")))
            .Throws(new TimeoutException("SQL timeout"))
            .Returns([entry]);
        _context.Storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        IJobConsole console = _context.Object.Console;

        // Treating the error as "no checkpoints" would make Resume redo the finished step
        Assert.Throws<TimeoutException>(() => console.HasCheckpoint("step"));
        Assert.True(console.HasCheckpoint("step"));
    }

    [Fact]
    public void HasCheckpoint_ReturnsFalse_WhenTheStorageCannotReadLists()
    {
        // The base class throws NotSupportedException from GetAllItemsFromList
        Mock<JobStorageConnection> connection = new() { CallBase = true };
        _context.Storage.Setup(x => x.GetConnection()).Returns(connection.Object);

        Assert.False(_context.Object.Console.HasCheckpoint("step"));
        Assert.Empty(_context.Object.Console.Checkpoints);
    }

    [Fact]
    public void Messages_AreTruncated_ToTheMaximumLength()
    {
        _context.Object.Console.WriteLine(new string('x', JobConsole.MaxMessageLength + 100));

        JobConsoleEntry? entry = SerializationHelper.Deserialize<JobConsoleEntry>(_inserted.Single(), SerializationOption.Internal);
        Assert.Equal(JobConsole.MaxMessageLength, entry.Message.Length);
    }

    [Fact]
    public void Writes_GoThroughAShortLivedConnection_WhenTheStorageIsKnown()
    {
        Mock<IStorageConnection> owned = new();
        Mock<IWriteOnlyTransaction> ownedTransaction = new();
        owned.Setup(x => x.CreateWriteTransaction()).Returns(ownedTransaction.Object);
        _context.Storage.Setup(x => x.GetConnection()).Returns(owned.Object);
        IJobConsole console = _context.Object.Console;

        console.WriteLine("hello");
        console.SetProgress(10);

        ownedTransaction.Verify(x => x.InsertToList(Key, It.IsAny<string>()));
        ownedTransaction.Verify(x => x.Commit());
        owned.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.IsAny<string>()));
        owned.Verify(x => x.Dispose(), Times.Exactly(2));
        _context.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        _context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Writes_FromSeveralThreads_RunOneAtATime()
    {
        int running = 0;
        int overlaps = 0;
        _transaction.Setup(x => x.InsertToList(Key, It.IsAny<string>())).Callback(() =>
        {
            if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlaps);
            Thread.Sleep(1);
            Interlocked.Decrement(ref running);
        });
        IJobConsole console = _context.Object.Console;

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => console.WriteLine($"line {i}"));

        Assert.Equal(0, overlaps);
        _transaction.Verify(x => x.Commit(), Times.Exactly(64));
    }

    [Fact]
    public void Console_IsCreatedOnce_WhenSeveralThreadsAskForIt()
    {
        PerformContext context = _context.Object;
        ConcurrentBag<IJobConsole> consoles = [];

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => consoles.Add(new PerformContext(context).Console));

        Assert.Single(consoles.Distinct());
    }

    [Fact]
    public void FirstWrite_MarksTheJob_SoTheStateFilterKnowsItHasAConsole()
    {
        IJobConsole console = _context.Object.Console;

        console.WriteLine("one");
        console.Checkpoint("two");

        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.MarkerParameter, It.IsAny<string>()), Times.Once);
        Assert.True(((JobConsole)console).HasStorageData);
    }

    [Fact]
    public void Writes_DoNotMarkTheJobAgain_WhenAnEarlierAttemptDid()
    {
        _context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { JobConsole.MarkerParameter, "1" } };

        _context.Object.Console.WriteLine("hello");

        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.MarkerParameter, It.IsAny<string>()), Times.Never);
        Assert.Single(_inserted);
    }

    [Fact]
    public void SetProgress_DoesNotMarkTheJob()
    {
        IJobConsole console = _context.Object.Console;

        console.SetProgress(10);

        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.MarkerParameter, It.IsAny<string>()), Times.Never);
        Assert.False(((JobConsole)console).HasStorageData);
    }

    [Fact]
    public void Writes_AreSkipped_OnceTheJobIsAborted()
    {
        StateData state = new() { Name = DeletedState.StateName, Data = new Dictionary<string, string>() };
        _context.Connection.Setup(x => x.GetStateData("JobId")).Returns(state);

        using ServerJobCancellationToken token = new(_context.Connection.Object, "JobId", "server", "worker", CancellationToken.None);
        IJobConsole console = new PerformContext(_context.Storage.Object, _context.Connection.Object, _context.BackgroundJob.Object, token).Console;
        console.WriteLine("before");
        _ = token.ShutdownToken;
        Assert.True(token.TryCheckJobIsAborted(_context.Connection.Object));

        console.WriteLine("after");
        console.SetProgress(50);
        console.Checkpoint("step");

        Assert.Single(_inserted);
        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.IsAny<string>()), Times.Never);
        Assert.True(console.HasCheckpoint("step"));
    }

    [Fact]
    public void Writes_AreSkipped_AfterThePerformanceIsOver()
    {
        ServerJobCancellationToken token = new(_context.Connection.Object, "JobId", "server", "worker", CancellationToken.None);
        IJobConsole console = new PerformContext(_context.Storage.Object, _context.Connection.Object, _context.BackgroundJob.Object, token).Console;
        token.Dispose();

        console.WriteLine("too late");
        console.SetProgress(100);

        Assert.Empty(_inserted);
        _context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReadEntries_ReturnsEmpty_ForConnectionsWithoutListSupport()
    {
        IReadOnlyList<JobConsoleEntry> entries = JobConsole.ReadEntries(_context.Connection.Object, "JobId");

        Assert.Empty(entries);
    }

    [Fact]
    public void ReadEntries_ReturnsEmpty_WhenTheConnectionLacksTheExtendedApi()
    {
        Mock<JobStorageConnection> connection = new() { CallBase = true };

        IReadOnlyList<JobConsoleEntry> entries = JobConsole.ReadEntries(connection.Object, "JobId");

        Assert.Empty(entries);
    }

    [Fact]
    public void ReadEntries_OrdersByTimestampThenSequence_AndSkipsGarbage()
    {
        Mock<JobStorageConnection> connection = new();
        connection.Setup(x => x.GetAllItemsFromList(Key)).Returns(
        [
            "{\"t\":2000,\"s\":1,\"l\":\"Info\",\"m\":\"second\"}",
            "not json",
            "{\"t\":1000,\"s\":2,\"c\":\"start\"}",
            "{\"t\":1000,\"s\":1,\"l\":\"Debug\",\"m\":\"first\"}",
        ]);

        IReadOnlyList<JobConsoleEntry> entries = JobConsole.ReadEntries(connection.Object, "JobId");

        Assert.Equal(["first", null, "second"], entries.Select(x => x.Message));
        Assert.Equal("start", entries[1].Checkpoint);
    }

    [Fact]
    public void ReadProgress_ReturnsNull_WhenNothingWasReported()
    {
        Assert.Null(JobConsole.ReadProgress(_context.Connection.Object, "JobId"));
    }

    [Fact]
    public void ReadProgress_DeserializesTheParameter()
    {
        _context.Connection.Setup(x => x.GetJobParameter("JobId", JobConsole.ProgressParameter)).Returns("{\"p\":55.5,\"m\":\"half\",\"t\":123}");

        JobProgress? progress = JobConsole.ReadProgress(_context.Connection.Object, "JobId");

        Assert.Equal(55.5, progress.Percent);
        Assert.Equal("half", progress.Message);
    }

    [Fact]
    public void ReadProgress_ThrowsAnException_WhenArgumentsAreNull()
    {
        Assert.Equal("connection", Assert.Throws<ArgumentNullException>(() => JobConsole.ReadProgress(null, "JobId")).ParamName);
        Assert.Equal("jobId", Assert.Throws<ArgumentNullException>(() => JobConsole.ReadProgress(_context.Connection.Object, null)).ParamName);
    }

    [Fact]
    public void ReadProgress_ReturnsNull_WhenTheStorageThrows()
    {
        _context.Connection.Setup(x => x.GetJobParameter("JobId", JobConsole.ProgressParameter)).Throws(new InvalidOperationException("storage is down"));

        Assert.Null(JobConsole.ReadProgress(_context.Connection.Object, "JobId"));
    }

    [Fact]
    public void ReadProgress_ReturnsNull_ForAMalformedParameter()
    {
        _context.Connection.Setup(x => x.GetJobParameter("JobId", JobConsole.ProgressParameter)).Returns("{not json");

        Assert.Null(JobConsole.ReadProgress(_context.Connection.Object, "JobId"));
    }

    [Fact, StaticLock]
    public void Console_IsInjected_IntoJobMethodParameters()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Report(null));
        CoreBackgroundJobPerformer performer = new(new JobActivator(), null);

        performer.Perform(_context.Object);

        _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.Is<string>(json => json.Contains("\"p\":50"))));
        Assert.Single(_inserted);
    }

    [Fact, StaticLock]
    public void LogInvocation_WritesStartAndFinishToTheConsole()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Logged());
        CoreBackgroundJobPerformer performer = new(new JobActivator(), null);

        performer.Perform(_context.Object);

        List<string?> messages = [.. _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal).Message)];
        Assert.Equal(2, messages.Count);
        Assert.StartsWith("Starting", messages[0]);
        Assert.StartsWith("Finished", messages[1]);
    }

    [Fact, StaticLock]
    public void LogInvocation_DoesNotFailTheJob_WhenWritingTheFinishLineFails()
    {
        // The method ran (an e-mail was sent): a timeout on the console write must not make it Failed and retried
        _transaction.SetupSequence(x => x.Commit()).Pass().Throws(new TimeoutException("SQL timeout"));
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Logged());
        CoreBackgroundJobPerformer performer = new(new JobActivator(), null);

        performer.Perform(_context.Object);

        List<string?> messages = [.. _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal).Message)];
        Assert.Equal(2, messages.Count);
        Assert.StartsWith("Finished", messages[1]);
    }

    [Fact, StaticLock]
    public void LogInvocation_DoesNotReportTheMethodAsFailed_WhenTheConsoleThrowsAfterItReturned()
    {
        Mock<IJobConsole> console = new();
        console.Setup(x => x.WriteLine(JobConsoleLevel.Debug, It.Is<string>(m => m.StartsWith("Finished", StringComparison.Ordinal))))
            .Throws(new TimeoutException("SQL timeout"));
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Logged());
        PerformContext context = _context.Object;
        context.Items[PerformContext.ConsoleItemKey] = console.Object;
        CoreBackgroundJobPerformer performer = new(new JobActivator(), null);

        Assert.ThrowsAny<Exception>(() => performer.Perform(context));

        console.Verify(x => x.WriteLine(JobConsoleLevel.Error, It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void LogInvocation_WritesTheException_AndRethrows()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.LoggedFailure());
        CoreBackgroundJobPerformer performer = new(new JobActivator(), null);

        Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

        List<JobConsoleEntry?> entries = [.. _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal))];
        Assert.Equal(JobConsoleLevel.Error, entries[1].Level);
        Assert.Contains("InvalidOperationException", entries[1].Message);
    }

    public class Jobs
    {
        public void Report(IJobConsole console)
        {
            console.SetProgress(50, "half");
            console.WriteLine("line");
        }

        [LogInvocation]
        public void Logged()
        {
        }

        [LogInvocation]
        public void LoggedFailure() => throw new InvalidOperationException("boom");
    }
}

public class JobConsoleStateFilterFacts
{
    private const string ConsoleKey = "job:JobId:console";
    private const string CheckpointsKey = "job:JobId:checkpoints";

    private readonly ApplyStateContextMock _context = new();
    private readonly Mock<JobStorageTransaction> _transaction = new();

    public JobConsoleStateFilterFacts()
    {
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
    }

    private void JobHasAConsole()
    {
        _context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { JobConsole.MarkerParameter, "1" } };
    }

    private void JobHasNoConsole()
    {
        _context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { "RetryCount", "1" } };
    }

    [Fact]
    public void OnStateApplied_ExpiresTheConsole_WithTheJob_ForFinalStates()
    {
        JobHasAConsole();
        _context.NewStateObject = new SucceededState(null, 0, 0);
        _context.JobExpirationTimeout = TimeSpan.FromHours(2);

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        _transaction.Verify(x => x.ExpireList(ConsoleKey, TimeSpan.FromHours(2)));
        _transaction.Verify(x => x.ExpireList(CheckpointsKey, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void OnStateApplied_PersistsTheConsole_ForNonFinalStates()
    {
        JobHasAConsole();
        _context.NewStateObject = new EnqueuedState();

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        _transaction.Verify(x => x.PersistList(ConsoleKey));
        _transaction.Verify(x => x.PersistList(CheckpointsKey));
        _transaction.Verify(x => x.InsertToList(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_FollowsTheJob_WhenTheStorageGivesNoParameterSnapshot()
    {
        _context.NewStateObject = new SucceededState(null, 0, 0);

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        _transaction.Verify(x => x.ExpireList(ConsoleKey, It.IsAny<TimeSpan>()));
    }

    [Theory]
    [InlineData("Enqueued")]
    [InlineData("Processing")]
    [InlineData("Succeeded")]
    public void OnStateApplied_LeavesJobsWithoutAConsoleAlone(string stateName)
    {
        JobHasNoConsole();
        _context.NewStateObject = stateName switch
        {
            "Enqueued" => new EnqueuedState(),
            "Processing" => new ProcessingState("server", "worker"),
            _ => new SucceededState(null, 0, 0)
        };

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        _transaction.VerifyNoOtherCalls();
    }

    [Fact]
    public void OnStateApplied_ExpiresTheConsole_WhenThePerformanceThatJustEndedWroteToIt()
    {
        PerformContextMock perform = new();
        perform.Connection.Setup(x => x.CreateWriteTransaction()).Returns(new Mock<IWriteOnlyTransaction>().Object);
        perform.Object.Console.WriteLine("hello");
        JobHasNoConsole();
        _context.CustomData = new Dictionary<string, object>(perform.Object.Items);
        _context.NewStateObject = new SucceededState(null, 0, 0);

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        _transaction.Verify(x => x.ExpireList(ConsoleKey, It.IsAny<TimeSpan>()));
        _transaction.Verify(x => x.ExpireList(CheckpointsKey, It.IsAny<TimeSpan>()));
    }

    [Fact]
    public void OnStateApplied_StillExpiresAConsoleWrittenBeforeTheMarkerExisted_WhenTheJobIsDeleted()
    {
        JobHasNoConsole();
        _context.NewStateObject = new DeletedState();

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        _transaction.Verify(x => x.ExpireList(ConsoleKey, It.IsAny<TimeSpan>()));
        _transaction.Verify(x => x.ExpireList(CheckpointsKey, It.IsAny<TimeSpan>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnStateApplied_DoesNothing_WhenTheStorageLacksTheExtendedApi(bool finalState)
    {
        // The base class throws NotSupportedException from ExpireList and PersistList
        Mock<JobStorageTransaction> transaction = new() { CallBase = true };
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        JobHasAConsole();
        _context.NewStateObject = finalState ? new SucceededState(null, 0, 0) : new EnqueuedState();

        new JobConsoleStateFilter().OnStateApplied(_context.Object, transaction.Object);

        transaction.Verify(x => x.ExpireList(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        transaction.Verify(x => x.PersistList(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Deleted")]
    public void OnStateApplied_ClearsTheCheckpoints_WhenAFinishedJobIsRequeued(string oldStateName)
    {
        JobHasAConsole();
        _context.OldStateName = oldStateName;
        _context.NewStateObject = new EnqueuedState();
        string? marker = null;
        _transaction.Setup(x => x.InsertToList(CheckpointsKey, It.IsAny<string>())).Callback<string, string>((_, value) => marker = value);

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        JobConsoleEntry? entry = SerializationHelper.Deserialize<JobConsoleEntry>(marker, SerializationOption.Internal);
        Assert.Null(entry.Checkpoint);
        _transaction.Verify(x => x.TrimList(CheckpointsKey, 0, 0));
        _transaction.Verify(x => x.PersistList(ConsoleKey));
        _transaction.Verify(x => x.PersistList(CheckpointsKey));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Scheduled")]
    [InlineData("Processing")]
    public void OnStateApplied_KeepsTheCheckpoints_WhenAnUnfinishedJobIsRequeued(string oldStateName)
    {
        JobHasAConsole();
        _context.OldStateName = oldStateName;
        _context.NewStateObject = new EnqueuedState();

        new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

        _transaction.Verify(x => x.InsertToList(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _transaction.Verify(x => x.TrimList(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _transaction.Verify(x => x.PersistList(CheckpointsKey));
    }

    [Fact]
    public void OnStateApplied_DoesNothing_ForTransactionsWithoutListExpiry()
    {
        _context.NewStateObject = new SucceededState(null, 0, 0);

        new JobConsoleStateFilter().OnStateApplied(_context.Object, new Mock<IWriteOnlyTransaction>().Object);
    }

    [Fact]
    public void OnStateApplied_ThrowsAnException_WhenArgumentsAreNull()
    {
        JobConsoleStateFilter filter = new();

        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => filter.OnStateApplied(null, _transaction.Object)).ParamName);
        Assert.Equal("transaction", Assert.Throws<ArgumentNullException>(() => filter.OnStateApplied(_context.Object, null)).ParamName);
    }

    [Fact]
    public void OnStateUnapplied_LeavesTheConsoleAlone()
    {
        _context.NewStateObject = new SucceededState(null, 0, 0);

        new JobConsoleStateFilter().OnStateUnapplied(_context.Object, _transaction.Object);

        _transaction.Verify(x => x.ExpireList(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        _transaction.Verify(x => x.PersistList(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Filter_IsRegisteredGlobally()
    {
        Assert.Contains(GlobalJobFilters.Filters, x => x.Instance is JobConsoleStateFilter);
    }
}
