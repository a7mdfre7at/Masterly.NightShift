using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class AuditLogFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 16, 0, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly List<string> _list = new List<string>();

        public AuditLogFacts()
        {
            AuditLog.Clock = () => _now;
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _connection.Setup(x => x.GetRangeFromList(AuditLog.ListKey, 0, It.IsAny<int>())).Returns<string, int, int>((_, __, to) => _list.Take(to + 1).ToList());
            _connection.Setup(x => x.GetListCount(AuditLog.ListKey)).Returns(() => _list.Count);
            _transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => _list.Insert(0, value));
        }

        public void Dispose()
        {
            AuditLog.Clock = () => DateTime.UtcNow;
            AuditLog.Enabled = true;
        }

        private DashboardContextStub Context(string path, string method = "POST", bool readOnly = false)
        {
            return new UserContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, new DashboardRequestStub(method, path), _storage.Object);
        }

        private sealed class UserContextStub : DashboardContextStub
        {
            public UserContextStub(DashboardOptions options, DashboardRequestStub request, JobStorage storage) : base(options, request, storage) { }
            public override string GetUserName() => "ahmad";
        }

        [Fact]
        public void Record_WritesUserPathTargetsAndDetail_AndTrimsTheList()
        {
            var context = Context("/jobs/failed/requeue");

            AuditLog.Record(context, new[] { "1", "2" }, "because", 204);

            var entry = Assert.Single(AuditLog.Read(_connection.Object, 10));
            Assert.Equal("ahmad", entry.User);
            Assert.Equal("127.0.0.1", entry.RemoteIpAddress);
            Assert.Equal("/jobs/failed/requeue", entry.Action);
            Assert.Equal(new[] { "1", "2" }, entry.Targets);
            Assert.Null(entry.TargetCount);
            Assert.Equal("because", entry.Detail);
            Assert.Equal(204, entry.Status);
            Assert.Equal(_now, entry.OccurredAt);
            _transaction.Verify(x => x.TrimList(AuditLog.ListKey, 0, AuditLog.MaxEntries - 1));
            _transaction.Verify(x => x.Commit());
        }

        [Fact]
        public void Record_KeepsOnlyTheFirstTargets_AndRemembersTheCount()
        {
            AuditLog.Record(Context("/jobs/failed/delete"), Enumerable.Range(1, 40).Select(i => i.ToString()));

            var entry = Assert.Single(AuditLog.Read(_connection.Object, 10));
            Assert.Equal(AuditLog.MaxTargets, entry.Targets.Count);
            Assert.Equal(40, entry.TargetCount);
        }

        [Fact]
        public void Record_DoesNothing_WhenDisabled_AndSwallowsStorageErrors()
        {
            AuditLog.Enabled = false;
            AuditLog.Record(Context("/x"));
            Assert.Empty(_list);

            AuditLog.Enabled = true;
            _connection.Setup(x => x.CreateWriteTransaction()).Throws(new InvalidOperationException("down"));
            AuditLog.Record(Context("/x"));
        }

        [Fact]
        public void Record_LeavesTheRemoteAddressEmpty_WhenTheHostCannotTellIt()
        {
            var context = new UserContextStub(new DashboardOptions(), new NoRemoteAddressRequest("/recurring/trigger"), _storage.Object);

            AuditLog.Record(context, new[] { "nightly" });

            var entry = Assert.Single(AuditLog.Read(_connection.Object, 10));
            Assert.Null(entry.RemoteIpAddress);
            Assert.Equal("ahmad", entry.User);
            Assert.Equal("/recurring/trigger", entry.Action);
            Assert.Equal(new[] { "nightly" }, entry.Targets);
        }

        private sealed class NoRemoteAddressRequest : DashboardRequestStub
        {
            public NoRemoteAddressRequest(string path) : base("POST", path) { }
            public override string RemoteIpAddress => throw new InvalidOperationException("The connection is gone.");
        }

        [Fact]
        public void Describe_NamesKnownActions_AndFallsBackToThePath()
        {
            Assert.Equal("Requeue failed jobs", AuditLog.Describe("/jobs/failed/requeue"));
            Assert.Equal("Requeue a job", AuditLog.Describe("/jobs/actions/requeue/123"));
            Assert.Equal("Pause a queue", AuditLog.Describe("/queues/pause/emails"));
            Assert.Equal("/something/else", AuditLog.Describe("/something/else"));
        }

        [Fact]
        public async Task CommandDispatcher_RecordsTheRouteTargets()
        {
            var context = Context("/jobs/actions/requeue/77");
            context.UriMatch = System.Text.RegularExpressions.Regex.Match("/jobs/actions/requeue/77", "/jobs/actions/requeue/(?<JobId>.+)");

            await new CommandDispatcher(_ => true).Dispatch(context);

            var entry = Assert.Single(AuditLog.Read(_connection.Object, 10));
            Assert.Equal(new[] { "77" }, entry.Targets);
            Assert.Equal(204, entry.Status);
        }

        [Fact]
        public async Task BatchCommandDispatcher_RecordsTheJobIds()
        {
            var request = new DashboardRequestStub("POST", "/jobs/failed/requeue");
            request.Form["jobs[]"] = new List<string> { "1", "2", "3" };
            var context = new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => false }, request, _storage.Object);

            await new BatchCommandDispatcher((_, __) => { }).Dispatch(context);

            var entry = Assert.Single(AuditLog.Read(_connection.Object, 10));
            Assert.Equal(new[] { "1", "2", "3" }, entry.Targets);
            Assert.Null(entry.User);
        }

        [Fact]
        public void Read_SkipsGarbage()
        {
            _list.Add("not json");
            _list.Add(SerializationHelper.Serialize(new AuditEntry { Timestamp = 1, Action = "/a" }, SerializationOption.User));

            var entries = AuditLog.Read(_connection.Object, 10);

            Assert.Equal("/a", Assert.Single(entries).Action);
            Assert.Equal(2, AuditLog.Count(_connection.Object));
        }
    }
}
