using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class JobNotesFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Dictionary<string, string> _parameters = new Dictionary<string, string>();

        public JobNotesFacts()
        {
            JobNotes.Clock = () => _now;
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetJobParameter("1", It.IsAny<string>())).Returns<string, string>((_, name) => _parameters.TryGetValue(name, out var value) ? value : null);
            _connection.Setup(x => x.SetJobParameter("1", It.IsAny<string>(), It.IsAny<string>())).Callback<string, string, string>((_, name, value) => _parameters[name] = value);
            _connection.Setup(x => x.GetJobData("1")).Returns(new JobData());
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(new Mock<IWriteOnlyTransaction>().Object);
        }

        public void Dispose()
        {
            JobNotes.Clock = () => DateTime.UtcNow;
        }

        [Fact]
        public void Add_AppendsNotes_InOrder_WithUserAndTime()
        {
            JobNotes.Add(_connection.Object, "1", "  first  ", "ann");
            JobNotes.Add(_connection.Object, "1", "second", null, acknowledges: true);

            var notes = JobNotes.Read(_connection.Object, "1");

            Assert.Equal(2, notes.Count);
            Assert.Equal("first", notes[0].Text);
            Assert.Equal("ann", notes[0].User);
            Assert.Equal(_now, notes[0].WrittenAt);
            Assert.False(notes[0].Acknowledges);
            Assert.Null(notes[1].User);
            Assert.True(notes[1].Acknowledges);
            Assert.Same(notes[1].Acknowledges ? notes[1] : null, JobNotes.GetAcknowledgement(notes));
        }

        [Fact]
        public void Add_ReadsAndWritesTheNotes_UnderALockOfTheirOwn()
        {
            var calls = new List<string>();
            var handle = new Mock<IDisposable>();
            handle.Setup(x => x.Dispose()).Callback(() => calls.Add("release"));
            _connection.Setup(x => x.AcquireDistributedLock("job:1:notes-lock", JobNotes.LockTimeout)).Callback(() => calls.Add("lock")).Returns(handle.Object);
            _connection.Setup(x => x.GetJobParameter("1", JobNotes.ParameterName)).Callback(() => calls.Add("read")).Returns((string)null);
            _connection.Setup(x => x.SetJobParameter("1", JobNotes.ParameterName, It.IsAny<string>())).Callback(() => calls.Add("write"));

            JobNotes.Add(_connection.Object, "1", "note");

            Assert.Equal(new[] { "lock", "read", "write", "release" }, calls);
        }

        [Fact]
        public void Add_Throws_AndWritesNothing_WhenTheLockCanNotBeTaken()
        {
            _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Throws(new DistributedLockTimeoutException("job:1:notes-lock"));

            Assert.Throws<DistributedLockTimeoutException>(() => JobNotes.Add(_connection.Object, "1", "note"));

            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Add_RejectsEmptyNotes_UnlessAcknowledging_AndCapsLengthAndCount()
        {
            Assert.Throws<ArgumentException>(() => JobNotes.Add(_connection.Object, "1", "   "));

            JobNotes.Add(_connection.Object, "1", "", acknowledges: true);
            Assert.Single(JobNotes.Read(_connection.Object, "1"));

            JobNotes.Add(_connection.Object, "1", new string('x', 5000));
            Assert.Equal(JobNotes.MaxLength, JobNotes.Read(_connection.Object, "1")[1].Text.Length);

            for (var i = 0; i < JobNotes.MaxNotes + 5; i++) JobNotes.Add(_connection.Object, "1", "n" + i);
            var notes = JobNotes.Read(_connection.Object, "1");
            Assert.Equal(JobNotes.MaxNotes, notes.Count);
            Assert.Equal("n" + (JobNotes.MaxNotes + 4), notes.Last().Text);
        }

        [Fact, StaticLock]
        public void Add_AndRead_WorkWithARestrictiveUserTypeInfoResolver()
        {
            // An app's source-generated JsonSerializerContext doesn't know List<JobNote>: "Add note" answered 500
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new EmptyResolver() });
            try
            {
                JobNotes.Add(_connection.Object, "1", "looked at it", "alice", acknowledges: true);

                var note = Assert.Single(JobNotes.Read(_connection.Object, "1"));
                Assert.Equal("looked at it", note.Text);
                Assert.True(note.Acknowledges);
            }
            finally
            {
                SerializationHelper.SetUserSerializerSettings(null);
            }
        }

        [Fact, StaticLock]
        public void Parse_ReadsNotesWrittenWithTheUserOptions_Before()
        {
            var written = SerializationHelper.Serialize(new List<JobNote> { new JobNote { Timestamp = 1, Text = "old", User = "bob" } }, SerializationOption.User);

            var note = Assert.Single(JobNotes.Parse(written));

            Assert.Equal("old", note.Text);
            Assert.Equal("bob", note.User);
        }

        [Fact]
        public void Parse_ToleratesGarbage()
        {
            Assert.Empty(JobNotes.Parse(null));
            Assert.Empty(JobNotes.Parse("nope"));
        }

        [Fact]
        public async Task Dispatcher_AddsANote_AndRecordsIt()
        {
            var request = new DashboardRequestStub("POST", "/jobs/notes/add/1");
            request.Form["text"] = new List<string> { "partner API down" };
            request.Form["ack"] = new List<string> { "true" };
            var context = new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => false }, request, _storage.Object);
            context.UriMatch = System.Text.RegularExpressions.Regex.Match("/jobs/notes/add/1", "/jobs/notes/add/(?<JobId>.+)");

            await new JobNotesDispatcher().Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            var note = Assert.Single(JobNotes.Read(_connection.Object, "1"));
            Assert.Equal("partner API down", note.Text);
            Assert.True(note.Acknowledges);
        }

        [Fact]
        public async Task Dispatcher_Answers401_405_404_And422()
        {
            async Task<int> Run(string method, bool readOnly, string jobId, string text)
            {
                var request = new DashboardRequestStub(method, "/jobs/notes/add/" + jobId);
                if (text != null) request.Form["text"] = new List<string> { text };
                var context = new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object);
                context.UriMatch = System.Text.RegularExpressions.Regex.Match("/jobs/notes/add/" + jobId, "/jobs/notes/add/(?<JobId>.+)");
                await new JobNotesDispatcher().Dispatch(context);
                return context.Response.StatusCode;
            }

            Assert.Equal(401, await Run("POST", true, "1", "x"));
            Assert.Equal(405, await Run("GET", false, "1", "x"));
            Assert.Equal(422, await Run("POST", false, "1", "  "));
            Assert.Equal(404, await Run("POST", false, "missing", "x"));
        }

        private sealed class EmptyResolver : IJsonTypeInfoResolver
        {
            public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options) => null;
        }
    }
}
