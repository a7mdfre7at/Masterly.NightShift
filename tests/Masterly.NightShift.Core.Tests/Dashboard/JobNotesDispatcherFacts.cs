using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class JobNotesDispatcherFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();

        public JobNotesDispatcherFacts()
        {
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetJobData(It.IsAny<string>())).Returns(new JobData());
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(new Mock<IWriteOnlyTransaction>().Object);
        }

        [Fact]
        public async Task Dispatch_Answers422_ForTextLongerThanTheTextBoxAllows()
        {
            var context = Create("1", new string('x', JobNotes.MaxLength + 1));

            await new JobNotesDispatcher().Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Dispatch_AcceptsTextOfTheMaximumLength_AroundWhitespace()
        {
            var context = Create("1", "  " + new string('x', JobNotes.MaxLength) + "  ");

            await new JobNotesDispatcher().Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _connection.Verify(x => x.SetJobParameter("1", JobNotes.ParameterName, It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task Dispatch_DecodesAnEncodedSlashInTheJobId()
        {
            var context = Create("tenant%2F5", "note");

            await new JobNotesDispatcher().Dispatch(context);

            _connection.Verify(x => x.SetJobParameter("tenant/5", JobNotes.ParameterName, It.IsAny<string>()), Times.Once);
        }

        private DashboardContextStub Create(string jobId, string text)
        {
            var path = "/jobs/notes/add/" + jobId;
            var request = new DashboardRequestStub("POST", path);
            request.Form["text"] = new List<string> { text };

            return new DashboardContextStub(new DashboardOptions(), request, _storage.Object)
            {
                UriMatch = Regex.Match(path, "/jobs/notes/add/(?<JobId>.+)"),
            };
        }
    }
}
