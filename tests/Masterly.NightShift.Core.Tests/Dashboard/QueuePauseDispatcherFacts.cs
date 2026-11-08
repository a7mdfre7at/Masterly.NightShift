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
    public class QueuePauseDispatcherFacts : IDisposable
    {
        private const string PauseRoute = "/queues/pause/(?<Queue>.+)";
        private const string ResumeRoute = "/queues/resume/(?<Queue>.+)";

        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, string> _hash = new Dictionary<string, string>();

        public QueuePauseDispatcherFacts()
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
            var context = Create("/queues/pause/critical", PauseRoute, readOnly: true);

            await new QueuePauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
            _storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact]
        public async Task Dispatch_Sets405_ForNonPostRequests()
        {
            var context = Create("/queues/pause/critical", PauseRoute, method: "GET");

            await new QueuePauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(405, context.Response.StatusCode);
            _storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact]
        public async Task Dispatch_Sets422_WhenTheQueueIsBlank()
        {
            var context = Create("/queues/pause/ ", PauseRoute);

            await new QueuePauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            _storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact, StaticLock]
        public async Task Dispatch_PausesTheUnescapedQueue_WithTheTrimmedReason()
        {
            var context = Create("/queues/pause/Critical Emails", PauseRoute, reason: "  smtp down  ");

            await new QueuePauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            var status = DrainMode.Read(_connection.Object);
            Assert.True(status.IsQueuePaused("critical emails"));
            Assert.Equal("smtp down", status.PausedQueues["critical emails"]);
            Assert.False(status.Active);
            _transaction.Verify(x => x.Commit(), Times.AtLeastOnce);
        }

        [Fact, StaticLock]
        public async Task Dispatch_PausesTheQueue_WithoutAReason_WhenNoneIsGiven()
        {
            var context = Create("/queues/pause/default", PauseRoute);

            await new QueuePauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            Assert.Equal(String.Empty, _hash[DrainMode.QueueFieldPrefix + "default"]);
            Assert.Null(DrainMode.Read(_connection.Object).PausedQueues["default"]);
        }

        [Fact, StaticLock]
        public async Task Dispatch_TruncatesTheReason_To200Characters()
        {
            var context = Create("/queues/pause/default", PauseRoute, reason: new string('q', 300));

            await new QueuePauseDispatcher(pause: true).Dispatch(context);

            Assert.Equal(new string('q', 200), _hash[DrainMode.QueueFieldPrefix + "default"]);
        }

        [Fact, StaticLock]
        public async Task Dispatch_ResumesAPausedQueue()
        {
            _hash[DrainMode.QueueFieldPrefix + "critical"] = "smtp down";
            var context = Create("/queues/resume/CRITICAL", ResumeRoute);

            await new QueuePauseDispatcher(pause: false).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            Assert.False(DrainMode.Read(_connection.Object).IsQueuePaused("critical"));
        }

        [Fact, StaticLock]
        public async Task Dispatch_RecordsTheQueueAndTheReason_InTheAuditLog()
        {
            var context = Create("/queues/pause/reports", PauseRoute, reason: "month end");

            await new QueuePauseDispatcher(pause: true).Dispatch(context);

            _transaction.Verify(x => x.InsertToList(
                AuditLog.ListKey,
                It.Is<string>(s => s.Contains("\"reports\"") && s.Contains("\"month end\"") && s.Contains("/queues/pause/reports"))));
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
