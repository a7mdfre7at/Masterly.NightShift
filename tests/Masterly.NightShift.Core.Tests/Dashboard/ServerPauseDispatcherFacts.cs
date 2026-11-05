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
    public class ServerPauseDispatcherFacts : IDisposable
    {
        private const string PauseRoute = "/servers/pause/(?<Id>.+)";
        private const string ResumeRoute = "/servers/resume/(?<Id>.+)";

        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, string> _hash = new Dictionary<string, string>();

        public ServerPauseDispatcherFacts()
        {
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) =>
                {
                    foreach (var pair in pairs) _hash[pair.Key] = pair.Value;
                });
            DrainMode.InvalidateCache();
        }

        public void Dispose()
        {
            DrainMode.InvalidateCache();
        }

        [Fact]
        public async Task Dispatch_Sets401_WhenReadOnly()
        {
            var context = Create("/servers/pause/web-1", PauseRoute, readOnly: true);

            await new ServerPauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
            _storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact]
        public async Task Dispatch_Sets405_ForNonPostRequests()
        {
            var context = Create("/servers/pause/web-1", PauseRoute, method: "GET");

            await new ServerPauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(405, context.Response.StatusCode);
            _storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact]
        public async Task Dispatch_Sets422_WhenTheServerIdIsBlank()
        {
            var context = Create("/servers/pause/%20%20", PauseRoute);

            await new ServerPauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            _storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact, StaticLock]
        public async Task Dispatch_PausesTheUnescapedServer_WithTheTrimmedReason()
        {
            var context = Create("/servers/pause/web-1%3A42", PauseRoute, reason: "  deploy  ");

            await new ServerPauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            var status = DrainMode.Read(_connection.Object);
            Assert.True(status.IsServerPaused("web-1:42"));
            Assert.Equal("deploy", status.PausedServers["web-1:42"]);
            Assert.False(status.Active);
            _transaction.Verify(x => x.Commit(), Times.AtLeastOnce);
        }

        [Fact, StaticLock]
        public async Task Dispatch_PausesTheServer_WithoutAReason_WhenNoneIsGiven()
        {
            var context = Create("/servers/pause/web-1", PauseRoute, reason: "   ");

            await new ServerPauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            Assert.Equal(String.Empty, _hash[DrainMode.ServerFieldPrefix + "web-1"]);
            Assert.Null(DrainMode.Read(_connection.Object).PausedServers["web-1"]);
        }

        [Fact, StaticLock]
        public async Task Dispatch_TruncatesTheReason_To200Characters()
        {
            var context = Create("/servers/pause/web-1", PauseRoute, reason: new string('r', 250));

            await new ServerPauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(new string('r', 200), _hash[DrainMode.ServerFieldPrefix + "web-1"]);
        }

        [Fact, StaticLock]
        public async Task Dispatch_ResumesAPausedServer_IgnoringAnyReason()
        {
            _hash[DrainMode.ServerFieldPrefix + "web-1"] = "deploy";
            var context = Create("/servers/resume/web-1", ResumeRoute, reason: "ignored");

            await new ServerPauseDispatcher(pause: false).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            Assert.False(DrainMode.Read(_connection.Object).IsServerPaused("web-1"));
            Assert.NotEqual("ignored", _hash[DrainMode.ServerFieldPrefix + "web-1"]);
        }

        [Fact, StaticLock]
        public async Task Dispatch_RecordsTheServerAndTheReason_InTheAuditLog()
        {
            var context = Create("/servers/pause/web-7", PauseRoute, reason: "debugging");

            await new ServerPauseDispatcher(pause: true).Dispatch(context);

            _transaction.Verify(x => x.InsertToList(
                AuditLog.ListKey,
                It.Is<string>(s => s.Contains("\"web-7\"") && s.Contains("\"debugging\"") && s.Contains("/servers/pause/web-7"))));
        }

        private DashboardContextStub Create(string path, string route, string method = "POST", bool readOnly = false, string reason = null)
        {
            var request = new DashboardRequestStub(method, path);
            if (reason != null) request.Form["reason"] = new List<string> { reason };

            return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object)
            {
                UriMatch = Regex.Match(path, route),
            };
        }
    }
}
