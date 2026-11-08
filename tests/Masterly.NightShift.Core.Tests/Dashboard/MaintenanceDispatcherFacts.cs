using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class MaintenanceDispatcherFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();

        [Fact]
        public async Task Dispatch_Sets401_WhenReadOnly()
        {
            var context = new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => true }, new DashboardRequestStub("POST"), _storage.Object);

            await new MaintenanceDispatcher(true).Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
            _storage.Verify(x => x.Purge(It.IsAny<StoragePurgeOptions>()), Times.Never);
        }

        [Fact]
        public async Task Dispatch_Sets405_ForNonPostRequests()
        {
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("GET"), _storage.Object);

            await new MaintenanceDispatcher(true).Dispatch(context);

            Assert.Equal(405, context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_Sets422_WhenTheStorageCannotPurge()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(false);
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object);

            await new MaintenanceDispatcher(true).Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            Assert.Contains("Storage.Purge", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
            _storage.Verify(x => x.Purge(It.IsAny<StoragePurgeOptions>()), Times.Never);
        }

        [Fact]
        public async Task Dispatch_ClearsJobs_KeepingRecurringJobsServersAuditLogAndDrainMode()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object);

            await new MaintenanceDispatcher(keepRecurringJobsAndServers: true).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _storage.Verify(x => x.Purge(It.Is<StoragePurgeOptions>(o => o.KeepRecurringJobs && o.KeepServers && o.KeepAuditLog && o.KeepDrainMode)));
        }

        [Fact]
        public async Task Dispatch_ResetsEverything_ButTheAuditLog()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object);

            await new MaintenanceDispatcher(keepRecurringJobsAndServers: false).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _storage.Verify(x => x.Purge(It.Is<StoragePurgeOptions>(o => !o.KeepRecurringJobs && !o.KeepServers && o.KeepAuditLog && !o.KeepDrainMode)));
        }

        [Fact, StaticLock]
        public async Task Dispatch_Sets500_WithAGenericMessage_AndRecordsTheFailure_WhenThePurgeFails()
        {
            // The storage's own message (server, database, table names) went to the browser, and nothing was audited
            var audit = new List<string>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => audit.Add(value));
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            _storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
            _storage.Setup(x => x.Purge(It.IsAny<StoragePurgeOptions>())).Throws(new System.InvalidOperationException("Server db-07: database Jobs is read-only."));
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST", "/maintenance/reset"), _storage.Object);

            await new MaintenanceDispatcher(keepRecurringJobsAndServers: true).Dispatch(context);

            Assert.Equal(500, context.Response.StatusCode);
            var error = JsonNode.Parse(context.Response.Text)["error"].GetValue<string>();
            Assert.DoesNotContain("db-07", error);
            Assert.Contains("log", error);
            var entry = Assert.Single(audit);
            Assert.Contains("\"s\":500", entry);
            Assert.DoesNotContain("db-07", entry);
        }

        [Fact, StaticLock]
        public async Task Dispatch_RecordsThe422_WhenTheStorageCannotPurge()
        {
            var audit = new List<string>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => audit.Add(value));
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            _storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(false);
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST", "/maintenance/reset"), _storage.Object);

            await new MaintenanceDispatcher(keepRecurringJobsAndServers: false).Dispatch(context);

            Assert.Contains("\"s\":422", Assert.Single(audit));
        }

        [Fact]
        public void JobStorage_Purge_ThrowsNotSupported_ByDefault()
        {
            var storage = new JobStorageStub();

            Assert.False(storage.HasFeature(JobStorageFeatures.Purge));
            Assert.Throws<System.NotSupportedException>(() => storage.Purge(new StoragePurgeOptions()));
            Assert.Throws<System.ArgumentNullException>(() => storage.Purge(null));
        }
    }
}
