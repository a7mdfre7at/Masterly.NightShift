using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Server
{
    public class JobConsoleResumeFacts : IDisposable
    {
        private const string Key = "job:JobId:console";
        private const string CheckpointsKey = "job:JobId:checkpoints";

        private readonly PerformContextMock _context = new PerformContextMock();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<JobStorageTransaction> _transaction = new Mock<JobStorageTransaction>();
        private readonly Dictionary<string, List<string>> _lists = new Dictionary<string, List<string>>
        {
            { Key, new List<string>() },
            { CheckpointsKey, new List<string>() },
        };

        public JobConsoleResumeFacts()
        {
            _context.Connection = _connection.As<IStorageConnection>();
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _connection.Setup(x => x.GetAllItemsFromList(It.IsAny<string>())).Returns<string>(key => _lists[key].ToList());

            // Lists keep the newest item first, like the storages do
            _transaction.Setup(x => x.InsertToList(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((key, value) => _lists[key].Insert(0, value));
            _transaction.Setup(x => x.TrimList(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                .Callback<string, int, int>((key, start, end) =>
                {
                    var kept = _lists[key].Skip(start).Take(end - start + 1).ToList();
                    _lists[key].Clear();
                    _lists[key].AddRange(kept);
                });
        }

        public void Dispose()
        {
            JobConsole.DefaultMaxLines = 500;
        }

        private List<string> ConsoleList => _lists[Key];
        private List<string> CheckpointsList => _lists[CheckpointsKey];

        /// <summary>Checkpoints as builds before the checkpoints list stored them: in the console list only.</summary>
        private void OlderBuildReached(params string[] checkpoints)
        {
            var timestamp = 1000L;
            foreach (var checkpoint in checkpoints)
            {
                ConsoleList.Insert(0, SerializationHelper.Serialize(new JobConsoleEntry { Timestamp = timestamp++, Checkpoint = checkpoint }, SerializationOption.Internal));
            }
        }

        private IJobConsole NextAttempt()
        {
            var context = new PerformContextMock { Connection = _connection.As<IStorageConnection>() };
            return context.Object.Console;
        }

        [Fact]
        public void Checkpoints_ComeFromEarlierAttempts_AndThisOne()
        {
            _context.Object.Console.Checkpoint("download");
            _context.Object.Console.Checkpoint("parse");
            var console = NextAttempt();

            Assert.Equal(new[] { "download", "parse" }, console.Checkpoints);
            Assert.True(console.HasCheckpoint("parse"));
            Assert.False(console.HasCheckpoint("save"));

            console.Checkpoint("save");

            Assert.True(console.HasCheckpoint("save"));
            Assert.Equal(3, console.Checkpoints.Count);
        }

        [Fact]
        public void Checkpoint_IsWrittenToTheConsole_AndOnceToTheCheckpointsList()
        {
            var console = _context.Object.Console;

            console.Checkpoint("download");
            console.Checkpoint("download");

            Assert.Equal(2, ConsoleList.Count);
            var entry = SerializationHelper.Deserialize<JobConsoleEntry>(Assert.Single(CheckpointsList), SerializationOption.Internal);
            Assert.Equal("download", entry.Checkpoint);
        }

        [Fact]
        public void Checkpoints_SurviveTheConsoleBeingTrimmed()
        {
            JobConsole.DefaultMaxLines = 5;
            var console = _context.Object.Console;
            var ran = new List<string>();

            console.Resume("download", () => ran.Add("download"));
            for (var i = 0; i < 20; i++) console.WriteLine($"row {i}");
            NextAttempt().Resume("download", () => ran.Add("download again"));

            Assert.Equal(5, ConsoleList.Count);
            Assert.DoesNotContain(ConsoleList, item => item.Contains("\"c\":\"download\""));
            Assert.Equal(new[] { "download" }, ran);
        }

        [Fact]
        public void Checkpoints_ReturnsACopy()
        {
            var console = _context.Object.Console;
            var before = console.Checkpoints;

            console.Checkpoint("download");

            Assert.Empty(before);
            Assert.Equal(new[] { "download" }, console.Checkpoints);
        }

        [Fact]
        public void Checkpoint_FromSeveralThreads_KeepsEveryName()
        {
            var console = _context.Object.Console;

            Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => console.Checkpoint($"step {i}"));

            Assert.Equal(200, console.Checkpoints.Distinct().Count());
            Assert.Equal(200, CheckpointsList.Count);
        }

        [Fact]
        public void Checkpoints_WrittenByAnOlderBuild_AreReadFromTheConsoleList()
        {
            OlderBuildReached("download", "parse");
            var console = _context.Object.Console;

            Assert.Equal(new[] { "download", "parse" }, console.Checkpoints);
        }

        [Fact]
        public void Checkpoints_WrittenByAnOlderBuild_MoveToTheCheckpointsList_WithTheFirstNewOne()
        {
            JobConsole.DefaultMaxLines = 3;
            OlderBuildReached("download", "download", "parse");

            _context.Object.Console.Checkpoint("save");
            for (var i = 0; i < 10; i++) _context.Object.Console.WriteLine($"row {i}");

            Assert.Equal(new[] { "download", "parse", "save" }, NextAttempt().Checkpoints);
            Assert.Equal(3, CheckpointsList.Count);
        }

        [Fact]
        public void Checkpoints_WrittenByAnOlderBuild_AreIgnored_WhenTheJobFinishedAndWasRequeued()
        {
            OlderBuildReached("download");
            _connection.Setup(x => x.GetListTtl(Key)).Returns(TimeSpan.FromHours(20));

            Assert.Empty(_context.Object.Console.Checkpoints);
        }

        [Fact]
        public void Checkpoints_StartOver_AfterARestartMarker()
        {
            _context.Object.Console.Checkpoint("download");
            CheckpointsList.Clear();
            CheckpointsList.Add(JobConsole.CreateRestartMarker());

            var console = NextAttempt();

            Assert.Empty(console.Checkpoints);
            Assert.True(ConsoleList.Count > 0);
        }

        [Fact]
        public void Resume_RunsEveryStepAgain_AfterASucceededJobIsRequeued()
        {
            var ran = new List<string>();
            _context.Object.Console.Resume("download", () => ran.Add("first"));
            ApplyState(new SucceededState(null, 0, 0), ProcessingState.StateName);
            ApplyState(new EnqueuedState(), SucceededState.StateName);

            NextAttempt().Resume("download", () => ran.Add("second"));

            Assert.Equal(new[] { "first", "second" }, ran);
        }

        [Fact]
        public void Resume_SkipsFinishedSteps_AfterAFailedJobIsRequeued()
        {
            var ran = new List<string>();
            _context.Object.Console.Resume("download", () => ran.Add("first"));
            ApplyState(new FailedState(new InvalidOperationException()), ProcessingState.StateName);
            ApplyState(new EnqueuedState(), FailedState.StateName);

            NextAttempt().Resume("download", () => ran.Add("second"));

            Assert.Equal(new[] { "first" }, ran);
        }

        private void ApplyState(IState state, string oldStateName)
        {
            var context = new ApplyStateContextMock
            {
                NewStateObject = state,
                OldStateName = oldStateName,
                Transaction = _transaction.As<IWriteOnlyTransaction>()
            };
            context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { JobConsole.MarkerParameter, "1" } };

            new JobConsoleStateFilter().OnStateApplied(context.Object, _transaction.Object);
        }

        [Fact]
        public void Checkpoints_StartEmpty_WhenTheStorageCannotReadLists()
        {
            _connection.Setup(x => x.GetAllItemsFromList(It.IsAny<string>())).Throws(new NotSupportedException("no extended API"));
            var console = _context.Object.Console;

            var countBefore = console.Checkpoints.Count;
            console.Checkpoint("download");

            Assert.Equal(0, countBefore);
            Assert.True(console.HasCheckpoint("download"));
            Assert.Equal(new[] { "download" }, console.Checkpoints);
        }

        [Fact]
        public void Resume_Throws_InsteadOfRedoingSteps_WhenTheStorageFailsToReadTheCheckpoints()
        {
            // Used to count as "no checkpoints", which made Resume run finished steps again
            _connection.Setup(x => x.GetAllItemsFromList(It.IsAny<string>())).Throws(new InvalidOperationException("list read failed"));
            var console = _context.Object.Console;
            var ran = false;

            Assert.Throws<InvalidOperationException>(() => console.Resume("download", () => ran = true));

            Assert.False(ran);
        }

        [Fact]
        public void Resume_SkipsStepsThatAlreadyFinished_AndRecordsNewOnes()
        {
            OlderBuildReached("download");
            var console = _context.Object.Console;
            var ran = new List<string>();

            console.Resume("download", () => ran.Add("download"));
            console.Resume("parse", () => ran.Add("parse"));

            Assert.Equal(new[] { "parse" }, ran);
            Assert.True(console.HasCheckpoint("parse"));
            Assert.Contains(ConsoleList, item => item.Contains("\"c\":\"parse\""));
            Assert.Contains(ConsoleList, item => item.Contains("Skipped 'download'"));
        }

        [Fact]
        public async Task ResumeAsync_BehavesLikeResume()
        {
            OlderBuildReached("download");
            var console = _context.Object.Console;
            var ran = new List<string>();

            await console.ResumeAsync("download", () => { ran.Add("download"); return Task.CompletedTask; });
            await console.ResumeAsync("parse", () => { ran.Add("parse"); return Task.CompletedTask; });

            Assert.Equal(new[] { "parse" }, ran);
        }

        [Fact]
        public void Resume_DoesNotRecordTheCheckpoint_WhenTheStepThrows()
        {
            var console = _context.Object.Console;

            Assert.Throws<InvalidOperationException>(() => console.Resume("parse", () => throw new InvalidOperationException()));

            Assert.False(console.HasCheckpoint("parse"));
        }

        [Fact]
        public void Checkpoints_AreEmpty_WhenTheStorageCannotReadLists()
        {
            var plain = new PerformContextMock();
            plain.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);

            Assert.Empty(plain.Object.Console.Checkpoints);
            Assert.False(plain.Object.Console.HasCheckpoint("x"));
        }

        [Fact]
        public void Checkpoints_AreEmpty_WhenTheConnectionLacksTheExtendedApi()
        {
            var plain = new PerformContextMock();
            var connection = new Mock<JobStorageConnection> { CallBase = true };
            plain.Connection = connection.As<IStorageConnection>();

            Assert.Empty(plain.Object.Console.Checkpoints);
        }
    }
}
