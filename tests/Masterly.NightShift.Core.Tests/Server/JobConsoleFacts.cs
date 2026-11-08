using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Server
{
    public class JobConsoleFacts : IDisposable
    {
        private const string Key = "job:JobId:console";

        private readonly PerformContextMock _context = new PerformContextMock();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly List<string> _inserted = new List<string>();
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

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
            var context = _context.Object;
            var performing = new PerformingContext(context);

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
            var console = _context.Object.Console;

            console.SetProgress(250);
            console.SetProgress(-3);

            _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.Is<string>(json => json.Contains("\"p\":100"))));
            _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.Is<string>(json => json.Contains("\"p\":0"))));
        }

        [Fact]
        public void SetProgress_SkipsWrites_WhenNothingChanged()
        {
            var console = _context.Object.Console;

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

            var entry = SerializationHelper.Deserialize<JobConsoleEntry>(_inserted.Single(), SerializationOption.Internal);
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
            var console = _context.Object.Console;

            console.WriteLine(JobConsoleLevel.Warning, "careful");
            console.WriteLine(JobConsoleLevel.Error, "boom");

            var entries = _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal)).ToList();
            Assert.Equal(JobConsoleLevel.Warning, entries[0].Level);
            Assert.Equal(JobConsoleLevel.Error, entries[1].Level);
            Assert.Equal(new[] { 1, 2 }, entries.Select(x => x.Sequence));
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
            var log = new RecordingLogProvider();

            using (log.Use())
            {
                _context.Object.Console.WriteLine("line");
            }

            var entry = Assert.Single(log.Entries, static x => x.Logger == typeof(JobConsole).FullName);
            Assert.Equal(LogLevel.Warn, entry.Level);
            Assert.Contains("'JobId'", entry.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(entry.Exception);
        }

        [Fact, StaticLock]
        public void WriteLine_LogsDroppedWrites_AtMostOnceAMinute_WithTheirCount()
        {
            _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("SQL timeout"));
            _context.Connection.Setup(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.IsAny<string>())).Throws(new TimeoutException("SQL timeout"));
            var now = _now;
            JobConsole.Clock = () => now;
            var console = _context.Object.Console;
            var log = new RecordingLogProvider();

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

            var messages = log.Entries.Where(static x => x.Logger == typeof(JobConsole).FullName).Select(static x => x.Message).ToArray();
            Assert.Equal(2, messages.Length);
            Assert.Contains(" 1 progress report(s) or line(s)", messages[0], StringComparison.Ordinal);
            Assert.Contains(" 4 progress report(s) or line(s)", messages[1], StringComparison.Ordinal);
        }

        [Fact]
        public void WriteLine_WritesAgain_OnceTheStorageRecovers()
        {
            _transaction.SetupSequence(x => x.Commit()).Throws(new TimeoutException("SQL timeout")).Pass();
            var console = _context.Object.Console;

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
            var console = _context.Object.Console;

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

            var entry = SerializationHelper.Deserialize<JobConsoleEntry>(_inserted.Single(), SerializationOption.Internal);
            Assert.Equal("loaded", entry.Checkpoint);
            Assert.Null(entry.Message);
            Assert.Null(entry.Level);
        }

        [Fact]
        public void HasCheckpoint_Throws_WhenTheStorageFails_AndReadsAgainOnTheNextCall()
        {
            var connection = new Mock<JobStorageConnection> { CallBase = true };
            var entry = SerializationHelper.Serialize(new JobConsoleEntry { Checkpoint = "step", Timestamp = 1 }, SerializationOption.Internal);
            connection.SetupSequence(x => x.GetAllItemsFromList(JobConsole.GetCheckpointsKey("JobId")))
                .Throws(new TimeoutException("SQL timeout"))
                .Returns(new List<string> { entry });
            _context.Storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            var console = _context.Object.Console;

            // Treating the error as "no checkpoints" would make Resume redo the finished step
            Assert.Throws<TimeoutException>(() => console.HasCheckpoint("step"));
            Assert.True(console.HasCheckpoint("step"));
        }

        [Fact]
        public void HasCheckpoint_ReturnsFalse_WhenTheStorageCannotReadLists()
        {
            // The base class throws NotSupportedException from GetAllItemsFromList
            var connection = new Mock<JobStorageConnection> { CallBase = true };
            _context.Storage.Setup(x => x.GetConnection()).Returns(connection.Object);

            Assert.False(_context.Object.Console.HasCheckpoint("step"));
            Assert.Empty(_context.Object.Console.Checkpoints);
        }

        [Fact]
        public void Messages_AreTruncated_ToTheMaximumLength()
        {
            _context.Object.Console.WriteLine(new string('x', JobConsole.MaxMessageLength + 100));

            var entry = SerializationHelper.Deserialize<JobConsoleEntry>(_inserted.Single(), SerializationOption.Internal);
            Assert.Equal(JobConsole.MaxMessageLength, entry.Message.Length);
        }

        [Fact]
        public void Writes_GoThroughAShortLivedConnection_WhenTheStorageIsKnown()
        {
            var owned = new Mock<IStorageConnection>();
            var ownedTransaction = new Mock<IWriteOnlyTransaction>();
            owned.Setup(x => x.CreateWriteTransaction()).Returns(ownedTransaction.Object);
            _context.Storage.Setup(x => x.GetConnection()).Returns(owned.Object);
            var console = _context.Object.Console;

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
            var running = 0;
            var overlaps = 0;
            _transaction.Setup(x => x.InsertToList(Key, It.IsAny<string>())).Callback(() =>
            {
                if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlaps);
                Thread.Sleep(1);
                Interlocked.Decrement(ref running);
            });
            var console = _context.Object.Console;

            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => console.WriteLine($"line {i}"));

            Assert.Equal(0, overlaps);
            _transaction.Verify(x => x.Commit(), Times.Exactly(64));
        }

        [Fact]
        public void Console_IsCreatedOnce_WhenSeveralThreadsAskForIt()
        {
            var context = _context.Object;
            var consoles = new ConcurrentBag<IJobConsole>();

            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => consoles.Add(new PerformContext(context).Console));

            Assert.Single(consoles.Distinct());
        }

        [Fact]
        public void FirstWrite_MarksTheJob_SoTheStateFilterKnowsItHasAConsole()
        {
            var console = _context.Object.Console;

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
            var console = _context.Object.Console;

            console.SetProgress(10);

            _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.MarkerParameter, It.IsAny<string>()), Times.Never);
            Assert.False(((JobConsole)console).HasStorageData);
        }

        [Fact]
        public void Writes_AreSkipped_OnceTheJobIsAborted()
        {
            var state = new StateData { Name = DeletedState.StateName, Data = new Dictionary<string, string>() };
            _context.Connection.Setup(x => x.GetStateData("JobId")).Returns(state);

            using (var token = new ServerJobCancellationToken(_context.Connection.Object, "JobId", "server", "worker", CancellationToken.None))
            {
                var console = new PerformContext(_context.Storage.Object, _context.Connection.Object, _context.BackgroundJob.Object, token).Console;
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
        }

        [Fact]
        public void Writes_AreSkipped_AfterThePerformanceIsOver()
        {
            var token = new ServerJobCancellationToken(_context.Connection.Object, "JobId", "server", "worker", CancellationToken.None);
            var console = new PerformContext(_context.Storage.Object, _context.Connection.Object, _context.BackgroundJob.Object, token).Console;
            token.Dispose();

            console.WriteLine("too late");
            console.SetProgress(100);

            Assert.Empty(_inserted);
            _context.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void ReadEntries_ReturnsEmpty_ForConnectionsWithoutListSupport()
        {
            var entries = JobConsole.ReadEntries(_context.Connection.Object, "JobId");

            Assert.Empty(entries);
        }

        [Fact]
        public void ReadEntries_ReturnsEmpty_WhenTheConnectionLacksTheExtendedApi()
        {
            var connection = new Mock<JobStorageConnection> { CallBase = true };

            var entries = JobConsole.ReadEntries(connection.Object, "JobId");

            Assert.Empty(entries);
        }

        [Fact]
        public void ReadEntries_OrdersByTimestampThenSequence_AndSkipsGarbage()
        {
            var connection = new Mock<JobStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromList(Key)).Returns(new List<string>
            {
                "{\"t\":2000,\"s\":1,\"l\":\"Info\",\"m\":\"second\"}",
                "not json",
                "{\"t\":1000,\"s\":2,\"c\":\"start\"}",
                "{\"t\":1000,\"s\":1,\"l\":\"Debug\",\"m\":\"first\"}",
            });

            var entries = JobConsole.ReadEntries(connection.Object, "JobId");

            Assert.Equal(new[] { "first", null, "second" }, entries.Select(x => x.Message));
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

            var progress = JobConsole.ReadProgress(_context.Connection.Object, "JobId");

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
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);

            performer.Perform(_context.Object);

            _context.Connection.Verify(x => x.SetJobParameter("JobId", JobConsole.ProgressParameter, It.Is<string>(json => json.Contains("\"p\":50"))));
            Assert.Single(_inserted);
        }

        [Fact, StaticLock]
        public void LogInvocation_WritesStartAndFinishToTheConsole()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Logged());
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);

            performer.Perform(_context.Object);

            var messages = _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal).Message).ToList();
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
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);

            performer.Perform(_context.Object);

            var messages = _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal).Message).ToList();
            Assert.Equal(2, messages.Count);
            Assert.StartsWith("Finished", messages[1]);
        }

        [Fact, StaticLock]
        public void LogInvocation_DoesNotReportTheMethodAsFailed_WhenTheConsoleThrowsAfterItReturned()
        {
            var console = new Mock<IJobConsole>();
            console.Setup(x => x.WriteLine(JobConsoleLevel.Debug, It.Is<string>(m => m.StartsWith("Finished", StringComparison.Ordinal))))
                .Throws(new TimeoutException("SQL timeout"));
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Logged());
            var context = _context.Object;
            context.Items[PerformContext.ConsoleItemKey] = console.Object;
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);

            Assert.ThrowsAny<Exception>(() => performer.Perform(context));

            console.Verify(x => x.WriteLine(JobConsoleLevel.Error, It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void LogInvocation_WritesTheException_AndRethrows()
        {
            _context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.LoggedFailure());
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);

            Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

            var entries = _inserted.Select(x => SerializationHelper.Deserialize<JobConsoleEntry>(x, SerializationOption.Internal)).ToList();
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

        private readonly ApplyStateContextMock _context = new ApplyStateContextMock();
        private readonly Mock<JobStorageTransaction> _transaction = new Mock<JobStorageTransaction>();

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
            var perform = new PerformContextMock();
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
            var transaction = new Mock<JobStorageTransaction> { CallBase = true };
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
            var marker = default(string);
            _transaction.Setup(x => x.InsertToList(CheckpointsKey, It.IsAny<string>())).Callback<string, string>((_, value) => marker = value);

            new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

            var entry = SerializationHelper.Deserialize<JobConsoleEntry>(marker, SerializationOption.Internal);
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
            var filter = new JobConsoleStateFilter();

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
}
