using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class DrainModeFacts : IDisposable
    {
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private Dictionary<string, string> _hash;

        public DrainModeFacts()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) =>
                {
                    _hash = _hash ?? new Dictionary<string, string>();
                    foreach (var pair in pairs) _hash[pair.Key] = pair.Value;
                });

            DrainMode.Clock = () => _now;
            DrainMode.CacheDuration = TimeSpan.Zero;
            DrainMode.InvalidateCache();
        }

        public void Dispose()
        {
            DrainMode.Clock = () => DateTime.UtcNow;
            DrainMode.CacheDuration = TimeSpan.FromSeconds(2);
            DrainMode.HoldInterval = TimeSpan.FromSeconds(30);
            DrainMode.InvalidateCache();
        }

        [Fact, StaticLock]
        public void Read_ReturnsInactive_WithoutTheHash()
        {
            var status = DrainMode.Read(_connection.Object);

            Assert.False(status.Active);
            Assert.Null(status.Since);
            Assert.Null(status.Reason);
        }

        [Fact, StaticLock]
        public void Enable_WritesTheSwitch_WithReasonAndTime()
        {
            DrainMode.Enable(_connection.Object, "deploy 2.0");

            var status = DrainMode.Read(_connection.Object);
            Assert.True(status.Active);
            Assert.Equal(_now, status.Since);
            Assert.Equal("deploy 2.0", status.Reason);
            _transaction.Verify(x => x.Commit());
        }

        [Fact, StaticLock]
        public void Disable_TurnsTheSwitchOff()
        {
            DrainMode.Enable(_connection.Object);
            DrainMode.Disable(_connection.Object);

            Assert.False(DrainMode.Read(_connection.Object).Active);
            Assert.False(DrainMode.IsActive(_connection.Object));
        }

        [Fact, StaticLock]
        public void IsActive_CachesTheAnswer_ForTheCacheDuration()
        {
            DrainMode.CacheDuration = TimeSpan.FromMinutes(5);
            _hash = new Dictionary<string, string> { { "Drain", "1" } };

            Assert.True(DrainMode.IsActive(_connection.Object));
            _hash["Drain"] = "0";
            Assert.True(DrainMode.IsActive(_connection.Object));

            DrainMode.InvalidateCache();
            Assert.False(DrainMode.IsActive(_connection.Object));
        }

        [Fact, StaticLock]
        public void Filter_HoldsTheJob_WhenTheSwitchIsOn()
        {
            _hash = new Dictionary<string, string> { { "Drain", "1" } };
            DrainMode.HoldInterval = TimeSpan.FromSeconds(45);
            var context = new ElectStateContextMock();
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.NewStateObject = new ProcessingState("server", "worker");

            new DrainModeFilter().OnStateElection(context.Object);

            var scheduled = Assert.IsType<ScheduledState>(context.Object.CandidateState);
            Assert.Equal(DrainModeFilter.HeldReason, scheduled.Reason);
            Assert.InRange(scheduled.EnqueueAt, DateTime.UtcNow.AddSeconds(40), DateTime.UtcNow.AddSeconds(50));
        }

        [Fact, StaticLock]
        public void Filter_LetsTheJobRun_WhenTheSwitchIsOff()
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.NewStateObject = new ProcessingState("server", "worker");

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<ProcessingState>(context.Object.CandidateState);
        }

        [Fact, StaticLock]
        public void Filter_IgnoresOtherStates_WithoutReadingTheStorage()
        {
            _hash = new Dictionary<string, string> { { "Drain", "1" } };
            var context = new ElectStateContextMock();
            context.ApplyContext.Connection = _connection;
            context.ApplyContext.NewStateObject = new EnqueuedState();

            new DrainModeFilter().OnStateElection(context.Object);

            Assert.IsType<EnqueuedState>(context.Object.CandidateState);
            _connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Filter_IsRegisteredGlobally_WithTheOtherDefaults()
        {
            var filters = GlobalJobFilters.Filters.Select(x => x.Instance.GetType()).ToList();

            Assert.Contains(typeof(DrainModeFilter), filters);
            Assert.Contains(typeof(ActivityFeedFilter), filters);
            Assert.Contains(typeof(FanInContinuationsSupportAttribute), filters);
        }
    }
}
