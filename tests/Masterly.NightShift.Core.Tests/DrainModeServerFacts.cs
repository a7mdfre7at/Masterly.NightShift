using System;
using System.Collections.Generic;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class DrainModeServerFacts : IDisposable
    {
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, string> _hash = new Dictionary<string, string>();

        public DrainModeServerFacts()
        {
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
            _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => { foreach (var pair in pairs) _hash[pair.Key] = pair.Value; });
            DrainMode.InvalidateCache();
        }

        public void Dispose()
        {
            DrainMode.InvalidateCache();
        }

        [Fact, StaticLock]
        public void PauseServer_AndResumeServer_RoundTrip()
        {
            DrainMode.PauseServer(_connection.Object, "web-1", "deploying");

            var status = DrainMode.Read(_connection.Object);
            Assert.False(status.Active);
            Assert.Equal("deploying", status.PausedServers["web-1"]);
            Assert.True(status.IsServerPaused("web-1"));
            Assert.False(status.IsServerPaused("web-2"));
            Assert.Empty(status.PausedQueues);

            DrainMode.ResumeServer(_connection.Object, "web-1");

            Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
        }

        [Fact, StaticLock]
        public void Filter_HoldsJobsOnPausedServers_Only()
        {
            DrainMode.PauseServer(_connection.Object, "web-1");
            var filter = new DrainModeFilter();

            var held = Elect("1", "web-1");
            filter.OnStateElection(held.Object);
            var scheduled = Assert.IsType<ScheduledState>(held.Object.CandidateState);
            Assert.Equal("Held: server 'web-1' is paused", scheduled.Reason);

            var running = Elect("2", "web-2");
            filter.OnStateElection(running.Object);
            Assert.IsType<ProcessingState>(running.Object.CandidateState);
            _connection.Verify(x => x.GetStateData(It.IsAny<string>()), Times.Never);
        }

        private ElectStateContextMock Elect(string jobId, string serverId)
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = jobId;
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.NewStateObject = new ProcessingState(serverId, "worker");
            return context;
        }
    }
}
