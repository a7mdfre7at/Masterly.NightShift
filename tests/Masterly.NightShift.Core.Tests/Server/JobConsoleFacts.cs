using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Masterly.NightShift.Common;
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
        public void Messages_AreTruncated_ToTheMaximumLength()
        {
            _context.Object.Console.WriteLine(new string('x', JobConsole.MaxMessageLength + 100));

            var entry = SerializationHelper.Deserialize<JobConsoleEntry>(_inserted.Single(), SerializationOption.Internal);
            Assert.Equal(JobConsole.MaxMessageLength, entry.Message.Length);
        }

        [Fact]
        public void ReadEntries_ReturnsEmpty_ForConnectionsWithoutListSupport()
        {
            var entries = JobConsole.ReadEntries(_context.Connection.Object, "JobId");

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
        private readonly ApplyStateContextMock _context = new ApplyStateContextMock();
        private readonly Mock<JobStorageTransaction> _transaction = new Mock<JobStorageTransaction>();

        [Fact]
        public void OnStateApplied_ExpiresTheConsole_WithTheJob_ForFinalStates()
        {
            _context.NewStateObject = new SucceededState(null, 0, 0);
            _context.JobExpirationTimeout = TimeSpan.FromHours(2);

            new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.ExpireList("job:JobId:console", TimeSpan.FromHours(2)));
        }

        [Fact]
        public void OnStateApplied_PersistsTheConsole_ForNonFinalStates()
        {
            _context.NewStateObject = new EnqueuedState();

            new JobConsoleStateFilter().OnStateApplied(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.PersistList("job:JobId:console"));
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
