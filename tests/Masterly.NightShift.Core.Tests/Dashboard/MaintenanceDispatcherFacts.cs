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
        public async Task Dispatch_ClearsJobs_KeepingRecurringJobsAndServers()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object);

            await new MaintenanceDispatcher(keepRecurringJobsAndServers: true).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _storage.Verify(x => x.Purge(It.Is<StoragePurgeOptions>(o => o.KeepRecurringJobs && o.KeepServers)));
        }

        [Fact]
        public async Task Dispatch_ResetsEverything_WhenNothingIsKept()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object);

            await new MaintenanceDispatcher(keepRecurringJobsAndServers: false).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _storage.Verify(x => x.Purge(It.Is<StoragePurgeOptions>(o => !o.KeepRecurringJobs && !o.KeepServers)));
        }

        [Fact]
        public async Task Dispatch_Sets500_WithTheError_WhenThePurgeFails()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
            _storage.Setup(x => x.Purge(It.IsAny<StoragePurgeOptions>())).Throws(new System.InvalidOperationException("The database is read-only."));
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object);

            await new MaintenanceDispatcher(keepRecurringJobsAndServers: true).Dispatch(context);

            Assert.Equal(500, context.Response.StatusCode);
            Assert.Equal("The database is read-only.", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
            _storage.Verify(x => x.GetConnection(), Times.Never);
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
