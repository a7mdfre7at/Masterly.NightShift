using System;
using System.Collections.Generic;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// Resuming a queue or a server writes a marker into its hash field, since hash fields can't be removed one by one.
    /// </summary>
    public class DrainModeResumedMarkerFacts : IDisposable
    {
        private const string LegacyResumedMarker = "\u0000resumed";

        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, string> _hash = new Dictionary<string, string>();

        public DrainModeResumedMarkerFacts()
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
        public void ResumeQueue_WritesAPrintableMarker()
        {
            DrainMode.PauseQueue(_connection.Object, "emails", "maintenance");

            DrainMode.ResumeQueue(_connection.Object, "emails");

            AssertPrintable(_hash[DrainMode.QueueFieldPrefix + "emails"]);
            Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
        }

        [Fact, StaticLock]
        public void ResumeServer_WritesAPrintableMarker()
        {
            DrainMode.PauseServer(_connection.Object, "web-1", "patching");

            DrainMode.ResumeServer(_connection.Object, "web-1");

            AssertPrintable(_hash[DrainMode.ServerFieldPrefix + "web-1"]);
            Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
        }

        [Fact, StaticLock]
        public void ClearStaleServerPauses_WritesAPrintableMarker()
        {
            var storage = new Mock<JobStorage>();
            var monitoring = new Mock<IMonitoringApi>();
            storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            storage.Setup(x => x.GetMonitoringApi()).Returns(monitoring.Object);
            monitoring.Setup(x => x.Servers()).Returns(new List<ServerDto>());
            DrainMode.PauseServer(_connection.Object, "web-1");

            var cleared = DrainMode.ClearStaleServerPauses(storage.Object);

            Assert.Equal(new[] { "web-1" }, cleared);
            AssertPrintable(_hash[DrainMode.ServerFieldPrefix + "web-1"]);
        }

        [Fact, StaticLock]
        public void Read_StillTreatsTheMarkerOfEarlierBuilds_AsResumed()
        {
            _hash[DrainMode.QueueFieldPrefix + "emails"] = LegacyResumedMarker;
            _hash[DrainMode.ServerFieldPrefix + "web-1"] = LegacyResumedMarker;

            Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
        }

        [Fact, StaticLock]
        public void PauseQueue_WithAReasonThatReadsLikeTheMarker_StaysPaused()
        {
            DrainMode.PauseQueue(_connection.Object, "emails");
            DrainMode.ResumeQueue(_connection.Object, "emails");
            var marker = _hash[DrainMode.QueueFieldPrefix + "emails"];

            DrainMode.PauseQueue(_connection.Object, "emails", marker);
            DrainMode.PauseServer(_connection.Object, "web-1", marker);

            var status = DrainMode.Read(_connection.Object);
            Assert.True(status.IsQueuePaused("emails"));
            Assert.True(status.IsServerPaused("web-1"));
        }

        private static void AssertPrintable(string value)
        {
            Assert.False(String.IsNullOrEmpty(value));
            Assert.DoesNotContain(value, static c => Char.IsControl(c));
        }
    }
}
