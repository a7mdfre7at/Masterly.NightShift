using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class DashboardRoutesFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<JobStorageMonitor> _monitor = new Mock<JobStorageMonitor>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<JobStorageTransaction> _transaction = new Mock<JobStorageTransaction>();
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();
        private readonly Mock<IRecurringJobManagerV3> _manager = new Mock<IRecurringJobManagerV3>();

        public DashboardRoutesFacts()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);

            _monitor.Setup(x => x.EnqueuedJobs("critical", 0, 500)).Returns(List("enqueued-1", new EnqueuedJobDto()));
            _monitor.Setup(x => x.ScheduledJobs(0, 500)).Returns(List("scheduled-1", new ScheduledJobDto()));
            _monitor.Setup(x => x.ProcessingJobs(0, 500)).Returns(List("processing-1", new ProcessingJobDto()));
            _monitor.Setup(x => x.SucceededJobs(0, 500)).Returns(List("succeeded-1", new SucceededJobDto()));
            _monitor.Setup(x => x.FailedJobs(0, 500)).Returns(List("failed-1", new FailedJobDto()));
            _monitor.Setup(x => x.AwaitingJobs(0, 500)).Returns(List("awaiting-1", new AwaitingJobDto()));
        }

        private static readonly Guid Build = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid AnotherBuild = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        // Fixed inputs for the independently computed hashes below, not the current resource names
        private const string JavaScript = "Hangfire.Dashboard.Content.js.hangfire.js";
        private const string Stylesheet = "Hangfire.Dashboard.Content.css.hangfire.css";

        [Fact]
        public void CombineResourceHash_IsTheSameInEveryProcessAndOnEveryPlatform()
        {
            // Expected values come from an independent FNV-1a implementation, so a result that depended
            // on per-process randomization (like String.GetHashCode) or on the platform would fail here
            var single = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript);
            var two = DashboardRoutes.CombineResourceHash(single, Build, Stylesheet);

            Assert.Equal(18652613, DashboardRoutes.InitialResourceHash);
            Assert.Equal(1170108145, single);
            Assert.Equal(1179987587, two);
        }

        [Fact]
        public void CombineResourceHash_Changes_WhenTheResourceAssemblyIsRebuilt()
        {
            var original = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript);
            var rebuilt = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, AnotherBuild, JavaScript);

            Assert.Equal(66424875, rebuilt);
            Assert.NotEqual(original, rebuilt);
        }

        [Fact]
        public void CombineResourceHash_DependsOnTheOrderOfResources_AsTheBundleDoes()
        {
            var jsFirst = DashboardRoutes.CombineResourceHash(
                DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript), Build, Stylesheet);
            var cssFirst = DashboardRoutes.CombineResourceHash(
                DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, Stylesheet), Build, JavaScript);

            Assert.Equal(461273807, cssFirst);
            Assert.NotEqual(jsFirst, cssFirst);
        }

        [Fact]
        public void CombineResourceHash_IsNeverNegative_SoMathAbsInTheLayoutCanNotOverflow()
        {
            var hash = DashboardRoutes.InitialResourceHash;

            foreach (var i in Enumerable.Range(0, 10000))
            {
                hash = DashboardRoutes.CombineResourceHash(hash, Guid.NewGuid(), $"resource-{i}");
                Assert.True(hash >= 0);
            }
        }

        [Fact]
        public void BundleHashes_IncludeNightShiftsOwnResources()
        {
            // The static constructor registers the built-in scripts and stylesheets
            Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.JavaScriptsHashCode);
            Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.StylesheetsHashCode);
            Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.StylesheetsDarkModeHashCode);
        }

        [Theory]
        [InlineData("/jobs/enqueued/delete-all/critical", "enqueued-1", "Enqueued")]
        [InlineData("/jobs/scheduled/delete-all", "scheduled-1", "Scheduled")]
        [InlineData("/jobs/processing/delete-all", "processing-1", "Processing")]
        [InlineData("/jobs/succeeded/delete-all", "succeeded-1", "Succeeded")]
        [InlineData("/jobs/failed/delete-all", "failed-1", "Failed")]
        [InlineData("/jobs/awaiting/delete-all", "awaiting-1", "Awaiting")]
        public async Task DeleteAllRoutes_DeleteEveryJobOfTheirList_ExpectingTheListsState(string path, string jobId, string expectedState)
        {
            var context = Create(path);

            await Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _client.Verify(x => x.ChangeState(jobId, It.Is<DeletedState>(s => s.Reason == "Deleted via Dashboard UI (delete all)"), expectedState), Times.Once);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task AwaitingDeleteAllRoute_DeletesNothing_WhenTheMonitorCanNotListAwaitingJobs()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(new Mock<IMonitoringApi>().Object);
            var context = Create("/jobs/awaiting/delete-all");

            await Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task DeleteAllRoutes_Answer401_ForReadOnlyDashboards()
        {
            var context = Create("/jobs/failed/delete-all", readOnly: true);

            await Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RecurringPauseRoute_PausesEveryPostedRecurringJob()
        {
            var context = Create("/recurring/pause", jobs: new[] { "nightly", "weekly" });

            await Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _manager.Verify(x => x.Pause("nightly"), Times.Once);
            _manager.Verify(x => x.Pause("weekly"), Times.Once);
            _manager.Verify(x => x.Resume(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RecurringResumeRoute_ResumesEveryPostedRecurringJob()
        {
            var context = Create("/recurring/resume", jobs: new[] { "nightly" });

            await Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _manager.Verify(x => x.Resume("nightly"), Times.Once);
            _manager.Verify(x => x.Pause(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RecurringPauseRoute_Answers422_WithoutJobs()
        {
            var context = Create("/recurring/pause");

            await Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            _manager.Verify(x => x.Pause(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task TagForgetRoute_RemovesTheUnescapedNormalizedTag()
        {
            var context = Create("/tags/forget/Customer%3AContoso");

            await Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _transaction.Verify(x => x.RemoveSet("tags:customer:contoso"), Times.Once);
            _transaction.Verify(x => x.RemoveFromSet(JobTags.IndexKey, "customer:contoso"), Times.Once);
        }

        [Fact]
        public async Task TagForgetRoute_Answers405_ForGet()
        {
            var context = Create("/tags/forget/import", method: "GET");

            await Dispatch(context);

            Assert.Equal(405, context.Response.StatusCode);
            _transaction.Verify(x => x.RemoveSet(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task CircuitResetRoute_ClosesTheUnescapedCircuit_AndForgetsItsFailures()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("circuit:payments api:failures")).Returns(new HashSet<string> { "f1", "f2" });
            var context = Create("/insights/circuits/reset/payments%20api");

            await Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            _connection.Verify(x => x.AcquireDistributedLock("circuit:payments api:lock", It.IsAny<TimeSpan>()));
            _transaction.Verify(x => x.SetRangeInHash(
                "circuit:payments api",
                It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs => pairs.Any(p => p.Key == "State" && p.Value == CircuitBreakerAttribute.ClosedState))));
            _transaction.Verify(x => x.RemoveFromSet("circuit:payments api:failures", "f1"), Times.Once);
            _transaction.Verify(x => x.RemoveFromSet("circuit:payments api:failures", "f2"), Times.Once);
            _transaction.Verify(x => x.Commit(), Times.AtLeastOnce);
        }

        [Fact]
        public async Task CircuitResetRoute_Answers401_ForReadOnlyDashboards()
        {
            var context = Create("/insights/circuits/reset/payments", readOnly: true);

            await Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
            _connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Theory]
        [InlineData("/servers/pause/web-1", typeof(ServerPauseDispatcher))]
        [InlineData("/servers/resume/web-1", typeof(ServerPauseDispatcher))]
        [InlineData("/queues/pause/default", typeof(QueuePauseDispatcher))]
        [InlineData("/queues/resume/default", typeof(QueuePauseDispatcher))]
        [InlineData("/maintenance/clear-jobs", typeof(MaintenanceDispatcher))]
        [InlineData("/maintenance/reset", typeof(MaintenanceDispatcher))]
        [InlineData("/maintenance/pause", typeof(DrainModeDispatcher))]
        [InlineData("/maintenance/resume", typeof(DrainModeDispatcher))]
        [InlineData("/catalog/run", typeof(JobCatalogRunDispatcher))]
        [InlineData("/recurring/export", typeof(RecurringJobManifestDispatcher))]
        [InlineData("/recurring/import", typeof(RecurringJobManifestDispatcher))]
        [InlineData("/jobs/failed/export", typeof(JobListExportDispatcher))]
        [InlineData("/jobs/enqueued/export/default", typeof(JobListExportDispatcher))]
        [InlineData("/search", typeof(SearchDispatcher))]
        [InlineData("/ping", typeof(PingDispatcher))]
        public void Routes_MapThePath_ToItsDispatcher(string path, Type dispatcherType)
        {
            var route = DashboardRoutes.Routes.FindDispatcher(path);

            Assert.NotNull(route);
            Assert.IsType(dispatcherType, route.Item1);
        }

        private static JobList<T> List<T>(string jobId, T dto)
        {
            return new JobList<T>(new[] { new KeyValuePair<string, T>(jobId, dto) });
        }

        private static Task Dispatch(DashboardContextStub context)
        {
            var route = DashboardRoutes.Routes.FindDispatcher(context.Request.Path);
            context.UriMatch = route.Item2;
            return route.Item1.Dispatch(context);
        }

        private DashboardContextStub Create(string path, string method = "POST", bool readOnly = false, string[] jobs = null)
        {
            var request = new DashboardRequestStub(method, path);
            if (jobs != null) request.Form["jobs[]"] = new List<string>(jobs);

            return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object)
            {
                BackgroundJobClient = _client.Object,
                RecurringJobManager = _manager.Object,
            };
        }
    }
}
