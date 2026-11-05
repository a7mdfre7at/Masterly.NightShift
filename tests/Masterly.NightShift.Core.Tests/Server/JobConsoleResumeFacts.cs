using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Server
{
    public class JobConsoleResumeFacts
    {
        private const string Key = "job:JobId:console";

        private readonly PerformContextMock _context = new PerformContextMock();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly List<string> _list = new List<string>();

        public JobConsoleResumeFacts()
        {
            _context.Connection = _connection.As<IStorageConnection>();
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _connection.Setup(x => x.GetAllItemsFromList(Key)).Returns(() => _list.ToList());
            _transaction.Setup(x => x.InsertToList(Key, It.IsAny<string>())).Callback<string, string>((_, value) => _list.Insert(0, value));
        }

        private void PreviousAttemptReached(params string[] checkpoints)
        {
            var timestamp = 1000L;
            foreach (var checkpoint in checkpoints)
            {
                _list.Insert(0, SerializationHelper.Serialize(new JobConsoleEntry { Timestamp = timestamp++, Checkpoint = checkpoint }, SerializationOption.Internal));
            }
        }

        [Fact]
        public void Checkpoints_ComeFromEarlierAttempts_AndThisOne()
        {
            PreviousAttemptReached("download", "parse");
            var console = _context.Object.Console;

            Assert.Equal(new[] { "download", "parse" }, console.Checkpoints);
            Assert.True(console.HasCheckpoint("parse"));
            Assert.False(console.HasCheckpoint("save"));

            console.Checkpoint("save");

            Assert.True(console.HasCheckpoint("save"));
            Assert.Equal(3, console.Checkpoints.Count);
        }

        [Fact]
        public void Checkpoints_StartEmpty_WhenTheStorageCannotReadTheConsole()
        {
            _connection.Setup(x => x.GetAllItemsFromList(Key)).Throws(new InvalidOperationException("list read failed"));
            var console = _context.Object.Console;

            var countBefore = console.Checkpoints.Count;
            console.Checkpoint("download");

            Assert.Equal(0, countBefore);
            Assert.True(console.HasCheckpoint("download"));
            Assert.Equal(new[] { "download" }, console.Checkpoints);
        }

        [Fact]
        public void Resume_SkipsStepsThatAlreadyFinished_AndRecordsNewOnes()
        {
            PreviousAttemptReached("download");
            var console = _context.Object.Console;
            var ran = new List<string>();

            console.Resume("download", () => ran.Add("download"));
            console.Resume("parse", () => ran.Add("parse"));

            Assert.Equal(new[] { "parse" }, ran);
            Assert.True(console.HasCheckpoint("parse"));
            Assert.Contains(_list, item => item.Contains("\"c\":\"parse\""));
            Assert.Contains(_list, item => item.Contains("Skipped 'download'"));
        }

        [Fact]
        public async Task ResumeAsync_BehavesLikeResume()
        {
            PreviousAttemptReached("download");
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
    }
}
