using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    }
}
