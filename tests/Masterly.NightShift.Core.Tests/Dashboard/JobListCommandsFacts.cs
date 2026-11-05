using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class JobListCommandsFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();
        private readonly List<string> _deleted = new List<string>();

        public JobListCommandsFacts()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _client
                .Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
                .Callback<string, IState, string>((id, _, _) => _deleted.Add(id))
                .Returns(true);
        }

        [Fact]
        public void DeleteAll_DeletesEveryJob_PageByPage_UntilTheListIsEmpty()
        {
            var remaining = new Queue<List<string>>(new[]
            {
                new List<string> { "1", "2" },
                new List<string> { "3" },
                new List<string>(),
            });
            var context = Create();

            var result = JobListCommands.DeleteAll(context, (_, from, count) => remaining.Dequeue(), ScheduledState.StateName);

            Assert.True(result);
            Assert.Equal(new[] { "1", "2", "3" }, _deleted);
            _client.Verify(x => x.ChangeState("1", It.Is<IState>(s => s is DeletedState), ScheduledState.StateName));
        }

        [Fact]
        public void DeleteAll_Stops_WhenAPageReturnsOnlyJobsAlreadySeen()
        {
            var context = Create();

            // A job that refuses to leave the list (its state changed meanwhile) must not loop forever
            var result = JobListCommands.DeleteAll(context, (_, from, count) => new[] { "stuck" }, FailedState.StateName);

            Assert.True(result);
            Assert.Equal(new[] { "stuck" }, _deleted);
        }

        [Fact]
        public void DeleteAllFromSet_UsesTheSetRange_AndTheExpectedState()
        {
            var calls = 0;
            _connection.Setup(x => x.GetRangeFromSet("retries", 0, 499)).Returns(() => calls++ == 0 ? new List<string> { "a", "b" } : new List<string>());
            var context = Create();

            var result = JobListCommands.DeleteAllFromSet(context, "retries", ScheduledState.StateName);

            Assert.True(result);
            Assert.Equal(new[] { "a", "b" }, _deleted);
        }

        [Fact]
        public void DeleteAllFromSet_ReturnsFalse_AndDeletesNothing_WhenTheConnectionCanNotPageASet()
        {
            var connection = new Mock<IStorageConnection>();
            _storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            var context = Create();

            var result = JobListCommands.DeleteAllFromSet(context, "retries", ScheduledState.StateName);

            Assert.False(result);
            Assert.Empty(_deleted);
            connection.Verify(x => x.Dispose(), Times.Once);
        }

        [Fact]
        public void RemoveAllRecurringJobs_RemovesEveryRegisteredJob()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { "nightly", "weekly" });
            var manager = new Mock<IRecurringJobManager>();
            var context = Create();
            context.RecurringJobManager = manager.Object;

            var result = JobListCommands.RemoveAllRecurringJobs(context);

            Assert.True(result);
            manager.Verify(x => x.RemoveIfExists("nightly"));
            manager.Verify(x => x.RemoveIfExists("weekly"));
        }

        private DashboardContextStub Create()
        {
            return new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object)
            {
                BackgroundJobClient = _client.Object,
            };
        }
    }
}
